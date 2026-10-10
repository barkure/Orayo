using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.ViewModels;

/// <summary>Presentation state stays out of serialized node configuration.</summary>
public sealed class ServerItemViewModel : INotifyPropertyChanged
{
    public ServerItemViewModel(ServerEntry server) => Server = server;

    public ServerEntry Server { get; }
    public string Id => Server.Id;
    public string Name => Server.Name;
    public string Host => Server.Host;
    public int Port => Server.Port;
    public string DisplayProtocol => Server.Protocol.ToLowerInvariant() switch
    {
        "ss" => "Shadowsocks",
        "vmess" => "VMess",
        "vless" => "VLESS",
        "hysteria2" => "Hysteria 2",
        "trojan" => "Trojan",
        _ => Server.Protocol
    };

    private bool _isActive;
    private string _latencyBadgeText = string.Empty;
    private Visibility _latencyBadgeVisibility = Visibility.Collapsed;
    private Brush _latencyBadgeBackground = LatencyDeepGreenBrush;
    private Brush _latencyBadgeForeground = LatencyWhiteForegroundBrush;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveVisibility)));
        }
    }

    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string LatencyBadgeText { get => _latencyBadgeText; private set => SetProperty(ref _latencyBadgeText, value); }
    public Visibility LatencyBadgeVisibility { get => _latencyBadgeVisibility; private set => SetProperty(ref _latencyBadgeVisibility, value); }
    public Brush LatencyBadgeBackground { get => _latencyBadgeBackground; private set => SetProperty(ref _latencyBadgeBackground, value); }
    public Brush LatencyBadgeForeground { get => _latencyBadgeForeground; private set => SetProperty(ref _latencyBadgeForeground, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly Brush LatencyDeepGreenBrush = CreateBrush(0x00, 0x82, 0x35);
    private static readonly Brush LatencyLightGreenBrush = CreateBrush(0x7c, 0xcf, 0x00);
    private static readonly Brush LatencyYellowBrush = CreateBrush(0xfd, 0xc7, 0x00);
    private static readonly Brush LatencyOrangeBrush = CreateBrush(0xff, 0x69, 0x00);
    private static readonly Brush LatencyRedBrush = CreateBrush(0xd9, 0x2d, 0x20);
    private static readonly Brush LatencyWhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff);
    private static readonly Brush LatencyDarkForegroundBrush = CreateBrush(0x1f, 0x29, 0x37);

    public void ApplyLatencyResult(LatencyProbeResult result)
    {
        if (result.TimedOut || result.Milliseconds is null)
        {
            LatencyBadgeText = Strings.LatencyTimeout;
            LatencyBadgeBackground = LatencyRedBrush;
            LatencyBadgeForeground = LatencyWhiteForegroundBrush;
            LatencyBadgeVisibility = Visibility.Visible;
            return;
        }

        var milliseconds = Math.Max(0, result.Milliseconds.Value);
        LatencyBadgeText = $"{milliseconds}ms";
        LatencyBadgeVisibility = Visibility.Visible;

        if (milliseconds <= 50)
        {
            LatencyBadgeBackground = LatencyDeepGreenBrush;
            LatencyBadgeForeground = LatencyWhiteForegroundBrush;
            return;
        }

        if (milliseconds <= 150)
        {
            LatencyBadgeBackground = LatencyLightGreenBrush;
            LatencyBadgeForeground = LatencyDarkForegroundBrush;
            return;
        }

        if (milliseconds <= 250)
        {
            LatencyBadgeBackground = LatencyYellowBrush;
            LatencyBadgeForeground = LatencyDarkForegroundBrush;
            return;
        }

        LatencyBadgeBackground = LatencyOrangeBrush;
        LatencyBadgeForeground = LatencyWhiteForegroundBrush;
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(new Windows.UI.Color { A = 255, R = r, G = g, B = b });
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
