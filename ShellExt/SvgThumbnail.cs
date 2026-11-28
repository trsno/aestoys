using System.Runtime.InteropServices;
using SkiaSharp;
using Svg.Skia;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Aestoys.ShellExt;

/// <summary>File Explorer thumbnails for .svg files (replaces PowerToys' SVG thumbnail provider).</summary>
[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class SvgThumbnail : IThumbnailProvider, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a01";

    private byte[]? _data;

    public void Initialize(IStream pstream, uint grfMode)
    {
        _data = Native.ReadAll(pstream);
        Marshal.ReleaseComObject(pstream);
    }

    public void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = Native.WTSAT_ARGB;
        if (_data is null || _data.Length == 0) throw new COMException("no data", unchecked((int)0x80004005));

        using var svg = new SKSvg();
        using (var ms = new MemoryStream(_data)) svg.Load(ms);
        var picture = svg.Picture ?? throw new COMException("not an SVG", unchecked((int)0x80004005));
        phbmp = Render.PictureToHBitmap(picture, (int)Math.Max(16, cx));
    }
}

internal static class Render
{
    /// <summary>Fits the picture into size × size (keeping its shape) on a transparent background.</summary>
    public static IntPtr PictureToHBitmap(SKPicture picture, int size)
    {
        var r = picture.CullRect;
        float pw = r.Width > 0 ? r.Width : size, ph = r.Height > 0 ? r.Height : size;
        float scale = size / Math.Max(pw, ph);
        int w = Math.Max(1, (int)Math.Round(pw * scale)), h = Math.Max(1, (int)Math.Round(ph * scale));
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-r.Left, -r.Top);
            canvas.DrawPicture(picture);
        }
        return Native.ToHBitmap(w, h, bmp.GetPixelSpan());
    }
}
