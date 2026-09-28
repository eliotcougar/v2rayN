using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.CoreConfig;

[NotInParallel]
public partial class RoutingBlockTests
{
    static RoutingBlockTests() => RxAppBuilder.CreateReactiveUIBuilder().WithMainThreadScheduler(ImmediateSequencer.Instance).WithCoreServices().BuildApp();

    private static RulesItem Rule(params (RoutingSelector Selector, string[] Values)[] filters)
    {
        var rule = new RulesItem { OutboundTag = Global.DirectTag, RuleType = ERuleType.Routing };
        RoutingBlockRules.Store(rule, new() { Filters = filters.Select(f => new RoutingFilter { Selector = f.Selector, Values = f.Values.ToList() }).ToList() });
        return rule;
    }

    private static CoreConfigContext Context(RulesItem rule, ECoreType core = ECoreType.Xray)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        return CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(core), core) with
        { RoutingItem = new RoutingItem { RuleSet = JsonUtils.Serialize(new[] { rule }) }, IsWindows = true };
    }

    [Test]
    public async Task EditorKeepsPathsWithSpacesAndCommasAndDoesNotMutateOnCancelOrValidationFailure()
    {
        var source = new RulesItem { Id = "existing", OutboundTag = "proxy", Process = [@"C:\Program Files\An App, Inc\app.exe"], Remarks = "original" };
        var before = JsonUtils.Serialize(source);
        var editor = new RoutingRuleBlocksViewModel(source) { Remarks = "edited" };
        await editor.Filters[0].Applications[0].Value.Should().BeEqualTo("C:/Program Files/An App, Inc/app.exe");
        await JsonUtils.Serialize(source).Should().BeEqualTo(before);
        editor.AddFilter(RoutingSelector.Domain);
        await editor.TrySave().Should().BeFalse();
        await JsonUtils.Serialize(source).Should().BeEqualTo(before);
        editor.Filters.Last().Text = "domain:example.com";
        await editor.TrySave().Should().BeTrue();
        await source.IsEnabled.Should().BeTrue();
        await source.Enabled.Should().BeFalse();
        await RoutingBlockRules.Values(source, RoutingSelector.Process).Should().BeEquivalentTo(["C:/Program Files/An App, Inc/app.exe"]);
    }

    [Test]
    public async Task DomainRegexQuantifiersRemainOneAlternativeAndConversionPreservesUnsupportedVersions()
    {
        var filter = new RoutingFilterViewModel(RoutingSelector.Domain, [@"regexp:^api[0-9]{1,3}\.example\.com$", "domain:example.org"]);
        await filter.Values().Count.Should().BeEqualTo(2);
        await filter.Values()[0].Should().BeEqualTo(@"regexp:^api[0-9]{1,3}\.example\.com$");
        var source = Rule((RoutingSelector.Domain, ["example.com"]));
        source.Blocks!.Version = 2;
        var before = JsonUtils.Serialize(source);
        await new RoutingRuleBlocksViewModel(source).TrySave().Should().BeFalse();
        await JsonUtils.Serialize(source).Should().BeEqualTo(before);
    }

    [Test]
    public async Task EverySelectorCanBeAddedOnceAndInboundOnlyRulesCanBeSaved()
    {
        var source = new RulesItem();
        var editor = new RoutingRuleBlocksViewModel(source);
        await editor.AvailableSelectors.Count.Should().BeEqualTo(9);
        editor.AddFilter(RoutingSelector.InboundTag, ["socks"]);
        editor.AddFilter(RoutingSelector.InboundTag, ["tun"]);
        await editor.Filters.Count.Should().BeEqualTo(1);
        await editor.AvailableSelectors.Count.Should().BeEqualTo(8);
        await editor.TrySave().Should().BeTrue();
        await CoreConfigV2rayService.CompileBlockRules(source, new()).Single().inboundTag!.Should().BeEquivalentTo(["socks"]);
    }

    [Test]
    public async Task DowngradeDropsUnknownFieldsWithoutCreatingAnUnconditionalRule()
    {
        var source = Rule((RoutingSelector.WindowsApp, ["Example.Package_test"]), (RoutingSelector.Domain, ["domain:example.com"]));
        var json = JsonNode.Parse(JsonUtils.Serialize(source))!.AsObject();
        foreach (var key in json.Select(p => p.Key).Where(k => k.Equals("Blocks", StringComparison.OrdinalIgnoreCase)).ToList()) { json.Remove(key); }
        var old = JsonUtils.Deserialize<RulesItem>(json.ToJsonString())!;
        await old.Enabled.Should().BeFalse();
        await old.Domain.Should().BeNull();
        await old.InboundTag!.Should().BeEquivalentTo([RoutingBlockRules.LegacyInbound]);
        // Even explicitly enabling the old fallback still requires an inbound that does not exist.
        old.Enabled = true;
        var generated = new CoreConfigV2rayService(Context(old)).GenerateClientConfigContent();
        await generated.Success.Should().BeTrue();
        var config = JsonUtils.Deserialize<V2rayConfig>(generated.Data!.ToString())!;
        var fallback = config.routing.rules.Single(r => r.inboundTag?.Contains(RoutingBlockRules.LegacyInbound) == true);
        await fallback.domain.Should().BeNull();
        await fallback.process.Should().BeNull();
    }

    [Test]
    public async Task XrayEmitsAlternativesWithCommonConstraintsAndLeavesLegacyExpansionUnchanged()
    {
        var block = Rule((RoutingSelector.Domain, ["domain:example.com"]), (RoutingSelector.IP, ["203.0.113.0/24"]),
            (RoutingSelector.Process, ["client.exe"]), (RoutingSelector.Port, ["443"]), (RoutingSelector.Protocol, ["tls"]),
            (RoutingSelector.Network, ["tcp"]), (RoutingSelector.InboundTag, ["socks"]));
        var generated = new CoreConfigV2rayService(Context(block)).GenerateClientConfigContent();
        await generated.Success.Should().BeTrue();
        var rules = JsonUtils.Deserialize<V2rayConfig>(generated.Data!.ToString())!.routing.rules.Where(r => r.outboundTag == Global.DirectTag).ToList();
        var combined = rules.Single(r => r.domain?.Contains("domain:example.com") == true);
        await combined.ip.Should().BeNull();
        await combined.process.Should().BeNull();
        await combined.port.Should().BeEqualTo("443");
        await combined.inboundTag!.Should().BeEquivalentTo(["socks"]);
        var legacy = new RulesItem { Enabled = true, Domain = ["domain:example.com"], Ip = ["203.0.113.0/24"], Process = ["client.exe"], OutboundTag = Global.DirectTag };
        var oldConfig = new CoreConfigV2rayService(Context(legacy)).GenerateClientConfigContent();
        var oldRules = JsonUtils.Deserialize<V2rayConfig>(oldConfig.Data!.ToString())!.routing.rules;
        await oldRules.Count(r => r.domain?.Contains("domain:example.com") == true || r.ip?.Contains("203.0.113.0/24") == true || r.process?.Contains("client.exe") == true).Should().BeEqualTo(3);
    }

    [Test]
    public async Task PackageAndProcessAreIndependentAlternatives()
    {
        var packages = new RoutingPackageSnapshot { Roots = new Dictionary<string, string> { ["One"] = "C:/Apps/One/" } };
        var rule = Rule((RoutingSelector.WindowsApp, ["One"]), (RoutingSelector.Process, ["client.exe"]));
        await CoreConfigV2rayService.CompileBlockRules(rule, packages).Count.Should().BeEqualTo(2);
        await CoreConfigV2rayService.CompileBlockRules(rule, new()).Single().process!.Should().BeEquivalentTo(["client.exe"]);
        await CoreConfigSingboxService.CompileBlockRule(rule, new(), true)!.process_name!.Should().BeEquivalentTo(["client.exe"]);
    }

    [Test]
    public async Task ServiceRowsPersistByShortNameAndNeverCompileAsUnconditionalCoreRules()
    {
        var source = new RulesItem { OutboundTag = Global.DirectTag };
        var editor = new RoutingRuleBlocksViewModel(source);
        editor.AddFilter(RoutingSelector.Service, ["Dnscache"]);
        await editor.TrySave().Should().BeTrue();
        await source.Blocks!.Filters.Single().Applications!.Single().Value.Should().BeEqualTo("Dnscache");
        await CoreConfigV2rayService.CompileBlockRules(source, new()).Count.Should().BeEqualTo(0);
        await CoreConfigSingboxService.CompileBlockRule(source, new(), true).Should().BeNull();

        source.Blocks.Filters[0].Applications![0].IncludeChildren = true;
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => RoutingBlockRules.Validate(source)));
        source.Blocks.Filters[0].Applications![0].IncludeChildren = false;
        source.Blocks.Filters[0].Applications![0].Value = "C:/Windows/Dnscache";
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => RoutingBlockRules.Validate(source)));
        source.Blocks.Filters[0].Applications = null;
        source.Blocks.Filters[0].Values = ["C:/Windows/Dnscache"];
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => RoutingBlockRules.Validate(source)));
    }

    [Test]
    public async Task SingboxKeepsProcessAlternativesAndConvertsNestedGeosite()
    {
        var rule = Rule((RoutingSelector.Domain, ["geosite:example"]), (RoutingSelector.IP, ["203.0.113.0/24"]),
            (RoutingSelector.Process, ["client.exe", "C:/Apps/Other.exe"]));
        var generated = new CoreConfigSingboxService(Context(rule, ECoreType.sing_box)).GenerateClientConfigContent();
        await generated.Success.Should().BeTrue();
        var config = JsonUtils.Deserialize<SingboxConfig>(generated.Data!.ToString())!;
        var combined = config.route.rules.Single(r => r.mode == "or" && r.outbound == Global.DirectTag);
        await combined.rules!.Count.Should().BeEqualTo(3);
        await combined.rules[0].rule_set!.Should().BeEquivalentTo(["geosite-example"]);
        await combined.rules[0].geosite.Should().BeNull();
        await config.route.rule_set!.Should().Contain(r => r.tag == "geosite-example");
        await combined.rules[2].mode.Should().BeEqualTo("or");
        await combined.rules[2].rules!.Count.Should().BeEqualTo(2);

        // Unix process names are case-sensitive; Windows-only deduplication must not
        // erase a distinct executable when the shared editor compiles for sing-box.
        var processRule = Rule((RoutingSelector.Process, []));
        processRule.Blocks!.Filters[0].Applications = [
            RoutingApplicationRow.FromValue("Client", RoutingSelector.Process),
            RoutingApplicationRow.FromValue("client", RoutingSelector.Process)];
        var unix = CoreConfigSingboxService.CompileBlockRule(processRule, new(), windows: false)!;
        await unix.rules!.SelectMany(r => r.process_name!).Should().BeEquivalentTo(["Client", "client"]);
    }

    [Test]
    public async Task DnsProjectsDomainAlternativeAndDisabledStateRoundTrips()
    {
        var rule = Rule((RoutingSelector.Domain, ["domain:example.com"]), (RoutingSelector.Process, ["client.exe"]));
        rule.RuleType = ERuleType.ALL;
        await RoutingBlockRules.ForDns(rule).Enabled.Should().BeTrue();
        rule.Blocks!.Filters.RemoveAt(1);
        await RoutingBlockRules.ForDns(rule).Enabled.Should().BeTrue();
        rule.Blocks.Enabled = false;
        var copy = JsonUtils.DeepCopy(rule);
        await copy.IsEnabled.Should().BeFalse();
        await RoutingBlockRules.ForDns(copy).Enabled.Should().BeFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task InvalidOrUnsupportedFiltersFailConfigurationInsteadOfBroadening(int failure)
    {
        var rule = Rule((RoutingSelector.Domain, ["domain:example.com"]));
        switch (failure)
        {
            case 0: rule.Blocks!.Version = 999; break;
            case 1: rule.Blocks!.Filters.Add(new() { Selector = RoutingSelector.Process }); break;
            case 2: rule.Blocks!.Filters.Add(new() { Selector = (RoutingSelector)999, Values = ["unknown"] }); break;
            case 3: rule.Blocks!.Filters.Add(new() { Selector = RoutingSelector.Port, Values = ["70000"] }); break;
            case 4: rule.Blocks!.Filters.Add(new() { Selector = RoutingSelector.Process, Applications = [new() { Value = "app.exe", Mode = (RoutingProcessMode)999 }] }); break;
            case 5: rule.Blocks!.Filters.Add(new() { Selector = RoutingSelector.Domain, Values = ["duplicate"] }); break;
        }
        await new CoreConfigV2rayService(Context(rule)).GenerateClientConfigContent().Success.Should().BeFalse();
        await new CoreConfigSingboxService(Context(rule, ECoreType.sing_box)).GenerateClientConfigContent().Success.Should().BeFalse();
    }
}
