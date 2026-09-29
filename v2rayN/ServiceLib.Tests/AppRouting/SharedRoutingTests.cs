using ServiceLib.Services.AppRouting;
using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.AppRouting;

public class SharedRoutingTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PrivateCoreKeepsQuietConnectionsWithoutChangingOtherPolicySettings(bool fileCore, bool existingPolicy)
    {
        var shared = new RouteSharedRules(Routing());
        const string json = """
            {"policy":{"system":{"statsOutboundDownlink":true},"levels":{"0":{"handshake":15,"bufferSize":64},"1":{"connIdle":120}}},
             "routing":{"rules":[{"type":"field","outboundTag":"direct"}]}}
            """;
        var input = JsonNode.Parse(json)!.AsObject();
        if (!existingPolicy) { input.Remove("policy"); }
        var template = new RouteSharedTemplate(input.ToJsonString(), shared);
        var config = fileCore ? JsonNode.Parse(template.ConfigFor([], "identity"))! : template.Root;
        // Xray's default is 300 seconds. A private transparent relay must instead
        // leave the quiet application's connected socket in control of its lifetime.
        await (config["policy"]!["levels"]!["0"]!["connIdle"]?.GetValue<int>() ?? 300).Should().BeGreaterThan(31_536_000);
        if (existingPolicy)
        {
            await config["policy"]!["levels"]!["0"]!["handshake"]!.GetValue<int>().Should().BeEqualTo(15);
            await config["policy"]!["levels"]!["0"]!["bufferSize"]!.GetValue<int>().Should().BeEqualTo(64);
            await config["policy"]!["levels"]!["1"]!["connIdle"]!.GetValue<int>().Should().BeEqualTo(120);
            await config["policy"]!["system"]!["statsOutboundDownlink"]!.GetValue<bool>().Should().BeTrue();
        }
    }

    private static RoutingItem Routing(params RulesItem[] rules) => new() { RuleSet = JsonUtils.Serialize(rules) };
    private static RulesItem App(RoutingSelector selector, string value, bool children = false, string outbound = "direct") => new()
    {
        OutboundTag = outbound, RuleType = ERuleType.Routing,
        Blocks = new() { Filters = [new() { Selector = selector, Applications = [new()
        { Value = value, Mode = RoutingProcessMode.Name, IncludeChildren = children }] }] }
    };
    private static RouteProcessInfo Process(int pid, string name, int parent = 0, long start = 1, string? family = "") =>
        new(new(pid, start), parent, name, "C:/Apps/" + name, PackageFamily: family);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingAncestorPathMattersOnlyForRulesThatIncludeChildren(bool children)
    {
        var rule = App(RoutingSelector.Process, "C:/Apps/worker.exe", children);
        rule.Blocks!.Filters[0].Applications![0].Mode = RoutingProcessMode.FullPath;
        var policy = RouteTestFactory.Policy(rule);
        var tree = new RouteProcessTree(policy, []);
        var child = Process(20, "worker.exe", 10, 2);
        tree.Update([Process(10, "worker.exe") with { Path = null }, child]);
        await tree.Decide(child.Key).Kind.Should().BeEqualTo(children ? RouteDecisionKind.Unresolved : RouteDecisionKind.Selected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FolderMatchesSubfoldersButNotSiblingPathsAndChildrenCanLeaveFolder(bool children)
    {
        var rule = App(RoutingSelector.Process, "C:/Apps/Suite/launcher.exe", children);
        rule.Blocks!.Filters[0].Applications![0].Mode = RoutingProcessMode.Folder;
        var shared = new RouteSharedRules(Routing(rule));
        var policy = new RouteSharedPolicy(shared, (_, _) => Task.FromResult(new RouteSocksEndpoint(12345)));
        var tree = new RouteProcessTree(policy, []);
        var parent = Process(10, "worker.exe") with { Path = "C:/Apps/Suite/bin/worker.exe" };
        var siblingFolder = Process(20, "worker.exe") with { Path = "C:/Apps/SuiteOther/worker.exe" };
        var child = Process(30, "helper.exe", 10, 2) with { Path = "D:/External/helper.exe" };
        tree.Update([parent, siblingFolder, child]);
        await tree.Decide(parent.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await tree.Decide(siblingFolder.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        await tree.Decide(child.Key).Kind.Should().BeEqualTo(children ? RouteDecisionKind.Selected : RouteDecisionKind.Unselected);
        await CoreConfigV2rayService.CompileBlockRules(rule, new()).Single().process!.Should().BeEquivalentTo(["C:/Apps/Suite/"]);
    }

    [Test]
    public async Task ChildrenKeepExitedParentIdentityAndTargetsBelongToTheirCore()
    {
        var main = new RouteSharedRules(Routing(App(RoutingSelector.Process, "launcher.exe", true)));
        IReadOnlyList<string>? selected = null;
        var policy = new RouteSharedPolicy(main, (markers, _) =>
        { selected = markers; return Task.FromResult(new RouteSocksEndpoint(12345)); });
        var tree = new RouteProcessTree(policy, []);
        var parent = Process(10, "launcher.exe") with { Exited = 3 };
        var child = Process(20, "child.exe", 10, 2);
        tree.Update([parent, child, Process(10, "unrelated.exe", start: 4)]);
        var decision = tree.Decide(child.Key);
        await decision.Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await decision.Rule!.ResolveEndpoint!(default);
        await selected!.Should().BeEquivalentTo([main.Branches.Single().Marker]);
        // Identical membership shares a target even across different process generations.
        var sibling = Process(30, "child.exe", 10, 2);
        tree.Update([sibling]);
        await tree.Decide(sibling.Key).Rule.Should().BeEqualTo(decision.Rule);
        await policy.Retains(decision.Rule).Should().BeTrue();
        var replacement = new RouteSharedPolicy(main, (_, _) => Task.FromResult(new RouteSocksEndpoint(12346)));
        var replaced = replacement.Select([parent]);
        await replacement.Retains(decision.Rule).Should().BeFalse();
        await replacement.Retains(replaced!).Should().BeTrue();
    }

    [Test]
    public async Task PackageIdentityAndChildFlagAreIndependentOfExecutablePathAndProtectedCores()
    {
        var main = new RouteSharedRules(Routing(App(RoutingSelector.WindowsApp, "Package_test", true)));
        var policy = new RouteSharedPolicy(main, (_, _) => Task.FromResult(new RouteSocksEndpoint(12345)));
        var tree = new RouteProcessTree(policy, []);
        var parent = Process(10, "launcher.exe", family: "Package_test");
        var child = Process(20, "worker.exe", 10, 2);
        var core = Process(30, "xray.exe", 10, 2);
        tree.Update([parent, child, core]);
        await tree.Decide(child.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Selected);
        await tree.Decide(core.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
        tree.SetRules(new(new(Routing(App(RoutingSelector.WindowsApp, "Package_test", false))), (_, _) => Task.FromResult(new RouteSocksEndpoint(12345))), []);
        await tree.Decide(child.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unselected);
    }

    [Test]
    [Arguments("black")]
    [Arguments("white")]
    [Arguments("global")]
    public async Task BuiltInRoutingPresetsKeepEveryBranchWhenApplicationRoutingStarts(string preset)
    {
        var rules = RoutingBlockRules.Read(EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + preset));
        rules.Insert(0, App(RoutingSelector.Process, "client.exe"));
        var routing = Routing(rules.ToArray());
        var shared = new RouteSharedRules(routing);
        var config = CoreConfigTestFactory.CreateConfig();
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray)
            with { RoutingItem = routing };
        var generated = new CoreConfigV2rayService(context).GenerateClientSocksConfig(12345, true, shared.Projection);
        await generated.Success.Should().BeTrue();
        var template = new RouteSharedTemplate((string)generated.Data!, shared);
        var native = template.RulesFor(shared.Branches.Select(b => b.Marker).ToArray(), "identity");
        await native.Count.Should().BeEqualTo(shared.Branches.Count + 1);
        if (rules.Last().Port == "0-65535")
        {
            await native[^2]!["port"]!.GetValue<string>().Should().BeEqualTo("0-65535");
            await native[^2]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(rules.Last().OutboundTag);
        }
    }

    [Test]
    public async Task EmptyLegacyNetworkDoesNotSuppressApplicationRules()
    {
        var routing = Routing(new RulesItem { Process = ["client.exe"], Network = "", OutboundTag = Global.DirectTag });
        var shared = new RouteSharedRules(routing);
        var config = CoreConfigTestFactory.CreateConfig();
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray)
            with { RoutingItem = routing };
        var generated = new CoreConfigV2rayService(context).GenerateClientSocksConfig(12345, true, shared.Projection);
        await generated.Success.Should().BeTrue();
        var template = new RouteSharedTemplate((string)generated.Data!, shared);
        var native = template.RulesFor(shared.Branches.Select(b => b.Marker).ToArray(), "identity");
        await native[0]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.DirectTag);
        await native[0]!["network"].Should().BeNull();
    }

    [Test]
    public async Task InvalidProjectedRuleFailsGenerationWithItsOriginalDiagnostic()
    {
        var routing = Routing(App(RoutingSelector.Process, "client.exe"), new() { Process = ["client.exe"], Port = "70000", OutboundTag = Global.BlockTag });
        var shared = new RouteSharedRules(routing);
        var config = CoreConfigTestFactory.CreateConfig();
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray)
            with { RoutingItem = routing };
        var generated = new CoreConfigV2rayService(context).GenerateClientSocksConfig(12345, true, shared.Projection);
        await generated.Success.Should().BeFalse();
        await generated.Data.Should().BeNull();
        await generated.Msg.Should().Contain(ResUI.RoutingBlocksInvalidPort);
    }

    [Test]
    public async Task NativeProjectionKeepsRuleOrderCommonConstraintsAndAllDestinations()
    {
        var application = App(RoutingSelector.Process, "client.exe", outbound: "chosen");
        application.Blocks!.Filters.Add(new() { Selector = RoutingSelector.Port, Values = ["443"] });
        var routing = Routing(new() { Domain = ["domain:blocked.example"], OutboundTag = Global.BlockTag }, application,
            new() { Ip = ["192.0.2.0/24"], OutboundTag = Global.DirectTag });
        var shared = new RouteSharedRules(routing);
        var config = CoreConfigTestFactory.CreateConfig();
        var active = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray);
        var chosen = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "selected");
        var context = CoreConfigTestFactory.CreateContext(config, active, ECoreType.Xray) with { RoutingItem = routing };
        context.AllProxiesMap["remark:chosen"] = chosen;
        var generated = new CoreConfigV2rayService(context).GenerateClientSocksConfig(12345, true, shared.Projection);
        await generated.Success.Should().BeTrue();
        var template = new RouteSharedTemplate((string)generated.Data!, shared);
        var rules = template.RulesFor(shared.Branches.Select(b => b.Marker).ToArray(), "identity-one");
        await rules.Count.Should().BeEqualTo(4);
        await rules[0]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.BlockTag);
        await rules[1]!["port"]!.GetValue<string>().Should().BeEqualTo("443");
        await rules[1]!["outboundTag"]!.GetValue<string>().Should().Contain("selected");
        await rules[1]!["process"].Should().BeNull();
        await rules[2]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.DirectTag);
        await rules[3]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.ProxyTag);
        await rules.All(r => r!["inboundTag"]![0]!.GetValue<string>() == "identity-one").Should().BeTrue();
        await template.Root["routing"]!["rules"]!.AsArray().All(r => r!["inboundTag"] != null).Should().BeTrue();
    }

    [Test]
    public async Task FileConfiguredCorePreservesOrderedGeoRulesDestinationsAndInternalDns()
    {
        var routing = Routing(new() { Domain = ["geosite:ru-blocked-all"], OutboundTag = Global.BlockTag },
            new() { Ip = ["geoip:!private"], OutboundTag = Global.DirectTag },
            App(RoutingSelector.Process, "client.exe"), App(RoutingSelector.Process, "other.exe", outbound: Global.BlockTag));
        var shared = new RouteSharedRules(routing);
        var context = CoreConfigTestFactory.CreateContext(CoreConfigTestFactory.CreateConfig(),
            CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray) with { RoutingItem = routing };
        var generated = new CoreConfigV2rayService(context).GenerateClientSocksConfig(12345, true, shared.Projection);
        await generated.Success.Should().BeTrue();
        var template = new RouteSharedTemplate((string)generated.Data!, shared);
        template.Root["api"] = new JsonObject { ["listen"] = "127.0.0.1:12346" };
        var dns = new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("dns-in"), ["outboundTag"] = Global.DirectTag };
        template.Root["routing"]!["rules"]!.AsArray().Insert(0, dns);
        var original = template.Root.ToJsonString();

        // Exclude the other application's branch while retaining common native rules.
        var markers = shared.Branches.Take(3).Select(b => b.Marker).ToArray();
        var configured = JsonNode.Parse(template.ConfigFor(markers, "identity"))!;
        var native = configured["routing"]!["rules"]!.AsArray();
        var identity = native.Where(r => r!["inboundTag"]![0]!.GetValue<string>() == "identity").ToArray();
        await identity.Length.Should().BeEqualTo(4);
        await identity[0]!["domain"]![0]!.GetValue<string>().Should().BeEqualTo("geosite:ru-blocked-all");
        await identity[1]!["ip"]![0]!.GetValue<string>().Should().BeEqualTo("geoip:!private");
        await identity[2]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.DirectTag);
        await identity[3]!["outboundTag"]!.GetValue<string>().Should().BeEqualTo(Global.ProxyTag);
        await JsonNode.DeepEquals(native[0], dns).Should().BeTrue();
        foreach (var field in new[] { "outbounds", "dns" })
        { await JsonNode.DeepEquals(configured[field], template.Root[field]).Should().BeTrue(); }
        await configured["api"].Should().BeNull();
        await configured["inbounds"]!.AsArray().Count.Should().BeEqualTo(1);
        await configured["inbounds"]![0]!["tag"]!.GetValue<string>().Should().BeEqualTo("identity");
        await configured["inbounds"]![0]!["settings"]!["udp"]!.GetValue<bool>().Should().BeTrue();
        await template.Root.ToJsonString().Should().BeEqualTo(original);
    }

    [Test]
    public async Task DisabledRowsAndOtherInboundScopesDoNotCaptureUnrelatedProcesses()
    {
        var disabled = App(RoutingSelector.Process, "client.exe");
        disabled.Blocks!.Filters[0].Applications![0].Enabled = false;
        var other = App(RoutingSelector.WindowsApp, "Package_test");
        other.Blocks!.Filters.Add(new() { Selector = RoutingSelector.InboundTag, Values = ["tun"] });
        var main = new RouteSharedRules(Routing(disabled, other));
        await main.HasApplications.Should().BeFalse();
        var policy = new RouteSharedPolicy(main, (_, _) => throw new InvalidOperationException());
        await policy.Select([Process(10, "client.exe", family: "Package_test")]).Should().BeNull();
    }
}
