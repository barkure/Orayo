using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Orayo;
using Orayo.Helpers;
using Orayo.Services;
using Orayo.Models;
using Velopack;
using Velopack.Sources;

namespace Orayo.Views;

public sealed partial class MoreWindow : Window
{
    private const int GWL_HWNDPARENT = -8;
    private const string UpdateRepositoryUrl = "https://github.com/barkure/Orayo";
    private const string LoopbackUtilityRelativePath = "Assets\\tools\\enableloopbackutility.exe";
    private const int DefaultWidth = 900;
    private const int DefaultHeight = 980;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly Func<Task> _prepareCoreUpdateAsync;
    private readonly AppStore _store = new();
    private readonly AppSettings _settings;
    private bool _isInitializing;

    public MoreWindow(
        Window owner,
        AppSettings settings,
        Func<Task> prepareCoreUpdateAsync)
    {
        _owner = owner;
        _prepareCoreUpdateAsync = prepareCoreUpdateAsync;
        _settings = settings;
        InitializeComponent();
        WindowThemeHelper.Apply(this);

        var title = Strings.TitleMore;
        AppWindow.Title = title;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBarTextBlock.Text = title;
        SetTitleBar(TitleBarGrid);

        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        SetWindowOwner(owner);
        AppWindow.SetPresenter(presenter);
        WindowMinSizeHelper.Apply(this, DefaultWidth, DefaultHeight);

        Closed += OnClosed;
        RefreshAppVersion();
        InitializeSettings();
        InitializeLanguageSelector();
        _ = RefreshVersionAsync();
    }

    private void RefreshAppVersion()
    {
        var manager = CreateUpdateManager();
        var version = FormatDisplayVersion(manager.CurrentVersion?.ToString() ?? ThisAssemblyVersion());
        var mode = manager.IsPortable ? "Portable" : manager.IsInstalled ? "Installer" : "Development";
        AppVersionTextBlock.Text = string.Format(Strings.AppVersionFormat, version, mode);
    }

    private void InitializeSettings()
    {
        _isInitializing = true;
        AutoStartToggleButton.IsChecked = _settings.IsAutoStartEnabled;
        UpdateAutoStartButtonText();
        _isInitializing = false;
    }

    private async Task RefreshVersionAsync()
    {
        var info = await CoreUpdateService.GetXrayVersionInfoAsync();
        VersionTextBlock.Text = info.Version;

        if (string.IsNullOrWhiteSpace(info.Commit) || string.IsNullOrWhiteSpace(info.ReleaseUrl))
        {
            CommitLinkButton.Visibility = Visibility.Collapsed;
            CommitLinkButton.Content = string.Empty;
            CommitLinkButton.NavigateUri = null;
            return;
        }

        CommitLinkButton.Content = info.Commit;
        CommitLinkButton.NavigateUri = new Uri(info.ReleaseUrl);
        CommitLinkButton.Visibility = Visibility.Visible;
    }

