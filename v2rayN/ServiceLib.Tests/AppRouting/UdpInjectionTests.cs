using System.ComponentModel;
using System.Threading.Channels;
using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class UdpInjectionTests
{
    [Test]
    public async Task OneRejectedReplyDoesNotReplaceTheAssociationOrDiscardLaterDatagrams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        tcp.Start();
        var destination = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 1194);
        var errors = new ConcurrentQueue<Exception>();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = Channel.CreateUnbounded<byte[]>();
        var server = Task.Run(async () =>
        {
            using var client = await tcp.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            await stream.ReadExactlyAsync(new byte[3], timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            await stream.ReadExactlyAsync(new byte[10], timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 0, 0 }.Concat(RouteConnector.EncodeAddress((IPEndPoint)udp.Client.LocalEndPoint!)).ToArray(), timeout.Token);
            IPEndPoint? source = null;
            for (var i = 0; i < 3; i++)
            {
                var datagram = await udp.ReceiveAsync(timeout.Token);
                source ??= datagram.RemoteEndPoint;
                await datagram.RemoteEndPoint.Should().BeEqualTo(source);
                await udp.SendAsync(datagram.Buffer, source, timeout.Token);
            }
            try { await (await stream.ReadAsync(new byte[1], timeout.Token)).Should().BeEqualTo(0); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
        });
        var attempted = 0;
        using var session = new RouteUdpSession(RouteTestFactory.Target(((IPEndPoint)tcp.LocalEndpoint).Port), destination,
            (_, payload) =>
            {
                if (Interlocked.Increment(ref attempted) == 1)
                {
                    rejected.TrySetResult();
                    throw new Win32Exception(592); // Native packet injection rejected one datagram.
                }
                replies.Writer.TryWrite(payload.ToArray());
            }, timeout.Token, errors.Enqueue, () => true);
        try
        {
            session.Send(destination, new byte[1536]);
            await rejected.Task.WaitAsync(timeout.Token);
            await session.IsUsable.Should().BeTrue();
            foreach (var size in new[] { 48, 1536 })
            {
                var payload = Enumerable.Range(0, size).Select(i => (byte)(i & 255)).ToArray();
                session.Send(destination, payload);
                await (await replies.Reader.ReadAsync(timeout.Token)).SequenceEqual(payload).Should().BeTrue();
            }
            await errors.Count.Should().BeEqualTo(1);
        }
        finally
        {
            session.Dispose();
            await session.Completion.WaitAsync(timeout.Token);
            timeout.Cancel();
            try { await server; } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }
}
