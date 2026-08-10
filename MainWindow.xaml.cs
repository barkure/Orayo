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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace Orayo;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppStore _store = new();
    private readonly RuntimeService _runtime;
    private readonly ServerLatencyService _serverLatencyService = new();
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
    private bool _isLatencyRefreshing;
    private string _routingModeText = Strings.RoutingRuleMode;
    private CancellationTokenSource? _latencyRefreshCts;
    private readonly List<Subscription> _subscriptions = new();
    private bool _isSubscriptionRefreshing;
    private bool _isSubscriptionAdding;
    private bool _isSubscriptionDeleting;

    private static readonly Brush LatencyDeepGreenBrush = CreateBrush(0x00, 0x82, 0x35);
    private static readonly Brush LatencyLightGreenBrush = CreateBrush(0x7c, 0xcf, 0x00);
    private static readonly Brush LatencyYellowBrush = CreateBrush(0xfd, 0xc7, 0x00);
    private static readonly Brush LatencyOrangeBrush = CreateBrush(0xff, 0x69, 0x00);
    private static readonly Brush LatencyRedBrush = CreateBrush(0xd9, 0x2d, 0x20);
    private static readonly Brush LatencyWhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff);
    private static readonly Brush LatencyDarkForegroundBrush = CreateBrush(0x1f, 0x29, 0x37);

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
                if (!_isInitializing && !_isSubscriptionRefreshing)
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

    public string StatusText => IsRunning && _activeServer is not null ? string.Format(Strings.StatusRunning, _activeServer.Name) : Strings.StatusDisconnected;
    public string SelectedSummary => SelectedServer is null ? Strings.StatusNotSelected : string.Format(Strings.StatusCurrentSelected, SelectedServer.Name);
    public Visibility IsEmptyHintVisible => Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsLatencyRefreshEnabled => !_isLatencyRefreshing && Servers.Count > 0;
    public bool IsSubscriptionRefreshEnabled =>
        !_isSubscriptionRefreshing && !_isSubscriptionAdding && !_isSubscriptionDeleting && _subscriptions.Count > 0;
    public bool IsSubscriptionMutationEnabled =>
        !_isSubscriptionRefreshing && !_isSubscriptionAdding && !_isSubscriptionDeleting;
    public bool IsTunToggleEnabled => !_isApplyingSelection;
    public bool IsRouteSettingsEnabled => !_isApplyingSelection;
    public bool IsSystemProxyToggleEnabled => !IsTunMode && !_isApplyingSelection;
    public string TunHintText => IsTunMode ? Strings.TunHintOn : Strings.TunHintOff;
    public string RouteSettingsSummary
    {
        get
        {
            var ruleCount = RouteRulePresetService.CountRules(_settings.RoutingRuleJson);
            var ruleText = ruleCount > 0 ? string.Format(Strings.RouteRuleCount, ruleCount) : string.Empty;
            return IsTunMode
                ? string.Format(Strings.RouteSummaryTun, RoutingModeText, ruleText)
                : string.Format(Strings.RouteSummaryNormal, RoutingModeText, IsSystemProxyEnabled ? Strings.ProxyEnabled : Strings.ProxyDisabled, ruleText);
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
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "AppIcon.ico");
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

        _subscriptions.AddRange(await _store.LoadSubscriptionsAsync());

        _isTunInternalUpdate = true;
        IsTunMode = _settings.IsTunMode;
        _isTunInternalUpdate = false;
        IsSystemProxyEnabled = _settings.IsSystemProxyEnabled;
        RoutingModeText = string.Equals(_settings.RoutingMode, "global", StringComparison.OrdinalIgnoreCase) ? Strings.GlobalProxyMode : Strings.RoutingRuleMode;
        RoutingModeComboBox.Items.Clear();
        RoutingModeComboBox.Items.Add(Strings.RoutingRuleMode);
        RoutingModeComboBox.Items.Add(Strings.GlobalProxyMode);
        RoutingModeComboBox.SelectedItem = RoutingModeText;
        SocksPortTextBox.Text = _settings.LocalSocksPort.ToString();
        HttpPortTextBox.Text = _settings.LocalHttpPort.ToString();
        SelectedServer = ResolveInitialSelection();
        _isInitializing = false;

        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
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

        ScheduleLatencyRefresh();
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

    private void ScheduleLatencyRefresh()
    {
        if (IsTunMode)
        {
            _latencyRefreshCts?.Cancel();
            _latencyRefreshCts?.Dispose();
            _latencyRefreshCts = null;
            return;
        }

        _latencyRefreshCts?.Cancel();
        _latencyRefreshCts?.Dispose();
        _latencyRefreshCts = new CancellationTokenSource();
        _ = RefreshLatenciesAsync(_latencyRefreshCts.Token);
    }

    private async Task RefreshLatenciesAsync(CancellationToken cancellationToken)
    {
        if (Servers.Count == 0)
        {
            return;
        }

        _isLatencyRefreshing = true;
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));

        try
        {
            await Task.Delay(250, cancellationToken);

            // Probe nodes with bounded concurrency instead of one-by-one.
            using var gate = new SemaphoreSlim(8);
            var probes = Servers.Select(async server =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return (server, await _serverLatencyService.ProbeAsync(server, cancellationToken));
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var results = await Task.WhenAll(probes);
            foreach (var (server, result) in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyLatencyResult(server, result);
            }
        }
        finally
        {
            _isLatencyRefreshing = false;
            OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        }
    }

    private static void ApplyLatencyResult(ServerEntry server, LatencyProbeResult result)
    {
        if (result.TimedOut || result.Milliseconds is null)
        {
            server.LatencyBadgeText = Strings.LatencyTimeout;
            server.LatencyBadgeBackground = LatencyRedBrush;
            server.LatencyBadgeForeground = LatencyWhiteForegroundBrush;
            server.LatencyBadgeVisibility = Visibility.Visible;
            return;
        }

        var milliseconds = Math.Max(0, result.Milliseconds.Value);
        server.LatencyBadgeText = $"{milliseconds}ms";
        server.LatencyBadgeVisibility = Visibility.Visible;

        if (milliseconds <= 50)
        {
            server.LatencyBadgeBackground = LatencyDeepGreenBrush;
            server.LatencyBadgeForeground = LatencyWhiteForegroundBrush;
            return;
        }

        if (milliseconds <= 150)
        {
            server.LatencyBadgeBackground = LatencyLightGreenBrush;
            server.LatencyBadgeForeground = LatencyDarkForegroundBrush;
            return;
        }

        if (milliseconds <= 250)
        {
            server.LatencyBadgeBackground = LatencyYellowBrush;
            server.LatencyBadgeForeground = LatencyDarkForegroundBrush;
            return;
        }

        server.LatencyBadgeBackground = LatencyOrangeBrush;
        server.LatencyBadgeForeground = LatencyWhiteForegroundBrush;
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(new Windows.UI.Color { A = 255, R = r, G = g, B = b });
    }

    private async void LatencyRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsTunMode)
        {
            await ShowMessageAsync(Strings.ErrCannotTest, Strings.ErrCloseTunForTest);
            return;
        }

        ScheduleLatencyRefresh();
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
            await ShowTunErrorAsync(string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError) ? Strings.ErrCannotStartTunBroker : _runtime.TunBrokerLastError);
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

            await ShowTunErrorAsync(string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError) ? Strings.ErrCannotStartTunBroker : _runtime.TunBrokerLastError);
            return;
        }

        var result = await _runtime.ConnectAsync(server, _settings, _runtimeState);
        if (!result.Success)
        {
            MarkStateDirty();
            if (string.Equals(result.ErrorTitle, Strings.ErrTunModeError, StringComparison.Ordinal))
            {
                await ShowTunErrorAsync(result.ErrorMessage ?? Strings.ErrConnectionFailedMsg);
            }
            else
            {
                await ShowMessageAsync(result.ErrorTitle ?? Strings.ErrConnectionFailed, result.ErrorMessage ?? Strings.ErrConnectionFailedMsg);
            }
            return;
        }

        MarkStateDirty();
    }

    private async Task FallbackFromStartupTunAsync(ServerEntry server)
    {
        var errorMessage = string.IsNullOrWhiteSpace(_runtime.TunBrokerLastError)
            ? Strings.ErrCannotStartTunBroker
            : _runtime.TunBrokerLastError;

        _settings.IsTunMode = false;
        SyncTunUiWithSettings();
        await SaveSettingsSafelyAsync();

        var fallbackResult = await _runtime.ConnectAsync(server, _settings, _runtimeState);
        if (!fallbackResult.Success)
        {
            MarkStateDirty();
            await ShowMessageAsync(
                fallbackResult.ErrorTitle ?? Strings.ErrConnectionFailed,
                fallbackResult.ErrorMessage ?? Strings.ErrConnectionFailedMsg);
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

        var newMode = selected == Strings.GlobalProxyMode ? "global" : "smart";
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
        var window = new MoreWindow(this, _settings, PrepareForCoreUpdateAsync);
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
            await ShowMessageAsync(Strings.MsgNoClipboard, Strings.MsgNoClipboardContent);
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
            await ShowMessageAsync(Strings.MsgImportDone, Strings.MsgNoNewNodes);
            return;
        }

        if (SelectedServer is null)
        {
            SelectedServer = firstImported;
        }

        await _store.SaveServersAsync(Servers);
        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        ScheduleLatencyRefresh();
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
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        ScheduleLatencyRefresh();
    }


    private async void EditServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        var window = new ServerEditorWindow(this, server, Strings.TitleEditServer, Strings.ButtonSave);
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
        ScheduleLatencyRefresh();
    }

    private async void DeleteServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        if (!await ConfirmAsync(Strings.TitleDeleteServer, string.Format(Strings.MsgConfirmDelete, server.Name)))
        {
            return;
        }

        var wasActive = ReferenceEquals(_activeServer, server);
        if (wasActive)
        {
            await ShowMessageAsync(Strings.ErrCannotDelete, Strings.ErrCannotDeleteActive);
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
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        ScheduleLatencyRefresh();
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
            await ShowMessageAsync(Strings.ErrCannotShare, Strings.ErrCannotShareProtocol);
            return;
        }

        if (TryCopyShareLink(link))
        {
            await ShowMessageAsync(Strings.MsgCopied, Strings.MsgShareLinkCopied);
            return;
        }

        await Task.Delay(120);
        if (TryCopyShareLink(link))
        {
            await ShowMessageAsync(Strings.MsgCopied, Strings.MsgShareLinkCopied);
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
            Title = string.IsNullOrWhiteSpace(serverName) ? Strings.TitleShareLink : $"{Strings.TitleShareLink} - {serverName}",
            PrimaryButtonText = Strings.ButtonClose,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.MsgClipboardFailed,
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
            CloseButtonText = Strings.ButtonOK,
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
                    Strings.ErrTunModeError,
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
            PrimaryButtonText = Strings.ButtonOK,
            CloseButtonText = Strings.ButtonCancel,
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ── Subscription ────────────────────────────────────────────────────────

    private async void SubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubscriptionRefreshing || _isSubscriptionAdding || _isSubscriptionDeleting)
        {
            return;
        }

        var input = await ShowSubscribeDialogAsync();
        if (input is null || _isSubscriptionRefreshing || _isSubscriptionAdding || _isSubscriptionDeleting)
        {
            return;
        }

        _isSubscriptionAdding = true;
        OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
        OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        try
        {
            await SubscribeAsync(input.Value.Url, input.Value.Remarks);
        }
        finally
        {
            _isSubscriptionAdding = false;
            OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
            OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        }
    }

    private async void RefreshSubscriptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubscriptionRefreshing || _isSubscriptionAdding || _isSubscriptionDeleting)
        {
            return;
        }

        var enabled = _subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0)
        {
            await ShowMessageAsync(Strings.TipRefreshSubscriptions, Strings.MsgSubNone);
            return;
        }

        var failed = 0;
        string? lastError = null;
        string? saveError = null;
        var updates = new List<(Subscription Subscription, List<ServerEntry> Nodes, string Payload, DateTime UpdatedAt)>();
        _isSubscriptionRefreshing = true;
        OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
        OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        try
        {
            foreach (var sub in enabled)
            {
                var (content, error) = await SubscriptionService.FetchAsync(sub.Url, sub.UserAgent, GetSubscriptionProxyPort());
                if (content is null)
                {
                    failed++;
                    lastError = error;
                    continue;
                }

                var nodes = SubscriptionService.ParseContent(content);
                if (nodes.Count == 0)
                {
                    failed++;
                    lastError = Strings.MsgSubNoNodes;
                    continue;
                }

                if (_subscriptions.Contains(sub))
                {
                    updates.Add((sub, nodes, content, DateTime.UtcNow));
                }
            }

            if (updates.Count > 0)
            {
                var updatedServers = Servers.ToList();
                var updatedSubscriptions = _subscriptions.Select(CloneSubscription).ToList();
                foreach (var update in updates)
                {
                    updatedServers.RemoveAll(s => s.SubscriptionId == update.Subscription.Id);
                    foreach (var node in update.Nodes)
                    {
                        node.SubscriptionId = update.Subscription.Id;
                    }
                    updatedServers.AddRange(update.Nodes);

                    var savedSubscription = updatedSubscriptions.First(s => s.Id == update.Subscription.Id);
                    savedSubscription.RawPayload = update.Payload;
                    savedSubscription.LastUpdated = update.UpdatedAt;
                }

                try
                {
                    await _store.SaveServersAndSubscriptionsAsync(updatedServers, updatedSubscriptions);
                }
                catch (Exception ex)
                {
                    saveError = ex.Message;
                }

                if (saveError is null)
                {
                    var reconnectSelected = false;
                    foreach (var update in updates)
                    {
                        reconnectSelected |= ReplaceSubscriptionNodes(
                            update.Subscription,
                            update.Nodes,
                            update.Payload,
                            update.UpdatedAt);
                    }

                    if (reconnectSelected && SelectedServer is not null)
                    {
                        await EnsureSelectedServerAppliedAsync(forceRestart: true);
                    }

                    OnPropertyChanged(nameof(IsEmptyHintVisible));
                    OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
                    ScheduleLatencyRefresh();
                }
            }
        }
        finally
        {
            _isSubscriptionRefreshing = false;
            OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
            OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        }

        if (saveError is not null)
        {
            await ShowMessageAsync(Strings.TipRefreshSubscriptions, string.Format(Strings.MsgSubSaveFailed, saveError));
            return;
        }

        var succeeded = updates.Count;
        var summary = succeeded > 0
            ? string.Format(Strings.MsgSubRefreshDone, succeeded, failed)
            : string.Format(Strings.MsgSubRefreshFailed, lastError ?? Strings.MsgSubNoNodes);
        await ShowMessageAsync(Strings.TipRefreshSubscriptions, summary);
    }

    private async void DeleteSubscriptionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubscriptionRefreshing || _isSubscriptionAdding || _isSubscriptionDeleting)
        {
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not ServerEntry server)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(server.SubscriptionId))
        {
            return;
        }

        var sub = _subscriptions.FirstOrDefault(s => s.Id == server.SubscriptionId);
        var subName = sub?.Remarks ?? server.SubscriptionId;
        if (!await ConfirmAsync(Strings.TitleDeleteSubscription, string.Format(Strings.MsgConfirmDeleteSubscription, subName))
            || _isSubscriptionRefreshing
            || _isSubscriptionAdding
            || _isSubscriptionDeleting)
        {
            return;
        }

        if (string.Equals(_activeServer?.SubscriptionId, server.SubscriptionId, StringComparison.Ordinal))
        {
            await ShowMessageAsync(Strings.ErrCannotDelete, Strings.ErrCannotDeleteActive);
            return;
        }

        _isSubscriptionDeleting = true;
        OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
        OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        try
        {
            var selectedBelongsToSubscription = string.Equals(
                SelectedServer?.SubscriptionId,
                server.SubscriptionId,
                StringComparison.Ordinal);
            var remainingServers = Servers.Where(s => s.SubscriptionId != server.SubscriptionId).ToList();
            var remainingSubscriptions = _subscriptions.Where(s => s.Id != server.SubscriptionId).ToList();
            await _store.SaveServersAndSubscriptionsAsync(remainingServers, remainingSubscriptions);

            foreach (var item in Servers.Where(s => s.SubscriptionId == server.SubscriptionId).ToList())
            {
                Servers.Remove(item);
            }

            if (selectedBelongsToSubscription)
            {
                _isApplyingSelection = true;
                SelectedServer = Servers.FirstOrDefault();
                _isApplyingSelection = false;
            }

            if (sub is not null)
            {
                _subscriptions.Remove(sub);
            }

            OnPropertyChanged(nameof(IsEmptyHintVisible));
            OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
            ScheduleLatencyRefresh();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Strings.TitleDeleteSubscription, string.Format(Strings.MsgSubSaveFailed, ex.Message));
            return;
        }
        finally
        {
            _isSubscriptionDeleting = false;
            OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
            OnPropertyChanged(nameof(IsSubscriptionMutationEnabled));
        }
    }

    private async Task<(string Url, string Remarks)?> ShowSubscribeDialogAsync()
    {
        var urlBox = new TextBox { PlaceholderText = Strings.LabelSubUrl, MinWidth = 420 };
        var remarksBox = new TextBox { PlaceholderText = Strings.LabelSubRemarks, MinWidth = 420 };
        var dialog = new ContentDialog
        {
            Title = Strings.TitleSubscribe,
            PrimaryButtonText = Strings.ButtonSubscribe,
            CloseButtonText = Strings.ButtonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { urlBox, remarksBox }
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var url = urlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return (url, remarksBox.Text?.Trim() ?? string.Empty);
    }

    private async Task SubscribeAsync(string url, string remarks)
    {
        if (_isSubscriptionRefreshing)
        {
            return;
        }

        var (content, error) = await SubscriptionService.FetchAsync(url, null, GetSubscriptionProxyPort());
        if (content is null)
        {
            await ShowMessageAsync(Strings.TitleSubscribe, string.Format(Strings.MsgSubFetchFailed, error));
            return;
        }

        var nodes = SubscriptionService.ParseContent(content);
        if (nodes.Count == 0)
        {
            await ShowMessageAsync(Strings.TitleSubscribe, Strings.MsgSubNoNodes);
            return;
        }

        var name = string.IsNullOrWhiteSpace(remarks) ? DeriveSubscriptionName(url) : remarks;
        var sub = new Subscription
        {
            Url = url,
            Remarks = name,
            RawPayload = content,
            LastUpdated = DateTime.UtcNow
        };

        foreach (var node in nodes)
        {
            node.SubscriptionId = sub.Id;
        }

        var updatedServers = Servers.Concat(nodes).ToList();
        var updatedSubscriptions = _subscriptions.Append(sub).ToList();
        try
        {
            await _store.SaveServersAndSubscriptionsAsync(updatedServers, updatedSubscriptions);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(Strings.TitleSubscribe, string.Format(Strings.MsgSubSaveFailed, ex.Message));
            return;
        }

        foreach (var node in nodes)
        {
            Servers.Add(node);
        }
        _subscriptions.Add(sub);
        if (SelectedServer is null)
        {
            SelectedServer = nodes[0];
        }

        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        OnPropertyChanged(nameof(IsSubscriptionRefreshEnabled));
        ScheduleLatencyRefresh();

        await ShowMessageAsync(Strings.MsgSubImportDone, string.Format(Strings.MsgSubImported, nodes.Count));
    }

    /// <summary>
    /// Whole-group replace (V2rayN semantics): drop every node of this subscription,
    /// re-insert the freshly parsed list, and keep the selection when the previously
    /// selected node still exists in the new list.
    /// </summary>
    private bool ReplaceSubscriptionNodes(
        Subscription sub,
        List<ServerEntry> nodes,
        string payload,
        DateTime updatedAt)
    {
        var selected = SelectedServer;
        var active = _activeServer;
        var selectedBelongsToSubscription = selected?.SubscriptionId == sub.Id;
        var activeBelongsToSubscription = active?.SubscriptionId == sub.Id;

        foreach (var server in Servers.Where(s => s.SubscriptionId == sub.Id).ToList())
        {
            Servers.Remove(server);
        }

        foreach (var node in nodes)
        {
            node.SubscriptionId = sub.Id;
            Servers.Add(node);
        }

        if (selectedBelongsToSubscription || activeBelongsToSubscription)
        {
            var previous = activeBelongsToSubscription ? active : selected;
            SelectedServer = previous is null
                ? nodes[0]
                : FindReplacementNode(nodes, previous) ?? nodes[0];
        }

        sub.RawPayload = payload;
        sub.LastUpdated = updatedAt;
        return selectedBelongsToSubscription || activeBelongsToSubscription;
    }

    private static ServerEntry? FindReplacementNode(IEnumerable<ServerEntry> nodes, ServerEntry previous)
    {
        return nodes.FirstOrDefault(node =>
            string.Equals(node.Protocol, previous.Protocol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Host, previous.Host, StringComparison.OrdinalIgnoreCase)
            && node.Port == previous.Port
            && string.Equals(node.Uuid, previous.Uuid, StringComparison.Ordinal)
            && string.Equals(node.Username, previous.Username, StringComparison.Ordinal)
            && string.Equals(node.Password, previous.Password, StringComparison.Ordinal));
    }

    private static Subscription CloneSubscription(Subscription subscription)
    {
        return new Subscription
        {
            Id = subscription.Id,
            Url = subscription.Url,
            Remarks = subscription.Remarks,
            Enabled = subscription.Enabled,
            UserAgent = subscription.UserAgent,
            LastUpdated = subscription.LastUpdated,
            RawPayload = subscription.RawPayload
        };
    }

    private int? GetSubscriptionProxyPort()
    {
        if (IsTunMode || !_runtime.IsRunning || !_settings.IsSystemProxyEnabled)
        {
            return null;
        }

        return _settings.LocalHttpPort;
    }

    private static string DeriveSubscriptionName(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (!string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.Host;
            }
        }
        catch
        {
        }

        return Strings.Unknown;
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

























