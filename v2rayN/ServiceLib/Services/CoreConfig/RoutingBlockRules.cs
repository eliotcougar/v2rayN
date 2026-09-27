namespace ServiceLib.Services.CoreConfig;

/// <summary>Shared expression semantics, validation and persistence boundary for the block editor.</summary>
internal static class RoutingBlockRules
{
    // Kept even when an old client re-saves the JSON and drops unknown properties.
    // Manually enabling the fallback in that client still cannot broaden this rule.
    internal const string LegacyInbound = "v2rayN-block-rule-requires-new-editor";

    public static void Store(RulesItem target, RoutingRuleBlocks blocks)
    {
        target.Blocks = blocks;
        target.Enabled = false;
        target.Type = "field";
        target.Domain = target.Ip = target.Process = target.Protocol = null;
        target.Port = target.Network = null;
        target.InboundTag = [LegacyInbound];
    }

    public static void Validate(RulesItem rule)
    {
        var blocks = rule.Blocks!;
        if (blocks.Version != 1)
        {
            throw new NotSupportedException($"Unsupported routing block version: {blocks.Version}.");
        }
        if (blocks.Filters.Count == 0 || blocks.Filters.Any(f => !ValidFilter(f))
            || blocks.Filters.Select(f => f.Selector).Distinct().Count() != blocks.Filters.Count)
        {
            throw new ArgumentException(ResUI.RoutingBlocksEmpty);
        }
        // DNS decisions have no application, inbound, transport or destination-port context.
        if (rule.RuleType == ERuleType.DNS && !IsDomainOnly(rule))
        {
            throw new ArgumentException(ResUI.RoutingBlocksDnsOnly);
        }
        foreach (var filter in blocks.Filters)
        {
            if (filter.Selector == RoutingSelector.Port && filter.Values.Any(value => !ValidPort(value)))
            {
                throw new ArgumentException(ResUI.RoutingBlocksInvalidPort);
            }
            if (filter.Selector == RoutingSelector.Network && filter.Values.Any(v => v is not ("tcp" or "udp")))
            {
                throw new ArgumentException(ResUI.RoutingBlocksInvalidNetwork);
            }
        }
    }

    private static bool ValidFilter(RoutingFilter filter)
    {
        if (!Enum.IsDefined(filter.Selector)) { return false; }
        if (filter.Applications == null) { return filter.Values.Count > 0 && !filter.Values.Any(string.IsNullOrWhiteSpace); }
        return filter.Selector is RoutingSelector.Process or RoutingSelector.WindowsApp && filter.Applications.Count > 0
            && filter.Applications.All(row => ValidApplication(row, filter.Selector));
    }

    private static bool ValidApplication(RoutingApplicationRow row, RoutingSelector selector)
    {
        if (!Enum.IsDefined(row.Mode) || string.IsNullOrWhiteSpace(row.Value)) { return false; }
        var value = row.MatchValue(selector);
        if (value.Length == 0) { return false; }
        if (selector == RoutingSelector.WindowsApp || row.Mode == RoutingProcessMode.Name) { return true; }
        if (value is "self/" or "xray/") { return true; }
        var absolute = value.StartsWith('/') || value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '/';
        return absolute && (row.Mode == RoutingProcessMode.Folder || !value.EndsWith('/'));
    }

    private static bool ValidPort(string value)
    {
        var range = value.Split('-');
        return range.Length is 1 or 2 && range.All(v => int.TryParse(v, out var port) && port is >= 0 and <= 65535)
            && (range.Length == 1 || int.Parse(range[0]) <= int.Parse(range[1]));
    }

    public static bool IsDomainOnly(RulesItem rule) =>
        rule.Blocks?.Filters is { Count: 1 } filters && filters[0].Selector == RoutingSelector.Domain;

    public static bool IsMatch(RoutingSelector selector) => selector is
        RoutingSelector.Domain or RoutingSelector.IP or RoutingSelector.Process or RoutingSelector.WindowsApp;

    // Match selectors are alternatives; common constraints apply to every alternative.
    public static List<List<RoutingFilter>> MatchGroups(RoutingRuleBlocks blocks) =>
        blocks.Filters.Where(f => IsMatch(f.Selector)).Select(f => new List<RoutingFilter> { f }).ToList();

    public static List<List<RoutingFilter>> Conjunctions(RoutingRuleBlocks blocks)
    {
        var groups = MatchGroups(blocks);
        if (groups.Count == 0) { groups.Add([]); }
        foreach (var group in groups) { group.AddRange(blocks.Filters.Where(f => !IsMatch(f.Selector))); }
        return groups;
    }

    public static string Describe(RoutingRuleBlocks blocks, Func<RoutingFilter, string> label)
    {
        var groups = MatchGroups(blocks);
        var terms = new List<string>();
        if (groups.Count > 0)
        {
            var alternatives = groups.Select(g => label(g[0]));
            var expression = string.Join($" {ResUI.RoutingBlocksOr} ", alternatives);
            terms.Add(groups.Count > 1 ? $"({expression})" : expression);
        }
        terms.AddRange(blocks.Filters.Where(f => !IsMatch(f.Selector)).Select(label));
        return string.Join($" {ResUI.RoutingBlocksAnd} ", terms);
    }

    public static List<string> Values(RulesItem rule, RoutingSelector selector) =>
        rule.Blocks?.Filters.FirstOrDefault(f => f.Selector == selector)?.EffectiveValues() ?? [];

    public static List<RulesItem> Read(string? json)
    {
        var rules = JsonUtils.Deserialize<List<RulesItem>>(json) ?? [];
        foreach (var rule in rules.Where(r => r.Blocks != null && r.IsEnabled)) { Validate(rule); }
        return rules;
    }

    public static RulesItem ForDns(RulesItem rule)
    {
        if (rule.Blocks == null) { return rule; }
        return new RulesItem
        {
            // Domain alternatives project into DNS just as in legacy OR rules.
            Enabled = rule.IsEnabled && MatchGroups(rule.Blocks).Any(g => g.Count == 1 && g[0].Selector == RoutingSelector.Domain),
            Domain = Values(rule, RoutingSelector.Domain),
            OutboundTag = rule.OutboundTag,
            RuleType = rule.RuleType,
        };
    }
}
