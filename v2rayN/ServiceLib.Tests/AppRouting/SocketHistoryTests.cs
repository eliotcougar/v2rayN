using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class SocketHistoryTests
{
    private static readonly long Second = Stopwatch.Frequency;
    private static readonly RouteDecision Selected = new(RouteDecisionKind.Selected, new(20, 1), RouteTestFactory.Target());
    private static RouteSocketEvent Open(long at, ulong endpoint = 1, byte protocol = 17) =>
        new(4, at, endpoint, 20, PacketTests.Flow(false) with { Protocol = protocol });
    private static RouteSocketPresence Presence(RouteSocketEvent socket) => socket.Flow.Protocol == 6
        ? new([new(socket.Flow.LocalAddress, socket.Flow.LocalPort, socket.Flow.RemoteAddress, socket.Flow.RemotePort, socket.Pid)], [])
        : new([], [new(socket.Flow.LocalAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any,
            socket.Flow.LocalPort, null, 0, socket.Pid)]);

    [Test]
    public async Task RepeatedAuthorizationDoesNotAccumulateLifetimes()
    {
        var history = new RouteSocketHistory();
        history.Update(Enumerable.Range(1, 70_000).Select(at => Open(at)), Second);
        await history.Count.Should().BeEqualTo(1);
        await history.Snapshot((_, _) => Selected).Find(Open(1).Flow, 1)!.Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        history.Update([Open(2 * Second) with { Event = 7 }, Open(3 * Second)], 3 * Second);
        await history.Count.Should().BeEqualTo(3); // The close still separates two lifetimes.
        await history.Snapshot((_, _) => Selected).Find(Open(1).Flow, 2 * Second + 1)!.Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
    }

    [Test]
    public async Task UdpBindCoversManyPeersWithoutAccumulatingConnectRecords()
    {
        var history = new RouteSocketHistory();
        var bind = Open(1) with { Event = 3, Flow = Open(1).Flow with { LocalAddress = IPAddress.Any, RemoteAddress = IPAddress.Any, RemotePort = 0 } };
        history.Update([bind], 1);
        history.Update(Enumerable.Range(1, 70_000).Select(i => Open(i + 1) with
        { Flow = Open(1).Flow with { RemotePort = (ushort)(1 + i % 65535) } }), Second);
        await history.Count.Should().BeEqualTo(1);
        await history.Snapshot((_, _) => Selected).Find(Open(1).Flow, 0)!.Endpoint.Should().BeEqualTo(1UL);
    }

    [Test]
    [Arguments((byte)6, false)]
    [Arguments((byte)17, false)]
    [Arguments((byte)6, true)]
    [Arguments((byte)17, true)]
    public async Task TwelveHoursOfMissingCloseEventsDoNotExhaustHistory(byte protocol, bool ipv6)
    {
        var history = new RouteSocketHistory();
        var persistent = Open(1, protocol: protocol) with { Flow = PacketTests.Flow(ipv6) with { Protocol = protocol } };
        var live = Presence(persistent);
        history.Update([persistent], Second, isPresent: live.Contains);
        // A long-lived process creates 120 endpoints per minute whose CLOSE
        // notifications never arrive. The VPN endpoint stays present throughout.
        for (var minute = 1; minute <= 12 * 60; minute++)
        {
            var now = minute * 60 * Second;
            var events = Enumerable.Range(0, 120).Select(i => Open(now, (ulong)(minute * 120 + i + 2), protocol) with
            { Flow = persistent.Flow with { LocalPort = (ushort)(20000 + i) } });
            history.Update(events, now, isPresent: live.Contains);
            history.Update([], now + 11 * Second, isPresent: live.Contains);
            await history.Count.Should().BeEqualTo(1);
        }
        await history.Snapshot((_, _) => Selected).Find(persistent.Flow, 0)!.Endpoint.Should().BeEqualTo(1UL);
    }

    [Test]
    public async Task TemporaryTableAbsenceDoesNotChangeAnActiveEndpoint()
    {
        var history = new RouteSocketHistory();
        var socket = Open(1);
        history.Update([socket], Second, isPresent: _ => false);
        history.Update([], 10 * Second, isPresent: _ => false);
        history.Update([], 11 * Second, isPresent: Presence(socket).Contains);
        history.Update([], 20 * Second, isPresent: _ => false);
        await history.Count.Should().BeEqualTo(1);
        await history.Snapshot((_, _) => Selected).Find(socket.Flow, 0)!.Endpoint.Should().BeEqualTo(1UL);
    }

    [Test]
    public async Task ReconciliationDoesNotDiscardRecentCloseOrMergeSharedSockets()
    {
        var history = new RouteSocketHistory();
        var socket = Open(1);
        history.Update([socket, Open(2, 2)], Second, isPresent: Presence(socket).Contains);
        await history.Snapshot((_, _) => Selected).Find(socket.Flow, 0)!.Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
        history.Update([Open(2 * Second, 2) with { Event = 7 }], 3 * Second, isPresent: Presence(socket).Contains);
        var snapshot = history.Snapshot((_, _) => Selected);
        await snapshot.Find(socket.Flow, 0)!.Endpoint.Should().BeEqualTo(1UL);
        await snapshot.Find(socket.Flow, Second)!.Kind.Should().BeEqualTo(RouteDecisionKind.Ambiguous);
    }

    [Test]
    public async Task PidReuseCannotCoalesceDifferentProcessGenerations()
    {
        var history = new RouteSocketHistory();
        var socket = Open(1);
        history.Update([socket], Second);
        history.Update([Open(3 * Second)], 3 * Second, (_, at) => at < 2 * Second ? 2 * Second : null);
        await history.Count.Should().BeEqualTo(2);
        var snapshot = history.Snapshot((_, at) => Selected with { Process = new(20, at < 2 * Second ? 1 : 2) });
        await snapshot.Find(socket.Flow, 0)!.Process.Should().BeEqualTo(new RouteProcessKey(20, 2));
        await snapshot.Find(socket.Flow, Second)!.Process.Should().BeEqualTo(new RouteProcessKey(20, 1));
    }

    [Test]
    public async Task ReconciliationRequiresMatchingPidFamilyAndTcpPeer()
    {
        var socket = Open(1, protocol: 6);
        await Presence(socket).Contains(socket).Should().BeTrue();
        await Presence(socket).Contains(socket with { Pid = 30 }).Should().BeFalse();
        await Presence(socket).Contains(socket with { Flow = socket.Flow with { RemotePort = 1 } }).Should().BeFalse();
        await Presence(socket).Contains(socket with { Flow = socket.Flow with { LocalAddress = IPAddress.IPv6Any } }).Should().BeFalse();
        var udp = socket with { Flow = socket.Flow with { Protocol = 17 } };
        await Presence(udp).Contains(udp with { Flow = udp.Flow with { RemotePort = 1 } }).Should().BeTrue();
        var link = IPAddress.Parse("fe80::1%3");
        var scoped = new RouteSocketPresence([], [new(link, socket.Flow.LocalPort, null, 0, socket.Pid)]);
        await scoped.Contains(udp with { Flow = udp.Flow with { LocalAddress = IPAddress.Parse("fe80::1") } }).Should().BeTrue();
    }
}
