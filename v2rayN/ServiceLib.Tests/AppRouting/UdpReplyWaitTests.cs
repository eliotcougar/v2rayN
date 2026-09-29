using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class UdpReplyWaitTests
{
    private static RouteDecision Selected() => new(RouteDecisionKind.Selected, new(10, 100), RouteTestFactory.Target(), 7);

    [Test]
    public async Task FreshOwnershipDoesNotRequestOrWaitForAnUpdate()
    {
        var selected = Selected();
        var owner = new RouteFlowOwner(selected, () => selected);
        var wait = owner.WaitForCurrentAsync(new(), () => throw new InvalidOperationException("Unexpected refresh"), CancellationToken.None);
        await wait.IsCompletedSuccessfully.Should().BeTrue();
        await (await wait).Should().BeTrue();
    }

    [Test]
    public async Task OneSnapshotWakesAllWaitingAssociations()
    {
        var selected = Selected();
        RouteDecision? current = null;
        var updates = new RouteAttributionUpdates();
        var waits = Enumerable.Range(0, 3).Select(_ => new RouteFlowOwner(selected, () => Volatile.Read(ref current))
            .WaitForCurrentAsync(updates, () => { }, CancellationToken.None).AsTask()).ToArray();
        await waits.All(task => !task.IsCompleted).Should().BeTrue();
        Volatile.Write(ref current, selected);
        updates.Publish();
        await (await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(3))).All(valid => valid).Should().BeTrue();
    }

    [Test]
    public async Task PublicationDuringOwnershipCheckIsNotMissed()
    {
        var selected = Selected();
        var updates = new RouteAttributionUpdates();
        var firstRead = true;
        var owner = new RouteFlowOwner(selected, () =>
        {
            if (!firstRead) { return selected; }
            firstRead = false;
            updates.Publish();
            return null; // The read raced with publication of the fresh snapshot.
        });
        await (await owner.WaitForCurrentAsync(updates, () => { }, CancellationToken.None)).Should().BeTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WaitingExpiresWithoutInvalidatingTheAssociationEvenIfStaleUpdatesContinue(bool keepPublishing)
    {
        var selected = Selected();
        RouteDecision? current = null;
        var owner = new RouteFlowOwner(selected, () => Volatile.Read(ref current));
        var updates = new RouteAttributionUpdates();
        using var stop = new CancellationTokenSource();
        async Task PublishStale()
        {
            try
            {
                while (keepPublishing)
                {
                    await Task.Delay(25, stop.Token);
                    updates.Publish();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        var publishing = PublishStale();
        try
        {
            await (await owner.WaitForCurrentAsync(updates, () => { }, stop.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();
            await owner.IsInvalidated.Should().BeFalse();
            Volatile.Write(ref current, selected);
            await owner.IsCurrent().Should().BeTrue();
        }
        finally { stop.Cancel(); await publishing; }
    }

    [Test]
    [Arguments("process")]
    [Arguments("endpoint")]
    [Arguments("rule")]
    [Arguments("revoked")]
    public async Task ChangedOwnershipDiscardsTheHeldReply(string change)
    {
        var selected = Selected();
        RouteDecision? current = null;
        var owner = new RouteFlowOwner(selected, () => Volatile.Read(ref current));
        var updates = new RouteAttributionUpdates();
        var wait = owner.WaitForCurrentAsync(updates, () => { }, CancellationToken.None).AsTask();
        await wait.IsCompleted.Should().BeFalse();
        Volatile.Write(ref current, change switch
        {
            "process" => selected with { Process = new(10, 200) },
            "endpoint" => selected with { Endpoint = 8 },
            "rule" => selected with { Rule = RouteTestFactory.Target() },
            _ => RouteDecision.Unselected
        });
        updates.Publish();
        await (await wait.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();
        await owner.IsInvalidated.Should().BeTrue();
        Volatile.Write(ref current, selected);
        await owner.IsCurrent().Should().BeFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HeldReplyIsCancelledWhenSessionOrSocksControlCloses(bool closeControl)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        tcp.Start();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new RouteFlowOwner(Selected(), () => null);
        var updates = new RouteAttributionUpdates();
        var errors = new ConcurrentQueue<Exception>();
        var replies = 0;
        var pool = new TrackingBytePool();
        var server = Task.Run(async () =>
        {
            using var client = await tcp.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[3], timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            await stream.ReadExactlyAsync(new byte[10], timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0, 0 }.Concat(RouteConnector.EncodeAddress((IPEndPoint)udp.Client.LocalEndPoint!)).ToArray(), timeout.Token);
            var datagram = await udp.ReceiveAsync(timeout.Token);
            await udp.SendAsync(datagram.Buffer, datagram.RemoteEndPoint, timeout.Token);
            await waiting.Task.WaitAsync(timeout.Token);
            if (closeControl) { return; }
            try { await (await stream.ReadAsync(new byte[1], timeout.Token)).Should().BeEqualTo(0); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        });
        var destination = new IPEndPoint(IPAddress.Loopback, 1194);
        using var session = new RouteUdpSession(RouteTestFactory.Target(((IPEndPoint)tcp.LocalEndpoint).Port), destination,
            (_, _) => Interlocked.Increment(ref replies), timeout.Token, errors.Enqueue, owner.IsCurrent, pool,
            canSend: () => true, canReuse: () => !owner.IsInvalidated,
            waitForOwner: token => owner.WaitForCurrentAsync(updates, () => waiting.TrySetResult(), token));
        try
        {
            session.Send(destination, [1, 2, 3]);
            await waiting.Task.WaitAsync(timeout.Token);
            if (!closeControl) { session.Dispose(); }
            await session.Completion.WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            await replies.Should().BeEqualTo(0);
            await pool.Outstanding.Should().BeEqualTo(0);
            await errors.Count.Should().BeEqualTo(closeControl ? 1 : 0);
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
