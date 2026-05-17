using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Orayo.Helpers;

namespace Orayo.Services;

public class TunService
{
    private readonly string _engineDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    private const string DefaultTunInterfaceName = "xray-tun";

    public bool IsWintunAvailable()
    {
        var wintunPath = Path.Combine(_engineDirectory, "wintun.dll");
        return File.Exists(wintunPath);
    }

    public string GetExpectedWintunPath() => Path.Combine(_engineDirectory, "wintun.dll");

    public string? DetectDefaultOutboundInterfaceName()
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
                .Select(item => item.Interface)
                .FirstOrDefault();

            return match?.Name;
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

    public void CleanupTunRoutes(string? serverAddress)
    {
        try
        {
            string[] legacyDnsServers = ["223.5.5.5", "119.29.29.29"];

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
}

