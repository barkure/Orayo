using System.Globalization;
using System.Text.Json;
using Orayo.Application;
using Orayo.Infrastructure.Storage;
using Orayo.Models;
using Orayo.Services;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task Session_normalizes_invalid_ports_and_presets_and_persists_shared_settings()
    {
        using var directory = new TestDirectory();
        var store = new AppStore(directory.Paths(), new JsonFileStore());
        await store.SaveSettingsAsync(new AppSettings { LocalSocksPort = 0, LocalHttpPort = 70000, DnsJson = "invalid", RoutingRuleJson = "invalid" });
        var session = new AppSession(store);
        await session.LoadAsync();
        session.NormalizeSettings();
        Assert.Equal(10808, session.Settings.LocalSocksPort);
        Assert.Equal(10809, session.Settings.LocalHttpPort);
        using var dns = JsonDocument.Parse(session.Settings.DnsJson!);
        using var routing = JsonDocument.Parse(session.Settings.RoutingRuleJson!);
        Assert.NotEqual(0, dns.RootElement.GetProperty("servers").GetArrayLength());
        Assert.NotEqual(0, routing.RootElement.GetProperty("rules").GetArrayLength());
        session.Settings.Language = "en";
        await session.SaveSettingsAsync();
        Assert.Equal("en", (await store.LoadSettingsAsync()).Language);
    }

    [Theory]
    [InlineData("en", "OK")]
    [InlineData("zh-Hans", "确定")]
    public void Core_resource_assembly_keeps_both_UI_languages(string culture, string expected)
    {
        Assert.Equal(expected, Strings.ResourceManager.GetString("ButtonOK", CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Imported_REALITY_node_generates_matching_proxy_and_local_inbounds()
    {
        var node = NodeLinkParser.Parse("vless://00000000-0000-0000-0000-000000000001@example.com:443?security=reality&type=tcp&sni=example.org&pbk=test-key&sid=abcd&flow=xtls-rprx-vision#Reality")!;
        using var config = JsonDocument.Parse(XrayConfigBuilder.Build(node, new AppSettings()));
        var proxy = config.RootElement.GetProperty("outbounds")[0];
        Assert.Equal("vless", proxy.GetProperty("protocol").GetString());
        Assert.Equal("example.com", proxy.GetProperty("settings").GetProperty("vnext")[0].GetProperty("address").GetString());
        Assert.Equal("test-key", proxy.GetProperty("streamSettings").GetProperty("realitySettings").GetProperty("publicKey").GetString());
        Assert.Equal(2, config.RootElement.GetProperty("inbounds").GetArrayLength());
    }
}
