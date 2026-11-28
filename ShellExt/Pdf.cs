using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Forms;
using SkiaSharp;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Aestoys.ShellExt;

/// <summary>PDF pages via Windows' built-in PDF renderer (Windows.Data.Pdf).</summary>
internal static class PdfRender
{
    public static PdfDocument Open(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(data.AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        return PdfDocument.LoadFromStreamAsync(stream).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>One page as a bitmap <paramref name="width"/> pixels wide, on white (PDF pages are paper).</summary>
    public static SKBitmap Page(PdfDocument doc, uint index, uint width)
    {
        using var page = doc.GetPage(index);
        using var output = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions { DestinationWidth = width, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
        page.RenderToStreamAsync(output, options).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[output.Size];
        output.Seek(0);
        output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask().GetAwaiter().GetResult();
        return SKBitmap.Decode(bytes) ?? throw new InvalidDataException("page didn't render");
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class PdfThumbnail : ThumbnailBase
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a08";
    protected override int MaxBytes => 256 << 20;

    protected override SKBitmap? Make(byte[] data, int size)
    {
        var doc = PdfRender.Open(data);
        if (doc.PageCount == 0) return null;
        using var page = doc.GetPage(0);
        // Width so that the longer side comes out at the requested size.
        double ratio = page.Size.Height > 0 ? page.Size.Width / page.Size.Height : 0.75;
        uint w = (uint)Math.Max(16, ratio >= 1 ? size : size * ratio);
        return PdfRender.Page(doc, 0, w);
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class PdfPreview : PreviewHandlerBase, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a09";
    private const int MaxPages = 30;
    protected override int MaxBytes => 256 << 20;

    protected override Control CreatePreview()
    {
        var doc = PdfRender.Open(Data);
        var pages = new List<Bitmap>();
        uint count = Math.Min(doc.PageCount, (uint)MaxPages);
        for (uint i = 0; i < count; i++)
        {
            using var bmp = PdfRender.Page(doc, i, 1100);
            pages.Add(Bitmaps.FromSkia(bmp));
        }
        var view = new PagesView(pages, Back, upscale: true);
        if (doc.PageCount <= MaxPages) return view;
        var panel = new Panel { BackColor = Back };
        var note = new Label { Text = $"Showing the first {MaxPages} of {doc.PageCount} pages", Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Fore, BackColor = Back, Font = new Font("Segoe UI", 9f) };
        view.Dock = DockStyle.Fill;
        panel.Controls.Add(view);
        panel.Controls.Add(note);
        return panel;
    }
}
