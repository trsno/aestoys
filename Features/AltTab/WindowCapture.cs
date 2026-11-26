using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aestoys.Interop;

namespace Aestoys.Features.AltTab;

/// <summary>
/// Snapshots window contents (PrintWindow) for the Coverflow previews. <see cref="Capture"/> is
/// thread-safe and meant to run off the UI thread; results are frozen and cached per window so
/// the next switcher opens with the last snapshot straight away.
/// </summary>
internal static class WindowCapture
{
    private const int MaxCached = 40;
    private static readonly ConcurrentDictionary<IntPtr, BitmapSource> Cache = new();

    /// <summary>Last snapshot of <paramref name="hwnd"/>, if its shape still roughly matches.</summary>
    public static BitmapSource? Cached(IntPtr hwnd, double aspect)
    {
        if (!Cache.TryGetValue(hwnd, out var bmp)) return null;
        double a = (double)bmp.PixelWidth / bmp.PixelHeight;
        return Math.Abs(a / aspect - 1) < 0.08 ? bmp : null;
    }

    /// <summary>Drops snapshots of windows that no longer exist and keeps the cache bounded.</summary>
    public static void Prune()
    {
        foreach (var hwnd in Cache.Keys)
            if (!Native.IsWindow(hwnd)) Cache.TryRemove(hwnd, out _);
        if (Cache.Count > MaxCached)
            foreach (var hwnd in Cache.Keys.Take(Cache.Count - MaxCached)) Cache.TryRemove(hwnd, out _);
    }

    public static BitmapSource? Capture(IntPtr hwnd, int maxSide)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.IsHungAppWindow(hwnd)) return null;
        if (!Native.GetWindowRect(hwnd, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;

        // Crop off the invisible resize borders / shadow.
        var fr = wr;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT f, Marshal.SizeOf<Native.RECT>()) == 0)
            fr = f;
        int cx = Math.Max(0, fr.Left - wr.Left), cy = Math.Max(0, fr.Top - wr.Top);
        int cw = Math.Min(fr.Width, wr.Width - cx), ch = Math.Min(fr.Height, wr.Height - cy);
        if (cw <= 0 || ch <= 0) return null;

        try
        {
            using var full = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) return null;
            }

            // Scale down to roughly the size it is shown at; bigger textures only cost upload time.
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(cw, ch));
            int ow = Math.Max(1, (int)(cw * scale)), oh = Math.Max(1, (int)(ch * scale));
            using var outBmp = new Bitmap(ow, oh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = scale < 1 ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, ow, oh), new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
            }

            var data = outBmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                if (IsBlank(data.Scan0, data.Stride, ow, oh)) return null;
                RoundCorners(data.Scan0, data.Stride, ow, oh);
                var src = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * oh, data.Stride);
                src.Freeze();
                Cache[hwnd] = src;
                return src;
            }
            finally { outBmp.UnlockBits(data); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Windows 11-style rounded corners, done in the pixel buffer (premultiplied BGRA): every pixel
    /// is made opaque, then the corner pixels are scaled by how much of them lies inside the arc.
    /// </summary>
    private static void RoundCorners(IntPtr scan0, int stride, int w, int h)
    {
        int r = (int)Math.Ceiling(Math.Max(8, Math.Min(w, h) * 0.02));
        r = Math.Min(r, Math.Min(w, h) / 2);
        for (int y = 0; y < r; y++)
            for (int x = 0; x < r; x++)
            {
                // Distance from the arc centre, sampled at the pixel centre; 1px soft edge.
                double dx = r - (x + 0.5), dy = r - (y + 0.5);
                double cover = Math.Clamp(r - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                if (cover >= 1) continue;
                Scale(scan0, stride, x, y, cover);
                Scale(scan0, stride, w - 1 - x, y, cover);
                Scale(scan0, stride, x, h - 1 - y, cover);
                Scale(scan0, stride, w - 1 - x, h - 1 - y, cover);
            }

        static void Scale(IntPtr scan0, int stride, int x, int y, double cover)
        {
            int off = y * stride + x * 4;
            uint px = (uint)Marshal.ReadInt32(scan0, off) | 0xFF000000u;
            uint b = (uint)((px & 0xFF) * cover), g = (uint)(((px >> 8) & 0xFF) * cover),
                 rr = (uint)(((px >> 16) & 0xFF) * cover), a = (uint)(255 * cover);
            Marshal.WriteInt32(scan0, off, (int)(b | g << 8 | rr << 16 | a << 24));
        }
    }

    /// <summary>Some windows (protected content, elevated apps) come back fully black.</summary>
    private static bool IsBlank(IntPtr scan0, int stride, int w, int h)
    {
        for (int i = 1; i <= 7; i++)
            for (int j = 1; j <= 7; j++)
                if ((Marshal.ReadInt32(scan0, h * j / 8 * stride + w * i / 8 * 4) & 0xFFFFFF) != 0) return false;
        return true;
    }

    /// <summary>Card used when a window can't be captured (minimized, hung, protected).</summary>
    public static BitmapSource Placeholder(SwitchItem item, double aspect = 1.6)
    {
        const int h = 300;
        int w = (int)(h * aspect);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 12, 12));
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0x38, 0x38)), null, new Rect(0, 0, w, 32));
            dc.Pop();
            if (item.Icon is not null) dc.DrawImage(item.Icon, new Rect(w / 2.0 - 48, h / 2.0 - 48, 96, 96));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
