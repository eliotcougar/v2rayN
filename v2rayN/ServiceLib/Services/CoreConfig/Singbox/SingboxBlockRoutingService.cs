namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    internal static Rule4Sbox? CompileBlockRule(RulesItem item, RoutingPackageSnapshot packages, bool windows)
    {
        RoutingBlockRules.Validate(item);
        var alternatives = RoutingBlockRules.Conjunctions(item.Blocks!).Select(filters => CompileConjunction(filters, packages, windows))
            .OfType<Rule4Sbox>().ToList();
        if (alternatives.Count == 0) { return null; }
        var result = Combine("or", alternatives);
        if (item.OutboundTag == Global.BlockTag) { result.action = "reject"; }
        else { result.outbound = item.OutboundTag ?? Global.ProxyTag; }
        return result;
    }

    private static Rule4Sbox? CompileConjunction(List<RoutingFilter> filters, RoutingPackageSnapshot packages, bool windows)
    {
        var conditions = new List<Rule4Sbox>();
        foreach (var filter in filters)
        {
            var values = filter.EffectiveValues().Where(v => filter.Selector is not (RoutingSelector.Domain or RoutingSelector.IP) || !v.StartsWith('#')).ToList();
            if (values.Count == 0) { return null; }
            var condition = new Rule4Sbox();
            switch (filter.Selector)
            {
                case RoutingSelector.Domain:
                    var domains = new List<Rule4Sbox>();
                    foreach (var value in values.Where(v => !v.StartsWith('#')))
                    {
                        var domain = new Rule4Sbox();
                        if (!ParseV2Domain(value, domain)) { throw new ArgumentException($"Unsupported sing-box domain: {value}"); }
                        domains.Add(domain);
                    }
                    condition = Combine("or", domains);
                    break;
                case RoutingSelector.IP:
                    // Each alternative is explicit, including negations; no empty positive rule.
                    var alternatives = new List<Rule4Sbox>();
                    foreach (var value in values)
                    {
                        var ip = new Rule4Sbox();
                        var negative = value.StartsWith('!');
                        if (!ParseV2Address(negative ? value[1..].Trim() : value, ip))
                        { throw new ArgumentException($"Unsupported sing-box IP: {value}"); }
                        if (negative) { ip.invert = true; }
                        alternatives.Add(ip);
                    }
                    condition = Combine("or", alternatives);
                    break;
                case RoutingSelector.Port:
                    condition.port = values.Where(v => !v.Contains('-')).Select(int.Parse).ToList();
                    condition.port_range = values.Where(v => v.Contains('-')).Select(v => v.Replace('-', ':')).ToList();
                    break;
                case RoutingSelector.Process:
                    condition = Combine("or", values.Select(value => ProcessCondition(value, windows)).ToList());
                    break;
                case RoutingSelector.WindowsApp:
                    var roots = packages.Resolve(values);
                    if (roots.Count == 0) { return null; }
                    condition.process_path_regex = roots.Select(root => "(?i)^" + Regex.Escape(root.Replace('/', '\\'))).ToList();
                    break;
                case RoutingSelector.Protocol: condition.protocol = values; break;
                case RoutingSelector.InboundTag: condition.inbound = values; break;
                case RoutingSelector.Network: condition.network = values; break;
            }
            conditions.Add(condition);
        }
        return Combine("and", conditions);
    }

    private static Rule4Sbox Combine(string mode, List<Rule4Sbox> rules) => rules.Count == 1 ? rules[0]
        : new() { type = "logical", mode = mode, rules = rules };

    private static Rule4Sbox ProcessCondition(string value, bool windows)
    {
        var path = RoutingPackageSnapshot.Normalize(value);
        if (path is "self/" or "xray/") { return new() { process_name = [windows ? "sing-box.exe" : "sing-box"] }; }
        if (path.Contains('/'))
        {
            if (windows) { path = path.Replace('/', '\\'); }
            if (value.EndsWith('/') || value.EndsWith('\\'))
            { return new() { process_path_regex = [(windows ? "(?i)" : "") + "^" + Regex.Escape(path)] }; }
            return new() { process_path = [path] };
        }
        return new() { process_name = [windows && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path + ".exe" : path] };
    }
}
