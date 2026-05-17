using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Serialization;

namespace Orayo.Models;

public class ServerEntry : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port;
    private string _protocol = "ss";
    private string _encryption = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _uuid = string.Empty;
    private string _network = "tcp";
    private string _path = string.Empty;
    private string _wsHost = string.Empty;
    private int _alterId;
    private string _security = "none";
    private string _sni = string.Empty;
    private string _fingerprint = string.Empty;
    private bool _allowInsecure;
    private string _echConfigList = string.Empty;
    private string _echForceQuery = string.Empty;
    private string _publicKey = string.Empty;
    private string _shortId = string.Empty;
    private string _spiderX = string.Empty;
    private string _flow = string.Empty;
    private string _vlessEncryption = string.Empty;
    private string _finalmask = string.Empty;
    private bool _isActive;
    private string _latencyBadgeText = string.Empty;
    private Visibility _latencyBadgeVisibility = Visibility.Collapsed;
    private Brush _latencyBadgeBackground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0, G = 130, B = 53 });
    private Brush _latencyBadgeForeground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 });

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }

    public string Protocol
    {
        get => _protocol;
        set
        {
            if (SetProperty(ref _protocol, value))
            {
                OnPropertyChanged(nameof(DisplayProtocol));
            }
        }
    }

    public string Encryption { get => _encryption; set => SetProperty(ref _encryption, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string Uuid { get => _uuid; set => SetProperty(ref _uuid, value); }
    public string Network { get => _network; set => SetProperty(ref _network, value); }
    public string Path { get => _path; set => SetProperty(ref _path, value); }
    public string WsHost { get => _wsHost; set => SetProperty(ref _wsHost, value); }
    public int AlterId { get => _alterId; set => SetProperty(ref _alterId, value); }
    public string Security { get => _security; set => SetProperty(ref _security, value); }
    public string Sni { get => _sni; set => SetProperty(ref _sni, value); }
    public string Fingerprint { get => _fingerprint; set => SetProperty(ref _fingerprint, value); }
    public bool AllowInsecure { get => _allowInsecure; set => SetProperty(ref _allowInsecure, value); }
    public string EchConfigList { get => _echConfigList; set => SetProperty(ref _echConfigList, value); }
    public string EchForceQuery { get => _echForceQuery; set => SetProperty(ref _echForceQuery, value); }
    public string PublicKey { get => _publicKey; set => SetProperty(ref _publicKey, value); }
    public string ShortId { get => _shortId; set => SetProperty(ref _shortId, value); }
    public string SpiderX { get => _spiderX; set => SetProperty(ref _spiderX, value); }
    public string Flow { get => _flow; set => SetProperty(ref _flow, value); }
    public string VlessEncryption { get => _vlessEncryption; set => SetProperty(ref _vlessEncryption, value); }
    public string Finalmask { get => _finalmask; set => SetProperty(ref _finalmask, value); }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(ActiveVisibility));
            }
        }
    }

    [JsonIgnore]
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public string LatencyBadgeText
    {
        get => _latencyBadgeText;
        set => SetProperty(ref _latencyBadgeText, value);
    }

    [JsonIgnore]
    public Visibility LatencyBadgeVisibility
    {
        get => _latencyBadgeVisibility;
        set => SetProperty(ref _latencyBadgeVisibility, value);
    }

    [JsonIgnore]
    public Brush LatencyBadgeBackground
    {
        get => _latencyBadgeBackground;
        set => SetProperty(ref _latencyBadgeBackground, value);
    }

    [JsonIgnore]
    public Brush LatencyBadgeForeground
    {
        get => _latencyBadgeForeground;
        set => SetProperty(ref _latencyBadgeForeground, value);
    }

    public string DisplayProtocol => Protocol.ToLowerInvariant() switch
    {
        "ss" => "Shadowsocks",
        "vmess" => "VMess",
        "vless" => "VLESS",
        "hysteria2" => "Hysteria 2",
        "trojan" => "Trojan",
        _ => Protocol
    };

    public ServerEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Host = Host,
        Port = Port,
        Protocol = Protocol,
        Encryption = Encryption,
        Username = Username,
        Password = Password,
        Uuid = Uuid,
        Network = Network,
        Path = Path,
        WsHost = WsHost,
        AlterId = AlterId,
        Security = Security,
        Sni = Sni,
        Fingerprint = Fingerprint,
        AllowInsecure = AllowInsecure,
        EchConfigList = EchConfigList,
        EchForceQuery = EchForceQuery,
        PublicKey = PublicKey,
        ShortId = ShortId,
        SpiderX = SpiderX,
        Flow = Flow,
        VlessEncryption = VlessEncryption,
        Finalmask = Finalmask,
        IsActive = IsActive,
    };

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

