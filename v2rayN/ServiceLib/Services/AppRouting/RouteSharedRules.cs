namespace ServiceLib.Services.AppRouting;

/// <summary>Splits application identity from native Xray predicates without changing rule order.
/// Each conjunction gets a private inbound marker; it is instantiated only for matching identities.</summary>
internal sealed class RouteSharedRules
{
    internal const string InboundTag = "app-routing";
    internal sealed record Branch(string Marker, IReadOnlyList<RoutingFilter> Applications, bool AcceptsInbound, bool CapturesPorts);
    public IReadOnlyList<Branch> Branches { get; }
    public RoutingItem Projection { get; }
    public bool HasApplications => Branches.Any(b => b.AcceptsInbound && b.Applications.Any(f => f.EffectiveValues().Count > 0));
    public RoutePortCapture? Ports { get; }
    public bool HasCaptureSelectors => HasApplications || Ports != null;

    public RouteSharedRules(RoutingItem? routing)
    {
        var branches = new List<Branch>();
        var projected = new List<RulesItem>();
        foreach (var rule in RoutingBlockRules.Read(routing?.RuleSet).Where(r => r.IsEnabled && r.RuleType != ERuleType.DNS))
        {
            var blocks = rule.Blocks ?? RoutingBlockRules.FromLegacy(rule);
            var capturesPorts = false;
            if (blocks.Filters.Any(f => f.Selector == RoutingSelector.Port)
                && blocks.Filters.All(f => f.Selector is RoutingSelector.Port or RoutingSelector.Network))
            {
                var ports = Ports ?? new RoutePortCapture();
                if (ports.Add(blocks)) { Ports = ports; capturesPorts = true; }
            }
            foreach (var group in RoutingBlockRules.Conjunctions(blocks))
            {
                if (group.Count == 0 || group.Any(f => f.Selector is RoutingSelector.Domain or RoutingSelector.IP
                    && f.Values.All(v => v.StartsWith('#')))) { continue; }
                var marker = $"app-rule-{branches.Count}";
                var inbound = group.FirstOrDefault(f => f.Selector == RoutingSelector.InboundTag);
                branches.Add(new(marker, group.Where(IsApplication).ToArray(), inbound == null || inbound.Values.Contains(InboundTag), capturesPorts));
                var filters = group.Where(f => !IsApplication(f) && f.Selector != RoutingSelector.InboundTag)
                    .Select(f => new RoutingFilter { Selector = f.Selector, Values = f.Values }).ToList();
                filters.Add(new() { Selector = RoutingSelector.InboundTag, Values = [marker] });
                projected.Add(new() { OutboundTag = rule.OutboundTag, RuleType = ERuleType.Routing,
                    Blocks = new() { Filters = filters } });
            }
        }
        Branches = branches;
        Projection = new() { DomainStrategy = routing?.DomainStrategy!, RuleSet = JsonUtils.Serialize(projected) };
    }

    internal static bool IsApplication(RoutingFilter filter) => filter.Selector is RoutingSelector.Process or RoutingSelector.WindowsApp or RoutingSelector.Service;

}

/// <summary>Evaluated with process generations and ancestry on the observer thread.
/// Packet lookup only sees the resulting immutable target. Endpoint preparation runs at connection setup.</summary>
internal sealed class RouteSharedPolicy(RouteSharedRules rules, Func<IReadOnlyList<string>, CancellationToken, Task<RouteSocksEndpoint>> prepare)
{
    private readonly ConcurrentDictionary<string, RouteTarget> _targets = new();
    private readonly RoutingFilter[] _filters = rules.Branches.Where(b => b.AcceptsInbound).SelectMany(b => b.Applications).Distinct().ToArray();
    public bool HasPackages => _filters.Any(f => f.Selector == RoutingSelector.WindowsApp && Rows(f).Any());
    public bool PackagesIncludeChildren => _filters.Any(f => f.Selector == RoutingSelector.WindowsApp && Rows(f).Any(r => r.IncludeChildren));
    public IReadOnlySet<string> ServiceNames { get; } = rules.Branches.Where(b => b.AcceptsInbound).SelectMany(b => b.Applications)
        .Where(f => f.Selector == RoutingSelector.Service)
        .SelectMany(f => Rows(f).Select(r => r.MatchValue(RoutingSelector.Service)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public bool HasServices => ServiceNames.Count != 0;

    private static IEnumerable<RoutingApplicationRow> Rows(RoutingFilter filter) =>
        (filter.Applications ?? filter.Values.Select(v => RoutingApplicationRow.FromValue(v, filter.Selector)).ToList()).Where(r => r.Enabled);

    public bool NeedsPath(string name, bool ancestor = false) => _filters.Where(f => f.Selector == RoutingSelector.Process).Any(f => Rows(f).Any(r =>
        (!ancestor || r.IncludeChildren) && (r.Mode == RoutingProcessMode.Folder || r.Mode == RoutingProcessMode.FullPath &&
        string.Equals(Path.GetFileName(r.Value), name, StringComparison.OrdinalIgnoreCase))));

    internal static bool Matches(RoutingFilter filter, IReadOnlyList<RouteProcessInfo> lineage, string? serviceName = null) =>
        filter.Selector == RoutingSelector.Service
            ? serviceName != null && Rows(filter).Any(row => string.Equals(row.MatchValue(RoutingSelector.Service), serviceName, StringComparison.OrdinalIgnoreCase))
            : Rows(filter).Any(row => lineage.Where((_, index) => index == 0 || row.IncludeChildren)
                .Any(process => Matches(row, filter.Selector, process)));

    private static bool Matches(RoutingApplicationRow row, RoutingSelector selector, RouteProcessInfo process)
    {
        var value = row.MatchValue(selector);
        if (selector == RoutingSelector.WindowsApp) { return string.Equals(value, process.PackageFamily, StringComparison.OrdinalIgnoreCase); }
        var path = process.Path?.Replace('\\', '/');
        return row.Mode switch
        {
            RoutingProcessMode.Folder => path?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true,
            RoutingProcessMode.FullPath => string.Equals(value, path, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(Path.GetFileNameWithoutExtension(value), Path.GetFileNameWithoutExtension(process.Name), StringComparison.OrdinalIgnoreCase),
        };
    }

    public RouteTarget? Select(IReadOnlyList<RouteProcessInfo> lineage, string? serviceName = null)
    {
        var matches = _filters.ToDictionary(f => f, f => Matches(f, lineage, serviceName));
        var onlyPorts = !matches.Values.Any(v => v);
        if (onlyPorts && rules.Ports == null) { return null; }
        var markers = rules.Branches.Where(b => b.AcceptsInbound && b.Applications.All(f => matches[f])).Select(b => b.Marker).ToArray();
        var key = string.Join(',', markers);
        return _targets.GetOrAdd(key, _ => new(key, token => prepare(markers, token), onlyPorts ? rules.Ports : null));
    }

    public bool Retains(RouteTarget target) => _targets.TryGetValue(target.Id, out var current) && ReferenceEquals(current, target);
}
