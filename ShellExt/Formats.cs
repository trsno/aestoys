using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SkiaSharp;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Aestoys.ShellExt;

/// <summary>Per-user options written by aestoys (HKCU\Software\aestoys\ExplorerAddons).</summary>
internal static class Options
{
    private static object? Get(string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\aestoys\ExplorerAddons");
            return k?.GetValue(name);
        }
        catch { return null; }
    }

    public static string String(string name, string fallback) => Get(name) as string ?? fallback;
    public static int Int(string name, int fallback) => Get(name) is int i ? i : fallback;
    public static bool Bool(string name, bool fallback) => Get(name) is int i ? i != 0 : fallback;
}

// ------------------------------------------------------------------------------------------ QOI

/// <summary>Decoder for the "Quite OK Image" format (qoiformat.org).</summary>
internal static class Qoi
{
    public static SKBitmap Decode(byte[] d)
    {
        if (d.Length < 22 || d[0] != 'q' || d[1] != 'o' || d[2] != 'i' || d[3] != 'f') throw new InvalidDataException("not a QOI image");
        int w = (d[4] << 24) | (d[5] << 16) | (d[6] << 8) | d[7];
        int h = (d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11];
        if (w <= 0 || h <= 0 || (long)w * h > 400_000_000) throw new InvalidDataException("bad QOI size");
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var px = new byte[w * h * 4];
        var index = new byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        int p = 14, run = 0, end = d.Length - 8;
        for (int o = 0; o < px.Length; o += 4)
        {
            if (run > 0) run--;
            else if (p < end)
            {
                int b1 = d[p++];
                if (b1 == 0xFE) { r = d[p++]; g = d[p++]; b = d[p++]; }
                else if (b1 == 0xFF) { r = d[p++]; g = d[p++]; b = d[p++]; a = d[p++]; }
                else if ((b1 & 0xC0) == 0x00) { int i = (b1 & 0x3F) * 4; r = index[i]; g = index[i + 1]; b = index[i + 2]; a = index[i + 3]; }
                else if ((b1 & 0xC0) == 0x40) { r += (byte)(((b1 >> 4) & 3) - 2); g += (byte)(((b1 >> 2) & 3) - 2); b += (byte)((b1 & 3) - 2); }
                else if ((b1 & 0xC0) == 0x80)
                {
                    int b2 = d[p++], vg = (b1 & 0x3F) - 32;
                    r += (byte)(vg - 8 + ((b2 >> 4) & 0x0F)); g += (byte)vg; b += (byte)(vg - 8 + (b2 & 0x0F));
                }
                else run = b1 & 0x3F;
                int k = ((r * 3 + g * 5 + b * 7 + a * 11) % 64) * 4;
                index[k] = r; index[k + 1] = g; index[k + 2] = b; index[k + 3] = a;
            }
            px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = a;
        }
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }
}

internal static class Images
{
    /// <summary>PNG, JPEG or QOI bytes to a bitmap.</summary>
    public static SKBitmap Decode(byte[] data)
    {
        if (data.Length > 4 && data[0] == 'q' && data[1] == 'o' && data[2] == 'i' && data[3] == 'f') return Qoi.Decode(data);
        return SKBitmap.Decode(data) ?? throw new InvalidDataException("unreadable image");
    }
}

// ------------------------------------------------------------------------------------------ G-code

/// <summary>Thumbnails that slicers (PrusaSlicer, Cura, OrcaSlicer…) embed in .gcode and .bgcode files.</summary>
internal static class GCode
{
    /// <summary>The largest embedded thumbnail, or null.</summary>
    public static SKBitmap? Thumbnail(byte[] file)
    {
        var images = file.Length > 10 && file[0] == 'G' && file[1] == 'C' && file[2] == 'D' && file[3] == 'E' ? FromBinary(file) : FromText(file);
        SKBitmap? best = null;
        foreach (var img in images)
        {
            try
            {
                var bmp = Images.Decode(img);
                if (best is null || bmp.Width * bmp.Height > best.Width * best.Height) { best?.Dispose(); best = bmp; }
                else bmp.Dispose();
            }
            catch { }
        }
        return best;
    }

