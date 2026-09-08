using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Hue
{
    /// <summary>
    /// Manages DTLS streaming connection to Hue Bridge for real-time entertainment control.
    ///
    /// Hue Entertainment API v2 packet layout (evidence: docs/HUE_STREAM_PROTOCOL.md):
    ///   Header:  "HueStream" (9 bytes, ASCII)
    ///   Version: 0x02 0x00           (2 bytes, major.minor)
    ///   SeqNum:  0x00                (1 byte, wrapping sequence number)
    ///   Reserved:0x00 0x00           (2 bytes)
    ///   ColorSpace: 0x00             (1 byte: 0x00=RGB, 0x01=XY Brightness)
    ///   Reserved:0x00                (1 byte)
    ///   Area: canonical hyphenated configuration UUID (36 ASCII bytes, no NUL)
    ///   Per channel: id(1) + R(2) + G(2) + B(2), with big-endian unsigned RGB16.
    /// The v2 channel identifier is not a v1 device type or 16-bit light address.
    /// </summary>
    public class HueStreamer
    {
        internal const int HueStreamPacketHeaderBytes = 16 + 36;
        internal const int HueStreamChannelBytes = 7;
        internal const int MaxHueStreamPacketBytes = HueDtlsLimits.ApplicationPayloadBytes;
        private const int PacketChannelCapacity = (MaxHueStreamPacketBytes - HueStreamPacketHeaderBytes) / HueStreamChannelBytes;
        internal const int MaxHueStreamChannels =
            PacketChannelCapacity < byte.MaxValue + 1 ? PacketChannelCapacity : byte.MaxValue + 1;
        internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan SafeStreamIdleWindow = TimeSpan.FromSeconds(8);

        private readonly ILogger<HueStreamer> _logger;
        private IHueDtlsConnection? _dtlsConnection;
        private readonly object _lock = new object();
        private PluginConfiguration? _lastConfig;
        private (string bridgeIp, string appKey, string clientKey)? _lastBridgeConfig;
        private int _reconnectAttempts = 0;
        private const int DefaultMaxReconnectAttempts = 3;
        private const int MinMaxReconnectAttempts = 0;
        private const int MaxMaxReconnectAttempts = 10;
        private int _maxReconnectAttempts = DefaultMaxReconnectAttempts;
        private Dictionary<int, byte[]>? _lastSentColors;
        private Guid? _streamAreaId;
        private IHueDtlsConnection? _lastSentConnection;
        private long _lastPacketTimestamp;
        private long? _connectionReadyTimestamp;
        private readonly TimeProvider _timeProvider;
        private ITimer? _keepAliveTimer;
        private Task? _keepAliveTask;
        private CancellationToken _streamCallerCancellationToken;
        private CancellationToken _lastSendCancellationToken;
        private byte _sequenceNumber = 0;
        private long _packetsSent;
        private long _packetsSkippedByThreshold;
        private long _packetSendFailures;
        private int _totalReconnectAttempts;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        private readonly Func<string, string, string, CancellationToken, Task<IHueDtlsConnection>> _connectDtlsAsync;
        // A stream stop cancels any delayed reconnect or in-flight DTLS startup. The
        // source is replaced for the next stream so a later playback can reconnect normally.
        private CancellationTokenSource _streamLifecycleCts = new CancellationTokenSource();
        // Incremented whenever a public lifecycle transition retires the lifecycle source.
        // Startup and reconnect workers carry this generation so stale work cannot install a
        // stream or clear a replacement stream after a stop/start race.
        private long _streamLifecycleGeneration;

        // How long to wait after establishing DTLS before attempting to write
        // The DTLS handshake typically takes 100-400ms on a local network
        private const int DtlsHandshakeWaitMs = 600;
        private const int EntertainmentAreaActivationDelayMs = 200;

        internal static string FormatBridgeEndpoint(string bridgeIp)
        {
            var host = bridgeIp.Trim();
            if (IPAddress.TryParse(host, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return $"[{host}]:2100";
            }

            return $"{host}:2100";
        }

        /// <summary>
        /// Optional callback invoked before each reconnection attempt.
        /// Used by HueSyncService to re-activate the entertainment area on the bridge,
        /// which is required before the DTLS tunnel will accept packets.
        /// Returns true if preparation succeeded and reconnection should proceed.
        /// </summary>
        public Func<Task<bool>>? OnBeforeReconnect { get; set; }

        /// <summary>
        /// Optional cancellation-aware preparation callback invoked before each reconnect.
        /// The legacy <see cref="OnBeforeReconnect"/> callback remains supported for callers
        /// that do not need to observe cancellation.
        /// </summary>
        public Func<CancellationToken, Task<bool>>? OnBeforeReconnectWithCancellation { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of DTLS reconnect attempts after a stream failure.
        /// HueSyncService applies the captured per-session execution policy here; zero disables
        /// reconnect attempts while preserving the active stream path.
        /// </summary>
        public int MaxReconnectAttempts
        {
            get => _maxReconnectAttempts;
            set => _maxReconnectAttempts = Math.Clamp(
                value,
                MinMaxReconnectAttempts,
                MaxMaxReconnectAttempts);
        }

        /// <summary>
        /// Number of color packets written successfully during the current DTLS stream.
        /// </summary>
        public long PacketsSent => Interlocked.Read(ref _packetsSent);

        /// <summary>
        /// Number of frames whose colors were intentionally suppressed by the configured
        /// color-change threshold during the current DTLS stream.
        /// </summary>
        public long PacketsSkippedByThreshold => Interlocked.Read(ref _packetsSkippedByThreshold);

        /// <summary>
        /// Number of non-canceled packet sends that failed during the current DTLS stream.
        /// </summary>
        public long PacketSendFailures => Interlocked.Read(ref _packetSendFailures);

        /// <summary>
        /// Total DTLS reconnect attempts made during the current stream, including attempts
        /// that eventually failed. This is separate from the bounded retry counter.
        /// </summary>
        public int ReconnectAttempts
        {
            get
            {
                lock (_lock)
                {
                    return _totalReconnectAttempts;
                }
            }
        }

        public HueStreamer(ILogger<HueStreamer> logger)
            : this(logger, null)
        {
        }

        internal HueStreamer(
            ILogger<HueStreamer> logger,
            Func<string, string, string, CancellationToken, Task<IHueDtlsConnection>>? connectDtlsAsync,
            TimeProvider? timeProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectDtlsAsync = connectDtlsAsync ?? ConnectDefaultDtlsAsync;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        private static async Task<IHueDtlsConnection> ConnectDefaultDtlsAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken)
        {
            return await HueDtlsConnection.ConnectAsync(
                bridgeIp,
                appKey,
                clientKey,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks if colors have changed significantly compared to last sent colors
        /// </summary>
        private bool HasSignificantColorChange(Dictionary<int, byte[]> newColors, int threshold)
        {
            if (_lastSentColors == null || _lastSentColors.Count != newColors.Count)
                return true;

            foreach (var kvp in newColors)
            {
                if (!_lastSentColors.TryGetValue(kvp.Key, out var oldColor))
                    return true;

                // SendColors validates the complete frame before reaching this helper,
                // but keep the comparison fail-closed for callers or future code paths
                // that provide malformed data. Invalid values must force the normal
                // packet validation path instead of indexing past a short/null buffer.
                if (kvp.Value == null || kvp.Value.Length != 6 || oldColor == null || oldColor.Length != 6)
                    return true;

                // Compare the high byte of each 16-bit R, G, B component (bytes 0, 2, 4)
                for (int i = 0; i < 6; i += 2)
                {
                    var diff = Math.Abs(kvp.Value[i] - oldColor[i]);
                    if (diff > threshold)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the DTLS stream is healthy and connected
        /// </summary>
        public bool IsHealthy()
        {
            lock (_lock)
            {
                if (_dtlsConnection?.IsHealthy != true)
                    return false;

                // An open local UDP transport does not prove that the bridge's area
                // survived a long initial blackout or a suspended/delayed heartbeat.
                long? lastActivity = ReferenceEquals(_lastSentConnection, _dtlsConnection)
                    ? _lastPacketTimestamp
                    : _connectionReadyTimestamp;
                return !lastActivity.HasValue ||
                    _timeProvider.GetElapsedTime(lastActivity.Value) < SafeStreamIdleWindow;
            }
        }

        /// <summary>
        /// Starts a managed DTLS streaming connection to the Hue Bridge
        /// </summary>
        /// <param name="config">Plugin configuration containing bridge IP and credentials</param>
        public async Task StartStreamAsync(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (!TryParseAreaId(config.EntertainmentAreaId, out var configuredAreaId))
                throw new ArgumentException("The entertainment area must be a hyphenated UUID.", nameof(config));

            var lifecycleGeneration = await StartStreamWithGenerationAsync(
                config.HueBridgeIp,
                config.HueAppKey,
                config.HueClientKey,
                CancellationToken.None,
                configuredAreaId).ConfigureAwait(false);

            // The configuration overload historically retained the full config for a
            // reconnect. Only publish it if the same startup still owns the lifecycle;
            // otherwise a stop that raced completion must not leave a stale reconnect target.
            if (lifecycleGeneration.HasValue)
            {
                lock (_lock)
                {
                    if (_streamLifecycleGeneration == lifecycleGeneration.Value)
                    {
                        _lastConfig = config;
                    }
                }
            }
        }

        /// <summary>
        /// Starts a managed DTLS streaming connection to the Hue Bridge with explicit parameters.
        ///
        /// Uses DTLS 1.2 with PSK. The ClientKey from Hue must be provided as hex.
        /// The bridge requires the plain PSK-AES128-GCM-SHA256 cipher suite.
        ///
        /// IMPORTANT: This method awaits DtlsHandshakeWaitMs to allow the DTLS handshake to complete
        /// before the caller starts writing packets.
        /// </summary>
        public async Task StartStreamAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken = default)
        {
            await StartStreamWithGenerationAsync(
                bridgeIp,
                appKey,
                clientKey,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<long?> StartStreamWithGenerationAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken,
            Guid? areaId = null)
        {
            // Reconnects use the same gate. A public replacement start must wait for an
            // in-flight reconnect to finish (or observe its canceled lifecycle) before
            // replacing the connection; otherwise stale reconnect cleanup can close the
            // newly installed stream after this method returns.
            await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await StartStreamCoreAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    cancellationToken,
                    cancelPendingReconnect: true,
                    areaId: areaId).ConfigureAwait(false);
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        private async Task<long?> StartStreamCoreAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken,
            bool cancelPendingReconnect,
            long? expectedLifecycleGeneration = null,
            Guid? areaId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(bridgeIp) || string.IsNullOrEmpty(clientKey))
            {
                _logger.LogError("Bridge IP or Client Key missing.");
                return null;
            }

            if (string.IsNullOrEmpty(appKey))
            {
                _logger.LogError("Hue App Key missing.");
                return null;
            }

            if (cancelPendingReconnect)
            {
                Interlocked.Exchange(ref _packetsSent, 0);
                Interlocked.Exchange(ref _packetsSkippedByThreshold, 0);
                Interlocked.Exchange(ref _packetSendFailures, 0);
                lock (_lock)
                {
                    _totalReconnectAttempts = 0;
                }
            }

            // Capture the lifecycle generation before replacing the connection. For a
            // reconnect this is the generation owned by the reconnect worker; for a public
            // start it lets us distinguish our replacement stop from a concurrent external
            // stop that must win and cancel this startup.
            long expectedStartGeneration;
            lock (_lock)
            {
                expectedStartGeneration = _streamLifecycleGeneration;
            }

            if (expectedLifecycleGeneration.HasValue &&
                expectedLifecycleGeneration.Value != expectedStartGeneration)
            {
                return null;
            }

            // A reconnect already owns the current lifecycle token. Do not cancel that
            // token when it replaces the failed connection; an external StopStream still can.
            if (!StopStream(cancelPendingReconnect, expectedStartGeneration))
            {
                return null;
            }

            var requiredLifecycleGeneration = cancelPendingReconnect
                ? expectedStartGeneration + 1
                : expectedStartGeneration;
            CancellationTokenSource? startupTokenSource = null;
            CancellationToken startupToken;
            long lifecycleGeneration;
            lock (_lock)
            {
                // StopStream logs outside the state lock. A reentrant/concurrent stop can
                // therefore retire the source before this block; never overwrite that stop
                // by publishing a new target against its replacement lifecycle.
                if (_streamLifecycleGeneration != requiredLifecycleGeneration)
                {
                    return null;
                }

                // A stop can clear the saved reconnect target while this worker is waiting
                // to capture its token. Do not resurrect it when this is an internal retry.
                if (!cancelPendingReconnect && _lastConfig == null && _lastBridgeConfig == null)
                {
                    return null;
                }

                if (cancelPendingReconnect)
                {
                    _lastBridgeConfig = (bridgeIp, appKey, clientKey);
                    _lastSentColors = null;
                    _streamAreaId = areaId;
                    _streamCallerCancellationToken = cancellationToken;
                }

                lifecycleGeneration = _streamLifecycleGeneration;
                startupToken = _streamLifecycleCts.Token;

                // Always use a linked source, even when the caller has no cancellation
                // token. Startup awaits (including Task.Delay) can then safely register on
                // this live source while StopStream cancels and disposes the retired
                // lifecycle source underneath it.
                startupTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    startupToken);
                startupToken = startupTokenSource.Token;
            }

            IHueDtlsConnection? dtlsConnection = null;
            try
            {
                // Use a managed DTLS PSK session so the App Key and Client Key remain in
                // this process's memory instead of being exposed through /proc or ps.
                _logger.LogInformation("Starting managed DTLS tunnel to {0}:2100", bridgeIp);

                dtlsConnection = await _connectDtlsAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    startupToken).ConfigureAwait(false);

                if (startupToken.IsCancellationRequested)
                {
                    SafeClose(dtlsConnection);
                    startupToken.ThrowIfCancellationRequested();
                }

                var installed = false;
                lock (_lock)
                {
                    if (_streamLifecycleGeneration == lifecycleGeneration &&
                        !startupToken.IsCancellationRequested)
                    {
                        _dtlsConnection = dtlsConnection;
                        _connectionReadyTimestamp = _timeProvider.GetTimestamp();
                        installed = true;
                    }
                }

                if (!installed)
                {
                    SafeClose(dtlsConnection);
                    startupToken.ThrowIfCancellationRequested();
                    return null;
                }

                // Keep the existing short bridge-settle delay so the first packet is
                // sent only after the entertainment area has switched to streaming mode.
                await Task.Delay(DtlsHandshakeWaitMs, startupToken).ConfigureAwait(false);

                var stillCurrent = false;
                lock (_lock)
                {
                    stillCurrent = _streamLifecycleGeneration == lifecycleGeneration &&
                        ReferenceEquals(_dtlsConnection, dtlsConnection);
                }

                if (!stillCurrent || startupToken.IsCancellationRequested)
                {
                    SafeClose(dtlsConnection);
                    startupToken.ThrowIfCancellationRequested();
                    return null;
                }

                if (!dtlsConnection.IsHealthy)
                {
                    _logger.LogError("Managed DTLS tunnel closed immediately — check bridge IP, Client Key, and that the entertainment area was activated (action=start) first.");
                    // A reconnect failure must leave the saved target and lifecycle
                    // token intact so a later SendColors call can consume the remaining
                    // bounded retry budget. A public startup still clears the target.
                    StopStream(cancelPendingReconnect, lifecycleGeneration);
                    return null;
                }

                _reconnectAttempts = 0;
                lock (_lock)
                {
                    if (_streamLifecycleGeneration == lifecycleGeneration &&
                        ReferenceEquals(_dtlsConnection, dtlsConnection) &&
                        !startupToken.IsCancellationRequested)
                    {
                        _connectionReadyTimestamp = _timeProvider.GetTimestamp();
                        _keepAliveTimer ??= _timeProvider.CreateTimer(
                            KeepAliveTick,
                            lifecycleGeneration,
                            KeepAliveInterval,
                            TimeSpan.FromSeconds(1));
                    }
                }
                _logger.LogInformation("Managed DTLS tunnel started to {0}:2100", bridgeIp);
                return lifecycleGeneration;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the managed Hue DTLS connection.");
                // Preserve reconnect state after an internal startup failure. Clearing
                // _lastBridgeConfig here would make all later frames permanently unable
                // to retry even though MaxReconnectAttempts has not been exhausted.
                if (dtlsConnection != null && !OwnsConnection(lifecycleGeneration, dtlsConnection))
                {
                    SafeClose(dtlsConnection);
                }

                StopStream(cancelPendingReconnect, lifecycleGeneration);
                throw;
            }
            finally
            {
                startupTokenSource?.Dispose();
            }
        }

        /// <summary>
        /// Attempts to reconnect the DTLS stream if it has failed
        /// </summary>
        private async Task<bool> TryReconnectAsync(
            CancellationToken cancellationToken,
            long? expectedLifecycleGeneration = null)
        {
            CancellationTokenSource reconnectTokenSource;
            long lifecycleGeneration;
            lock (_lock)
            {
                lifecycleGeneration = _streamLifecycleGeneration;
                if (expectedLifecycleGeneration.HasValue &&
                    expectedLifecycleGeneration.Value != lifecycleGeneration)
                {
                    return false;
                }
                // Keep linked-source registration under the lifecycle lock. StopStream
                // retires and disposes the old source after releasing this lock.
                reconnectTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _streamLifecycleCts.Token,
                    _streamCallerCancellationToken);
            }

            using var reconnectTokenSourceLease = reconnectTokenSource;
            var reconnectToken = reconnectTokenSource.Token;

            var reconnectLockAcquired = false;
            try
            {
                await _reconnectLock.WaitAsync(reconnectToken).ConfigureAwait(false);
                reconnectLockAcquired = true;
                reconnectToken.ThrowIfCancellationRequested();

                lock (_lock)
                {
                    if (_streamLifecycleGeneration != lifecycleGeneration ||
                        (_lastConfig == null && _lastBridgeConfig == null))
                    {
                        return false;
                    }
                }

                // A concurrent SendColors call may have repaired the stream while this
                // caller was waiting for the reconnect gate.
                if (IsHealthy())
                    return true;

                if (_reconnectAttempts >= MaxReconnectAttempts)
                    return false;

                _reconnectAttempts++;
                _totalReconnectAttempts++;
                var attempt = _reconnectAttempts;
                _logger.LogWarning("Attempting to reconnect DTLS stream (attempt {0}/{1})", attempt, MaxReconnectAttempts);

                if (!StopStream(false, lifecycleGeneration))
                    return false;
                // Exponential backoff — await so we don't block a thread pool thread
                await Task.Delay(1000 * attempt, reconnectToken).ConfigureAwait(false);

                // Re-activate the entertainment area before reopening the DTLS tunnel.
                // The bridge requires action=start or it silently drops all packets.
                if (OnBeforeReconnectWithCancellation != null)
                {
                    if (!await OnBeforeReconnectWithCancellation(reconnectToken).ConfigureAwait(false))
                    {
                        _logger.LogWarning("Pre-reconnect preparation failed, aborting reconnect");
                        return false;
                    }
                    await Task.Delay(EntertainmentAreaActivationDelayMs, reconnectToken).ConfigureAwait(false);
                }
                else if (OnBeforeReconnect != null)
                {
                    if (!await OnBeforeReconnect().ConfigureAwait(false))
                    {
                        _logger.LogWarning("Pre-reconnect preparation failed, aborting reconnect");
                        return false;
                    }
                    await Task.Delay(EntertainmentAreaActivationDelayMs, reconnectToken).ConfigureAwait(false); // Let bridge enter streaming mode
                }

                (string bridgeIp, string appKey, string clientKey)? bridgeConfig;
                PluginConfiguration? config;
                lock (_lock)
                {
                    if (_streamLifecycleGeneration != lifecycleGeneration)
                        return false;

                    bridgeConfig = _lastBridgeConfig;
                    config = _lastConfig;
                }

                if (bridgeConfig != null)
                {
                    var (bridgeIp, appKey, clientKey) = bridgeConfig.Value;
                    await StartStreamCoreAsync(
                        bridgeIp,
                        appKey,
                        clientKey,
                        reconnectToken,
                        cancelPendingReconnect: false,
                        expectedLifecycleGeneration: lifecycleGeneration).ConfigureAwait(false);
                }
                else if (config != null)
                {
                    await StartStreamCoreAsync(
                        config.HueBridgeIp,
                        config.HueAppKey,
                        config.HueClientKey,
                        reconnectToken,
                        cancelPendingReconnect: false,
                        expectedLifecycleGeneration: lifecycleGeneration).ConfigureAwait(false);
                }

                return IsHealthy();
            }
            catch (OperationCanceledException) when (reconnectToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnection attempt {0} failed", _reconnectAttempts);
                return false;
            }
            finally
            {
                if (reconnectLockAcquired)
                    _reconnectLock.Release();
            }
        }

        public virtual void StopStream()
            => StopStream(cancelPendingReconnect: true);

        private bool OwnsConnection(long lifecycleGeneration, IHueDtlsConnection connection)
        {
            lock (_lock)
            {
                return _streamLifecycleGeneration == lifecycleGeneration &&
                    ReferenceEquals(_dtlsConnection, connection);
            }
        }

        private void SafeClose(IHueDtlsConnection? connection)
        {
            if (connection == null)
                return;

            try
            {
                connection.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to close the DTLS stream");
            }
        }

        private void CancelAndDisposeLifecycle(CancellationTokenSource lifecycle)
        {
            try
            {
                // Cancel synchronously so all linked registrations have observed the stop
                // before the retired source is disposed.
                lifecycle.Cancel(throwOnFirstException: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error canceling retired DTLS stream lifecycle");
            }

            try
            {
                lifecycle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing retired DTLS stream lifecycle");
            }
        }

        private bool StopStream(
            bool cancelPendingReconnect,
            long? expectedLifecycleGeneration = null)
        {
            CancellationTokenSource? canceledLifecycle = null;
            try
            {
                lock (_lock)
                {
                    if (expectedLifecycleGeneration.HasValue &&
                        expectedLifecycleGeneration.Value != _streamLifecycleGeneration)
                    {
                        return false;
                    }

                    if (cancelPendingReconnect)
                    {
                        canceledLifecycle = _streamLifecycleCts;
                        _streamLifecycleCts = new CancellationTokenSource();
                        _streamLifecycleGeneration++;
                    }

                    if (_dtlsConnection != null)
                    {
                        SafeClose(_dtlsConnection);
                    }
                    _dtlsConnection = null;
                    _connectionReadyTimestamp = null;
                    _lastSentConnection = null;
                    if (cancelPendingReconnect)
                    {
                        _keepAliveTimer?.Dispose();
                        _keepAliveTimer = null;
                        _keepAliveTask = null;
                        _lastSentColors = null;
                        _streamAreaId = null;
                        _lastSendCancellationToken = default;
                        _streamCallerCancellationToken = default;
                        // A public stop ends the stream lifecycle completely. Clear the
                        // saved target so a stale caller cannot resurrect a later tunnel.
                        _lastConfig = null;
                        _lastBridgeConfig = null;
                    }
                }

                if (canceledLifecycle != null)
                {
                    CancelAndDisposeLifecycle(canceledLifecycle);
                }

                _logger.LogInformation("DTLS stream stopped");
                return true;
            }
            catch (Exception ex)
            {
                if (canceledLifecycle != null)
                {
                    CancelAndDisposeLifecycle(canceledLifecycle);
                }

                _logger.LogWarning(ex, "Error stopping DTLS stream");
                return false;
            }
        }

        /// <summary>
        /// Builds a Hue Entertainment API v2 binary packet.
        ///
        /// Packet layout:
        ///   [0..8]  "HueStream" ASCII (9 bytes)
        ///   [9]     0x02  — protocol major version
        ///   [10]    0x00  — protocol minor version
        ///   [11]    seqNo — sequence number (wraps 0-255)
        ///   [12]    0x00  — reserved
        ///   [13]    0x00  — reserved
        ///   [14]    0x00  — color space: RGB
        ///   [15]    0x00  — reserved
        ///   [16-51] configuration UUID in canonical lowercase ASCII D format
        ///   Repeated per channel (7 bytes each): unsigned byte channel ID,
        ///   followed by R high/low, G high/low, and B high/low bytes.
        ///
        /// channelColors values must be 6 bytes: [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo]
        /// </summary>
        public byte[] BuildHueStreamPacket(string areaId, Dictionary<int, byte[]> channelColors)
        {
            ArgumentNullException.ThrowIfNull(channelColors);
            if (!TryParseAreaId(areaId, out var parsedAreaId))
                throw new ArgumentException("The entertainment area must be a hyphenated UUID.", nameof(areaId));
            lock (_lock)
                return BuildHueStreamPacketCore(parsedAreaId, channelColors);
        }

        /// <summary>
        /// Builds for the area already bound by a configuration start or SendColors.
        /// Standalone callers must use the overload that supplies the area UUID.
        /// </summary>
        public byte[] BuildHueStreamPacket(Dictionary<int, byte[]> channelColors)
        {
            ArgumentNullException.ThrowIfNull(channelColors);
            lock (_lock)
            {
                if (!_streamAreaId.HasValue)
                    throw new InvalidOperationException("Supply an entertainment area UUID before building a Hue v2 packet.");
                return BuildHueStreamPacketCore(_streamAreaId.Value, channelColors);
            }
        }

        private static bool TryParseAreaId(string? value, out Guid areaId)
            => Guid.TryParseExact(value?.Trim(), "D", out areaId);

        // Validate the actual entries written; public callers may still own mutable
        // dictionaries. Capacity stays bounded even if a caller changes the frame.
        private byte[] BuildHueStreamPacketCore(Guid areaId, Dictionary<int, byte[]> channelColors)
        {
            var channelCount = channelColors.Count;
            ValidateChannelCount(channelCount);
            var packet = new byte[HueStreamPacketHeaderBytes + channelCount * HueStreamChannelBytes];
            "HueStream"u8.CopyTo(packet);
            packet[9] = 2;
            packet[11] = _sequenceNumber;
            Utf8Formatter.TryFormat(areaId, packet.AsSpan(16, 36), out _, 'D');
            var offset = HueStreamPacketHeaderBytes;
            foreach (var kvp in channelColors)
            {
                ValidateChannelColor(kvp);
                if (offset > packet.Length - HueStreamChannelBytes)
                    throw new ArgumentException("The channel frame changed while it was being serialized.", nameof(channelColors));
                packet[offset++] = (byte)kvp.Key;
                kvp.Value.CopyTo(packet, offset);
                offset += 6;
            }
            if (offset != packet.Length)
                throw new ArgumentException("The channel frame changed while it was being serialized.", nameof(channelColors));
            _sequenceNumber = unchecked((byte)(_sequenceNumber + 1));
            return packet;
        }

        private static Dictionary<int, byte[]> CaptureFrame(Dictionary<int, byte[]> channelColors)
        {
            var channelCount = channelColors.Count;
            ValidateChannelCount(channelCount);
            var frame = new Dictionary<int, byte[]>(channelCount);
            foreach (var kvp in channelColors)
            {
                ValidateChannelColor(kvp);
                if (frame.Count >= channelCount)
                    throw new ArgumentException("The channel frame changed while it was being captured.", nameof(channelColors));
                frame.Add(kvp.Key, (byte[])kvp.Value.Clone());
            }
            if (frame.Count != channelCount)
                throw new ArgumentException("The channel frame changed while it was being captured.", nameof(channelColors));
            return frame;
        }

        /// <summary>
        /// Sends color data to the Hue Bridge for all channels in an entertainment area.
        /// Each stream lifecycle binds one area UUID; reconnects and keepalives retain it.
        /// </summary>
        /// <param name="areaId">The entertainment configuration UUID embedded in every packet.</param>
        /// <param name="channelColors">Dictionary mapping channel IDs to 6-byte RGB16 color data</param>
        /// <param name="colorChangeThreshold">Minimum per-channel color change to trigger update (0 to disable)</param>
        /// <param name="cancellationToken">Cancels the send or any reconnect attempt.</param>
        /// <returns>True when the packet was sent or intentionally skipped by the change threshold; otherwise false.</returns>
        public virtual async Task<bool> SendColors(
            string areaId,
            Dictionary<int, byte[]> channelColors,
            int colorChangeThreshold = 0,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(channelColors);

            if (cancellationToken.IsCancellationRequested)
                return false;

            if (!TryParseAreaId(areaId, out var parsedAreaId))
            {
                _logger.LogWarning("Cannot send colors: the entertainment area must be a hyphenated UUID");
                return RecordPacketSendFailure(cancellationToken);
            }

            long lifecycleGeneration;
            lock (_lock)
            {
                lifecycleGeneration = _streamLifecycleGeneration;
                if (cancellationToken.IsCancellationRequested || _streamCallerCancellationToken.IsCancellationRequested)
                    return false;
                if (_streamAreaId.HasValue && _streamAreaId.Value != parsedAreaId)
                    return RecordPacketSendFailure(cancellationToken);
            }

            Dictionary<int, byte[]> frame;
            try
            {
                // Own the bounded frame before reconnect/transport callbacks or any
                // await can let callers reuse the dictionary and its color buffers.
                frame = CaptureFrame(channelColors);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning("Cannot send colors: the frame must contain valid byte channel IDs and RGB16 values within the DTLS packet budget");
                return RecordPacketSendFailure(cancellationToken);
            }

            // Check health before applying the color-change threshold. A static scene can
            // legitimately produce identical frames for a long time; suppressing the
            // health check in that case would allow a dead DTLS process to remain broken
            // indefinitely while every frame is reported as successfully skipped.
            if (!IsHealthy())
            {
                _logger.LogWarning("DTLS stream unhealthy, attempting reconnect");
                if (!await TryReconnectAsync(cancellationToken, lifecycleGeneration).ConfigureAwait(false))
                {
                    _logger.LogError("Failed to reconnect DTLS stream after {0} attempts", MaxReconnectAttempts);
                    return RecordPacketSendFailure(cancellationToken);
                }
            }

            lock (_lock)
            {
                if (_streamLifecycleGeneration != lifecycleGeneration ||
                    cancellationToken.IsCancellationRequested ||
                    _streamCallerCancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (_streamAreaId.HasValue && _streamAreaId.Value != parsedAreaId)
                    return RecordPacketSendFailure(cancellationToken);
                _streamAreaId = parsedAreaId;

                if (_dtlsConnection == null)
                {
                    _logger.LogWarning("Cannot send colors: DTLS stream not initialized");
                    return RecordPacketSendFailure(cancellationToken);
                }

                if (!IsHealthy())
                {
                    _ = ScheduleReconnect(cancellationToken);
                    return RecordPacketSendFailure(cancellationToken);
                }

                // A reconnect must receive a frame even when its colors match those
                // sent on the previous connection. The timer covers longer suppression.
                if (colorChangeThreshold > 0 &&
                    ReferenceEquals(_lastSentConnection, _dtlsConnection) &&
                    !HasSignificantColorChange(frame, colorChangeThreshold))
                {
                    Interlocked.Increment(ref _packetsSkippedByThreshold);
                    return true;
                }

                var sent = SendPacketLocked(frame, cancellationToken, out var reconnectNeeded);
                if (sent)
                    _lastSendCancellationToken = cancellationToken;
                else if (reconnectNeeded)
                    _ = ScheduleReconnect(cancellationToken);
                return sent;
            }
        }

        // Transport writes, sequence numbers, snapshots, and stop share this lock so
        // a queued heartbeat cannot send retired colors or overwrite a newer frame.
        private bool SendPacketLocked(
            Dictionary<int, byte[]> channelColors,
            CancellationToken cancellationToken,
            out bool reconnectNeeded)
        {
            reconnectNeeded = false;
            var dtlsConnection = _dtlsConnection!;
            var lifecycleGeneration = _streamLifecycleGeneration;
            try
            {
                var packet = BuildHueStreamPacketCore(_streamAreaId!.Value, channelColors);
                cancellationToken.ThrowIfCancellationRequested();
                dtlsConnection.Send(packet, 0, packet.Length);
                if (_streamLifecycleGeneration != lifecycleGeneration ||
                    !ReferenceEquals(_dtlsConnection, dtlsConnection))
                {
                    return false;
                }

                // Both caller paths supply a private snapshot: a captured frame or
                // this same last-frame cache for keepalive.
                _lastSentColors = channelColors;
                _lastSentConnection = dtlsConnection;
                _lastPacketTimestamp = _timeProvider.GetTimestamp();
                Interlocked.Increment(ref _packetsSent);
                return true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("DTLS stream was disposed while sending colors, attempting reconnect");
                InvalidateConnection(dtlsConnection);
                reconnectNeeded = !cancellationToken.IsCancellationRequested;
                return RecordPacketSendFailure(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error sending colors to bridge, attempting reconnect");
                if (cancellationToken.IsCancellationRequested)
                    return false;

                InvalidateConnection(dtlsConnection);
                reconnectNeeded = true;
                return RecordPacketSendFailure(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending colors");
                return RecordPacketSendFailure(cancellationToken);
            }
        }

        private void KeepAliveTick(object? state)
        {
            lock (_lock)
            {
                if ((long)state! != _streamLifecycleGeneration ||
                    _streamLifecycleCts.IsCancellationRequested ||
                    _streamCallerCancellationToken.IsCancellationRequested ||
                    _lastSendCancellationToken.IsCancellationRequested ||
                    _lastSentColors == null ||
                    _keepAliveTask is { IsCompleted: false } ||
                    (ReferenceEquals(_lastSentConnection, _dtlsConnection) &&
                        _timeProvider.GetElapsedTime(_lastPacketTimestamp) < KeepAliveInterval))
                {
                    return;
                }

                _keepAliveTask = SendKeepAliveAsync(_streamLifecycleGeneration);
            }
        }

        private async Task SendKeepAliveAsync(long lifecycleGeneration)
        {
            // Created under the lifecycle lock by KeepAliveTick, before StopStream
            // can retire any source. Keep cancellation linked through a reconnect.
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                _streamLifecycleCts.Token,
                _streamCallerCancellationToken,
                _lastSendCancellationToken);
            var token = cancellationSource.Token;
            try
            {
                if (IsHealthy() &&
                    SendPacketLocked(_lastSentColors!, token, out _))
                {
                    return;
                }

                if (token.IsCancellationRequested || _reconnectAttempts >= MaxReconnectAttempts)
                    return;

                if (!await TryReconnectAsync(token, lifecycleGeneration).ConfigureAwait(false))
                    return;

                lock (_lock)
                {
                    if (_streamLifecycleGeneration == lifecycleGeneration &&
                        !token.IsCancellationRequested &&
                        IsHealthy() &&
                        _lastSentColors != null &&
                        !ReferenceEquals(_lastSentConnection, _dtlsConnection))
                    {
                        SendPacketLocked(_lastSentColors, token, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hue stream keepalive failed");
            }
        }

        internal static bool IsHueStreamChannelCountWithinPacketBudget(int channelCount)
            => channelCount >= 0 && channelCount <= MaxHueStreamChannels;

        private static void ValidateChannelCount(int channelCount)
        {
            if (!IsHueStreamChannelCountWithinPacketBudget(channelCount))
            {
                throw new ArgumentOutOfRangeException(
                    "channelColors",
                    channelCount,
                    $"Hue stream packets support at most {MaxHueStreamChannels} channels.");
            }
        }

        private static void ValidateChannelColor(KeyValuePair<int, byte[]> channel)
        {
            if (channel.Key < 0 || channel.Key > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    "channelColors",
                    channel.Key,
                    "Hue channel IDs must fit in an unsigned byte (0 to 255).");
            }

            if (channel.Value == null || channel.Value.Length != 6)
            {
                throw new ArgumentException(
                    "Every channel color must contain exactly six RGB16 bytes.",
                    "channelColors");
            }
        }

        /// <summary>
        /// Detaches a failed connection only when it is still the active stream. A public
        /// replacement start can race with a send failure; the reference check prevents
        /// stale cleanup from closing the newly installed stream.
        /// </summary>
        private void InvalidateConnection(IHueDtlsConnection failedConnection)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_dtlsConnection, failedConnection))
                    return;

                try
                {
                    failedConnection.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to close the unhealthy DTLS stream");
                }

                _dtlsConnection = null;
                _connectionReadyTimestamp = null;
                _lastSentConnection = null;
            }
        }

        /// <summary>
        /// Starts one bounded reconnect worker for a failed send. The reconnect gate
        /// serializes overlapping workers and the lifecycle token cancels them on stop.
        /// </summary>
        private Task ScheduleReconnect(CancellationToken cancellationToken)
        {
            return TryReconnectAsync(cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception!.GetBaseException(), "Unobserved exception during DTLS reconnect");
                else if (!t.IsCanceled && !cancellationToken.IsCancellationRequested && !t.Result)
                    _logger.LogWarning("DTLS reconnection failed — lights may stop syncing until next playback");
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private bool RecordPacketSendFailure(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                Interlocked.Increment(ref _packetSendFailures);

            return false;
        }
    }
}
