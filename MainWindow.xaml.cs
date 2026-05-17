using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
using Forms = System.Windows.Forms;

namespace Orayo;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppStore _store = new();
    private readonly RuntimeService _runtime;
    private AppSettings _settings = new();
    private AppRuntimeState _runtimeState = new();
    private ServerEntry? _selectedServer;
    private ServerEntry? _activeServer;
    private bool _isRunning;
    private bool _isInitializing;
    private bool _isTunMode;
    private bool _isSystemProxyEnabled = true;
    private bool _isTunInternalUpdate;
    private bool _isApplyingSelection;
    private bool _isStateDirty;
    private bool _isRestoringStartupSession;
    private string _routingModeText = "规则路由";

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
            var ruleCount = RouteRulePresetService.CountRules(_settings.RoutingRuleJson);
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

    public MainWindow(RuntimeService runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        WindowThemeHelper.Apply(this);
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);
        SetWindowIcon();
        AppWindow.Resize(new SizeInt32(1300, 815));
        WindowMinSizeHelper.Apply(this, 1300, 815);
        AppWindow.Closing += OnAppWindowClosing;
        _runtime.StateChanged += Runtime_StateChanged;
    }

    public Task StartAsync()
    {
        return InitializeAsync();
    }


    private void SetWindowIcon()
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public void HideForShutdown()
    {
        AppWindow.Hide();
    }

    public async Task PersistStateForShutdownAsync()
    {
        _settings.IsTunMode = IsTunMode;
        _settings.IsSystemProxyEnabled = IsSystemProxyEnabled;
        if (_isStateDirty)
        {
            await SaveStateSafelyAsync();
            _isStateDirty = false;
        }
    }


    public async Task PrepareForCoreUpdateAsync()
    {
        await _runtime.PrepareForCoreUpdateAsync();
    }

    private async Task InitializeAsync()
    {
        _isInitializing = true;
        _settings = await _store.LoadSettingsAsync();
        _runtimeState = await _store.LoadRuntimeStateAsync();
        _settings.RoutingRuleJson = RouteRulePresetService.EnsureRoutingJson(_settings.RoutingRuleJson);
        _settings.DnsJson = DnsPresetService.EnsureDnsJson(_settings.DnsJson);
        EnsureDefaultLocalPorts();

        foreach (var server in await _store.LoadServersAsync())
        {
            server.IsActive = false;
            Servers.Add(server);
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

        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(RouteSettingsSummary));
        OnPropertyChanged(nameof(TunHintText));

        if (SelectedServer is not null)
        {
            _isRestoringStartupSession = true;
            try
            {
                await EnsureSelectedServerAppliedAsync(forceRestart: false);
            }
            finally
            {
                _isRestoringStartupSession = false;
                SyncTunUiWithSettings();
            }
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

        if (!string.IsNullOrWhiteSpace(_runtimeState.LastSelectedServerId))
        {
            var matched = Servers.FirstOrDefault(x => x.Id == _runtimeState.LastSelectedServerId);
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

        if (!forceRestart && _runtime.ActiveServer is not null && _runtime.ActiveServer.Id == SelectedServer.Id && _runtime.IsRunning)
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
        if (wantEnable && !await EnsureTunCanStartAsync())
        {
            _isTunInternalUpdate = true;
            IsTunMode = _settings.IsTunMode;
            _isTunInternalUpdate = false;
            await ShowTunErrorAsync(string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError) ? "无法启动 TUN 权限代理。" : _runtime.TunBrokerLastError);
            return;
        }

        _settings.IsTunMode = wantEnable;
        await SaveSettingsSafelyAsync();

        if (SelectedServer is not null)
        {
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }
    }

    private async Task ConnectServerAsync(ServerEntry server)
    {
        if (IsTunMode && !await EnsureTunCanStartAsync())
        {
            if (_isRestoringStartupSession)
            {
                await FallbackFromStartupTunAsync(server);
                return;
            }

            await ShowTunErrorAsync(string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError) ? "无法启动 TUN 权限代理。" : _runtime.TunBrokerLastError);
            return;
        }

        var result = await _runtime.ConnectAsync(server, _settings, _runtimeState);
        if (!result.Success)
        {
            MarkStateDirty();
            if (string.Equals(result.ErrorTitle, "TUN 模式错误", StringComparison.Ordinal))
            {
                await ShowTunErrorAsync(result.ErrorMessage ?? "连接失败。");
            }
            else
            {
                await ShowMessageAsync(result.ErrorTitle ?? "连接失败", result.ErrorMessage ?? "连接失败。");
            }
            return;
        }

        MarkStateDirty();
    }

    private async Task FallbackFromStartupTunAsync(ServerEntry server)
    {
        var errorMessage = string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError)
            ? "无法启动 TUN 权限代理。"
            : _runtime.TunBrokerLastError;

        _settings.IsTunMode = false;
        SyncTunUiWithSettings();
        await SaveSettingsSafelyAsync();

        var fallbackResult = await _runtime.ConnectAsync(server, _settings, _runtimeState);
        if (!fallbackResult.Success)
        {
            MarkStateDirty();
            await ShowMessageAsync(
                fallbackResult.ErrorTitle ?? "连接失败",
                fallbackResult.ErrorMessage ?? "连接失败。");
            return;
        }

        MarkStateDirty();
        await ShowTunErrorAsync(errorMessage);
    }

    private void SyncTunUiWithSettings()
    {
        _isTunInternalUpdate = true;
        IsTunMode = _settings.IsTunMode;
        _isTunInternalUpdate = false;
    }

    private async Task<bool> EnsureTunCanStartAsync()
    {
        if (!IsTunMode)
        {
            return true;
        }

        if (await _runtime.EnsureTunBrokerAvailableAsync())
        {
            return true;
        }

        return false;
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

    private void ServerCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DependencyObject card)
        {
            return;
        }

        SetServerCardHoverOpacity(card, 1);
    }

    private void ServerCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DependencyObject card)
        {
            return;
        }

        SetServerCardHoverOpacity(card, 0);
    }

    private static void SetServerCardHoverOpacity(DependencyObject card, double opacity)
    {
        if (FindDescendantByName<Border>(card, "ServerCardHoverOverlay") is { } overlay)
        {
            overlay.Opacity = opacity;
        }
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            var nested = FindDescendantByName<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
            _runtime.ApplySystemProxy(_settings);
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
            _runtime.ApplySystemProxy(_settings);
        }
    }


    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new MoreWindow(this, _settings, PrepareForCoreUpdateAsync, () => HasActiveConnection, RestartSelectedServerAsync);
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

        if (_runtimeState.LastSelectedServerId == server.Id)
        {
            _runtimeState.LastSelectedServerId = SelectedServer?.Id ?? string.Empty;
            MarkStateDirty();
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

    private void Runtime_StateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetActiveServer(_runtime.ActiveServer);
            IsRunning = _runtime.IsRunning;
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

        _runtimeState.LastSelectedServerId = SelectedServer?.Id ?? string.Empty;
        MarkStateDirty();
    }

    private void MarkStateDirty()
    {
        _isStateDirty = true;
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

    private async Task SaveRuntimeStateSafelyAsync()
    {
        try
        {
            await _store.SaveRuntimeStateAsync(_runtimeState);
        }
        catch
        {
        }
    }

    private async Task SaveStateSafelyAsync()
    {
        await SaveSettingsSafelyAsync();
        await SaveRuntimeStateSafelyAsync();
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

    private Task ShowTunErrorAsync(string message)
    {
        return Task.Run(() =>
        {
            try
            {
                Forms.MessageBox.Show(
                    message,
                    "TUN 模式错误",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Error);
            }
            catch
            {
            }
        });
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





























