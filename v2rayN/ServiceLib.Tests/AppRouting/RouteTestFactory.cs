using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

internal static class RouteTestFactory
{
    public static RouteTarget Target(int port = 1, string username = "", string password = "") =>
        new(Guid.NewGuid().ToString("N"), _ => Task.FromResult(new RouteSocksEndpoint(port, username, password)));

    public static RouteSharedPolicy Policy(params RulesItem[] rules) => new(
        new(new RoutingItem { RuleSet = JsonUtils.Serialize(rules) }), (_, _) => Task.FromResult(new RouteSocksEndpoint(1)));

    public static (RouteSharedPolicy Policy, RouteTarget Target) Process(string name, bool children = true) =>
        Application(RoutingSelector.Process, name, children);

    public static (RouteSharedPolicy Policy, RouteTarget Target) Package(string family, bool children = false) =>
        Application(RoutingSelector.WindowsApp, family, children);

    private static (RouteSharedPolicy Policy, RouteTarget Target) Application(RoutingSelector selector, string value, bool children)
    {
        var policy = Policy(new RulesItem { OutboundTag = Global.ProxyTag, Blocks = new() { Filters = [new()
        { Selector = selector, Applications = [new() { Value = value, Mode = RoutingProcessMode.Name, IncludeChildren = children }] }] } });
        var process = new RouteProcessInfo(new(10, 1), 0, selector == RoutingSelector.Process ? value : "app.exe", null,
            PackageFamily: selector == RoutingSelector.WindowsApp ? value : "");
        return (policy, policy.Select([process])!);
    }
}
