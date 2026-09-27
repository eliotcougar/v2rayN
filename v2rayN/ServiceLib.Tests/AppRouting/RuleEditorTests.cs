using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Extensions;
using ReactiveUI.Primitives.Signals;
using ServiceLib.Services.AppRouting;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.AppRouting;

// These headless view-model tests share ReactiveUI scheduler initialization.
[NotInParallel]
public class RuleEditorTests
{
    static RuleEditorTests()
    {
        // Headless tests have no UI dispatcher: run UI notifications synchronously.
        RxAppBuilder.CreateReactiveUIBuilder().WithMainThreadScheduler(ImmediateSequencer.Instance)
            .WithCoreServices().BuildApp();
    }

    [Test]
    public async Task PathsWithSpacesAndQuotesSurviveNormalizationAndSerialization()
    {
        var path = Path.Combine(Path.GetTempPath(), "Applications With Spaces", "My Network App.exe");
        var normalized = AppRouteMatcher.Normalize("  \"" + path + "\"  ", false);
        var rule = JsonUtils.DeepCopy(new AppRouteRule { ExecutablePath = normalized });
        await rule.ExecutablePath.Should().BeEqualTo(path);
        await new AppRouteMatcher([rule]).Find(path, "My Network App.exe").Should().BeEqualTo(rule);
        await rule.MatchByName.Should().BeFalse();
    }

