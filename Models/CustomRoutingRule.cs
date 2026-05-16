namespace Orayo.Models;

public class CustomRoutingRule
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = "domain";

    public string Match { get; set; } = string.Empty;

    public string OutboundTag { get; set; } = "proxy";

    public bool IsEnabled { get; set; } = true;

    public string DisplayType => Type switch
    {
        "ip" => "IP",
        "process" => "进程",
        _ => "域名"
    };

    public string DisplayOutbound => OutboundTag switch
    {
        "direct" => "直连",
        "block" => "阻止",
        _ => "代理"
    };

    public CustomRoutingRule Clone() => new()
    {
        Name = Name,
        Type = Type,
        Match = Match,
        OutboundTag = OutboundTag,
        IsEnabled = IsEnabled,
    };
}

