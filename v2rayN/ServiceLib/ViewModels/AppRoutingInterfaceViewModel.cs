using ServiceLib.Services.AppRouting;

namespace ServiceLib.ViewModels;

public partial class AppRoutingInterfaceRow : ReactiveObject
{
    public string Id { get; }
    public string Name { get; }
    public string Details { get; }
    [Reactive] public partial bool Monitored { get; set; }

    internal AppRoutingInterfaceRow(AppRouteInterfaceItem item, RouteInterfaceInfo adapter)
    {
        Id = item.Id;
        Name = item.Name;
        Details = $"{adapter.Description} — {adapter.Status}";
        Monitored = item.Monitored;
    }
}

public partial class AppRoutingInterfaceViewModel : ReactiveObject
{
    public IReadOnlyList<AppRoutingInterfaceRow> Interfaces { get; }
    [Reactive] public partial bool MonitorNewInterfaces { get; set; }

    internal AppRoutingInterfaceViewModel(AppRouteInterfaceOptions options, IReadOnlyList<RouteInterfaceInfo> adapters)
    {
        MonitorNewInterfaces = options.MonitorNewInterfaces;
        // Retain absent adapters' settings, but do not show historical entries
        // (including filter-module IDs remembered by older builds) as extra rows.
        var present = adapters.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        Interfaces = options.Interfaces.Where(i => present.ContainsKey(i.Id))
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new AppRoutingInterfaceRow(i, present[i.Id])).ToArray();
    }

    internal AppRouteInterfaceOptions ToOptions() => new()
    {
        MonitorNewInterfaces = MonitorNewInterfaces,
        Interfaces = Interfaces.Select(i => new AppRouteInterfaceItem { Id = i.Id, Name = i.Name, Monitored = i.Monitored }).ToList()
    };
}
