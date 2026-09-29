using System.Threading.Channels;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class UdpRecoveryTests
{
    [Test]
    public async Task StaleSnapshotSuppressesRepliesWithoutInvalidatingTheSocket()
    {
        var flow = PacketTests.Flow(false);
        var selected = new RouteDecision(RouteDecisionKind.Selected, new(10, 100), RouteTestFactory.Target());
        RouteAttributionSnapshot Snapshot(long at) => new([], [new(flow.LocalAddress, flow.LocalPort, null, 0, 10)], _ => selected, at);
        var snapshot = Snapshot(1000);
        var now = 1000L;
        var owner = new RouteFlowOwner(selected, () => snapshot.FindFresh(flow, now));
        await owner.IsCurrent().Should().BeTrue();
        now = 1500;
        await owner.IsCurrent().Should().BeTrue();
        now = 1501;
        await owner.IsCurrent().Should().BeFalse();
        await owner.IsInvalidated.Should().BeFalse();
        snapshot = Snapshot(now);
        await owner.IsCurrent().Should().BeTrue();
        await snapshot.FindFresh(flow, now, arrived: now + 1).Should().BeNull();
    }

    [Test]
    [Arguments("process")]
    [Arguments("pid-generation")]
    [Arguments("endpoint")]
    [Arguments("table-fallback")]
    [Arguments("rule")]
    [Arguments("closed")]
    [Arguments("shared")]
    [Arguments("unselected")]
    public async Task FreshOwnershipMismatchNeverRevivesAnOldAssociation(string change)
    {
        var selected = new RouteDecision(RouteDecisionKind.Selected, new(10, 100), RouteTestFactory.Target(), 5);
        RouteDecision? current = selected;
        var owner = new RouteFlowOwner(selected, () => current);
        await owner.IsCurrent().Should().BeTrue();
        current = null; // A snapshot can be late immediately before a real ownership change.
        await owner.IsCurrent().Should().BeFalse();
        current = change switch
        {
            "process" => selected with { Process = new(20, 100) },
            "pid-generation" => selected with { Process = new(10, 200) },
            "endpoint" => selected with { Endpoint = 6 },
            "table-fallback" => selected with { Endpoint = 0 },
            "rule" => selected with { Rule = RouteTestFactory.Target() },
            "closed" => RouteDecision.Unresolved,
            "shared" => new(RouteDecisionKind.Ambiguous),
            _ => RouteDecision.Unselected
        };
        await owner.IsCurrent().Should().BeFalse();
        await owner.IsInvalidated.Should().BeTrue();
        current = selected;
        await owner.IsCurrent().Should().BeFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task UdpRepliesWaitForFreshAttributionAndRetireAfterOwnershipChange(bool ipv6, bool invalidate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        tcp.Start();
        var destination = new IPEndPoint(PacketTests.Flow(ipv6).RemoteAddress, 1194);
        var target = RouteTestFactory.Target(((IPEndPoint)tcp.LocalEndpoint).Port);
        var selected = new RouteDecision(RouteDecisionKind.Selected, new(10, 100), target, 7);
        RouteDecision? current = selected;
        var owner = new RouteFlowOwner(selected, () => Volatile.Read(ref current));
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentWhileWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new RouteAttributionUpdates();
        var replies = Channel.CreateUnbounded<byte[]>();
        var errors = new ConcurrentQueue<Exception>();
        var pool = new TrackingBytePool();
        var associationSources = new List<IPEndPoint>();
        var server = Task.Run(async () =>
        {
            for (var association = 0; association < (invalidate ? 2 : 1); association++)
            {
                using var client = await tcp.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                await stream.ReadExactlyAsync(new byte[3], timeout.Token);
                await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
                await stream.ReadExactlyAsync(new byte[10], timeout.Token);
                await stream.WriteAsync(new byte[] { 5, 0, 0 }.Concat(RouteConnector.EncodeAddress((IPEndPoint)udp.Client.LocalEndPoint!)).ToArray(), timeout.Token);
                // The expected exchange count gates teardown: no terminator packet
                // can be lost when ownership changes or the association is cancelled.
                var packets = invalidate ? association == 0 ? 2 : 100 : 103;
                IPEndPoint? source = null;
                for (var i = 0; i < packets; i++)
                {
                    var datagram = await udp.ReceiveAsync(timeout.Token);
                    source ??= datagram.RemoteEndPoint;
                    await datagram.RemoteEndPoint.Should().BeEqualTo(source);
                    var offset = RouteConnector.UnwrapDatagram(datagram.Buffer, destination);
                    await (offset >= 0).Should().BeTrue();
                    await udp.SendAsync(datagram.Buffer, source, timeout.Token);
                    if (!invalidate && i == 2) { sentWhileWaiting.TrySetResult(); }
                }
                associationSources.Add(source!);
                try { await (await stream.ReadAsync(new byte[1], timeout.Token)).Should().BeEqualTo(0); }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
            }
        });

        RouteUdpSession Create(RouteFlowOwner lifetime) => new(target, destination,
            (_, bytes) => replies.Writer.TryWrite(bytes.ToArray()), timeout.Token, errors.Enqueue,
            () => { var valid = lifetime.IsCurrent(); if (!valid) { rejected.TrySetResult(); } return valid; },
            pool, canSend: () => true, canReuse: () => !lifetime.IsInvalidated,
            waitForOwner: token => lifetime.WaitForCurrentAsync(updates, () => refreshRequested.TrySetResult(), token));
        var session = Create(owner);
        async Task RoundTrip(byte value, int size)
        {
            var payload = Enumerable.Range(0, size).Select(i => (byte)((i * 31 + value) & 255)).ToArray();
            session.Send(destination, payload);
            await (await replies.Reader.ReadAsync(timeout.Token)).SequenceEqual(payload).Should().BeTrue();
        }
        try
        {
            await RoundTrip(1, 48);
            // Fresh unresolved ownership is terminal even if the original cache
            // key becomes selected again. A late snapshot alone is recoverable.
            Volatile.Write(ref current, invalidate ? RouteDecision.Unresolved : null);
            session.Send(destination, [2]);
            await rejected.Task.WaitAsync(timeout.Token);
            await replies.Reader.TryRead(out _).Should().BeFalse();
            await session.IsUsable.Should().BeEqualTo(!invalidate);
            if (!invalidate)
            {
                await refreshRequested.Task.WaitAsync(timeout.Token);
                session.Send(destination, [3]);
                await sentWhileWaiting.Task.WaitAsync(timeout.Token);
                await replies.Reader.TryRead(out _).Should().BeFalse();
            }
            Volatile.Write(ref current, selected);
            updates.Publish();
            if (invalidate)
            {
                await session.IsUsable.Should().BeFalse();
                session.Dispose();
                await session.Completion.WaitAsync(timeout.Token);
                session = Create(new RouteFlowOwner(selected, () => Volatile.Read(ref current)));
            }
            else
            {
                // The original reply survives the stale snapshot without a resend.
                // Subsequent traffic stayed in the bounded socket queue, in order.
                await (await replies.Reader.ReadAsync(timeout.Token)).SequenceEqual(new byte[] { 2 }).Should().BeTrue();
                await (await replies.Reader.ReadAsync(timeout.Token)).SequenceEqual(new byte[] { 3 }).Should().BeTrue();
            }
            // Keepalive-sized and data-sized packets keep the same association after recovery.
            for (var i = 0; i < 100; i++) { await RoundTrip((byte)(3 + i), i % 2 == 0 ? 48 : 1400); }
            session.Dispose();
            await session.Completion.WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            await errors.IsEmpty.Should().BeTrue();
            await associationSources.Count.Should().BeEqualTo(invalidate ? 2 : 1);
            await pool.Outstanding.Should().BeEqualTo(0);
        }
        finally
        {
            session.Dispose();
            timeout.Cancel();
            await session.Completion;
            try { await server; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }
}
