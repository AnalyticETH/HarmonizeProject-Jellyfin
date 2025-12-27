using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Hue.Video
{
    public class FfmpegStreamer
    {
        private readonly ILogger<FfmpegStreamer> _logger;
        private Process? _ffmpegProcess;

        public FfmpegStreamer(ILogger<FfmpegStreamer> logger)
        {
            _logger = logger;
        }

        public Stream? StartFfmpeg(string videoPath, int fps = 20, string customFlags = "", string ffmpegPath = "ffmpeg")
        {
             // -vf scale=160:90 -f rawvideo -pix_fmt rgb24
             // Add -r {fps} and custom flags
             var flags = string.IsNullOrEmpty(customFlags) ? "" : customFlags + " ";
             var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"{flags}-re -i \"{videoPath}\" -vf scale=160:90 -r {fps} -f rawvideo -pix_fmt rgb24 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true, // To avoid cluttering stdout
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            _logger.LogInformation("Starting FFmpeg: {0} {1}", startInfo.FileName, startInfo.Arguments);

            _ffmpegProcess = new Process { StartInfo = startInfo };
            _ffmpegProcess.Start();
            
            return _ffmpegProcess.StandardOutput.BaseStream;
        }

        public void Stop()
        {
             try 
            {
                _ffmpegProcess?.Kill();
                _ffmpegProcess = null;
            } 
            catch {}
        }
    }
}
