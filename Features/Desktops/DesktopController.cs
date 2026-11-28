using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Aestoys.Features.AltTab;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;
using Forms = System.Windows.Forms;

namespace Aestoys.Features.Desktops;

/// <summary>
/// Virtual desktop keys (ported from VirtualDesktopLoop.ahk):
/// <list type="bullet">
/// <item>Ctrl+Win+Left/Right wrap around: only on the first/last desktop, everything else is native Windows.</item>
/// <item>Ctrl+Win+Shift+Left/Right takes the active window along (wraps too). The window is pinned
/// during the switch so it never disappears, then unpinned on the new desktop and re-activated.</item>
/// <item>Ctrl+Win+Shift+D shows the active window on every desktop (toggle, blue border while pinned).</item>
/// <item>Alt+Space centres the active window.</item>
/// </list>
/// The keyboard hook only decides and hands off; the work runs on the UI thread.
/// </summary>
internal sealed class DesktopController : IDisposable
{
    private const int VK_D = 0x44, VK_SPACE = 0x20;
    private const uint PinBorder = 0x00D77800; // COLORREF 0x00BBGGRR = RGB(0,120,215), Windows blue

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<int> _swallowed = new(); // hook thread only
    private readonly HashSet<IntPtr> _bordered = new();
    private readonly ExplorerWatcher _watcher = new();
    private volatile bool _busy;
    private DesktopIndicator? _indicator;

