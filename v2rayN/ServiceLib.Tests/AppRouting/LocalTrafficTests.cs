using ServiceLib.Services.AppRouting;
using static ServiceLib.Tests.AppRouting.InterfaceTests;

namespace ServiceLib.Tests.AppRouting;

public class LocalTrafficTests
{
    private static RouteInterfaceInfo Lan(uint index = 7, string ip = "192.168.8.12", int prefix = 24) =>
        Adapter("lan", index) with { Addresses = [new(IPAddress.Parse(ip), prefix)] };

    private static RouteFlow Flow(string peer, ushort source = 5353, ushort destination = 5353, byte protocol = 17) =>
        new(protocol, IPAddress.Parse(peer.Contains(':') ? "fd12:3456::12" : "192.168.8.12"), source, IPAddress.Parse(peer), destination);

    private static byte[] Packet(RouteFlow flow) => RoutePacket.CreateUdpReply(flow with
    { LocalAddress = flow.RemoteAddress, LocalPort = flow.RemotePort, RemoteAddress = flow.LocalAddress, RemotePort = flow.LocalPort }, new byte[32]);

    [Test]
    [Arguments("224.0.0.251", true)]
    [Arguments("239.255.255.250", true)]
    [Arguments("223.255.255.255", false)]
    [Arguments("240.0.0.1", false)]
    [Arguments("255.255.255.255", true)]
    [Arguments("169.254.6.8", true)]
    [Arguments("169.253.6.8", false)]
    [Arguments("ff02::fb", true)]
    [Arguments("ff05::1:3", true)]
    [Arguments("fe80::1234%7", true)]
    [Arguments("febf::1", true)]
    [Arguments("fec0::1", false)]
    [Arguments("fd12:3456::99", false)]
    [Arguments("192.168.8.99", false)]
    [Arguments("10.0.0.1", false)]
    [Arguments("203.0.113.1", false)]
    public async Task NativeAddressScopeDoesNotBypassOrdinaryLanOrInternetTraffic(string peer, bool native)
    {
        var policy = new RouteInterfacePolicy(new(), [Lan()]);
        await policy.BypassesLocalTraffic(7, Flow(peer, 50000, 443, 6)).Should().BeEqualTo(native);
        await new RouteInterfacePolicy(new(), [Lan()], false).BypassesLocalTraffic(7, Flow(peer)).Should().BeFalse();
    }

