using System.Collections.Generic;

namespace Orayo.Models;

public class AppSettings
{
    public int LocalSocksPort { get; set; } = 10808;

    public int LocalHttpPort { get; set; } = 10809;

    public string LastSelectedServerId { get; set; } = string.Empty;

    public string RoutingMode { get; set; } = "smart";

    public bool IsTunMode { get; set; }

    public bool IsSystemProxyEnabled { get; set; } = true;

    public string? LastTunServerHost { get; set; }

    public string? RoutingRuleJson { get; set; }

    public string? DnsJson { get; set; }

    public List<CustomRoutingRule>? CustomRules { get; set; }
}

