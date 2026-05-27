using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Orayo;

namespace Orayo.Services;

public static class DnsPresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string CreateDefaultDnsJson() => CreateDefaultDnsObject().ToJsonString(JsonOptions);

    public static string CreateDefaultDnsBodyJson() => ExtractDnsBody(CreateDefaultDnsObject().ToJsonString(JsonOptions));

    public static string EnsureDnsJson(string? dnsJson)
    {
        if (!string.IsNullOrWhiteSpace(dnsJson))
        {
            try
            {
                return FormatDnsJson(dnsJson);
            }
            catch
            {
            }
        }

        return CreateDefaultDnsJson();
    }

    public static string EnsureDnsBodyJson(string? dnsJson)
    {
        return ExtractDnsBody(EnsureDnsJson(dnsJson));
    }

    public static string FormatDnsJson(string json)
    {
        return ParseDnsObject(json).ToJsonString(JsonOptions);
    }

    public static string FormatDnsBodyJson(string json)
    {
        return ExtractDnsBody(ParseDnsBodyToObject(json).ToJsonString(JsonOptions));
    }

    public static string BuildDnsJsonFromBody(string json)
    {
        return ParseDnsBodyToObject(json).ToJsonString(JsonOptions);
    }

    public static JsonObject EnsureDnsObject(string? dnsJson)
    {
        try
        {
            return ParseDnsObject(EnsureDnsJson(dnsJson));
        }
        catch
        {
            return CreateDefaultDnsObject();
        }
    }

    public static JsonObject ParseDnsObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException(Strings.ErrDnsJsonEmpty);
        }

        var node = JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException(Strings.ErrDnsJsonNotObject);

        node["hosts"] = node["hosts"] as JsonObject ?? new JsonObject();
        node["servers"] = ValidateServers(node["servers"] as JsonArray ?? throw new JsonException(Strings.ErrDnsJsonMissingServers));
        node["queryStrategy"] = string.IsNullOrWhiteSpace(node["queryStrategy"]?.GetValue<string>())
            ? "UseIPv4"
            : node["queryStrategy"]!.GetValue<string>();

        return node.DeepClone() as JsonObject ?? CreateDefaultDnsObject();
    }

    private static JsonObject ParseDnsBodyToObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException(Strings.ErrDnsJsonEmpty);
        }

        var wrapped = "{" + Environment.NewLine + json.Trim() + Environment.NewLine + "}";
        return ParseDnsObject(wrapped);
    }

    private static string ExtractDnsBody(string dnsJson)
    {
        var normalized = dnsJson.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length < 3)
        {
            return normalized.Trim().TrimStart('{').TrimEnd('}').Trim();
        }

        var bodyLines = new string[lines.Length - 2];
        for (var i = 1; i < lines.Length - 1; i++)
        {
            var line = lines[i];
            bodyLines[i - 1] = line.Length >= 2 ? line[2..] : line.TrimStart();
        }

        return string.Join(Environment.NewLine, bodyLines).Trim();
    }

    private static JsonArray ValidateServers(JsonArray servers)
    {
        for (var i = 0; i < servers.Count; i++)
        {
            var item = servers[i];
            if (item is JsonValue)
            {
                continue;
            }

            if (item is not JsonObject)
            {
                throw new JsonException(string.Format(Strings.ErrDnsServerInvalid, i + 1));
            }
        }

        return servers.DeepClone() as JsonArray ?? new JsonArray();
    }

    private static JsonObject CreateDefaultDnsObject()
    {
        return new JsonObject
        {
            ["hosts"] = new JsonObject
            {
                ["dns.google"] = "8.8.8.8",
                ["dns.pub"] = "119.29.29.29",
                ["dns.alidns.com"] = "223.5.5.5",
                ["geosite:category-ads-all"] = "127.0.0.1"
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = "https://1.1.1.1/dns-query",
                    ["domains"] = new JsonArray("geosite:geolocation-!cn"),
                    ["expectIPs"] = new JsonArray("geoip:!cn")
                },
                "8.8.8.8",
                new JsonObject
                {
                    ["address"] = "114.114.114.114",
                    ["port"] = 53,
                    ["domains"] = new JsonArray("geosite:cn", "geosite:category-games@cn"),
                    ["expectIPs"] = new JsonArray("geoip:cn"),
                    ["skipFallback"] = true
                },
                new JsonObject
                {
                    ["address"] = "localhost",
                    ["skipFallback"] = true
                }
            },
            ["queryStrategy"] = "UseIPv4"
        };
    }
}

