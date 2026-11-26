using System;
using System.Drawing;
using System.Drawing.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aestoys.Interop;

namespace Aestoys.Features.AltTab;

/// <summary>Snapshots window contents (PrintWindow) for the Coverflow previews.</summary>
internal static class WindowCapture
{
    private const int MaxSide = 1600;

    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.IsHungAppWindow(hwnd)) return null;
        if (!Native.GetWindowRect(hwnd, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;

        // Crop off the invisible resize borders / shadow.
        var fr = wr;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT f, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) == 0)
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

            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(cw, ch));
            int ow = Math.Max(1, (int)(cw * scale)), oh = Math.Max(1, (int)(ch * scale));
            using var outBmp = new Bitmap(ow, oh, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, new Rectangle(0, 0, ow, oh), new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
            }

            var data = outBmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var src = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * oh, data.Stride);
                src.Freeze();
                return IsBlank(src) ? null : src;
            }
            finally { outBmp.UnlockBits(data); }
        }
        catch { return null; }
    }

    /// <summary>Some windows (protected content, elevated apps) come back fully black.</summary>
    private static bool IsBlank(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new int[1];
        for (int i = 1; i <= 7; i++)
            for (int j = 1; j <= 7; j++)
            {
                src.CopyPixels(new Int32Rect(w * i / 8, h * j / 8, 1, 1), px, 4, 0);
                if ((px[0] & 0xFFFFFF) != 0) return false;
            }
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
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x38, 0x38, 0x38)), null, new Rect(0, 0, w, 32));
            if (item.Icon is not null) dc.DrawImage(item.Icon, new Rect(w / 2.0 - 48, h / 2.0 - 48, 96, 96));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
