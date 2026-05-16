using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
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
    private const int DefaultWidth = 900;
    private const int DefaultHeight = 810;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly Func<Task> _prepareCoreUpdateAsync;
    private readonly Func<bool> _shouldRestartConnection;
    private readonly Func<Task> _restartSelectedServerAsync;
    private readonly AppStore _store = new();
    private AppSettings _settings = new();
    private bool _isInitializing;

    public MoreWindow(
        Window owner,
        Func<Task> prepareCoreUpdateAsync,
        Func<bool> shouldRestartConnection,
        Func<Task> restartSelectedServerAsync)
    {
        _owner = owner;
        _prepareCoreUpdateAsync = prepareCoreUpdateAsync;
        _shouldRestartConnection = shouldRestartConnection;
        _restartSelectedServerAsync = restartSelectedServerAsync;
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
        _ = LoadSettingsAsync();
        _ = RefreshVersionAsync();
    }

    private void RefreshAppVersion()
    {
        var manager = CreateUpdateManager();
        var version = manager.CurrentVersion?.ToString() ?? ThisAssemblyVersion();
        var mode = manager.IsPortable ? "便携版" : manager.IsInstalled ? "安装版" : "开发版";
        AppVersionTextBlock.Text = $"当前版本：{version}（{mode}）";
    }

    private async Task LoadSettingsAsync()
    {
        _isInitializing = true;
        _settings = await _store.LoadSettingsAsync();
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
        var shouldRestart = _shouldRestartConnection();
        await RunActionAsync("正在更新 Xray-core", async () =>
        {
            await _prepareCoreUpdateAsync();
            await CoreUpdateService.UpdateXrayCoreAsync();
            await RefreshVersionAsync();
            if (shouldRestart)
            {
                await _restartSelectedServerAsync();
            }
        }, "Xray-core 已更新");
    }

    private async void UpdateGeofilesButton_Click(object sender, RoutedEventArgs e)
    {
        var shouldRestart = _shouldRestartConnection();
        await RunActionAsync("正在更新 Geo 数据文件", async () =>
        {
            await CoreUpdateService.UpdateGeofilesAsync();
            if (shouldRestart)
            {
                await _restartSelectedServerAsync();
            }
        }, "Geo 数据文件已更新");
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
                StatusTextBlock.Text = "当前运行方式不支持自动更新，请下载安装包或便携版新版本后替换。";
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                StatusTextBlock.Text = "当前已是最新版本";
                return;
            }

            var targetVersion = update.TargetFullRelease.Version.ToString();
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
                app.PrepareForRestart();
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
        CheckAppUpdateButton.IsEnabled = !isBusy;
    }

    private async void AutoStartToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.IsAutoStartEnabled = AutoStartToggleButton.IsChecked == true;
        UpdateAutoStartButtonText();
        ApplyAutoStartSetting(_settings.IsAutoStartEnabled);
        await _store.SaveSettingsAsync(_settings);
    }

    private void UpdateAutoStartButtonText()
    {
        AutoStartToggleButton.Content = _settings.IsAutoStartEnabled ? "开启" : "关闭";
    }

    private static void ApplyAutoStartSetting(bool enabled)
    {
        const string runKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue("Orayo", $"\"{exePath}\"");
            }
        }
        else
        {
            key.DeleteValue("Orayo", throwOnMissingValue: false);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static UpdateManager CreateUpdateManager()
    {
        var source = new GithubSource(UpdateRepositoryUrl, accessToken: null, prerelease: false);
        return new UpdateManager(source);
    }

    private static string ThisAssemblyVersion()
    {
        return typeof(App).Assembly.GetName().Version?.ToString() ?? "未知";
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



