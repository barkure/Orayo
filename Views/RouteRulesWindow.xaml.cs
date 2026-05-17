using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Orayo.Helpers;
using Orayo.Services;

namespace Orayo.Views;

public sealed partial class RouteRulesWindow : Window
{
    private const int GWL_HWNDPARENT = -8;
    private const int DefaultWidth = 900;
    private const int DefaultHeight = 1000;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly TaskCompletionSource<string?> _completion = new();
    private readonly string _initialRoutingJson;
    private bool _completed;
    private bool _editorReady;
    private bool _isInitializingEditor;

    public RouteRulesWindow(Window owner, string? routingJson)
    {
        _owner = owner;
        _initialRoutingJson = RouteRulePresetService.EnsureRoutingBodyJson(routingJson);
        InitializeComponent();
        WindowThemeHelper.Apply(this);

        const string title = "路由设置";
        AppWindow.Title = title;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBarTextBlock.Text = title;
        SetTitleBar(TitleBarGrid);

        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = true;
        SetWindowOwner(owner);
        AppWindow.SetPresenter(presenter);
        WindowMinSizeHelper.Apply(this, DefaultWidth, DefaultHeight);

        Closed += OnClosed;
        Activated += RouteRulesWindow_Activated;
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += RouteRulesWindow_ThemeChanged;
        }
    }

    public Task<string?> ShowModalAsync()
    {
        AppWindow.Show();
        Activate();
        return _completion.Task;
    }

    private async void RouteRulesWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_editorReady)
        {
            await ApplyEditorThemeAsync();
            return;
        }

        if (_isInitializingEditor)
        {
            return;
        }

        try
        {
            _isInitializingEditor = true;
            await InitializeEditorAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Monaco 编辑器初始化失败：{ex.Message}");
        }
        finally
        {
            _isInitializingEditor = false;
        }
    }

    private async void RouteRulesWindow_ThemeChanged(FrameworkElement sender, object args)
    {
        if (_editorReady)
        {
            await ApplyEditorThemeAsync();
        }
    }

    private async Task InitializeEditorAsync()
    {
        ApplyEditorHostTheme();
        await EditorWebView.EnsureCoreWebView2Async();
        EditorWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        EditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        EditorWebView.NavigationCompleted += EditorWebView_NavigationCompleted;

        var editorFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "editor");
        EditorWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "appassets.orayo",
            editorFolder,
            CoreWebView2HostResourceAccessKind.Allow);
        EditorWebView.Source = new Uri("https://appassets.orayo/route-rules-monaco.html");
    }

    private async void EditorWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            if (!args.IsSuccess)
            {
                ShowError($"Monaco 页面加载失败：{args.WebErrorStatus}");
                return;
            }

            await WaitForEditorReadyAsync();
            _editorReady = true;
            ClearError();
            await ApplyEditorThemeAsync();
            await SetEditorContentAsync(_initialRoutingJson);
        }
        catch (Exception ex)
        {
            ShowError($"Monaco 编辑器初始化失败：{ex.Message}");
        }
    }

    private async void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorReady)
        {
            return;
        }

        try
        {
            ClearError();
            var current = await GetEditorContentAsync();
            var formatted = RouteRulePresetService.FormatRoutingBodyJson(current);
            await SetEditorContentAsync(formatted);
            await ExecuteScriptAsync("window.formatEditorContent();");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorReady)
        {
            return;
        }

        ClearError();
        await SetEditorContentAsync(RouteRulePresetService.CreateDefaultRoutingBodyJson());
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_editorReady)
        {
            return;
        }

        try
        {
            ClearError();
            var current = await GetEditorContentAsync();
            var formatted = RouteRulePresetService.FormatRoutingBodyJson(current);
            await SetEditorContentAsync(formatted);
            Complete(RouteRulePresetService.BuildRoutingJsonFromBody(formatted));
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(null);
        Close();
    }

    private async Task WaitForEditorReadyAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            var result = await ExecuteScriptAsync("window.isEditorReady ? window.isEditorReady() : false;");
            if (JsonSerializer.Deserialize<bool>(result))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("编辑器初始化超时。");
    }
    private async Task SetEditorContentAsync(string content)
    {
        var json = JsonSerializer.Serialize(content);
        await ExecuteScriptAsync($"window.setEditorContent({json});");
    }

    private async Task<string> GetEditorContentAsync()
    {
        var result = await ExecuteScriptAsync("window.getEditorContent();");
        if (string.IsNullOrWhiteSpace(result) || string.Equals(result, "null", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return JsonSerializer.Deserialize<string>(result) ?? string.Empty;
    }

    private async Task<string> ExecuteScriptAsync(string script)
    {
        var core = EditorWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 尚未初始化完成。");
        return await core.ExecuteScriptAsync(script);
    }

    private async Task ApplyEditorThemeAsync()
    {
        ApplyEditorHostTheme();
        var theme = WindowThemeHelper.IsDarkMode() ? "dark" : "light";
        await ExecuteScriptAsync($"window.setOrayoTheme && window.setOrayoTheme('{theme}');");
    }

    private void ApplyEditorHostTheme()
    {
        EditorWebView.DefaultBackgroundColor = WindowThemeHelper.IsDarkMode()
            ? Windows.UI.Color.FromArgb(255, 31, 31, 31)
            : Windows.UI.Color.FromArgb(255, 246, 246, 246);
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorTextBlock.Text = string.Empty;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        EditorWebView.NavigationCompleted -= EditorWebView_NavigationCompleted;
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged -= RouteRulesWindow_ThemeChanged;
        }

        Complete(null);
        _owner.Activate();
    }

    private void Complete(string? routingJson)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completion.TrySetResult(routingJson);
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










