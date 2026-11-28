using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Desktops;

/// <summary>
/// New app windows open centred on the monitor they appear on (its work area, so the taskbar is
/// respected). Done on EVENT_OBJECT_SHOW, normally before the first frame, so there's no visible jump.
/// Only the first show of real app windows: top-level, with a title bar, not maximized/minimized,
/// not owned (dialogs stay with their parent), not tool/popup windows, not cloaked. Windows that
/// were already open are left alone. Admin windows can only be moved while aestoys runs as admin.
/// </summary>
internal sealed class WindowPlacement : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Win32.WinEventProc _proc;   // keep the delegate alive
    private readonly HashSet<IntPtr> _seen = new();
    private IntPtr _hook;

    /// <summary>
    /// Gets first say over a new window (FancyZones puts apps back in their last zone). Returns true
    /// when it placed the window, which then isn't centred. Set before <see cref="ApplySettings"/>.
    /// </summary>
    internal static Func<IntPtr, bool>? NewWindowHandler;
    internal static Func<bool>? NewWindowHandlerActive;

    private bool Wanted => _settings.Placement.CenterNewWindows || NewWindowHandlerActive?.Invoke() == true;

    public WindowPlacement(AppSettings settings)
    {
        _settings = settings;
        _proc = OnEvent;
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (Wanted && _hook == IntPtr.Zero)
        {
            _seen.Clear();
            Native.EnumWindows((h, _) => { _seen.Add(h); return true; }, IntPtr.Zero); // existing windows aren't "new"
            // Out-of-context, all processes, skipping our own; delivered through this thread's message loop.
            _hook = Win32.SetWinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
        }
        else if (!Wanted) Unhook();
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || _hook == IntPtr.Zero) return; // OBJID_WINDOW only
        if (evt == Win32.EVENT_OBJECT_DESTROY) { _seen.Remove(hwnd); return; }
        if (!_seen.Add(hwnd)) return;                                   // only the first show
        if (Win32.GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;  // top-level only
        // A window that appears while a mouse button is held is a drag (e.g. a Chrome tab torn off
        // into its own window). Moving it would break the drag.
        if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0) return;
        try
        {
            if (!IsNewAppWindow(hwnd)) return;
            if (NewWindowHandler?.Invoke(hwnd) == true) return;
            if (_settings.Placement.CenterNewWindows) Center(hwnd);
        }
        catch (Exception e) { App.Log("center: " + e.Message); }
    }

    private static bool IsNewAppWindow(IntPtr hwnd)
    {
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((style & 0x40000000) != 0) return false;                    // WS_CHILD
        if ((style & 0x00C00000) != 0x00C00000) return false;           // needs a title bar (WS_CAPTION)
        if ((ex & 0x08000080) != 0) return false;                       // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        if (Win32.IsZoomed(hwnd) || Native.IsIconic(hwnd)) return false;
        if (Win32.ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow") return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return true;
    }

    /// <summary>Alt+Space: centre the active window on its monitor.</summary>
    public static void CenterActive()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        if (Win32.ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return;
        if (Win32.IsZoomed(hwnd) || Native.IsIconic(hwnd)) return;   // leave maximized/minimized windows alone
        Center(hwnd);
    }

    /// <summary>Centres the visible frame in the monitor's work area. Never resizes or activates.</summary>
    public static void Center(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var wr)) return;
        var fr = wr;   // visible bounds (without the invisible resize border)
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT f, Marshal.SizeOf<Native.RECT>()) == 0) fr = f;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref mi);
        var work = mi.rcWork;
        if (fr.Width > work.Width || fr.Height > work.Height) return;   // bigger than the screen: don't touch
        int nx = work.Left + (work.Width - fr.Width) / 2 - (fr.Left - wr.Left);
        int ny = work.Top + (work.Height - fr.Height) / 2 - (fr.Top - wr.Top);
        if (nx != wr.Left || ny != wr.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void Unhook()
    {
        if (_hook != IntPtr.Zero) { Win32.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }

    public void Dispose() => Unhook();
}
