using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class ServiceRuleTests
{
    private static RouteSharedPolicy Policy(string name = "Dnscache") => RouteTestFactory.Policy(new RulesItem
    {
        OutboundTag = Global.ProxyTag,
        Blocks = new() { Filters = [new() { Selector = RoutingSelector.Service,
            Applications = [RoutingApplicationRow.FromValue(name, RoutingSelector.Service)] }] }
    });

    private static RouteProcessInfo Process(int pid, long generation = 1) =>
        new(new(pid, generation), 0, "svchost.exe", "C:/Windows/System32/svchost.exe", StartedAt: 100);

    [Test]
    public async Task ServiceNameSelectsItsHostWithoutMatchingAnUnrelatedChild()
    {
        var policy = Policy();
        var tree = new RouteProcessTree(policy, []);
        var host = Process(20);
        var child = new RouteProcessInfo(new(30, 2), 20, "worker.exe", "C:/Apps/worker.exe", StartedAt: 200);
        tree.Update([host, child]);
        await policy.ServiceNames.Contains("dnscache").Should().BeTrue();
        await tree.Decide(host.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await tree.Decide(host.Key, serviceName: "Dnscache").Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await tree.Decide(child.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    public async Task SharedHostRequiresExactModuleNameAndCurrentProcessGeneration()
    {
        var policy = Policy();
        var tree = new RouteProcessTree(policy, []);
        var host = Process(20);
        tree.Update([host]);
        var catalog = new[] { new RouteServiceInfo("Dnscache", "DNS Client", 20),
            new RouteServiceInfo("OtherSvc", "Other", 20) };
        var services = new RouteServiceSnapshot(catalog, tree.Processes, policy.ServiceNames);
        var index = new RouteProcessDecisions(tree, 0, services);
        await services.NeedsModule(20).Should().BeTrue();
        await index.Current(20).ServicePending.Should().BeTrue();
        await index.Current(20, "Dnscache").Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await index.Current(20, "OtherSvc").Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await index.Current(20, "svchost.exe").Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);

        var recycled = new RouteServiceSnapshot(catalog, [Process(20, 2)], policy.ServiceNames);
        await recycled.Resolve(20, host.Key, "Dnscache").Should().BeNull();
    }

    [Test]
    public async Task UnknownSharedServiceDoesNotFallThroughToACompetingProcessRule()
    {
        var service = new RulesItem { OutboundTag = Global.DirectTag,
            Blocks = new() { Filters = [new() { Selector = RoutingSelector.Service,
                Applications = [RoutingApplicationRow.FromValue("Dnscache", RoutingSelector.Service)] }] } };
        var process = new RulesItem { OutboundTag = Global.ProxyTag,
            Blocks = new() { Filters = [new() { Selector = RoutingSelector.Process,
                Applications = [RoutingApplicationRow.FromValue("svchost.exe", RoutingSelector.Process)] }] } };
        var policy = RouteTestFactory.Policy(service, process);
        var tree = new RouteProcessTree(policy, []);
        tree.Update([Process(20)]);
        var services = new RouteServiceSnapshot([new("Dnscache", "DNS Client", 20), new("OtherSvc", "Other", 20)],
            tree.Processes, policy.ServiceNames);
        var index = new RouteProcessDecisions(tree, 0, services);
        await tree.Decide(new(20, 1)).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await index.Current(20).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        await index.Current(20, "Dnscache").Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await index.Current(20, "OtherSvc").Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await (index.Current(20, "Dnscache").Rule == index.Current(20, "OtherSvc").Rule).Should().BeFalse();
    }

    [Test]
    public async Task OwnerModuleRowCanResolvePendingSocketOnlyForSameOwner()
    {
        var policy = Policy();
        var tree = new RouteProcessTree(policy, []);
        tree.Update([Process(20)]);
        var services = new RouteServiceSnapshot([new("Dnscache", "DNS Client", 20), new("OtherSvc", "Other", 20)],
            tree.Processes, policy.ServiceNames);
        var decisions = new RouteProcessDecisions(tree, 0, services);
        var flow = new RouteFlow(6, IPAddress.Parse("192.0.2.10"), 45678, IPAddress.Parse("198.51.100.10"), 443);
        var history = new RouteSocketHistory();
        history.Update([new RouteSocketEvent(4, 100, 1, 20, flow)], 200);
        var sockets = history.Snapshot((pid, at) => decisions.At(pid, at));
        var matching = new RouteAttributionSnapshot([new(flow.LocalAddress, flow.LocalPort, flow.RemoteAddress,
            flow.RemotePort, 20, "Dnscache")], [], decisions.Current, 200, sockets);
        await matching.Find(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        var differentPid = new RouteAttributionSnapshot([new(flow.LocalAddress, flow.LocalPort, flow.RemoteAddress,
            flow.RemotePort, 30, "Dnscache")], [], (pid, module) =>
            pid == 30 ? new(RouteDecisionKind.Selected, new(30, 1), policy.Select([Process(30)], module)) : decisions.Current(pid, module), 200, sockets);
        await differentPid.Find(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }
}
