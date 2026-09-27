namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigV2rayService
{
    private void ValidateBlockRules()
    {
        foreach (var rule in RoutingBlockRules.Read(context.RoutingItem?.RuleSet).Where(r => r.IsEnabled && r.Blocks != null))
        { CompileBlockRules(rule, context.RoutingPackages); }
    }

    internal static List<RulesItem4Ray> CompileBlockRules(RulesItem item, RoutingPackageSnapshot packages)
    {
        RoutingBlockRules.Validate(item);
        return RoutingBlockRules.Conjunctions(item.Blocks!).Select(filters => CompileConjunction(item, filters, packages))
            .OfType<RulesItem4Ray>().ToList();
    }

    private static RulesItem4Ray? CompileConjunction(RulesItem item, List<RoutingFilter> filters, RoutingPackageSnapshot packages)
    {
        var rule = new RulesItem4Ray { type = "field", outboundTag = item.OutboundTag };
        foreach (var filter in filters)
        {
            var values = filter.EffectiveValues().Where(v => filter.Selector is not (RoutingSelector.Domain or RoutingSelector.IP) || !v.StartsWith('#')).ToList();
            if (values.Count == 0) { return null; }
            switch (filter.Selector)
            {
                case RoutingSelector.Domain:
                    rule.domain = values.Select(v => v.Replace(Global.RoutingRuleComma, ",")).ToList();
                    break;
                case RoutingSelector.IP: rule.ip = values; break;
                case RoutingSelector.Port: rule.port = string.Join(',', values); break;
                case RoutingSelector.Process: rule.process = values; break;
                case RoutingSelector.Protocol: rule.protocol = values; break;
                case RoutingSelector.InboundTag: rule.inboundTag = values; break;
                case RoutingSelector.Network: rule.network = string.Join(',', values); break;
            }
        }
        var families = filters.FirstOrDefault(f => f.Selector == RoutingSelector.WindowsApp)?.EffectiveValues();
        if (families?.Count > 0)
        {
            rule.process = packages.Resolve(families);
            // No installed package means no match, never a removed predicate.
            if (rule.process.Count == 0) { return null; }
        }
        return rule;
    }
}
