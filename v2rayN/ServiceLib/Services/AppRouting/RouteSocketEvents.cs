namespace ServiceLib.Services.AppRouting;

internal sealed record RouteSocketEvent(byte Event, long Timestamp, ulong Endpoint, int Pid, RouteFlow Flow)
{
    public static RouteSocketEvent FromAddress(DivertAddress address) => new(address.Event, address.Timestamp,
        address.EndpointId, checked((int)address.ProcessId), new(address.Protocol, address.LocalAddress.ToAddress(),
            address.LocalPort, address.RemoteAddress.ToAddress(), address.RemotePort));
}

/// <summary>Passive socket metadata only. Never blocks or reinjects a socket operation.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RouteSocketEvents : IDisposable
{
    private readonly object _gate = new();
    private readonly RouteEventQueue<RouteSocketEvent> _events = new("socket");
    private readonly Task _worker;
    private IntPtr _handle;
    private bool _stopping;

    public RouteSocketEvents(Action changed)
    {
        _handle = WinDivertApi.WinDivertOpen("(tcp or udp) and (event == BIND or event == CONNECT or event == CLOSE)", 3, 0, 5);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!WinDivertApi.WinDivertSetParam(_handle, 0, 16384))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            WinDivertApi.WinDivertClose(_handle);
            throw error;
        }
        _worker = Task.Factory.StartNew(() =>
        {
            try
            {
                while (WinDivertApi.WinDivertRecv(_handle, IntPtr.Zero, 0, out _, out var address))
                {
                    _events.Add(RouteSocketEvent.FromAddress(address));
                    changed();
                }
                var error = Marshal.GetLastWin32Error();
                lock (_gate)
                { if (!_stopping) { throw new Win32Exception(error); } }
            }
            catch (Exception ex) { _events.Fail(ex); changed(); }
            finally
            {
                lock (_gate) { WinDivertApi.WinDivertClose(_handle); _handle = IntPtr.Zero; }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public IReadOnlyList<RouteSocketEvent> Drain() => _events.Drain();

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_handle != IntPtr.Zero) { WinDivertApi.WinDivertShutdown(_handle, 1); }
        }
        _worker.GetAwaiter().GetResult();
    }
}

/// <summary>Socket lifetimes survive close long enough to attribute packets already in the capture queue.</summary>
internal sealed class RouteSocketHistory
{
    internal sealed record Entry(RouteSocketEvent Open, long? Closed = null, long? MissingSince = null);
    private readonly Dictionary<ulong, List<Entry>> _endpoints = [];
    private static readonly long Retention = 10 * Stopwatch.Frequency;
    internal int Count => _endpoints.Sum(p => p.Value.Count);

    public void Update(IEnumerable<RouteSocketEvent> events, long now, Func<int, long, long?>? processExit = null,
        Func<RouteSocketEvent, bool>? isPresent = null)
    {
        foreach (var item in events.OrderBy(e => e.Timestamp))
        {
            if (!_endpoints.TryGetValue(item.Endpoint, out var entries)) { _endpoints.Add(item.Endpoint, entries = []); }
            if (item.Event == 7)
            {
                // Keep a close tombstone even when its open record is delivered later.
                entries.Add(new(item, item.Timestamp));
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Open.Timestamp <= item.Timestamp && (entries[i].Closed == null || entries[i].Closed > item.Timestamp))
                    { entries[i] = entries[i] with { Closed = item.Timestamp }; }
                }
            }
            else
            {
                // UDP authorization may repeat for a live endpoint. A bind already
                // covers its peers; identical observations are not new lifetimes.
                if (entries.Any(e => Covers(e, item, processExit))) { continue; }
                var closed = entries.Where(e => e.Closed >= item.Timestamp).Select(e => e.Closed).Min();
                entries.Add(new(item, closed));
            }
        }
        foreach (var entries in _endpoints.Values)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (processExit?.Invoke(entry.Open.Pid, entry.Open.Timestamp) is { } exited && (entry.Closed == null || exited < entry.Closed))
                { entry = entry with { Closed = exited }; }
                if (entry.Closed == null && isPresent != null)
                {
                    // SOCKET events are observations, not a reliable lifetime ledger.
                    // Require sustained absence from complete owner tables, so a bind
                    // racing table publication cannot retire a long-lived UDP socket.
                    long? missing = isPresent(entry.Open) ? null : entry.MissingSince ?? now;
                    if (entry.MissingSince != missing) { entry = entry with { MissingSince = missing }; }
                    if (missing is { } since && now - since > Retention)
                    { entry = entry with { Closed = since }; }
                }
                entries[i] = entry;
            }
        }
        var oldest = now - Retention;
        foreach (var entries in _endpoints.Values) { entries.RemoveAll(e => e.Closed < oldest); }
        foreach (var key in _endpoints.Where(p => p.Value.Count == 0).Select(p => p.Key).ToArray()) { _endpoints.Remove(key); }
        if (Count > 65536) { throw new IOException($"Application-routing socket history exceeded its capacity ({Count} records in {_endpoints.Count} endpoints)."); }
    }

    private static bool Covers(Entry entry, RouteSocketEvent item, Func<int, long, long?>? processExit)
    {
        var open = entry.Open;
        if (open.Pid != item.Pid || open.Timestamp > item.Timestamp || entry.Closed < item.Timestamp ||
            processExit?.Invoke(open.Pid, open.Timestamp) < item.Timestamp) { return false; }
        if (open.Event == item.Event && open.Flow == item.Flow) { return true; }
        return open.Event == 3 && open.Flow.Protocol == 17 && item.Flow.Protocol == 17 &&
            open.Flow.RemotePort == 0 && open.Flow.LocalPort == item.Flow.LocalPort &&
            RouteSocketSnapshot.MatchesAddress(open.Flow.LocalAddress, item.Flow.LocalAddress);
    }

    public RouteSocketSnapshot Snapshot(Func<int, long, RouteDecision> decide) => new(_endpoints.Values.SelectMany(e => e), decide);
}

