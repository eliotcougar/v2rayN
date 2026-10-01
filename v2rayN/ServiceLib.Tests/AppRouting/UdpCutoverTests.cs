using ServiceLib.Services.AppRouting;
using static ServiceLib.Tests.AppRouting.InterfaceTests;

namespace ServiceLib.Tests.AppRouting;

public class UdpCutoverTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheSameApplicationSocketSendsThroughTheReplacementProfile(bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var flow = PacketTests.Flow(ipv6) with { RemotePort = 1194 };
        var payload = Enumerable.Range(0, 1536).Select(i => (byte)(i & 255)).ToArray();
        var packet = RoutePacket.CreateUdpReply(flow with
        { LocalAddress = flow.RemoteAddress, LocalPort = flow.RemotePort, RemoteAddress = flow.LocalAddress, RemotePort = flow.LocalPort }, payload);
        var proxies = new List<Proxy>();
        var engine = new AppRouteEngine(_ => { });
        InitializeEngine(engine);
        try
        {
            RouteUdpSession? previous = null;
            for (var generation = 0; generation < 3; generation++)
            {
                var proxy = new Proxy(timeout.Token);
                proxies.Add(proxy);
                var policy = new RouteSharedPolicy(new(new RoutingItem { RuleSet = JsonUtils.Serialize(new[] { new RulesItem
                { OutboundTag = Global.ProxyTag, Process = ["fixture.exe"] } }) }),
                    (_, _) => Task.FromResult(new RouteSocksEndpoint(proxy.Port)));
                var target = policy.Select([new(new(10, 1), 0, "fixture.exe", null)])!;
                await engine.ApplyAsync(policy, [], timeout.Token);
                if (previous != null) { await previous.Completion.WaitAsync(timeout.Token); }
                var owner = new RouteDecision(RouteDecisionKind.Selected, new(10, 1), target, 7);
                // The deterministic owner snapshot substitutes only Windows table input.
                // Capture, policy replacement and actual localhost SOCKS sockets are production code.
                var snapshot = new RouteAttributionSnapshot([], [new(flow.LocalAddress, flow.LocalPort, null, 0, 10)],
                    _ => owner, Environment.TickCount64);
                typeof(AppRouteEngine).GetMethod("PublishRouting", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(engine, [new RoutePolicy(policy, []), snapshot]);
                Capture(engine, packet.ToArray(), new() { InterfaceIndex = 7 }, new RoutePacketBatch((_, _) => { }));
                var frame = await proxy.Received.Task.WaitAsync(timeout.Token);
                var offset = RouteConnector.UnwrapDatagram(frame, out var destination);
                await destination.Should().BeEqualTo(new IPEndPoint(flow.RemoteAddress, flow.RemotePort));
                await frame.AsSpan(offset).SequenceEqual(payload).Should().BeTrue();
                previous = Field<System.Collections.IDictionary>(engine, "_udp").Values.Cast<RouteUdpSession>().Single();
            }
        }
        finally
        {
            await engine.DisposeAsync();
            timeout.Cancel();
            foreach (var proxy in proxies) { await proxy.DisposeAsync(); }
        }
    }

    private sealed class Proxy : IAsyncDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationToken _token;
        private readonly Task _server;
        public int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;
        public TaskCompletionSource<byte[]> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Proxy(CancellationToken token)
        {
            _token = token;
            _tcp.Start();
            _server = Serve();
        }

        private async Task Serve()
        {
            using var client = await _tcp.AcceptTcpClientAsync(_token);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[3], _token);
            await stream.WriteAsync(new byte[] { 5, 0 }, _token);
            await stream.ReadExactlyAsync(new byte[10], _token);
            await stream.WriteAsync(new byte[] { 5, 0, 0 }.Concat(RouteConnector.EncodeAddress((IPEndPoint)_udp.Client.LocalEndPoint!)).ToArray(), _token);
            var packet = await _udp.ReceiveAsync(_token);
            Received.TrySetResult(packet.Buffer);
            // The fixture needs no native reply injection. It verifies that outgoing
            // datagrams keep their application endpoint while changing SOCKS profiles.
            try { await (await stream.ReadAsync(new byte[1], _token)).Should().BeEqualTo(0); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        }

        public async ValueTask DisposeAsync()
        {
            _tcp.Stop();
            _udp.Dispose();
            try { await _server; } catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        }
    }
}
