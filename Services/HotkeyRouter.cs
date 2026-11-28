using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Aestoys.Interop;

namespace Aestoys.Services;

/// <summary>
/// Global hotkeys on the shared low-level keyboard hook. Hotkeys are read through delegates each
/// time (so settings changes apply at once); a match is swallowed, Win/Alt is masked so Start or a
/// menu bar doesn't open on release, and the action runs on the UI thread.
/// </summary>
internal sealed class HotkeyRouter
{
    private sealed record Entry(Func<Hotkey> Key, Func<bool> Enabled, Action Action);

    private readonly Dispatcher _dispatcher;
    private volatile Entry[] _entries = Array.Empty<Entry>();
    private readonly HashSet<int> _held = new(); // hook thread only

    public HotkeyRouter(KeyboardHook hook, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        hook.Add(OnKey);
    }

    public void Register(Func<Hotkey> key, Func<bool> enabled, Action action)
    {
        lock (this) _entries = [.. _entries, new Entry(key, enabled, action)];
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool OnKey(int vk, bool isDown, bool altDown, bool injected)
    {
        if (injected) return false;
        if (!isDown) return _held.Remove(vk);
        if (_held.Contains(vk)) return true;            // auto-repeat of a hotkey we took
        if (Hotkey.IsModifierKey(vk)) return false;

        var pressed = new Hotkey(IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN), IsDown(Native.VK_CONTROL),
                                 altDown || IsDown(Native.VK_MENU), IsDown(Native.VK_SHIFT), vk);
        foreach (var e in _entries)
        {
            Hotkey k;
            try { k = e.Key(); } catch { continue; }
            if (k.IsNone || k != pressed) continue;
            bool enabled;
            try { enabled = e.Enabled(); } catch { enabled = false; }
            if (!enabled) continue;
            _held.Add(vk);
            if (pressed.Win || pressed.Alt) Native.SendKeys((Native.VK_DUMMY, false), (Native.VK_DUMMY, true));
            _dispatcher.BeginInvoke(() =>
            {
                try { e.Action(); } catch (Exception ex) { App.Log($"hotkey {k}: {ex}"); }
            });
            return true;
        }
        return false;
    }
}
