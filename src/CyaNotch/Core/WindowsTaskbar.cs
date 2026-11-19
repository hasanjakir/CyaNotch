using System;
using System.Runtime.InteropServices;

namespace CyaNotch.Core;

/// <summary>
/// Hides and restores Windows' own taskbar, and registers CyaNotch's bar as an "app bar" so
/// maximized windows stop above it. Every change is undone by <see cref="RestoreWindows"/>,
/// which also runs on quit and after a crash (see App).
/// </summary>
internal static class WindowsTaskbar
{
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    private const uint ABE_BOTTOM = 3;
    private const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    private static IntPtr Tray => FindWindow("Shell_TrayWnd", null);

    /// <summary>Hides Windows' taskbar (and its copies on other screens) and lets windows use its space.</summary>
    public static void HideWindows()
    {
        var tray = Tray;
        if (tray == IntPtr.Zero) return;
        if (!Settings.Current.TaskbarHiddenByUs)
        {
            // Remember whether the user had auto-hide on, so we put it back exactly.
            var d = Data(tray);
            Settings.Current.TaskbarWasAutoHide = ((int)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
            Settings.Current.TaskbarHiddenByUs = true;
            Settings.SaveQuietly();
        }
        SetAutoHide(tray, true);
        ShowWindow(tray, 0 /* SW_HIDE */);
        for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); w != IntPtr.Zero;
             w = FindWindowEx(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null))
            ShowWindow(w, 0);
    }

    /// <summary>Brings Windows' taskbar back as it was. Safe to call any time.</summary>
    public static void RestoreWindows()
    {
        if (!Settings.Current.TaskbarHiddenByUs) return;
        var tray = Tray;
        if (tray != IntPtr.Zero)
        {
            SetAutoHide(tray, Settings.Current.TaskbarWasAutoHide);
            ShowWindow(tray, 5 /* SW_SHOW */);
            for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); w != IntPtr.Zero;
                 w = FindWindowEx(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null))
                ShowWindow(w, 5);
        }
        Settings.Current.TaskbarHiddenByUs = false;
        Settings.SaveQuietly();
    }

    /// <summary>Briefly shows Windows' taskbar so its tray icons can be used, then hides it again later.</summary>
    public static void PeekWindows(bool show)
    {
        var tray = Tray;
        if (tray != IntPtr.Zero) ShowWindow(tray, show ? 5 : 0);
    }

    private static void SetAutoHide(IntPtr tray, bool on)
    {
        var d = Data(tray);
        d.lParam = (IntPtr)(on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }

    private static APPBARDATA Data(IntPtr hwnd) => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    // ---- CyaNotch's bar as an app bar ----

    public static void Register(IntPtr hwnd)
    {
        var d = Data(hwnd);
        SHAppBarMessage(ABM_NEW, ref d);
    }

    /// <summary>Reserves <paramref name="heightPx"/> at the bottom of the screen and returns the rectangle Windows gave.</summary>
    public static Native.RECT Reserve(IntPtr hwnd, Native.RECT screen, int heightPx)
    {
        var d = Data(hwnd);
        d.uEdge = ABE_BOTTOM;
        d.rc = new Native.RECT { Left = screen.Left, Right = screen.Right, Top = screen.Bottom - heightPx, Bottom = screen.Bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.Top = d.rc.Bottom - heightPx;
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    public static void Unregister(IntPtr hwnd)
    {
        var d = Data(hwnd);
        SHAppBarMessage(ABM_REMOVE, ref d);
    }
}
