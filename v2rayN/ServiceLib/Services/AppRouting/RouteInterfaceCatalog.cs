namespace ServiceLib.Services.AppRouting;

internal sealed record RouteInterfaceInfo(string Id, string Name, string Description,
    OperationalStatus Status, uint IPv4Index, uint IPv6Index);

internal static class RouteInterfaceCatalog
{
    public static IReadOnlyList<RouteInterfaceInfo> Read()
    {
        var filters = OperatingSystem.IsWindows()
            ? RouteInterfaceNative.Read().Where(i => i.IsFilter).Select(i => i.Id).ToHashSet() : [];
        return WithoutFilterModules(NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(a =>
            {
                var properties = a.GetIPProperties();
                return new RouteInterfaceInfo(a.Id, a.Name, a.Description, a.OperationalStatus,
                    a.Supports(NetworkInterfaceComponent.IPv4) ? (uint)(properties.GetIPv4Properties()?.Index ?? 0) : 0,
                    a.Supports(NetworkInterfaceComponent.IPv6) ? (uint)(properties.GetIPv6Properties()?.Index ?? 0) : 0);
            }), filters);
    }

    // Filter modules (Npcap, QoS, etc.) belong to an adapter's network stack;
    // they are not separate IP routing choices. Keep real VPN/virtual adapters.
    internal static IReadOnlyList<RouteInterfaceInfo> WithoutFilterModules(IEnumerable<RouteInterfaceInfo> adapters, IReadOnlySet<Guid> filters) =>
        adapters.Where(a => !Guid.TryParse(a.Id, out var id) || !filters.Contains(id)).ToArray();

    // Store the first observed default, rather than reinterpreting old adapters
    // whenever MonitorNewInterfaces changes. Disconnected/removed IDs are retained.
    public static AppRouteInterfaceOptions Discover(AppRouteInterfaceOptions options, IReadOnlyList<RouteInterfaceInfo> adapters)
    {
        var updated = JsonUtils.DeepCopy(options);
        foreach (var adapter in adapters)
        {
            var known = updated.Interfaces.FirstOrDefault(i => string.Equals(i.Id, adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                updated.Interfaces.Add(new() { Id = adapter.Id, Name = adapter.Name, Monitored = options.MonitorNewInterfaces });
            }
            else { known.Name = adapter.Name; }
        }
        return updated;
    }
}

/// <summary>Immutable index lookup; persisted choices use adapter IDs, never transient indexes.</summary>
internal sealed class RouteInterfacePolicy
{
    private readonly Dictionary<(uint Index, bool IPv6), (string Id, bool Monitored)> _interfaces = [];
    private readonly bool _monitorNew;
    public static RouteInterfacePolicy All { get; } = new(new(), []);

    public RouteInterfacePolicy(AppRouteInterfaceOptions options, IReadOnlyList<RouteInterfaceInfo> adapters)
    {
        _monitorNew = options.MonitorNewInterfaces;
        var choices = options.Interfaces.ToDictionary(i => i.Id, i => i.Monitored, StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            var choice = (adapter.Id, choices.GetValueOrDefault(adapter.Id, _monitorNew));
            if (adapter.IPv4Index != 0) { _interfaces[(adapter.IPv4Index, false)] = choice; }
            if (adapter.IPv6Index != 0) { _interfaces[(adapter.IPv6Index, true)] = choice; }
        }
    }

    public bool Monitors(uint index, bool ipv6) => _interfaces.TryGetValue((index, ipv6), out var adapter) ? adapter.Monitored : _monitorNew;

    public bool Retains(RouteInterfacePolicy previous, uint index, bool ipv6) => Monitors(index, ipv6) &&
        string.Equals(_interfaces.GetValueOrDefault((index, ipv6)).Id,
            previous._interfaces.GetValueOrDefault((index, ipv6)).Id, StringComparison.OrdinalIgnoreCase);

    public bool SameAs(RouteInterfacePolicy other) => _monitorNew == other._monitorNew &&
        _interfaces.Count == other._interfaces.Count && _interfaces.All(p => other._interfaces.TryGetValue(p.Key, out var value) && value == p.Value);
}
