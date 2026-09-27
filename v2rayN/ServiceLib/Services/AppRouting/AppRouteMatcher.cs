namespace ServiceLib.Services.AppRouting;

internal sealed class AppRouteMatcher
{
    internal RouteSharedPolicy? Shared { get; }
    private readonly Dictionary<string, AppRouteRule> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppRouteRule> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppRouteRule> _packages = new(StringComparer.OrdinalIgnoreCase);
    public bool HasPackages => _packages.Count != 0 || Shared?.HasPackages == true;
    public bool PackagesIncludeChildren { get; }
    public bool IncludesChildren => Shared?.IncludesChildren == true || _paths.Values.Concat(_names.Values).Concat(_packages.Values).Any(rule => rule.IncludeChildProcesses);

    public bool NeedsPath(string name) => Shared?.NeedsPath(name) == true || _paths.Keys.Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    public AppRouteMatcher(IEnumerable<AppRouteRule> rules, RouteSharedPolicy? shared = null)
    {
        Shared = shared;
        PackagesIncludeChildren = shared?.PackagesIncludeChildren == true;
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            if (rule.MatchKind == AppRouteMatchKind.WindowsApp)
            {
                PackagesIncludeChildren |= rule.IncludeChildProcesses;
                foreach (var family in rule.PackageFamilies) { _packages.Add(family, rule); }
            }
            else
            {
                (rule.MatchByName ? _names : _paths).Add(Normalize(rule.ExecutablePath, rule.MatchByName), rule);
            }
        }
    }

    public static string Normalize(string value, bool matchByName)
    {
        var executable = value.Trim();
        if (executable.Length >= 2 && executable[0] == '"' && executable[^1] == '"')
        {
            executable = executable[1..^1];
        }

        if (matchByName)
        {
            // A path selected in the browser/picker may also be used to create a name rule.
            executable = Path.GetFileName(executable);
            if (executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || executable.Length == 0)
            {
                throw new ArgumentException(ResUI.AppRoutingInvalidExecutable);
            }
        }
        else
        {
            if (!Path.IsPathFullyQualified(executable))
            {
                throw new ArgumentException(ResUI.AppRoutingInvalidExecutable);
            }

            executable = Path.GetFullPath(executable);
        }
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(ResUI.AppRoutingInvalidExecutable);
        }

        return executable;
    }

    public AppRouteRule? Find(string? path, string executableName, string? packageFamily = null)
    {
        if (!string.IsNullOrEmpty(path) && _paths.TryGetValue(Path.GetFullPath(path), out var exact))
        {
            return exact;
        }

        // Explicit executable rules override the broader package selection.
        return _names.GetValueOrDefault(executableName) ??
            (packageFamily == null ? null : _packages.GetValueOrDefault(packageFamily));
    }
}
