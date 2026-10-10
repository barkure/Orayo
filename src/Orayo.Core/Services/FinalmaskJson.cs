using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Orayo.Services;

internal static class FinalmaskJson
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string NormalizeForStorage(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var node = Parse(value);
        return node?.ToJsonString(CompactJson) ?? value;
    }

    public static string NormalizeForShare(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var node = Parse(value);
        return node?.ToJsonString(CompactJson) ?? value;
    }

    public static JsonNode? Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

