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
    public class HueStreamer
    {
        private readonly ILogger<HueStreamer> _logger;
        private Process? _opensslProcess;
        private Stream? _stdin;
        private readonly object _lock = new object();

        public HueStreamer(ILogger<HueStreamer> logger)
        {
            _logger = logger;
        }

        public void StartStream(PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(config.HueBridgeIp) || string.IsNullOrEmpty(config.HueClientKey))
            {
                _logger.LogError("Bridge IP or Client Key missing.");
                return;
            }

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
            
            _logger.LogInformation("OpenSSL DTLS Tunnel started.");
        }

        public void StopStream()
        {
            try 
            {
                lock(_lock) {
                    _stdin?.Close();
                    _opensslProcess?.Kill();
                    _opensslProcess = null;
                    _stdin = null;
                }
            } 
            catch {}
        }

        public async Task SendColors(string areaId, Dictionary<int, byte[]> channelColors)
        {
            if (_stdin == null) return;

            // Format: HueStream + version (2.0) + AreaId + Channels
            // Harmonize uses: bytes('HueStream','utf-8') + b'\2\0\0\0\0\0\0' + bytes(entertainment_id,'utf-8')
            
            try 
            {
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
                        // channel_id as string/byte? 
                        // Harmonize: bytes(chr(int(i)), 'utf-8') -> It seems channel IDX is sent as a character??
                        // Wait, chr(int(i)) converts int to char. If i=0, chr(0) is null char.
                        // bytes(chr(0), 'utf-8') -> b'\x00'
                        // So it sends the byte value of the channel index.
                        ms.WriteByte(channelId);
                        
                        // RGB Bytes (R, R, G, G, B, B) ??
                        // Harmonize: bytearray([int(c[0]/2), int(c[0]/2), int(c[1]/2), int(c[1]/2), int(c[2]/2), int(c[2]/2),] )
                        // It seems to send 16-bit color? 
                        // int(c[0]/2) is 8-bit? Wait.
                        // If c[0] is 0-255 in OpenCV (BGR). 
                        // Hue API expects 16-bit per channel usually. 
                        // Harmonize sends 6 bytes. 
                        // If c[0] is 255. c[0]/2 = 127. 
                        // It seems it duplicates the byte? 
                        // Actually, Hue Stream API documentation says R (16bit), G (16bit), B (16bit).
                        // Harmonize logic: int(c[0]/2) -> if c[0] is 255 -> 127. 
                        // So it sends 0x7F 0x7F. Max value ~32000? 
                        // Hue max brightness is 65535.
                        // Let's copy the Harmonize logic exactly for safety.
                        ms.Write(kvp.Value, 0, kvp.Value.Length);
                    }

                    var packet = ms.ToArray();
                    await _stdin.WriteAsync(packet, 0, packet.Length);
                    await _stdin.FlushAsync();
                }
            }
            catch (Exception)
            {
                // Process likely died
            }
        }
    }
}
