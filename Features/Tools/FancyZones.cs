using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Features.Desktops;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Tools;

/// <summary>
/// FancyZones (PowerToys, MIT), for canvas layouts:
/// - hold Shift while dragging a window to see the zones of the monitor under the cursor, and drop
///   it into the highlighted zone(s) (overlapping zones are resolved like PowerToys' "positional");
/// - Win+Left/Right moves the active window to the previous/next zone of its monitor (wrapping);
///   on a monitor without zones the keys go to Windows as usual;
/// - apps reopen in the zone they were last snapped to;
/// - snapped windows are put back into their zones when displays change.
/// </summary>
internal sealed class FancyZones : IDisposable
{
    // ------------------------------------------------------------------ native

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref Native.RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const long WS_EX_LAYERED = 0x80000, WS_CAPTION = 0xC00000, WS_EX_TOOLWINDOW = 0x80;

    // ------------------------------------------------------------------ monitors

    internal sealed record Monitor(IntPtr Handle, string Key, Rectangle Work, Rectangle Bounds, double Scale);

    private List<Monitor> _monitors = new();

    internal IReadOnlyList<Monitor> Monitors => _monitors;

    private void RefreshMonitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(mon, ref mi)) return true;
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string key = mi.szDevice;
            // \\?\DISPLAY#GSM5BBA#5&...  →  GSM5BBA (the id PowerToys uses for "monitor")
            if (EnumDisplayDevices(mi.szDevice, 0, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */))
            {
                var parts = dd.DeviceID.Split('#');
                if (parts.Length > 1 && parts[1].Length > 0) key = parts[1];
            }
            double scale = 1;
            try { if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { }
            list.Add(new Monitor(mon, key,
                Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom), scale));
            return true;
        }, IntPtr.Zero);
        _monitors = list;
    }

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    private Monitor? MonitorOf(IntPtr hwnd)
    {
        IntPtr m = MonitorFromWindow(hwnd, 2);
        return _monitors.FirstOrDefault(x => x.Handle == m) ?? Refreshed(m);
    }

    private Monitor? MonitorAt(Point p)
    {
        IntPtr m = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, 2);
        return _monitors.FirstOrDefault(x => x.Handle == m) ?? Refreshed(m);
    }

    private Monitor? Refreshed(IntPtr handle)
    {
        RefreshMonitors();
        return _monitors.FirstOrDefault(x => x.Handle == handle);
    }

    // ------------------------------------------------------------------ layouts and zones

    internal ZoneLayout? LayoutFor(Monitor m)
    {
        string? id = S.MonitorLayouts.TryGetValue(m.Key, out var v) ? v : S.DefaultLayoutId;
        if (string.IsNullOrEmpty(id)) return null;
        return S.Layouts.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Canvas zones scaled from the layout's reference size to the monitor's work area.</summary>
    internal static List<Rectangle> ZonesFor(ZoneLayout layout, Rectangle work)
    {
        double sx = work.Width / (double)Math.Max(1, layout.RefWidth), sy = work.Height / (double)Math.Max(1, layout.RefHeight);
        return layout.Zones.Select(z => new Rectangle(
            work.Left + (int)(z.X * sx), work.Top + (int)(z.Y * sy),
            (int)(z.Width * sx), (int)(z.Height * sy))).ToList();
    }

    /// <summary>PowerToys' ZonesFromPoint for the "positional" (and other) overlap algorithms.</summary>
    internal static List<int> ZonesFromPoint(List<Rectangle> zones, Point pt, int radius, OverlapAlgorithm algorithm)
    {
        var captured = new List<int>();
        var strict = new List<int>();
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (pt.X >= z.Left - radius && pt.X < z.Right + radius && pt.Y >= z.Top - radius && pt.Y < z.Bottom + radius)
                captured.Add(i);
            if (pt.X >= z.Left && pt.X < z.Right && pt.Y >= z.Top && pt.Y < z.Bottom)
                strict.Add(i);
        }
        if (captured.Count == 1 && strict.Count == 0) return new List<int>();

        bool overlap = false;
        for (int a = 0; a < captured.Count && !overlap; a++)
            for (int b = a + 1; b < captured.Count && !overlap; b++)
            {
                var r1 = zones[captured[a]]; var r2 = zones[captured[b]];
                if (Math.Max(r1.Top, r2.Top) + radius < Math.Min(r1.Bottom, r2.Bottom) &&
                    Math.Max(r1.Left, r2.Left) + radius < Math.Min(r1.Right, r2.Right))
                    overlap = true;
            }
        if (!overlap) return captured;

        switch (algorithm)
        {
            case OverlapAlgorithm.Smallest:
                return new List<int> { captured.OrderBy(i => (long)zones[i].Width * zones[i].Height).First() };
            case OverlapAlgorithm.Largest:
                return new List<int> { captured.OrderByDescending(i => (long)zones[i].Width * zones[i].Height).First() };
            case OverlapAlgorithm.ClosestCenter:
            {
                const int sensitivity = 75;
                var best = captured.OrderBy(i =>
                {
                    var z = zones[i];
                    double dx = pt.X - (z.Left + z.Width / 2.0), dy = pt.Y - (z.Top + z.Height / 2.0);
                    return dx * dx + dy * dy;
                }).ToList();
                return new List<int> { best[0] };
            }
            default: // Positional
            {
                int half = radius / 2;
                int left = int.MinValue, top = int.MinValue, right = int.MaxValue, bottom = int.MaxValue;
                foreach (int i in captured)
                {
                    var z = zones[i];
                    left = Math.Max(left, z.Left - half); top = Math.Max(top, z.Top - half);
                    right = Math.Min(right, z.Right + half); bottom = Math.Min(bottom, z.Bottom + half);
                }
                int w = right - left, h = bottom - top, n = captured.Count, index;
                if (h > w) index = h > 0 ? (pt.Y - top) * n / h : 0;
                else index = w > 0 ? (pt.X - left) * n / w : 0;
                return new List<int> { captured[Math.Clamp(index, 0, n - 1)] };
            }
        }
    }

    // ------------------------------------------------------------------ state

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Win32.WinEventProc _proc;
    private readonly DispatcherTimer _dragTimer;
    private IntPtr _hook;
    private readonly Dictionary<IntPtr, (string monitor, List<int> zones)> _snapped = new();
    private readonly Dictionary<IntPtr, LayeredWindow> _overlays = new();

    // drag
    private IntPtr _dragged;
    private Size _dragStartSize;
    private bool _zonesShown;
    private Monitor? _dragMonitor;
    private List<int> _highlight = new();
    private (bool layered, byte alpha, bool hadAlpha)? _savedAlpha;

    private FancyZonesSettings S => _settings.Tools.FancyZones;

    public FancyZones(AppSettings settings, HotkeyRouter hotkeys)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        _proc = OnEvent;
        _dragTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => DragTick(), _ui);
        RefreshMonitors();

        hotkeys.Register(() => Hotkey.Parse("Win+Left"), () => S.Enabled && S.OverrideSnapKeys && ForegroundHasZones(), () => MoveByIndex(-1));
        hotkeys.Register(() => Hotkey.Parse("Win+Right"), () => S.Enabled && S.OverrideSnapKeys && ForegroundHasZones(), () => MoveByIndex(+1));
        hotkeys.Register(() => S.EditorHotkey, () => S.Enabled, OpenEditor);

        WindowPlacement.NewWindowHandler = OnNewWindow;
        WindowPlacement.NewWindowHandlerActive = () => S.Enabled && S.MoveToLastZone;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (S.Enabled && _hook == IntPtr.Zero)
            _hook = Win32.SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
        else if (!S.Enabled && _hook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            EndDrag(snap: false);
        }
    }

    // ------------------------------------------------------------------ dragging

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        if (evt == EVENT_SYSTEM_MOVESIZESTART) StartDrag(hwnd);
        else if (evt == EVENT_SYSTEM_MOVESIZEEND && hwnd == _dragged) EndDrag(snap: true);
    }

    private bool Eligible(IntPtr hwnd)
    {
        if (Win32.GetAncestor(hwnd, 2) != hwnd) return false;
        long style = Native.GetWindowLongPtr(hwnd, -16).ToInt64(), ex = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION || (ex & WS_EX_TOOLWINDOW) != 0) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        return !AlwaysOnTop.IsExcluded(hwnd, S.ExcludedApps);
    }

    private void StartDrag(IntPtr hwnd)
    {
        if (!S.Enabled || !Eligible(hwnd)) return;
        Native.GetWindowRect(hwnd, out var r);
        _dragged = hwnd;
        _dragStartSize = new Size(r.Width, r.Height);
        _zonesShown = false;
        _highlight = new List<int>();
        _dragMonitor = null;
        _dragTimer.Start();
    }

    private void DragTick()
    {
        if (_dragged == IntPtr.Zero || !Native.IsWindow(_dragged)) { EndDrag(snap: false); return; }
        Native.GetWindowRect(_dragged, out var r);
        // A size change means the user is resizing, not moving: FancyZones stays out of it.
        if (!Win32.IsZoomed(_dragged) && (Math.Abs(r.Width - _dragStartSize.Width) > 2 || Math.Abs(r.Height - _dragStartSize.Height) > 2) && !_zonesShown)
        {
            EndDrag(snap: false);
            return;
        }
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool want = S.ShiftDrag ? shift : !shift;
        if (!want) { HideZones(); return; }

        Native.GetCursorPos(out var cp);
        var pt = new Point(cp.X, cp.Y);
        var mon = MonitorAt(pt);
        var layout = mon is null ? null : LayoutFor(mon);
        if (mon is null || layout is null || layout.Zones.Count == 0) { HideZones(); return; }

        var zones = ZonesFor(layout, mon.Work);
        var hit = ZonesFromPoint(zones, pt, (int)(layout.SensitivityRadius * mon.Scale), S.OverlapAlgorithm);
        bool changed = !_zonesShown || mon != _dragMonitor || !hit.SequenceEqual(_highlight);
        if (!_zonesShown) MakeTransparent(true);
        _zonesShown = true;
        if (mon != _dragMonitor) foreach (var (h, o) in _overlays) if (h != mon.Handle) o.Hide();
        _dragMonitor = mon;
        _highlight = hit;
        if (changed) DrawZones(mon, zones, hit);
    }

    private void HideZones()
    {
        if (!_zonesShown) return;
        _zonesShown = false;
        _highlight = new List<int>();
        foreach (var o in _overlays.Values) o.Hide();
        MakeTransparent(false);
    }

    private void EndDrag(bool snap)
    {
        _dragTimer.Stop();
        var hwnd = _dragged;
        var mon = _dragMonitor;
        var zones = _highlight;
        bool shown = _zonesShown;
        HideZones();
        _dragged = IntPtr.Zero;
        if (!snap || hwnd == IntPtr.Zero) return;
        if (shown && mon is not null && zones.Count > 0) SnapTo(hwnd, mon, zones, remember: true);
        else _snapped.Remove(hwnd);   // dropped outside any zone
    }

    private void MakeTransparent(bool on)
    {
        if (!S.MakeDraggedWindowTransparent || _dragged == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(_dragged, -20).ToInt64();
        if (on)
        {
            bool layered = (ex & WS_EX_LAYERED) != 0;
            byte alpha = 255; bool hadAlpha = false;
            if (layered && GetLayeredWindowAttributes(_dragged, out _, out alpha, out uint flags)) hadAlpha = (flags & 2) != 0;
            // Windows that draw themselves with UpdateLayeredWindow can't take an alpha; leave them be.
            if (layered && !hadAlpha) return;
            _savedAlpha = (layered, alpha, hadAlpha);
            if (!layered) Native.SetWindowLongPtr(_dragged, -20, (IntPtr)(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(_dragged, 0, 128, 2);
        }
        else if (_savedAlpha is { } s)
        {
            SetLayeredWindowAttributes(_dragged, 0, s.hadAlpha ? s.alpha : (byte)255, 2);
            if (!s.layered) Native.SetWindowLongPtr(_dragged, -20, (IntPtr)(ex & ~WS_EX_LAYERED));
            _savedAlpha = null;
        }
    }

    // ------------------------------------------------------------------ drawing

    private void DrawZones(Monitor mon, List<Rectangle> zones, List<int> highlight)
    {
        if (!_overlays.TryGetValue(mon.Handle, out var overlay))
            _overlays[mon.Handle] = overlay = new LayeredWindow(clickThrough: true);
        var work = mon.Work;
        using var bmp = new Bitmap(work.Width, work.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var (zoneColor, borderColor, highColor, numberColor) = Colors();
            int alpha = (int)(Math.Clamp(S.HighlightOpacity, 0, 100) * 2.55);
            float r = (float)(8 * mon.Scale), inset = (float)(4 * mon.Scale);
            using var font = new Font("Segoe UI Variable Display", (float)(40 * mon.Scale), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            // Highlighted zones last, so they sit on top of the ones they overlap.
            var order = Enumerable.Range(0, zones.Count).OrderBy(i => highlight.Contains(i) ? 1 : 0).ToList();
            foreach (int i in order)
            {
                var z = zones[i];
                z.Offset(-work.Left, -work.Top);
                bool hot = highlight.Contains(i);
                using var path = Toast.RoundRect(z.Left + inset, z.Top + inset, z.Width - 2 * inset, z.Height - 2 * inset, r);
                using var fill = new SolidBrush(Color.FromArgb(hot ? Math.Max(alpha, 90) : alpha, hot ? highColor : zoneColor));
                g.FillPath(fill, path);
                using var pen = new Pen(Color.FromArgb(hot ? 255 : 200, hot ? highColor : borderColor), (float)((hot ? 3 : 1.5) * mon.Scale));
                g.DrawPath(pen, path);
                if (S.ShowZoneNumbers)
                {
                    using var nb = new SolidBrush(numberColor);
                    var s = (i + 1).ToString();
                    var size = g.MeasureString(s, font);
                    g.DrawString(s, font, nb, z.Left + (z.Width - size.Width) / 2, z.Top + (z.Height - size.Height) / 2);
                }
            }
        }
        overlay.Update(bmp, work.Left, work.Top);
        overlay.Show();
    }

    private (Color zone, Color border, Color high, Color number) Colors()
    {
        if (S.UseSystemTheme)
        {
            bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() != Wpf.Ui.Appearance.SystemTheme.Light;
            var a = System.Windows.SystemParameters.WindowGlassColor;
            var accent = Color.FromArgb(a.R, a.G, a.B);
            return dark
                ? (Color.FromArgb(32, 32, 32), Color.FromArgb(120, 120, 120), accent, Color.White)
                : (Color.FromArgb(243, 243, 243), Color.FromArgb(160, 160, 160), accent, Color.Black);
        }
        return (Parse(S.ZoneColor, Color.FromArgb(0xF5, 0xFC, 0xFF)), Parse(S.BorderColor, Color.White), Parse(S.HighlightColor, Color.FromArgb(0x4E, 0x26, 0xD7)), Color.Black);
    }

    private static Color Parse(string s, Color fallback)
    {
        try { return ColorTranslator.FromHtml(s.Length == 9 ? "#" + s[3..] : s); } catch { return fallback; }
    }

    // ------------------------------------------------------------------ snapping

    /// <summary>Moves <paramref name="hwnd"/> so its visible frame fills the union of the zones.</summary>
    internal void SnapTo(IntPtr hwnd, Monitor mon, List<int> indices, bool remember)
    {
        var layout = LayoutFor(mon);
        if (layout is null) return;
        var zones = ZonesFor(layout, mon.Work);
        var valid = indices.Where(i => i >= 0 && i < zones.Count).Distinct().OrderBy(i => i).ToList();
        if (valid.Count == 0) return;
        var target = valid.Select(i => zones[i]).Aggregate(Rectangle.Union);

        if (Win32.IsZoomed(hwnd) || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        Place(hwnd, target);
        Place(hwnd, target);   // again: crossing to a monitor with another DPI resizes the window once

        _snapped[hwnd] = (mon.Key, valid);
        if (remember) Remember(hwnd, mon, valid);
    }

    /// <summary>SetWindowPos for a rectangle meant for the visible frame (adds the invisible DWM borders).</summary>
    private static void Place(IntPtr hwnd, Rectangle target)
    {
        Native.GetWindowRect(hwnd, out var win);
        int l = 0, t = 0, rr = 0, b = 0;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            l = frame.Left - win.Left; t = frame.Top - win.Top; rr = win.Right - frame.Right; b = win.Bottom - frame.Bottom;
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, target.Left - l, target.Top - t, target.Width + l + rr, target.Height + t + b,
            0x4 /* NOZORDER */ | 0x10 /* NOACTIVATE */ | 0x200 /* NOOWNERZORDER */);
    }

    private bool ForegroundHasZones()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || !Eligible(fg)) return false;
        var mon = MonitorOf(fg);
        return mon is not null && LayoutFor(mon) is { Zones.Count: > 0 };
    }

    /// <summary>Win+Left/Right: previous/next zone by index, wrapping within the monitor.</summary>
    private void MoveByIndex(int dir)
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        var mon = hwnd == IntPtr.Zero ? null : MonitorOf(hwnd);
        var layout = mon is null ? null : LayoutFor(mon);
        if (mon is null || layout is null || layout.Zones.Count == 0) return;
        int n = layout.Zones.Count;
        int next;
        if (_snapped.TryGetValue(hwnd, out var s) && s.monitor == mon.Key && s.zones.Count > 0)
            next = dir > 0 ? (s.zones.Max() + 1) % n : (s.zones.Min() - 1 + n) % n;
        else
            next = dir > 0 ? 0 : n - 1;
        SnapTo(hwnd, mon, new List<int> { next }, remember: true);
    }

    // ------------------------------------------------------------------ history

    private void Remember(IntPtr hwnd, Monitor mon, List<int> zones)
    {
        string? path = Native.GetProcessPath(hwnd);
        if (string.IsNullOrEmpty(path)) return;
        S.History.RemoveAll(h => h.AppPath.Equals(path, StringComparison.OrdinalIgnoreCase) && h.Monitor == mon.Key);
        S.History.Add(new ZoneHistoryEntry { AppPath = path, Monitor = mon.Key, Zones = zones.ToList() });
        _settings.Save();
    }

    /// <summary>A new app window: put it back in its last zone (on its own monitor if remembered there).</summary>
    private bool OnNewWindow(IntPtr hwnd)
    {
        if (!S.Enabled || !S.MoveToLastZone || !Eligible(hwnd)) return false;
        string? path = Native.GetProcessPath(hwnd);
        if (string.IsNullOrEmpty(path)) return false;
        var entries = S.History.Where(h => h.AppPath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0) return false;
        var here = MonitorOf(hwnd);
        var entry = entries.FirstOrDefault(e => here is not null && e.Monitor == here.Key)
                    ?? entries.FirstOrDefault(e => _monitors.Any(m => m.Key == e.Monitor));
        var mon = entry is null ? null : _monitors.FirstOrDefault(m => m.Key == entry.Monitor);
        if (entry is null || mon is null || LayoutFor(mon) is null) return false;
        SnapTo(hwnd, mon, entry.Zones, remember: false);
        return true;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(() =>
        {
            RefreshMonitors();
            foreach (var o in _overlays.Values) o.Dispose();
            _overlays.Clear();
            if (!S.Enabled || !S.MoveOnDisplayChange) return;
            foreach (var (hwnd, (key, zones)) in _snapped.ToList())
            {
                if (!Native.IsWindow(hwnd)) { _snapped.Remove(hwnd); continue; }
                var mon = _monitors.FirstOrDefault(m => m.Key == key);
                if (mon is not null) SnapTo(hwnd, mon, zones, remember: false);
            }
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ editor

    private ZoneEditorWindow? _editor;

    public void OpenEditor()
    {
        RefreshMonitors();
        Native.GetCursorPos(out var cp);
        var mon = MonitorAt(new Point(cp.X, cp.Y)) ?? _monitors.FirstOrDefault();
        if (mon is null) return;
        _editor?.Close();
        _editor = new ZoneEditorWindow(this, _settings, mon);
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
        _editor.Activate();
    }

    /// <summary>Test hook (--demo-zones): shows the zones of every monitor for a few seconds.</summary>
    public void Demo(int highlight)
    {
        RefreshMonitors();
        foreach (var m in _monitors)
        {
            var layout = LayoutFor(m);
            if (layout is null) continue;
            DrawZones(m, ZonesFor(layout, m.Work), highlight >= 0 ? new List<int> { highlight } : new List<int>());
        }
        var t = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); foreach (var o in _overlays.Values) o.Hide(); }, _ui);
        t.Start();
    }

    public void Dispose()
    {
        EndDrag(snap: false);
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        WindowPlacement.NewWindowHandler = null;
        WindowPlacement.NewWindowHandlerActive = null;
        foreach (var o in _overlays.Values) o.Dispose();
        _overlays.Clear();
        _editor?.Close();
    }
}
