using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class LifecycleTests
{
    [Test]
    public async Task EveryReloadAppliesCurrentConfigurationAndDisabledSettingStopsRouting()
    {
        var config = new Config { AppRouting = new() { Enabled = true }, RoutingBasicItem = new() };
        var runtime = new Runtime();
        await AppRoutingLifecycle.SynchronizeAsync(config, runtime);
        config.RoutingBasicItem.DomainStrategy = "IPIfNonMatch";
        await AppRoutingLifecycle.SynchronizeAsync(config, runtime);
        await runtime.Starts.Should().BeEqualTo(2);
        await runtime.LastConfig!.RoutingBasicItem.DomainStrategy.Should().BeEqualTo("IPIfNonMatch");
        config.AppRouting.Enabled = false;
        await AppRoutingLifecycle.SynchronizeAsync(config, runtime);
        await runtime.Stops.Should().BeEqualTo(1);
        await runtime.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task FailedStartIsRetriedOnNextReloadWithoutLosingSavedEnablement()
    {
        var config = new Config { AppRouting = new() { Enabled = true } };
        var runtime = new Runtime { FailStart = true };
        await Assert.ThrowsAsync<IOException>(() => AppRoutingLifecycle.SynchronizeAsync(config, runtime));
        await config.AppRouting.Enabled.Should().BeTrue();
        runtime.FailStart = false;
        await AppRoutingLifecycle.SynchronizeAsync(config, runtime);
        await runtime.IsEnabled.Should().BeTrue();
        await runtime.Starts.Should().BeEqualTo(2);
        await runtime.StopAsync();
        await AppRoutingLifecycle.SynchronizeAsync(JsonUtils.DeepCopy(config), runtime);
        await runtime.IsEnabled.Should().BeTrue();
    }

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

    private sealed class Runtime : IAppRoutingRuntime
    {
        public bool IsEnabled { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public bool FailStart { get; set; }
        public Config? LastConfig { get; private set; }
        public Task StartAsync(Config config)
        {
            Starts++;
            if (FailStart) { throw new IOException("Fixture startup failure"); }
            LastConfig = JsonUtils.DeepCopy(config);
            IsEnabled = true;
            return Task.CompletedTask;
        }
        public Task StopAsync() { Stops++; IsEnabled = false; return Task.CompletedTask; }
    }
}
