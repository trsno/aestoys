using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Aestoys.ShellExt;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref RECT prc);
    void SetRect(ref RECT prc);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr phwnd);
    [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
}

[ComImport, Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    void GetWindowContext(IntPtr pinfo);
    [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint color);
    void SetFont(IntPtr plf);
    void SetTextColor(uint color);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

[ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
    void GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppvSite);
}

/// <summary>
/// Shared plumbing for the preview pane: the shell hands us a stream and a parent window; the
/// subclass builds a WinForms control that is placed in that window.
/// </summary>
public abstract class PreviewHandlerBase : IPreviewHandler, IOleWindow, IObjectWithSite, IPreviewHandlerVisuals
{
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();

    private IntPtr _parent;
    private Rectangle _bounds;
    private object? _site;
    private Control? _control;

    protected byte[] Data { get; private set; } = Array.Empty<byte>();
    protected Color Back { get; private set; } = Theme.Dark ? Color.FromArgb(32, 32, 32) : Color.White;
    protected Color Fore { get; private set; } = Theme.Dark ? Color.FromArgb(230, 230, 230) : Color.Black;

    /// <summary>Builds the preview for <see cref="Data"/>.</summary>
    protected abstract Control CreatePreview();

    // Subclasses list IInitializeWithStream (most) or IInitializeWithFile (code, which needs the extension).
    public void Initialize(IStream pstream, uint grfMode)
    {
        Data = Native.ReadAll(pstream, MaxBytes);
        Marshal.ReleaseComObject(pstream);
    }

    protected string? FilePath { get; private set; }

    public void Initialize(string pszFilePath, uint grfMode)
    {
        FilePath = pszFilePath;
        using var fs = new FileStream(pszFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[(int)Math.Min(fs.Length, MaxBytes)];
        fs.ReadExactly(buf);
        Data = buf;
    }

    protected virtual int MaxBytes => 64 << 20;

    public void SetWindow(IntPtr hwnd, ref RECT prc)
    {
        _parent = hwnd;
        _bounds = ToRect(prc);
        if (_control is { IsDisposed: false })
        {
            SetParent(_control.Handle, _parent);
            _control.Bounds = _bounds;
        }
    }

    public void SetRect(ref RECT prc)
    {
        _bounds = ToRect(prc);
        if (_control is { IsDisposed: false }) _control.Bounds = _bounds;
    }

    public void DoPreview()
    {
        Control c;
        try { c = CreatePreview(); }
        catch (Exception e) { c = Message("This file can't be previewed: " + e.Message); }
        _control = c;
        c.Bounds = _bounds;
        _ = c.Handle;   // create the window before re-parenting it
        SetParent(c.Handle, _parent);
        c.Bounds = _bounds;
        c.Visible = true;
    }

    public void Unload()
    {
        _control?.Dispose();
        _control = null;
        Data = Array.Empty<byte>();
    }

    public void SetFocus() => _control?.Focus();

    public void QueryFocus(out IntPtr phwnd) => phwnd = GetFocus();

    public int TranslateAccelerator(ref MSG pmsg)
    {
        if (_site is IPreviewHandlerFrame frame) return frame.TranslateAccelerator(ref pmsg);
        return 1;   // S_FALSE
    }

    public void GetWindow(out IntPtr phwnd) => phwnd = _parent;
    public void ContextSensitiveHelp(bool fEnterMode) { }

    public void SetSite(object? pUnkSite) => _site = pUnkSite;
    public void GetSite(ref Guid riid, out object? ppvSite) => ppvSite = _site;

    public void SetBackgroundColor(uint color) => Back = ColorTranslator.FromWin32((int)color);
    public void SetFont(IntPtr plf) { }
    public void SetTextColor(uint color) => Fore = ColorTranslator.FromWin32((int)color);

    private static Rectangle ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    protected Control Message(string text) => new Label
    {
        Text = text, TextAlign = ContentAlignment.MiddleCenter, BackColor = Back, ForeColor = Fore,
        Font = new Font("Segoe UI", 10f), AutoSize = false,
    };
}

internal static class Theme
{
    /// <summary>Windows "app mode" (the preview pane follows it).</summary>
    public static bool Dark
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }
}
