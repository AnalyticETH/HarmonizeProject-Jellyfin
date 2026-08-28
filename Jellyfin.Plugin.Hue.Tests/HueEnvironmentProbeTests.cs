using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Service;
using MediaBrowser.Controller.MediaEncoding;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueEnvironmentProbeTests
{
    [Fact]
    public void ResolveExecutable_AcceptsTheCurrentProcessPath()
    {
        var processPath = Environment.ProcessPath;

        Assert.False(string.IsNullOrWhiteSpace(processPath));
        Assert.Equal(
            Path.GetFullPath(processPath!),
            HueEnvironmentProbe.ResolveExecutable(processPath!));
    }

    [Fact]
    public void ExtractVersionLine_PrefersFirstNonEmptyOutputLineAndLimitsLength()
    {
        var version = HueEnvironmentProbe.ExtractVersionLine(
            "\n  ffmpeg version 7.0\nsecond line",
            "diagnostic output");

        Assert.Equal("ffmpeg version 7.0", version);
        Assert.True(
            HueEnvironmentProbe.ExtractVersionLine(new string('x', 200), null)!.Length <= 180);
    }

    [Fact]
    public async Task CheckAsync_WhenToolsAreMissingReportsActionableStatuses()
    {
        var probe = new HueEnvironmentProbe(
            "/definitely-missing-hue-ffmpeg",
            "/definitely-missing-hue-openssl");

        var result = await probe.CheckAsync();

        Assert.False(result.Ffmpeg.Available);
        Assert.False(result.AudioCapture.Available);
        Assert.True(result.OpenSsl.Available);
        Assert.Contains("PATH", result.Ffmpeg.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("managed", result.OpenSsl.Version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_ReportsVersionForAvailableExecutable()
    {
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        var probe = new HueEnvironmentProbe(processPath!, processPath!, "--version");

        var result = await probe.CheckAsync();

        Assert.True(result.Ffmpeg.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Ffmpeg.Version));
        Assert.False(result.AudioCapture.Available);
        Assert.Contains("audio capture", result.AudioCapture.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.OpenSsl.Available);
        Assert.Contains("BouncyCastle", result.OpenSsl.Version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_WhenFfmpegCanCapturePcmReportsAudioCapability()
    {
        var ffmpegPath = HueEnvironmentProbe.ResolveExecutable("ffmpeg");
        var processPath = Environment.ProcessPath;
        if (ffmpegPath == null || string.IsNullOrWhiteSpace(processPath))
            return;

        var probe = new HueEnvironmentProbe(ffmpegPath, processPath!, "-version");

        var result = await probe.CheckAsync();

        Assert.True(result.Ffmpeg.Available);
        Assert.True(result.AudioCapture.Available, result.AudioCapture.Message);
        Assert.Contains("PCM s16le", result.AudioCapture.Version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_UsesConfiguredMediaEncoderPathForFfmpeg()
    {
        var processPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));

        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.SetupGet(encoder => encoder.EncoderPath).Returns(processPath!);
        var probe = new HueEnvironmentProbe(
            openSslCommand: processPath!,
            versionArgument: "--version",
            mediaEncoder: mediaEncoder.Object);

        var result = await probe.CheckAsync();

        Assert.True(result.Ffmpeg.Available);
        Assert.Equal(Path.GetFullPath(processPath!), result.Ffmpeg.ExecutablePath);
        Assert.False(string.IsNullOrWhiteSpace(result.Ffmpeg.Version));
    }

    [Fact]
    public async Task CheckAsync_WhenCanceledBeforeProbeHonorsCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var probe = new HueEnvironmentProbe(
            "/definitely-missing-hue-ffmpeg",
            "/definitely-missing-hue-openssl");

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.CheckAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task CheckAsync_WhenVersionStdoutIsUnboundedStopsAndReportsUnavailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var yesPath = HueEnvironmentProbe.ResolveExecutable("yes");
        if (yesPath == null)
            return;

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probe = new HueEnvironmentProbe(yesPath, versionArgument: "fixture");

        var result = await probe.CheckAsync(cancellationSource.Token).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.False(result.Ffmpeg.Available);
        Assert.Contains("too much output", result.Ffmpeg.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_WhenVersionStderrIsUnboundedStopsAndReportsUnavailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var yesPath = HueEnvironmentProbe.ResolveExecutable("yes");
        if (yesPath == null)
            return;

        var scriptPath = CreateExecutableScript(
            $"if [ \"$1\" = \"--version\" ]; then\n  exec \"{yesPath}\" fixture >&2\nfi\nprintf 'fixture version\\n'\n");
        try
        {
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var probe = new HueEnvironmentProbe(scriptPath, versionArgument: "--version");

            var result = await probe.CheckAsync(cancellationSource.Token).WaitAsync(TimeSpan.FromSeconds(8));

            Assert.False(result.Ffmpeg.Available);
            Assert.Contains("too much output", result.Ffmpeg.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    [Fact]
    public async Task CheckAsync_WhenPcmStdoutIsUnboundedStopsAndReportsUnavailable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var yesPath = HueEnvironmentProbe.ResolveExecutable("yes");
        if (yesPath == null)
            return;

        var scriptPath = CreateExecutableScript(
            $"if [ \"$1\" = \"--version\" ]; then\n  printf 'fixture version\\n'\n  exit 0\nfi\nexec \"{yesPath}\" fixture\n");
        try
        {
            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var probe = new HueEnvironmentProbe(scriptPath, versionArgument: "--version");

            var result = await probe.CheckAsync(cancellationSource.Token).WaitAsync(TimeSpan.FromSeconds(8));

            Assert.True(result.Ffmpeg.Available);
            Assert.False(result.AudioCapture.Available);
            Assert.Contains("too much output", result.AudioCapture.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private static string CreateExecutableScript(string contents)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"hue-environment-probe-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{contents}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Test fixture cleanup must not mask the probe assertion.
        }
    }
}
