using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Orayo.Models;

namespace Orayo.Services;

public static class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string Build(ServerEntry server, AppSettings settings)
    {
        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = "warning"
            },
            ["dns"] = BuildDns(settings),
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = BuildOutbounds(server),
            ["routing"] = BuildRouting(settings),
        };

        return config.ToJsonString(JsonOptions);
    }

    private static JsonObject BuildDns(AppSettings settings)
    {
        return DnsPresetService.EnsureDnsObject(settings.DnsJson);
    }

    private static JsonArray BuildInbounds(AppSettings settings)
    {
        var list = new JsonArray();
        if (settings.IsTunMode)
        {
            list.Add(BuildTunInbound());
        }

        if (settings.LocalSocksPort == settings.LocalHttpPort)
        {
            list.Add(BuildMixedInbound(settings.LocalSocksPort));
        }
        else
        {
            list.Add(BuildSocksInbound(settings.LocalSocksPort));
            list.Add(BuildHttpInbound(settings.LocalHttpPort));
        }

        return list;
    }

    private static JsonObject BuildTunInbound()
    {
        return new JsonObject
        {
            ["tag"] = "tun-in",
            ["protocol"] = "tun",
            ["settings"] = new JsonObject
            {
                ["name"] = "xray-tun",
                ["mtu"] = 1500,
                ["gateway"] = new JsonArray("172.18.0.1/30"),
                ["dns"] = new JsonArray("1.1.1.1", "8.8.8.8"),
                ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0"),
                ["autoOutboundsInterface"] = "auto"
            },
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic")
            }
        };
    }

    private static JsonObject BuildMixedInbound(int port)
    {
        return new JsonObject
        {
            ["tag"] = "mixed-in",
            ["protocol"] = "mixed",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["settings"] = new JsonObject
            {
                ["auth"] = "noauth",
                ["udp"] = true
            },
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic")
            }
        };
    }

    private static JsonObject BuildSocksInbound(int port)
    {
        return new JsonObject
        {
            ["tag"] = "socks-in",
            ["protocol"] = "socks",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["settings"] = new JsonObject
            {
                ["auth"] = "noauth",
                ["udp"] = true
            },
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic")
            }
        };
    }

    private static JsonObject BuildHttpInbound(int port)
    {
        return new JsonObject
        {
            ["tag"] = "http-in",
            ["protocol"] = "http",
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["settings"] = new JsonObject(),
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic")
            }
        };
    }

    private static JsonArray BuildOutbounds(ServerEntry server)
    {
        return new JsonArray
        {
            BuildProxyOutbound(server),
            new JsonObject
            {
                ["tag"] = "direct",
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject()
            },
            new JsonObject
            {
                ["tag"] = "block",
                ["protocol"] = "blackhole",
                ["settings"] = new JsonObject()
            }
        };
    }

    private static JsonObject BuildRouting(AppSettings settings)
    {
        if (string.Equals(settings.RoutingMode, "global", StringComparison.OrdinalIgnoreCase))
        {
            var rules = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "field",
                    ["outboundTag"] = "proxy",
                    ["network"] = "tcp,udp"
                }
            };

            return new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            };
        }

        var routing = RouteRulePresetService.EnsureRoutingObject(settings.RoutingRuleJson);
        return routing;
    }

    private static JsonObject BuildProxyOutbound(ServerEntry server)
    {
        return server.Protocol.ToLowerInvariant() switch
        {
            "vmess" => BuildVmessOutbound(server),
            "vless" => BuildVlessOutbound(server),
            "hysteria2" => BuildHysteria2Outbound(server),
            "trojan" => BuildTrojanOutbound(server),
            _ => BuildSsOutbound(server)
        };
    }

    private static JsonObject BuildSsOutbound(ServerEntry server)
    {
        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "shadowsocks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = server.Host,
                        ["port"] = server.Port,
                        ["method"] = server.Encryption,
                        ["password"] = server.Password
                    }
                }
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "tcp"
            }
        };

        ApplyFinalmask((JsonObject)outbound["streamSettings"]!, server);
        return outbound;
    }

    private static JsonObject BuildVmessOutbound(ServerEntry server)
    {
        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vmess",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = server.Host,
                        ["port"] = server.Port,
                        ["users"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = server.Uuid,
                                ["alterId"] = server.AlterId,
                                ["security"] = "auto"
                            }
                        }
                    }
                }
            },
            ["streamSettings"] = BuildStreamSettings(server)
        };
    }

    private static JsonObject BuildVlessOutbound(ServerEntry server)
    {
        var user = new JsonObject
        {
            ["id"] = server.Uuid,
            ["encryption"] = string.IsNullOrWhiteSpace(server.VlessEncryption) ? "none" : server.VlessEncryption
        };

        if (!string.IsNullOrWhiteSpace(server.Flow))
        {
            user["flow"] = server.Flow;
        }

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = server.Host,
                        ["port"] = server.Port,
                        ["users"] = new JsonArray { user }
                    }
                }
            },
            ["streamSettings"] = BuildStreamSettings(server)
        };
    }

    private static JsonObject BuildHysteria2Outbound(ServerEntry server)
    {
        var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni;
        var streamSettings = new JsonObject
        {
            ["network"] = "hysteria",
            ["security"] = "tls",
            ["tlsSettings"] = new JsonObject
            {
                ["serverName"] = sni,
                ["allowInsecure"] = server.AllowInsecure
            },
            ["hysteriaSettings"] = new JsonObject
            {
                ["version"] = 2,
                ["auth"] = server.Password
            }
        };
        ApplyFinalmask(streamSettings, server);

        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "hysteria",
            ["settings"] = new JsonObject
            {
                ["version"] = 2,
                ["address"] = server.Host,
                ["port"] = server.Port
            },
            ["streamSettings"] = streamSettings
        };
    }

    private static JsonObject BuildTrojanOutbound(ServerEntry server)
    {
        return new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "trojan",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = server.Host,
                        ["port"] = server.Port,
                        ["password"] = server.Password
                    }
                }
            },
            ["streamSettings"] = BuildStreamSettings(server)
        };
    }

    private static JsonObject BuildStreamSettings(ServerEntry server)
    {
        var network = string.IsNullOrWhiteSpace(server.Network) ? "tcp" : server.Network.ToLowerInvariant();
        var security = string.IsNullOrWhiteSpace(server.Security) ? "none" : server.Security.ToLowerInvariant();

        var stream = new JsonObject
        {
            ["network"] = network,
            ["security"] = security
        };

        if (security == "tls")
        {
            var tlsSettings = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint,
                ["allowInsecure"] = server.AllowInsecure
            };

            if (!string.IsNullOrWhiteSpace(server.EchConfigList))
            {
                tlsSettings["echConfigList"] = server.EchConfigList;
                var echForceQuery = EchSettings.NormalizeForceQuery(server.EchForceQuery);
                if (!string.IsNullOrWhiteSpace(echForceQuery))
                {
                    tlsSettings["echForceQuery"] = echForceQuery;
                }
            }

            stream["tlsSettings"] = tlsSettings;
        }
        else if (security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrWhiteSpace(server.Sni) ? server.Host : server.Sni,
                ["fingerprint"] = string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint,
                ["publicKey"] = server.PublicKey,
                ["shortId"] = server.ShortId,
                ["spiderX"] = string.IsNullOrWhiteSpace(server.SpiderX) ? "/" : server.SpiderX
            };
        }

        if (network == "ws")
        {
            var headers = new JsonObject();
            if (!string.IsNullOrWhiteSpace(server.WsHost))
            {
                headers["Host"] = server.WsHost;
            }

            stream["wsSettings"] = new JsonObject
            {
                ["path"] = server.Path,
                ["headers"] = headers
            };
        }
        else if (network == "grpc")
        {
            stream["grpcSettings"] = new JsonObject
            {
                ["serviceName"] = server.Path
            };
        }
        else if (network == "xhttp")
        {
            var xhttpSettings = new JsonObject
            {
                ["path"] = server.Path
            };

            if (!string.IsNullOrWhiteSpace(server.WsHost))
            {
                xhttpSettings["host"] = server.WsHost;
            }

            stream["xhttpSettings"] = xhttpSettings;
        }

        ApplyFinalmask(stream, server);
        return stream;
    }

    private static void ApplyFinalmask(JsonObject streamSettings, ServerEntry server)
    {
        var finalmask = FinalmaskJson.Parse(server.Finalmask);
        if (finalmask is JsonObject)
        {
            streamSettings["finalmask"] = finalmask;
        }
    }
}




