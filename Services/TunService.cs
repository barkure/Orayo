using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Orayo.Helpers;

namespace Orayo.Services;

public class TunService
{
    private readonly string _engineDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    private const string DefaultTunInterfaceName = "xray-tun";
    private const string TunIpv4Address = "172.18.0.1";
    private const string TunIpv4Mask = "255.255.255.252";
    private static readonly string[] TunDnsServers = ["1.1.1.1", "8.8.8.8"];

    public sealed class OutboundInterfaceContext
    {
        public required string InterfaceName { get; init; }
        public required string LocalAddress { get; init; }
        public required string GatewayAddress { get; init; }
        public required int InterfaceIndex { get; init; }
    }

    public bool IsWintunAvailable()
    {
        var wintunPath = Path.Combine(_engineDirectory, "wintun.dll");
        return File.Exists(wintunPath);
    }

    public string GetExpectedWintunPath() => Path.Combine(_engineDirectory, "wintun.dll");

    public string? DetectDefaultOutboundInterfaceName()
    {
        return DetectDefaultOutboundContext()?.InterfaceName;
    }

    public OutboundInterfaceContext? DetectDefaultOutboundContext()
    {
        try
        {
            var localAddress = GetDefaultOutboundAddress();
            if (localAddress is null)
            {
                return null;
            }

            var match = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsCandidateOutboundInterface)
                .Select(nic => new
                {
                    Interface = nic,
                    Properties = nic.GetIPProperties()
                })
                .Where(item => item.Properties.UnicastAddresses.Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork
                    && address.Address.Equals(localAddress)))
                .FirstOrDefault();

            if (match is null)
            {
                return null;
            }

