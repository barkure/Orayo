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

        const string title = "更多";
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
        _ = RefreshVersionAsync();
    }

    private void RefreshAppVersion()
    {
        var manager = CreateUpdateManager();
        var version = FormatDisplayVersion(manager.CurrentVersion?.ToString() ?? ThisAssemblyVersion());
        var mode = manager.IsPortable ? "Portable" : manager.IsInstalled ? "Installer" : "Development";
        AppVersionTextBlock.Text = $"当前版本：{version} ({mode})";
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
        await RunActionAsync("正在下载 Xray-core", async () =>
        {
            using var update = await CoreUpdateService.StageXrayCoreUpdateAsync();
            StatusTextBlock.Text = "下载完成，正在保存待应用更新";
            CoreUpdateService.StagePendingXrayCoreUpdate(update);
            await RefreshVersionAsync();
        }, "Xray-core 已下载，将在下次启动应用时生效");
    }

    private async void UpdateGeofilesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync("正在更新 Geo 数据文件", async () =>
        {
            using var update = await CoreUpdateService.StageGeofilesUpdateAsync();
            StatusTextBlock.Text = "下载完成，正在替换 Geo 数据文件";
            CoreUpdateService.ApplyGeofilesUpdate(update);
        }, "Geo 数据文件已更新，新的规则将在下次启动或手动重连后生效");
    }

    private async void CheckAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusTextBlock.Text = "正在检查应用更新";
        try
        {
            var manager = CreateUpdateManager();
            if (!manager.IsInstalled)
            {
                StatusTextBlock.Text = "当前运行方式不支持自动更新，请下载安装包或便携版新版本后替换";
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                StatusTextBlock.Text = "当前已是最新版本";
                return;
            }

            var targetVersion = FormatDisplayVersion(update.TargetFullRelease.Version.ToString());
            var confirmed = await ConfirmAsync(
                "发现新版本",
                $"发现 Orayo {targetVersion}，是否现在下载并重启完成更新？");
            if (!confirmed)
            {
                StatusTextBlock.Text = "已取消应用更新";
                return;
            }

            await manager.DownloadUpdatesAsync(
                update,
                progress => DispatcherQueue.TryEnqueue(() =>
                {
                    StatusTextBlock.Text = $"正在下载应用更新：{progress}%";
                }),
                CancellationToken.None);

            StatusTextBlock.Text = "下载完成，正在重启应用";
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
                StatusTextBlock.Text = $"找不到网络回环管理器：{utilityPath}";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = utilityPath,
                UseShellExecute = true
            });
            StatusTextBlock.Text = "已打开网络回环管理器";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"打开网络回环管理器失败：{ex.Message}";
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
            StatusTextBlock.Text = "写入开机自启失败";
            return;
        }

        await _store.SaveSettingsAsync(_settings);
    }

    private void UpdateAutoStartButtonText()
    {
        AutoStartToggleButton.Content = _settings.IsAutoStartEnabled ? "已开启" : "开启";
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
            return "未知";
        }

        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}-r{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string FormatDisplayVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "未知", StringComparison.OrdinalIgnoreCase))
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
            PrimaryButtonText = "更新并重启",
            CloseButtonText = "取消",
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
}



