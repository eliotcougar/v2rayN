namespace ServiceLib.Services.AppRouting;

internal enum RouteDecisionKind { Selected, Unselected, Unresolved, Ambiguous }
internal sealed record RouteDecision(RouteDecisionKind Kind, RouteProcessKey? Process = null, AppRouteRule? Rule = null, ulong Endpoint = 0)
{
    public static readonly RouteDecision Unresolved = new(RouteDecisionKind.Unresolved);
    public static readonly RouteDecision Unselected = new(RouteDecisionKind.Unselected);

    public RouteDecision ForFlow(RouteFlow flow) => Kind == RouteDecisionKind.Selected
        && Rule?.CapturePorts is { } ports && !ports.Matches(flow) ? Unselected : this;
}

internal sealed class RoutePolicy(IReadOnlyList<AppRouteRule> rules, IEnumerable<int> excluded, RouteSharedPolicy? shared = null)
{
    public AppRouteMatcher Matcher { get; } = new(rules, shared);
    public HashSet<int> Excluded { get; } = excluded.Append(Environment.ProcessId).ToHashSet();
    private readonly Dictionary<string, string> _signatures = rules.ToDictionary(r => r.Id, r => Signature(r));
    internal static string Signature(AppRouteRule rule) => JsonUtils.Serialize(rule) + (rule.ProxyEndpoint == null ? "" : JsonUtils.Serialize(rule.ProxyEndpoint));
    public bool Retains(AppRouteRule rule) => Retains(rule.Id, Signature(rule));
    public bool Retains(string id, string signature) => (_signatures.TryGetValue(id, out var current) && current == signature) || shared?.Retains(id, signature) == true;
}

/// <summary>Immutable indexed ownership and precomputed process decisions. No native calls on lookup.</summary>
internal sealed class RouteAttributionSnapshot
{
    private readonly Dictionary<RouteFlow, RouteDecision> _tcp;
    private readonly Dictionary<(IPAddress, ushort), RouteDecision[]> _udp;
    private readonly RouteSocketSnapshot? _events;
    public long ReadAt { get; }

    public RouteAttributionSnapshot(IEnumerable<RouteOwnerTable.Row> tcp, IEnumerable<RouteOwnerTable.Row> udp,
        Func<int, RouteDecision> decide, long readAt, RouteSocketSnapshot? events = null)
    {
        ReadAt = readAt;
        _events = events;
        _tcp = tcp.GroupBy(r => new RouteFlow(6, r.Local, r.Port, r.Remote!, r.RemotePort))
            .ToDictionary(g => g.Key, g => Merge(g.Select(r => r.Pid).Distinct().Select(pid => decide(pid).ForFlow(g.Key))));
        _udp = udp.GroupBy(r => (r.Local, r.Port))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Pid).Distinct().Select(decide).Distinct().ToArray());
    }

    public RouteDecision Find(RouteFlow flow, long timestamp = 0)
    {
        var observed = _events?.Find(flow, timestamp);
        var sampled = FindTable(flow);
        if (observed == null) { return sampled ?? RouteDecision.Unresolved; }
        // A historical packet can legitimately predate the table's current owner.
        // For live endpoints, retain the existing shared-bind ambiguity checks.
        if (timestamp != 0 && observed != _events!.Find(flow, 0)) { return observed; }
        if (observed.Kind == RouteDecisionKind.Unresolved || sampled?.Kind == RouteDecisionKind.Unresolved) { return RouteDecision.Unresolved; }
        if (sampled == null || observed.Kind == sampled.Kind && observed.Process == sampled.Process && observed.Rule == sampled.Rule) { return observed; }
        return Merge(observed, sampled);
    }

    private RouteDecision? FindTable(RouteFlow flow)
    {
        if (flow.Protocol == 6) { return _tcp.GetValueOrDefault(flow); }
        var exact = ForFlow(_udp.GetValueOrDefault((flow.LocalAddress, flow.LocalPort)), flow);
        var any = flow.LocalAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        var wildcard = ForFlow(_udp.GetValueOrDefault((any, flow.LocalPort)), flow);
        if (exact == null) { return wildcard; }
        if (wildcard == null || exact == wildcard) { return exact; }
        return Merge(exact, wildcard);
    }

    private static RouteDecision? ForFlow(RouteDecision[]? owners, RouteFlow flow)
    {
        // UDP binds have no remote port. Apply eligibility before merging owners,
        // so a port-only rule cannot make unrelated destinations ambiguous.
        RouteDecision? result = null;
        if (owners == null) { return null; }
        foreach (var owner in owners)
        {
            var decision = owner.ForFlow(flow);
            result = result == null ? decision : Merge(result, decision);
        }
        return result;
    }

    internal static RouteDecision Merge(RouteDecision first, RouteDecision second)
    {
        if (first == second) { return first; }
        if (first.Kind is RouteDecisionKind.Selected or RouteDecisionKind.Ambiguous || second.Kind is RouteDecisionKind.Selected or RouteDecisionKind.Ambiguous)
        { return new(RouteDecisionKind.Ambiguous); }
        return first.Kind == RouteDecisionKind.Unresolved || second.Kind == RouteDecisionKind.Unresolved
            ? RouteDecision.Unresolved : RouteDecision.Unselected;
    }

    internal static RouteDecision Merge(IEnumerable<RouteDecision> decisions)
    {
        var owners = decisions.Distinct().ToArray();
        if (owners.Length == 1) { return owners[0]; }
        if (owners.Any(d => d.Kind is RouteDecisionKind.Selected or RouteDecisionKind.Ambiguous)) { return new(RouteDecisionKind.Ambiguous); }
        return owners.Any(d => d.Kind == RouteDecisionKind.Unresolved) ? RouteDecision.Unresolved : RouteDecision.Unselected;
    }
}

