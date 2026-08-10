using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orayo.Models;

namespace Orayo.Services;

/// <summary>
/// Subscription pipeline. Mirrors V2rayN's subscription model with a cleaner shape:
///   fetch(url)  -> raw payload          (kept as the single source of truth)
///   parse(payload) -> node list         (pure function, idempotent, never throws)
/// The caller applies the whole-group-replace semantics (V2rayN style).
/// </summary>
public static class SubscriptionService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Downloads the subscription payload. Tries the local HTTP proxy first when one is
    /// available (matching V2rayN's proxy-first behavior), then falls back to direct.
    /// </summary>
    public static async Task<(string? Content, string? Error)> FetchAsync(string url, string? userAgent, int? httpProxyPort)
    {
        var attempts = httpProxyPort is > 0
            ? new[] { true, false }
            : new[] { false };
        var errors = new List<string>();

        foreach (var useProxy in attempts)
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseProxy = useProxy,
                    Proxy = useProxy ? new WebProxy($"http://127.0.0.1:{httpProxyPort}") : null
                };
                using var client = new HttpClient(handler) { Timeout = RequestTimeout };
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);
                }
                else
                {
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd("Orayo/1.0");
                }

                using var cts = new CancellationTokenSource(RequestTimeout);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"HTTP {(int)response.StatusCode}");
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                return (content, null);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        return (null, string.Join("; ", errors));
    }

    /// <summary>
    /// Pure function: payload -> node list. Base64-wrapped payloads are decoded first;
    /// plain-text link lists pass through. Duplicate nodes are dropped. Never throws.
    /// </summary>
    public static List<ServerEntry> ParseContent(string content)
    {
        var raw = content ?? string.Empty;
        var decoded = TryDecodePayload(raw);
        if (decoded is not null)
        {
            raw = decoded;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<ServerEntry>();
        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var server = NodeLinkParser.Parse(line.Trim());
            if (server is null)
            {
                continue;
            }

            var key = $"{server.Protocol}|{server.Host}|{server.Port}|{server.Name}";
            if (!seen.Add(key))
            {
                continue;
            }

            nodes.Add(server);
        }

        return nodes;
    }

    /// <summary>
    /// Returns the decoded payload when the raw text is a Base64 blob that actually
    /// contains share links, otherwise null (the raw text is already plain).
    /// </summary>
    private static string? TryDecodePayload(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var decoded = TryBase64Decode(trimmed);
        if (decoded is null)
        {
            return null;
        }

        return decoded.Contains("://", StringComparison.Ordinal) ? decoded : null;
    }

    private static string? TryBase64Decode(string input)
    {
        try
        {
            input = input.Replace('-', '+').Replace('_', '/');
            var pad = input.Length % 4;
            if (pad == 2)
            {
                input += "==";
            }
            else if (pad == 3)
            {
                input += "=";
            }

            var bytes = Convert.FromBase64String(input);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}