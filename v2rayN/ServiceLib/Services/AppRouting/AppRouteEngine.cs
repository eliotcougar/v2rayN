using System.Buffers;
using System.Buffers.Binary;

namespace ServiceLib.Services.AppRouting;

/// <summary>
/// Windows outbound TCP reflection and UDP relay for applications selected by routing rules.
/// WinDivert's streamdump example documents the TCP reflection technique; no TLS interception is used.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AppRouteEngine : IRouteEngine
{
    private readonly CancellationTokenSource _stop = new();
    private readonly RouteAttributionSource _attribution = new();
    private readonly RoutePendingPackets _pending = new();
    private readonly SemaphoreSlim _refreshRequest = new(0, 1);
    private readonly RouteAttributionUpdates _attributionUpdates = new();
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Routing(RoutePolicy Policy, RouteAttributionSnapshot Owners);
    private Routing _routing;
    private readonly RouteNatTable _nat = new();
    private readonly RouteFragmentBuffer _fragments;
    private readonly Func<RouteInterfacePolicy> _getInterfaces;
    private RouteInterfacePolicy _interfaces = RouteInterfacePolicy.All;
    private readonly record struct UdpEndpoint(RouteProcessKey Process, IPAddress Local, ushort Port, string RuleId, uint Interface, ulong Endpoint);
    private readonly ConcurrentDictionary<UdpEndpoint, RouteUdpSession> _udp = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly List<Socket> _listeners = [];
    private readonly Dictionary<AddressFamily, ushort> _ports = [];
    private readonly List<Task> _workers = [];
    private readonly Action<string> _error;
    private readonly object _sendGate = new();
    private readonly object _packetGate = new();
    private IntPtr _handle;
    private long _connectionId;
    private long _lastError;
    private int _waitingForOwner;
    private int _stopping;
    public Task<Exception?> Completion => _completion.Task;

    public AppRouteEngine(Action<string> error,
        Func<RouteInterfacePolicy>? interfaces = null)
    {
        _routing = new(new(null, []), new([], [], _ => RouteDecision.Unresolved, 0));
        _error = error;
        _getInterfaces = interfaces ?? (() => RouteInterfacePolicy.All);
        _fragments = new((address, protocol, local, remote) =>
            (!Monitors(address, local.AddressFamily) || _interfaces.BypassesLocalTraffic(address.InterfaceIndex, remote)) &&
            // Destination Options can conceal a TCP header until reassembly.
            (protocol is not (6 or 60) || !_nat.MayBeReflection(local, remote)));
    }

    private bool Monitors(DivertAddress address, AddressFamily family) =>
        _interfaces.Monitors(address.InterfaceIndex, family == AddressFamily.InterNetworkV6);

    private void RefreshInterfaces()
    {
        var next = _getInterfaces();
        if (ReferenceEquals(next, _interfaces)) { return; }
        var previous = _interfaces;
        _interfaces = next;
        bool Retain(uint index, bool ipv6) => next.Retains(previous, index, ipv6);
        _fragments.RetainInterfaces(Retain);
        for (var remaining = _pending.Count; remaining > 0; remaining--)
        {
            var packet = _pending.Dequeue();
            if (Retain(packet.Address.InterfaceIndex, packet.Bytes[0] >> 4 == 6)) { _pending.Add(packet); }
        }
        ResetTcpConnections(_nat.Retain(_routing.Policy, e => next.Retains(previous, e.OriginalAddress.InterfaceIndex,
            e.Flow.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)));
        foreach (var pair in _udp.Where(p => !next.Retains(previous, p.Key.Interface,
                     p.Key.Local.AddressFamily == AddressFamily.InterNetworkV6)).ToArray())
        {
            if (_udp.TryRemove(pair)) { pair.Value.Dispose(); }
        }
    }

    public async Task ApplyAsync(RouteSharedPolicy routes, IEnumerable<int> excludedProcesses, CancellationToken token)
    {
        var policy = new RoutePolicy(routes, excludedProcesses);
        var owners = await Task.Run(() => _attribution.Read(policy));
        lock (_packetGate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            PublishRouting(policy, owners);
            ResetTcpConnections(_nat.Retain(policy));
            foreach (var pair in _udp.Where(p => !policy.Retains(p.Value.Rule)).ToArray())
            {
                if (_udp.TryRemove(pair))
                { pair.Value.Dispose(); }
            }
        }
    }

    public void Start()
    {
        // Observe lifecycle events before opening NETWORK, including processes
        // that run entirely between the seed snapshot and the first captured packet.
        _attribution.StartEvents(() => { if (Volatile.Read(ref _waitingForOwner) != 0) { RequestRefresh(); } });
        PublishRouting(_routing.Policy, _attribution.Read(_routing.Policy, flushEvents: true));
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            {
                continue;
            }

            var listener = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            _listeners.Add(listener);
            if (family == AddressFamily.InterNetworkV6)
            {
                listener.DualMode = false;
            }

            listener.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0));
            listener.Listen(128);
            _ports[family] = (ushort)((IPEndPoint)listener.LocalEndPoint!).Port;
        }
        // NETWORK has no PID field; resolve new flows against the complete Windows owner tuple.
        // Include non-initial fragments, which have no transport header for the tcp/udp filter.
        _handle = WinDivertApi.WinDivertOpen("outbound and !loopback and (tcp or udp or fragment)", 0, 100, 0);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            _handle = IntPtr.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        foreach (var listener in _listeners)
        {
            _workers.Add(Accept(listener.AcceptAsync));
        }

        _workers.Add(Task.Factory.StartNew(Capture, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        _workers.Add(Cleanup());
        _workers.Add(Task.Run(WatchAttribution));
    }

    private async Task WatchAttribution()
    {
        var lastRead = Environment.TickCount64;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _refreshRequest.WaitAsync(100, _stop.Token);
                // Coalesce bursts of new flows instead of letting unknown packets drive a busy loop.
                var delay = 25 - (Environment.TickCount64 - lastRead);
                if (delay > 0) { await Task.Delay((int)delay, _stop.Token); }
                Routing previous;
                bool flush;
                lock (_packetGate)
                { previous = _routing; flush = _pending.Count != 0; }
                var snapshot = _attribution.Read(previous.Policy, flush);
                lastRead = Environment.TickCount64;
                lock (_packetGate)
                {
                    if (!ReferenceEquals(previous.Policy, _routing.Policy))
                    { continue; }
                    PublishRouting(previous.Policy, snapshot);
                    RefreshInterfaces();
                    // Retry each packet once per snapshot, even if it must wait again.
                    for (var remaining = _pending.Count; remaining > 0; remaining--)
                    {
                        var packet = _pending.Dequeue();
                        try
                        { Process(packet.Bytes, packet.Address, packet.Fragments, packet); }
                        catch (Exception ex) { Report(ex); }
                    }
                    Volatile.Write(ref _waitingForOwner, _pending.Count);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void PublishRouting(RoutePolicy policy, RouteAttributionSnapshot owners)
    {
        Volatile.Write(ref _routing, new(policy, owners));
        _attributionUpdates.Publish();
    }

    private void RequestRefresh()
    {
        // Event callbacks never take the packet lock. Coalesce their wakeups with
        // requests from capture; observers stop before this semaphore is disposed.
        lock (_refreshRequest)
        {
            if (!_stop.IsCancellationRequested && _refreshRequest.CurrentCount == 0) { _refreshRequest.Release(); }
        }
    }

    private RouteDecision Match(RouteFlow flow, long arrived = 0, bool requireFreshSnapshot = false, long timestamp = 0)
    {
        var snapshot = Volatile.Read(ref _routing).Owners;
        return snapshot.FindFresh(flow, Environment.TickCount64, requireFreshSnapshot ? arrived : 0, timestamp)
            ?? RouteDecision.Unresolved;
    }

    private void Capture()
    {
        try
        {
            var bytes = new byte[RoutePacketBatch.MaxPacketLength];
            var addresses = new DivertAddress[RoutePacketBatch.Capacity];
            var lengths = new int[RoutePacketBatch.Capacity];
            var output = new RoutePacketBatch(SendBatch);
            while (!_stop.IsCancellationRequested)
            {
                var addressLength = (uint)(addresses.Length * RoutePacketBatch.AddressSize);
                if (!WinDivertApi.WinDivertRecvEx(_handle, bytes, (uint)bytes.Length, out var count,
                    0, addresses, ref addressLength, IntPtr.Zero))
                {
                    if (_stop.IsCancellationRequested)
                    {
                        break;
                    }

                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var packetCount = RoutePacketBatch.ReadLengths(bytes.AsSpan(0, checked((int)count)), addressLength, lengths);
                lock (_packetGate)
                {
                    RefreshInterfaces();
                    var offset = 0;
                    for (var i = 0; i < packetCount && !_stop.IsCancellationRequested; i++)
                    {
                        var packet = bytes.AsMemory(offset, lengths[i]);
                        offset += lengths[i];
                        var address = addresses[i];
                        try { ProcessCapturedPacket(packet, address, output); }
                        catch (Exception ex) { Report(ex); } // A failed selected packet never falls back to direct routing.
                    }
                    // Flush available packets immediately, including a partially filled batch.
                    try { output.Flush(); }
                    catch (Exception ex) { Report(ex); }
                }
            }
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            // Never leave an unserviced capture handle blocking unrelated applications.
            lock (_sendGate)
            {
                if (_handle != IntPtr.Zero)
                {
                    WinDivertApi.WinDivertClose(_handle);
                }

                _handle = IntPtr.Zero;
            }
        }
    }

    private void ProcessCapturedPacket(Memory<byte> bytes, DivertAddress address, RoutePacketBatch output)
    {
        if (!_fragments.Add(bytes.Span, address, out var fragments))
        {
            Process(bytes, address, output: output);
            return;
        }
        if (fragments == null) { return; } // Buffered or discarded fragment; nothing to forward.
        if (fragments.PassThrough)
        {
            foreach (var original in fragments.Originals)
            {
                Send(original.Packet, original.Address, checksum: false, output);
            }
            return;
        }
        Process(fragments.Packet, fragments.Address, fragments.Originals, output: output);
    }

    private void Process(Memory<byte> bytes, DivertAddress address,
        List<(byte[] Packet, DivertAddress Address)>? fragments = null, RoutePendingPackets.Packet? pending = null, RoutePacketBatch? output = null)
    {
        var arrived = pending?.Arrived ?? Environment.TickCount64;
        bool DeferIfUnresolved(RouteDecision decision, RouteFlow flow)
        {
            if (decision.Kind is not (RouteDecisionKind.Unresolved or RouteDecisionKind.Ambiguous))
            { return false; }
            if (decision.Kind == RouteDecisionKind.Ambiguous)
            { throw new IOException($"Ambiguous shared endpoint; {(flow.Protocol == 6 ? "TCP" : "UDP")} {new IPEndPoint(flow.LocalAddress, flow.LocalPort)} -> {new IPEndPoint(flow.RemoteAddress, flow.RemotePort)} blocked."); }
            var withinDeadline = Environment.TickCount64 - arrived < RoutePendingPackets.WaitMilliseconds;
            var queued = withinDeadline && (pending == null
                ? _pending.Add(bytes.Span, address, arrived, fragments)
                : _pending.Add(pending));
            if (!queued)
            {
                throw new IOException("Could not identify the application owning a packet; packet blocked.");
            }
            Volatile.Write(ref _waitingForOwner, _pending.Count);
            if (pending == null)
            { RequestRefresh(); }
            return true;
        }
        void PassThrough()
        {
            if (fragments == null)
            {
                Send(bytes, address, checksum: false, output);
            }
            else
            {
                foreach (var fragment in fragments)
                {
                    Send(fragment.Packet, fragment.Address, checksum: false, output);
                }
            }
        }
        var packet = RoutePacket.Parse(bytes.Span, address.InterfaceIndex);
        if (packet == null)
        {
            PassThrough();
            return;
        }
        var flow = packet.Flow;
        if (flow.Protocol == 6 && _ports.TryGetValue(flow.LocalAddress.AddressFamily, out var listenerPort))
        {
            if (flow.LocalPort == listenerPort)
            {
                var reverse = _nat.Reverse(flow.LocalAddress, flow.RemoteAddress, flow.RemotePort);
                if (reverse == null)
                {
                    return; // Never let a reflected response escape onto the physical network.
                }

                reverse.LastActivity = Environment.TickCount64;
                packet.Rewrite(bytes.Span, reverse.Flow.RemoteAddress, reverse.Flow.RemotePort, reverse.Flow.LocalAddress, reverse.Flow.LocalPort);
                address.InterfaceIndex = reverse.OriginalAddress.InterfaceIndex;
                address.SubInterfaceIndex = reverse.OriginalAddress.SubInterfaceIndex;
                address.Outbound = false;
                Send(bytes, address, checksum: true, output);
                return;
            }
            var entry = _nat.Find(flow, packet.IsTcpSyn ? packet.TcpSequence : null);
            if (entry?.Retired == true)
            {
                ResetTcpPacket(packet, bytes.Span, address, output);
                return;
            }
            if (!Monitors(address, flow.LocalAddress.AddressFamily)) { PassThrough(); return; }
            if (entry == null)
            {
                if (_interfaces.BypassesLocalTraffic(address.InterfaceIndex, flow)) { PassThrough(); return; }
                // A new connection can reuse a closed tuple. Require a snapshot
                // begun after its SYN before choosing the route for the stream.
                var match = Match(flow, arrived, requireFreshSnapshot: packet.IsTcpSyn, timestamp: address.Timestamp);
                if (DeferIfUnresolved(match, flow))
                { return; }
                if (match.Kind == RouteDecisionKind.Unselected)
                {
                    PassThrough();
                    return;
                }
                // A router restart cannot reconstruct an established TCP stream.
                // Tell the application to reconnect instead of silently blackholing it.
                if (!packet.IsTcpSyn)
                {
                    ResetTcpPacket(packet, bytes.Span, address, output);
                    return;
                }

                entry = _nat.GetOrAdd(flow, match.Rule!, packet.TcpSequence);
                entry.OriginalAddress = address;
            }
            if ((packet.TcpFlags & 16) != 0)
            { entry.ClientAcknowledgement = BinaryPrimitives.ReadUInt32BigEndian(bytes.Span[(packet.TransportOffset + 8)..]); }
            entry.LastActivity = Environment.TickCount64;
            packet.Rewrite(bytes.Span, flow.RemoteAddress, entry.TranslatedPort, flow.LocalAddress, listenerPort);
            address.Outbound = false;
            Send(bytes, address, checksum: true, output);
            return;
        }
        if (flow.Protocol == 17)
        {
            if (!Monitors(address, flow.LocalAddress.AddressFamily) || _interfaces.BypassesLocalTraffic(address.InterfaceIndex, flow))
            { PassThrough(); return; }
            var match = Match(flow, timestamp: address.Timestamp);
            if (DeferIfUnresolved(match, flow))
            { return; }
            if (match.Kind == RouteDecisionKind.Unselected)
            { PassThrough(); return; }
            var key = new UdpEndpoint(match.Process!.Value, flow.LocalAddress, flow.LocalPort, match.Rule!.Id, address.InterfaceIndex, match.Endpoint);
            _udp.TryGetValue(key, out var session);
            if (session != null && !session.IsUsable)
            {
                if (_udp.TryRemove(key, out var expired))
                {
                    expired.Dispose();
                }

                session = null;
            }
            if (session == null)
            {
                if (_udp.Count >= 2048)
                {
                    throw new IOException("UDP routing session limit reached.");
                }

                session = CreateUdpSession(flow, match, address);
                _udp[key] = session;
                Track(_sessions, session.Completion);
            }
            var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[(packet.TransportOffset + 4)..]) - 8;
            session.Send(new(flow.RemoteAddress, flow.RemotePort), bytes.Span.Slice(packet.TransportOffset + 8, payloadLength));
            return;
        }
        PassThrough();
    }

    private RouteUdpSession CreateUdpSession(RouteFlow flow, RouteDecision selected, DivertAddress replyAddress)
    {
        var rule = selected.Rule!;
        var originalInterfaces = _interfaces;
        var owner = new RouteFlowOwner(selected, () =>
        {
            var routing = Volatile.Read(ref _routing);
            if (!routing.Policy.Retains(rule) || !_getInterfaces().Retains(originalInterfaces, replyAddress.InterfaceIndex,
                    flow.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)) { return RouteDecision.Unselected; }
            return routing.Owners.FindFresh(flow, Environment.TickCount64);
        });
        replyAddress.Outbound = false;
        return new(rule, new(flow.RemoteAddress, flow.RemotePort),
            (peer, payload) => SendUdpReply(flow with { RemoteAddress = peer.Address, RemotePort = (ushort)peer.Port }, replyAddress, payload),
            _stop.Token, Report, owner.IsCurrent, canSend: () => !_stop.IsCancellationRequested &&
                Volatile.Read(ref _routing).Policy.Retains(rule) && _getInterfaces().Retains(originalInterfaces,
                    replyAddress.InterfaceIndex, flow.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6),
            canReuse: () => !owner.IsInvalidated,
            waitForOwner: token => owner.WaitForCurrentAsync(_attributionUpdates, RequestRefresh, token));
    }

    private void ResetTcpPacket(RoutePacket packet, ReadOnlySpan<byte> bytes, DivertAddress address, RoutePacketBatch? output)
    {
        var reset = packet.CreateTcpReset(bytes);
        if (reset == null) { return; }
        address.Outbound = false;
        Send(reset, address, checksum: true, output);
    }

    internal void ResetTcpConnections(IEnumerable<RouteNatEntry> entries, RoutePacketBatch? output = null)
    {
        foreach (var entry in entries)
        {
            try
            {
                var address = entry.OriginalAddress;
                address.Outbound = false;
                Send(entry.CreateReset(), address, checksum: true, output);
            }
            catch (Exception ex) { Report(ex); } // One failed reset must not prevent the policy cutover.
        }
    }

    private void SendUdpReply(RouteFlow flow, DivertAddress address, ReadOnlySpan<byte> payload)
    {
        var reply = ArrayPool<byte>.Shared.Rent(48 + payload.Length);
        try
        {
            var length = RoutePacket.WriteUdpReply(reply, flow, payload);
            Send(reply.AsMemory(0, length), address, checksum: true);
        }
        finally { ArrayPool<byte>.Shared.Return(reply); }
    }

    private void Send(Memory<byte> bytes, DivertAddress address, bool checksum, RoutePacketBatch? output = null)
    {
        if (_stop.IsCancellationRequested) { return; }
        if (checksum && !WinDivertApi.WinDivertHelperCalcChecksums(ref MemoryMarshal.GetReference(bytes.Span), (uint)bytes.Length, ref address, 0))
        {
            throw new IOException("Could not calculate redirected packet checksums.");
        }
        // Packet preparation owns its buffers; only native handle operations
        // need serialization with other injections and shutdown.
        if (output != null)
        {
            output.Add(bytes.Span, address);
            return;
        }
        lock (_sendGate)
        {
            if (_handle == IntPtr.Zero || _stop.IsCancellationRequested)
            {
                return;
            }

            if (!WinDivertApi.WinDivertSend(_handle, ref MemoryMarshal.GetReference(bytes.Span), (uint)bytes.Length, out _, ref address))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    private void SendBatch(Span<byte> packets, Span<DivertAddress> addresses)
    {
        lock (_sendGate)
        {
            if (_handle == IntPtr.Zero || _stop.IsCancellationRequested) { return; }
            if (!WinDivertApi.WinDivertSendEx(_handle, ref MemoryMarshal.GetReference(packets), (uint)packets.Length,
                out _, 0, ref MemoryMarshal.GetReference(addresses), (uint)(addresses.Length * RoutePacketBatch.AddressSize), IntPtr.Zero))
            { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
    }

    internal async Task Accept(Func<CancellationToken, ValueTask<Socket>> accept)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket? client = null;
                try
                {
                    client = await accept(_stop.Token);
                    var local = (IPEndPoint)client.LocalEndPoint!;
                    var remote = (IPEndPoint)client.RemoteEndPoint!;
                    var entry = _nat.Reverse(local.Address, remote.Address, (ushort)remote.Port);
                    if (entry == null || entry.Closed || entry.Accepted || _connections.Count >= 2048)
                    {
                        continue;
                    }
                    entry.Accepted = true;
                    var task = Relay(client, entry);
                    client = null; // Relay now owns the accepted socket.
                    Track(_connections, task);
                }
                // A peer can cancel while its connection is still in the accept queue.
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
                finally { client?.Dispose(); }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Track(ConcurrentDictionary<long, Task> tasks, Task task)
    {
        var id = Interlocked.Increment(ref _connectionId);
        tasks[id] = task;
        // Drain retiring tasks on shutdown, but free completed slots immediately
        // even when the thread pool is busy. The callback only removes one entry.
        _ = task.ContinueWith(_ => { tasks.TryRemove(id, out var ignored); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task Relay(Socket client, RouteNatEntry entry)
    {
        using (client)
        using (var timeout = entry.BeginRelay(_stop.Token))
        {
            try
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var outbound = await RouteConnector.ConnectTcp(entry.Rule, new(entry.Flow.RemoteAddress, entry.Flow.RemotePort), timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                using var input = new NetworkStream(client, false);
                using var output = new NetworkStream(outbound, false);
                async Task Copy(NetworkStream source, NetworkStream target, Socket targetSocket)
                {
                    try
                    {
                        await source.CopyToAsync(target, timeout.Token);
                        targetSocket.Shutdown(SocketShutdown.Send);
                    }
                    catch { client.Dispose(); outbound.Dispose(); throw; }
                }
                await Task.WhenAll(Copy(input, output, outbound), Copy(output, input, client));
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested && !entry.Closed) { Report(new TimeoutException("Application route connection timed out.")); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Report(ex); }
            finally { entry.Closed = true; entry.LastActivity = Environment.TickCount64; }
        }
    }

    private async Task Cleanup()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                _nat.Expire(Environment.TickCount64);
                foreach (var pair in _udp)
                {
                    if (pair.Value.CanRetire())
                    {
                        // The capture thread may have replaced this completed session already.
                        if (_udp.TryRemove(pair))
                        {
                            pair.Value.Dispose();
                            // Track owns the drain. One slow teardown must not
                            // delay ownership checks for the remaining sessions.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception ex)
    {
        if (!Stop(ex)) { return; }
        Logging.SaveLog("Application routing failure:" + Environment.NewLine + ex);
        _error("Application routing stopped: " + ex.Message);
    }

    private void Report(Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastError) < 1000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastError, now);
        _error(ex.Message);
    }

    private void StopCapture()
    {
        // Capture closes the handle under this same lock; never shut down a stale handle.
        lock (_sendGate)
        {
            if (_handle != IntPtr.Zero)
            {
                WinDivertApi.WinDivertShutdown(_handle, 1);
            }
        }
    }

    private bool Stop(Exception? error = null)
    {
        // Several workers can observe the same shutdown. Only the first one
        // owns retirement and reports the failure that triggered recovery.
        if (Interlocked.Exchange(ref _stopping, 1) != 0) { return false; }
        lock (_packetGate)
        {
            // Reset live streams while capture can still inject their original
            // endpoints; a replacement engine has no sequence/translation state.
            if (_handle != IntPtr.Zero)
            { ResetTcpConnections(_nat.Retain(new(null, []))); }
        }
        _completion.TrySetResult(error);
        _stop.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
        StopCapture();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        foreach (var session in _udp.Values)
        {
            session.Dispose();
        }

        try
        {
            // Stop producers first: an accept/capture iteration already in flight
            // may still register a connection after cancellation was requested.
            await Task.WhenAll(_workers);
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        try
        {
            await Task.WhenAll(_connections.Values.Concat(_sessions.Values));
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        lock (_sendGate)
        {
            if (_handle != IntPtr.Zero)
            {
                WinDivertApi.WinDivertClose(_handle);
            }
            _handle = IntPtr.Zero;
        }
        try { _attribution.Dispose(); }
        finally { _refreshRequest.Dispose(); _stop.Dispose(); }
    }
}
