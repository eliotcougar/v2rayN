using System.Buffers.Binary;

namespace ServiceLib.Services.AppRouting;

internal sealed record RouteInterfaceAddress(IPAddress Address, int PrefixLength);

/// <summary>Native discovery/configuration traffic needs its original source, TTL and link.
/// Connected prefixes are captured with the adapter snapshot, never queried per packet.</summary>
internal sealed class RouteLocalTraffic
{
    private sealed record Network(IPAddress Prefix, int Length, IPAddress? Broadcast)
    {
        public static Network? From(RouteInterfaceAddress address)
        {
            var bytes = address.Address.GetAddressBytes();
            if (address.PrefixLength <= 0 || address.PrefixLength > bytes.Length * 8) { return null; }
            var whole = address.PrefixLength / 8;
            var bits = address.PrefixLength % 8;
            if (bits != 0) { bytes[whole] = (byte)(bytes[whole] & (byte.MaxValue << (8 - bits))); whole++; }
            bytes.AsSpan(whole).Clear();
            IPAddress? broadcast = null;
            if (bytes.Length == 4 && address.PrefixLength <= 30)
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(bytes) | (uint.MaxValue >> address.PrefixLength);
                Span<byte> result = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(result, value);
                broadcast = new(result);
            }
            return new(new(bytes), address.PrefixLength, broadcast);
        }

        public bool Contains(IPAddress address)
        {
            Span<byte> prefix = stackalloc byte[16];
            Span<byte> peer = stackalloc byte[16];
            Prefix.TryWriteBytes(prefix, out var length);
            address.TryWriteBytes(peer, out var peerLength);
            if (length != peerLength) { return false; }
            var whole = Length / 8;
            var bits = Length % 8;
            return prefix[..whole].SequenceEqual(peer[..whole]) &&
                (bits == 0 || (prefix[whole] ^ peer[whole]) >> (8 - bits) == 0);
        }
    }

    private readonly Dictionary<(uint Index, bool IPv6), Network[]> _networks = [];

    public RouteLocalTraffic(IReadOnlyList<RouteInterfaceInfo> adapters)
    {
        foreach (var adapter in adapters)
        {
            foreach (var group in (adapter.Addresses ?? []).GroupBy(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6))
            {
                var index = group.Key ? adapter.IPv6Index : adapter.IPv4Index;
                if (index == 0) { continue; }
                _networks[(index, group.Key)] = group.Select(Network.From).OfType<Network>().Distinct()
                    .OrderBy(n => n.Prefix.ToString(), StringComparer.Ordinal).ThenBy(n => n.Length).ToArray();
            }
        }
    }

    public bool SameAs(RouteLocalTraffic other) => _networks.Count == other._networks.Count &&
        _networks.All(p => other._networks.TryGetValue(p.Key, out var networks) && p.Value.SequenceEqual(networks));

    public bool Bypasses(uint index, IPAddress destination)
    {
        if (destination.IsIPv6Multicast || destination.IsIPv6LinkLocal) { return true; }
        if (destination.AddressFamily != AddressFamily.InterNetwork) { return false; }
        Span<byte> bytes = stackalloc byte[4];
        destination.TryWriteBytes(bytes, out _);
        if (bytes[0] is >= 224 and <= 239 || bytes[0] == 169 && bytes[1] == 254 || destination.Equals(IPAddress.Broadcast))
        { return true; }
        if (_networks.TryGetValue((index, false), out var networks))
        {
            foreach (var network in networks)
            { if (destination.Equals(network.Broadcast)) { return true; } }
        }
        return false;
    }

    public bool Bypasses(uint index, RouteFlow flow)
    {
        if (Bypasses(index, flow.RemoteAddress)) { return true; }
        if (flow.Protocol != 17) { return false; }
        // Preserve client renewal, server replies and relay traffic, including off-link DHCP servers.
        var ipv6 = flow.RemoteAddress.AddressFamily == AddressFamily.InterNetworkV6;
        var clientPort = ipv6 ? 546 : 68;
        var serverPort = ipv6 ? 547 : 67;
        if (flow.LocalPort == clientPort && flow.RemotePort == serverPort ||
            flow.LocalPort == serverPort && (flow.RemotePort == clientPort || flow.RemotePort == serverPort))
        { return true; }
        // Discovery replies may target an ephemeral port. A public destination
        // using the same ports still follows the user's routing rules.
        static bool Discovery(ushort port) => port is 5353 or 5355 or 1900 or 3702 or 137 or 138;
        if ((Discovery(flow.LocalPort) || Discovery(flow.RemotePort)) && _networks.TryGetValue((index, ipv6), out var networks))
        {
            foreach (var network in networks)
            { if (network.Contains(flow.RemoteAddress)) { return true; } }
        }
        return false;
    }
}
