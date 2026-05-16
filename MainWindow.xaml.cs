using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orayo.Helpers;
using Orayo.Models;
using Orayo.Services;
using Orayo.Views;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Orayo;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppStore _store = new();
    private readonly XrayService _xray = new();
    private readonly TunService _tunService = new();
    private AppSettings _settings = new();
    private ServerEntry? _selectedServer;
    private ServerEntry? _activeServer;
    private bool _isRunning;
    private bool _isInitializing;
    private bool _isTunMode;
    private bool _isSystemProxyEnabled = true;
    private bool _isTunInternalUpdate;
    private bool _isApplyingSelection;
    private string _routingModeText = "规则路由";
    private string? _currentTunServerHost;
    private bool? _pendingTunLaunchState;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerEntry> Servers { get; } = [];

    public ServerEntry? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                OnPropertyChanged(nameof(SelectedSummary));
                PersistSelectedServer();
                if (!_isInitializing)
                {
                    _ = EnsureSelectedServerAppliedAsync(forceRestart: false);
                }
            }
        }
    }

    public bool IsTunMode
    {
        get => _isTunMode;
        set
        {
            if (SetProperty(ref _isTunMode, value))
            {
                OnPropertyChanged(nameof(TunHintText));
                OnPropertyChanged(nameof(RouteSettingsSummary));
                OnPropertyChanged(nameof(IsRouteSettingsEnabled));
                OnPropertyChanged(nameof(IsSystemProxyToggleEnabled));
                OnPropertyChanged(nameof(IsTunToggleEnabled));

                if (!_isTunInternalUpdate && !_isInitializing)
                {
                    _ = HandleTunToggleAsync(value);
                }
            }
        }
    }

    public bool IsSystemProxyEnabled
    {
        get => _isSystemProxyEnabled;
        set
        {
            if (SetProperty(ref _isSystemProxyEnabled, value))
            {
                OnPropertyChanged(nameof(RouteSettingsSummary));
            }
        }
    }

    public string RoutingModeText
    {
        get => _routingModeText;
        set
        {
            if (SetProperty(ref _routingModeText, value))
            {
                OnPropertyChanged(nameof(RouteSettingsSummary));
            }
        }
    }

    public string StatusText => IsRunning && _activeServer is not null ? $"运行中: {_activeServer.Name}" : "未连接";
    public string SelectedSummary => SelectedServer is null ? "未选择节点" : $"当前选中: {SelectedServer.Name}";
    public Visibility IsEmptyHintVisible => Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsTunToggleEnabled => !_isApplyingSelection;
    public bool IsRouteSettingsEnabled => !_isApplyingSelection;
    public bool IsSystemProxyToggleEnabled => !IsTunMode && !_isApplyingSelection;
    public string TunHintText => IsTunMode ? "TUN 模式参考 XrayUI-dev：当前节点会持续运行，切节点时自动重建 TUN 会话。" : "关闭 TUN 时始终维持本地代理入口；系统代理开关只影响 Windows 代理接管。";
    public string RouteSettingsSummary
    {
        get
        {
            var ruleCount = RouteRulePresetService.CountRules(_settings.RoutingRuleJson, _settings.CustomRules);
            var ruleText = ruleCount > 0 ? $"，规则 {ruleCount} 条" : string.Empty;
            return IsTunMode
                ? $"当前：TUN + {RoutingModeText}{ruleText}。系统代理设置在 TUN 模式下不生效。"
                : $"当前：{RoutingModeText}，系统代理{(IsSystemProxyEnabled ? "已接管" : "未接管")}{ruleText}。";
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowThemeHelper.Apply(this);
        SetWindowIcon();
        AppWindow.Resize(new SizeInt32(1300, 810));
        WindowMinSizeHelper.Apply(this, 1300, 810);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        _xray.RunningChanged += Xray_RunningChanged;
        _ = InitializeAsync();
    }


    private void SetWindowIcon()
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    public void SetTunEnabledSilently(bool value)
    {
        _pendingTunLaunchState = value;
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }


    public async Task PrepareForCoreUpdateAsync()
    {
        await _xray.StopAsync();
        SystemProxyService.ClearProxy();
        CleanupTunRoutesSafely();
        SetActiveServer(null);
        IsRunning = false;
    }
    public void StopBackgroundServicesOnExit(bool fastShutdown = false)
    {
        _xray.RunningChanged -= Xray_RunningChanged;
        SystemProxyService.ClearProxy();

        if (IsTunMode)
        {
            CleanupTunRoutesSafely();
        }

        _xray.StopForShutdown();
        if (!fastShutdown)
        {
            try
            {
                _settings.IsTunMode = false;
                _settings.LastTunServerHost = null;
                _store.SaveSettingsAsync(_settings).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }

    private async Task InitializeAsync()
    {
        _isInitializing = true;
        _settings = await _store.LoadSettingsAsync();
        _settings.RoutingRuleJson = RouteRulePresetService.EnsureRoutingJson(_settings.RoutingRuleJson, _settings.CustomRules);
        _settings.DnsJson = DnsPresetService.EnsureDnsJson(_settings.DnsJson);
        EnsureDefaultLocalPorts();

        foreach (var server in await _store.LoadServersAsync())
        {
            server.IsActive = false;
            Servers.Add(server);
        }

        if (_pendingTunLaunchState.HasValue)
        {
            _settings.IsTunMode = _pendingTunLaunchState.Value;
        }

        _isTunInternalUpdate = true;
        IsTunMode = _settings.IsTunMode;
        _isTunInternalUpdate = false;
        IsSystemProxyEnabled = _settings.IsSystemProxyEnabled;
        RoutingModeText = string.Equals(_settings.RoutingMode, "global", StringComparison.OrdinalIgnoreCase) ? "全局代理" : "规则路由";
        RoutingModeComboBox.SelectedItem = RoutingModeText;
        SocksPortTextBox.Text = _settings.LocalSocksPort.ToString();
        HttpPortTextBox.Text = _settings.LocalHttpPort.ToString();
        SelectedServer = ResolveInitialSelection();
        _isInitializing = false;

        await SaveSettingsSafelyAsync();

        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(RouteSettingsSummary));
        OnPropertyChanged(nameof(TunHintText));

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: false);
        }
    }

    private void EnsureDefaultLocalPorts()
    {
        if (_settings.LocalSocksPort <= 0)
        {
            _settings.LocalSocksPort = 10808;
        }

        if (_settings.LocalHttpPort <= 0)
        {
            _settings.LocalHttpPort = 10809;
        }


    }

    private ServerEntry? ResolveInitialSelection()
    {
        if (Servers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastSelectedServerId))
        {
            var matched = Servers.FirstOrDefault(x => x.Id == _settings.LastSelectedServerId);
            if (matched is not null)
            {
                return matched;
            }
        }

        return Servers[0];
    }

    private async Task EnsureSelectedServerAppliedAsync(bool forceRestart)
    {
        if (_isApplyingSelection || SelectedServer is null)
        {
            return;
        }

        if (!forceRestart && _activeServer is not null && _activeServer.Id == SelectedServer.Id && IsRunning)
        {
            return;
        }

        _isApplyingSelection = true;
        OnPropertyChanged(nameof(IsTunToggleEnabled));
        OnPropertyChanged(nameof(IsRouteSettingsEnabled));
        OnPropertyChanged(nameof(IsSystemProxyToggleEnabled));

        try
        {
            await ConnectServerAsync(SelectedServer);
        }
        finally
        {
            _isApplyingSelection = false;
            OnPropertyChanged(nameof(IsTunToggleEnabled));
            OnPropertyChanged(nameof(IsRouteSettingsEnabled));
            OnPropertyChanged(nameof(IsSystemProxyToggleEnabled));
        }
    }

    private async Task HandleTunToggleAsync(bool wantEnable)
    {
        if (wantEnable && !AdminHelper.IsAdministrator())
        {
            _isTunInternalUpdate = true;
            IsTunMode = false;
            _isTunInternalUpdate = false;

            var confirmed = await ConfirmAsync("开启 TUN 模式", "开启 TUN 模式需要管理员权限，程序将重启。是否继续？");
            if (!confirmed)
            {
                return;
            }

            RestartAsAdmin("--tun");
            return;
        }

        _settings.IsTunMode = wantEnable;
        await SaveSettingsSafelyAsync();

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }

    private static void RestartAsAdmin(string arguments)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            });

            if (Application.Current is App app)
            {
                app.RequestShutdown(fastShutdown: true);
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
        }
        catch
        {
        }
    }

    private async Task ConnectServerAsync(ServerEntry server)
    {

        var portConflict = await PortConflictService.EnsurePortsAvailableForCurrentXrayAsync(_settings.LocalSocksPort, _settings.LocalHttpPort);
        if (!string.IsNullOrWhiteSpace(portConflict))
        {
            await ShowMessageAsync("连接失败", portConflict);
            return;
        }

        string? tunOutboundInterfaceName = null;
        if (IsTunMode)
        {
            tunOutboundInterfaceName = await RunTunPreflightAsync();
            if (tunOutboundInterfaceName is null)
            {
                return;
            }

            await CleanupPersistedTunRoutesAsync();
        }

        var config = XrayConfigBuilder.Build(server, _settings, tunOutboundInterfaceName);
        var ok = await _xray.StartAsync(config);
        if (!ok)
        {
            await ShowMessageAsync("连接失败", string.IsNullOrWhiteSpace(_xray.LastError) ? "xray 启动失败。" : _xray.LastError);
            return;
        }

        _settings.LastSelectedServerId = server.Id;
        if (IsTunMode)
        {
            _currentTunServerHost = server.Host;
            _settings.LastTunServerHost = server.Host;
            SystemProxyService.ClearProxy();
        }
        else
        {
            _settings.LastTunServerHost = null;
            if (IsSystemProxyEnabled)
            {
                SystemProxyService.SetProxy("127.0.0.1", _settings.LocalHttpPort);
            }
            else
            {
                SystemProxyService.ClearProxy();
            }
        }

        await SaveSettingsSafelyAsync();
        SetActiveServer(server);
        IsRunning = true;
    }

    private async void RoutingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || RoutingModeComboBox.SelectedItem is not string selected)
        {
            return;
        }

        var newMode = selected == "全局代理" ? "global" : "smart";
        if (string.Equals(_settings.RoutingMode, newMode, StringComparison.OrdinalIgnoreCase))
        {
            RoutingModeText = selected;
            return;
        }

        RoutingModeText = selected;
        _settings.RoutingMode = newMode;
        await SaveSettingsSafelyAsync();
        OnPropertyChanged(nameof(RouteSettingsSummary));

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }


    private async void SocksPortTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await ApplyLocalPortSettingAsync(SocksPortTextBox, isSocksPort: true);
    }

    private async void HttpPortTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await ApplyLocalPortSettingAsync(HttpPortTextBox, isSocksPort: false);
    }

    private async Task ApplyLocalPortSettingAsync(TextBox sender, bool isSocksPort)
    {
        if (_isInitializing)
        {
            return;
        }

        var currentPort = isSocksPort ? _settings.LocalSocksPort : _settings.LocalHttpPort;
        var otherPort = isSocksPort ? _settings.LocalHttpPort : _settings.LocalSocksPort;
        var rawText = sender.Text?.Trim();

        if (!int.TryParse(rawText, out var newPort) || newPort is < 1 or > 65535)
        {
            sender.Text = currentPort.ToString();
            return;
        }

        if (newPort == currentPort)
        {
            sender.Text = currentPort.ToString();
            return;
        }


        if (isSocksPort)
        {
            _settings.LocalSocksPort = newPort;
        }
        else
        {
            _settings.LocalHttpPort = newPort;
        }

        sender.Text = newPort.ToString();
        await SaveSettingsSafelyAsync();

        if (!isSocksPort && !IsTunMode && IsSystemProxyEnabled)
        {
            SystemProxyService.SetProxy("127.0.0.1", _settings.LocalHttpPort);
        }

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }

    private async void SystemProxyToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.IsSystemProxyEnabled = IsSystemProxyEnabled;
        await SaveSettingsSafelyAsync();
        OnPropertyChanged(nameof(RouteSettingsSummary));

        if (!IsTunMode)
        {
            if (IsSystemProxyEnabled)
            {
                SystemProxyService.SetProxy("127.0.0.1", _settings.LocalHttpPort);
            }
            else
            {
                SystemProxyService.ClearProxy();
            }
        }
    }


    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new MoreWindow(this, PrepareForCoreUpdateAsync, () => HasActiveConnection, RestartSelectedServerAsync);
        window.AppWindow.Show();
        window.Activate();
    }

    public bool HasActiveConnection => IsRunning && SelectedServer is not null;

    public async Task RestartSelectedServerAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        await EnsureSelectedServerAppliedAsync(forceRestart: true);
    }

    private async void RouteRulesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new RouteRulesWindow(this, _settings.RoutingRuleJson);
        var savedRoutingJson = await window.ShowModalAsync();
        if (savedRoutingJson is null)
        {
            return;
        }

        _settings.RoutingRuleJson = savedRoutingJson;
        await SaveSettingsSafelyAsync();
        OnPropertyChanged(nameof(RouteSettingsSummary));

        if (SelectedServer is not null && string.Equals(_settings.RoutingMode, "smart", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }

    private async void DnsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new DnsSettingsWindow(this, _settings.DnsJson);
        var savedDnsJson = await window.ShowModalAsync();
        if (savedDnsJson is null)
        {
            return;
        }

        _settings.DnsJson = savedDnsJson;
        await SaveSettingsSafelyAsync();

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }

    private async Task<string?> RunTunPreflightAsync()
    {
        if (!_tunService.IsWintunAvailable())
        {
            await ShowMessageAsync("TUN 模式错误", $"找不到 wintun.dll\n路径：{_tunService.GetExpectedWintunPath()}");
            return null;
        }

        var iface = _tunService.DetectDefaultOutboundInterfaceName();
        if (string.IsNullOrWhiteSpace(iface))
        {
            await ShowMessageAsync("TUN 模式错误", "无法确定默认出站网卡，请确认 Wi-Fi 或以太网已连接。");
            return null;
        }

        SystemProxyService.ClearProxy();
        return iface;
    }

    private async Task CleanupPersistedTunRoutesAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastTunServerHost))
        {
            return;
        }

        CleanupTunRoutesSafely();
        _settings.LastTunServerHost = null;
        await SaveSettingsSafelyAsync();
    }

    private void CleanupTunRoutesSafely()
    {
        var serverHost = !string.IsNullOrWhiteSpace(_currentTunServerHost)
            ? _currentTunServerHost
            : _settings.LastTunServerHost;

        if (string.IsNullOrWhiteSpace(serverHost))
        {
            return;
        }

        try
        {
            _tunService.CleanupTunRoutes(serverHost);
        }
        catch
        {
        }
        finally
        {
            _currentTunServerHost = null;
        }
    }

    private async void ImportFromClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        var text = await ReadClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            await ShowMessageAsync("没有可导入内容", "剪贴板里没有文本节点链接。");
            return;
        }

        var count = 0;
        ServerEntry? firstImported = null;
        foreach (var token in Regex.Split(text, @"\s+"))
        {
            var trimmed = token.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var server = NodeLinkParser.Parse(trimmed);
            if (server is null)
            {
                continue;
            }

            if (Servers.Any(x => x.Protocol == server.Protocol && x.Host == server.Host && x.Port == server.Port && x.Name == server.Name))
            {
                continue;
            }

            Servers.Add(server);
            firstImported ??= server;
            count++;
        }

        if (count == 0)
        {
            await ShowMessageAsync("导入完成", "没有识别到可导入的新节点。");
            return;
        }

        if (SelectedServer is null)
        {
            SelectedServer = firstImported;
        }

        await _store.SaveServersAsync(Servers);
        OnPropertyChanged(nameof(IsEmptyHintVisible));
    }

    private async void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ServerEditorWindow(this, new ServerEntry { Protocol = "ss", Network = "tcp", Security = "none" });
        var server = await window.ShowModalAsync();
        if (server is null)
        {
            return;
        }

        Servers.Add(server);
        SelectedServer = server;
        await _store.SaveServersAsync(Servers);
        OnPropertyChanged(nameof(IsEmptyHintVisible));
    }


    private async void EditServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        var window = new ServerEditorWindow(this, server, "编辑节点", "保存");
        var replacement = await window.ShowModalAsync();
        if (replacement is null)
        {
            return;
        }

        var index = Servers.IndexOf(server);
        if (index < 0)
        {
            return;
        }

        Servers[index] = replacement;

        if (ReferenceEquals(SelectedServer, server) || ReferenceEquals(_activeServer, server))
        {
            SelectedServer = replacement;
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }

        await _store.SaveServersAsync(Servers);
    }

    private async void DeleteServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        if (!await ConfirmAsync("删除节点", $"确认删除节点“{server.Name}”？"))
        {
            return;
        }

        var wasActive = ReferenceEquals(_activeServer, server);
        if (wasActive)
        {
            await ShowMessageAsync("无法删除", "正在使用的节点无法删除，请先切换到其他节点。");
            return;
        }

        var index = Servers.IndexOf(server);
        var wasSelected = ReferenceEquals(SelectedServer, server);
        Servers.Remove(server);

        if (Servers.Count == 0)
        {
            SelectedServer = null;
        }
        else if (wasSelected)
        {
            _isApplyingSelection = true;
            SelectedServer = Servers[Math.Clamp(index, 0, Servers.Count - 1)];
            _isApplyingSelection = false;
        }

        if (_settings.LastSelectedServerId == server.Id)
        {
            _settings.LastSelectedServerId = SelectedServer?.Id ?? string.Empty;
            await SaveSettingsSafelyAsync();
        }

        await _store.SaveServersAsync(Servers);
        OnPropertyChanged(nameof(IsEmptyHintVisible));
    }

    private async void ShareServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        var link = NodeLinkSerializer.ToLink(server);
        if (string.IsNullOrWhiteSpace(link))
        {
            await ShowMessageAsync("无法分享", "当前节点协议暂不支持导出分享链接。");
            return;
        }

        if (TryCopyShareLink(link))
        {
            await ShowMessageAsync("已复制", "分享链接已复制到剪贴板。");
            return;
        }

        await Task.Delay(120);
        if (TryCopyShareLink(link))
        {
            await ShowMessageAsync("已复制", "分享链接已复制到剪贴板。");
            return;
        }

        await ShowShareFallbackAsync(server.Name, link);
    }

    private void Xray_RunningChanged(object? sender, bool running)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (running)
            {
                return;
            }

            if (IsTunMode && !_isApplyingSelection)
            {
                CleanupTunRoutesSafely();
            }

            if (!_isApplyingSelection)
            {
                SystemProxyService.ClearProxy();
                SetActiveServer(null);
                IsRunning = false;
            }
        });
    }

    private void SetActiveServer(ServerEntry? server)
    {
        foreach (var entry in Servers)
        {
            entry.IsActive = false;
        }

        _activeServer = server;

        if (_activeServer is not null)
        {
            _activeServer.IsActive = true;
        }

        OnPropertyChanged(nameof(StatusText));
    }

    private void PersistSelectedServer()
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.LastSelectedServerId = SelectedServer?.Id ?? string.Empty;
        _ = SaveSettingsSafelyAsync();
    }

    private async Task SaveSettingsSafelyAsync()
    {
        try
        {
            await _store.SaveSettingsAsync(_settings);
        }
        catch
        {
        }
    }


    private static bool TryCopyShareLink(string link)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(link);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ReadClipboardTextAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            return null;
        }

        return await content.GetTextAsync();
    }

    private async Task ShowShareFallbackAsync(string? serverName, string link)
    {
        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(serverName) ? "分享链接" : $"分享链接 - {serverName}",
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "写入剪贴板失败，请手动复制下面的分享链接。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBox
                    {
                        Text = link,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        AcceptsReturn = true,
                        MinWidth = 420,
                        MaxWidth = 560,
                        MaxHeight = 240
                    }
                }
            }
        };

        await dialog.ShowAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        StopBackgroundServicesOnExit();
    }

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





























