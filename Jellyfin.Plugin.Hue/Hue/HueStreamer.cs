using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The area UUID does NOT go in the packet — it is established when OpenSSL connects.
    /// The bridge knows which area is active because we PUT action=start before connecting.
    /// </summary>
    public class HueStreamer
    {
        private readonly ILogger<HueStreamer> _logger;
        private Process? _opensslProcess;
        private Stream? _stdin;
        private readonly object _lock = new object();
        private PluginConfiguration? _lastConfig;
        private (string bridgeIp, string appKey, string clientKey)? _lastBridgeConfig;
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;
        private Dictionary<int, byte[]>? _lastSentColors;
        private byte _sequenceNumber = 0;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);

        // How long to wait after spawning OpenSSL before attempting to write
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

        public HueStreamer(ILogger<HueStreamer> logger)
        {
            _logger = logger;
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
                try
                {
                    return _opensslProcess != null && !_opensslProcess.HasExited && _stdin != null;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Starts a DTLS streaming connection to the Hue Bridge using OpenSSL
        /// </summary>
        /// <param name="config">Plugin configuration containing bridge IP and credentials</param>
        public async Task StartStreamAsync(PluginConfiguration config)
        {
            await StartStreamAsync(config.HueBridgeIp, config.HueAppKey, config.HueClientKey).ConfigureAwait(false);
            _lastConfig = config;
        }

        /// <summary>
        /// Starts a DTLS streaming connection to the Hue Bridge using OpenSSL with explicit parameters.
        ///
        /// Uses DTLS 1.2 with PSK. The ClientKey from Hue must be provided as hex.
        /// OpenSSL 3.x accepts the PSK suite through the standard -cipher option.
        ///
        /// IMPORTANT: This method awaits DtlsHandshakeWaitMs to allow the DTLS handshake to complete
        /// before the caller starts writing packets.
        /// </summary>
        public async Task StartStreamAsync(string bridgeIp, string appKey, string clientKey)
        {
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

            _lastBridgeConfig = (bridgeIp, appKey, clientKey);
            StopStream();
            _lastSentColors = null;

            try
            {
                // The standard -cipher flag selects the TLS 1.2 cipher suite.
                // When -psk is provided, OpenSSL 3.x accepts PSK cipher suites via -cipher.
                // Note: -pskcipher and -security_level are NOT valid s_client flags —
                // they cause immediate exit with "unknown option".
                var startInfo = new ProcessStartInfo
                {
                    FileName = "openssl",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("s_client");
                startInfo.ArgumentList.Add("-dtls1_2");
                startInfo.ArgumentList.Add("-cipher");
                startInfo.ArgumentList.Add("PSK-AES128-GCM-SHA256");
                startInfo.ArgumentList.Add("-psk_identity");
                startInfo.ArgumentList.Add(appKey);
                startInfo.ArgumentList.Add("-psk");
                startInfo.ArgumentList.Add(clientKey);
                startInfo.ArgumentList.Add("-connect");
                startInfo.ArgumentList.Add(FormatBridgeEndpoint(bridgeIp));

                _logger.LogInformation("Starting OpenSSL DTLS tunnel to {0}:2100", bridgeIp);

                var process = new Process { StartInfo = startInfo };
                process.Start();
                lock (_lock)
                {
                    _opensslProcess = process;
                    _stdin = process.StandardInput.BaseStream;
                }

                // OpenSSL writes handshake and application output to stdout. Drain it
                // continuously so the redirected pipe cannot fill and block the tunnel.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });

                // Log stderr asynchronously for diagnostics
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var reader = process.StandardError;
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                                _logger.LogDebug("OpenSSL: {0}", line);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                });

                // Wait for DTLS handshake to complete before returning.
                // Without this wait, the first SendColors call will fail because
                // the UDP channel isn't established yet.
                // during the wait rather than blocking it.
                await Task.Delay(DtlsHandshakeWaitMs).ConfigureAwait(false);

                if (process.HasExited)
                {
                    _logger.LogError("OpenSSL process exited immediately — check bridge IP, ClientKey hex, and that the entertainment area was activated (action=start) first.");
                    StopStream();
                    return;
                }

                _reconnectAttempts = 0;
                _logger.LogInformation("OpenSSL DTLS tunnel started to {0}:2100", bridgeIp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start OpenSSL process. Ensure openssl is installed.");
                StopStream();
                throw;
            }
        }

        /// <summary>
        /// Attempts to reconnect the DTLS stream if it has failed
        /// </summary>
        private async Task<bool> TryReconnectAsync()
        {
            await _reconnectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // A concurrent SendColors call may have repaired the stream while this
                // caller was waiting for the reconnect gate.
                if (IsHealthy())
                    return true;

                if (_lastConfig == null && _lastBridgeConfig == null)
                    return false;

                if (_reconnectAttempts >= MaxReconnectAttempts)
                    return false;

                _reconnectAttempts++;
                var attempt = _reconnectAttempts;
                _logger.LogWarning("Attempting to reconnect DTLS stream (attempt {0}/{1})", attempt, MaxReconnectAttempts);

                StopStream();
                // Exponential backoff — await so we don't block a thread pool thread
                await Task.Delay(1000 * attempt).ConfigureAwait(false);

                // Re-activate the entertainment area before reopening the DTLS tunnel.
                // The bridge requires action=start or it silently drops all packets.
                if (OnBeforeReconnect != null)
                {
                    if (!await OnBeforeReconnect().ConfigureAwait(false))
                    {
                        _logger.LogWarning("Pre-reconnect preparation failed, aborting reconnect");
                        return false;
                    }
                    await Task.Delay(EntertainmentAreaActivationDelayMs).ConfigureAwait(false); // Let bridge enter streaming mode
                }

                if (_lastBridgeConfig != null)
                {
                    var (bridgeIp, appKey, clientKey) = _lastBridgeConfig.Value;
                    await StartStreamAsync(bridgeIp, appKey, clientKey).ConfigureAwait(false);
                }
                else if (_lastConfig != null)
                {
                    await StartStreamAsync(_lastConfig.HueBridgeIp, _lastConfig.HueAppKey, _lastConfig.HueClientKey).ConfigureAwait(false);
                }

                return IsHealthy();
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

        public void StopStream()
        {
            try
            {
                lock (_lock)
                {
                    _stdin?.Close();
                    if (_opensslProcess != null && !_opensslProcess.HasExited)
                    {
                        _opensslProcess.Kill();
                        if (!_opensslProcess.WaitForExit(1000))
                        {
                            _logger.LogWarning("OpenSSL process did not exit within 1 second after Kill()");
                        }
                    }
                    _opensslProcess?.Dispose();
                    _opensslProcess = null;
                    _stdin = null;
                    _lastSentColors = null;
                }
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
        /// <returns>True when the packet was sent or intentionally skipped by the change threshold; otherwise false.</returns>
        public async Task<bool> SendColors(string areaId, Dictionary<int, byte[]> channelColors, int colorChangeThreshold = 0)
        {
            ArgumentNullException.ThrowIfNull(channelColors);

            // Skip if colors haven't changed significantly
            if (colorChangeThreshold > 0 && !HasSignificantColorChange(channelColors, colorChangeThreshold))
            {
                return true;
            }

            // Check health and try to reconnect if needed
            if (!IsHealthy())
            {
                _logger.LogWarning("DTLS stream unhealthy, attempting reconnect");
                if (!await TryReconnectAsync().ConfigureAwait(false))
                {
                    _logger.LogError("Failed to reconnect DTLS stream after {0} attempts", MaxReconnectAttempts);
                    return false;
                }
            }

            Stream? stdinCopy;
            lock (_lock)
            {
                if (_stdin == null)
                {
                    _logger.LogWarning("Cannot send colors: DTLS stream not initialized");
                    return false;
                }
                stdinCopy = _stdin;
            }

            try
            {
                var packet = BuildHueStreamPacket(channelColors);
                await stdinCopy.WriteAsync(packet, 0, packet.Length);
                await stdinCopy.FlushAsync();

                // Store last sent colors for change detection
                _lastSentColors = new Dictionary<int, byte[]>();
                foreach (var kvp in channelColors)
                {
                    _lastSentColors[kvp.Key] = (byte[])kvp.Value.Clone();
                }
                return true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Stream disposed while sending colors");
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error sending colors to bridge, attempting reconnect");
                _ = TryReconnectAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger.LogError(t.Exception!.GetBaseException(), "Unobserved exception during DTLS reconnect");
                    else if (t.Result == false)
                        _logger.LogWarning("DTLS reconnection failed — lights may stop syncing until next playback");
                }, TaskContinuationOptions.ExecuteSynchronously);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending colors");
                return false;
            }
        }
    }
}
