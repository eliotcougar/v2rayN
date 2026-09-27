using ServiceLib.Services.AppRouting;

namespace ServiceLib.Services.CoreConfig;

/// <summary>Resolves stable package families once per configuration, never on the packet path.</summary>
public sealed class RoutingPackageSnapshot
{
    public IReadOnlyDictionary<string, string> Roots { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static RoutingPackageSnapshot Read(IReadOnlyList<RulesItem> rules)
    {
        var selected = rules.Where(r => r.IsEnabled).SelectMany(r => RoutingBlockRules.Values(r, RoutingSelector.WindowsApp))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) { return new(); }
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(ResUI.RoutingBlocksWindowsOnly); }
        var roots = RoutePackageCatalog.Read(selected).Where(p => selected.Contains(p.Family))
            .ToDictionary(p => p.Family, p => Normalize(p.InstallPath!).TrimEnd('/') + "/", StringComparer.OrdinalIgnoreCase);
        return new() { Roots = roots };
    }

    internal List<string> Resolve(IEnumerable<string> families) => families.Where(Roots.ContainsKey)
        .Select(f => Roots[f]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    internal static string Normalize(string path) => path.Trim().Trim('"').Replace('\\', '/');

}
