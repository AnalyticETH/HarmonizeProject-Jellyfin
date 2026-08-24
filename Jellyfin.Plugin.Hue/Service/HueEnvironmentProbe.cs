using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Checks local runtime dependencies without contacting or mutating a Hue bridge.
/// </summary>
public interface IHueEnvironmentProbe
{
    Task<HueEnvironmentProbeResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes the executables required by playback. The commands are injectable so the
/// probe remains deterministic in tests and can be reused by hosted environments.
/// </summary>
public sealed class HueEnvironmentProbe : IHueEnvironmentProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    // The probe launches the configured playback encoder and captures a short PCM
    // stream. On a busy self-hosted Jellyfin host, process startup can legitimately
    // exceed the normal version-check budget; keep the diagnostic bounded while
    // avoiding a false unavailable result during runner contention.
    private static readonly TimeSpan AudioProbeTimeout = TimeSpan.FromSeconds(10);
    private const int MinimumAudioProbeBytes = 800;
    private const int AudioProbeSampleRate = 8000;
    private const int AudioProbeChannels = 2;
    private const string AudioProbeDuration = "0.15";

    private readonly string _ffmpegCommand;
    private readonly string _openSslCommand;
    private readonly string _versionArgument;

    public HueEnvironmentProbe(
        string ffmpegCommand = "ffmpeg",
        string openSslCommand = "openssl",
        string versionArgument = "-version",
        IMediaEncoder? mediaEncoder = null)
    {
        // Jellyfin may use a bundled encoder that is not on the service account's PATH.
        // Prefer the same configured executable playback uses, while retaining the
        // injectable command fallback for hosted environments and deterministic tests.
        _ffmpegCommand = !string.IsNullOrWhiteSpace(mediaEncoder?.EncoderPath)
            ? mediaEncoder.EncoderPath.Trim()
            : (string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand.Trim());
        _openSslCommand = string.IsNullOrWhiteSpace(openSslCommand) ? "openssl" : openSslCommand.Trim();
        _versionArgument = string.IsNullOrWhiteSpace(versionArgument) ? "-version" : versionArgument.Trim();
    }

    public async Task<HueEnvironmentProbeResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var ffmpeg = await ProbeToolAsync(_ffmpegCommand, _versionArgument, cancellationToken).ConfigureAwait(false);
        var audioCapture = await ProbeAudioCaptureAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var openSsl = await ProbeToolAsync(_openSslCommand, _versionArgument, cancellationToken).ConfigureAwait(false);
        return new HueEnvironmentProbeResult
        {
            Ffmpeg = ffmpeg,
            AudioCapture = audioCapture,
            OpenSsl = openSsl
        };
    }

    internal static string? ResolveExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var normalizedCommand = command.Trim();
        if (Path.IsPathRooted(normalizedCommand) ||
            normalizedCommand.Contains(Path.DirectorySeparatorChar) ||
            normalizedCommand.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(normalizedCommand) ? Path.GetFullPath(normalizedCommand) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, normalizedCommand + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    internal static string? ExtractVersionLine(string? standardOutput, string? standardError)
    {
        var line = (standardOutput ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Concat((standardError ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0);

        if (string.IsNullOrWhiteSpace(line))
            return null;

        return line.Length <= 180 ? line : line[..180];
    }

    private static async Task<HueToolStatus> ProbeToolAsync(
        string command,
        string versionArgument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = ResolveExecutable(command);
        if (executablePath == null)
        {
            return new HueToolStatus
            {
                Available = false,
                Message = "Executable was not found at the configured path or on the server PATH."
            };
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(versionArgument);

        try
        {
            if (!process.Start())
            {
                return new HueToolStatus
                {
                    Available = false,
                    ExecutablePath = executablePath,
                    Message = "The executable could not be started."
                };
            }
        }
        catch
        {
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = executablePath,
                Message = "The executable could not be started."
            };
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        try
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);

            var version = ExtractVersionLine(standardOutputTask.Result, standardErrorTask.Result);
            var available = process.ExitCode == 0;
            return new HueToolStatus
            {
                Available = available,
                ExecutablePath = executablePath,
                Version = version,
                Message = available
                    ? null
                    : "The executable returned a non-zero exit code."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = executablePath,
                Message = "The version check timed out."
            };
        }
        catch
        {
            StopProcess(process);
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = executablePath,
                Message = "The version check failed."
            };
        }
    }

    private static async Task<HueToolStatus> ProbeAudioCaptureAsync(
        HueToolStatus ffmpeg,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ffmpeg.Available || string.IsNullOrWhiteSpace(ffmpeg.ExecutablePath))
        {
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = ffmpeg.ExecutablePath,
                Message = "Audio capture probe skipped because FFmpeg is unavailable."
            };
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg.ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var arguments = new[]
        {
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:sample_rate={AudioProbeSampleRate}:duration={AudioProbeDuration}",
            "-map",
            "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-ar",
            AudioProbeSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac",
            AudioProbeChannels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a",
            "pcm_s16le",
            "-f",
            "s16le",
            "-t",
            AudioProbeDuration,
            "pipe:1"
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
            {
                return new HueToolStatus
                {
                    Available = false,
                    ExecutablePath = ffmpeg.ExecutablePath,
                    Message = "FFmpeg could not start the PCM audio capture probe."
                };
            }
        }
        catch
        {
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = ffmpeg.ExecutablePath,
                Message = "FFmpeg could not start the PCM audio capture probe."
            };
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(AudioProbeTimeout);

        try
        {
            var standardOutputTask = ReadBytesAsync(process.StandardOutput.BaseStream, timeoutSource.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var pcm = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            var succeeded = process.ExitCode == 0 && pcm.Length >= MinimumAudioProbeBytes;
            return new HueToolStatus
            {
                Available = succeeded,
                ExecutablePath = ffmpeg.ExecutablePath,
                Version = succeeded
                    ? $"PCM s16le {AudioProbeSampleRate} Hz stereo"
                    : null,
                Message = succeeded
                    ? null
                    : process.ExitCode != 0
                        ? $"FFmpeg audio capture probe failed: {ExtractVersionLine(null, standardError) ?? "non-zero exit code"}."
                        : $"FFmpeg audio capture probe returned only {pcm.Length} PCM bytes; at least {MinimumAudioProbeBytes} were expected."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = ffmpeg.ExecutablePath,
                Message = "The FFmpeg audio capture probe timed out."
            };
        }
        catch
        {
            StopProcess(process);
            return new HueToolStatus
            {
                Available = false,
                ExecutablePath = ffmpeg.ExecutablePath,
                Message = "The FFmpeg audio capture probe failed."
            };
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Diagnostics must not mask the original cancellation or probe failure.
        }
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

public sealed class HueEnvironmentProbeResult
{
    public HueToolStatus Ffmpeg { get; init; } = new();
    public HueToolStatus AudioCapture { get; init; } = new();
    public HueToolStatus OpenSsl { get; init; } = new();
}

public sealed class HueToolStatus
{
    public bool Available { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public string? Message { get; init; }
}