    private async void UpdateCoreButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(Strings.StatusDownloadingXray, async () =>
        {
            using var update = await CoreUpdateService.StageXrayCoreUpdateAsync();
            StatusTextBlock.Text = Strings.StatusXrayDownloaded;
            CoreUpdateService.StagePendingXrayCoreUpdate(update);
            await RefreshVersionAsync();
        }, Strings.StatusXrayApplied);
    }

    private async void UpdateGeofilesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(Strings.StatusUpdatingGeo, async () =>
        {
            using var update = await CoreUpdateService.StageGeofilesUpdateAsync();
            StatusTextBlock.Text = Strings.StatusGeoDownloaded;
            CoreUpdateService.ApplyGeofilesUpdate(update);
        }, Strings.StatusGeoApplied);
    }

    private async void CheckAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusTextBlock.Text = Strings.StatusCheckingUpdate;
        try
        {
            var manager = CreateUpdateManager();
            if (!manager.IsInstalled)
            {
                StatusTextBlock.Text = Strings.StatusUpdateNotSupported;
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                StatusTextBlock.Text = Strings.StatusAlreadyLatest;
                return;
            }

            var targetVersion = FormatDisplayVersion(update.TargetFullRelease.Version.ToString());
            var confirmed = await ConfirmAsync(
                Strings.TitleNewVersion,
                string.Format(Strings.MsgNewVersion, targetVersion));
            if (!confirmed)
            {
                StatusTextBlock.Text = Strings.StatusUpdateCancelled;
                return;
            }

            await manager.DownloadUpdatesAsync(
                update,
                progress => DispatcherQueue.TryEnqueue(() =>
                {
                    StatusTextBlock.Text = string.Format(Strings.StatusDownloadingUpdate, progress);
                }),
                CancellationToken.None);

            StatusTextBlock.Text = Strings.StatusUpdateDownloaded;
            await _prepareCoreUpdateAsync();
            if (Application.Current is App app)
            {
                await app.PrepareForRestartAsync();
            }

            manager.ApplyUpdatesAndRestart(update.TargetFullRelease, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunActionAsync(string pendingText, Func<Task> action, string doneText)
    {
        SetBusy(true);
        StatusTextBlock.Text = pendingText;
        try
        {
            await action();
            StatusTextBlock.Text = doneText;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        UpdateCoreButton.IsEnabled = !isBusy;
        UpdateGeofilesButton.IsEnabled = !isBusy;
        OpenLoopbackUtilityButton.IsEnabled = !isBusy;
        CheckAppUpdateButton.IsEnabled = !isBusy;
    }

    private void OpenLoopbackUtilityButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var utilityPath = Path.Combine(AppContext.BaseDirectory, LoopbackUtilityRelativePath);
            if (!File.Exists(utilityPath))
            {
                StatusTextBlock.Text = string.Format(Strings.ErrLoopbackNotFound, utilityPath);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = utilityPath,
                UseShellExecute = true
            });
            StatusTextBlock.Text = Strings.StatusLoopbackOpened;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = string.Format(Strings.ErrLoopbackFailed, ex.Message);
        }
    }

    private async void AutoStartToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var previousValue = _settings.IsAutoStartEnabled;
        _settings.IsAutoStartEnabled = AutoStartToggleButton.IsChecked == true;
        UpdateAutoStartButtonText();
        var applied = AutoStartService.Apply(_settings.IsAutoStartEnabled);
        if (!applied)
        {
            _settings.IsAutoStartEnabled = previousValue;
            AutoStartToggleButton.IsChecked = previousValue;
            UpdateAutoStartButtonText();
            StatusTextBlock.Text = Strings.ErrAutoStartFailed;
            return;
        }

        await _store.SaveSettingsAsync(_settings);
    }

    private void UpdateAutoStartButtonText()
    {
        AutoStartToggleButton.Content = _settings.IsAutoStartEnabled ? Strings.AutoStartEnabled : Strings.AutoStartDisabled;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private UpdateManager CreateUpdateManager()
    {
        var downloader = new OrayoUpdateFileDownloader(
            _settings.IsSystemProxyEnabled && !_settings.IsTunMode,
            _settings.LocalHttpPort);
        var source = new GithubSource(UpdateRepositoryUrl, string.Empty, false, downloader);
        return new UpdateManager(source);
    }

    private static string ThisAssemblyVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        if (version is null)
        {
            return Strings.Unknown;
        }

        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}-r{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string FormatDisplayVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, Strings.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return version;
        }

        var normalized = version.TrimStart('v');
        if (normalized.Contains("-r", StringComparison.OrdinalIgnoreCase))
        {
            return $"v{normalized}";
        }

        var parts = normalized.Split('.');
        if (parts.Length == 4 && int.TryParse(parts[3], out var releaseNumber))
        {
            normalized = $"{parts[0]}.{parts[1]}.{parts[2]}-r{releaseNumber}";
        }

        return $"v{normalized}";
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = Strings.ButtonUpdateAndRestart,
            CloseButtonText = Strings.ButtonCancel,
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _owner.Activate();
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

    private void InitializeLanguageSelector()
    {
        var currentLang = _settings.Language ?? "zh-Hans";
        foreach (var item in LanguageComboBox.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, currentLang, StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedItem = cbi;
                return;
            }
        }
        LanguageComboBox.SelectedIndex = 0;
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || LanguageComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var newLang = selected.Tag as string ?? "zh-Hans";
        if (string.Equals(_settings.Language, newLang, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var confirmed = await ConfirmLanguageChangeAsync();
        if (!confirmed)
        {
            // Revert selection
            _isInitializing = true;
            InitializeLanguageSelector();
            _isInitializing = false;
            return;
        }

        _settings.Language = newLang;
        await _store.SaveSettingsAsync(_settings);

        if (Application.Current is App app)
        {
            await app.PrepareForRestartAsync();
        }

        System.Windows.Forms.Application.Restart();
        Environment.Exit(0);
    }

    private async Task<bool> ConfirmLanguageChangeAsync()
    {
        var dialog = new ContentDialog
        {
            Title = Strings.TitleLanguageChange,
            Content = Strings.MsgConfirmLanguageChange,
            PrimaryButtonText = Strings.ButtonOK,
            CloseButtonText = Strings.ButtonCancel,
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}



