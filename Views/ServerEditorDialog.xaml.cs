using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.Views;

public sealed partial class ServerEditorDialog : ContentDialog
{
    public ServerEntry Server { get; private set; } = new();

    public ServerEditorDialog()
    {
        InitializeComponent();
        SelectProtocol("ss");
        NetworkComboBox.SelectedItem = "tcp";
        SelectSecurity("none");
        SelectEchForceQuery(string.Empty);
        RefreshFieldVisibility();
    }

    public void ConfigureForCreate(ServerEntry server)
    {
        Title = "手动添加节点";
        PrimaryButtonText = "添加";
        Load(server);
    }

    public void ConfigureForEdit(ServerEntry server)
    {
        Title = "编辑节点";
        PrimaryButtonText = "保存";
        Load(server);
    }

    private void Load(ServerEntry server)
    {
        Server = server.Clone();
        NameTextBox.Text = Server.Name;
        SelectProtocol(Server.Protocol);
        HostTextBox.Text = Server.Host;
        PortNumberBox.Value = Server.Port;
        PasswordTextBox.Password = Server.Password;
        EncryptionTextBox.Text = Server.Encryption;
        UuidTextBox.Text = Server.Uuid;
        AlterIdNumberBox.Value = Server.AlterId;
        NetworkComboBox.SelectedItem = string.IsNullOrWhiteSpace(Server.Network) ? "tcp" : Server.Network;
        PathTextBox.Text = Server.Path;
        WsHostTextBox.Text = Server.WsHost;
        SelectSecurity(Server.Security);
        SniTextBox.Text = Server.Sni;
        FingerprintTextBox.Text = Server.Fingerprint;
        AllowInsecureCheckBox.IsChecked = Server.AllowInsecure;
        EchConfigListTextBox.Text = Server.EchConfigList;
        SelectEchForceQuery(Server.EchForceQuery);
        PublicKeyTextBox.Text = Server.PublicKey;
        ShortIdTextBox.Text = Server.ShortId;
        SpiderXTextBox.Text = Server.SpiderX;
        FlowTextBox.Text = Server.Flow;
        VlessEncryptionTextBox.Text = Server.VlessEncryption;
        FinalmaskTextBox.Text = Server.Finalmask;

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

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text) || string.IsNullOrWhiteSpace(HostTextBox.Text) || PortNumberBox.Value < 1)
        {
            args.Cancel = true;
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
}