    public DesktopController(AppSettings settings, Dispatcher dispatcher, KeyboardHook hook)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        VirtualDesktops.Init();
        var (count, index) = VirtualDesktops.State();
        App.Log($"virtual desktops: registry {count} desktops, current {index}; " +
                (VirtualDesktops.DllLoaded
                    ? $"VirtualDesktopAccessor.dll {VirtualDesktops.GetDesktopCount()}/{VirtualDesktops.GetCurrentDesktopNumber()} ({(VirtualDesktops.DllAgrees(count, index) ? "agrees" : "DISAGREES")}) from {VirtualDesktops.DllPath}"
                    : "VirtualDesktopAccessor.dll not available, using the key-press fallback"));
        hook.Add(OnKey);
        ApplySettings();
    }

    /// <summary>Starts or stops the parts that run on their own (the indicator).</summary>
    public void ApplySettings()
    {
        if (_settings.Desktops.Indicator && _indicator is null) _indicator = new DesktopIndicator();
        else if (!_settings.Desktops.Indicator && _indicator is not null) { _indicator.Dispose(); _indicator = null; }
    }

    public static event Action? ExplorerRestarted
    {
        add => ExplorerWatcher.Restarted += value;
        remove => ExplorerWatcher.Restarted -= value;
    }

    // ---------------------------------------------------------------- keyboard hook (hook thread)

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool OnKey(int vk, bool isDown, bool altDown, bool injected)
    {
        if (injected) return false;
        if (!isDown) return _swallowed.Remove(vk); // swallow the release of a key we took

        bool win = IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN);
        bool ctrl = IsDown(Native.VK_CONTROL);
        bool shift = IsDown(Native.VK_SHIFT);
        var d = _settings.Desktops;

        if (_swallowed.Contains(vk)) return true; // auto-repeat of a key we already acted on

        if (ctrl && win && !altDown && vk is Native.VK_LEFT or Native.VK_RIGHT)
        {
            int dir = vk == Native.VK_RIGHT ? 1 : -1;
            if (shift)
            {
                if (!d.MoveWindowKeys) return false;
                Take(vk);
                _dispatcher.BeginInvoke(() => MoveActiveWindow(dir));
                return true;
            }
            if (!d.WrapAround) return false;
            if (_busy) { Take(vk); return true; }          // swallow extra presses while a wrap runs
            if (!AtEdge(dir)) return false;                 // not on an edge desktop: native Windows
            Take(vk);
            _dispatcher.BeginInvoke(() => Wrap(dir));
            return true;
        }

        if (ctrl && win && shift && !altDown && vk == VK_D && d.PinKey)
        {
            Take(vk);
            _dispatcher.BeginInvoke(TogglePinActive);
            return true;
        }

        if (vk == VK_SPACE && altDown && !ctrl && !win && !shift && _settings.Placement.AltSpaceCenters)
        {
            Take(vk);
            _dispatcher.BeginInvoke(() => WindowPlacement.CenterActive());
            return true;
        }
        return false;
    }

    /// <summary>Swallows the key and taps an unassigned key so a lone Win/Alt release doesn't open Start or a menu bar.</summary>
    private void Take(int vk)
    {
        _swallowed.Add(vk);
        Native.SendKeys((Native.VK_DUMMY, false), (Native.VK_DUMMY, true));
    }

    private bool AtEdge(int dir)
    {
        var (count, idx) = VirtualDesktops.State();
        return count >= 2 && idx >= 0 && (dir > 0 ? idx == count - 1 : idx == 0);
    }

    // ---------------------------------------------------------------- wrap

    private async void Wrap(int dir)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (count, idx) = VirtualDesktops.State();
            if (count < 2 || idx < 0) return;
            int target = dir > 0 ? 0 : count - 1;
            if (!await DirectJump(count, idx, target))
                await CoveredSteps(target, dir);
        }
        catch (Exception e) { App.Log("wrap: " + e); }
        finally { _busy = false; }
    }

    /// <summary>Primary: jump straight there with the DLL, if it agrees with the registry.</summary>
    private async Task<bool> DirectJump(int count, int idx, int target)
    {
        if (!VirtualDesktops.DllAgrees(count, idx)) return false;
        try { VirtualDesktops.GoToDesktopNumber(target); }
        catch (Exception e) { App.Log("wrap: DLL error " + e.Message); return false; }
        if (await WaitForChange(idx, 400) != target) return false;
        _indicator?.Tick();
        await FixFocus();
        return true;
    }

    /// <summary>
    /// Backup: cover the screen with a still snapshot, step through the desktops with clean
    /// Ctrl+Win+Arrow presses (after the user lets go of the keys), then uncover.
    /// </summary>
    private async Task CoveredSteps(int target, int dir)
    {
        // Our keys must not overlap the user's: wait for Ctrl/Win to be released (max 1.5 s).
        for (int t = 0; t < 150 && (IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN) || IsDown(Native.VK_CONTROL)); t++)
            await Task.Delay(10);

        using var cover = SnapshotCover.Show();
        var (count, idx) = VirtualDesktops.State();
        ushort arrow = (ushort)(dir > 0 ? Native.VK_LEFT : Native.VK_RIGHT);
        for (int tries = 0; idx != target && idx >= 0 && tries < count * 3; tries++)
        {
            int before = idx;
            Native.SendKeys((Native.VK_CONTROL, false), (Native.VK_LWIN, false), (arrow, false), (arrow, true), (Native.VK_LWIN, true), (Native.VK_CONTROL, true));
            idx = await WaitForChange(before, 300);
            await Task.Delay(60);
        }
        _indicator?.Tick();                 // draw the final desktop before the cover lifts
        await Task.Delay(40);
    }

    private static async Task<int> WaitForChange(int before, int ms)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < ms)
        {
            await Task.Delay(10);
            int now = VirtualDesktops.State().index;
            if (now != before) return now;
        }
        return before;
    }

    /// <summary>After a programmatic switch focus can stay on a window from the old desktop; fix that.</summary>
    private static async Task FixFocus()
    {
        if (!VirtualDesktops.HasManager) return;
        await Task.Delay(30);
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && VirtualDesktops.IsOnCurrentDesktop(fg)) return;
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;
            if (!VirtualDesktops.IsOnCurrentDesktop(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) found = Win32.FindWindow("Progman", null);
        if (found != IntPtr.Zero) AltTabController.Activate(found);
    }

    // ---------------------------------------------------------------- move the active window along

    private async void MoveActiveWindow(int dir)
    {
        if (_busy) return;
        _busy = true;
        IntPtr hwnd = Native.GetForegroundWindow();
        bool pinnedByUs = false;
        try
        {
            var (count, idx) = VirtualDesktops.State();
            if (count < 2 || idx < 0) return;
            int target = (idx + dir + count) % count;   // wraps around

            if (!VirtualDesktops.DllAgrees(count, idx))
            {
                Toast.Show("Can't move windows: VirtualDesktopAccessor.dll isn't working", 1500);
                return;
            }
            if (!Movable(hwnd))
            {
                VirtualDesktops.GoToDesktopNumber(target);
                await WaitForChange(idx, 400);
                _indicator?.Tick();
                return;
            }
            if (VirtualDesktops.IsPinnedWindow(hwnd) != 1)
            {
                VirtualDesktops.PinWindow(hwnd);
                pinnedByUs = true;
            }
            VirtualDesktops.GoToDesktopNumber(target);
            int now = await WaitForChange(idx, 400);
            if (now != target) { App.Log($"move: switch did not reach {target} (at {now})"); return; }
            _indicator?.Tick();

            if (pinnedByUs)
            {
                VirtualDesktops.UnPinWindow(hwnd);
                pinnedByUs = false;
                VirtualDesktops.MoveWindowToDesktopNumber(hwnd, target);
            }
            // Only activate if the window is really here: activating a window on another desktop
            // makes Windows switch back to that desktop.
            if (VirtualDesktops.GetWindowDesktopNumber(hwnd) == target || VirtualDesktops.IsPinnedWindow(hwnd) == 1)
                AltTabController.Activate(hwnd);
        }
        catch (Exception e) { App.Log("move: " + e); }
        finally
        {
            if (pinnedByUs && hwnd != IntPtr.Zero)      // never leave a window pinned by accident
                try { VirtualDesktops.UnPinWindow(hwnd); } catch { }
            _busy = false;
        }
    }

    // ---------------------------------------------------------------- pin

    private void TogglePinActive()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (!VirtualDesktops.DllLoaded || !Movable(hwnd))
        {
            Toast.Show(VirtualDesktops.DllLoaded ? "This window can't be pinned" : "Can't pin: VirtualDesktopAccessor.dll isn't working");
            return;
        }
        try
        {
            bool pinned = VirtualDesktops.IsPinnedWindow(hwnd) == 1;
            if (pinned) VirtualDesktops.UnPinWindow(hwnd); else VirtualDesktops.PinWindow(hwnd);
            bool now = VirtualDesktops.IsPinnedWindow(hwnd) == 1;
            if (now != pinned) SetBorder(hwnd, now);
            Toast.Show(now == pinned ? "Couldn't change pin state" : now ? "Shown on all desktops" : "Only on this desktop");
        }
        catch (Exception e) { App.Log("pin: " + e); Toast.Show("Couldn't change pin state"); }
    }

    /// <summary>Native Windows 11 border colour on pinned windows: no overlay, no timer.</summary>
    private void SetBorder(IntPtr hwnd, bool on)
    {
        uint color = on ? PinBorder : 0xFFFFFFFF; // 0xFFFFFFFF = default colour
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, 4);
        if (on) _bordered.Add(hwnd); else _bordered.Remove(hwnd);
    }

    /// <summary>Normal app windows only: not the desktop, taskbar or tool windows.</summary>
    private static bool Movable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        string cls = Win32.ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        return (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) == 0;
    }

    public void Dispose()
    {
        foreach (var hwnd in new List<IntPtr>(_bordered)) SetBorder(hwnd, false);
        _indicator?.Dispose();
        _indicator = null;
        _watcher.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Hidden top-level window that hears the TaskbarCreated broadcast (Explorer restarted).
    /// Message-only windows don't get broadcasts, so this must be a real top-level window.
    /// </summary>
    private sealed class ExplorerWatcher : LayeredWindow
    {
        private static readonly uint TaskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        public static event Action? Restarted;
        public ExplorerWatcher() : base(clickThrough: true) { }

        protected override bool OnMessage(ref Forms.Message m)
        {
            if (TaskbarCreated != 0 && (uint)m.Msg == TaskbarCreated) Restarted?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// A click-through, always-on-top tool window (tool windows show on every desktop) holding a
    /// screenshot of all monitors, so the in-between desktops are never seen.
    /// </summary>
    private sealed class SnapshotCover : IDisposable
    {
        private readonly LayeredWindow _window = new(clickThrough: true);

        public static SnapshotCover Show()
        {
            var c = new SnapshotCover();
            int x = Win32.GetSystemMetrics(76), y = Win32.GetSystemMetrics(77), w = Win32.GetSystemMetrics(78), h = Win32.GetSystemMetrics(79);
            IntPtr screen = Win32.GetDC(IntPtr.Zero);
            IntPtr mem = Win32.CreateCompatibleDC(screen);
            IntPtr hbm = Win32.CreateCompatibleBitmap(screen, w, h);
            IntPtr old = Win32.SelectObject(mem, hbm);
            Win32.BitBlt(mem, 0, 0, w, h, screen, x, y, Win32.SRCCOPY);
            c._window.UpdateFromDc(mem, x, y, w, h, opaque: true);
            Win32.SelectObject(mem, old);
            Win32.DeleteObject(hbm);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
            return c;
        }

        public void Dispose() => _window.Dispose();
    }
}
