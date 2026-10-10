using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Orayo.Services;
using Xunit;

namespace Orayo.Core.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The bundled Xray executable and process job objects require Windows.";
    }
}

public sealed class XrayLifecycleTests
{
    [WindowsFact]
    public async Task Start_opens_local_port_and_stop_releases_it_and_ephemeral_config()
    {
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Root, "config.json");
        var service = new XrayService(configPath, deleteConfigOnStop: true);
        var port = ReserveUnusedPort();
        var states = new List<bool>();
        service.RunningChanged += (_, running) => states.Add(running);
        try
        {
            Assert.True(await service.StartAsync(Config(port)), service.LastError);
            Assert.True(service.IsRunning);
            Assert.True(File.Exists(configPath));
            using (var client = new TcpClient())
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));

            await service.StopAsync();
            Assert.False(service.IsRunning);
            Assert.False(File.Exists(configPath));
            Assert.Equal(new[] { true, false }, states);
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            await service.StopAsync(); // Stopping twice remains safe.
        }
        finally
        {
            service.StopForShutdown();
        }
    }

    [WindowsFact]
    public async Task Missing_inbound_fails_without_leaving_process_or_config_and_can_retry()
    {
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Root, "config.json");
        var service = new XrayService(configPath, deleteConfigOnStop: true);
        try
        {
            Assert.False(await service.StartAsync("{\"outbounds\":[{\"protocol\":\"freedom\"}]}"));
            Assert.False(service.IsRunning);
            Assert.NotEmpty(service.LastError);
            Assert.False(File.Exists(configPath));
            Assert.True(await service.StartAsync(Config(ReserveUnusedPort())), service.LastError);
            Assert.Empty(service.LastError);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [WindowsFact]
    public async Task Invalid_config_fails_cleanly_and_next_start_succeeds()
    {
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Root, "config.json");
        var service = new XrayService(configPath, deleteConfigOnStop: true);
        try
        {
            Assert.False(await service.StartAsync("not JSON"));
            Assert.False(service.IsRunning);
            Assert.NotEmpty(service.LastError);
            Assert.False(File.Exists(configPath));
            Assert.True(await service.StartAsync(Config(ReserveUnusedPort())), service.LastError);
        }
        finally
        {
            service.StopForShutdown();
        }
        Assert.False(service.IsRunning);
        Assert.False(File.Exists(configPath));
    }

    [WindowsFact]
    public async Task Restart_releases_previous_port_and_keeps_persistent_config()
    {
        using var directory = new TestDirectory();
        var configPath = Path.Combine(directory.Root, "config.json");
        var service = new XrayService(configPath);
        var firstPort = ReserveUnusedPort();
        try
        {
            Assert.True(await service.StartAsync(Config(firstPort)), service.LastError);
            var secondPort = ReserveUnusedPort();
            Assert.True(await service.StartAsync(Config(secondPort)), service.LastError);
            using var listener = new TcpListener(IPAddress.Loopback, firstPort);
            listener.Start();
            Assert.True(service.IsRunning);
            await service.StopAsync();
            Assert.True(File.Exists(configPath));
        }
        finally
        {
            service.StopForShutdown();
        }
    }

    private static int ReserveUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Config(int port) => JsonSerializer.Serialize(new
    {
        log = new { loglevel = "warning" },
        inbounds = new[] { new { listen = "127.0.0.1", port, protocol = "socks", settings = new { auth = "noauth", udp = false } } },
        outbounds = new[] { new { protocol = "freedom" } }
    });
}
