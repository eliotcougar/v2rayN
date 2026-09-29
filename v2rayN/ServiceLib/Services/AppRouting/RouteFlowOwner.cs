namespace ServiceLib.Services.AppRouting;

/// <summary>Suppresses replies without fresh ownership evidence and retires changed UDP endpoints.</summary>
internal sealed class RouteFlowOwner(RouteDecision owner, Func<RouteDecision?> readOwner)
{
    // This bounds local attribution waiting, independently of network round-trip time.
    private const int ReplyWaitMilliseconds = 500;
    private readonly object _gate = new();
    private bool _invalidated;

    public bool IsInvalidated
    {
        get { lock (_gate) { return _invalidated; } }
    }

    public bool IsCurrent()
    {
        lock (_gate)
        {
            if (_invalidated) { return false; }
            // A stale snapshot cannot authorize a reply, but is not evidence of
            // socket closure. Keep the association for the next fresh snapshot.
            var current = readOwner();
            if (current == null) { return false; }
            // An unchanged live socket can temporarily lack its service name.
            // Suppress replies until it resolves, without abandoning the association.
            if (current.Kind == RouteDecisionKind.Unresolved && current.ServicePending && owner.Endpoint != 0 &&
                current.Process == owner.Process && current.Endpoint == owner.Endpoint) { return false; }
            // A fresh mismatch is terminal, including process/endpoint reuse and
            // shared binds. Never revive an old association for a different socket.
            _invalidated = current.Kind != RouteDecisionKind.Selected || current.Process != owner.Process
                || !ReferenceEquals(current.Rule, owner.Rule) || current.Endpoint != owner.Endpoint;
            return !_invalidated;
        }
    }

    public async ValueTask<bool> WaitForCurrentAsync(RouteAttributionUpdates updates, Action requestRefresh, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + ReplyWaitMilliseconds;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Subscribe before reading ownership so a concurrent publication cannot
            // leave us waiting for an update that has already happened.
            var changed = updates.Next;
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) { return false; }
            if (IsCurrent()) { return true; }
            if (IsInvalidated) { return false; }
            requestRefresh();
            try { await changed.WaitAsync(TimeSpan.FromMilliseconds(remaining), token); }
            catch (TimeoutException) { return false; }
        }
    }
}
