using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;
using Forms = System.Windows.Forms;

namespace Aestoys.Features.Tools;

/// <summary>
/// Crop And Lock, thumbnail mode (PowerToys, MIT): Win+Ctrl+Shift+T, then drag a rectangle over the
/// active window. A new window shows a live DWM thumbnail of just that region; it closes when
/// the source window closes. Esc or right-click cancels the selection.
/// </summary>
internal sealed class CropAndLock : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private LayeredWindow? _overlay;
    private volatile bool _selecting;
    private IntPtr _target;
    private Native.RECT _bounds;          // target window on screen (visible frame)
    private Point _start, _end;
    private bool _dragging, _redrawQueued;
    private readonly List<CropWindow> _windows = new();

    private CropAndLockSettings S => _settings.Tools.CropAndLock;

    public CropAndLock(AppSettings settings, HotkeyRouter hotkeys, KeyboardHook keyboard, MouseHook mouse)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        hotkeys.Register(() => S.ThumbnailHotkey, () => S.Enabled, Begin);
        keyboard.Add(OnKey);
        mouse.Add(OnMouse);
    }

    public void Begin()
    {
        if (_selecting) return;
        IntPtr fg = Win32.GetAncestor(Native.GetForegroundWindow(), 2 /* GA_ROOT */);
        if (fg == IntPtr.Zero || Win32.ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        if (Native.DwmGetWindowAttribute(fg, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0) return;
        _target = fg;
        _bounds = r;
        _dragging = false;
        _overlay ??= new LayeredWindow(clickThrough: true);
        _selecting = true;
        Redraw();
    }

    private void Cancel()
    {
        _selecting = false;
        _dragging = false;
        _overlay?.Hide();
    }

    // ------------------------------------------------------------------ input (hook threads)

    private bool OnKey(int vk, bool down, bool alt, bool injected)
    {
        if (!_selecting || injected || vk != 0x1B) return false;
        if (down) _ui.BeginInvoke(Cancel);
        return true;
    }

    private bool OnMouse(int msg, Native.POINT pt, int wheel, bool injected)
    {
        if (!_selecting || injected) return false;
        var p = new Point(pt.X, pt.Y);
        switch (msg)
        {
            case MouseHook.WM_LBUTTONDOWN:
                _ui.BeginInvoke(() => { _start = _end = Clamp(p); _dragging = true; Redraw(); });
                return true;
            case MouseHook.WM_MOUSEMOVE:
                if (_dragging) _ui.BeginInvoke(() => { _end = Clamp(p); QueueRedraw(); });
                return false;
            case MouseHook.WM_LBUTTONUP:
                _ui.BeginInvoke(() => { _end = Clamp(p); Finish(); });
                return true;
            case MouseHook.WM_RBUTTONDOWN:
                _ui.BeginInvoke(Cancel);
                return true;
            case MouseHook.WM_RBUTTONUP or MouseHook.WM_MBUTTONDOWN or MouseHook.WM_MBUTTONUP:
                return true;
        }
        return false;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, _bounds.Left, _bounds.Right), Math.Clamp(p.Y, _bounds.Top, _bounds.Bottom));

    private Rectangle Selection => Rectangle.FromLTRB(Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y), Math.Max(_start.X, _end.X), Math.Max(_start.Y, _end.Y));

    private void Finish()
    {
        if (!_selecting) return;
        var sel = Selection;
        Cancel();
        if (sel.Width < 8 || sel.Height < 8 || !Native.IsWindow(_target)) return;
        var w = new CropWindow(_target, sel);
        w.FormClosed += (_, _) => _windows.Remove(w);
        _windows.Add(w);
        w.Show();
    }

    // ------------------------------------------------------------------ overlay

    private void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Render, () => { _redrawQueued = false; Redraw(); });
    }

    private void Redraw()
    {
        if (!_selecting || _overlay is null) return;
        int w = _bounds.Width, h = _bounds.Height;
        if (w <= 0 || h <= 0) return;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var dim = Color.FromArgb(110, 0, 0, 0);
            var sel = Selection;
            sel.Offset(-_bounds.Left, -_bounds.Top);
            using var brush = new SolidBrush(dim);
            if (_dragging && sel.Width > 0 && sel.Height > 0)
            {
                using var region = new Region(new Rectangle(0, 0, w, h));
                region.Exclude(sel);
                g.FillRegion(brush, region);
                var accent = System.Windows.SystemParameters.WindowGlassColor;
                using var pen = new Pen(Color.FromArgb(255, accent.R, accent.G, accent.B), 2) { Alignment = PenAlignment.Inset };
                g.DrawRectangle(pen, sel);
                Label(g, $"{sel.Width} × {sel.Height}", sel.Left, sel.Bottom + 6, h);
            }
            else
            {
                g.FillRectangle(brush, 0, 0, w, h);
                Label(g, "Drag to choose the part to keep · Esc to cancel", 16, 16, h);
            }
        }
        _overlay.Update(bmp, _bounds.Left, _bounds.Top);
    }

    private static void Label(Graphics g, string text, int x, int y, int maxY)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Variable Text", 13, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        if (y + size.Height + 8 > maxY) y = maxY - (int)size.Height - 12;
        using var path = Toast.RoundRect(x, y, size.Width + 16, size.Height + 8, 6);
        using var bg = new SolidBrush(Color.FromArgb(230, 32, 32, 32));
        g.FillPath(bg, path);
        using var fg = new SolidBrush(Color.White);
        g.DrawString(text, font, fg, x + 8, y + 4);
    }

    public void Dispose()
    {
        Cancel();
        _overlay?.Dispose();
        foreach (var w in _windows.ToArray()) w.Close();
    }
}

