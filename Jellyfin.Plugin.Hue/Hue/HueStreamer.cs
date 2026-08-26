using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Hue
{
    /// <summary>
    /// Manages DTLS streaming connection to Hue Bridge for real-time entertainment control.
    ///
    /// Hue Entertainment API v2 packet format (per Philips documentation):
    ///   Header:  "HueStream" (9 bytes, ASCII)
    ///   Version: 0x02 0x00           (2 bytes, major.minor)
    ///   SeqNum:  0x00                (1 byte, wrapping sequence number)
    ///   Reserved:0x00 0x00           (2 bytes)
    ///   ColorSpace: 0x00             (1 byte: 0x00=RGB, 0x01=XY Brightness)
    ///   Reserved:0x00                (1 byte)
    ///   Per channel: type(1) + id_hi(1) + id_lo(1) + r_hi(1) + r_lo(1) + g_hi(1) + g_lo(1) + b_hi(1) + b_lo(1)
    ///     type: 0x00 = light device
    ///
    /// The area UUID does NOT go in the packet — it is established when the managed DTLS session connects.
    /// The bridge knows which area is active because we PUT action=start before connecting.
    /// </summary>
    public class HueStreamer
    {
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
            Func<string, string, string, CancellationToken, Task<IHueDtlsConnection>>? connectDtlsAsync)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectDtlsAsync = connectDtlsAsync ?? ConnectDefaultDtlsAsync;
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
                return _dtlsConnection?.IsHealthy == true;
            }
        }

        /// <summary>
        /// Starts a managed DTLS streaming connection to the Hue Bridge
        /// </summary>
        /// <param name="config">Plugin configuration containing bridge IP and credentials</param>
        public async Task StartStreamAsync(PluginConfiguration config)
        {
            await StartStreamAsync(config.HueBridgeIp, config.HueAppKey, config.HueClientKey).ConfigureAwait(false);
            _lastConfig = config;
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
            // Reconnects use the same gate. A public replacement start must wait for an
            // in-flight reconnect to finish (or observe its canceled lifecycle) before
            // replacing the connection; otherwise stale reconnect cleanup can close the
            // newly installed stream after this method returns.
            await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StartStreamCoreAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    cancellationToken,
                    cancelPendingReconnect: true).ConfigureAwait(false);
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        private async Task StartStreamCoreAsync(
            string bridgeIp,
            string appKey,
            string clientKey,
            CancellationToken cancellationToken,
            bool cancelPendingReconnect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(bridgeIp) || string.IsNullOrEmpty(clientKey))
            {
                _logger.LogError("Bridge IP or Client Key missing.");
                return;
            }

            if (string.IsNullOrEmpty(appKey))
            {
                _logger.LogError("Hue App Key missing.");
                return;
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

            // A reconnect already owns the current lifecycle token. Do not cancel that
            // token when it replaces the failed connection; an external StopStream still can.
            StopStream(cancelPendingReconnect);
            _lastBridgeConfig = (bridgeIp, appKey, clientKey);
            _lastSentColors = null;

            CancellationTokenSource? startupTokenSource = null;
            CancellationToken startupToken;
            lock (_lock)
            {
                startupToken = _streamLifecycleCts.Token;
            }

            if (cancellationToken.CanBeCanceled)
            {
                startupTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    startupToken);
                startupToken = startupTokenSource.Token;
            }

            try
            {
                // Use a managed DTLS PSK session so the App Key and Client Key remain in
                // this process's memory instead of being exposed through /proc or ps.
                _logger.LogInformation("Starting managed DTLS tunnel to {0}:2100", bridgeIp);

                var dtlsConnection = await _connectDtlsAsync(
                    bridgeIp,
                    appKey,
                    clientKey,
                    startupToken).ConfigureAwait(false);

                if (startupToken.IsCancellationRequested)
                {
                    dtlsConnection.Close();
                    startupToken.ThrowIfCancellationRequested();
                }

                lock (_lock)
                {
                    _dtlsConnection = dtlsConnection;
                }

                // Keep the existing short bridge-settle delay so the first packet is
                // sent only after the entertainment area has switched to streaming mode.
                await Task.Delay(DtlsHandshakeWaitMs, startupToken).ConfigureAwait(false);

                if (!dtlsConnection.IsHealthy)
                {
                    _logger.LogError("Managed DTLS tunnel closed immediately — check bridge IP, Client Key, and that the entertainment area was activated (action=start) first.");
                    // A reconnect failure must leave the saved target and lifecycle
                    // token intact so a later SendColors call can consume the remaining
                    // bounded retry budget. A public startup still clears the target.
                    StopStream(cancelPendingReconnect);
                    return;
                }

                _reconnectAttempts = 0;
                _logger.LogInformation("Managed DTLS tunnel started to {0}:2100", bridgeIp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start the managed Hue DTLS connection.");
                // Preserve reconnect state after an internal startup failure. Clearing
                // _lastBridgeConfig here would make all later frames permanently unable
                // to retry even though MaxReconnectAttempts has not been exhausted.
                StopStream(cancelPendingReconnect);
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
        private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
        {
            CancellationToken lifecycleToken;
            lock (_lock)
            {
                lifecycleToken = _streamLifecycleCts.Token;
            }

            using var reconnectTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifecycleToken);
            var reconnectToken = reconnectTokenSource.Token;

            await _reconnectLock.WaitAsync(reconnectToken).ConfigureAwait(false);
            try
            {
                reconnectToken.ThrowIfCancellationRequested();

                // A concurrent SendColors call may have repaired the stream while this
                // caller was waiting for the reconnect gate.
                if (IsHealthy())
                    return true;

                if (_lastConfig == null && _lastBridgeConfig == null)
                    return false;

                if (_reconnectAttempts >= MaxReconnectAttempts)
                    return false;

                _reconnectAttempts++;
                _totalReconnectAttempts++;
                var attempt = _reconnectAttempts;
                _logger.LogWarning("Attempting to reconnect DTLS stream (attempt {0}/{1})", attempt, MaxReconnectAttempts);

                StopStream(cancelPendingReconnect: false);
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

                if (_lastBridgeConfig != null)
                {
                    var (bridgeIp, appKey, clientKey) = _lastBridgeConfig.Value;
                    await StartStreamCoreAsync(
                        bridgeIp,
                        appKey,
                        clientKey,
                        reconnectToken,
                        cancelPendingReconnect: false).ConfigureAwait(false);
                }
                else if (_lastConfig != null)
                {
                    await StartStreamCoreAsync(
                        _lastConfig.HueBridgeIp,
                        _lastConfig.HueAppKey,
                        _lastConfig.HueClientKey,
                        reconnectToken,
                        cancelPendingReconnect: false).ConfigureAwait(false);
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
                _reconnectLock.Release();
            }
        }

        public virtual void StopStream()
            => StopStream(cancelPendingReconnect: true);

        private void StopStream(bool cancelPendingReconnect)
        {
            CancellationTokenSource? canceledLifecycle = null;
            try
            {
                lock (_lock)
                {
                    if (cancelPendingReconnect)
                    {
                        canceledLifecycle = _streamLifecycleCts;
                        _streamLifecycleCts = new CancellationTokenSource();
                    }

                    _dtlsConnection?.Close();
                    _dtlsConnection = null;
                    _lastSentColors = null;
                    if (cancelPendingReconnect)
                    {
                        // A public stop ends the stream lifecycle completely. Clear the
                        // saved target so a stale caller cannot resurrect a later tunnel.
                        _lastConfig = null;
                        _lastBridgeConfig = null;
                    }
                }
                canceledLifecycle?.Cancel();
                _logger.LogInformation("DTLS stream stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping DTLS stream");
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
        ///   Repeated per channel (9 bytes each):
        ///     [0]   0x00  — device type: light
        ///     [1]   channelId >> 8  (high byte of 16-bit channel ID)
        ///     [2]   channelId &amp; 0xFF (low byte)
        ///     [3]   R high byte
        ///     [4]   R low byte
        ///     [5]   G high byte
        ///     [6]   G low byte
        ///     [7]   B high byte
        ///     [8]   B low byte
        ///
        /// channelColors values must be 6 bytes: [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo]
        /// </summary>
        public byte[] BuildHueStreamPacket(Dictionary<int, byte[]> channelColors)
        {
            ArgumentNullException.ThrowIfNull(channelColors);

            using var ms = new MemoryStream(16 + channelColors.Count * 9);

            // Fixed 9-byte ASCII magic
            ms.Write(Encoding.ASCII.GetBytes("HueStream"), 0, 9);

            // Version 2.0
            ms.WriteByte(0x02); // major
            ms.WriteByte(0x00); // minor

            // Sequence number (wraps 0-255)
            ms.WriteByte(_sequenceNumber++);

            // 2 reserved bytes
            ms.WriteByte(0x00);
            ms.WriteByte(0x00);

            // Color space: 0x00 = RGB
            ms.WriteByte(0x00);

            // 1 reserved byte
            ms.WriteByte(0x00);

            // Channel data
            foreach (var kvp in channelColors)
            {
                int channelId = kvp.Key;
                var rgb16 = kvp.Value; // [R_hi, R_lo, G_hi, G_lo, B_hi, B_lo]

                if (channelId < 0 || channelId > ushort.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(channelColors), channelId, "Hue channel IDs must fit in an unsigned 16-bit value.");

                if (rgb16 == null || rgb16.Length != 6)
                    throw new ArgumentException("Every channel color must contain exactly six RGB16 bytes.", nameof(channelColors));

                ms.WriteByte(0x00);               // device type: light
                ms.WriteByte((byte)(channelId >> 8));   // channel ID high byte
                ms.WriteByte((byte)(channelId & 0xFF)); // channel ID low byte
                ms.Write(rgb16, 0, 6);            // RRGGBB (16-bit each)
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Sends color data to the Hue Bridge for all channels in an entertainment area.
        /// The areaId parameter is kept for API compatibility but is no longer embedded
        /// in the packet — the area is selected when the DTLS session is opened via action=start.
        /// </summary>
        /// <param name="areaId">The entertainment area ID (used for logging only)</param>
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

            // Check health before applying the color-change threshold. A static scene can
            // legitimately produce identical frames for a long time; suppressing the
            // health check in that case would allow a dead DTLS process to remain broken
            // indefinitely while every frame is reported as successfully skipped.
            if (!IsHealthy())
            {
                _logger.LogWarning("DTLS stream unhealthy, attempting reconnect");
                if (!await TryReconnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogError("Failed to reconnect DTLS stream after {0} attempts", MaxReconnectAttempts);
                    return RecordPacketSendFailure(cancellationToken);
                }
            }

            // Skip if colors haven't changed significantly. This is safe only after the
            // stream has been confirmed healthy (or successfully reconnected).
            if (colorChangeThreshold > 0 && !HasSignificantColorChange(channelColors, colorChangeThreshold))
            {
                Interlocked.Increment(ref _packetsSkippedByThreshold);
                return true;
            }

            IHueDtlsConnection? dtlsConnection;
            lock (_lock)
            {
                if (_dtlsConnection == null)
                {
                    _logger.LogWarning("Cannot send colors: DTLS stream not initialized");
                    return RecordPacketSendFailure(cancellationToken);
                }
                dtlsConnection = _dtlsConnection;
            }

            try
            {
                var packet = BuildHueStreamPacket(channelColors);
                cancellationToken.ThrowIfCancellationRequested();
                dtlsConnection.Send(packet, 0, packet.Length);

                // Store last sent colors for change detection
                _lastSentColors = new Dictionary<int, byte[]>();
                foreach (var kvp in channelColors)
                {
                    _lastSentColors[kvp.Key] = (byte[])kvp.Value.Clone();
                }
                Interlocked.Increment(ref _packetsSent);
                return true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Stream disposed while sending colors");
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

                _ = TryReconnectAsync(cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger.LogError(t.Exception!.GetBaseException(), "Unobserved exception during DTLS reconnect");
                    else if (t.Result == false && !cancellationToken.IsCancellationRequested)
                        _logger.LogWarning("DTLS reconnection failed — lights may stop syncing until next playback");
                }, TaskContinuationOptions.ExecuteSynchronously);
                return RecordPacketSendFailure(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending colors");
                return RecordPacketSendFailure(cancellationToken);
            }
        }

        private bool RecordPacketSendFailure(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                Interlocked.Increment(ref _packetSendFailures);

            return false;
        }
    }
}
