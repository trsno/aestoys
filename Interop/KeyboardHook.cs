using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aestoys.Interop;

/// <summary>
/// Global low-level keyboard hook. The handler returns true to swallow the key.
/// The hook lives on its own thread with its own message loop, so every keystroke on the system
/// is answered right away even while the UI thread is busy rendering or loading.
/// The handler therefore runs on that thread and must only decide and hand work off.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    public delegate bool KeyHandler(int vk, bool isDown, bool altDown, bool injected);

    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly KeyHandler _handler;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback; // keep delegate alive
        int error = 0;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "aestoys keyboard hook", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait();
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"SetWindowsHookEx failed: {error}");
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
        if (_threadId != 0 && _thread.IsAlive)
        {
            Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }
    }
}
