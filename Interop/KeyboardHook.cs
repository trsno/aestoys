using System;
using System.Runtime.InteropServices;

namespace Aestoys.Interop;

/// <summary>Global low-level keyboard hook. The handler returns true to swallow the key.</summary>
internal sealed class KeyboardHook : IDisposable
{
    public delegate bool KeyHandler(int vk, bool isDown, bool altDown, bool injected);

    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly KeyHandler _handler;
    private IntPtr _hook;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback; // keep delegate alive
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool injected = (data.flags & Native.LLKHF_INJECTED) != 0 && data.dwExtraInfo == Native.OwnInputTag;
            bool alt = (data.flags & Native.LLKHF_ALTDOWN) != 0;
            try
            {
                if (_handler((int)data.vkCode, down, alt, injected))
                    return (IntPtr)1;
            }
            catch { /* never let an exception kill the hook */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
