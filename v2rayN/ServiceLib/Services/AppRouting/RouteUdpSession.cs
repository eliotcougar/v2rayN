using System.Buffers;
using System.Threading.Channels;

namespace ServiceLib.Services.AppRouting;

internal sealed class RouteUdpSession : IDisposable
{
    // The payload borrows the receive buffer and is valid only during the callback.
    internal delegate void Reply(IPEndPoint peer, ReadOnlySpan<byte> payload);
    private const int MaxQueuedBytes = 64 * 1024;
    private readonly CancellationTokenSource _stop;
    private readonly record struct Datagram(byte[] Bytes, int Length, int PayloadLength);
    private readonly ArrayPool<byte> _buffers;
    private readonly Channel<Datagram> _queue = Channel.CreateBounded<Datagram>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private int _queuedBytes;
    private readonly RouteTarget _rule;
    public RouteTarget Rule => _rule;
    private readonly IPEndPoint _destination;
    private readonly Reply _reply;
    private readonly Func<bool> _ownsFlow;
    private readonly Func<bool> _canSend;
    private readonly Func<bool> _canReuse;
    private Socket? _socket;
    private Socket? _control;
    public Task Completion
    {
        get;
    }
    // A permanently invalid reply owner must not leave an outbound-only session
    // cached forever. Already-attributed queued packets still use _canSend below.
    public bool IsUsable => !Completion.IsCompleted && _canSend() && _canReuse();
    private long _lastActivity = Environment.TickCount64;
    public long LastActivity => Interlocked.Read(ref _lastActivity);

    public RouteUdpSession(RouteTarget rule, IPEndPoint destination, Reply reply, CancellationToken token, Action<Exception> error,
        Func<bool> ownsFlow, ArrayPool<byte>? buffers = null, Func<bool>? canSend = null, Func<bool>? canReuse = null)
    {
        _rule = rule;
        _destination = destination;
        _reply = reply;
        _ownsFlow = ownsFlow;
        _canSend = canSend ?? ownsFlow;
        _canReuse = canReuse ?? (() => true);
        _buffers = buffers ?? ArrayPool<byte>.Shared;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Completion = Run(error);
    }

    // Engine packet processing serializes producers. TryWrite never waits for capacity.
    public void Send(IPEndPoint destination, ReadOnlySpan<byte> payload)
    {
        if (!IsUsable)
        {
            Dispose();
            return;
        }
        Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        if (Volatile.Read(ref _queuedBytes) + payload.Length > MaxQueuedBytes)
        {
            return;
        }
        var header = RouteConnector.DatagramHeaderLength(destination);
        var bytes = _buffers.Rent(header + payload.Length);
        var queued = false;
        try
        {
            RouteConnector.WriteDatagram(bytes, destination, payload);
            Interlocked.Add(ref _queuedBytes, payload.Length);
            queued = _queue.Writer.TryWrite(new(bytes, header + payload.Length, payload.Length));
            if (!queued) { Interlocked.Add(ref _queuedBytes, -payload.Length); }
        }
        finally { if (!queued) { _buffers.Return(bytes); } }
    }

    private async Task Run(Action<Exception> error)
    {
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            _control = await RouteConnector.ConnectProxy(_rule, connectTimeout.Token);
            var local = ((IPEndPoint)_control.LocalEndPoint!).Address;
            if (local.IsIPv4MappedToIPv6)
            {
                local = local.MapToIPv4();
            }

            _socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(local, 0));
            var relay = await RouteConnector.Request(_control, 3, (IPEndPoint)_socket.LocalEndPoint!, connectTimeout.Token);
            if (relay is IPEndPoint ipRelay && ipRelay.AddressFamily != _socket.AddressFamily)
            {
                throw new IOException("Invalid SOCKS5 UDP relay endpoint.");
            }

            await _socket.ConnectAsync(relay, connectTimeout.Token);
            var receive = Receive();
            var send = SendLoop(error);
            var control = WatchControl();
            try
            {
                var completed = await Task.WhenAny(receive, send, control);
                await completed; // Observe the first worker's outcome before stopping the others.
            }
            finally
            {
                _stop.Cancel();
                _socket.Dispose();
                _control?.Dispose();
                try
                {
                    await Task.WhenAll(receive, send, control);
                }
                catch (Exception) when (_stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { error(new TimeoutException("UDP route connection timed out.")); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { error(ex); }
        finally
        {
            _socket?.Dispose();
            _control?.Dispose();
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var pending))
            {
                Interlocked.Add(ref _queuedBytes, -pending.PayloadLength);
                _buffers.Return(pending.Bytes);
            }
            _stop.Dispose();
        }
    }

    private async Task WatchControl()
    {
        var data = new byte[1];
        await _control!.ReceiveAsync(data, SocketFlags.None, _stop.Token);
        throw new IOException("SOCKS5 UDP association closed.");
    }

    private async Task SendLoop(Action<Exception> error)
    {
        await foreach (var datagram in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            Interlocked.Add(ref _queuedBytes, -datagram.PayloadLength);
            try
            {
                // Capture already attributed these datagrams. A sender may exit during
                // proxy setup; rule/interface revocation still cancels sending. Replies
                // always require current socket ownership through the separate predicate.
                if (!_canSend()) { return; }
                var bytes = datagram.Bytes.AsMemory(0, datagram.Length);
                await _socket!.SendAsync(bytes, SocketFlags.None, _stop.Token);
            }
            // MessageSize rejects this datagram without damaging the association.
            // Other socket errors still end the session through Run's supervision.
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { error(ex); }
            finally { _buffers.Return(datagram.Bytes); }
        }
    }

    private async Task Receive()
    {
        var bytes = new byte[65535];
        while (!_stop.IsCancellationRequested)
        {
            var count = await _socket!.ReceiveAsync(bytes, SocketFlags.None, _stop.Token);
            var offset = RouteConnector.UnwrapDatagram(bytes.AsSpan(0, count), out var peer);
            if (offset < 0 || peer?.AddressFamily != _destination.AddressFamily)
            {
                continue;
            }

            if (!_ownsFlow())
            {
                // A late reply must not cancel already-attributed outbound datagrams.
                if (!_canSend()) { return; }
                continue;
            }

            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            _reply(peer, bytes.AsSpan(offset, count - offset));
        }
    }

    public void Dispose()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException) { }
        _socket?.Dispose();
        _control?.Dispose();
    }
}
