using System.Collections;

namespace ServiceLib.Services.AppRouting;

/// <summary>Destination-port eligibility for standalone Port (+ Network) rules.
/// Built once per policy; packet lookup does not parse rules or scan ranges.</summary>
internal sealed class RoutePortCapture
{
    private readonly BitArray _tcp = new(65536);
    private readonly BitArray _udp = new(65536);

    public bool Add(RoutingRuleBlocks blocks)
    {
        RoutingBlockRules.Validate(new RulesItem { Blocks = blocks });
        var ranges = blocks.Filters.Single(f => f.Selector == RoutingSelector.Port).Values.Select(value =>
        {
            var parts = value.Split('-');
            return (First: int.Parse(parts[0]), Last: int.Parse(parts[^1]));
        }).OrderBy(range => range.First).ToArray();
        // Full port coverage conventionally means the routing table's final fallback.
        // Keep that native rule, but do not let it opt every application into capture.
        var covered = 0;
        foreach (var range in ranges)
        {
            if (range.First > covered + 1) { break; }
            covered = Math.Max(covered, range.Last);
        }
        if (covered == 65535) { return false; }

        var network = blocks.Filters.FirstOrDefault(f => f.Selector == RoutingSelector.Network)?.Values;
        var tcp = network == null || network.Contains("tcp");
        var udp = network == null || network.Contains("udp");
        foreach (var range in ranges)
        {
            for (var port = range.First; port <= range.Last; port++)
            {
                if (tcp) { _tcp[port] = true; }
                if (udp) { _udp[port] = true; }
            }
        }
        return true;
    }

    public bool Matches(RouteFlow flow) => flow.Protocol switch
    {
        6 => _tcp[flow.RemotePort],
        17 => _udp[flow.RemotePort],
        _ => false,
    };
}
