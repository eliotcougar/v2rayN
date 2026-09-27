using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class PortRoutingTests
{
    private static RulesItem Ports(string ports = "123", string? network = null)
    {
        var rule = new RulesItem { OutboundTag = Global.DirectTag, Blocks = new()
        { Filters = [new() { Selector = RoutingSelector.Port, Values = ports.Split(',').ToList() }] } };
        if (network != null) { rule.Blocks.Filters.Add(new() { Selector = RoutingSelector.Network, Values = [network] }); }
        return rule;
    }

    private static RouteSharedRules Rules(params RulesItem[] rules) => new(new() { RuleSet = JsonUtils.Serialize(rules) });
    private static RouteProcessInfo Process(int pid = 20, string name = "client.exe", int parent = 0) =>
        new(new(pid, pid), parent, name, "C:/Apps/" + name, PackageFamily: "");
    private static RouteSharedPolicy Policy(RouteSharedRules rules) => new(rules, (_, _) => Task.FromResult(new RouteSocksEndpoint(12345)));
    private static RouteDecision Decision(RouteSharedPolicy policy, int pid = 20) =>
        new(RouteDecisionKind.Selected, Process(pid).Key, policy.Select([Process(pid)]));

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DestinationPortsAndRangesCaptureAnyAppForBothTransportsAndAddressFamilies(bool ipv6)
    {
        var rules = Rules(Ports("0,123,443,8000-8080,65535"));
        await rules.HasCaptureSelectors.Should().BeTrue();
        await rules.HasApplications.Should().BeFalse();
        var selected = Decision(Policy(rules));
        foreach (byte protocol in new byte[] { 6, 17 })
        {
            foreach (ushort port in new ushort[] { 0, 123, 443, 8000, 8080, 65535 })
            { await selected.ForFlow(PacketTests.Flow(ipv6) with { Protocol = protocol, RemotePort = port }).Kind.Should().BeEqualTo(RouteDecisionKind.Selected); }
            foreach (ushort port in new ushort[] { 1, 122, 444, 7999, 8081, 65534 })
            { await selected.ForFlow(PacketTests.Flow(ipv6) with { Protocol = protocol, LocalPort = 123, RemotePort = port }).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected); }
        }
    }

    [Test]
    [Arguments("tcp", 6)]
    [Arguments("udp", 17)]
    public async Task NetworkConstrainsCaptureWithoutChangingOtherPorts(string network, int protocol)
    {
        var selected = Decision(Policy(Rules(Ports("123", network))));
        var flow = PacketTests.Flow(false) with { RemotePort = 123, Protocol = (byte)protocol };
        await selected.ForFlow(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await selected.ForFlow(flow with { Protocol = (byte)(protocol == 6 ? 17 : 6) }).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    [Arguments(RoutingSelector.Domain, "example.com")]
    [Arguments(RoutingSelector.IP, "192.0.2.1")]
    [Arguments(RoutingSelector.Process, "other.exe")]
    [Arguments(RoutingSelector.WindowsApp, "Family_test")]
    [Arguments(RoutingSelector.Protocol, "http")]
    [Arguments(RoutingSelector.InboundTag, "app-routing")]
    public async Task AdditionalSelectorsNeverBroadenPortCapture(RoutingSelector selector, string value)
    {
        var rule = Ports();
        rule.Blocks!.Filters.Add(new() { Selector = selector, Values = [value] });
        var rules = Rules(rule);
        await rules.Ports.Should().BeNull();
        await Policy(rules).Select([Process()]).Should().BeNull();
    }

    [Test]
    [Arguments("0-65535")]
    [Arguments("1-65535")]
    [Arguments("32768-65535,0-32767")]
    [Arguments("1-40000,30000-65535")]
    [Arguments("0,1-65534,65535")]
    public async Task FullRangeFallbackNeverOptsOtherAppsIntoCaptureButRemainsInNativeRuleOrder(string ports)
    {
        foreach (var network in new string?[] { null, "tcp", "udp" })
        {
            var catchAll = Ports(ports, network);
            var rules = Rules(catchAll);
            await rules.HasCaptureSelectors.Should().BeFalse();
            await Policy(rules).Select([Process()]).Should().BeNull();
            var combined = Rules(Ports("123", "udp"), catchAll);
            var selected = Decision(Policy(combined));
            await selected.ForFlow(PacketTests.Flow(false) with { Protocol = 6, RemotePort = 443 }).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
            await selected.ForFlow(PacketTests.Flow(false) with { Protocol = 17, RemotePort = 123 }).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
            await combined.Branches.Count.Should().BeEqualTo(2);
            var native = RoutingBlockRules.Read(combined.Projection.RuleSet);
            await RoutingBlockRules.Values(native[^1], RoutingSelector.Port).Should().BeEquivalentTo(ports.Split(','));
        }
    }

    [Test]
    public async Task LegacyPortSyntaxAndDisabledRulesKeepTheirMeaning()
    {
        var selected = Rules(new RulesItem { Port = "123, 443", Network = "", OutboundTag = Global.DirectTag });
        await selected.HasCaptureSelectors.Should().BeTrue();
        await Rules(new RulesItem { Port = "0-65535", OutboundTag = Global.ProxyTag }).HasCaptureSelectors.Should().BeFalse();
        var disabled = Ports();
        disabled.Blocks!.Enabled = false;
        await Rules(disabled).HasCaptureSelectors.Should().BeFalse();
        await Rules(new RulesItem { Network = "udp", OutboundTag = Global.DirectTag }).HasCaptureSelectors.Should().BeFalse();
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => Rules(new RulesItem { Port = "70000" })));
    }

    [Test]
    public async Task ProtectedAndExcludedProcessesAndTheirChildrenRemainOutsideCapture()
    {
        var policy = Policy(Rules(Ports()));
        var tree = new RouteProcessTree(policy, [40]);
        var processes = new[] { Process(), Process(30, "xray.exe"), Process(40), Process(50, parent: 30), Process(60, parent: 40) };
        tree.Update(processes);
        var flow = PacketTests.Flow(false) with { RemotePort = 123 };
        await tree.Decide(processes[0].Key).ForFlow(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        foreach (var process in processes.Skip(1))
        { await tree.Decide(process.Key).ForFlow(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected); }
    }

    [Test]
    public async Task ApplicationSelectionsCaptureTheirOtherPortsWhilePortOnlyTargetsStayRestricted()
    {
        var process = new RulesItem { Process = ["client.exe"], OutboundTag = Global.ProxyTag };
        var policy = Policy(Rules(Ports(), process));
        var flow = PacketTests.Flow(false) with { RemotePort = 443 };
        await Decision(policy).ForFlow(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        var rule = policy.Select([Process(name: "other.exe")]);
        await new RouteDecision(RouteDecisionKind.Selected, Process().Key, rule).ForFlow(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task SharedUdpOwnersAreAmbiguousOnlyForSelectedDestinationPorts(bool ipv6, bool wildcard)
    {
        var policy = Policy(Rules(Ports("123", "udp")));
        var flow = PacketTests.Flow(ipv6) with { Protocol = 17, RemotePort = 123 };
        var bind = flow with { RemoteAddress = ipv6 ? IPAddress.IPv6Any : IPAddress.Any, RemotePort = 0 };
        var other = wildcard ? bind with { LocalAddress = ipv6 ? IPAddress.IPv6Any : IPAddress.Any } : bind;
        var history = new RouteSocketHistory();
        history.Update([new(3, 100, 1, 20, bind), new(3, 100, 2, 30, other)], 200);
        var events = history.Snapshot((pid, _) => Decision(policy, pid));
        var snapshot = new RouteAttributionSnapshot([], [new(bind.LocalAddress, bind.LocalPort, null, 0, 20), new(other.LocalAddress, other.LocalPort, null, 0, 30)],
            pid => Decision(policy, pid), 200, events);
        await snapshot.Find(flow, 150).Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
        await snapshot.Find(flow with { RemotePort = 443 }, 150).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await events.Find(flow with { RemotePort = 443 }, 150)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    [Arguments(6)]
    [Arguments(17)]
    public async Task SocketAndOwnerTableAgreeOnSelectionAndPreserveEndpointIdentity(int protocol)
    {
        var policy = Policy(Rules(Ports("123")));
        var flow = PacketTests.Flow(false) with { Protocol = (byte)protocol, RemotePort = 123 };
        var ignored = flow with { RemotePort = 443 };
        var history = new RouteSocketHistory();
        history.Update(protocol == 6 ? [new(4, 100, 1, 20, flow), new(4, 100, 2, 20, ignored)]
            : [new(3, 100, 1, 20, flow with { RemotePort = 0, RemoteAddress = IPAddress.Any })], 200);
        var snapshot = new RouteAttributionSnapshot(protocol == 6
                ? [new(flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, 123, 20), new(flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, 443, 20)] : [],
            protocol == 17 ? [new(flow.LocalAddress, flow.LocalPort, null, 0, 20)] : [],
            pid => Decision(policy, pid), 200, history.Snapshot((pid, _) => Decision(policy, pid)));
        await snapshot.Find(flow, 150).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await snapshot.Find(flow, 150).Endpoint.Should().BeEqualTo(1UL);
        await snapshot.Find(ignored, 150).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await snapshot.Find(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
    }

    [Test]
    public async Task AddingInboundConstraintChangesCapturePolicyEvenWhenNativeProjectionIsIdentical()
    {
        var rule = Ports();
        var captured = Rules(rule);
        rule.Blocks!.Filters.Add(new() { Selector = RoutingSelector.InboundTag, Values = ["app-routing"] });
        var constrained = Rules(rule);
        await captured.Projection.RuleSet.Should().BeEqualTo(constrained.Projection.RuleSet);
        await captured.Branches[0].CapturesPorts.Should().BeTrue();
        await constrained.Branches[0].CapturesPorts.Should().BeFalse();
        // Branch metadata participates in the shared-core reuse key in AppRoutingManager.
        await (JsonUtils.Serialize(captured.Branches) == JsonUtils.Serialize(constrained.Branches)).Should().BeFalse();
    }
}