/// <summary>Only background preparation/refresh calls this source. Process handles and ancestry
/// are retained across policy changes; each result is a complete immutable packet-path snapshot.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RouteAttributionSource : IDisposable
{
    private readonly object _gate = new();
    private readonly RouteProcessSnapshot _processes = new();
    private readonly RouteProcessTree _tree = new(new([]), []);
    private readonly RouteSocketHistory _sockets = new();
    private RouteProcessEvents? _processEvents;
    private RouteSocketEvents? _socketEvents;
    private long _lastProcessRead;
    private bool _readPackageIdentity;

    public void StartEvents(Action changed)
    {
        lock (_gate)
        {
            _processEvents = new(changed);
            try { _socketEvents = new(changed); }
            catch { _processEvents.Dispose(); _processEvents = null; throw; }
            _lastProcessRead = 0;
        }
    }

    public RouteAttributionSnapshot Read(RoutePolicy policy, bool flushEvents = false)
    {
        lock (_gate)
        {
            var readAt = Environment.TickCount64;
            _tree.SetRules(policy.Matcher, policy.Excluded);
            var updates = new List<RouteProcessInfo>();
            if (_processEvents == null || readAt - _lastProcessRead >= 1000 || policy.Matcher.HasPackages && !_readPackageIdentity)
            {
                updates.AddRange(_processes.Read(policy.Matcher.HasPackages).Select(p => p.Exited == null ? p : p with { ExitedAt = Stopwatch.GetTimestamp() }));
                _lastProcessRead = readAt;
                _readPackageIdentity = policy.Matcher.HasPackages;
            }
            if (_processEvents != null) { updates.AddRange(_processEvents.Drain(flushEvents)); }
            _tree.Update(updates);
            var processes = new RouteProcessDecisions(_tree, _processEvents?.Started ?? 0);
            _sockets.Update(_socketEvents?.Drain() ?? [], Stopwatch.GetTimestamp(), (pid, at) => processes.OwnerAt(pid, at)?.ExitedAt);
            var sockets = _socketEvents == null ? null : _sockets.Snapshot((pid, at) =>
                pid <= 4 || policy.Excluded.Contains(pid) ? RouteDecision.Unselected :
                processes.At(pid, at));
            var tcp = new List<RouteOwnerTable.Row>();
            var udp = new List<RouteOwnerTable.Row>();
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            {
                if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) { continue; }
                tcp.AddRange(RouteOwnerTable.Read(6, family));
                udp.AddRange(RouteOwnerTable.Read(17, family));
            }
            return new(tcp, udp, pid => pid <= 4 || policy.Excluded.Contains(pid)
                ? RouteDecision.Unselected : processes.Current(pid), readAt, sockets);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _socketEvents?.Dispose();
            _processEvents?.Dispose();
            _processes.Dispose();
        }
    }
}

/// <summary>Generation-aware process decisions, computed off the packet path.</summary>
internal sealed class RouteProcessDecisions
{
    private readonly Dictionary<int, RouteProcessInfo[]> _byPid;
    private readonly Dictionary<RouteProcessKey, RouteDecision> _decisions;
    private readonly long _observedSince;

    public RouteProcessDecisions(RouteProcessTree tree, long observedSince)
    {
        _observedSince = observedSince;
        var processes = tree.Processes.ToArray();
        _byPid = processes.GroupBy(p => p.Key.Pid).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Key.Started).ToArray());
        _decisions = processes.ToDictionary(p => p.Key, p => tree.Decide(p.Key, observedSince));
    }

    public RouteProcessInfo? OwnerAt(int pid, long timestamp)
    {
        if (!_byPid.TryGetValue(pid, out var candidates)) { return null; }
        // A new generation seen only by polling has no verified QPC lifetime yet.
        if (_observedSince != 0 && candidates.Any(p => p.Key.Started >= _observedSince && p.StartedAt == 0)) { return null; }
        return candidates.FirstOrDefault(p => p.StartedAt <= timestamp && (p.ExitedAt == null || p.ExitedAt >= timestamp));
    }

    public RouteDecision At(int pid, long timestamp) => OwnerAt(pid, timestamp) is { } owner ? _decisions[owner.Key] : RouteDecision.Unresolved;
    public RouteDecision Current(int pid) => _byPid.TryGetValue(pid, out var owners) && owners[0].Exited == null
        ? _decisions[owners[0].Key] : RouteDecision.Unresolved;
}
