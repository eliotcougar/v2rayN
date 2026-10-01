namespace ServiceLib.Services.AppRouting;

internal sealed class RouteNatEntry(RouteFlow flow, RouteTarget rule, ushort translatedPort, uint initialSequence)
{
    public RouteFlow Flow { get; } = flow;
    public RouteTarget Rule { get; } = rule;
    public ushort TranslatedPort { get; } = translatedPort;
    public uint InitialSequence { get; } = initialSequence;
    // Packet processing serializes updates. The application's latest ACK gives
    // an acceptable reset sequence, including when its stream is otherwise idle.
    public uint? ClientAcknowledgement { get; set; }
    public byte[] CreateReset() => ClientAcknowledgement is uint ack
        ? RoutePacket.CreateTcpReset(Flow, ack)
        : RoutePacket.CreateTcpReset(Flow, 0, unchecked(InitialSequence + 1));
    private long _lastActivity = Environment.TickCount64;
    private volatile bool _accepted;
    private volatile bool _closed;
    private volatile bool _retired;
    public bool Retired => _retired;
    private readonly object _relayGate = new();
    private CancellationTokenSource? _relayStop;
    public CancellationTokenSource BeginRelay(CancellationToken token)
    {
        lock (_relayGate)
        {
            _relayStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (Closed) { _relayStop.Cancel(); }
            return _relayStop;
        }
    }

    public void Retire()
    {
        lock (_relayGate)
        {
            _retired = true;
            Closed = true;
            LastActivity = Environment.TickCount64;
            try { _relayStop?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    public long LastActivity
    {
        get => Interlocked.Read(ref _lastActivity);
        set => Interlocked.Exchange(ref _lastActivity, value);
    }
    public bool Accepted
    {
        get => _accepted;
        set => _accepted = value;
    }
    public bool Closed
    {
        get => _closed;
        set => _closed = value;
    }
    public DivertAddress OriginalAddress
    {
        get; set;
    }
}

/// <summary>Each original five-tuple gets an independent reflected connection.</summary>
internal sealed class RouteNatTable
{
    private readonly object _gate = new();
    private readonly Dictionary<RouteFlow, RouteNatEntry> _forward = [];
    private readonly Dictionary<ushort, RouteNatEntry> _reverse = [];
    private int _next = 1024;

    public RouteNatEntry GetOrAdd(RouteFlow flow, RouteTarget rule, uint initialSequence)
    {
        lock (_gate)
        {
            if (_forward.TryGetValue(flow, out var entry))
            {
                return entry;
            }

            for (var i = 0; i < 64512; i++)
            {
                var port = (ushort)_next;
                _next = _next == 65535 ? 1024 : _next + 1;
                if (_reverse.ContainsKey(port))
                {
                    continue;
                }

                entry = new(flow, rule, port, initialSequence);
                _forward.Add(flow, entry);
                _reverse.Add(port, entry);
                return entry;
            }
            throw new IOException("Application routing connection limit reached.");
        }
    }

    public RouteNatEntry? Find(RouteFlow flow, uint? synSequence = null)
    {
        lock (_gate)
        {
            var entry = _forward.GetValueOrDefault(flow);
            // A fresh SYN may arrive before the previous relay finishes closing.
            // Retransmissions retain their initial sequence and keep the same mapping.
            if (entry != null && synSequence is uint sequence && (entry.Closed || entry.InitialSequence != sequence))
            {
                entry.Retire();
                _forward.Remove(flow);
                return null;
            }
            return entry;
        }
    }

    public RouteNatEntry? Reverse(IPAddress local, IPAddress remote, ushort translatedPort)
    {
        lock (_gate)
        {
            var entry = _reverse.GetValueOrDefault(translatedPort);
            // Reflected listener traffic can carry a different IPv6 scope. Match wire
            // addresses here; reverse NAT restores the original interface metadata.
            return entry != null && entry.Flow.LocalAddress.GetAddressBytes().AsSpan().SequenceEqual(local.GetAddressBytes()) &&
                entry.Flow.RemoteAddress.GetAddressBytes().AsSpan().SequenceEqual(remote.GetAddressBytes()) ? entry : null;
        }
    }

    public void Expire(long now)
    {
        lock (_gate)
        {
            foreach (var entry in _reverse.Values.Where(e => (e.Closed || !e.Accepted) &&
                         now - e.LastActivity > 120_000).ToArray())
            {
                entry.Retire();
                if (ReferenceEquals(_forward.GetValueOrDefault(entry.Flow), entry)) { _forward.Remove(entry.Flow); }
                _reverse.Remove(entry.TranslatedPort);
            }
        }
    }

    public bool MayBeReflection(IPAddress local, IPAddress remote)
    {
        lock (_gate)
        {
            return _reverse.Values.Any(e => e.Flow.LocalAddress.GetAddressBytes().AsSpan().SequenceEqual(local.GetAddressBytes()) &&
                e.Flow.RemoteAddress.GetAddressBytes().AsSpan().SequenceEqual(remote.GetAddressBytes()));
        }
    }

    public IReadOnlyList<RouteNatEntry> Retain(RoutePolicy policy, Func<RouteNatEntry, bool>? retainInterface = null)
    {
        lock (_gate)
        {
            var retired = _forward.Values.Where(e => !e.Closed && (!policy.Retains(e.Rule) || retainInterface?.Invoke(e) == false)).ToArray();
            // Keep retired forward tombstones until a new SYN or expiry. Old
            // ACK/data must not follow a newly unselected/direct policy.
            foreach (var entry in retired) { entry.Retire(); }
            return retired;
        }
    }
}
