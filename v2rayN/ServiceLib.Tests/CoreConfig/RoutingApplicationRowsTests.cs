using ReactiveUI.Primitives;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.CoreConfig;

public partial class RoutingBlockTests
{
    [Test]
    public async Task RowValuesDefineMatchModeAndDisabledRowsNeverBroadenRouting()
    {
        const string path = "C:/Program Files/My App/client.exe";
        var source = Rule((RoutingSelector.Process, [path]));
        var editor = new RoutingRuleBlocksViewModel(source);
        var block = editor.Filters.Single();
        var row = block.Applications.Single();
        row.Value = "client.exe";
        await row.ToModel().Mode.Should().BeEqualTo(RoutingProcessMode.Name);
        await block.Values().Should().BeEquivalentTo(["client.exe"]);
        row.Value = "  \"C:\\Program Files\\My App\\\"  ";
        await row.ToModel().Mode.Should().BeEqualTo(RoutingProcessMode.Folder);
        await block.Values().Should().BeEquivalentTo(["C:/Program Files/My App/"]);
        row.Value = path;
        await row.ToModel().Mode.Should().BeEqualTo(RoutingProcessMode.FullPath);
        await block.Values().Should().BeEquivalentTo([path]);
        row.Enabled = false;
        row.IncludeChildren = true;
        await editor.TrySave().Should().BeTrue();
        var reopened = new RoutingRuleBlocksViewModel(JsonUtils.DeepCopy(source)).Filters.Single().Applications.Single();
        await reopened.Value.Should().BeEqualTo(path);
        await reopened.Enabled.Should().BeFalse();
        await reopened.IncludeChildren.Should().BeTrue();
        await CoreConfigV2rayService.CompileBlockRules(source, new()).Count.Should().BeEqualTo(0);
        await CoreConfigSingboxService.CompileBlockRule(source, new(), true).Should().BeNull();
    }

    [Test]
    [Arguments(RoutingProcessMode.FullPath, "C:/Program Files/My App/client.exe")]
    [Arguments(RoutingProcessMode.Folder, "C:/Program Files/My App/")]
    [Arguments(RoutingProcessMode.Name, "client.exe")]
    public async Task ExistingExplicitModesKeepTheirMeaningAfterEditing(RoutingProcessMode mode, string expected)
    {
        var source = Rule((RoutingSelector.Process, []));
        source.Blocks!.Filters[0].Applications = [new()
        {
            Value = "C:/Program Files/My App/client.exe", Mode = mode, Enabled = false, IncludeChildren = true,
        }];
        var original = JsonUtils.Serialize(source);
        var editor = new RoutingRuleBlocksViewModel(source);
        await editor.Filters.Single().Applications.Single().Value.Should().BeEqualTo(expected);
        await JsonUtils.Serialize(source).Should().BeEqualTo(original);
        await editor.TrySave().Should().BeTrue();
        var row = source.Blocks.Filters[0].Applications!.Single();
        await row.Value.Should().BeEqualTo(expected);
        await row.Mode.Should().BeEqualTo(mode);
        await row.Enabled.Should().BeFalse();
        await row.IncludeChildren.Should().BeTrue();
    }

    [Test]
    public async Task PackageNamesSortWithoutChangingIdentityOrFlagsAndMissingNamesFallBackToFamily()
    {
        var filter = new RoutingFilterViewModel(RoutingSelector.WindowsApp, ["One", "Two"]);
        var retained = filter.Applications[0];
        retained.Enabled = false;
        retained.IncludeChildren = true;
        filter.SetPackages(new Dictionary<string, string> { ["Two"] = "Alpha", ["Three"] = "Alpha", ["One"] = "Zulu", ["Missing"] = "" });
        await string.Join(',', filter.Applications.Select(row => row.Value)).Should().BeEqualTo("Three,Two,Missing,One");
        await retained.Enabled.Should().BeFalse();
        await retained.IncludeChildren.Should().BeTrue();
        await retained.DisplayName.Should().BeEqualTo("Zulu");
        await retained.ToModel().Value.Should().BeEqualTo("One");
        await filter.Applications.Single(row => row.Value == "Missing").DisplayName.Should().BeEqualTo("Missing");
        await filter.SortPackagesCmd.Execute().ToTask();
        await string.Join(',', filter.Applications.Select(row => row.Value)).Should().BeEqualTo("One,Missing,Two,Three");
        await retained.DeleteCmd.Execute().ToTask();
        await filter.Values().Should().BeEquivalentTo(["Missing", "Two", "Three"]);
        await filter.SortPackagesCmd.Execute().ToTask();
        await string.Join(',', filter.Applications.Select(row => row.Value)).Should().BeEqualTo("Three,Two,Missing");
    }

