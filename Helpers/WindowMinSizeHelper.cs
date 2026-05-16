using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Orayo.Helpers;

public static class WindowMinSizeHelper
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;

    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed class State
    {
        public required WindowProc Proc { get; init; }
        public required IntPtr PreviousProc { get; init; }
        public int MinWidth { get; set; }
        public int MinHeight { get; set; }
    }

    private static readonly Dictionary<IntPtr, State> States = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void Apply(Window window, int minWidth, int minHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (States.TryGetValue(hwnd, out var existing))
        {
            existing.MinWidth = minWidth;
            existing.MinHeight = minHeight;
            return;
        }

        var state = new State
        {
            MinWidth = minWidth,
            MinHeight = minHeight,
            Proc = WndProc,
            PreviousProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate((WindowProc)WndProc))
        };

        States[hwnd] = state;
        window.Closed += (_, _) => Restore(hwnd);
    }

    private static void Restore(IntPtr hwnd)
    {
        if (!States.Remove(hwnd, out var state))
        {
            return;
        }

        SetWindowLongPtr(hwnd, GWLP_WNDPROC, state.PreviousProc);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (States.TryGetValue(hWnd, out var state) && msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = state.MinWidth;
            info.ptMinTrackSize.Y = state.MinHeight;
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return States.TryGetValue(hWnd, out var current)
            ? CallWindowProc(current.PreviousProc, hWnd, msg, wParam, lParam)
            : IntPtr.Zero;
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, newLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, newLong.ToInt32()));
    }
}

