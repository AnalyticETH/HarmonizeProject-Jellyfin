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

    [Fact]
    public void DistinctPlaybackTargetsCanStreamConcurrentlyButSameTargetCannot()
    {
        var gate = new HueBridgeLifecycleGate();
        using var livingRoom = gate.TryEnterPlayback("192.168.1.10|living-room");
        using var bedroom = gate.TryEnterPlayback("192.168.1.10|bedroom");

        Assert.NotNull(livingRoom);
        Assert.NotNull(bedroom);
        Assert.True(gate.IsPlaybackActive);
        Assert.Null(gate.TryEnterPlayback("192.168.1.10|living-room"));
        Assert.Null(gate.TryEnterDiagnostic());

        livingRoom!.Dispose();
        Assert.True(gate.IsPlaybackActive);
        using var replacement = gate.TryEnterPlayback("192.168.1.10|living-room");
        Assert.NotNull(replacement);
    }

    [Fact]
    public void ScopedDiagnosticCanUseIndependentTargetButNotActivePlaybackTarget()
    {
        var gate = new HueBridgeLifecycleGate();
        using var livingRoom = gate.TryEnterPlayback("192.168.1.10|living-room");

        using var bedroomDiagnostic = gate.TryEnterDiagnostic("192.168.1.10|bedroom");
        Assert.NotNull(bedroomDiagnostic);
        Assert.True(gate.IsDiagnosticActive);
        Assert.False(gate.IsPlaybackActiveForResource("192.168.1.10|bedroom"));
        Assert.True(gate.IsPlaybackActiveForResource("192.168.1.10|living-room"));
        Assert.Null(gate.TryEnterDiagnostic("192.168.1.10|living-room"));

        bedroomDiagnostic!.Dispose();
        Assert.False(gate.IsDiagnosticActive);
    }

    [Fact]
    public void ConfigurationMutationBlocksNewLifecyclesUntilDisposed()
    {
        var gate = new HueBridgeLifecycleGate();
        using var mutation = gate.TryEnterConfigurationMutation();

        Assert.NotNull(mutation);
        Assert.True(gate.IsConfigurationMutationActive);
        Assert.Null(gate.TryEnterPlayback());
        Assert.Null(gate.TryEnterPlayback("192.168.1.10|living-room"));
        Assert.Null(gate.TryEnterDiagnostic());
        Assert.Null(gate.TryEnterDiagnostic("192.168.1.10|living-room"));
        Assert.Null(gate.TryEnterConfigurationMutation());

        mutation!.Dispose();
        Assert.False(gate.IsConfigurationMutationActive);
        using var playback = gate.TryEnterPlayback();
        Assert.NotNull(playback);
    }

    [Fact]
    public void ConfigurationMutationRejectsExistingLifecycle()
    {
        var gate = new HueBridgeLifecycleGate();
        using var playback = gate.TryEnterPlayback("192.168.1.10|living-room");
        Assert.NotNull(playback);
        Assert.Null(gate.TryEnterConfigurationMutation());

        playback!.Dispose();
        using var diagnostic = gate.TryEnterDiagnostic("192.168.1.10|living-room");
        Assert.NotNull(diagnostic);
        Assert.Null(gate.TryEnterConfigurationMutation());
    }

    [Fact]
    public void SchedulerEvaluationBlocksConfigurationMutationUntilDisposed()
    {
        var gate = new HueBridgeLifecycleGate();
        using var evaluation = gate.TryEnterSchedulerEvaluation();

        Assert.NotNull(evaluation);
        Assert.True(gate.IsSchedulerEvaluationActive);
        Assert.Null(gate.TryEnterConfigurationMutation());
        var secondEvaluation = gate.TryEnterSchedulerEvaluation();
        Assert.NotNull(secondEvaluation);

        secondEvaluation!.Dispose();
        evaluation!.Dispose();
        Assert.False(gate.IsSchedulerEvaluationActive);
        using var mutation = gate.TryEnterConfigurationMutation();
        Assert.NotNull(mutation);
    }
}
