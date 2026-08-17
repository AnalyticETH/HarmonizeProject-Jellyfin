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
        Assert.Null(gate.TryEnterPlayback());

        diagnosticLease!.Dispose();
        using var playbackLease = gate.TryEnterPlayback();
        Assert.NotNull(playbackLease);
    }

    [Fact]
    public void PlaybackLeaseBlocksDiagnosticsUntilDisposed()
    {
        var gate = new HueBridgeLifecycleGate();
        var playbackLease = gate.TryEnterPlayback();

        Assert.NotNull(playbackLease);
        Assert.Null(gate.TryEnterDiagnostic());

        playbackLease!.Dispose();
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
