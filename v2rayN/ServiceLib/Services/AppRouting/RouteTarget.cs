namespace ServiceLib.Services.AppRouting;

/// <summary>An immutable target shared by processes with the same routing-table matches.
/// Identity is tied to its owning core: replacement retires all of that core's targets.</summary>
internal sealed class RouteTarget(string id, Func<CancellationToken, Task<RouteSocksEndpoint>> resolveEndpoint,
    RoutePortCapture? capturePorts = null)
{
    public string Id { get; } = id;
    public Func<CancellationToken, Task<RouteSocksEndpoint>> ResolveEndpoint { get; } = resolveEndpoint;
    public RoutePortCapture? CapturePorts { get; } = capturePorts;
}
