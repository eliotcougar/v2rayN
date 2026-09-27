using ReactiveUI.Primitives;
using ServiceLib.Services.AppRouting;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.AppRouting;

public class SettingsTests
{
    [Test]
    public async Task OldStandaloneRulesAreIgnoredWhilePreferencesAndInterfacesSurvive()
    {
        var config = JsonUtils.Deserialize<AppRoutingItem>("""
            {"Enabled":true,"Rules":[{"Enabled":false,"Kind":"retired","ProfileId":"missing"}],
             "InterfaceMonitoring":{"MonitorNewInterfaces":false,"Interfaces":[{"Id":"vpn","Name":"VPN","Monitored":false}]}}
            """)!;
        await config.Enabled.Should().BeTrue();
        await config.InterfaceMonitoring.Interfaces.Single().Monitored.Should().BeFalse();
        await config.InterfaceMonitoring.MonitorNewInterfaces.Should().BeFalse();
        await JsonUtils.Serialize(config).Contains("rules", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        await JsonUtils.Deserialize<AppRoutingItem>("{}")!.Enabled.Should().BeFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnablementIsOnlyCommittedWithSuccessfulSettingsSave(bool enabled)
    {
        var config = new Config { AppRouting = new() { Enabled = !enabled } };
        var editor = new AppRoutingSettingsViewModel(config, true, null) { Enabled = enabled };
        await config.AppRouting.Enabled.Should().BeEqualTo(!enabled);
        string? saved = null;
        await editor.SaveAsync(c => { saved = JsonUtils.Serialize(c); return Task.FromResult(0); });
        await JsonUtils.Deserialize<Config>(saved)!.AppRouting.Enabled.Should().BeEqualTo(enabled);
        await config.AppRouting.Enabled.Should().BeEqualTo(enabled);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FailedSaveRestoresPreferenceAndKeepsEditorDraft(bool enabled, bool throws)
    {
        var config = new Config { AppRouting = new() { Enabled = !enabled } };
        var editor = new AppRoutingSettingsViewModel(config, true, null) { Enabled = enabled };
        Task<int> Save(Config _) => throws ? Task.FromException<int>(new IOException("Fixture")) : Task.FromResult(-1);
        if (throws) { await Assert.ThrowsAsync<IOException>(() => editor.SaveAsync(Save)); }
        else { await (await editor.SaveAsync(Save)).Should().BeEqualTo(-1); }
        await config.AppRouting.Enabled.Should().BeEqualTo(!enabled);
        await editor.Enabled.Should().BeEqualTo(enabled);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NonAdministratorSeesSavedStateAndCannotChangeItThroughSave(bool enabled)
    {
        var config = new Config { AppRouting = new() { Enabled = enabled } };
        var editor = new AppRoutingSettingsViewModel(config, false, null);
        await editor.Enabled.Should().BeEqualTo(enabled);
        await editor.CanChangeRouting.Should().BeFalse();
        editor.Enabled = !enabled;
        await editor.SaveAsync(_ => Task.FromResult(0));
        await config.AppRouting.Enabled.Should().BeEqualTo(enabled);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InterfaceDialogCommitsOnlyOnConfirmAndPreservesUnsavedEnablement(bool confirm)
    {
        var config = new Config();
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(0),
            () => [InterfaceTests.Adapter("fixture", 7)], _ => { });
        var editor = new AppRoutingSettingsViewModel(config, true, monitor) { Enabled = true };
        using var handler = editor.PickInterfaces.RegisterHandler(interaction =>
        {
            interaction.Input.MonitorNewInterfaces = false;
            interaction.Input.Interfaces.Single().Monitored = false;
            interaction.SetOutput(confirm);
        });
        await editor.PickInterfacesCmd.Execute().ToTask();
        await config.AppRouting.InterfaceMonitoring.MonitorNewInterfaces.Should().BeEqualTo(!confirm);
        await monitor.Policy.Monitors(7, false).Should().BeEqualTo(!confirm);
        await config.AppRouting.Enabled.Should().BeFalse();
        await editor.Enabled.Should().BeTrue();
    }
}
