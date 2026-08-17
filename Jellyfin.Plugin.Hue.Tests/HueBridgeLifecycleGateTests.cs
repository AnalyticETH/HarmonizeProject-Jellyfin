using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueBridgeLifecycleGateTests
{
    [Fact]
    public void DiagnosticLeaseBlocksPlaybackUntilDisposed()
    {
        var gate = new HueBridgeLifecycleGate();
        var diagnosticLease = gate.TryEnterDiagnostic();

        Assert.NotNull(diagnosticLease);
        Assert.False(gate.IsPlaybackActive);
        Assert.True(gate.IsDiagnosticActive);
        Assert.Null(gate.TryEnterPlayback());

        diagnosticLease!.Dispose();
        Assert.False(gate.IsDiagnosticActive);
        using var playbackLease = gate.TryEnterPlayback();
        Assert.NotNull(playbackLease);
        Assert.True(gate.IsPlaybackActive);
    }

    [Fact]
    public void PlaybackLeaseBlocksDiagnosticsUntilDisposed()
    {
        var gate = new HueBridgeLifecycleGate();
        var playbackLease = gate.TryEnterPlayback();

        Assert.NotNull(playbackLease);
        Assert.True(gate.IsPlaybackActive);
        Assert.False(gate.IsDiagnosticActive);
        Assert.Null(gate.TryEnterDiagnostic());

        playbackLease!.Dispose();
        Assert.False(gate.IsPlaybackActive);
        using var diagnosticLease = gate.TryEnterDiagnostic();
        Assert.NotNull(diagnosticLease);
    }

    [Fact]
    public void DisposingLeaseTwiceDoesNotReleaseAnotherLease()
    {
        var gate = new HueBridgeLifecycleGate();
        var firstLease = gate.TryEnterDiagnostic();

        Assert.NotNull(firstLease);
        firstLease!.Dispose();
        firstLease.Dispose();

        using var secondLease = gate.TryEnterDiagnostic();
        Assert.NotNull(secondLease);
        Assert.Null(gate.TryEnterPlayback());
    }
}
