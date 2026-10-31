using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InkFlow.Services;

/// <summary>Win32 window dress-up: dark frames, rounded corners, no-activate overlays.</summary>
public static class WindowEffects
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    public static IntPtr Hwnd(Window window) => new WindowInteropHelper(window).Handle;

    /// <summary>Hides the overlay from Alt+Tab and stops it from stealing focus from the editor.</summary>
    public static void MakeOverlay(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if (enabled)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~(long)WS_EX_TRANSPARENT;
        }

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetDarkFrame(Window window, bool dark)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
        {
            // Older Windows 10 builds use attribute 19.
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
    }

    public static void EnableRoundedCorners(Window window)
    {
        var hwnd = Hwnd(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        var hwnd = Hwnd(window);
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
