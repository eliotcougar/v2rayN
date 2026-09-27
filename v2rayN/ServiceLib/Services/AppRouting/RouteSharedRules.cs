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
            var blocks = rule.Blocks ?? LegacyBlocks(rule);
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

    internal static bool IsApplication(RoutingFilter filter) => filter.Selector is RoutingSelector.Process or RoutingSelector.WindowsApp;

    private static RoutingRuleBlocks LegacyBlocks(RulesItem rule)
    {
        var filters = new List<RoutingFilter>();
        void Add(RoutingSelector selector, List<string>? values)
        {
            if (values?.Count > 0) { filters.Add(new() { Selector = selector, Values = values }); }
        }
        Add(RoutingSelector.Domain, rule.Domain);
        Add(RoutingSelector.IP, rule.Ip);
        Add(RoutingSelector.Process, rule.Process);
        Add(RoutingSelector.Port, rule.Port?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
        Add(RoutingSelector.Protocol, rule.Protocol);
        Add(RoutingSelector.InboundTag, rule.InboundTag);
        Add(RoutingSelector.Network, rule.Network?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
        return new() { Filters = filters };
    }
}

/// <summary>Evaluated with process generations and ancestry on the observer thread.
/// Packet lookup only sees the resulting immutable target. Endpoint preparation runs at connection setup.</summary>
internal sealed class RouteSharedPolicy(RouteSharedRules rules, Func<IReadOnlyList<string>, AppRouteRule?, CancellationToken, Task<RouteSocksEndpoint>> prepare)
{
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, AppRouteRule> _targets = new();
    private readonly RoutingFilter[] _filters = rules.Branches.Where(b => b.AcceptsInbound).SelectMany(b => b.Applications).Distinct().ToArray();
    public bool HasPackages => _filters.Any(f => f.Selector == RoutingSelector.WindowsApp && Rows(f).Any());
    public bool PackagesIncludeChildren => _filters.Any(f => f.Selector == RoutingSelector.WindowsApp && Rows(f).Any(r => r.IncludeChildren));
    public bool IncludesChildren => _filters.Any(f => Rows(f).Any(r => r.IncludeChildren));

    private static IEnumerable<RoutingApplicationRow> Rows(RoutingFilter filter) =>
        (filter.Applications ?? filter.Values.Select(v => RoutingApplicationRow.FromValue(v, filter.Selector)).ToList()).Where(r => r.Enabled);

    public bool NeedsPath(string name) => _filters.Where(f => f.Selector == RoutingSelector.Process).Any(f => Rows(f).Any(r =>
        r.Mode == RoutingProcessMode.Folder || r.Mode == RoutingProcessMode.FullPath &&
        string.Equals(Path.GetFileName(r.Value), name, StringComparison.OrdinalIgnoreCase)));

    internal static bool Matches(RoutingFilter filter, IReadOnlyList<RouteProcessInfo> lineage) => Rows(filter).Any(row =>
        lineage.Where((_, index) => index == 0 || row.IncludeChildren).Any(process => Matches(row, filter.Selector, process)));

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

    public AppRouteRule? Select(IReadOnlyList<RouteProcessInfo> lineage, AppRouteRule? fallback)
    {
        var matches = _filters.ToDictionary(f => f, f => Matches(f, lineage));
        var onlyPorts = fallback == null && !matches.Values.Any(v => v);
        if (onlyPorts && rules.Ports == null) { return null; }
        var markers = rules.Branches.Where(b => b.AcceptsInbound && b.Applications.All(f => matches[f])).Select(b => b.Marker).ToArray();
        var key = _generation + ":" + string.Join(',', markers) + ":" + fallback?.Id;
        return _targets.GetOrAdd(key, _ => new() { Id = key, Kind = AppRouteKind.Profile,
            CapturePorts = onlyPorts ? rules.Ports : null,
            ResolveEndpoint = token => prepare(markers, fallback, token) });
    }

    public bool Retains(string id, string signature) => _targets.TryGetValue(id, out var rule) && JsonUtils.Serialize(rule) == signature;
}
