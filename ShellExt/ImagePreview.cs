using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkiaSharp;
using Svg.Skia;

namespace Aestoys.ShellExt;

/// <summary>Scrollable column of images (PDF pages, a picture), each scaled to fit the pane's width.</summary>
internal sealed class PagesView : ScrollableControl
{
    private readonly List<Bitmap> _pages;
    private readonly bool _upscale;
    private const int Gap = 10;

    public PagesView(List<Bitmap> pages, Color back, bool upscale = false)
    {
        _pages = pages;
        _upscale = upscale;
        BackColor = back;
        AutoScroll = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Resize += (_, _) => Layout();
    }

    private int ContentWidth => Math.Max(32, ClientSize.Width - 2 * Gap);

    private Size SizeOf(Bitmap b)
    {
        double s = (double)ContentWidth / b.Width;
        if (!_upscale) s = Math.Min(1, s);
        if (_pages.Count == 1) s = Math.Min(s, (double)Math.Max(32, ClientSize.Height - 2 * Gap) / b.Height);   // a single picture fits entirely
        return new Size(Math.Max(1, (int)(b.Width * s)), Math.Max(1, (int)(b.Height * s)));
    }

    private new void Layout()
    {
        int h = Gap;
        foreach (var p in _pages) h += SizeOf(p).Height + Gap;
        AutoScrollMinSize = new Size(0, _pages.Count == 1 ? 0 : h);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int y = Gap + AutoScrollPosition.Y;
        foreach (var p in _pages)
        {
            var s = SizeOf(p);
            int x = (ClientSize.Width - s.Width) / 2;
            int top = _pages.Count == 1 ? (ClientSize.Height - s.Height) / 2 : y;
            if (top + s.Height >= 0 && top <= ClientSize.Height) g.DrawImage(p, new Rectangle(x, top, s.Width, s.Height));
            y += s.Height + Gap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var p in _pages) p.Dispose();
        base.Dispose(disposing);
    }
}

internal static class Bitmaps
{
    /// <summary>SkiaSharp bitmap (any format) to a System.Drawing bitmap.</summary>
    public static Bitmap FromSkia(SKBitmap src)
    {
        using var converted = src.ColorType == SKColorType.Bgra8888 && src.AlphaType == SKAlphaType.Premul
            ? null : src.Copy(SKColorType.Bgra8888);
        var s = converted ?? src;
        var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, s.Width, s.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var span = s.GetPixelSpan();
            for (int row = 0; row < s.Height; row++)
                Marshal.Copy(span.Slice(row * s.RowBytes, s.Width * 4).ToArray(), 0, data.Scan0 + row * data.Stride, s.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>HBITMAP for a thumbnail, scaled to fit <paramref name="size"/>.</summary>
    public static IntPtr ThumbnailFrom(SKBitmap src, int size)
    {
        double s = Math.Min(1.0, (double)size / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * s)), h = Math.Max(1, (int)Math.Round(src.Height * s));
        using var scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Transparent);
            using var img = SKImage.FromBitmap(src);
            canvas.DrawImage(img, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        return Native.ToHBitmap(w, h, scaled.GetPixelSpan());
    }
}

/// <summary>Preview pane for .svg files.</summary>
[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class SvgPreview : PreviewHandlerBase, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a02";

    protected override Control CreatePreview()
    {
        using var svg = new SKSvg();
        using (var ms = new MemoryStream(Data)) svg.Load(ms);
        var pic = svg.Picture ?? throw new InvalidDataException("not a valid SVG");
        var r = pic.CullRect;
        // Render large enough to stay sharp in a big preview pane.
        float scale = 1600f / Math.Max(1, Math.Max(r.Width, r.Height));
        int w = Math.Max(1, (int)(r.Width * scale)), h = Math.Max(1, (int)(r.Height * scale));
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-r.Left, -r.Top);
            canvas.DrawPicture(pic);
        }
        return new PagesView(new List<Bitmap> { Bitmaps.FromSkia(bmp) }, Back, upscale: true);
    }
}