    [Test]
    public async Task PackagePickerNamesIncludeSelectionsHiddenBySearchAndAreNeverSavedAsIdentifiers()
    {
        using var picker = new AppRoutingPackageViewModel(["Family_A", "Family_B"],
            read: () => [new("Family_A", "Alpha", ""), new("Family_B", "Beta", "")]);
        await picker.Initialize();
        picker.Search = "Alpha";
        await picker.Packages.Count.Should().BeEqualTo(1);
        var source = Rule((RoutingSelector.WindowsApp, ["Family_A"]));
        var original = JsonUtils.Serialize(source);
        var editor = new RoutingRuleBlocksViewModel(source);
        editor.Filters.Single().SetPackages(picker.SelectedPackageNames());
        await editor.Filters.Single().Applications.Select(row => row.DisplayName).Should().BeEquivalentTo(["Alpha", "Beta"]);
        await JsonUtils.Serialize(source).Should().BeEqualTo(original);
        await editor.TrySave().Should().BeTrue();
        await source.Blocks!.Filters[0].Applications!.Select(row => row.Value).Should().BeEquivalentTo(["Family_A", "Family_B"]);
    }

    [Test]
    public async Task CommentedDomainAndIpRowsAreIgnoredIncludingAllCommentedBlocks()
    {
        var rule = Rule((RoutingSelector.Domain, ["# disabled.example"]),
            (RoutingSelector.IP, ["# 192.0.2.1", "203.0.113.1"]), (RoutingSelector.Port, ["443"]));
        var ray = CoreConfigV2rayService.CompileBlockRules(rule, new()).Single();
        await ray.domain.Should().BeNull();
        await ray.ip!.Should().BeEquivalentTo(["203.0.113.1"]);
        await ray.port.Should().BeEqualTo("443");
        var box = CoreConfigSingboxService.CompileBlockRule(rule, new(), true)!;
        await box.rules![0].ip_cidr!.Should().BeEquivalentTo(["203.0.113.1"]);
        rule.Blocks!.Filters[1].Values = ["# disabled"];
        await CoreConfigV2rayService.CompileBlockRules(rule, new()).Count.Should().BeEqualTo(0);
        await CoreConfigSingboxService.CompileBlockRule(rule, new(), true).Should().BeNull();
    }

    [Test]
    public async Task ProcessButtonsAddUnambiguousValuesAndCancellationLeavesRowsUnchanged()
    {
        var source = new RulesItem();
        var editor = new RoutingRuleBlocksViewModel(source);
        editor.AddFilter(RoutingSelector.Process);
        using var handler = editor.PickProcess.RegisterHandler(interaction =>
        {
            interaction.Input.SelectedProcess = new(9, "client.exe", "C:/An App/client.exe", 1, 1);
            interaction.SetOutput(true);
        });
        await editor.Filters.Single().ChooseCmd.Execute().ToTask();
        await editor.Filters.Single().Applications.Single().Enabled.Should().BeTrue();
        await editor.Filters.Single().Applications.Single().Value.Should().BeEqualTo("client.exe");
        using (editor.BrowseProcessPath.RegisterHandler(interaction => interaction.SetOutput(
            interaction.Input == RoutingProcessMode.Folder ? "C:\\An App.exe" : "C:\\An App\\client.exe")))
        {
            await editor.Filters.Single().AddFullPathCmd.Execute().ToTask();
            await editor.Filters.Single().AddFolderCmd.Execute().ToTask();
        }
        await editor.Filters.Single().Values().Should().BeEquivalentTo(["client.exe", "C:/An App/client.exe", "C:/An App.exe/"]);
        using (editor.BrowseProcessPath.RegisterHandler(interaction => interaction.SetOutput(null)))
        {
            await editor.Filters.Single().AddFullPathCmd.Execute().ToTask();
            await editor.Filters.Single().AddFolderCmd.Execute().ToTask();
        }
        await editor.Filters.Single().Applications.Count.Should().BeEqualTo(3);
        await source.Blocks.Should().BeNull();
        var config = CoreConfigTestFactory.CreateConfig();
        config.Inbound[0].SecondLocalPortEnabled = true;
        config.Inbound[0].AllowLANConn = true;
        config.Inbound[0].NewPort4LAN = true;
        config.TunModeItem.EnableTun = true;
        await RoutingFilterViewModel.AvailableInbounds(config).Should().BeEquivalentTo(["socks", "app-routing", "socks2", "socks3", "tun"]);
    }
}