/// <summary>A normal window showing a live DWM thumbnail of part of another window.</summary>
internal sealed class CropWindow : Forms.Form
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbProps
    {
        public uint dwFlags;
        public Native.RECT rcDestination, rcSource;
        public byte opacity;
        public bool fVisible, fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);
    private const uint DWM_TNP_RECTDESTINATION = 1, DWM_TNP_RECTSOURCE = 2, DWM_TNP_OPACITY = 4, DWM_TNP_VISIBLE = 8, DWM_TNP_SOURCECLIENTAREAONLY = 16;

    private readonly IntPtr _source;
    private readonly Rectangle _crop;        // screen coordinates at the time of cropping
    private Native.RECT _sourceOrigin;       // source window rect then (thumbnail source coords are relative to it)
    private IntPtr _thumb;
    private readonly Forms.Timer _watch = new() { Interval = 500 };

    public CropWindow(IntPtr source, Rectangle crop)
    {
        _source = source;
        _crop = crop;
        Native.GetWindowRect(source, out _sourceOrigin);
        Text = "Crop of " + Native.GetWindowTitle(source);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = Color.Black;
        FormBorderStyle = Forms.FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        AutoScaleMode = Forms.AutoScaleMode.None;
        Location = new Point(crop.Left + 24, crop.Top + 24);
        ClientSize = crop.Size;
        _watch.Tick += (_, _) => { if (!Native.IsWindow(_source)) Close(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        uint dark = 1;
        Win32.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        if (DwmRegisterThumbnail(Handle, _source, out _thumb) == 0) UpdateThumb();
        _watch.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        if (_thumb == IntPtr.Zero) return;
        var client = ClientSize;
        // Keep the crop's aspect ratio, centred.
        double scale = Math.Min(client.Width / (double)_crop.Width, client.Height / (double)_crop.Height);
        int w = (int)(_crop.Width * scale), h = (int)(_crop.Height * scale);
        int x = (client.Width - w) / 2, y = (client.Height - h) / 2;
        var props = new ThumbProps
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            rcSource = new Native.RECT
            {
                Left = _crop.Left - _sourceOrigin.Left, Top = _crop.Top - _sourceOrigin.Top,
                Right = _crop.Right - _sourceOrigin.Left, Bottom = _crop.Bottom - _sourceOrigin.Top,
            },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    protected override void OnFormClosed(Forms.FormClosedEventArgs e)
    {
        _watch.Stop();
        if (_thumb != IntPtr.Zero) DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
        base.OnFormClosed(e);
    }
}
