using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Tools;

/// <summary>
/// Grab And Move (PowerToys, MIT): hold Win (or Alt) and drag anywhere in a window with the left
/// button to move it, or with the right button to resize it from the nearest of its 8 handles.
/// A press only becomes a drag after the system drag distance; if the button comes up first, the
/// click is replayed so ordinary Win+clicks still work. Maximised windows are restored on the
/// first move, keeping the grab point in proportion. Win is masked afterwards so Start doesn't open.
/// All work happens on the mouse-hook thread with asynchronous window moves, so a hung app can't
/// stall the mouse.
/// </summary>
internal sealed class GrabAndMove
{
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public int length, flags, showCmd; public Native.POINT min, max; public Native.RECT normal; }

    private const uint SWP_ASYNC = 0x4000, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOSIZE = 0x1;
    private const long WS_THICKFRAME = 0x40000;

    private enum Mode { None, PendingMove, PendingResize, Moving, Resizing }

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private volatile bool _winDown, _altDown, _usedModifier;
    private Mode _mode;
    private IntPtr _target;
    private Native.POINT _press;
    private Native.RECT _startRect;
    private int _handleX, _handleY;          // -1 left/top edge, 0 none, 1 right/bottom edge
    private bool _wasMaximized;

    private GrabAndMoveSettings S => _settings.Tools.GrabAndMove;

    public GrabAndMove(AppSettings settings, KeyboardHook keyboard, MouseHook mouse)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        keyboard.Add(OnKey);
        mouse.Add(OnMouse);
    }

    private bool ModifierDown => S.Modifier == GrabModifier.Win ? _winDown : _altDown;

    // ------------------------------------------------------------------ keyboard

    private bool OnKey(int vk, bool down, bool alt, bool injected)
    {
        if (injected) return false;
        bool isWin = vk is 0x5B or 0x5C, isAlt = vk is 0x12 or 0xA4 or 0xA5;
        if (!isWin && !isAlt) return false;
        if (isWin) _winDown = down; else _altDown = down;
        if (down) return false;
        // Released after a grab: an extra key press stops Start (Win) or the menu bar (Alt) opening.
        bool ours = (isWin && S.Modifier == GrabModifier.Win) || (isAlt && S.Modifier == GrabModifier.Alt);
        if (ours && _usedModifier)
        {
            _usedModifier = false;
            Native.SendKeys((Native.VK_DUMMY, false), (Native.VK_DUMMY, true));
        }
        if (ours && _mode != Mode.None) _mode = Mode.None;   // let go of the key: drop the drag
        return false;
    }

    // ------------------------------------------------------------------ mouse

    private bool OnMouse(int msg, Native.POINT pt, int wheel, bool injected)
    {
        if (injected) return false;
        switch (msg)
        {
            case MouseHook.WM_LBUTTONDOWN or MouseHook.WM_RBUTTONDOWN:
                if (_mode != Mode.None || !S.Enabled || !ModifierDown) return false;
                bool resize = msg == MouseHook.WM_RBUTTONDOWN;
                if (resize && !S.ResizeWithRightButton) return false;
                if (!Grab(pt, resize)) return false;
                _usedModifier = true;
                return true;

            case MouseHook.WM_MOUSEMOVE:
                if (_mode == Mode.None) return false;
                Move(pt);
                return false;

            case MouseHook.WM_LBUTTONUP or MouseHook.WM_RBUTTONUP:
                if (_mode == Mode.None) return false;
                bool rightUp = msg == MouseHook.WM_RBUTTONUP;
                bool rightMode = _mode is Mode.PendingResize or Mode.Resizing;
                if (rightUp != rightMode) return false;
                var was = _mode;
                _mode = Mode.None;
                if (was is Mode.PendingMove or Mode.PendingResize)
                    MouseHook.SendClick(rightUp ? 2 : 1);   // no drag happened: give the app its click
                return true;
        }
        return false;
    }

    private bool Grab(Native.POINT pt, bool resize)
    {
        if (App.AltTab?.IsActive == true) return false;
        IntPtr hwnd = Win32.GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
        if (hwnd == IntPtr.Zero) return false;
        if (Win32.ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (S.SkipInGameMode && AlwaysOnTop.GameMode()) return false;
        if (AlwaysOnTop.IsExcluded(hwnd, S.ExcludedApps)) return false;
        long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
        if (resize && (style & WS_THICKFRAME) == 0) return false;
        if (resize && Win32.IsZoomed(hwnd)) return false;
        if (!Native.GetWindowRect(hwnd, out _startRect)) return false;

        _target = hwnd;
        _press = pt;
        _wasMaximized = Win32.IsZoomed(hwnd);
        _mode = resize ? Mode.PendingResize : Mode.PendingMove;
        if (resize) PickHandle(pt);
        return true;
    }

    /// <summary>The nearest of the 4 corners and 4 edge midpoints.</summary>
    private void PickHandle(Native.POINT pt)
    {
        var r = _startRect;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        (int x, int y, int hx, int hy)[] handles =
        {
            (r.Left, r.Top, -1, -1), (cx, r.Top, 0, -1), (r.Right, r.Top, 1, -1),
            (r.Left, cy, -1, 0), (r.Right, cy, 1, 0),
            (r.Left, r.Bottom, -1, 1), (cx, r.Bottom, 0, 1), (r.Right, r.Bottom, 1, 1),
        };
        long best = long.MaxValue;
        foreach (var h in handles)
        {
            long d = (long)(h.x - pt.X) * (h.x - pt.X) + (long)(h.y - pt.Y) * (h.y - pt.Y);
            if (d < best) { best = d; _handleX = h.hx; _handleY = h.hy; }
        }
    }

    private void Move(Native.POINT pt)
    {
        if (!Native.IsWindow(_target)) { _mode = Mode.None; return; }
        int dx = pt.X - _press.X, dy = pt.Y - _press.Y;
        if (_mode is Mode.PendingMove or Mode.PendingResize)
        {
            if (Math.Abs(dx) < Win32.GetSystemMetrics(68 /* SM_CXDRAG */) && Math.Abs(dy) < Win32.GetSystemMetrics(69 /* SM_CYDRAG */)) return;
            _mode = _mode == Mode.PendingMove ? Mode.Moving : Mode.Resizing;
            IntPtr target = _target;
            _ui.BeginInvoke(() => Native.SetForegroundWindow(target));
            if (_mode == Mode.Moving && _wasMaximized) RestoreForDrag();
        }

        var r = _startRect;
        if (_mode == Mode.Moving)
        {
            Native.SetWindowPos(_target, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0, SWP_ASYNC | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
            return;
        }

        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_handleX < 0) left += dx; else if (_handleX > 0) right += dx;
        if (_handleY < 0) top += dy; else if (_handleY > 0) bottom += dy;
        int minW = Win32.GetSystemMetrics(34 /* SM_CXMINTRACK */), minH = Win32.GetSystemMetrics(35 /* SM_CYMINTRACK */);
        if (right - left < minW) { if (_handleX < 0) left = right - minW; else right = left + minW; }
        if (bottom - top < minH) { if (_handleY < 0) top = bottom - minH; else bottom = top + minH; }
        Native.SetWindowPos(_target, IntPtr.Zero, left, top, right - left, bottom - top, SWP_ASYNC | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Un-maximise, keeping the grab point at the same fraction across the restored window.</summary>
    private void RestoreForDrag()
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(_target, ref wp)) return;
        var max = _startRect;
        int nw = wp.normal.Width, nh = wp.normal.Height;
        double fx = max.Width > 0 ? (_press.X - max.Left) / (double)max.Width : 0.5;
        int left = _press.X - (int)(nw * fx);
        int top = _press.Y - Math.Min(nh / 2, Math.Max(8, _press.Y - max.Top));
        ShowWindowAsync(_target, 1 /* SW_SHOWNORMAL */);
        _startRect = new Native.RECT { Left = left, Top = top, Right = left + nw, Bottom = top + nh };
        Native.SetWindowPos(_target, IntPtr.Zero, left, top, nw, nh, SWP_ASYNC | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
