using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Interop;
using Aestoys.Services;
using Microsoft.Win32;

namespace Aestoys.Features.Tools;

/// <summary>
/// Per-app window transparency (replaces Glass2k). Modifiers+1…9 makes the active window see-through
/// (1 = most transparent, 9 = barely), Modifiers+0 makes it opaque again. The level is remembered
/// for that app's kind of window (exe + window class, like Glass2k) and applied to every such
/// window when it appears. Levels match Glass2k exactly: alpha = 105 + 15 × level.
/// </summary>
internal sealed class WindowTransparency : IDisposable
{
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr l);

    private const long WS_EX_LAYERED = 0x80000;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, LWA_ALPHA = 2;

    private readonly AppSettings _settings;
    private readonly Win32.WinEventProc _proc;
    private IntPtr _hookShow, _hookFg;
    /// <summary>Windows we changed, and whether we added WS_EX_LAYERED (so it can be removed again).</summary>
    private readonly Dictionary<IntPtr, bool> _touched = new();
    private Hotkey[] _keys = new Hotkey[10];

    private WindowTransparencySettings S => _settings.Tools.Transparency;

    public static byte Alpha(int level) => level <= 0 ? (byte)255 : (byte)Math.Clamp(105 + 15 * level, 0, 255);

    public WindowTransparency(AppSettings settings, HotkeyRouter hotkeys)
    {
        _settings = settings;
        _proc = OnEvent;
        for (int d = 0; d <= 9; d++)
        {
            int level = d;
            hotkeys.Register(() => _keys[level], () => S.Enabled, () => SetForeground(level));
        }
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (S.Rules.Keys.Any(k => k != k.ToLowerInvariant()))
            S.Rules = S.Rules.GroupBy(r => r.Key.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Last().Value);
        var mods = Hotkey.Parse(S.Modifiers + "+A");
        _keys = Enumerable.Range(0, 10).Select(d => mods with { Vk = 0x30 + d }).ToArray();
        if (S.Enabled)
        {
            if (_hookShow == IntPtr.Zero)
            {
                _hookShow = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
                // Some apps (Chromium, Electron) reset their layered style when they redraw their frame; re-check on focus.
                _hookFg = Win32.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_SKIPOWNPROCESS);
            }
            ApplyAll();
        }
        else
        {
            Unhook();
            RestoreAll();
        }
    }

    private void Unhook()
    {
        if (_hookShow != IntPtr.Zero) { Win32.UnhookWinEvent(_hookShow); _hookShow = IntPtr.Zero; }
        if (_hookFg != IntPtr.Zero) { Win32.UnhookWinEvent(_hookFg); _hookFg = IntPtr.Zero; }
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || S.Rules.Count == 0) return;
        if (GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
        // Some windows finish setting themselves up after they're shown: check a matching one once more.
        if (Apply(hwnd) && evt == Win32.EVENT_OBJECT_SHOW)
            new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); Apply(hwnd); }, System.Windows.Application.Current.Dispatcher).Start();
    }

    private void ApplyAll()
    {
        if (S.Rules.Count == 0) return;
        EnumWindows((h, _) => { if (Native.IsWindowVisible(h)) Apply(h); return true; }, IntPtr.Zero);
    }

    /// <summary>pid → exe file name; windows are shown often (menus, tooltips), opening the process each time is wasteful.</summary>
    private readonly Dictionary<uint, string> _exeByPid = new();

    /// <summary>"exe|class" in lower case (settings reload as a plain dictionary, so keys must not depend on case).</summary>
    private string? Key(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        if (!_exeByPid.TryGetValue(pid, out var exe))
        {
            string? path = Native.GetProcessPath(hwnd);
            if (path is null) return null;
            if (_exeByPid.Count > 512) _exeByPid.Clear();
            _exeByPid[pid] = exe = Path.GetFileName(path);
        }
        return RuleKey(exe, Win32.ClassName(hwnd));
    }

    public static string RuleKey(string exe, string cls) => (exe + "|" + cls).ToLowerInvariant();

    /// <summary>Applies the remembered level for this kind of window, if there is one. True if a rule matched.</summary>
    private bool Apply(IntPtr hwnd)
    {
        if (!S.Enabled || !Native.IsWindow(hwnd)) return false;
        if (Key(hwnd) is not { } key || !S.Rules.TryGetValue(key, out int level)) return false;
        Set(hwnd, level);
        return true;
    }

    /// <summary>Removes a rule and makes that app's open windows solid again.</summary>
    public void Forget(string key)
    {
        S.Rules.Remove(key);
        EnumWindows((h, _) => { if (Native.IsWindowVisible(h) && Key(h) == key) Set(h, 0); return true; }, IntPtr.Zero);
    }

    /// <summary>Called by other features that change a window's alpha (Always On Top) to put this one's back.</summary>
    public void Reapply(IntPtr hwnd)
    {
        _touched.Remove(hwnd);
        Apply(hwnd);
    }

    private void SetForeground(int level)
    {
        IntPtr hwnd = GetAncestor(Native.GetForegroundWindow(), 2);
        if (hwnd == IntPtr.Zero) return;
        string cls = Win32.ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
        Set(hwnd, level);
        if (S.Remember && Key(hwnd) is { } key)
        {
            if (level == 0) S.Rules.Remove(key); else S.Rules[key] = level;
            _settings.Save();
        }
    }

    private void Set(IntPtr hwnd, int level)
    {
        if (Native.IsHungAppWindow(hwnd)) return;   // style changes on a hung window would block us too
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        bool layered = (ex & WS_EX_LAYERED) != 0;
        byte current = 255;
        if (layered && GetLayeredWindowAttributes(hwnd, out _, out byte a0, out uint flags) && (flags & LWA_ALPHA) != 0) current = a0;
        if (level <= 0)
        {
            // Also resets windows made see-through before this run (a previous session, or Glass2k).
            bool added = _touched.Remove(hwnd, out bool a) && a;
            if (current < 255) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            if (added && layered) Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
            return;
        }
        byte alpha = Alpha(level);
        if (current != alpha)
        {
            if (!layered) Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED));
            if (!SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA)) return;   // e.g. an admin window
        }
        // Track it even when it already had this alpha, so turning the feature off restores it.
        if (!_touched.ContainsKey(hwnd)) _touched[hwnd] = !layered;
    }

    private void RestoreAll()
    {
        foreach (var h in _touched.Keys.ToList()) if (Native.IsWindow(h)) Set(h, 0);
        _touched.Clear();
    }

    // ---------------------------------------------------------------- Glass2k import

    /// <summary>Copies Glass2k's remembered windows and its hotkey modifiers. Returns a summary.</summary>
    public static string ImportGlass2k(AppSettings settings)
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\VB and VBA Program Settings\Glass2k");
        if (root is null) return "Glass2k settings not found";
        var s = settings.Tools.Transparency;
        int n = 0;
        using (var wins = root.OpenSubKey("Wins"))
            foreach (var name in wins?.GetValueNames() ?? Array.Empty<string>())
            {
                // Glass2k stores "<exe><class>" with nothing between them.
                int cut = name.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (cut < 0 || !int.TryParse(wins!.GetValue(name) as string, out int level)) continue;
                string exe = name[..(cut + 4)], cls = name[(cut + 4)..];
                if (exe.Equals("Glass2k.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("Ditto.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (level <= 0) continue;   // 0 = opaque, the same as no rule
                s.Rules[RuleKey(exe, cls)] = Math.Clamp(level, 1, 9);
                n++;
            }
        using (var opt = root.OpenSubKey("Settings"))
        {
            // KS is the Ctrl / Shift / Alt boxes of Glass2k's hotkey option (7 = all three).
            if (int.TryParse(opt?.GetValue("KS") as string, out int ks) && ks > 0)
                s.Modifiers = string.Join("+", new[] { (1, "Ctrl"), (4, "Alt"), (2, "Shift") }.Where(m => (ks & m.Item1) != 0).Select(m => m.Item2));
            if (int.TryParse(opt?.GetValue("Remember") as string, out int rem)) s.Remember = rem != 0;
        }
        s.Enabled = true;
        s.Glass2kImported = true;
        settings.Save();
        return $"Glass2k import: {n} remembered windows ({string.Join(", ", s.Rules.Select(r => $"{r.Key}={r.Value}"))}); hotkey {s.Modifiers}+0-9";
    }

    public void Dispose()
    {
        Unhook();
        RestoreAll();
    }
}
