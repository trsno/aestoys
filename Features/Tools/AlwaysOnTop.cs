using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Features.Desktops;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Tools;

/// <summary>
/// Pins the active window on top (Win+Ctrl+T) with a coloured frame, and steps a pinned window's
/// opacity (Win+Ctrl+= / Win+Ctrl+-). Ported from PowerToys Always On Top (MIT): the frame is a
/// layered window placed just *below* the pinned window, so it never covers its content; sounds
/// are Speech On / Speech Sleep; opacity moves in 10% steps between 20% and 100%.
/// Everything is undone on exit.
/// </summary>
internal sealed class AlwaysOnTop : IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string sound, IntPtr mod, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    private static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    private const long WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    private sealed class Pinned
    {
        public IntPtr Hwnd;
        public LayeredWindow? Frame;
        public Native.RECT LastRect;
        public bool LastVisible;
        public bool WeAddedLayered;
        public bool Alpha;   // we changed its opacity
    }

    private readonly AppSettings _settings;
    private readonly Dictionary<IntPtr, Pinned> _pinned = new();
    private readonly Win32.WinEventProc _proc;
    private readonly DispatcherTimer _timer;
    private IntPtr _hookLoc, _hookMin, _hookDestroy;

    private AlwaysOnTopSettings S => _settings.Tools.AlwaysOnTop;

    public AlwaysOnTop(AppSettings settings, HotkeyRouter hotkeys)
    {
        _settings = settings;
        _proc = OnEvent;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) => UpdateAll(), Dispatcher.CurrentDispatcher);
        hotkeys.Register(() => S.Hotkey, () => S.Enabled, () => Toggle(Native.GetForegroundWindow()));
        hotkeys.Register(() => S.IncreaseOpacity, () => S.Enabled, () => StepOpacity(Native.GetForegroundWindow(), +10));
        hotkeys.Register(() => S.DecreaseOpacity, () => S.Enabled, () => StepOpacity(Native.GetForegroundWindow(), -10));
    }

    public int Count => _pinned.Count;

    /// <summary>Settings changed: redraw frames, or unpin everything when switched off.</summary>
    public void ApplySettings()
    {
        if (!S.Enabled) { UnpinAll(); return; }
        foreach (var p in _pinned.Values) { p.LastRect = default; }
        UpdateAll();
    }

    public void Toggle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;
        if (_pinned.ContainsKey(hwnd)) { Unpin(hwnd, sound: true); return; }
        if (!Eligible(hwnd)) return;
        Native.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        var p = new Pinned { Hwnd = hwnd };
        _pinned[hwnd] = p;
        if (S.ShowFrame) p.Frame = new LayeredWindow(clickThrough: true);
        EnsureHooks();
        Update(p);
        if (S.Sound) Play("Speech On.wav");
    }

    private bool Eligible(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (Win32.ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (S.SkipInGameMode && GameMode()) return false;
        if (IsExcluded(hwnd, S.ExcludedApps)) return false;
        return true;
    }

    internal static bool GameMode() => Win32.SHQueryUserNotificationState(out int s) == 0 && s == 3; // QUNS_RUNNING_D3D_FULL_SCREEN

    /// <summary>PowerToys-style exclusion list: one entry per line, matched against the exe path or window title.</summary>
    internal static bool IsExcluded(IntPtr hwnd, string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return false;
        string path = (Native.GetProcessPath(hwnd) ?? "").ToUpperInvariant();
        string title = Native.GetWindowTitle(hwnd).ToUpperInvariant();
        foreach (var raw in list.Split('\n', '\r'))
        {
            var e = raw.Trim().ToUpperInvariant();
            if (e.Length > 0 && (path.Contains(e) || title.Contains(e))) return true;
        }
        return false;
    }

    private void Unpin(IntPtr hwnd, bool sound)
    {
        if (!_pinned.Remove(hwnd, out var p)) return;
        if (Native.IsWindow(hwnd))
        {
            Native.SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            RestoreAlpha(p);
        }
        p.Frame?.Dispose();
        if (sound && S.Sound) Play("Speech Sleep.wav");
        if (_pinned.Count == 0) RemoveHooks();
    }

    public void UnpinAll()
    {
        foreach (var h in _pinned.Keys.ToList()) Unpin(h, sound: false);
    }

    // ---------------------------------------------------------------- opacity

    private void StepOpacity(IntPtr hwnd, int delta)
    {
        if (!_pinned.TryGetValue(hwnd, out var p)) return;   // like PowerToys: pinned windows only
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        int current = 100;
        if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte a, out uint flags) && (flags & 2) != 0)
            current = a * 100 / 255;
        int next = Math.Clamp(current + delta, 20, 100);
        if (next == current) return;
        if ((ex & WS_EX_LAYERED) == 0)
        {
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED));
            p.WeAddedLayered = true;
        }
        SetLayeredWindowAttributes(hwnd, 0, (byte)(next * 255 / 100), 2 /* LWA_ALPHA */);
        p.Alpha = true;
        if (S.Sound) Play(delta > 0 ? "Windows Hardware Insert.wav" : "Windows Hardware Remove.wav");
    }

    private static void RestoreAlpha(Pinned p)
    {
        if (!p.Alpha) return;
        SetLayeredWindowAttributes(p.Hwnd, 0, 255, 2);
        if (p.WeAddedLayered)
        {
            long ex = Native.GetWindowLongPtr(p.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(p.Hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
        }
    }

    // ---------------------------------------------------------------- frame tracking

    private void EnsureHooks()
    {
        if (_hookLoc != IntPtr.Zero) return;
        _hookLoc = Win32.SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
        _hookMin = Win32.SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
        _hookDestroy = Win32.SetWinEventHook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_DESTROY, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
        _timer.Start();   // catches desktop switches, cloaking and z-order changes
    }

    private void RemoveHooks()
    {
        foreach (var h in new[] { _hookLoc, _hookMin, _hookDestroy }) if (h != IntPtr.Zero) Win32.UnhookWinEvent(h);
        _hookLoc = _hookMin = _hookDestroy = IntPtr.Zero;
        _timer.Stop();
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || !_pinned.TryGetValue(hwnd, out var p)) return;
        if (evt == Win32.EVENT_OBJECT_DESTROY) { p.Frame?.Dispose(); _pinned.Remove(hwnd); if (_pinned.Count == 0) RemoveHooks(); return; }
        Update(p);
    }

    private void UpdateAll()
    {
        foreach (var p in _pinned.Values.ToList())
        {
            if (!Native.IsWindow(p.Hwnd)) { p.Frame?.Dispose(); _pinned.Remove(p.Hwnd); continue; }
            // Something else may have taken topmost away; keep it pinned.
            if ((Native.GetWindowLongPtr(p.Hwnd, Native.GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0)
                Native.SetWindowPos(p.Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            Update(p);
        }
        if (_pinned.Count == 0) RemoveHooks();
    }

    private void Update(Pinned p)
    {
        if (!S.ShowFrame) { p.Frame?.Hide(); return; }
        p.Frame ??= new LayeredWindow(clickThrough: true);
        bool visible = Native.IsWindowVisible(p.Hwnd) && !Native.IsIconic(p.Hwnd) && !Win32.IsZoomed(p.Hwnd)
                       && !(Native.DwmGetWindowAttribute(p.Hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                       && VirtualDesktops.IsOnCurrentDesktop(p.Hwnd);
        if (!visible) { p.Frame.Hide(); p.LastVisible = false; return; }
        if (Native.DwmGetWindowAttribute(p.Hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0) return;

        bool moved = r.Left != p.LastRect.Left || r.Top != p.LastRect.Top;
        bool resized = r.Width != p.LastRect.Width || r.Height != p.LastRect.Height;
        if (resized || !p.LastVisible) Draw(p, r);
        else if (moved) MoveFrame(p, r);
        else PlaceBelow(p, r);
        p.LastRect = r;
        p.LastVisible = true;
    }

    private int Thickness(IntPtr hwnd) => S.FrameThickness;
    private static double Scale(IntPtr hwnd) => Math.Max(1, Win32.GetDpiForWindow(hwnd)) / 96.0;

    private void Draw(Pinned p, Native.RECT r)
    {
        int border = Thickness(p.Hwnd);
        double scale = Scale(p.Hwnd);
        int w = r.Width + 2 * border, h = r.Height + 2 * border;
        if (w <= 0 || h <= 0) return;
        float thickness = (float)(S.FrameThickness * scale);
        float radius = S.RoundCorners ? (float)(8 * scale) : 0;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = FrameColor();
            using var pen = new Pen(Color.FromArgb((int)(Math.Clamp(S.FrameOpacity, 0, 100) * 2.55), c), thickness);
            float inset = thickness / 2 + 1;
            var rect = new RectangleF(inset, inset, w - 2 * inset, h - 2 * inset);
            if (radius > 0)
            {
                using var path = Toast.RoundRect(rect.X, rect.Y, rect.Width, rect.Height, radius + thickness / 2);
                g.DrawPath(pen, path);
            }
            else g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
        p.Frame!.Update(bmp, r.Left - border, r.Top - border);
        PlaceBelow(p, r);
    }

    private void MoveFrame(Pinned p, Native.RECT r)
    {
        int border = Thickness(p.Hwnd);
        Native.SetWindowPos(p.Frame!.Handle, p.Hwnd, r.Left - border, r.Top - border, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Directly below the pinned window, so the frame shows only around it.</summary>
    private static void PlaceBelow(Pinned p, Native.RECT r) =>
        Native.SetWindowPos(p.Frame!.Handle, p.Hwnd, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

    private Color FrameColor()
    {
        if (S.FrameUsesAccentColor)
        {
            var a = System.Windows.SystemParameters.WindowGlassColor;
            return Color.FromArgb(a.R, a.G, a.B);
        }
        try { return ColorTranslator.FromHtml(S.FrameColor.Length == 9 ? "#" + S.FrameColor[3..] : S.FrameColor); }
        catch { return Color.FromArgb(0x00, 0x58, 0xCC); }
    }

    private static void Play(string file)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);
        if (File.Exists(path)) PlaySound(path, IntPtr.Zero, 0x20000 /* SND_FILENAME */ | 0x1 /* SND_ASYNC */);
    }

    public void Dispose()
    {
        UnpinAll();
        RemoveHooks();
    }
}
