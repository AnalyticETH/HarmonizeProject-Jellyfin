using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Hue.Service;

/// <summary>
/// Tracks non-mutating administrator diagnostics so the configuration page can request
/// cancellation while a prerequisite or multi-target check is still in flight.
/// </summary>
public sealed class HueDiagnosticsCancellationGate
{
    private readonly object _sync = new();
    private readonly HashSet<CancellationTokenSource> _activeOperations = new();

    /// <summary>
    /// Starts one diagnostics operation linked to the HTTP request lifetime.
    /// </summary>
    public OperationLease Begin(CancellationToken requestCancellation)
    {
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
        lock (_sync)
        {
            _activeOperations.Add(operationCancellation);
        }

        return new OperationLease(this, operationCancellation);
    }

    /// <summary>
    /// Requests cancellation for every active administrator diagnostics operation.
    /// The operation still owns cleanup/disposal through its normal request lifecycle.
    /// </summary>
    public int CancelActive()
    {
        CancellationTokenSource[] operations;
        lock (_sync)
        {
            operations = _activeOperations.ToArray();
        }

        var canceledCount = 0;
        foreach (var operation in operations)
        {
            try
            {
                if (!operation.IsCancellationRequested)
                {
                    operation.Cancel(throwOnFirstException: false);
                    canceledCount++;
                }
            }
            catch (ObjectDisposedException)
            {
                // The request completed between the snapshot and cancellation. Its
                // lease has already disposed the source, so there is nothing to do.
            }
        }

        return canceledCount;
    }

    private void Release(CancellationTokenSource operationCancellation)
    {
        lock (_sync)
        {
            _activeOperations.Remove(operationCancellation);
        }

        operationCancellation.Dispose();
    }

    /// <summary>
    /// Owns a linked cancellation source until the diagnostics request completes.
    /// </summary>
    public sealed class OperationLease : IDisposable
    {
        private HueDiagnosticsCancellationGate? _owner;
        private readonly CancellationTokenSource _operationCancellation;

        internal OperationLease(
            HueDiagnosticsCancellationGate owner,
            CancellationTokenSource operationCancellation)
        {
            _owner = owner;
            _operationCancellation = operationCancellation;
        }

        public CancellationToken Token => _operationCancellation.Token;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_operationCancellation);
        }
    }
}