    /// <summary>"; thumbnail[_PNG|_JPG|_QOI] begin WxH len" … base64 lines … "; thumbnail… end".</summary>
    private static IEnumerable<byte[]> FromText(byte[] file)
    {
        // Thumbnails are in the header; don't scan a whole 100 MB print.
        string text = Encoding.ASCII.GetString(file, 0, Math.Min(file.Length, 4 << 20));
        var lines = text.Split('\n');
        StringBuilder? b64 = null;
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (!line.StartsWith(';')) { if (b64 is null) continue; else { b64 = null; continue; } }
            string body = line.TrimStart(';', ' ');
            if (body.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) && body.Contains(" begin")) { b64 = new StringBuilder(); continue; }
            if (body.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) && body.Contains(" end"))
            {
                byte[]? bytes = null;
                if (b64 is not null) { try { bytes = Convert.FromBase64String(b64.ToString()); } catch { } }
                b64 = null;
                if (bytes is not null) yield return bytes;
                continue;
            }
            b64?.Append(body.Trim());
        }
    }

    /// <summary>Prusa binary G-code: thumbnail blocks (type 5) hold PNG, JPG or QOI data.</summary>
    private static IEnumerable<byte[]> FromBinary(byte[] f)
    {
        int checksum = BitConverter.ToUInt16(f, 8) == 1 ? 4 : 0;   // CRC32 after each block
        int p = 10;
        while (p + 8 <= f.Length)
        {
            int type = BitConverter.ToUInt16(f, p), compression = BitConverter.ToUInt16(f, p + 2);
            int size = BitConverter.ToInt32(f, p + 4);
            int header = 8;
            if (compression != 0) { size = BitConverter.ToInt32(f, p + 8); header = 12; }
            int paramSize = type == 5 ? 6 : 2;
            int dataStart = p + header + paramSize;
            if (size < 0 || dataStart + size > f.Length) yield break;
            if (type == 5 && compression == 0) yield return f[dataStart..(dataStart + size)];
            if (type == 1) yield break;   // G-code blocks follow the thumbnails; nothing more to find
            p = dataStart + size + checksum;
        }
    }
}

// ------------------------------------------------------------------------------------------ STL

/// <summary>Draws an STL mesh (binary or ASCII) as a shaded isometric picture.</summary>
internal static class Stl
{
    public static List<(Vector3 a, Vector3 b, Vector3 c)> Read(byte[] f)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        bool binary = f.Length >= 84 && 84 + (long)BitConverter.ToUInt32(f, 80) * 50 == f.Length;
        if (binary)
        {
            int n = (int)BitConverter.ToUInt32(f, 80);
            for (int i = 0, p = 84; i < n; i++, p += 50)
                tris.Add((V(f, p + 12), V(f, p + 24), V(f, p + 36)));
            return tris;
        }
        var verts = new List<Vector3>(3);
        foreach (var raw in Encoding.ASCII.GetString(f).Split('\n'))
        {
            var t = raw.Trim();
            if (!t.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            verts.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
            if (verts.Count == 3) { tris.Add((verts[0], verts[1], verts[2])); verts.Clear(); }
        }
        return tris;
    }