internal sealed class RouteSocketSnapshot
{
    private sealed record Owner(RouteSocketHistory.Entry Entry, RouteDecision Decision);
    private readonly Dictionary<(byte Protocol, ushort Port), Owner[]> _ports;

    public RouteSocketSnapshot(IEnumerable<RouteSocketHistory.Entry> entries, Func<int, long, RouteDecision> decide)
    {
        _ports = entries.Where(e => e.Open.Flow.LocalPort != 0 &&
                (e.Open.Flow.Protocol == 17 || e.Open.Event is 4 or 7))
            .Select(e => new Owner(e, decide(e.Open.Pid, e.Open.Timestamp) with { Endpoint = e.Open.Endpoint }))
            .GroupBy(e => (e.Entry.Open.Flow.Protocol, e.Entry.Open.Flow.LocalPort)).ToDictionary(g => g.Key, g => g.ToArray());
    }

    // timestamp == 0 asks about current ownership (reply delivery), never closed history.
    public RouteDecision? Find(RouteFlow flow, long timestamp)
    {
        if (!_ports.TryGetValue((flow.Protocol, flow.LocalPort), out var owners)) { return null; }
        RouteDecision? result = null;
        var known = false;
        foreach (var owner in owners)
        {
            var entry = owner.Entry;
            var socket = entry.Open.Flow;
            if (!MatchesAddress(socket.LocalAddress, flow.LocalAddress)) { continue; }
            if (socket.RemotePort != 0 && (socket.RemotePort != flow.RemotePort || !MatchesAddress(socket.RemoteAddress, flow.RemoteAddress))) { continue; }
            known = true;
            if (entry.Open.Event == 7 || (timestamp == 0 ? entry.Closed != null : timestamp < entry.Open.Timestamp || entry.Closed < timestamp)) { continue; }
            var decision = owner.Decision.ForFlow(flow);
            result = result == null ? decision : RouteAttributionSnapshot.Merge(result, decision);
        }
        // Do not assign a packet in a lifecycle gap to a newer table owner.
        return result ?? (known ? RouteDecision.Unresolved : null);
    }

    internal static bool MatchesAddress(IPAddress socket, IPAddress packet)
    {
        if (socket.AddressFamily != packet.AddressFamily) { return false; }
        if (socket.Equals(IPAddress.Any) || socket.Equals(IPAddress.IPv6Any) || socket.Equals(packet)) { return true; }
        if (packet.AddressFamily != AddressFamily.InterNetworkV6 || packet.ScopeId == 0) { return false; }
        // SOCKET metadata has no interface scope. Compare the address bytes without allocating.
        Span<byte> socketBytes = stackalloc byte[16];
        Span<byte> packetBytes = stackalloc byte[16];
        socket.TryWriteBytes(socketBytes, out _);
        packet.TryWriteBytes(packetBytes, out _);
        return socketBytes.SequenceEqual(packetBytes);
    }
}
