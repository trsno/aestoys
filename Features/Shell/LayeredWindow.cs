using System;
using System.Drawing;
using Aestoys.Interop;
using Forms = System.Windows.Forms;

namespace Aestoys.Features.Shell;

/// <summary>
/// A bare Win32 layered window drawn with UpdateLayeredWindow, in physical pixels.
/// Topmost tool window (shows on every virtual desktop), never takes focus.
/// </summary>
internal class LayeredWindow : Forms.NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

    public event Action<int, int, int>? Message; // msg, x, y (client coords) for mouse messages

    public bool Visible { get; private set; }

    public LayeredWindow(bool clickThrough)
    {
        var cp = new Forms.CreateParams
        {
            Caption = "aestoys overlay",
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | (clickThrough ? WS_EX_TRANSPARENT : 0),
            X = -100, Y = -100, Width = 1, Height = 1,
        };
        CreateHandle(cp);
    }

    /// <summary>Puts <paramref name="bmp"/> on screen at (x, y). Per-pixel alpha (premultiplied) unless <paramref name="opaque"/>.</summary>
    public void Update(Bitmap bmp, int x, int y, bool opaque = false)
    {
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = Win32.CreateCompatibleDC(screen);
        IntPtr hbm = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Win32.SelectObject(mem, hbm);
        try { UpdateFromDc(mem, x, y, bmp.Width, bmp.Height, opaque); }
        finally
        {
            Win32.SelectObject(mem, old);
            Win32.DeleteObject(hbm);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void UpdateFromDc(IntPtr hdc, int x, int y, int w, int h, bool opaque)
    {
        var pt = new Native.POINT { X = x, Y = y };
        var size = new Win32.SIZE { cx = w, cy = h };
        var src = new Native.POINT();
        var blend = new Win32.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = (byte)(opaque ? 0 : 1) };
        Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref pt, ref size, hdc, ref src, 0, ref blend, Win32.ULW_ALPHA);
        Show();
    }

    public void Show()
    {
        if (!Visible) { Native.ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */); Visible = true; }
        BringToTop();
    }

    public void BringToTop() =>
        Native.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOSENDCHANGING);

    public void Hide()
    {
        if (!Visible) return;
        Native.ShowWindow(Handle, 0 /* SW_HIDE */);
        Visible = false;
    }

    /// <summary>Gets a look at every message first; return true to mark it handled.</summary>
    protected virtual bool OnMessage(ref Forms.Message m) => false;

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        if (m.Msg is >= 0x200 and <= 0x20E)
        {
            long lp = m.LParam.ToInt64();
            Message?.Invoke(m.Msg, (short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        }
        if (OnMessage(ref m)) return;
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