    private static Vector3 V(byte[] f, int p) => new(BitConverter.ToSingle(f, p), BitConverter.ToSingle(f, p + 4), BitConverter.ToSingle(f, p + 8));
    private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    public static SKBitmap Render(byte[] file, int size, SKColor color)
    {
        var tris = Read(file);
        if (tris.Count == 0) throw new InvalidDataException("no triangles");
        // Isometric-style view: turn 45° around Z (models are Z-up), then tilt the camera down.
        var rot = Matrix4x4.CreateRotationZ(MathF.PI / 4) * Matrix4x4.CreateRotationX(-MathF.PI / 3.2f);
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var t = new (Vector3 a, Vector3 b, Vector3 c)[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            t[i] = (Vector3.Transform(a, rot), Vector3.Transform(b, rot), Vector3.Transform(c, rot));
            min = Vector3.Min(min, Vector3.Min(t[i].a, Vector3.Min(t[i].b, t[i].c)));
            max = Vector3.Max(max, Vector3.Max(t[i].a, Vector3.Max(t[i].b, t[i].c)));
        }
        float span = Math.Max(max.X - min.X, max.Y - min.Y);
        if (span <= 0) span = 1;
        float scale = size * 0.92f / span;
        float ox = (size - (max.X - min.X) * scale) / 2, oy = (size - (max.Y - min.Y) * scale) / 2;
        SKPoint P(Vector3 v) => new(ox + (v.X - min.X) * scale, size - (oy + (v.Y - min.Y) * scale));

        // Painter's algorithm: farthest triangles first (camera looks down -Z after the rotation).
        Array.Sort(t, (x, y) => (x.a.Z + x.b.Z + x.c.Z).CompareTo(y.a.Z + y.b.Z + y.c.Z));
        var light = Vector3.Normalize(new Vector3(-0.4f, 0.5f, 1f));
        var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 0.6f };
        using var path = new SKPath();
        foreach (var (a, b, c) in t)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-12f) continue;
            n = Vector3.Normalize(n);
            float shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(n, light));
            paint.Color = new SKColor((byte)(color.Red * shade), (byte)(color.Green * shade), (byte)(color.Blue * shade));
            path.Reset();
            path.MoveTo(P(a)); path.LineTo(P(b)); path.LineTo(P(c)); path.Close();
            canvas.DrawPath(path, paint);
        }
        return bmp;
    }
}

// ------------------------------------------------------------------------------------------ handlers

/// <summary>Base for thumbnail providers that turn the file into a bitmap.</summary>
public abstract class ThumbnailBase : IThumbnailProvider, IInitializeWithStream
{
    private byte[]? _data;

    public void Initialize(IStream pstream, uint grfMode)
    {
        _data = Native.ReadAll(pstream, MaxBytes);
        Marshal.ReleaseComObject(pstream);
    }

    protected virtual int MaxBytes => 64 << 20;

    protected abstract SKBitmap? Make(byte[] data, int size);

    public void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
    {
        pdwAlpha = Native.WTSAT_ARGB;
        phbmp = IntPtr.Zero;
        using var bmp = Make(_data ?? Array.Empty<byte>(), (int)Math.Max(16, cx)) ?? throw new COMException("no thumbnail", unchecked((int)0x80004005));
        phbmp = Bitmaps.ThumbnailFrom(bmp, (int)cx);
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class QoiThumbnail : ThumbnailBase
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a03";
    protected override SKBitmap? Make(byte[] data, int size) => Qoi.Decode(data);
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class QoiPreview : PreviewHandlerBase, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a04";
    protected override Control CreatePreview()
    {
        using var bmp = Qoi.Decode(Data);
        return new PagesView(new List<Bitmap> { Bitmaps.FromSkia(bmp) }, Back);
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class GCodeThumbnail : ThumbnailBase
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a05";
    protected override int MaxBytes => 8 << 20;   // thumbnails sit at the start of the file
    protected override SKBitmap? Make(byte[] data, int size) => GCode.Thumbnail(data);
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class GCodePreview : PreviewHandlerBase, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a06";
    protected override int MaxBytes => 8 << 20;
    protected override Control CreatePreview()
    {
        using var bmp = GCode.Thumbnail(Data);
        if (bmp is null) return Message("This G-code file has no embedded preview image.");
        return new PagesView(new List<Bitmap> { Bitmaps.FromSkia(bmp) }, Back);
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class StlThumbnail : ThumbnailBase
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a07";
    protected override int MaxBytes => 256 << 20;
    protected override SKBitmap? Make(byte[] data, int size)
    {
        var color = SKColor.TryParse(Options.String("StlColor", "#FFC924"), out var c) ? c : new SKColor(0xFF, 0xC9, 0x24);
        return Stl.Render(data, size, color);
    }
}
