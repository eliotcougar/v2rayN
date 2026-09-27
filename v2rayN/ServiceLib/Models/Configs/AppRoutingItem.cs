namespace ServiceLib.Models.Configs;

public class AppRoutingItem
{
    public bool Enabled
    {
        get; set;
    }
    // Older preview clients stored standalone Rules here. Unknown JSON fields are
    // ignored: matching now comes exclusively from the ordinary routing table.
    public AppRouteInterfaceOptions InterfaceMonitoring { get; set; } = new();
}

public class AppRouteInterfaceOptions
{
    public bool MonitorNewInterfaces { get; set; } = true;
    public List<AppRouteInterfaceItem> Interfaces { get; set; } = [];
}

public class AppRouteInterfaceItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Monitored { get; set; } = true;
}
