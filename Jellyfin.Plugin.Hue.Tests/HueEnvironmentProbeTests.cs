using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Hue.Service;
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
        Assert.False(result.OpenSsl.Available);
        Assert.Contains("PATH", result.Ffmpeg.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", result.OpenSsl.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_ReportsVersionForAvailableExecutable()
    {
        var probe = new HueEnvironmentProbe("dotnet", "dotnet", "--version");

        var result = await probe.CheckAsync();

        Assert.True(result.Ffmpeg.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Ffmpeg.Version));
        Assert.True(result.OpenSsl.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.OpenSsl.Version));
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
}
