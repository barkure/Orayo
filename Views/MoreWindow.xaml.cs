using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Orayo.Helpers;
using Orayo.Services;

namespace Orayo.Views;

public sealed partial class MoreWindow : Window
{
    private const int GWL_HWNDPARENT = -8;
    private const int DefaultWidth = 900;
    private const int DefaultHeight = 700;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly Func<Task> _prepareCoreUpdateAsync;
    private readonly Func<bool> _shouldRestartConnection;
    private readonly Func<Task> _restartSelectedServerAsync;

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

        AppWindow.Title = "更多";
        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        SetWindowOwner(owner);
        AppWindow.SetPresenter(presenter);
        WindowMinSizeHelper.Apply(this, DefaultWidth, DefaultHeight);

        Closed += OnClosed;
        _ = RefreshVersionAsync();
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
        CloseButton.IsEnabled = !isBusy;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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



