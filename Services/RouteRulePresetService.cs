using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Orayo.Models;

namespace Orayo.Services;

public static class RouteRulePresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string CreateDefaultRoutingJson() => CreateDefaultRoutingObject().ToJsonString(JsonOptions);

    public static string CreateDefaultRoutingBodyJson() => ExtractRoutingBody(CreateDefaultRoutingObject().ToJsonString(JsonOptions));

    public static List<CustomRoutingRule> CreateDefaultRules()
    {
        var routing = CreateDefaultRoutingObject();
        return ConvertRoutingObjectToLegacyRules(routing);
    }

    public static List<CustomRoutingRule> EnsureRules(IEnumerable<CustomRoutingRule>? rules)
    {
        if (rules is null)
        {
            return CreateDefaultRules();
        }

        var list = new List<CustomRoutingRule>();
        foreach (var rule in rules)
        {
            list.Add(rule.Clone());
        }

        return list.Count > 0 ? list : CreateDefaultRules();
    }

    public static string EnsureRoutingJson(string? routingJson, IEnumerable<CustomRoutingRule>? legacyRules = null)
    {
        if (!string.IsNullOrWhiteSpace(routingJson))
        {
            try
            {
                return FormatRoutingJson(routingJson);
            }
            catch
            {
            }
        }

        if (legacyRules is not null)
        {
            var migrated = TryConvertLegacyRules(legacyRules);
            if (migrated is not null)
            {
                return migrated.ToJsonString(JsonOptions);
            }
        }

        return CreateDefaultRoutingJson();
    }

    public static string EnsureRoutingBodyJson(string? routingJson, IEnumerable<CustomRoutingRule>? legacyRules = null)
    {
        return ExtractRoutingBody(EnsureRoutingJson(routingJson, legacyRules));
    }

    public static string FormatRoutingJson(string json)
    {
        return ParseRoutingObject(json).ToJsonString(JsonOptions);
    }

    public static string FormatRoutingBodyJson(string json)
    {
        return ExtractRoutingBody(ParseRoutingBodyToObject(json).ToJsonString(JsonOptions));
    }

    public static string BuildRoutingJsonFromBody(string json)
    {
        return ParseRoutingBodyToObject(json).ToJsonString(JsonOptions);
    }

    public static int CountRules(string? routingJson, IEnumerable<CustomRoutingRule>? legacyRules = null)
    {
        var routing = EnsureRoutingObject(routingJson, legacyRules);
        return routing["rules"] is JsonArray rules ? rules.Count : 0;
    }

    public static JsonObject EnsureRoutingObject(string? routingJson, IEnumerable<CustomRoutingRule>? legacyRules = null)
    {
        try
        {
            return ParseRoutingObject(EnsureRoutingJson(routingJson, legacyRules));
        }
        catch
        {
            return CreateDefaultRoutingObject();
        }
    }

    public static JsonObject ParseRoutingObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("routing JSON 不能为空。");
        }

        var node = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("routing JSON 必须是一个对象。");

        node["rules"] = ValidateRulesArray(node["rules"] as JsonArray ?? throw new JsonException("routing JSON 缺少 rules 数组。"));
        node["domainStrategy"] = string.IsNullOrWhiteSpace(node["domainStrategy"]?.GetValue<string>())
            ? "IPIfNonMatch"
            : node["domainStrategy"]!.GetValue<string>();

        return node.DeepClone() as JsonObject ?? CreateDefaultRoutingObject();
    }

    private static JsonObject ParseRoutingBodyToObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("routing JSON 不能为空。");
        }

        var wrapped = "{" + Environment.NewLine + json.Trim() + Environment.NewLine + "}";
        return ParseRoutingObject(wrapped);
    }

    private static string ExtractRoutingBody(string routingJson)
    {
        var normalized = routingJson.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length < 3)
        {
            return normalized.Trim().TrimStart('{').TrimEnd('}').Trim();
        }

        var bodyLines = new List<string>();
        for (var i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            bodyLines.Add(line.Length >= 2 ? line[2..] : line.TrimStart());
        }

        return string.Join(Environment.NewLine, bodyLines).Trim();
    }

    private static JsonArray ValidateRulesArray(JsonArray rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is not JsonObject rule)
            {
                throw new JsonException($"第 {i + 1} 条规则必须是对象。");
            }

            rule["type"] = string.IsNullOrWhiteSpace(rule["type"]?.GetValue<string>()) ? "field" : rule["type"]!.GetValue<string>();
            if (!string.Equals(rule["type"]?.GetValue<string>(), "field", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException($"第 {i + 1} 条规则的 type 目前只支持 field。");
            }

            if (string.IsNullOrWhiteSpace(rule["outboundTag"]?.GetValue<string>()))
            {
                throw new JsonException($"第 {i + 1} 条规则缺少 outboundTag。");
            }
        }

        return rules.DeepClone() as JsonArray ?? new JsonArray();
    }

    private static JsonObject CreateDefaultRoutingObject()
    {
        return new JsonObject
        {
            ["domainStrategy"] = "IPIfNonMatch",
            ["rules"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "block",
                    ["domain"] = new JsonArray("geosite:category-ads-all")
                },
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["domain"] = new JsonArray(
                        "geosite:private",
                        "geosite:cn",
                        "geosite:apple-cn",
                        "geosite:google-cn",
                        "geosite:tld-cn",
                        "geosite:category-games@cn")
                },
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["domain"] = new JsonArray("geosite:geolocation-!cn")
                },
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "direct",
                    ["ip"] = new JsonArray("geoip:cn", "geoip:private")
                },
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["network"] = "tcp,udp"
                }
            }
        };
    }

    private static List<CustomRoutingRule> ConvertRoutingObjectToLegacyRules(JsonObject routing)
    {
        var list = new List<CustomRoutingRule>();
        if (routing["rules"] is not JsonArray rules)
        {
            return list;
        }

        var index = 1;
        foreach (var node in rules)
        {
            if (node is not JsonObject rule)
            {
                continue;
            }

            var outboundTag = rule["outboundTag"]?.GetValue<string>() ?? "proxy";
            if (rule["domain"] is JsonArray domains && domains.Count > 0)
            {
                foreach (var domain in domains)
                {
                    list.Add(new CustomRoutingRule
                    {
                        Name = $"规则 {index++}",
                        Type = "domain",
                        Match = domain?.GetValue<string>() ?? string.Empty,
                        OutboundTag = outboundTag,
                        IsEnabled = true,
                    });
                }
            }
            else if (rule["ip"] is JsonArray ips && ips.Count > 0)
            {
                foreach (var ip in ips)
                {
                    list.Add(new CustomRoutingRule
                    {
                        Name = $"规则 {index++}",
                        Type = "ip",
                        Match = ip?.GetValue<string>() ?? string.Empty,
                        OutboundTag = outboundTag,
                        IsEnabled = true,
                    });
                }
            }
            else if (rule["process"] is JsonArray processes && processes.Count > 0)
            {
                foreach (var process in processes)
                {
                    list.Add(new CustomRoutingRule
                    {
                        Name = $"规则 {index++}",
                        Type = "process",
                        Match = process?.GetValue<string>() ?? string.Empty,
                        OutboundTag = outboundTag,
                        IsEnabled = true,
                    });
                }
            }
        }

        return list;
    }

    private static JsonObject? TryConvertLegacyRules(IEnumerable<CustomRoutingRule> legacyRules)
    {
        var rules = new JsonArray();
        foreach (var legacy in legacyRules)
        {
            if (string.IsNullOrWhiteSpace(legacy.Match))
            {
                continue;
            }

            var rule = new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = string.IsNullOrWhiteSpace(legacy.OutboundTag) ? "proxy" : legacy.OutboundTag
            };

            switch (legacy.Type?.Trim().ToLowerInvariant())
            {
                case "ip":
                    rule["ip"] = new JsonArray(legacy.Match);
                    break;
                case "process":
                    rule["process"] = new JsonArray(legacy.Match);
                    break;
                default:
                    rule["domain"] = new JsonArray(legacy.Match);
                    break;
            }

            rules.Add(rule);
        }

        if (rules.Count == 0)
        {
            return null;
        }

        var hasProxyFallback = false;
        foreach (var node in rules)
        {
            if (node is JsonObject rule && string.Equals(rule["network"]?.GetValue<string>(), "tcp,udp", StringComparison.OrdinalIgnoreCase))
            {
                hasProxyFallback = true;
                break;
            }
        }

        if (!hasProxyFallback)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "proxy",
                ["network"] = "tcp,udp"
            });
        }

        return new JsonObject
        {
            ["domainStrategy"] = "IPIfNonMatch",
            ["rules"] = rules
        };
    }
}

