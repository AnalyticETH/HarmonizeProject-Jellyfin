using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // How long to wait after spawning OpenSSL before attempting to write
        // The DTLS handshake typically takes 100-400ms on a local network
        private const int DtlsHandshakeWaitMs = 600;
        private const int EntertainmentAreaActivationDelayMs = 200;

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
                return _opensslProcess != null && !_opensslProcess.HasExited && _stdin != null;
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
        /// OpenSSL 3.x requires -pskcipher instead of -cipher for PSK suites.
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

            try
            {
                // The standard -cipher flag selects the TLS 1.2 cipher suite.
                // When -psk is provided, OpenSSL 3.x accepts PSK cipher suites via -cipher.
                // Note: -pskcipher and -security_level are NOT valid s_client flags —
                // they cause immediate exit with "unknown option".
                var startInfo = new ProcessStartInfo
                {
                    FileName = "openssl",
                    Arguments = $"s_client -dtls1_2 -cipher PSK-AES128-GCM-SHA256 -psk_identity {appKey} -psk {clientKey} -connect {bridgeIp}:2100",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _logger.LogInformation("Starting OpenSSL DTLS tunnel: openssl {0}", startInfo.Arguments);

                _opensslProcess = new Process { StartInfo = startInfo };
                _opensslProcess.Start();
                _stdin = _opensslProcess.StandardInput.BaseStream;

                // Log stderr asynchronously for diagnostics
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var reader = _opensslProcess.StandardError;
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            if (!string.IsNullOrEmpty(line))
                                _logger.LogDebug("OpenSSL: {0}", line);
                        }
                    }
                    catch { /* process ended */ }
                });

                // Wait for DTLS handshake to complete before returning.
                // Without this wait, the first SendColors call will fail because
                // the UDP channel isn't established yet.
                // Using await Task.Delay (not Thread.Sleep) so we yield the thread pool thread
                // during the wait rather than blocking it.
                await Task.Delay(DtlsHandshakeWaitMs).ConfigureAwait(false);

                if (_opensslProcess.HasExited)
                {
                    _logger.LogError("OpenSSL process exited immediately — check bridge IP, ClientKey hex, and that the entertainment area was activated (action=start) first.");
                    _stdin = null;
                    return;
                }

                _reconnectAttempts = 0;
                _logger.LogInformation("OpenSSL DTLS tunnel started to {0}:2100", bridgeIp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start OpenSSL process. Ensure openssl is installed.");
                throw;
            }
        }

        /// <summary>
        /// Attempts to reconnect the DTLS stream if it has failed
        /// </summary>
        private async Task<bool> TryReconnectAsync()
        {
            if (_lastConfig == null && _lastBridgeConfig == null)
                return false;

            if (_reconnectAttempts >= MaxReconnectAttempts)
                return false;

            _reconnectAttempts++;
            _logger.LogWarning("Attempting to reconnect DTLS stream (attempt {0}/{1})", _reconnectAttempts, MaxReconnectAttempts);

            try
            {
                StopStream();
                // Exponential backoff — await so we don't block a thread pool thread
                await Task.Delay(1000 * _reconnectAttempts).ConfigureAwait(false);

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
        public async Task SendColors(string areaId, Dictionary<int, byte[]> channelColors, int colorChangeThreshold = 0)
        {
            // Skip if colors haven't changed significantly
            if (colorChangeThreshold > 0 && !HasSignificantColorChange(channelColors, colorChangeThreshold))
            {
                return;
            }

            // Check health and try to reconnect if needed
            if (!IsHealthy())
            {
                _logger.LogWarning("DTLS stream unhealthy, attempting reconnect");
                if (!await TryReconnectAsync().ConfigureAwait(false))
                {
                    _logger.LogError("Failed to reconnect DTLS stream after {0} attempts", MaxReconnectAttempts);
                    return;
                }
            }

            Stream? stdinCopy;
            lock (_lock)
            {
                if (_stdin == null)
                {
                    _logger.LogWarning("Cannot send colors: DTLS stream not initialized");
                    return;
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
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Stream disposed while sending colors");
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending colors");
            }
        }
    }
}
