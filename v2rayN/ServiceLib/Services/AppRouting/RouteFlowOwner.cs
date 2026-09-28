namespace ServiceLib.Services.AppRouting;

/// <summary>Suppresses replies without fresh ownership evidence and retires changed UDP endpoints.</summary>
internal sealed class RouteFlowOwner(RouteDecision owner, Func<RouteDecision?> readOwner)
{
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
            // A fresh mismatch is terminal, including process/endpoint reuse and
            // shared binds. Never revive an old association for a different socket.
            _invalidated = current.Kind != RouteDecisionKind.Selected || current.Process != owner.Process
                || !ReferenceEquals(current.Rule, owner.Rule) || current.Endpoint != owner.Endpoint;
            return !_invalidated;
        }
    }
}
