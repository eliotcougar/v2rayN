using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class OwnerTableTests
{
    [Test]
    public async Task OwnerModuleTablesPreserveTheCurrentProcessesLoopbackSockets()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = (ushort)((IPEndPoint)client.Client.LocalEndPoint!).Port;
        var udpPort = (ushort)((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var pid = Environment.ProcessId;
        var services = new RouteServiceSnapshot([new("Chosen", "Chosen", pid), new("Other", "Other", pid)],
            [new RouteProcessInfo(new(pid, 1), 0, "ServiceLib.Tests.exe", null)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Chosen" });
        var tcp = RouteServiceOwnerTable.Read(6, AddressFamily.InterNetwork, services);
        var datagrams = RouteServiceOwnerTable.Read(17, AddressFamily.InterNetwork, services);
        await tcp.Any(row => row.Pid == pid && row.Port == tcpPort).Should().BeTrue();
        await datagrams.Any(row => row.Pid == pid && row.Port == udpPort).Should().BeTrue();
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6Listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            ipv6Listener.Start();
            using var ipv6Client = new TcpClient(AddressFamily.InterNetworkV6);
            await ipv6Client.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)ipv6Listener.LocalEndpoint).Port);
            using var ipv6Server = await ipv6Listener.AcceptTcpClientAsync();
            using var ipv6Udp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            var tcp6Port = (ushort)((IPEndPoint)ipv6Client.Client.LocalEndPoint!).Port;
            var udp6Port = (ushort)((IPEndPoint)ipv6Udp.Client.LocalEndPoint!).Port;
            await RouteServiceOwnerTable.Read(6, AddressFamily.InterNetworkV6, services)
                .Any(row => row.Pid == pid && row.Port == tcp6Port).Should().BeTrue();
            await RouteServiceOwnerTable.Read(17, AddressFamily.InterNetworkV6, services)
                .Any(row => row.Pid == pid && row.Port == udp6Port).Should().BeTrue();
        }
        await RouteServiceCatalog.Read().Any(service => service.Name.Length > 0).Should().BeTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DualStackUdpSocketIsFoundForItsIpv4Packets(bool connected)
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        var port = (ushort)((IPEndPoint)client.LocalEndPoint!).Port;
        var remotePort = (ushort)((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var destination = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), remotePort);
        if (connected)
        {
            await client.ConnectAsync(destination, timeout.Token);
            await client.SendAsync(new byte[] { 1 }, SocketFlags.None, timeout.Token);
        }
        else
        {
            await client.SendToAsync(new byte[] { 1 }, SocketFlags.None, destination, timeout.Token);
        }
        await server.ReceiveAsync(timeout.Token);
        var flow = new RouteFlow(17, IPAddress.Loopback, port, IPAddress.Loopback, remotePort);
        var snapshot = new RouteAttributionSnapshot([], RouteOwnerTable.Read(17, AddressFamily.InterNetwork),
            pid => new(RouteDecisionKind.Unselected, new(pid, 0)), 0);
        await snapshot.Find(flow).Process!.Value.Pid.Should().BeEqualTo(Environment.ProcessId);
    }

    [Test]
    public async Task DualStackTcpSocketIsFoundForItsIpv4Packets()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback.MapToIPv6(), remotePort), timeout.Token);
        using var server = await listener.AcceptSocketAsync(timeout.Token);
        var port = (ushort)((IPEndPoint)client.LocalEndPoint!).Port;
        var flow = new RouteFlow(6, IPAddress.Loopback, port, IPAddress.Loopback, remotePort);
        var snapshot = new RouteAttributionSnapshot(RouteOwnerTable.Read(6, AddressFamily.InterNetwork), [],
            pid => new(RouteDecisionKind.Unselected, new(pid, 0)), 0);
        await snapshot.Find(flow).Process!.Value.Pid.Should().BeEqualTo(Environment.ProcessId);
    }
}
