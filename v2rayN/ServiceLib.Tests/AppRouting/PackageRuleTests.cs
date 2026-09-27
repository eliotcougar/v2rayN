using ServiceLib.Services.AppRouting;

namespace ServiceLib.Tests.AppRouting;

public class PackageRuleTests
{
    internal const string Family = "Example.NetworkApp_123456789abcd";
    internal static AppRouteRule Group(params string[] families) => new()
    {
        MatchKind = AppRouteMatchKind.WindowsApp, Name = "Windows applications", PackageFamilies = [.. families], Kind = AppRouteKind.ActiveProfile
    };

    [Test]
    public async Task PackageGroupsRoundTripAndOldRulesKeepProcessMatching()
    {
        var group = JsonUtils.DeepCopy(Group(Family, "Example.Other_123456789abcd"));

        await group.PackageFamilies.Count.Should().BeEqualTo(2);
        var matcher = new AppRouteMatcher([group]);
        await matcher.Find(null, "Host.exe", Family.ToUpperInvariant()).Should().BeEqualTo(group);
        await matcher.Find(null, "Host.exe", group.PackageFamilies[1]).Should().BeEqualTo(group);
        await matcher.Find(null, "Host.exe", "Example.OtherPublisher_987654321abcd").Should().BeNull();
        var old = JsonUtils.Deserialize<AppRouteRule>("{\"executablePath\":\"App.exe\",\"matchByName\":true,\"kind\":3}")!;
        await old.MatchKind.Should().BeEqualTo(AppRouteMatchKind.Process);
        await new AppRouteMatcher([old]).Find(null, "App.exe", Family).Should().BeEqualTo(old);
    }

    [Test]
    public async Task ExplicitProcessRulesOverridePackagesRegardlessOfOrder()
    {
        var group = Group(Family);
        var name = new AppRouteRule { ExecutablePath = "App.exe", MatchByName = true };
        var path = new AppRouteRule { ExecutablePath = Path.Combine(Path.GetTempPath(), "App.exe") };
        foreach (var rules in new[] { new[] { group, name, path }, new[] { path, name, group } })
        {
            var matcher = new AppRouteMatcher(rules);
            await matcher.Find(path.ExecutablePath, "App.exe", Family).Should().BeEqualTo(path);
            await matcher.Find(null, "App.exe", Family).Should().BeEqualTo(name);
            await matcher.Find(null, "Helper.exe", Family).Should().BeEqualTo(group);
        }
    }

    [Test]
    public async Task BrokeredPackageProcessesMatchWithoutMatchingUnrelatedHosts()
    {
        var rule = Group(Family);
        var tree = new RouteProcessTree(new([rule]), []);
        tree.Update([
            new(new(10, 1), 0, "RuntimeBroker.exe", null, PackageFamily: ""),
            new(new(20, 2), 10, "backgroundTaskHost.exe", null, PackageFamily: Family),
            new(new(30, 3), 10, "backgroundTaskHost.exe", null, PackageFamily: "Other_123456789abcd")]);
        await tree.Find(new(10, 1)).Should().BeNull();
        await tree.Find(new(20, 2)).Should().BeEqualTo(rule);
        await tree.Find(new(30, 3)).Should().BeNull();
    }

    [Test]
    public async Task PackageChildrenRespectGenerationAndCoreExclusions()
    {
        var rule = Group(Family);
        rule.IncludeChildProcesses = true;
        var tree = new RouteProcessTree(new([rule]), []);
        tree.Update([new(new(10, 1), 0, "App.exe", null, Exited: 3, PackageFamily: Family),
            new(new(20, 2), 10, "Helper.exe", null, PackageFamily: ""),
            new(new(10, 4), 0, "Unrelated.exe", null, PackageFamily: ""),
            new(new(30, 5), 10, "Helper.exe", null, PackageFamily: ""),
            new(new(40, 2), 0, "xray.exe", null, PackageFamily: Family)]);
        await tree.Find(new(20, 2)).Should().BeEqualTo(rule);
        await tree.Find(new(30, 5)).Should().BeNull();
        await tree.Find(new(40, 2)).Should().BeNull();
        rule.IncludeChildProcesses = false;
        tree.SetRules(new([rule]), []);
        await tree.Find(new(20, 2)).Should().BeNull();
    }

    [Test]
    public async Task UnknownIdentityWaitsAndLaterStopOrSnapshotDoesNotEraseKnownPackage()
    {
        var rule = Group(Family);
        var tree = new RouteProcessTree(new([rule]), []);
        var process = new RouteProcessInfo(new(10, 1), 0, "App.exe", null);
        tree.Update([process]);
        await tree.Decide(process.Key).Kind.Should().BeEqualTo(RouteDecisionKind.Unresolved);
        tree.Update([process with { PackageFamily = Family }]);
        tree.Update([process, process with { PackageFamily = "" }, new(process.Key, 0, "", null, Exited: 5)]);
        await tree.Find(process.Key).Should().BeEqualTo(rule);
        var explicitRule = new AppRouteRule { ExecutablePath = "Other.exe", MatchByName = true };
        tree.SetRules(new([rule, explicitRule]), []);
        tree.Update([new(new(20, 2), 0, "Other.exe", null)]);
        await tree.Find(new(20, 2)).Should().BeEqualTo(explicitRule);
    }

    [Test]
    public async Task PackageIdentityComesFromWindowsNotVersionedPaths()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string full = "Example.NetworkApp_1.2.3.4_x64__123456789abcd";
        await RoutePackageIdentity.FromFullName(full).Should().BeEqualTo(Family);
        await RoutePackageIdentity.FromFullName(full.Replace("1.2.3.4_x64", "5.6.7.8_x86")).Should().BeEqualTo(Family);
        await RoutePackageIdentity.FromFullName("").Should().BeEqualTo("");
        await RoutePackageIdentity.FromFullName("invalid").Should().BeNull();
    }

    [Test]
    public async Task NativePackageInventoryAndLoopbackImportAreReadOnlyAndArchitectureSafe()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var packages = await Task.Run(RoutePackageCatalog.Read);
        await packages.Select(p => p.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count().Should().BeEqualTo(packages.Count);
        await packages.All(p => p.Family.Length > 0 && p.Name.Length > 0).Should().BeTrue();
        var imported = RouteLoopbackImport.Read(packages);
        await imported.Families.All(f => packages.Any(p => p.Family == f)).Should().BeTrue();
        using var snapshot = new RouteProcessSnapshot();
        await snapshot.Read().Single(p => p.Key.Pid == Environment.ProcessId).PackageFamily.Should().BeNull();
        var process = snapshot.Read(true).Single(p => p.Key.Pid == Environment.ProcessId);
        await process.PackageFamily.Should().BeEqualTo("");
    }
}
