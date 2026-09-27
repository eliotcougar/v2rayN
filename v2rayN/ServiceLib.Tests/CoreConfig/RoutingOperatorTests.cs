using ReactiveUI.Primitives;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.CoreConfig;

public partial class RoutingBlockTests
{
    [Test]
    public async Task LegacyConversionPreservesOrAndAllCommonConstraintsInBothCores()
    {
        var legacy = new RulesItem
        {
            Id = "existing", Enabled = true, OutboundTag = Global.DirectTag, RuleType = ERuleType.ALL,
            Domain = ["full:example.com"], Ip = ["203.0.113.5"], Process = ["client.exe"],
            Port = "443", Protocol = ["tls"], InboundTag = ["test-inbound"], Network = "tcp",
        };
        var xrayBefore = new CoreConfigV2rayService(Context(legacy)).GenerateClientConfigContent();
        var singboxBefore = new CoreConfigSingboxService(Context(legacy, ECoreType.sing_box)).GenerateClientConfigContent();
        await xrayBefore.Success.Should().BeTrue();
        await singboxBefore.Success.Should().BeTrue();
        var oldXray = JsonUtils.Deserialize<V2rayConfig>(xrayBefore.Data!.ToString())!.routing.rules
            .Where(r => r.inboundTag?.Contains("test-inbound") == true).ToList();
        var oldSingbox = JsonUtils.Deserialize<SingboxConfig>(singboxBefore.Data!.ToString())!.route.rules
            .Where(r => r.inbound?.Contains("test-inbound") == true).ToList();
        await oldXray.Count.Should().BeEqualTo(3);
        await oldSingbox.Count.Should().BeEqualTo(3);
        await new RoutingRuleBlocksViewModel(legacy).TrySave().Should().BeTrue();
        var xray = CoreConfigV2rayService.CompileBlockRules(legacy, new());
        var singbox = CoreConfigSingboxService.CompileBlockRule(legacy, new(), true)!;
        await xray.Count.Should().BeEqualTo(3);
        for (var mask = 0; mask < 256; mask++)
        {
            var c = new TestConnection(mask);
            await xray.Any(r => Matches(r, c)).Should().BeEqualTo(oldXray.Any(r => Matches(r, c)));
            await Matches(singbox, c).Should().BeEqualTo(oldSingbox.Any(r => Matches(r, c)));
        }
        await RoutingBlockRules.ForDns(legacy).Enabled.Should().BeTrue();
        await RoutingBlockRules.ForDns(legacy).Domain!.Should().BeEquivalentTo(["full:example.com"]);
    }

    [Test]
    public async Task EditorShowsFixedOrForMatchesAndAndForCommonConstraints()
    {
        var source = new RulesItem();
        var editor = new RoutingRuleBlocksViewModel(source);
        editor.AddFilter(RoutingSelector.Port, ["443"]);
        editor.AddFilter(RoutingSelector.Domain, ["example.com"]);
        editor.AddFilter(RoutingSelector.IP, ["203.0.113.5"]);
        editor.AddFilter(RoutingSelector.Process, ["client.exe"]);
        await editor.Expression.Should().BeEqualTo($"(Domain {ResUI.RoutingBlocksOr} IP {ResUI.RoutingBlocksOr} {ResUI.RoutingBlocksProcess}) {ResUI.RoutingBlocksAnd} {ResUI.LvPort}");
        await editor.MatchFilters.Select(f => f.ShowOr).Should().BeEquivalentTo([false, true, true]);
        await editor.ConstraintFilters.Single().ShowAnd.Should().BeTrue();
        await editor.MatchFilters[0].RemoveCmd.Execute().ToTask();
        await editor.MatchFilters[0].ShowOr.Should().BeFalse();
        await editor.TrySave().Should().BeTrue();
        await new RoutingRuleBlocksViewModel(JsonUtils.DeepCopy(source)).Expression.Should().BeEqualTo(editor.Expression);
    }

