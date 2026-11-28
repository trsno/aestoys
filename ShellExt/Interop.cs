using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Aestoys.ShellExt;

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    public const uint WTSAT_ARGB = 2;

    /// <summary>Whole stream into memory (shell streams are files; cap at 64 MB).</summary>
    public static byte[] ReadAll(IStream stream, int max = 64 << 20)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        IntPtr read = Marshal.AllocHGlobal(4);
        try
        {
            while (ms.Length < max)
            {
                stream.Read(buf, buf.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
        }
        finally { Marshal.FreeHGlobal(read); }
        return ms.ToArray();
    }

    /// <summary>Top-down 32-bit DIB from premultiplied BGRA pixels; the caller (the shell) frees it.</summary>
    public static unsafe IntPtr ToHBitmap(int w, int h, ReadOnlySpan<byte> bgraPremul)
    {
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr hbmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero) throw new OutOfMemoryException("CreateDIBSection failed");
        bgraPremul[..(w * h * 4)].CopyTo(new Span<byte>((void*)bits, w * h * 4));
        return hbmp;
    }
}
