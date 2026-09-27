namespace ServiceLib.Models.Entities;

[Serializable]
public class RulesItem
{
    public string Id { get; set; }
    public string? Type { get; set; }
    public string? Port { get; set; }
    public string? Network { get; set; }
    public List<string>? InboundTag { get; set; }
    public string? OutboundTag { get; set; }
    public List<string>? Ip { get; set; }
    public List<string>? Domain { get; set; }
    public List<string>? Protocol { get; set; }
    public List<string>? Process { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Remarks { get; set; }
    public ERuleType? RuleType { get; set; }

    // The legacy fields remain an inactive fallback. Older clients ignore this extension.
    public RoutingRuleBlocks? Blocks { get; set; }

    [JsonIgnore]
    public bool IsEnabled => Blocks?.Enabled ?? Enabled;
}
