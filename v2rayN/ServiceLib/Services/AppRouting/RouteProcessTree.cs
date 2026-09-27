namespace ServiceLib.Services.AppRouting;

internal readonly record struct RouteProcessKey(int Pid, long Started);
internal sealed record RouteProcessInfo(RouteProcessKey Key, int ParentPid, string Name, string? Path, long? Exited = null,
    ulong Sequence = 0, ulong ParentSequence = 0, long StartedAt = 0, long? ExitedAt = null, string? PackageFamily = null);

/// <summary>Process identities include creation time; an inherited route never follows a recycled PID.</summary>
internal sealed class RouteProcessTree(AppRouteMatcher rules, IEnumerable<int> excludedProcesses)
{
    private readonly HashSet<int> _excluded = excludedProcesses.ToHashSet();
    private readonly Dictionary<RouteProcessKey, Node> _nodes = [];

    private sealed class Node(RouteProcessInfo info)
    {
        public RouteProcessInfo Info = info;
        public Node? Parent;
    }

    public bool Contains(RouteProcessKey key) => _nodes.ContainsKey(key);
    public IEnumerable<RouteProcessInfo> Processes => _nodes.Values.Select(n => n.Info);

    public void SetRules(AppRouteMatcher updated, IEnumerable<int> excluded)
    {
        rules = updated;
        _excluded.Clear();
        _excluded.UnionWith(excluded);
    }

    public void Update(IEnumerable<RouteProcessInfo> snapshot)
    {
        foreach (var info in snapshot)
        {
            if (_nodes.TryGetValue(info.Key, out var node))
            {
                // A delayed snapshot/start must not resurrect a stopped generation.
                // Stop records intentionally contain only identity and exit metadata.
                var previous = node.Info;
                node.Info = info with
                {
                    ParentPid = info.Name.Length == 0 ? previous.ParentPid : info.ParentPid,
                    Name = info.Name.Length == 0 ? previous.Name : info.Name,
                    Path = info.Path ?? previous.Path,
                    // Package identity belongs to this generation. A sparse event or
                    // snapshot must not erase a family already obtained from Windows.
                    PackageFamily = string.IsNullOrEmpty(info.PackageFamily) ? previous.PackageFamily ?? info.PackageFamily : info.PackageFamily,
                    Exited = previous.Exited ?? info.Exited,
                    Sequence = info.Sequence == 0 ? previous.Sequence : info.Sequence,
                    ParentSequence = info.ParentSequence == 0 ? previous.ParentSequence : info.ParentSequence,
                    StartedAt = info.StartedAt == 0 ? previous.StartedAt : info.StartedAt,
                    ExitedAt = previous.ExitedAt == null ? info.ExitedAt : info.ExitedAt == null ? previous.ExitedAt : Math.Min(previous.ExitedAt.Value, info.ExitedAt.Value)
                };
            }
            else
            {
                _nodes.Add(info.Key, new(info));
            }
        }
        var byPid = _nodes.Values.GroupBy(n => n.Info.Key.Pid).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var node in _nodes.Values)
        {
            if (node.Parent?.Info.Exited < node.Info.Key.Started || node.Parent != null &&
                node.Info.ParentSequence != 0 && node.Parent.Info.Sequence != 0 && node.Parent.Info.Sequence != node.Info.ParentSequence)
            {
                node.Parent = null;
            }

            if (node.Parent != null)
            {
                continue;
            }

            if (!byPid.TryGetValue(node.Info.ParentPid, out var parents))
            {
                continue;
            }

            node.Parent = parents.Where(p => p != node && p.Info.Key.Started <= node.Info.Key.Started &&
                    (node.Info.ParentSequence == 0 || p.Info.Sequence == 0 || p.Info.Sequence == node.Info.ParentSequence) &&
                    (p.Info.Exited == null || p.Info.Exited >= node.Info.Key.Started))
                .MaxBy(p => p.Info.Key.Started);
        }

        // Keep live ancestry and recent exits for delayed process/socket events.
        // Overflow must stop observation, rather than silently erase a needed launcher.
        var recent = DateTime.UtcNow.AddSeconds(-10).ToFileTimeUtc();
        var retained = new HashSet<RouteProcessKey>();
        foreach (var node in _nodes.Values.Where(n => n.Info.Exited == null)
            .Concat(_nodes.Values.Where(n => n.Info.Exited >= recent))
            .Concat(_nodes.Values.Where(n => n.Info.Exited != null).OrderByDescending(n => n.Info.Exited).Take(2048)))
        {
            for (var ancestor = node; ancestor != null && retained.Add(ancestor.Info.Key); ancestor = ancestor.Parent)
            {
            }
        }

        foreach (var key in _nodes.Keys.Where(key => !retained.Contains(key)).ToList())
        {
            _nodes.Remove(key);
        }
        if (_nodes.Count > 65536) { throw new IOException("Application-routing process history exceeded its capacity."); }
    }

    public AppRouteRule? Find(RouteProcessKey key) => Decide(key).Rule;

    public RouteDecision Decide(RouteProcessKey key, long observedSince = 0)
    {
        if (!_nodes.TryGetValue(key, out var node))
        {
            return RouteDecision.Unresolved;
        }

        var lineage = new List<RouteProcessInfo>();
        var seen = new HashSet<RouteProcessKey>();
        AppRouteRule? match = null;
        var incomplete = false;
        for (var ancestor = node; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!seen.Add(ancestor.Info.Key))
            {
                return RouteDecision.Unresolved;
            }

            if (_excluded.Contains(ancestor.Info.Key.Pid) || AppRoutingManager.IsProtectedExecutable(ancestor.Info.Name))
            {
                return RouteDecision.Unselected;
            }

            incomplete |= ancestor.Info.Name.Length == 0 || ancestor.Info.Path == null && rules.NeedsPath(ancestor.Info.Name);
            if (observedSince != 0 && ancestor.Info.Key.Started >= observedSince)
            {
                incomplete |= ancestor.Info.StartedAt == 0 || ancestor.Parent == null && ancestor.Info.ParentPid > 4;
            }

            lineage.Add(ancestor.Info);
            incomplete |= ancestor.Info.PackageFamily == null &&
                (ancestor == node ? rules.Shared?.HasPackages == true : rules.Shared?.PackagesIncludeChildren == true);
            var rule = rules.Find(ancestor.Info.Path, ancestor.Info.Name, ancestor.Info.PackageFamily);
            // Unknown package identity matters only while a package rule could change
            // the winner. An explicit process rule already takes precedence.
            incomplete |= match == null && rule == null && ancestor.Info.PackageFamily == null &&
                (ancestor == node ? rules.HasPackages : rules.PackagesIncludeChildren);
            if (match == null && rule != null && (ancestor == node || rule.IncludeChildProcesses))
            {
                match = rule;
            }
        }
        if (incomplete) { return RouteDecision.Unresolved; }
        match = rules.Shared?.Select(lineage, match) ?? match;
        return new(match == null ? RouteDecisionKind.Unselected : RouteDecisionKind.Selected, key, match);
    }
}
