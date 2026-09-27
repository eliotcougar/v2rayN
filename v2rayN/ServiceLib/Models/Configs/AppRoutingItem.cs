namespace ServiceLib.Models.Configs;

// Keep the persisted values stable when adding choices to the editor.
public enum AppRouteKind
{
    Profile = 0, ActiveProfile = 3, Direct = 4, Block = 5 // 1 and 2 were retired preview destinations.
}

public enum AppRouteMatchKind
{
    Process = 0, WindowsApp = 1
}

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

public class AppRouteRule
{
    // Runtime-only endpoint preparation for a shared-table match, never persisted.
    internal Func<CancellationToken, Task<ServiceLib.Services.AppRouting.RouteSocksEndpoint>>? ResolveEndpoint { get; init; }
    internal ServiceLib.Services.AppRouting.RouteSocksEndpoint? ProxyEndpoint { get; set; }
    internal ServiceLib.Services.AppRouting.RoutePortCapture? CapturePorts { get; init; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public AppRouteMatchKind MatchKind { get; set; }
    public string Name { get; set; } = "";
    public List<string> PackageFamilies { get; set; } = [];
    public string ExecutablePath { get; set; } = "";
    public bool MatchByName
    {
        get; set;
    }
    public bool IncludeChildProcesses
    {
        get; set;
    }
    public AppRouteKind Kind
    {
        get; set;
    }
    public string ProfileId { get; set; } = "";
    public bool ApplyBlockingRules { get; set; }
}
