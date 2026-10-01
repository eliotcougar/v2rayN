using System.Buffers.Binary;

namespace ServiceLib.Services.AppRouting;

/// <summary>Bypasses native/excluded traffic and reassembles other fragments before attribution.
/// The engine serializes access with packet processing and policy updates.</summary>
internal sealed class RouteFragmentBuffer(Func<DivertAddress, byte, IPAddress, IPAddress, bool>? bypassInterface = null)
{
    internal sealed record Batch(byte[] Packet, DivertAddress Address, List<(byte[] Packet, DivertAddress Address)> Originals, bool PassThrough = false);
    private sealed record Key(IPAddress Source, IPAddress Destination, uint Id, byte Protocol, uint Interface);
    private sealed class Assembly
    {
        public long Created = Environment.TickCount64;
        public byte[]? Prefix;
        public DivertAddress Address;
        public int? Length;
        public int Received;
        public bool Rejected;
        public SortedDictionary<int, byte[]> Parts = [];
        public List<(byte[] Packet, DivertAddress Address)> Originals = [];
    }
    private readonly Dictionary<Key, Assembly> _pending = [];
    private int _buffered;

    // False means an ordinary unfragmented packet. True with null batch means buffered.
    public bool Add(ReadOnlySpan<byte> packet, DivertAddress address, out Batch? batch)
    {
        batch = null;
        if (packet.Length < 20)
        {
            return false;
        }

        var six = packet[0] >> 4 == 6;
        int offset, start, prefixLength, previousNext = 6, total;
        uint id;
        byte protocol;
        bool more;
        if (packet[0] >> 4 == 4)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
            if ((flags & 0x3fff) == 0)
            {
                return false;
            }

            start = prefixLength = (packet[0] & 15) * 4;
            total = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
            offset = (flags & 0x1fff) * 8;
            more = (flags & 0x2000) != 0;
            id = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            protocol = packet[9];
        }
        else if (six)
        {
            if (packet.Length < 40)
            {
                return false;
            }

            total = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            protocol = packet[6];
            start = 40;
            for (var i = 0; protocol is 0 or 43 or 60; i++)
            {
                if (i >= 8 || start + 2 > packet.Length)
                {
                    throw new IOException("Invalid IPv6 extension header.");
                }

                previousNext = start;
                protocol = packet[start];
                start += (packet[start + 1] + 1) * 8;
            }
            if (protocol != 44)
            {
                return false;
            }

            if (start + 8 > packet.Length)
            {
                throw new IOException("Truncated IPv6 fragment header.");
            }

            var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[(start + 2)..]);
            offset = flags & 0xfff8;
            more = (flags & 1) != 0;
            id = BinaryPrimitives.ReadUInt32BigEndian(packet[(start + 4)..]);
            protocol = packet[start];
            prefixLength = start;
            start += 8;
        }
        else
        {
            return false;
        }

        // IPv6 Destination Options may follow the Fragment header. They are part
        // of the fragmentable data; RoutePacket walks them after reassembly.
        if (protocol is not (6 or 17) && !(six && protocol == 60)) { return false; }
        if (prefixLength < 20 || total > packet.Length || start >= total ||
            more && (total - start) % 8 != 0 || offset + total - start > 65535)
        {
            throw new IOException("Invalid IP fragment length.");
        }

        packet = packet[..total];
        var key = new Key(new(packet.Slice(six ? 8 : 12, six ? 16 : 4)),
            new(packet.Slice(six ? 24 : 16, six ? 16 : 4)), id, protocol, address.InterfaceIndex);
        Expire(Environment.TickCount64);
        // Excluded adapters and native local traffic need no ownership lookup or reassembly, even when a
        // non-initial fragment arrives first. Potential TCP relay replies are the
        // exception: their translated ports must never escape onto the network.
        if (bypassInterface?.Invoke(address, protocol, key.Source, key.Destination) == true)
        {
            var originals = _pending.TryGetValue(key, out var buffered) ? buffered.Originals.ToList() : [];
            if (buffered != null) { Remove(key, buffered); }
            originals.Add((packet.ToArray(), address));
            batch = new([], address, originals, true);
            return true;
        }
        // Ports and process ownership are established only for the complete
        // datagram. IP IDs can be reused, and do not identify an application's
        // socket: caching a previous bypass could emit a new flow's tail natively.
        if (!_pending.TryGetValue(key, out var assembly))
        {
            if (_pending.Count >= 256)
            {
                throw new IOException("IP fragment assembly limit reached.");
            }

            _pending[key] = assembly = new();
        }
        if (assembly.Rejected)
        {
            return true;
        }

        var payload = packet[start..];
        foreach (var part in assembly.Parts)
        {
            if (part.Key == offset && payload.SequenceEqual(part.Value))
            {
                return true;
            }

            if (part.Key < offset + payload.Length && offset < part.Key + part.Value.Length)
            {
                Reject(assembly);
                throw new IOException("Overlapping IP fragments blocked.");
            }
        }
        if (_buffered + packet.Length + payload.Length > 16 * 1024 * 1024 || assembly.Parts.Count >= 1024)
        {
            Reject(assembly);
            throw new IOException("IP fragment buffer limit reached.");
        }
        if (!more)
        {
            assembly.Length = offset + payload.Length;
        }

        if (assembly.Length is int length && (offset + payload.Length > length ||
            assembly.Parts.Any(p => p.Key + p.Value.Length > length)))
        {
            Reject(assembly);
            throw new IOException("Inconsistent IP fragment length.");
        }
        assembly.Parts.Add(offset, payload.ToArray());
        assembly.Originals.Add((packet.ToArray(), address));
        assembly.Received += payload.Length;
        _buffered += packet.Length + payload.Length;
        if (offset == 0)
        {
            assembly.Prefix = packet[..prefixLength].ToArray();
            assembly.Address = address;
            if (six)
            {
                assembly.Prefix[previousNext] = protocol;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(assembly.Prefix.AsSpan(6), 0);
            }
        }
        if (assembly.Prefix == null || assembly.Length != assembly.Received)
        {
            return true;
        }

        var header = assembly.Prefix;
        var resultLength = header.Length + assembly.Received;
        if (resultLength > (six ? 65575 : 65535))
        {
            Reject(assembly);
            throw new IOException("Reassembled IP packet is too large.");
        }
        var result = new byte[resultLength];
        header.CopyTo(result, 0);
        foreach (var part in assembly.Parts)
        {
            part.Value.CopyTo(result, header.Length + part.Key);
        }

        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(six ? 4 : 2), (ushort)(six ? resultLength - 40 : resultLength));
        batch = new(result, assembly.Address, assembly.Originals);
        Remove(key, assembly);
        return true;
    }

    internal void Expire(long now)
    {
        foreach (var pair in _pending.Where(p => now - p.Value.Created > 15_000).ToArray())
        {
            Remove(pair.Key, pair.Value);
        }
    }

    public void RetainInterfaces(Func<uint, bool, bool> retain)
    {
        foreach (var pair in _pending.Where(p => !retain(p.Key.Interface,
                     p.Key.Source.AddressFamily == AddressFamily.InterNetworkV6)).ToArray())
        {
            Remove(pair.Key, pair.Value);
        }
    }

    private void Reject(Assembly assembly)
    {
        _buffered -= assembly.Originals.Sum(p => p.Packet.Length) + assembly.Received;
        assembly.Originals.Clear();
        assembly.Parts.Clear();
        assembly.Received = 0;
        assembly.Prefix = null;
        assembly.Rejected = true;
    }

    private void Remove(Key key, Assembly assembly)
    {
        _pending.Remove(key);
        _buffered -= assembly.Originals.Sum(p => p.Packet.Length) + assembly.Received;
    }
}
