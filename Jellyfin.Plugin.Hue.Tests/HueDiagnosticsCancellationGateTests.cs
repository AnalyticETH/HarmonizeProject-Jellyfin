using System.Threading;
using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueDiagnosticsCancellationGateTests
{
    [Fact]
    public void CancelActive_CancelsEveryOutstandingOperation()
    {
        var gate = new HueDiagnosticsCancellationGate();
        using var first = gate.Begin(CancellationToken.None);
        using var second = gate.Begin(CancellationToken.None);

        Assert.Equal(2, gate.CancelActive());
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(0, gate.CancelActive());
    }

    [Fact]
    public void DisposingOperationRemovesItBeforeCancellation()
    {
        var gate = new HueDiagnosticsCancellationGate();
        var completed = gate.Begin(CancellationToken.None);
        var completedToken = completed.Token;
        completed.Dispose();
        using var active = gate.Begin(CancellationToken.None);

        Assert.Equal(1, gate.CancelActive());
        Assert.False(completedToken.IsCancellationRequested);
        Assert.True(active.Token.IsCancellationRequested);
    }

    [Fact]
    public void BeginLinksRequestCancellation()
    {
        var gate = new HueDiagnosticsCancellationGate();
        using var requestCancellation = new CancellationTokenSource();
        using var operation = gate.Begin(requestCancellation.Token);

        requestCancellation.Cancel();

        Assert.True(operation.Token.IsCancellationRequested);
    }
}