            var gateway = match.Properties.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(x) && !IPAddress.None.Equals(x));
            var ipv4 = match.Properties.GetIPv4Properties();
            if (gateway is null || ipv4 is null)
            {
                return null;
            }

            return new OutboundInterfaceContext
            {
                InterfaceName = match.Interface.Name,
                LocalAddress = localAddress.ToString(),
                GatewayAddress = gateway.ToString(),
                InterfaceIndex = ipv4.Index
            };
        }
        catch
        {
            return null;
        }
    }

    public bool IsTunInterfaceActive()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(nic =>
                    string.Equals(nic.Name, DefaultTunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    && nic.OperationalStatus == OperationalStatus.Up);
        }
        catch
        {
            return false;
        }
    }

    public bool HasExpectedTunAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => string.Equals(nic.Name, DefaultTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork
                    && string.Equals(address.Address.ToString(), TunIpv4Address, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public bool ConfigureTunInterface(string? serverAddress)
    {
        var outbound = DetectDefaultOutboundContext();
        if (outbound is null)
        {
            return false;
        }

        var tunIndex = WaitForTunInterfaceIndex();
        if (tunIndex <= 0)
        {
            return false;
        }

        if (TryResolveIPv4Address(serverAddress, out var serverIPv4))
        {
            _ = serverIPv4;
        }

        var serverRouteScript = TryResolveIPv4Address(serverAddress, out serverIPv4)
            ? $"New-NetRoute -DestinationPrefix '{serverIPv4}/32' -InterfaceIndex {outbound.InterfaceIndex} -NextHop '{outbound.GatewayAddress}' -RouteMetric 3 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null{Environment.NewLine}"
            : string.Empty;
        var dnsRouteScript = string.Join(Environment.NewLine, TunDnsServers.Select(dns =>
            $"New-NetRoute -DestinationPrefix '{dns}/32' -InterfaceIndex {outbound.InterfaceIndex} -NextHop '{outbound.GatewayAddress}' -RouteMetric 3 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Out-Null"));

        var script = $@"
$ErrorActionPreference = 'Stop'
$ifIndex = {tunIndex}
Get-NetIPAddress -InterfaceIndex $ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
Get-NetRoute -InterfaceIndex $ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceIndex $ifIndex -IPAddress '{TunIpv4Address}' -PrefixLength 30 -Type Unicast -AddressFamily IPv4 -ErrorAction Stop | Out-Null
Set-DnsClientServerAddress -InterfaceIndex $ifIndex -ServerAddresses @('{TunDnsServers[0]}','{TunDnsServers[1]}') -ErrorAction Stop
{serverRouteScript}{dnsRouteScript}
New-NetRoute -DestinationPrefix '0.0.0.0/1' -InterfaceIndex $ifIndex -NextHop '{TunIpv4Address}' -RouteMetric 6 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
New-NetRoute -DestinationPrefix '128.0.0.0/1' -InterfaceIndex $ifIndex -NextHop '{TunIpv4Address}' -RouteMetric 6 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
";

        return RunElevatedPowerShell(script);
    }

    public void CleanupTunRoutes(string? serverAddress)
    {
        try
        {
            string[] legacyDnsServers = ["223.5.5.5", "119.29.29.29", .. TunDnsServers];

            var batch = new List<string>
            {
                $"netsh interface ipv4 delete route 0.0.0.0/0 \"{DefaultTunInterfaceName}\" store=active",
                $"netsh interface ipv4 delete route 0.0.0.0/1 \"{DefaultTunInterfaceName}\" store=active",
                $"netsh interface ipv4 delete route 128.0.0.0/1 \"{DefaultTunInterfaceName}\" store=active",
                "route delete 0.0.0.0 mask 128.0.0.0",
                "route delete 128.0.0.0 mask 128.0.0.0",
            };

            if (TryParseSafeIPv4Address(serverAddress, out var serverIPv4))
            {
                batch.Add($"netsh interface ipv4 delete route {serverIPv4}/32 \"{DefaultTunInterfaceName}\" store=active");
                batch.Add($"route delete {serverIPv4} mask 255.255.255.255");
            }

            foreach (var dns in legacyDnsServers)
            {
                batch.Add($"route delete {dns} mask 255.255.255.255");
            }

            RunElevatedBatch(batch);
        }
        catch
        {
        }
    }

    private int GetTunInterfaceIndex()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => string.Equals(nic.Name, DefaultTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                .Select(nic => nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1)
                .FirstOrDefault(index => index > 0);
        }
        catch
        {
            return -1;
        }
    }

    private int WaitForTunInterfaceIndex()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var index = GetTunInterfaceIndex();
            if (index > 0)
            {
                return index;
            }

            Thread.Sleep(100);
        }

        return -1;
    }

    private static bool TryResolveIPv4Address(string? value, out string address)
    {
        if (TryParseSafeIPv4Address(value, out address))
        {
            return true;
        }

        address = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            address = Dns.GetHostAddresses(value)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?
                .ToString()
                ?? string.Empty;
            return !string.IsNullOrWhiteSpace(address);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseSafeIPv4Address(string? value, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed.ToString();
        return true;
    }

    private static IPAddress? GetDefaultOutboundAddress()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect("8.8.8.8", 53);
        return (socket.LocalEndPoint as IPEndPoint)?.Address;
    }

    private bool IsCandidateOutboundInterface(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        var name = nic.Name ?? string.Empty;
        var description = nic.Description ?? string.Empty;
        var combined = $"{name} {description}";

        return !ContainsAny(combined,
            DefaultTunInterfaceName,
            "wintun",
            "xray",
            "loopback",
            "pseudo-interface",
            "virtualbox",
            "vmware",
            "hyper-v virtual",
            "vethernet");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RunElevatedBatch(IReadOnlyList<string> commandLines)
    {
        if (commandLines.Count == 0)
        {
            return true;
        }

        var combined = string.Join(" & ", commandLines);
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var isAdmin = AdminHelper.IsAdministrator();

        var psi = new ProcessStartInfo
        {
            FileName = cmdPath,
            Arguments = "/c " + combined,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (isAdmin)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        else
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunElevatedPowerShell(string script)
    {
        var isAdmin = AdminHelper.IsAdministrator();
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (isAdmin)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        else
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}