    [Test]
    public async Task NameRuleMatchesAllLocationsButExactPathWinsRegardlessOfOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), "App One", "browser.exe");
        var exact = new AppRouteRule { ExecutablePath = path };
        var byName = new AppRouteRule { ExecutablePath = "BROWSER.EXE", MatchByName = true };
        foreach (var rules in new[] { new[] { exact, byName }, new[] { byName, exact } })
        {
            var matcher = new AppRouteMatcher(rules);
            await matcher.Find(path, "browser.exe").Should().BeEqualTo(exact);
            await matcher.Find(Path.Combine(Path.GetTempPath(), "App Two", "browser.exe"), "browser.exe").Should().BeEqualTo(byName);
            await matcher.Find(null, "browser.exe").Should().BeEqualTo(byName);
            await (matcher.Find(null, "browser-helper.exe") == null).Should().BeTrue();
        }
    }

    [Test]
    public async Task NameRulesNeedNoInstalledFileAndRemainNamesAfterReload()
    {
        var name = "network-fixture-" + Guid.NewGuid().ToString("N") + ".exe";
        var rule = new AppRouteRule { ExecutablePath = name, MatchByName = true, Kind = AppRouteKind.ActiveProfile };
        var copy = JsonUtils.DeepCopy(rule);
        await copy.MatchByName.Should().BeTrue();
        await copy.ExecutablePath.Should().BeEqualTo(name);
        await AppRouteMatcher.Normalize(Path.Combine(Path.GetTempPath(), "Program Files", "My App.exe"), true).Should().BeEqualTo("My App.exe");
    }

    [Test]
    public async Task RemovedExecutableDoesNotInvalidateOtherRulesOrBroadenPathMatching()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Removed app.exe");
        var removed = new AppRouteRule { ExecutablePath = path, Kind = AppRouteKind.ActiveProfile };
        var other = new AppRouteRule { ExecutablePath = "Installed app.exe", MatchByName = true, Kind = AppRouteKind.ActiveProfile };
        var reloaded = JsonUtils.DeepCopy(new List<AppRouteRule> { removed, other });
        var matcher = new AppRouteMatcher(reloaded);
        await matcher.Find(null, "Installed app.exe").Should().BeEqualTo(reloaded[1]);
        await (matcher.Find(null, "Removed app.exe") == null).Should().BeTrue();
        await matcher.Find(path, "Removed app.exe").Should().BeEqualTo(reloaded[0]);
    }

    [Test]
    public async Task NetworkPickerGroupsAllProtocolsAndFiltersNamePidAndFullPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Program Files", "Network App.exe");
        var apps = AppRouteProcessCatalog.Build([(10, 6), (10, 6), (10, 17), (20, 17), (30, 6), (40, 6)],
            pid => pid switch { 10 => ("Network App.exe", path), 20 => ("Another.exe", null), 30 => ("xray.exe", path), _ => null });
        await apps.Count.Should().BeEqualTo(2);
        await apps[0].Pid.Should().BeEqualTo(10);
        await apps[0].TcpConnections.Should().BeEqualTo(2);
        await apps[0].UdpEndpoints.Should().BeEqualTo(1);
        await apps[0].MatchesSearch("network app").Should().BeTrue();
        await apps[0].MatchesSearch("Program Files").Should().BeTrue();
        await apps[0].MatchesSearch("10").Should().BeTrue();
        await apps[0].MatchesSearch("not present").Should().BeFalse();
        await (apps[1].Path == null).Should().BeTrue();
    }

    [Test]
    public async Task NetworkPickerSeesOwnedUdpEndpointWithoutDriver()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var apps = AppRouteProcessCatalog.Read();
        var own = apps.Single(p => p.Pid == Environment.ProcessId);
        await (own.UdpEndpoints > 0).Should().BeTrue();
        await Path.IsPathFullyQualified(own.Path!).Should().BeTrue();
    }

    [Test]
    public async Task NetworkPickerHidesSystemDirectoriesWithoutFilteringNamesElsewhere()
    {
        var windows = Path.Combine(Path.GetTempPath(), "Fixture Windows");
        var resolvedSystem = false;
        var apps = AppRouteProcessCatalog.Build(
            [(4, 6), (10, 6), (20, 6), (30, 17), (40, 6), (50, 6), (60, 17), (70, 6), (80, 6), (90, 6)],
            pid =>
            {
                if (pid == 4)
                {
                    resolvedSystem = true;
                }

                return pid switch
                {
                    10 => ("svchost.exe", Path.Combine(windows, "System32", "svchost.exe")),
                    20 => ("services.exe", Path.Combine(windows, "SYSWOW64", "services.exe")),
                    30 => ("wininit.exe", Path.Combine(windows, "System32", "nested", "wininit.exe")),
                    40 => ("System.exe", null),
                    50 => ("svchost.exe", Path.Combine(windows, "System32-tools", "svchost.exe")),
                    60 => ("services.exe", Path.Combine(Path.GetTempPath(), "User apps", "services.exe")),
                    70 => ("Unknown path.exe", null),
                    80 => ("System.exe", Path.Combine(Path.GetTempPath(), "User apps", "System.exe")),
                    90 => ("wininit.exe", Path.Combine(windows, "SysWOW64", "..", "System32", "wininit.exe")),
                    _ => ("System.exe", null)
                };
            }, windows);
        await resolvedSystem.Should().BeFalse();
        await apps.Select(app => app.Pid).Order().ToArray().Should().BeEquivalentTo(new[] { 50, 60, 70, 80 });
    }

    [Test]
    public async Task CompactInterfacePickerKeepsDisconnectedAdaptersButHidesHistoricalRows()
    {
        var active = InterfaceTests.Adapter("active", 1);
        var disconnected = InterfaceTests.Adapter("vpn", 0, "VPN") with { Status = System.Net.NetworkInformation.OperationalStatus.Down };
        var config = new Config();
        config.AppRouting.InterfaceMonitoring = RouteInterfaceCatalog.Discover(new(),
            [active, disconnected, InterfaceTests.Adapter("old-filter", 0, "Ethernet-QoS"), InterfaceTests.Adapter("removed", 7)]);
        config.AppRouting.InterfaceMonitoring.Interfaces.Single(i => i.Id == "removed").Monitored = false;
        await using var monitor = new RouteInterfaceMonitor(config, _ => Task.FromResult(0), () => [active, disconnected], _ => { });
        var snapshot = await monitor.RefreshAsync();
        var editor = new AppRoutingInterfaceViewModel(snapshot.Options, snapshot.Adapters);
        await editor.Interfaces.Select(i => i.Id).Order().SequenceEqual(new[] { "active", "vpn" }).Should().BeTrue();
        editor.Interfaces.Single(i => i.Id == "active").Monitored = false;
        await monitor.SaveAsync(editor.ToOptions());
        await config.AppRouting.InterfaceMonitoring.Interfaces.Single(i => i.Id == "removed").Monitored.Should().BeFalse();
        await config.AppRouting.InterfaceMonitoring.Interfaces.Single(i => i.Id == "vpn").Monitored.Should().BeTrue();
        await monitor.Policy.Monitors(1, false).Should().BeFalse();
    }

    [Test]
    public async Task PackagePickerSearchKeepsSelectionAndImportMergesWithoutStealingOtherRules()
    {
        var catalog = new[] { new RoutePackage("One", "First", "Publisher"), new RoutePackage("Two", "Second", "Publisher"),
            new RoutePackage("Three", "Third", "Publisher") };
        using var editor = new AppRoutingPackageViewModel("Group", ["Missing"], new Dictionary<string, string> { ["Two"] = "Other rule" },
            () => catalog, _ => new(["One", "Two"], 2));
        await editor.Initialize();
        await editor.Packages.Single(p => p.Family == "Missing").Selected.Should().BeTrue();
        await editor.Packages.Single(p => p.Family == "Two").CanSelect.Should().BeFalse();
        editor.Search = "Third";
        await editor.SelectVisibleCmd.Execute().ToTask();
        await editor.ImportCmd.Execute().ToTask();
        await editor.SelectedFamilies().Order().SequenceEqual(new[] { "Missing", "One", "Three" }).Should().BeTrue();
        await editor.Packages.Single().Family.Should().BeEqualTo("Three");
        await editor.ClearVisibleCmd.Execute().ToTask();
        await editor.SelectedFamilies().Order().SequenceEqual(new[] { "Missing", "One" }).Should().BeTrue();
        await editor.Status.Should().BeEqualTo(string.Format(ServiceLib.Resx.ResUI.AppRoutingPackageImportResult, 1, 2, 1));
        await editor.CanConfirm.Should().BeTrue();
        editor.Name = "  ";
        await editor.CanConfirm.Should().BeFalse();
    }

    [Test]
    public async Task FailedPackageImportKeepsExistingChecksAndDialogCanStillBeSaved()
    {
        using var editor = new AppRoutingPackageViewModel("Group", ["One"], new Dictionary<string, string>(),
            () => [new("One", "First", "")], _ => throw new IOException("Fixture import failure"));
        await editor.Initialize();
        await editor.ImportCmd.Execute().ToTask();
        await editor.SelectedFamilies().Single().Should().BeEqualTo("One");
        await editor.CanConfirm.Should().BeTrue();
        await editor.Status.Contains("Fixture import failure").Should().BeTrue();
    }

    [Test]
    public async Task ClosedPackagePickerIgnoresLateInventoryCompletion()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var editor = new AppRoutingPackageViewModel("", [], new Dictionary<string, string>(), () =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            return [new("One", "First", "")];
        });
        var load = editor.Initialize();
        await entered.Task;
        editor.Dispose();
        release.SetResult();
        await load;
        await editor.Packages.Count.Should().BeEqualTo(0);
    }

}
