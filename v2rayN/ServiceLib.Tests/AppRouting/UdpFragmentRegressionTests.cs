using System.Buffers.Binary;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class UdpFragmentRegressionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletedDatagramsCannotAuthorizeFragmentsWithAReusedIpId(bool ipv6)
    {
        var buffer = new RouteFragmentBuffer();
        var old = RoutePacket.CreateUdpReply(PacketTests.Flow(ipv6), new byte[32]);
        buffer.Add(FragmentTests.Fragment(old, 0, 16, true), default, out _);
        buffer.Add(FragmentTests.Fragment(old, 16, 24, false), default, out _);
        var next = old.ToArray();
        var header = ipv6 ? 40 : 20;
        BinaryPrimitives.WriteUInt16BigEndian(next.AsSpan(header), 1194); // Another socket, same IP endpoints/ID.
        next.AsSpan(header + 8).Fill(123);
        buffer.Add(FragmentTests.Fragment(next, 16, 24, false), default, out var tail);
        await tail.Should().BeNull(); // Never emit selected tail fragments on the native path.
        buffer.Add(FragmentTests.Fragment(next, 0, 16, true), default, out var complete);
        await complete!.PassThrough.Should().BeFalse();
        await complete.Packet.SequenceEqual(next).Should().BeTrue();
    }

    [Test]
    [Arguments(false, 1472)]
    [Arguments(false, 1500)]
    [Arguments(false, 1536)]
    [Arguments(false, 16384)]
    [Arguments(true, 1472)]
    [Arguments(true, 1500)]
    [Arguments(true, 1536)]
    [Arguments(true, 16384)]
    public async Task MtuSizedAndLargerDatagramsKeepEveryByteThroughReassemblyAndSocks(bool ipv6, int size)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)((i * 31) & 255)).ToArray();
        var original = RoutePacket.CreateUdpReply(PacketTests.Flow(ipv6), payload);
        var header = ipv6 ? 40 : 20;
        var fragmentSize = (1500 - header - (ipv6 ? 8 : 0)) & ~7;
        var fragments = new List<byte[]>();
        for (var offset = 0; offset < size + 8; offset += fragmentSize)
        {
            var count = Math.Min(fragmentSize, size + 8 - offset);
            fragments.Add(FragmentTests.Fragment(original, offset, count, offset + count < size + 8));
        }
        var buffer = new RouteFragmentBuffer();
        RouteFragmentBuffer.Batch? complete = null;
        foreach (var fragment in fragments.AsEnumerable().Reverse())
        {
            buffer.Add(fragment, new() { InterfaceIndex = 7 }, out var batch);
            if (batch != null) { complete = batch; }
        }
        // A datagram that fits IPv4's MTU is not fragmented by the fixture.
        var packet = complete?.Packet ?? original;
        await packet.SequenceEqual(original).Should().BeTrue();
        if (fragments.Count > 1) { await complete!.Originals.Count.Should().BeEqualTo(fragments.Count); }
        var parsed = RoutePacket.Parse(packet)!;
        var wire = RouteConnector.WrapDatagram(new(parsed.Flow.RemoteAddress, parsed.Flow.RemotePort), packet.AsSpan(parsed.TransportOffset + 8));
        var start = RouteConnector.UnwrapDatagram(wire, out _);
        await wire.AsSpan(start).SequenceEqual(payload).Should().BeTrue();
        var reply = RoutePacket.CreateUdpReply(parsed.Flow, wire.AsSpan(start));
        await reply.AsSpan(header + 8).SequenceEqual(payload).Should().BeTrue();
    }

    [Test]
    public async Task Ipv6DestinationOptionsAfterFragmentHeaderRemainRoutable()
    {
        var udp = RoutePacket.CreateUdpReply(PacketTests.Flow(true), new byte[32]);
        var original = new byte[udp.Length + 8];
        udp.AsSpan(0, 40).CopyTo(original);
        original[6] = 60;
        original[40] = 17;
        udp.AsSpan(40).CopyTo(original.AsSpan(48));
        BinaryPrimitives.WriteUInt16BigEndian(original.AsSpan(4), (ushort)(original.Length - 40));
        var buffer = new RouteFragmentBuffer();
        await buffer.Add(FragmentTests.Fragment(original, 16, 32, false), default, out var tail).Should().BeTrue();
        await tail.Should().BeNull();
        await buffer.Add(FragmentTests.Fragment(original, 0, 16, true), default, out var complete).Should().BeTrue();
        await complete!.PassThrough.Should().BeFalse();
        await complete.Packet.SequenceEqual(original).Should().BeTrue();
        await RoutePacket.Parse(complete.Packet)!.Flow.Should().BeEqualTo(RoutePacket.Parse(udp)!.Flow);
    }
}
