using System.Buffers.Binary;
using System.Net.NetworkInformation;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class InterfaceTests
{
    [Test]
    public async Task FilterModulesAreNotIndependentAdapterChoices()
    {
        var ethernet = Adapter(Guid.NewGuid().ToString("B"), 1, "Ethernet");
        var vpn = Adapter(Guid.NewGuid().ToString("B"), 2, "VPN");
        var filter = Adapter(Guid.NewGuid().ToString("B"), 0, "Ethernet-Npcap Filter");
        var qos = Adapter(Guid.NewGuid().ToString("B"), 0, "Ethernet-QoS Packet Scheduler");
        var similarlyNamedAdapter = Adapter(Guid.NewGuid().ToString("B"), 3, "Npcap test network");
        var result = RouteInterfaceCatalog.WithoutFilterModules(
            [ethernet, vpn, filter, qos, similarlyNamedAdapter], new HashSet<Guid> { Guid.Parse(filter.Id), Guid.Parse(qos.Id) });
        await result.SequenceEqual(new[] { ethernet, vpn, similarlyNamedAdapter }).Should().BeTrue();
    }

    [Test]
    public async Task NativeInterfaceRowsHaveWindowsLayout()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Marshal.SizeOf<RouteInterfaceNative.InterfaceRow>().Should().BeEqualTo(1352);
        await Marshal.OffsetOf<RouteInterfaceNative.InterfaceTable>(nameof(RouteInterfaceNative.InterfaceTable.First)).ToInt32().Should().BeEqualTo(8);
        var rows = RouteInterfaceNative.Read().ToDictionary(r => r.Id);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            await rows.ContainsKey(Guid.Parse(adapter.Id)).Should().BeTrue();
            if (adapter.Supports(NetworkInterfaceComponent.IPv4))
            {
                await rows[Guid.Parse(adapter.Id)].Index.Should().BeEqualTo((uint)adapter.GetIPProperties().GetIPv4Properties()!.Index);
            }
        }
        var catalog = RouteInterfaceCatalog.Read();
        await catalog.All(a => !rows[Guid.Parse(a.Id)].IsFilter).Should().BeTrue();
    }

    internal static RouteInterfaceInfo Adapter(string id, uint index, string name = "Ethernet") =>
        new(id, name, "Fixture adapter", System.Net.NetworkInformation.OperationalStatus.Up, index, index + 100);

    [Test]
    public async Task OldConfigurationKeepsMonitoringAllInterfaces()
    {
        var config = JsonUtils.Deserialize<AppRoutingItem>("{\"Rules\":[]}")!;
        await config.InterfaceMonitoring.MonitorNewInterfaces.Should().BeTrue();
        await new RouteInterfacePolicy(config.InterfaceMonitoring, []).Monitors(25, false).Should().BeTrue();
    }

    [Test]
    public async Task DiscoveryPersistsFirstSeenDefaultAcrossRenamesReindexingAndRemoval()
    {
        var options = RouteInterfaceCatalog.Discover(new(), [Adapter("id-a", 1)]);
        options.MonitorNewInterfaces = false;
        options = RouteInterfaceCatalog.Discover(options, [Adapter("ID-A", 8, "Renamed"), Adapter("id-b", 1, "VPN")]);
        options = JsonUtils.DeepCopy(options);
        await options.Interfaces.Count.Should().BeEqualTo(2);
        await options.Interfaces[0].Name.Should().BeEqualTo("Renamed");
        await options.Interfaces[0].Monitored.Should().BeTrue();
        await options.Interfaces[1].Monitored.Should().BeFalse();
        options = RouteInterfaceCatalog.Discover(options, []);
        options.MonitorNewInterfaces = true;
        options = RouteInterfaceCatalog.Discover(options, [Adapter("id-b", 9), Adapter("id-c", 10)]);
        await options.Interfaces.Single(i => i.Id == "id-b").Monitored.Should().BeFalse();
        await options.Interfaces.Single(i => i.Id == "id-c").Monitored.Should().BeTrue();
        await options.Interfaces.Count.Should().BeEqualTo(3);
    }

    [Test]
    public async Task PolicySeparatesFamiliesAndDoesNotTransferAChoiceToAReusedIndex()
    {
        var first = Adapter("a", 7);
        var options = RouteInterfaceCatalog.Discover(new() { MonitorNewInterfaces = false }, [first]);
        options.Interfaces[0].Monitored = true;
        var before = new RouteInterfacePolicy(options, [first]);
        var after = new RouteInterfacePolicy(options, [Adapter("a", 8), Adapter("b", 7)]);
        await before.Monitors(7, false).Should().BeTrue();
        await before.Monitors(107, true).Should().BeTrue();
        await before.Monitors(107, false).Should().BeFalse();
        await after.Monitors(7, false).Should().BeFalse();
        await after.Monitors(8, false).Should().BeTrue();
        await after.Retains(before, 7, false).Should().BeFalse();
        options.MonitorNewInterfaces = true;
        after = new(options, [Adapter("b", 7)]);
        await after.Monitors(7, false).Should().BeTrue();
        await after.Retains(before, 7, false).Should().BeFalse();
    }

    [Test]
    public async Task DiscoveryWhileDisabledIsSavedAndUnchangedSnapshotsDoNotWriteAgain()
    {
        var config = new Config();
        var saves = 0;
        string? json = null;
        await using var monitor = new RouteInterfaceMonitor(config, c =>
        {
            saves++;
            json = JsonUtils.Serialize(c);
            return Task.FromResult(0);
        }, () => [Adapter("a", 1)], _ => { });
        await monitor.RefreshAsync();
        var policy = monitor.Policy;
        await monitor.RefreshAsync();
        await saves.Should().BeEqualTo(1);
        await ReferenceEquals(policy, monitor.Policy).Should().BeTrue();
        var reloaded = JsonUtils.Deserialize<Config>(json)!;
        await reloaded.AppRouting.Enabled.Should().BeFalse();
        await reloaded.AppRouting.InterfaceMonitoring.Interfaces[0].Id.Should().BeEqualTo("a");
    }

    [Test]
    public async Task SavingAnOlderDialogPreservesInterfacesDiscoveredSinceItOpened()
    {
        var config = new Config();
        IReadOnlyList<RouteInterfaceInfo> adapters = [Adapter("a", 1)];
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(0), () => adapters, _ => { });
        var draft = JsonUtils.DeepCopy((await monitor.RefreshAsync()).Options);
        adapters = [Adapter("a", 1), Adapter("b", 2)];
        await monitor.RefreshAsync();
        draft.MonitorNewInterfaces = false;
        draft.Interfaces[0].Monitored = false;
        await monitor.SaveAsync(draft);
        adapters = [.. adapters, Adapter("c", 3)];
        await monitor.RefreshAsync();
        await monitor.Policy.Monitors(1, false).Should().BeFalse();
        await monitor.Policy.Monitors(2, false).Should().BeTrue();
        await monitor.Policy.Monitors(3, false).Should().BeFalse();
    }

    [Test]
    public async Task FailedSavePreservesBothSavedChoicesAndPublishedPolicy()
    {
        var config = new Config();
        var fail = false;
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(fail ? -1 : 0),
            () => [Adapter("a", 1)], _ => { });
        var draft = JsonUtils.DeepCopy((await monitor.RefreshAsync()).Options);
        draft.Interfaces[0].Monitored = false;
        fail = true;
        var failed = false;
        try { await monitor.SaveAsync(draft); }
        catch (IOException) { failed = true; }
        await failed.Should().BeTrue();
        await config.AppRouting.InterfaceMonitoring.Interfaces[0].Monitored.Should().BeTrue();
        await monitor.Policy.Monitors(1, false).Should().BeTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ExcludedPacketsBypassOwnershipAndKeepTheirBytesAndMetadata(bool ipv6, bool tcp)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = new RouteInterfacePolicy(new() { MonitorNewInterfaces = false }, []);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var bytes = Packet(ipv6, tcp);
        var original = bytes.ToArray();
        var address = new DivertAddress { InterfaceIndex = 99, SubInterfaceIndex = 2, Timestamp = 123, Outbound = true };
        var sent = new List<byte[]>();
        var metadata = new List<DivertAddress>();
        var output = new RoutePacketBatch((packets, addresses) => { sent.Add(packets.ToArray()); metadata.AddRange(addresses.ToArray()); });
        Capture(engine, bytes, address, output);
        output.Flush();
        await sent.Single().SequenceEqual(original).Should().BeTrue();
        await metadata.Single().Equals(address).Should().BeTrue();
        await Field<RoutePendingPackets>(engine, "_pending").Count.Should().BeEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ExcludedFragmentsBypassReassemblyEvenOutOfOrder(bool ipv6, bool tcp)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = new RouteInterfacePolicy(new() { MonitorNewInterfaces = false }, []);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var packet = Packet(ipv6, tcp, 32);
        var last = FragmentTests.Fragment(packet, 16, 24, false);
        var first = FragmentTests.Fragment(packet, 0, 16, true);
        var sent = new List<byte[]>();
        var output = new RoutePacketBatch((packets, _) => sent.Add(packets.ToArray()));
        Capture(engine, last, new() { InterfaceIndex = 5 }, output);
        output.Flush();
        await sent.Single().SequenceEqual(last).Should().BeTrue();
        Capture(engine, first, new() { InterfaceIndex = 5 }, output);
        output.Flush();
        await sent[1].SequenceEqual(first).Should().BeTrue();
    }

    [Test]
    public async Task UnknownAdapterUsesNewInterfaceDefaultBeforeDiscovery()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = RouteInterfacePolicy.All;
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var output = new RoutePacketBatch((_, _) => throw new InvalidOperationException("Unresolved monitored packet must be deferred."));
        Capture(engine, Packet(false, false), new() { InterfaceIndex = 123 }, output);
        output.Flush();
        await Field<RoutePendingPackets>(engine, "_pending").Count.Should().BeEqualTo(1);
    }

    [Test]
    public async Task ExcludingAnAdapterRetiresOnlyItsConnectionsAndKeepsReverseMappings()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var adapters = new[] { Adapter("a", 1), Adapter("b", 2) };
        var options = RouteInterfaceCatalog.Discover(new(), adapters);
        var policy = new RouteInterfacePolicy(options, adapters);
        var selected = RouteTestFactory.Process("fixture.exe");
        var rule = selected.Target;
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        await engine.ApplyAsync(selected.Policy, [], default);
        InitializeEngine(engine);
        var nat = Field<RouteNatTable>(engine, "_nat");
        var first = nat.GetOrAdd(PacketTests.Flow(false) with { Protocol = 6 }, rule, 1);
        first.OriginalAddress = new() { InterfaceIndex = 1 };
        var second = nat.GetOrAdd(first.Flow with { LocalPort = 5555 }, rule, 2);
        second.OriginalAddress = new() { InterfaceIndex = 2 };
        options.Interfaces[0].Monitored = false;
        policy = new(options, adapters);
        InitializeEngine(engine);
        await first.Closed.Should().BeTrue();
        await second.Closed.Should().BeFalse();
        await (nat.Find(first.Flow) == null).Should().BeTrue();
        await ReferenceEquals(nat.Reverse(first.Flow.LocalAddress, first.Flow.RemoteAddress, first.TranslatedPort), first).Should().BeTrue();
        await nat.MayBeReflection(first.Flow.LocalAddress, first.Flow.RemoteAddress).Should().BeTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ListenerPacketsAreNotPassedThroughAnExcludedAdapter(bool fragmented)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var policy = new RouteInterfacePolicy(new() { MonitorNewInterfaces = false }, []);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var bytes = Packet(false, true, 32);
        var flow = RoutePacket.Parse(bytes)!.Flow;
        Field<Dictionary<AddressFamily, ushort>>(engine, "_ports")[AddressFamily.InterNetwork] = flow.LocalPort;
        // No reverse mapping: the listener's stale response must be discarded.
        // With fragments, retain a different mapping between the same addresses
        // to exercise the conservative reply-reassembly path.
        if (fragmented)
        {
            Field<RouteNatTable>(engine, "_nat").GetOrAdd(flow with { LocalPort = 1234 }, RouteTestFactory.Target(), 0);
        }
        var count = 0;
        var output = new RoutePacketBatch((_, _) => count++);
        if (fragmented)
        {
            Capture(engine, FragmentTests.Fragment(bytes, 16, 24, false), new() { InterfaceIndex = 9 }, output);
            Capture(engine, FragmentTests.Fragment(bytes, 0, 16, true), new() { InterfaceIndex = 9 }, output);
        }
        else { Capture(engine, bytes, new() { InterfaceIndex = 9 }, output); }
        output.Flush();
        await count.Should().BeEqualTo(0);
    }

    private static byte[] Packet(bool ipv6, bool tcp, int payload = 12)
    {
        var bytes = RoutePacket.CreateUdpReply(PacketTests.Flow(ipv6), new byte[payload]);
        if (tcp)
        {
            var offset = ipv6 ? 40 : 20;
            bytes[ipv6 ? 6 : 9] = 6;
            bytes[offset + 12] = 0x50;
            bytes[offset + 13] = 2;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 123);
        }
        return bytes;
    }

    [Test]
    public async Task ReadOnlyCatalogProvidesIndexesForBothAddressFamilies()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var adapters = RouteInterfaceCatalog.Read();
        await adapters.All(a => !string.IsNullOrWhiteSpace(a.Id)).Should().BeTrue();
        await adapters.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().BeEqualTo(adapters.Count);
        var options = RouteInterfaceCatalog.Discover(new() { MonitorNewInterfaces = false }, adapters);
        var policy = new RouteInterfacePolicy(options, adapters);
        foreach (var adapter in adapters)
        {
            if (adapter.IPv4Index != 0) { await policy.Monitors(adapter.IPv4Index, false).Should().BeFalse(); }
            if (adapter.IPv6Index != 0) { await policy.Monitors(adapter.IPv6Index, true).Should().BeFalse(); }
        }
    }

    [Test]
    public async Task FailedDiscoveryKeepsLastSnapshotAndSavedChoices()
    {
        var config = new Config();
        var fail = false;
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(0),
            () => fail ? throw new IOException("Adapter enumeration failed") : [Adapter("a", 1)], _ => { });
        await monitor.RefreshAsync();
        var policy = monitor.Policy;
        fail = true;
        var failed = false;
        try { await monitor.RefreshAsync(); }
        catch (IOException) { failed = true; }
        await failed.Should().BeTrue();
        await ReferenceEquals(policy, monitor.Policy).Should().BeTrue();
        await config.AppRouting.InterfaceMonitoring.Interfaces.Single().Id.Should().BeEqualTo("a");
    }

    [Test]
    public async Task InterfaceChangeRetiresPendingAttributionOnlyOnAffectedAdapter()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var adapters = new[] { Adapter("a", 1), Adapter("b", 2) };
        var options = RouteInterfaceCatalog.Discover(new(), adapters);
        var policy = new RouteInterfacePolicy(options, adapters);
        await using var engine = new AppRouteEngine(_ => { }, () => policy);
        InitializeEngine(engine);
        var output = new RoutePacketBatch((_, _) => throw new InvalidOperationException("Unresolved traffic must wait."));
        Capture(engine, Packet(false, false), new() { InterfaceIndex = 1 }, output);
        Capture(engine, Packet(false, false), new() { InterfaceIndex = 2 }, output);
        var pending = Field<RoutePendingPackets>(engine, "_pending");
        await pending.Count.Should().BeEqualTo(2);
        options.Interfaces[0].Monitored = false;
        policy = new(options, adapters);
        InitializeEngine(engine);
        await pending.Count.Should().BeEqualTo(1);
        await pending.Dequeue().Address.InterfaceIndex.Should().BeEqualTo(2u);
    }

    private static T Field<T>(AppRouteEngine engine, string name) =>
        (T)typeof(AppRouteEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InterfaceChangePreservesOtherAdaptersFragmentBypass(bool ipv6)
    {
        var bytes = Packet(ipv6, false, 32);
        var first = FragmentTests.Fragment(bytes, 0, 16, true);
        var last = FragmentTests.Fragment(bytes, 16, 24, false);
        var buffer = new RouteFragmentBuffer(_ => RouteDecisionKind.Unselected);
        buffer.Add(first, new() { InterfaceIndex = 1 }, out _);
        buffer.Add(first, new() { InterfaceIndex = 2 }, out _);
        buffer.RetainInterfaces((index, _) => index == 2);
        buffer.Add(last, new() { InterfaceIndex = 1 }, out var retired);
        buffer.Add(last, new() { InterfaceIndex = 2 }, out var retained);
        await (retired == null).Should().BeTrue();
        await retained!.PassThrough.Should().BeTrue();
        await retained.Originals.Single().Packet.SequenceEqual(last).Should().BeTrue();
    }

    private static void InitializeEngine(AppRouteEngine engine)
    {
        typeof(AppRouteEngine).GetMethod("RefreshInterfaces", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, null);
        var ports = Field<Dictionary<AddressFamily, ushort>>(engine, "_ports");
        ports[AddressFamily.InterNetwork] = 60000;
        ports[AddressFamily.InterNetworkV6] = 60001;
    }

    private static void Capture(AppRouteEngine engine, byte[] packet, DivertAddress address, RoutePacketBatch output) =>
        typeof(AppRouteEngine).GetMethod("ProcessCapturedPacket", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, [(Memory<byte>)packet, address, output]);
}
