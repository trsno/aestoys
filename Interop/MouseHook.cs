using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aestoys.Interop;

/// <summary>
/// Global low-level mouse hook on its own thread (like <see cref="KeyboardHook"/>). Handlers return
/// true to swallow the event; they run on the hook thread and must be quick. Move events go to
/// handlers too, so handlers must return early unless they are in the middle of something.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public delegate bool MouseHandler(int msg, Native.POINT pt, int wheelDelta, bool injected);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public Native.POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr mod, uint tid);

    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
                     WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A;

    private readonly LowLevelMouseProc _proc;
    private volatile MouseHandler[] _handlers = Array.Empty<MouseHandler>();
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    public MouseHook()
    {
        _proc = Callback;
        int error = 0;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, Native.GetModuleHandle(null), 0);
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
        { IsBackground = true, Name = "aestoys mouse hook", Priority = ThreadPriority.Highest };
        _thread.IsBackground = true;   // never keep the process alive on exit
        _thread.Start();
        ready.Wait();
        if (_hook == IntPtr.Zero) throw new InvalidOperationException($"SetWindowsHookEx (mouse) failed: {error}");
    }

    public void Add(MouseHandler handler)
    {
        lock (_thread) _handlers = [.. _handlers, handler];
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool injected = data.dwExtraInfo == Native.OwnInputTag;
            int wheel = msg == WM_MOUSEWHEEL ? (short)(data.mouseData >> 16) : 0;
            try
            {
                foreach (var h in _handlers)
                    if (h(msg, data.pt, wheel, injected)) return (IntPtr)1;
            }
            catch { }
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

    /// <summary>Sends a click of <paramref name="button"/> (1 left, 2 right, 3 middle), tagged as ours.</summary>
    public static void SendClick(int button)
    {
        uint down = button switch { 2 => 0x0008u, 3 => 0x0020u, _ => 0x0002u };
        var inputs = new Native.INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = 0; // INPUT_MOUSE
            inputs[i].U.mi.dwFlags = i == 0 ? down : down << 1;
            inputs[i].U.mi.dwExtraInfo = Native.OwnInputTag;
        }
        Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
    }
}
