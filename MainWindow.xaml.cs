using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orayo.Helpers;
using Orayo.Models;
using Orayo.Services;
using Orayo.Views;
using Orayo.Application;
using Orayo.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace Orayo;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppServices _services;
    private readonly AppSession _session;
    private readonly RuntimeService _runtime;
    private readonly ServerLatencyService _serverLatencyService = new();
    private readonly AppSettings _settings;
    private readonly AppRuntimeState _runtimeState;
    private ServerItemViewModel? _selectedServer;
    private ServerEntry? _activeServer;
    private bool _isRunning;
    private bool _isInitializing;
    private bool _isTunMode;
    private bool _isSystemProxyEnabled = true;
    private bool _isTunInternalUpdate;
    private bool _isApplyingSelection;
    private bool _isTunTransitioning;
    private bool _isStateDirty;
    private bool _isLatencyRefreshing;
    private string _routingModeText = Strings.RoutingRuleMode;
    private CancellationTokenSource? _latencyRefreshCts;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    public ServerItemViewModel? SelectedServer
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

    public string StatusText => IsRunning && _activeServer is not null ? string.Format(Strings.StatusRunning, _activeServer.Name) : Strings.StatusDisconnected;
    public string SelectedSummary => SelectedServer is null ? Strings.StatusNotSelected : string.Format(Strings.StatusCurrentSelected, SelectedServer.Name);
    public Visibility IsEmptyHintVisible => Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsLatencyRefreshEnabled => !_isLatencyRefreshing && Servers.Count > 0;
    public bool IsTunToggleEnabled => !_isApplyingSelection && !_isTunTransitioning;
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

    public MainWindow(AppServices services)
    {
        _services = services;
        _session = services.Session;
        _settings = _session.Settings;
        _runtimeState = _session.RuntimeState;
        _runtime = services.Runtime;
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
        foreach (var server in _session.Catalog.Servers)
        {
            Servers.Add(new ServerItemViewModel(server));
        }

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
        OnPropertyChanged(nameof(RouteSettingsSummary));
        OnPropertyChanged(nameof(TunHintText));

        if (SelectedServer is not null)
        {
            try
            {
                await EnsureSelectedServerAppliedAsync(forceRestart: false);
            }
            finally
            {
                SyncTunUiWithSettings();
            }
        }

        ScheduleLatencyRefresh();
    }

    private ServerItemViewModel? ResolveInitialSelection()
    {
        var selected = _session.Catalog.ResolveSelection(_runtimeState.LastSelectedServerId);
        return Servers.FirstOrDefault(x => ReferenceEquals(x.Server, selected));
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
                    return (server, await _serverLatencyService.ProbeAsync(server.Server, cancellationToken));
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
                server.ApplyLatencyResult(result);
            }
        }
        finally
        {
            _isLatencyRefreshing = false;
            OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        }
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
            await ConnectServerAsync(SelectedServer.Server);
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
        if (_isTunTransitioning)
        {
            return;
        }

        _isTunTransitioning = true;
        OnPropertyChanged(nameof(IsTunToggleEnabled));
        try
        {
            if (wantEnable && !await EnsureTunCanStartAsync())
            {
                _isTunInternalUpdate = true;
                IsTunMode = _settings.IsTunMode;
                _isTunInternalUpdate = false;
                return;
            }

            _settings.IsTunMode = wantEnable;
            await SaveSettingsSafelyAsync();

            if (SelectedServer is not null)
            {
                await EnsureSelectedServerAppliedAsync(forceRestart: true);
            }
        }
        finally
        {
            _isTunTransitioning = false;
            OnPropertyChanged(nameof(IsTunToggleEnabled));
        }
    }

    private async Task ConnectServerAsync(ServerEntry server)
    {
        if (IsTunMode && !await EnsureTunCanStartAsync())
        {
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

    private async Task<bool> EnsureTunCanStartAsync()
    {
        if (!IsTunMode || await _runtime.IsTunHelperAvailableAsync())
        {
            return true;
        }

        var confirmed = await ConfirmTunAuthorizationAsync();
        if (!confirmed)
        {
            return false;
        }

        if (await _runtime.EnsureTunHelperAvailableAsync())
        {
            return true;
        }

        await ShowTunErrorAsync(
            string.IsNullOrWhiteSpace(_runtime.TunHelperLastError)
                ? Strings.ErrCannotStartTunHelper
                : _runtime.TunHelperLastError);
        return false;
    }

    private async Task<bool> ConfirmTunAuthorizationAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Strings.ErrTunModeError,
            Content = Strings.MsgTunNeedAdmin,
            PrimaryButtonText = Strings.ButtonAuthorizeTun,
            CloseButtonText = Strings.ButtonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SyncTunUiWithSettings()
    {
        _isTunInternalUpdate = true;
        IsTunMode = _settings.IsTunMode;
        _isTunInternalUpdate = false;
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
        var window = new MoreWindow(this, _services, PrepareForCoreUpdateAsync);
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

        var imported = await _session.Catalog.ImportAsync(text);
        var items = imported.Select(server => new ServerItemViewModel(server)).ToList();
        foreach (var item in items) Servers.Add(item);

        if (items.Count == 0)
        {
            await ShowMessageAsync(Strings.MsgImportDone, Strings.MsgNoNewNodes);
            return;
        }

        if (SelectedServer is null)
        {
            SelectedServer = items[0];
        }

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

        await _session.Catalog.AddAsync(server);
        var item = new ServerItemViewModel(server);
        Servers.Add(item);
        SelectedServer = item;
        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        ScheduleLatencyRefresh();
    }


    private async void EditServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerItemViewModel server)
        {
            return;
        }

        var window = new ServerEditorWindow(this, server.Server, Strings.TitleEditServer, Strings.ButtonSave);
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

        if (!await _session.Catalog.ReplaceAsync(server.Server, replacement)) return;
        var replacementItem = new ServerItemViewModel(replacement);
        Servers[index] = replacementItem;

        if (ReferenceEquals(SelectedServer, server) || ReferenceEquals(_activeServer, server.Server))
        {
            SelectedServer = replacementItem;
            await EnsureSelectedServerAppliedAsync(forceRestart: true);
        }

        ScheduleLatencyRefresh();
    }

    private async void DeleteServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerItemViewModel server)
        {
            return;
        }

        if (!await ConfirmAsync(Strings.TitleDeleteServer, string.Format(Strings.MsgConfirmDelete, server.Name)))
        {
            return;
        }

        var wasActive = ReferenceEquals(_activeServer, server.Server);
        if (wasActive)
        {
            await ShowMessageAsync(Strings.ErrCannotDelete, Strings.ErrCannotDeleteActive);
            return;
        }

        var index = Servers.IndexOf(server);
        var wasSelected = ReferenceEquals(SelectedServer, server);
        if (!await _session.Catalog.RemoveAsync(server.Server)) return;
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

        OnPropertyChanged(nameof(IsEmptyHintVisible));
        OnPropertyChanged(nameof(IsLatencyRefreshEnabled));
        ScheduleLatencyRefresh();
    }

    private async void ShareServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ServerItemViewModel server)
        {
            return;
        }

        var link = NodeLinkSerializer.ToLink(server.Server);
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
        _activeServer = server;
        foreach (var entry in Servers)
        {
            entry.IsActive = ReferenceEquals(entry.Server, server);
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
            await _session.SaveSettingsAsync();
        }
        catch
        {
        }
    }

    private async Task SaveRuntimeStateSafelyAsync()
    {
        try
        {
            await _session.SaveRuntimeStateAsync();
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























