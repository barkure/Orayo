using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Orayo.Helpers;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.Views;

public sealed partial class ServerEditorWindow : Window
{
    private const int GWL_HWNDPARENT = -8;
    private const int DefaultWidth = 760;
    private const int DefaultHeight = 860;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly TaskCompletionSource<ServerEntry?> _completion = new();
    private bool _completed;

    public ServerEntry Server { get; private set; }

    public ServerEditorWindow(Window owner, ServerEntry server, string title = "手动添加节点", string saveButtonText = "添加")
    {
        _owner = owner;
        Server = server.Clone();
        InitializeComponent();

        AppWindow.Title = title;
        SaveButton.Content = saveButtonText;
        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = true;
        SetWindowOwner(owner);
        AppWindow.SetPresenter(presenter);
        WindowMinSizeHelper.Apply(this, DefaultWidth, DefaultHeight);

        Closed += OnClosed;
        Load(Server);
    }

    public Task<ServerEntry?> ShowModalAsync()
    {
        AppWindow.Show();
        Activate();
        return _completion.Task;
    }

    private void Load(ServerEntry server)
    {
        NameTextBox.Text = server.Name;
        SelectProtocol(server.Protocol);
        HostTextBox.Text = server.Host;
        PortNumberBox.Value = server.Port;
        PasswordTextBox.Password = server.Password;
        EncryptionTextBox.Text = server.Encryption;
        UuidTextBox.Text = server.Uuid;
        AlterIdNumberBox.Value = server.AlterId;
        NetworkComboBox.SelectedItem = string.IsNullOrWhiteSpace(server.Network) ? "tcp" : server.Network;
        PathTextBox.Text = server.Path;
        WsHostTextBox.Text = server.WsHost;
        SelectSecurity(server.Security);
        SniTextBox.Text = server.Sni;
        FingerprintTextBox.Text = server.Fingerprint;
        AllowInsecureCheckBox.IsChecked = server.AllowInsecure;
        EchConfigListTextBox.Text = server.EchConfigList;
        SelectEchForceQuery(server.EchForceQuery);
        PublicKeyTextBox.Text = server.PublicKey;
        ShortIdTextBox.Text = server.ShortId;
        SpiderXTextBox.Text = server.SpiderX;
        FlowTextBox.Text = server.Flow;
        VlessEncryptionTextBox.Text = server.VlessEncryption;
        FinalmaskTextBox.Text = server.Finalmask;

        if (NetworkComboBox.SelectedItem is null)
        {
            NetworkComboBox.SelectedItem = "tcp";
        }

        NormalizeSecuritySelection();
        RefreshFieldVisibility();
    }

