namespace ServiceLib.Models.Entities;

public enum RoutingSelector
{
    // Persisted values: append new selectors without renumbering existing ones.
    Domain = 0, IP = 1, Port = 2, Process = 3, WindowsApp = 4, Protocol = 5, InboundTag = 6, Network = 7
}

public sealed class RoutingFilter
{
    public RoutingSelector Selector { get; set; }
    public List<string> Values { get; set; } = [];
    public List<RoutingApplicationRow>? Applications { get; set; }

    public List<string> EffectiveValues() => Applications == null ? Values
        : Applications.Where(row => row.Enabled).Select(row => row.MatchValue(Selector)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public enum RoutingProcessMode { FullPath = 0, Folder = 1, Name = 2 }

/// <summary>The original selection is retained when changing match mode.</summary>
public sealed class RoutingApplicationRow
{
    public bool Enabled { get; set; } = true;
    public string Value { get; set; } = "";
    public RoutingProcessMode Mode { get; set; }
    public bool IncludeChildren { get; set; }

    public string MatchValue(RoutingSelector selector)
    {
        var value = Value.Trim().Trim('"').Replace('\\', '/');
        if (selector == RoutingSelector.WindowsApp) { return value; }
        return Mode switch
        {
            RoutingProcessMode.Name => value[(value.LastIndexOf('/') + 1)..],
            RoutingProcessMode.Folder => value.EndsWith('/') ? value : value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? value[..(value.LastIndexOf('/') + 1)] : value + "/",
            _ => value,
        };
    }

    public static RoutingApplicationRow FromValue(string value, RoutingSelector selector)
    {
        value = value.Trim().Trim('"').Replace('\\', '/');
        return new()
        {
            Value = value,
            Mode = selector == RoutingSelector.WindowsApp ? RoutingProcessMode.FullPath
                : value.EndsWith('/') ? RoutingProcessMode.Folder
                : value.Contains('/') ? RoutingProcessMode.FullPath : RoutingProcessMode.Name,
        };
    }
}

/// <summary>Alternative match selectors plus common constraints. Values within each filter are alternatives.</summary>
public sealed class RoutingRuleBlocks
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<RoutingFilter> Filters { get; set; } = [];
}