    [Test]
    public async Task BothCoresMatchAllFourAlternativesWithEveryCommonConstraint()
    {
        var rule = Rule((RoutingSelector.Domain, ["full:example.com"]), (RoutingSelector.IP, ["203.0.113.5"]),
            (RoutingSelector.Process, ["client.exe"]), (RoutingSelector.WindowsApp, ["One"]),
            (RoutingSelector.Port, ["443"]), (RoutingSelector.Protocol, ["tls"]),
            (RoutingSelector.InboundTag, ["test-inbound"]), (RoutingSelector.Network, ["tcp"]));
        var packages = new RoutingPackageSnapshot { Roots = new Dictionary<string, string> { ["One"] = "C:/Apps/One/" } };
        var xray = CoreConfigV2rayService.CompileBlockRules(rule, packages);
        var singbox = CoreConfigSingboxService.CompileBlockRule(rule, packages, true)!;
        for (var mask = 0; mask < 256; mask++)
        {
            var c = new TestConnection(mask);
            var expected = (c.Has(0) || c.Has(1) || c.Has(2) || c.Has(3)) && c.Has(4) && c.Has(5) && c.Has(6) && c.Has(7);
            await xray.Any(r => Matches(r, c)).Should().BeEqualTo(expected);
            await Matches(singbox, c).Should().BeEqualTo(expected);
        }
    }

    [Test]
    public async Task MissingPackageOnlyRemovesItsOwnAlternative()
    {
        var rule = Rule((RoutingSelector.WindowsApp, ["Missing"]), (RoutingSelector.Domain, ["full:example.com"]),
            (RoutingSelector.Port, ["443"]));
        var xray = CoreConfigV2rayService.CompileBlockRules(rule, new()).Single();
        await xray.domain!.Should().BeEquivalentTo(["full:example.com"]);
        await xray.port.Should().BeEqualTo("443");
        await xray.process.Should().BeNull();
        var singbox = CoreConfigSingboxService.CompileBlockRule(rule, new(), true)!;
        await singbox.mode.Should().BeEqualTo("and");
        await singbox.rules!.Count.Should().BeEqualTo(2);
    }

    private sealed record TestConnection(int Mask)
    {
        public bool Has(int bit) => (Mask & (1 << bit)) != 0;
        public string Domain => Has(0) ? "example.com" : "other.org";
        public string Ip => Has(1) ? "203.0.113.5" : "198.51.100.7";
        public string ProcessName => Has(2) ? "client.exe" : "other.exe";
        public string ProcessPath => (Has(3) ? "C:/Apps/One/" : "C:/Outside/") + ProcessName;
        public int Port => Has(4) ? 443 : 80;
        public string Protocol => Has(5) ? "tls" : "http";
        public string Inbound => Has(6) ? "test-inbound" : "other-inbound";
        public string Network => Has(7) ? "tcp" : "udp";
    }

    // Evaluate only the native predicates emitted by these fixtures, independently
    // of the block parser. This catches lost conditions in either core's compiler.
    private static bool Matches(RulesItem4Ray rule, TestConnection c) =>
        (rule.domain == null || rule.domain.Contains("full:" + c.Domain))
        && (rule.ip == null || rule.ip.Contains(c.Ip))
        && (rule.process == null || rule.process.Any(p => p == c.ProcessName || p == c.ProcessPath
            || (p.EndsWith('/') && c.ProcessPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        && (rule.port == null || rule.port == c.Port.ToString())
        && (rule.protocol == null || rule.protocol.Contains(c.Protocol))
        && (rule.inboundTag == null || rule.inboundTag.Contains(c.Inbound))
        && (rule.network == null || rule.network == c.Network);

    private static bool Matches(Rule4Sbox rule, TestConnection c)
    {
        if (rule.type == "logical")
        { return rule.mode == "or" ? rule.rules!.Any(r => Matches(r, c)) : rule.rules!.All(r => Matches(r, c)); }
        return (rule.domain == null || rule.domain.Contains(c.Domain))
            && (rule.ip_cidr == null || rule.ip_cidr.Contains(c.Ip))
            && (rule.process_name == null || rule.process_name.Contains(c.ProcessName))
            && (rule.process_path == null || rule.process_path.Contains(c.ProcessPath.Replace('/', '\\')))
            && (rule.process_path_regex == null || rule.process_path_regex.Any(p => Regex.IsMatch(c.ProcessPath.Replace('/', '\\'), p)))
            && (rule.port == null || rule.port.Contains(c.Port))
            && (rule.protocol == null || rule.protocol.Contains(c.Protocol))
            && (rule.inbound == null || rule.inbound.Contains(c.Inbound))
            && (rule.network == null || rule.network.Contains(c.Network));
    }
}
