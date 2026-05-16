using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Orayo.Helpers;
using Orayo.Models;

namespace Orayo.Views;

public sealed partial class RouteRuleEditorWindow : Window
{
    private const int GWL_HWNDPARENT = -8;
    private const int DefaultWidth = 800;
    private const int DefaultHeight = 700;

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly Window _owner;
    private readonly TaskCompletionSource<CustomRoutingRule?> _completion = new();
    private bool _completed;

    public RouteRuleEditorWindow(Window owner, CustomRoutingRule rule, bool isEdit)
    {
        _owner = owner;
        InitializeComponent();

        AppWindow.Title = isEdit ? "编辑路由规则" : "新增路由规则";
        AppWindow.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = true;
        SetWindowOwner(owner);
        AppWindow.SetPresenter(presenter);
        WindowMinSizeHelper.Apply(this, DefaultWidth, DefaultHeight);

        TypeComboBox.SelectedItem = string.IsNullOrWhiteSpace(rule.Type) ? "domain" : rule.Type;
        OutboundComboBox.SelectedItem = string.IsNullOrWhiteSpace(rule.OutboundTag) ? "proxy" : rule.OutboundTag;
        NameTextBox.Text = rule.Name;
        MatchTextBox.Text = rule.Match;

        Closed += OnClosed;
    }

    public Task<CustomRoutingRule?> ShowModalAsync()
    {
        AppWindow.Show();
        Activate();
        return _completion.Task;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var match = MatchTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(match))
        {
            ErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        Complete(new CustomRoutingRule
        {
            Name = NameTextBox.Text.Trim(),
            Type = (TypeComboBox.SelectedItem as string ?? "domain").Trim(),
            Match = match,
            OutboundTag = (OutboundComboBox.SelectedItem as string ?? "proxy").Trim(),
            IsEnabled = true,
        });
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(null);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Complete(null);
        _owner.Activate();
    }

    private void Complete(CustomRoutingRule? rule)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _completion.TrySetResult(rule);
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

