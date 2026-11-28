using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Clipboard;

/// <summary>
/// Clipboard history (replaces Ditto). Every copy is saved with the formats Ditto saves by
/// default (text, Unicode text, rich text, HTML, images as DIB and PNG, file lists), exact bytes.
/// Win+V opens the quick paste list; picking a clip puts all its formats back on the clipboard and
/// pastes into the window that was active. Copies marked as private by the source app (password
/// managers set "ExcludeClipboardContentFromMonitorProcessing" or "CanIncludeInClipboardHistory"
/// = 0) are not recorded, and neither are aestoys' own clipboard writes.
/// </summary>
internal sealed class ClipboardHistory : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_TEXT = 1, CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15, GMEM_MOVEABLE = 2;
    private const int MaxFormatBytes = 64 << 20;

    private static readonly Dictionary<uint, string> Standard = new() { [CF_TEXT] = "CF_TEXT", [CF_DIB] = "CF_DIB", [CF_UNICODETEXT] = "CF_UNICODETEXT", [CF_HDROP] = "CF_HDROP" };
    private static readonly string[] Registered = { "Rich Text Format", "HTML Format", "PNG" };

    private readonly AppSettings _settings;
    private readonly HwndSource _window;
    private readonly uint _excludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _viewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private readonly uint _canInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private uint _ownSequence;
    private QuickPasteWindow? _quick;

    public ClipboardStore Store { get; }

    private ClipboardSettings S => _settings.Tools.Clipboard;

    public ClipboardHistory(AppSettings settings, HotkeyRouter hotkeys)
    {
        _settings = settings;
        Store = new ClipboardStore(ClipboardStore.DefaultPath);
        var p = new HwndSourceParameters("aestoys clipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        hotkeys.Register(() => S.Hotkey, () => S.Enabled, Show);
        ApplySettings();
    }

    private bool _listening;

    public void ApplySettings()
    {
        if (S.Enabled && !_listening) _listening = AddClipboardFormatListener(_window.Handle);
        else if (!S.Enabled && _listening) { RemoveClipboardFormatListener(_window.Handle); _listening = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { Capture(); } catch (Exception e) { App.Log("clipboard: " + e.Message); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ recording

    private void Capture()
    {
        if (!S.Enabled) return;
        if (GetClipboardSequenceNumber() == _ownSequence) return;
        if (!Open()) return;
        List<ClipFormat> formats;
        try
        {
            if (IsClipboardFormatAvailable(_excludeMonitor) || IsClipboardFormatAvailable(_viewerIgnore)) return;
            if (IsClipboardFormatAvailable(_canInclude) && Read(_canInclude) is { Length: >= 4 } inc && BitConverter.ToInt32(inc, 0) == 0) return;

            formats = new List<ClipFormat>();
            // Ditto's order: the first format is what dedupe compares.
            foreach (uint f in new[] { CF_UNICODETEXT, CF_TEXT })
                if (IsClipboardFormatAvailable(f) && Read(f) is { } b) formats.Add(new ClipFormat(Standard[f], b));
            foreach (var name in Registered)
            {
                uint id = RegisterClipboardFormat(name);
                if (IsClipboardFormatAvailable(id) && Read(id) is { } b) formats.Add(new ClipFormat(name, b));
            }
            if (IsClipboardFormatAvailable(CF_HDROP) && Read(CF_HDROP) is { } drop) formats.Add(new ClipFormat("CF_HDROP", drop));
            // CF_DIB is also synthesised by Windows from CF_BITMAP, so screenshots are caught too.
            if (IsClipboardFormatAvailable(CF_DIB) && Read(CF_DIB) is { } dib) formats.Add(new ClipFormat("CF_DIB", dib));
        }
        finally { CloseClipboard(); }

        if (formats.Count == 0) return;
        string text = Describe(formats);
        int max = S.MaxEntries;
        Task.Run(() =>
        {
            try { Store.Add(text, formats, max); _quick?.Dispatcher.BeginInvoke(() => _quick.ReloadIfVisible()); }
            catch (Exception e) { App.Log("clipboard save: " + e.Message); }
        });
    }

    private static bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static byte[]? Read(uint format)
    {
        IntPtr h = GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        long size = (long)GlobalSize(h).ToUInt64();
        if (size <= 0 || size > MaxFormatBytes) return null;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var b = new byte[size];
            Marshal.Copy(p, b, 0, (int)size);
            return b;
        }
        finally { GlobalUnlock(h); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    /// <summary>The Windows ANSI code page (Encoding.Default is UTF-8 on .NET, which garbles CF_TEXT).</summary>
    private static readonly Encoding Ansi = CreateAnsi();

    private static Encoding CreateAnsi()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding((int)GetACP()); }
        catch { return Encoding.Latin1; }
    }

    /// <summary>The text Ditto shows and searches: the text itself, the file paths, or the format name.</summary>
    internal static string Describe(IReadOnlyList<ClipFormat> formats)
    {
        if (formats.FirstOrDefault(f => f.Name == "CF_UNICODETEXT") is { } u)
            return Encoding.Unicode.GetString(u.Data).TrimEnd('\0');
        if (formats.FirstOrDefault(f => f.Name == "CF_HDROP") is { } d)
            return string.Join(Environment.NewLine, Files(d.Data));
        if (formats.FirstOrDefault(f => f.Name == "CF_TEXT") is { } t)
            return Ansi.GetString(t.Data).TrimEnd('\0');
        return formats[0].Name;
    }

    /// <summary>Paths in a DROPFILES block.</summary>
    internal static List<string> Files(byte[] dropfiles)
    {
        var list = new List<string>();
        if (dropfiles.Length < 20) return list;
        int offset = BitConverter.ToInt32(dropfiles, 0);
        bool wide = BitConverter.ToInt32(dropfiles, 16) != 0;
        var enc = wide ? Encoding.Unicode : Ansi;
        string all = enc.GetString(dropfiles, offset, dropfiles.Length - offset);
        foreach (var s in all.Split('\0')) if (s.Length > 0) list.Add(s);
        return list;
    }

    // ------------------------------------------------------------------ pasting

    public void Show()
    {
        if (_quick is null)
        {
            _quick = new QuickPasteWindow(this, _settings);
            _quick.Closed += (_, _) => _quick = null;
        }
        _quick.Open(Native.GetForegroundWindow());
    }

    /// <summary>Puts a clip back on the clipboard (all formats, or just its text) and pastes it into <paramref name="target"/>.</summary>
    public void Paste(long id, bool plainText, IntPtr target)
    {
        var formats = Store.Formats(id);
        if (plainText)
        {
            string text = Describe(formats);
            formats = new List<ClipFormat> { new("CF_UNICODETEXT", Encoding.Unicode.GetBytes(text + "\0")) };
        }
        if (!SetClipboard(formats)) { App.Log("clipboard: couldn't open the clipboard to paste"); return; }
        if (!plainText) Store.MoveToTop(id);

        if (target != IntPtr.Zero && Native.IsWindow(target))
        {
            Win32.AllowSetForegroundWindow(-1);
            Native.SetForegroundWindow(target);
        }
        // Give the target a moment to take focus, then Ctrl+V (tagged so our own hooks ignore it).
        // Release any modifier still held (Shift from Shift+Enter, Win/Alt) so the app sees plain Ctrl+V,
        // not Ctrl+Shift+V, which means something else in many apps.
        Task.Delay(60).ContinueWith(_ =>
        {
            var keys = new List<(ushort, bool)>();
            foreach (ushort vk in new ushort[] { 0xA0, 0xA1, 0xA4, 0xA5, Native.VK_LWIN, Native.VK_RWIN })   // L/R Shift, L/R Alt, Win
                if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) keys.Add((vk, true));
            if (keys.Any(k => k.Item1 is 0xA4 or 0xA5 or Native.VK_LWIN or Native.VK_RWIN)) keys.Insert(0, (Native.VK_DUMMY, false)); // keep Start/menus from opening
            if (keys.Count > 0 && keys[0].Item1 == Native.VK_DUMMY) keys.Add((Native.VK_DUMMY, true));
            keys.AddRange(new (ushort, bool)[] { (Native.VK_CONTROL, false), (0x56, false), (0x56, true), (Native.VK_CONTROL, true) });
            Native.SendKeys(keys.ToArray());
        });
    }

    private bool SetClipboard(IReadOnlyList<ClipFormat> formats)
    {
        if (!OpenClipboard(_window.Handle))
        {
            if (!Open()) return false;
        }
        try
        {
            EmptyClipboard();
            foreach (var f in formats)
            {
                uint id = Standard.FirstOrDefault(kv => kv.Value == f.Name).Key;
                if (id == 0) id = RegisterClipboardFormat(f.Name);
                IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, f.Data.Length));
                if (mem == IntPtr.Zero) continue;
                IntPtr p = GlobalLock(mem);
                Marshal.Copy(f.Data, 0, p, f.Data.Length);
                GlobalUnlock(mem);
                if (SetClipboardData(id, mem) == IntPtr.Zero) GlobalFree(mem);   // on success the clipboard owns it
            }
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
        return true;
    }

    public void Dispose()
    {
        if (_listening) RemoveClipboardFormatListener(_window.Handle);
        _quick?.CloseForGood();
        _window.Dispose();
        Store.Dispose();
    }
}
