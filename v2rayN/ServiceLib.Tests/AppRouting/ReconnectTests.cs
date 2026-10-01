using System.Buffers.Binary;
using ServiceLib.Services.AppRouting;
using static ServiceLib.Tests.AppRouting.InterfaceTests;

namespace ServiceLib.Tests.AppRouting;

public class ReconnectTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalRelayCompletionStillLetsTheFinalTcpAckReachItsListener(bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        var entry = Field<RouteNatTable>(engine, "_nat").GetOrAdd(flow, RouteTestFactory.Target(), 100);
        entry.Accepted = true;
        entry.Closed = true; // Both stream copies ended normally, not policy retirement.
        byte[]? sent = null;
        var output = new RoutePacketBatch((bytes, _) => sent = bytes.ToArray());
        Capture(engine, Tcp(flow, 0x10, 100, 200), new() { InterfaceIndex = 7 }, output);
        output.Flush();
        var translated = RoutePacket.Parse(sent!)!;
        await translated.TcpFlags.Should().BeEqualTo((byte)0x10);
        await translated.Flow.LocalPort.Should().BeEqualTo(entry.TranslatedPort);
        await translated.Flow.RemotePort.Should().BeEqualTo(ipv6 ? (ushort)60001 : (ushort)60000);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AResetDistinguishesAZeroAckFromAPendingHandshake(bool acknowledged)
    {
        var flow = PacketTests.Flow(false) with { Protocol = 6 };
        var entry = new RouteNatEntry(flow, RouteTestFactory.Target(), 1024, uint.MaxValue);
        if (acknowledged) { entry.ClientAcknowledgement = 0; }
        var reset = RoutePacket.Parse(entry.CreateReset())!;
        await reset.TcpFlags.Should().BeEqualTo(acknowledged ? (byte)0x04 : (byte)0x14);
        await reset.TcpSequence.Should().BeEqualTo(0u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CutoverResetsIdleStreamsAndAllowsAFreshSynOnTheSameTuple(bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        var (_, oldTarget) = RouteTestFactory.Process("fixture.exe");
        var (newPolicy, newTarget) = RouteTestFactory.Process("fixture.exe");
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        var nat = Field<RouteNatTable>(engine, "_nat");
        var old = nat.GetOrAdd(flow, oldTarget, uint.MaxValue);
        old.OriginalAddress = new() { InterfaceIndex = 7, SubInterfaceIndex = 3 };
        old.Accepted = true;
        // A real captured ACK records RCV.NXT for a reset of an otherwise idle stream.
        var ignored = new RoutePacketBatch((_, _) => { });
        Capture(engine, Tcp(flow, 0x10, 100, 200), old.OriginalAddress, ignored);
        byte[]? sent = null;
        var output = new RoutePacketBatch((bytes, _) => sent = bytes.ToArray());
        engine.ResetTcpConnections(nat.Retain(new(newPolicy, [])), output);
        output.Flush();
        await old.Closed.Should().BeTrue();
        await RoutePacket.Parse(sent!)!.TcpSequence.Should().BeEqualTo(200u);
        await ReferenceEquals(nat.Find(flow), old).Should().BeTrue();
        await nat.Find(flow, 300).Should().BeNull();
        var fresh = nat.GetOrAdd(flow, newTarget, 300);
        await fresh.Rule.Should().BeEqualTo(newTarget);
        await (fresh.TranslatedPort != old.TranslatedPort).Should().BeTrue();
        await ReferenceEquals(nat.Reverse(flow.LocalAddress, flow.RemoteAddress, old.TranslatedPort), old).Should().BeTrue();
    }

    [Test]
    [Arguments(false, (byte)0x02, 3, 0xfffffffeu, 2u)]
    [Arguments(true, (byte)0x01, 4, 0xfffffffeu, 3u)]
    public async Task ResetWithoutAnAckAccountsForPayloadSynFinAndSequenceWrap(bool ipv6, byte flags, int payload, uint sequence, uint expectedAck)
    {
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        var bytes = Tcp(flow, flags, sequence, 999, payload);
        var reset = RoutePacket.Parse(bytes)!.CreateTcpReset(bytes)!;
        var parsed = RoutePacket.Parse(reset)!;
        await parsed.TcpFlags.Should().BeEqualTo((byte)0x14);
        await parsed.TcpSequence.Should().BeEqualTo(0u);
        await BinaryPrimitives.ReadUInt32BigEndian(reset.AsSpan(parsed.TransportOffset + 8)).Should().BeEqualTo(expectedAck);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task SelectedTcpWithoutALiveRelayResetsInsteadOfWaitingForAnAppRestart(bool ipv6, bool retired)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (policy, target) = RouteTestFactory.Process("fixture.exe");
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        Publish(engine, policy, flow, new(RouteDecisionKind.Selected, new(10, 1), target));
        if (retired)
        {
            var old = Field<RouteNatTable>(engine, "_nat").GetOrAdd(flow, RouteTestFactory.Target(), 100);
            old.Accepted = true;
            Field<RouteNatTable>(engine, "_nat").Retain(new(policy, []));
        }
        var sent = new List<byte[]>();
        var addresses = new List<DivertAddress>();
        var output = new RoutePacketBatch((bytes, metadata) =>
        { sent.Add(bytes.ToArray()); addresses.AddRange(metadata.ToArray()); });
        var original = Tcp(flow, 0x18, 100, 0xfedcba98, 7);
        Capture(engine, original, new() { InterfaceIndex = 7, SubInterfaceIndex = 3, Outbound = true }, output);
        output.Flush();
        await sent.Count.Should().BeEqualTo(1);
        var reset = RoutePacket.Parse(sent.Single())!;
        await reset.Flow.Should().BeEqualTo(flow with
        { LocalAddress = flow.RemoteAddress, LocalPort = flow.RemotePort, RemoteAddress = flow.LocalAddress, RemotePort = flow.LocalPort });
        await reset.TcpFlags.Should().BeEqualTo((byte)0x04);
        await reset.TcpSequence.Should().BeEqualTo(0xfedcba98u);
        await sent.Single().Length.Should().BeEqualTo((ipv6 ? 40 : 20) + 20);
        await addresses.Single().Outbound.Should().BeFalse();
        await addresses.Single().InterfaceIndex.Should().BeEqualTo(7u);
        await addresses.Single().SubInterfaceIndex.Should().BeEqualTo(3u);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AWithdrawnTcpRuleResetsItsOldStreamInsteadOfPassingItDirect(bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        var policy = RouteTestFactory.Policy();
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        Publish(engine, policy, flow, RouteDecision.Unselected);
        var nat = Field<RouteNatTable>(engine, "_nat");
        nat.GetOrAdd(flow, RouteTestFactory.Target(), 100).Accepted = true;
        nat.Retain(new(policy, []));
        byte[]? sent = null;
        var output = new RoutePacketBatch((bytes, _) => sent = bytes.ToArray());
        Capture(engine, Tcp(flow, 0x10, 100, 200), new() { InterfaceIndex = 7, Outbound = true }, output);
        output.Flush();
        await sent.Should().NotBeNull();
        await RoutePacket.Parse(sent!)!.TcpFlags.Should().BeEqualTo((byte)0x04);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrphanResetsDoNotGenerateAnotherReset(bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var (policy, target) = RouteTestFactory.Process("fixture.exe");
        var flow = PacketTests.Flow(ipv6) with { Protocol = 6 };
        await using var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        Publish(engine, policy, flow, new(RouteDecisionKind.Selected, new(10, 1), target));
        var sent = 0;
        var output = new RoutePacketBatch((_, _) => sent++);
        Capture(engine, Tcp(flow, 0x14, 100, 200), new() { InterfaceIndex = 7 }, output);
        output.Flush();
        await sent.Should().BeEqualTo(0);
    }

    internal static byte[] Tcp(RouteFlow flow, byte flags, uint sequence, uint acknowledgement, int payload = 0)
    {
        var bytes = RoutePacket.CreateUdpReply(flow with
        { LocalAddress = flow.RemoteAddress, LocalPort = flow.RemotePort, RemoteAddress = flow.LocalAddress, RemotePort = flow.LocalPort }, new byte[12 + payload]);
        var header = flow.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6 ? 40 : 20;
        bytes[header == 40 ? 6 : 9] = 6;
        bytes[header + 12] = 0x50;
        bytes[header + 13] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header + 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(header + 8), acknowledgement);
        return bytes;
    }

    internal static void Publish(AppRouteEngine engine, RouteSharedPolicy policy, RouteFlow flow, RouteDecision owner)
    {
        var snapshot = new RouteAttributionSnapshot([new(flow.LocalAddress, flow.LocalPort, flow.RemoteAddress, flow.RemotePort, 10)],
            [], _ => owner, Environment.TickCount64);
        typeof(AppRouteEngine).GetMethod("PublishRouting", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(engine, [new RoutePolicy(policy, []), snapshot]);
    }
}
