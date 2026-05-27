using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Orayo;

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

    public static string EnsureRoutingJson(string? routingJson)
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

        return CreateDefaultRoutingJson();
    }

    public static string EnsureRoutingBodyJson(string? routingJson)
    {
        return ExtractRoutingBody(EnsureRoutingJson(routingJson));
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

    public static int CountRules(string? routingJson)
    {
        var routing = EnsureRoutingObject(routingJson);
        return routing["rules"] is JsonArray rules ? rules.Count : 0;
    }

    public static JsonObject EnsureRoutingObject(string? routingJson)
    {
        try
        {
            return ParseRoutingObject(EnsureRoutingJson(routingJson));
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
            throw new JsonException(Strings.ErrRoutingJsonEmpty);
        }

        var node = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException(Strings.ErrRoutingJsonNotObject);

        node["rules"] = ValidateRulesArray(node["rules"] as JsonArray ?? throw new JsonException(Strings.ErrRoutingJsonMissingRules));
        node["domainStrategy"] = string.IsNullOrWhiteSpace(node["domainStrategy"]?.GetValue<string>())
            ? "IPIfNonMatch"
            : node["domainStrategy"]!.GetValue<string>();

        return node.DeepClone() as JsonObject ?? CreateDefaultRoutingObject();
    }

    private static JsonObject ParseRoutingBodyToObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException(Strings.ErrRoutingJsonEmpty);
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
                throw new JsonException(string.Format(Strings.ErrRoutingRuleNotObject, i + 1));
            }

            rule["type"] = string.IsNullOrWhiteSpace(rule["type"]?.GetValue<string>()) ? "field" : rule["type"]!.GetValue<string>();
            if (!string.Equals(rule["type"]?.GetValue<string>(), "field", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException(string.Format(Strings.ErrRoutingRuleType, i + 1));
            }

            if (string.IsNullOrWhiteSpace(rule["outboundTag"]?.GetValue<string>()))
            {
                throw new JsonException(string.Format(Strings.ErrRoutingRuleMissingTag, i + 1));
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
}