    private void ProtocolComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NormalizeSecuritySelection();
        RefreshFieldVisibility();
    }

    private void NetworkComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFieldVisibility();

    private void SecurityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NormalizeSecuritySelection();
        RefreshFieldVisibility();
    }

    private void RefreshFieldVisibility()
    {
        var protocol = GetSelectedProtocol();
        var network = NetworkComboBox.SelectedItem as string ?? "tcp";
        var security = GetSelectedSecurity();

        var isSs = protocol == "ss";
        var isVmess = protocol == "vmess";
        var isVless = protocol == "vless";
        var isHysteria2 = protocol == "hysteria2";
        var isTrojan = protocol == "trojan";
        var hasTransport = isVmess || isVless || isTrojan;
        var hasSecuritySelector = isVmess || isVless;
        var hasWs = hasTransport && network == "ws";
        var hasXhttp = hasTransport && network == "xhttp";
        var hasGrpc = hasTransport && network == "grpc";
        var hasTls = (hasSecuritySelector && security == "tls") || isTrojan || isHysteria2;
        var hasReality = isVless && security == "reality";
        var hasEch = isVless && security == "tls";

        SecurityNoneItem.Visibility = isTrojan ? Visibility.Collapsed : Visibility.Visible;
        SecurityTlsItem.Visibility = Visibility.Visible;
        SecurityRealityItem.Visibility = isVless ? Visibility.Visible : Visibility.Collapsed;

        ProtocolSettingsTitle.Visibility = Visibility.Visible;
        TransportSecurityTitle.Visibility = hasSecuritySelector || hasTls || hasReality ? Visibility.Visible : Visibility.Collapsed;
        NetworkComboBox.Visibility = hasTransport ? Visibility.Visible : Visibility.Collapsed;
        SecurityComboBox.Visibility = hasSecuritySelector ? Visibility.Visible : Visibility.Collapsed;
        PasswordTextBox.Visibility = isSs || isTrojan || isHysteria2 ? Visibility.Visible : Visibility.Collapsed;
        EncryptionTextBox.Visibility = isSs ? Visibility.Visible : Visibility.Collapsed;
        UuidTextBox.Visibility = isVmess || isVless ? Visibility.Visible : Visibility.Collapsed;
        AlterIdNumberBox.Visibility = isVmess ? Visibility.Visible : Visibility.Collapsed;
        PathTextBox.Visibility = hasWs || hasXhttp || hasGrpc ? Visibility.Visible : Visibility.Collapsed;
        WsHostTextBox.Visibility = hasWs || hasXhttp ? Visibility.Visible : Visibility.Collapsed;
        SniTextBox.Visibility = hasTls || hasReality ? Visibility.Visible : Visibility.Collapsed;
        FingerprintTextBox.Visibility = (hasTls && !isHysteria2) || hasReality ? Visibility.Visible : Visibility.Collapsed;
        AllowInsecureCheckBox.Visibility = hasTls || hasReality ? Visibility.Visible : Visibility.Collapsed;
        EchConfigListTextBox.Visibility = hasEch ? Visibility.Visible : Visibility.Collapsed;
        EchForceQueryComboBox.Visibility = hasEch ? Visibility.Visible : Visibility.Collapsed;
        PublicKeyTextBox.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
        ShortIdTextBox.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
        SpiderXTextBox.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
        FlowTextBox.Visibility = isVless ? Visibility.Visible : Visibility.Collapsed;
        VlessEncryptionTextBox.Visibility = isVless ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) || string.IsNullOrWhiteSpace(HostTextBox.Text) || PortNumberBox.Value < 1)
        {
            return;
        }

        Server.Name = NameTextBox.Text.Trim();
        Server.Protocol = GetSelectedProtocol();
        Server.Host = HostTextBox.Text.Trim();
        Server.Port = (int)PortNumberBox.Value;
        Server.Password = PasswordTextBox.Password.Trim();
        Server.Encryption = EncryptionTextBox.Text.Trim();
        Server.Uuid = UuidTextBox.Text.Trim();
        Server.AlterId = (int)AlterIdNumberBox.Value;
        Server.Network = NormalizeNetworkForProtocol(Server.Protocol, NetworkComboBox.SelectedItem as string ?? "tcp");
        Server.Path = PathTextBox.Text.Trim();
        Server.WsHost = WsHostTextBox.Text.Trim();
        Server.Security = NormalizeSecurityForProtocol(Server.Protocol, GetSelectedSecurity());
        Server.Sni = SniTextBox.Text.Trim();
        Server.Fingerprint = FingerprintTextBox.Text.Trim();
        Server.AllowInsecure = AllowInsecureCheckBox.IsChecked == true;
        Server.EchConfigList = EchConfigListTextBox.Text.Trim();
        Server.EchForceQuery = GetSelectedEchForceQuery();
        Server.PublicKey = PublicKeyTextBox.Text.Trim();
        Server.ShortId = ShortIdTextBox.Text.Trim();
        Server.SpiderX = SpiderXTextBox.Text.Trim();
        Server.Flow = FlowTextBox.Text.Trim();
        Server.VlessEncryption = VlessEncryptionTextBox.Text.Trim();
        Server.Finalmask = FinalmaskTextBox.Text.Trim();

        Complete(Server);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(null);
        Close();
    }

    private string GetSelectedProtocol() => (ProtocolComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ss";
    private string GetSelectedSecurity() => (SecurityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
    private string GetSelectedEchForceQuery() => EchSettings.NormalizeForceQuery((EchForceQueryComboBox.SelectedItem as ComboBoxItem)?.Tag as string);

    private void NormalizeSecuritySelection()
    {
        var normalized = NormalizeSecurityForProtocol(GetSelectedProtocol(), GetSelectedSecurity());
        SelectSecurity(normalized);
    }

    private static string NormalizeNetworkForProtocol(string protocol, string network)
    {
        protocol = protocol.Trim().ToLowerInvariant();
        network = string.IsNullOrWhiteSpace(network) ? "tcp" : network.Trim().ToLowerInvariant();
        return protocol is "ss" or "hysteria2" ? "tcp" : network;
    }

    private static string NormalizeSecurityForProtocol(string protocol, string security)
    {
        protocol = protocol.Trim().ToLowerInvariant();
        security = string.IsNullOrWhiteSpace(security) ? "none" : security.Trim().ToLowerInvariant();
        return protocol switch
        {
            "ss" => "none",
            "hysteria2" => "tls",
            "trojan" => "tls",
            "vmess" => security == "tls" ? "tls" : "none",
            "vless" => security is "tls" or "reality" ? security : "none",
            _ => "none"
        };
    }

    private void SelectProtocol(string protocol)
    {
        foreach (var item in ProtocolComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag as string, protocol, StringComparison.OrdinalIgnoreCase))
            {
                ProtocolComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        ProtocolComboBox.SelectedIndex = 0;
    }

    private void SelectSecurity(string security)
    {
        foreach (var item in SecurityComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag as string, security, StringComparison.OrdinalIgnoreCase))
            {
                SecurityComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        SecurityComboBox.SelectedItem = SecurityNoneItem;
    }

    private void SelectEchForceQuery(string value)
    {
        var normalized = EchSettings.NormalizeForceQuery(value);
        foreach (var item in EchForceQueryComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem && string.Equals(comboBoxItem.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                EchForceQueryComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        EchForceQueryComboBox.SelectedIndex = 0;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Complete(null);
        _owner.Activate();
    }

    private void Complete(ServerEntry? server)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completion.TrySetResult(server);
    }

    private void SetWindowOwner(Window owner)
    {
        var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        var ownedHwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr(ownedHwnd, GWL_HWNDPARENT, ownerHwnd);
        }
        else
        {
            SetWindowLong(ownedHwnd, GWL_HWNDPARENT, ownerHwnd);
        }
    }
}
