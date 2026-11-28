using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.AltTab;

/// <summary>
/// Replaces Alt+Tab with a switcher that only lists windows on one monitor
/// (the one under the cursor, or the one holding the active window).
/// The switcher is either the compact grid or the 3D Coverflow/Timeline view.
/// </summary>
internal sealed class AltTabController : IDisposable
{
    private const int VK_Q = 0x51, VK_D = 0x44, VK_F4 = 0x73;

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _hook;
    private readonly DispatcherTimer _showDelay;
    private SwitcherWindow? _grid;
    private CoverflowWindow? _coverflow;
    private ISwitcherView? _view;

    // Set on the hook thread, read on both; everything else runs on the UI thread.
    private volatile bool _active;
    internal bool IsActive => _active;

    // Session state.
    private List<SwitchItem> _items = new();
    private int _index;
    private IntPtr _monitor;

    public AltTabController(AppSettings settings, Dispatcher dispatcher, KeyboardHook hook)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _showDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (_, _) => ShowOverlay(), dispatcher) { IsEnabled = false };
        _hook = hook;
        hook.Add(OnKey);

        // Build the switcher window ahead of time so the first Alt+Tab doesn't pay for it.
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_settings.Coverflow.Enabled) new System.Windows.Interop.WindowInteropHelper((System.Windows.Window)(_coverflow ??= Wire(new CoverflowWindow()))).EnsureHandle();
            else new System.Windows.Interop.WindowInteropHelper((System.Windows.Window)(_grid ??= Wire(new SwitcherWindow()))).EnsureHandle();
        });
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool OnKey(int vk, bool isDown, bool altDown, bool injected)
    {
        if (injected) return false;
        if (!_active && !_settings.AltTabPerMonitorEnabled) return false;

        if (isDown)
        {
            if (vk == Native.VK_TAB && altDown && !IsDown(Native.VK_CONTROL) && !IsDown(Native.VK_LWIN) && !IsDown(Native.VK_RWIN))
            {
                bool shift = IsDown(Native.VK_SHIFT);
                if (!_active)
                {
                    _active = true;
                    // Tap an unassigned key while Alt is held so the window that had focus
                    // doesn't open its menu bar when Alt is released.
                    Native.SendKeys((Native.VK_DUMMY, false), (Native.VK_DUMMY, true));
                    _dispatcher.BeginInvoke(() => Begin(shift));
                }
                else
                {
                    _dispatcher.BeginInvoke(() => Move(shift ? -1 : 1));
                }
                return true;
            }

            if (_active)
            {
                switch (vk)
                {
                    case Native.VK_ESCAPE:
                        _active = false;
                        _dispatcher.BeginInvoke(Cancel);
                        return true;
                    case Native.VK_RETURN:
                        _active = false;
                        _dispatcher.BeginInvoke(Commit);
                        return true;
                    case Native.VK_LEFT:
                    case Native.VK_UP:
                        _dispatcher.BeginInvoke(() => Move(-1));
                        return true;
                    case Native.VK_RIGHT:
                    case Native.VK_DOWN:
                        _dispatcher.BeginInvoke(() => Move(1));
                        return true;
                    case VK_Q:
                    case VK_F4:
                        _dispatcher.BeginInvoke(CloseSelected);
                        return true;
                    case VK_D:
                        _active = false;
                        _dispatcher.BeginInvoke(ShowDesktop);
                        return true;
                }
            }
        }
        else if (_active)
        {
            if (vk is Native.VK_TAB or VK_Q or VK_F4) return true;
            if (vk is Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU)
            {
                _active = false;
                _dispatcher.BeginInvoke(Commit);
                return false; // let Alt-up through so key state stays consistent
            }
        }
        return false;
    }

    private void Begin(bool reverse)
    {
        _monitor = GetTargetMonitor();
        _items = WindowEnumerator.GetWindows(_monitor, _settings.IncludeMinimized);

        IntPtr fg = Native.GetForegroundWindow();
        if (_items.Count == 0) _index = -1;
        else if (reverse) _index = _items.Count - 1;
        else if (_items[0].Hwnd == fg && _items.Count > 1) _index = 1;
        else _index = 0;

        _showDelay.Stop();
        _showDelay.Start();
    }

    /// <summary>Shows the switcher for a few seconds without any key input (aestoys.exe --demo).</summary>
    public void Demo(double seconds, bool allMonitors = false)
    {
        if (_active) return;
        _active = true;
        Perf.Enabled = true;
        long t0 = Perf.Now;
        Begin(false);
        if (allMonitors)
        {
            _items = WindowEnumerator.GetWindows(IntPtr.Zero, _settings.IncludeMinimized);
            _index = _items.Count > 1 ? 1 : 0;
        }
        _showDelay.Stop();
        Perf.Log($"--- demo: {_items.Count} windows, enumerate {Perf.Ms(t0):0.0} ms");
        long t1 = Perf.Now;
        try { ShowOverlay(); }
        catch (Exception e) { App.Log("demo: " + e); }
        Perf.Log($"ShowOn {Perf.Ms(t1):0.0} ms");

        // Step through a few windows so switching animations get measured too.
        int steps = 0;
        var step = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        step.Tick += (_, _) => { if (!_active || ++steps > 4) { step.Stop(); return; } Move(1); };
        step.Start();

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); if (_active) { _active = false; Cancel(); } Perf.Enabled = false; };
        t.Start();
    }

    private IntPtr GetTargetMonitor()
    {
        if (_settings.MonitorMode == MonitorMode.ActiveWindow)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero) return Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
        }
        Native.GetCursorPos(out var pt);
        return Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
    }

    private void ShowOverlay()
    {
        _showDelay.Stop();
        if (!_active) return;
        _view = _settings.Coverflow.Enabled ? (_coverflow ??= Wire(new CoverflowWindow())) : (_grid ??= Wire(new SwitcherWindow()));
        _view.ShowOn(_monitor, _items, _index);
    }

    private T Wire<T>(T view) where T : ISwitcherView
    {
        view.ItemClicked += i => { _index = i; _active = false; Commit(); };
        view.CancelRequested += () => { _active = false; Cancel(); };
        view.Scrolled += Move;
        return view;
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        _index = ((_index < 0 ? 0 : _index) + delta + _items.Count) % _items.Count;
        if (_view?.IsShown == true) _view.Select(_index);
    }

    private void HideView()
    {
        _showDelay.Stop();
        _view?.HideView();
        _view = null;
    }

    private void Cancel()
    {
        HideView();
        _items = new();
    }

    private void Commit()
    {
        HideView();
        if (_index >= 0 && _index < _items.Count)
            Activate(_items[_index].Hwnd);
        _items = new();
    }

    /// <summary>Q / F4: close the selected window and keep the switcher open.</summary>
    private void CloseSelected()
    {
        if (_index < 0 || _index >= _items.Count) return;
        Native.PostMessage(_items[_index].Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _items.RemoveAt(_index);
        if (_items.Count == 0) { _index = -1; }
        else if (_index >= _items.Count) _index = _items.Count - 1;
        if (_view?.IsShown == true) _view.ShowOn(_monitor, _items, _index);
    }

    /// <summary>D: minimize every window on this monitor.</summary>
    private void ShowDesktop()
    {
        HideView();
        foreach (var item in _items)
            if (!item.IsMinimized) Native.ShowWindow(item.Hwnd, Native.SW_MINIMIZE);
        _items = new();
    }

    internal static void Activate(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;

        // Foreground lock: a synthetic Alt press grants this process permission to switch focus.
        Native.SendKeys((Native.VK_MENU, false), (Native.VK_DUMMY, false), (Native.VK_DUMMY, true));
        Native.SetForegroundWindow(hwnd);
        Native.SendKeys((Native.VK_MENU, true));
        if (Native.GetForegroundWindow() == hwnd) { Native.BringWindowToTop(hwnd); return; }

        Native.SwitchToThisWindow(hwnd, true);
    }

    public void Dispose()
    {
        _showDelay.Stop();
        _grid?.CloseView();
        _coverflow?.CloseView();
    }
}
