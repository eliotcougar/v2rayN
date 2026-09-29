namespace ServiceLib.Services.AppRouting;

/// <summary>Reconciles event history with the complete TCP/UDP tables, off the packet path.</summary>
internal sealed class RouteSocketPresence
{
    private readonly Dictionary<(byte Protocol, int Pid, ushort Port), RouteOwnerTable.Row[]> _rows;

    public RouteSocketPresence(IEnumerable<RouteOwnerTable.Row> tcp, IEnumerable<RouteOwnerTable.Row> udp)
    {
        _rows = tcp.Select(row => (Protocol: (byte)6, Row: row))
            .Concat(udp.Select(row => (Protocol: (byte)17, Row: row)))
            .GroupBy(item => (item.Protocol, item.Row.Pid, item.Row.Port))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Row).ToArray());
    }

    public bool Contains(RouteSocketEvent socket)
    {
        var flow = socket.Flow;
        if (!_rows.TryGetValue((flow.Protocol, socket.Pid, flow.LocalPort), out var rows)) { return false; }
        return rows.Any(row =>
            (RouteSocketSnapshot.MatchesAddress(row.Local, flow.LocalAddress) ||
             RouteSocketSnapshot.MatchesAddress(flow.LocalAddress, row.Local)) &&
            (flow.Protocol == 17 || flow.RemotePort == 0 ||
             row.RemotePort == flow.RemotePort && row.Remote != null &&
             RouteSocketSnapshot.MatchesAddress(flow.RemoteAddress, row.Remote)));
    }
}
