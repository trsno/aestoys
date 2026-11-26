using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aestoys.Interop;

namespace Aestoys.Features.AltTab;

public sealed class SwitchItem
{
    public required IntPtr Hwnd { get; init; }
    public required string Title { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsMinimized { get; init; }
}

internal static class WindowEnumerator
{
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Returns Alt+Tab-eligible windows on <paramref name="monitor"/>, in z-order (most recent first).</summary>
    public static List<SwitchItem> GetWindows(IntPtr monitor, bool includeMinimized)
    {
        var result = new List<SwitchItem>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd)) return true;
            bool minimized = Native.IsIconic(hwnd);
            if (minimized && !includeMinimized) return true;
            if (monitor != IntPtr.Zero && GetWindowMonitor(hwnd, minimized) != monitor) return true;

            result.Add(new SwitchItem
            {
                Hwnd = hwnd,
                Title = Native.GetWindowTitle(hwnd),
                Icon = GetIcon(hwnd),
                IsMinimized = minimized,
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return false;

        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;

        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if ((style & Native.WS_DISABLED) != 0) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero && !appWindow) return false;

        // Cloaked windows: UWP apps that are suspended, windows on other virtual desktops, etc.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (Native.GetWindowTextLength(hwnd) == 0) return false;
        return true;
    }

    private static IntPtr GetWindowMonitor(IntPtr hwnd, bool minimized)
    {
        if (minimized)
        {
            // A minimized window sits at -32000,-32000; use where it will be restored to instead.
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(hwnd, ref wp))
            {
                var rc = wp.rcNormalPosition;
                return Native.MonitorFromRect(ref rc, Native.MONITOR_DEFAULTTONEAREST);
            }
        }
        return Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
    }

    private static readonly Dictionary<string, ImageSource?> ExeIconCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? GetIcon(IntPtr hwnd)
    {
        IntPtr hIcon = IntPtr.Zero;
        foreach (int type in new[] { Native.ICON_BIG, Native.ICON_SMALL2, Native.ICON_SMALL })
        {
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)type, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 40, out hIcon);
            if (hIcon != IntPtr.Zero) break;
        }
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);

        if (hIcon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { }
        }

        string? path = Native.GetProcessPath(hwnd);
        if (path is null) return null;
        if (ExeIconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
        }
        catch (Exception e) { Debug.WriteLine(e); }
        ExeIconCache[path] = img;
        return img;
    }
}
