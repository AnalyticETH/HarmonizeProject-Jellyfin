using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Hue.Hue
{
    /// <summary>
    /// Manages DTLS streaming connection to Hue Bridge for real-time entertainment control
    /// </summary>
    public class HueStreamer
    {
        private readonly ILogger<HueStreamer> _logger;
        private Process? _opensslProcess;
        private Stream? _stdin;
        private readonly object _lock = new object();
        private PluginConfiguration? _lastConfig;
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;

        public HueStreamer(ILogger<HueStreamer> logger)
        {
            _logger = logger;
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
        public void StartStream(PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(config.HueBridgeIp) || string.IsNullOrEmpty(config.HueClientKey))
            {
                _logger.LogError("Bridge IP or Client Key missing.");
                return;
            }

            if (string.IsNullOrEmpty(config.HueAppKey))
            {
                _logger.LogError("Hue App Key missing.");
                return;
            }

            _lastConfig = config;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "openssl",
                    Arguments = $"s_client -dtls1_2 -cipher PSK-AES128-GCM-SHA256 -psk_identity {config.HueAppKey} -psk {config.HueClientKey} -connect {config.HueBridgeIp}:2100",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _opensslProcess = new Process { StartInfo = startInfo };
                _opensslProcess.Start();
                _stdin = _opensslProcess.StandardInput.BaseStream;

                _reconnectAttempts = 0; // Reset on successful start
                _logger.LogInformation("OpenSSL DTLS Tunnel started to {0}:2100", config.HueBridgeIp);
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
        private bool TryReconnect()
        {
            if (_lastConfig == null || _reconnectAttempts >= MaxReconnectAttempts)
                return false;

            _reconnectAttempts++;
            _logger.LogWarning("Attempting to reconnect DTLS stream (attempt {0}/{1})", _reconnectAttempts, MaxReconnectAttempts);

            try
            {
                StopStream();
                Thread.Sleep(1000 * _reconnectAttempts); // Exponential backoff
                StartStream(_lastConfig);
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
                lock(_lock)
                {
                    _stdin?.Close();
                    if (_opensslProcess != null && !_opensslProcess.HasExited)
                    {
                        _opensslProcess.Kill();
                        _opensslProcess.WaitForExit(1000); // Wait up to 1 second
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
        /// Sends color data to the Hue Bridge for all channels in an entertainment area
        /// </summary>
        /// <param name="areaId">The entertainment area ID</param>
        /// <param name="channelColors">Dictionary mapping channel IDs to RGB color data (6 bytes per channel)</param>
        public async Task SendColors(string areaId, Dictionary<int, byte[]> channelColors)
        {
            // Check health and try to reconnect if needed
            if (!IsHealthy())
            {
                _logger.LogWarning("DTLS stream unhealthy, attempting reconnect");
                if (!TryReconnect())
                {
                    _logger.LogError("Failed to reconnect DTLS stream after {0} attempts", MaxReconnectAttempts);
                    return;
                }
            }

            if (_stdin == null)
            {
                _logger.LogWarning("Cannot send colors: DTLS stream not initialized");
                return;
            }

            // Format: HueStream + version (2.0) + AreaId + Channels
            // Following HarmonizeProject protocol

            try
            {
                lock(_lock)
                {
                    if (_stdin == null) return;
                }

                using (var ms = new MemoryStream())
                {
                    var header = Encoding.UTF8.GetBytes("HueStream");
                    ms.Write(header, 0, header.Length);

                    var version = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                    ms.Write(version, 0, version.Length);

                    var areaBytes = Encoding.UTF8.GetBytes(areaId);
                    ms.Write(areaBytes, 0, areaBytes.Length);

                    foreach (var kvp in channelColors)
                    {
                        var channelId = (byte)kvp.Key;
                        ms.WriteByte(channelId);

                        // RGB bytes: 16-bit per channel following Harmonize logic
                        // Each 8-bit color component is halved and duplicated for 16-bit representation
                        ms.Write(kvp.Value, 0, kvp.Value.Length);
                    }

                    var packet = ms.ToArray();
                    await _stdin.WriteAsync(packet, 0, packet.Length);
                    await _stdin.FlushAsync();
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("Stream disposed while sending colors");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error sending colors to bridge, attempting reconnect");
                TryReconnect();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending colors");
            }
        }
    }
}
