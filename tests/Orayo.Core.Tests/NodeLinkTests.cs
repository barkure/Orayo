using Orayo.Models;
using Orayo.Services;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class NodeLinkTests
{
    [Theory]
    [InlineData("ss")]
    [InlineData("vmess")]
    [InlineData("vless")]
    [InlineData("trojan")]
    [InlineData("hysteria2")]
    public void Share_link_round_trip_preserves_name_and_connection_fields(string protocol)
    {
        var original = new ServerEntry
        {
            Protocol = protocol,
            Name = "香港 / Test #1 & 你好",
            Host = "example.com",
            Port = 443,
            Encryption = "aes-256-gcm",
            Password = "p@ss:word/#?&中文",
            Uuid = "00000000-0000-0000-0000-000000000001",
            Network = "ws",
            Security = "tls",
            Sni = "tls.example.com",
            Fingerprint = "chrome",
            Path = "/proxy?token=a&b=c",
            WsHost = "cdn.example.com",
            AlterId = 0,
            VlessEncryption = "none"
        };

        var link = Assert.IsType<string>(NodeLinkSerializer.ToLink(original));
        var parsed = Assert.IsType<ServerEntry>(NodeLinkParser.Parse(link));
        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Protocol, parsed.Protocol);
        Assert.Equal(original.Host, parsed.Host);
        Assert.Equal(original.Port, parsed.Port);
        if (protocol is "ss" or "trojan" or "hysteria2")
            Assert.Equal(original.Password, parsed.Password);
        if (protocol == "ss")
            Assert.Equal(original.Encryption, parsed.Encryption);
        if (protocol is "vmess" or "vless")
            Assert.Equal(original.Uuid, parsed.Uuid);
        if (protocol is "vmess" or "vless" or "trojan")
        {
            Assert.Equal(original.Network, parsed.Network);
            Assert.Equal(original.Security, parsed.Security);
            Assert.Equal(original.Path, parsed.Path);
            Assert.Equal(original.WsHost, parsed.WsHost);
            Assert.Equal(original.Fingerprint, parsed.Fingerprint);
        }
        if (protocol != "ss")
            Assert.Equal(original.Sni, parsed.Sni);
    }

    [Fact]
    public void Reality_share_preserves_keys_and_flow()
    {
        var original = new ServerEntry
        {
            Protocol = "vless", Host = "example.com", Port = 443, Name = "Reality",
            Uuid = "00000000-0000-0000-0000-000000000001", Security = "reality",
            PublicKey = "test-key", ShortId = "abcd", SpiderX = "/hello?x=1&y=2",
            Sni = "tls.example.com", Fingerprint = "chrome", Flow = "xtls-rprx-vision"
        };
        var parsed = Assert.IsType<ServerEntry>(NodeLinkParser.Parse(NodeLinkSerializer.ToLink(original)!));
        Assert.Equal(original.PublicKey, parsed.PublicKey);
        Assert.Equal(original.ShortId, parsed.ShortId);
        Assert.Equal(original.SpiderX, parsed.SpiderX);
        Assert.Equal(original.Flow, parsed.Flow);
        Assert.Equal(original.Security, parsed.Security);
    }

    [Theory]
    [InlineData("vless")]
    [InlineData("trojan")]
    [InlineData("ss")]
    [InlineData("hysteria2")]
    public void IPv6_share_preserves_address(string protocol)
    {
        var original = new ServerEntry
        {
            Protocol = protocol, Host = "2001:db8::1", Port = 443,
            Uuid = "00000000-0000-0000-0000-000000000001",
            Encryption = "aes-256-gcm", Password = "test-password", Name = "IPv6"
        };
        var parsed = Assert.IsType<ServerEntry>(NodeLinkParser.Parse(NodeLinkSerializer.ToLink(original)!));
        Assert.Equal(original.Host, parsed.Host.Trim('[', ']'));
        Assert.Equal(original.Port, parsed.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a link")]
    [InlineData("vmess://not-base64")]
    [InlineData("ss://invalid")]
    [InlineData("anytls://password@example.com:443")]
    [InlineData("ss://YWVzLTI1Ni1nY206dGVzdA==@example.com:443?plugin=obfs-local")]
    public void Invalid_or_unsupported_links_are_rejected(string link)
    {
        Assert.Null(NodeLinkParser.Parse(link));
    }

    [Theory]
    [InlineData("ss")]
    [InlineData("vmess")]
    [InlineData("vless")]
    [InlineData("trojan")]
    [InlineData("hysteria2")]
    [InlineData("unsupported")]
    public void Entries_without_required_credentials_cannot_be_shared(string protocol)
    {
        Assert.Null(NodeLinkSerializer.ToLink(new ServerEntry { Protocol = protocol, Host = "example.com", Port = 443 }));
    }
}