    [Test]
    public async Task BroadcastAndDiscoveryUseTheOutgoingAdapterPrefix()
    {
        var adapters = new[] { Lan(), Lan(8, "10.7.0.9", 16) with { Id = "vpn" } };
        var policy = new RouteInterfacePolicy(new(), adapters);
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.255", 45000, 45001)).Should().BeTrue();
        await policy.BypassesLocalTraffic(8, Flow("192.168.8.255", 45000, 45001)).Should().BeFalse();
        await policy.BypassesLocalTraffic(8, Flow("10.7.255.255", 45000, 45001)).Should().BeTrue();
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.99")).Should().BeTrue();
        await policy.BypassesLocalTraffic(8, Flow("192.168.8.99")).Should().BeFalse();
        await policy.BypassesLocalTraffic(99, Flow("192.168.8.99")).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("192.168.9.99")).Should().BeFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(31)]
    [Arguments(32)]
    public async Task PointToPointAndDefaultPrefixesDoNotCreateABroadcast(int prefix)
    {
        var policy = new RouteInterfacePolicy(new(), [Lan(prefix: prefix)]);
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.13", 45000, 45001)).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.255", 45000, 45001)).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("203.0.113.1")).Should().BeFalse();
    }

    [Test]
    [Arguments((ushort)5353)]
    [Arguments((ushort)5355)]
    [Arguments((ushort)1900)]
    [Arguments((ushort)3702)]
    [Arguments((ushort)137)]
    [Arguments((ushort)138)]
    public async Task DiscoveryQueriesAndUnicastRepliesStayOnLinkOnly(ushort port)
    {
        var policy = new RouteInterfacePolicy(new(), [Lan()]);
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.99", 50000, port)).Should().BeTrue();
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.99", port, 50000)).Should().BeTrue();
        await policy.BypassesLocalTraffic(7, Flow("203.0.113.99", 50000, port)).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("203.0.113.99", port, 50000)).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("192.168.8.99", 50000, port, 6)).Should().BeFalse();
    }

    [Test]
    public async Task IPv6DiscoveryUsesPrefixBitsWithoutAnIpv4OrScopeIdShortcut()
    {
        var policy = new RouteInterfacePolicy(new(), [Lan(ip: "fd12:3456::12", prefix: 63)]);
        await policy.BypassesLocalTraffic(107, Flow("fd12:3456:0:1::99")).Should().BeTrue();
        await policy.BypassesLocalTraffic(107, Flow("fd12:3456:0:2::99")).Should().BeFalse();
        await policy.BypassesLocalTraffic(7, Flow("fd12:3456::99")).Should().BeFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DhcpUnicastRenewalKeepsItsNativePortPair(bool ipv6)
    {
        var flow = Flow(ipv6 ? "2001:db8::99" : "203.0.113.99", (ushort)(ipv6 ? 546 : 68), (ushort)(ipv6 ? 547 : 67));
        var policy = RouteInterfacePolicy.All;
        await policy.BypassesLocalTraffic(99, flow).Should().BeTrue();
        await policy.BypassesLocalTraffic(99, flow with { LocalPort = flow.RemotePort, RemotePort = flow.LocalPort }).Should().BeTrue();
        await policy.BypassesLocalTraffic(99, flow with { LocalPort = flow.RemotePort }).Should().BeTrue();
        await policy.BypassesLocalTraffic(99, flow with { LocalPort = 50000 }).Should().BeFalse();
        await policy.BypassesLocalTraffic(99, flow with { RemotePort = 443 }).Should().BeFalse();
        await policy.BypassesLocalTraffic(99, flow with { RemotePort = flow.LocalPort }).Should().BeFalse();
        await policy.BypassesLocalTraffic(99, flow with { Protocol = 6 }).Should().BeFalse();
    }

    [Test]
    public async Task AddressChangesAndTheExceptionSwitchPublishFreshPolicyWithoutRetiringConnections()
    {
        var config = new Config();
        IReadOnlyList<RouteInterfaceInfo> adapters = [Lan()];
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(0), () => adapters, _ => { });
        await monitor.RefreshAsync();
        var previous = monitor.Policy;
        adapters = [Lan(ip: "10.7.0.12")];
        await monitor.RefreshAsync();
        await monitor.Policy.SameAs(previous).Should().BeFalse();
        await monitor.Policy.Retains(previous, 7, false).Should().BeTrue();
        await monitor.Policy.BypassesLocalTraffic(7, Flow("192.168.8.255", 50000, 9)).Should().BeFalse();
        await monitor.Policy.BypassesLocalTraffic(7, Flow("10.7.0.255", 50000, 9)).Should().BeTrue();
        previous = monitor.Policy;
        config.AppRouting.BypassLocalTraffic = false;
        await monitor.RefreshAsync();
        await monitor.Policy.SameAs(previous).Should().BeFalse();
        await monitor.Policy.Retains(previous, 7, false).Should().BeTrue();
        await monitor.Policy.BypassesLocalTraffic(7, Flow("224.0.0.251")).Should().BeFalse();
    }

    [Test]
    [Arguments("224.0.0.251", false)]
    [Arguments("ff02::fb", false)]
    [Arguments("192.168.8.99", false)]
    [Arguments("224.0.0.251", true)]
    [Arguments("ff02::fb", true)]
    [Arguments("192.168.8.99", true)]
    public async Task SharedMdnsPacketsAndOutOfOrderFragmentsKeepTheirOriginalBytesAndMetadata(string peer, bool fragmented)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = new RouteInterfacePolicy(new(), [Lan()]);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var flow = Flow(peer);
        var selected = RouteTestFactory.Process("fixture.exe");
        var owners = new RouteAttributionSnapshot([], [new(flow.LocalAddress, flow.LocalPort, null, 0, 10), new(flow.LocalAddress, flow.LocalPort, null, 0, 20)],
            pid => pid == 10 ? new(RouteDecisionKind.Selected, new(10, 1), selected.Target) : RouteDecision.Unselected, Environment.TickCount64);
        await owners.Find(flow).Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
        typeof(AppRouteEngine).GetMethod("PublishRouting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, [new RoutePolicy(selected.Policy, []), owners]);
        var original = Packet(flow);
        var parts = fragmented ? new[] { FragmentTests.Fragment(original, 16, 24, false), FragmentTests.Fragment(original, 0, 16, true) } : [original];
        var address = new DivertAddress { InterfaceIndex = 7, SubInterfaceIndex = 2, Timestamp = 123, Outbound = true };
        var sent = new List<byte[]>();
        var metadata = new List<DivertAddress>();
        var output = new RoutePacketBatch((bytes, addresses) =>
        {
            Span<int> lengths = stackalloc int[RoutePacketBatch.Capacity];
            var count = RoutePacketBatch.ReadLengths(bytes, (uint)(addresses.Length * RoutePacketBatch.AddressSize), lengths);
            for (var i = 0; i < count; i++) { sent.Add(bytes[..lengths[i]].ToArray()); bytes = bytes[lengths[i]..]; }
            metadata.AddRange(addresses.ToArray());
        });
        foreach (var part in parts) { Capture(engine, part, address, output); output.Flush(); }
        await sent.Count.Should().BeEqualTo(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            await sent[i].SequenceEqual(parts[i]).Should().BeTrue();
            await metadata[i].Equals(address).Should().BeTrue();
        }
        await Field<RoutePendingPackets>(engine, "_pending").Count.Should().BeEqualTo(0);
    }

    [Test]
    public async Task DisablingTheExceptionRestoresOwnershipMatching()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = new RouteInterfacePolicy(new(), [Lan()], false);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var flow = Flow("224.0.0.251");
        var output = new RoutePacketBatch((_, _) => throw new InvalidOperationException("Unresolved packets must wait."));
        Capture(engine, Packet(flow), new() { InterfaceIndex = 7 }, output);
        output.Flush();
        await Field<RoutePendingPackets>(engine, "_pending").Count.Should().BeEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnotherAdaptersAddressOrSelectionChangeDoesNotStrandLocalFragments(bool changeAddress)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var adapters = new[] { Lan(), Lan(8, "10.7.0.9") with { Id = "vpn" } };
        var options = RouteInterfaceCatalog.Discover(new(), adapters);
        var policy = new RouteInterfacePolicy(options, adapters);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var bytes = Packet(Flow("192.168.8.99"));
        var packets = 0;
        var output = new RoutePacketBatch((_, addresses) => packets += addresses.Length);
        Capture(engine, FragmentTests.Fragment(bytes, 0, 16, true), new() { InterfaceIndex = 7 }, output);
        output.Flush();
        if (changeAddress) { adapters[1] = adapters[1] with { Addresses = [new(IPAddress.Parse("10.8.0.9"), 24)] }; }
        else { options.Interfaces[1].Monitored = false; }
        policy = new(options, adapters);
        InitializeEngine(engine);
        Capture(engine, FragmentTests.Fragment(bytes, 16, 24, false), new() { InterfaceIndex = 7 }, output);
        output.Flush();
        await packets.Should().BeEqualTo(2);
    }

    [Test]
    public async Task ExistingTcpTranslationStillOwnsItsLinkLocalFragments()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        var flow = Flow("169.254.6.8", 50000, 443, 6);
        Field<RouteNatTable>(engine, "_nat").GetOrAdd(flow, RouteTestFactory.Target(), 123);
        var bytes = Packet(flow);
        bytes[9] = 6;
        bytes[32] = 0x50;
        var buffer = Field<RouteFragmentBuffer>(engine, "_fragments");
        buffer.Add(FragmentTests.Fragment(bytes, 16, 24, false), new() { InterfaceIndex = 7 }, out var tail);
        await tail.Should().BeNull();
        buffer.Add(FragmentTests.Fragment(bytes, 0, 16, true), new() { InterfaceIndex = 7 }, out var whole);
        await whole!.PassThrough.Should().BeFalse();
        await whole.Packet.SequenceEqual(bytes).Should().BeTrue();
    }
}
