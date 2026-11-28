using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Threading;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.Desktops;

/// <summary>
/// The taskbar has no pins (only open apps show). Resting the mouse on the Start button shows a bar
/// with the pinned apps right next to it, styled like the taskbar (same slot width and icon size,
/// background colour sampled from the taskbar). Click an icon to launch; the bar hides when the
/// mouse leaves. Never takes focus. Apps: Settings + every .lnk in the launcher folder, sorted by
/// name (a number prefix sets the order).
/// </summary>
internal sealed class StartLauncher : IDisposable
{
    private const string SettingsApp = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private sealed record Item(string Target, string? Args, string IconSource);
    private sealed record Geometry(int Tx, int Ty, int Th, double S, int StartW, int SlotW, int Icon, int X, int W);

    private readonly AppSettings _settings;
    private readonly LayeredWindow _window = new(clickThrough: false);
    private readonly DispatcherTimer _timer;
    private List<Item> _items = new();
    private List<Bitmap?> _icons = new();
    private int _iconPx;
    private DateTime _folderStamp;
    private Geometry? _geo, _cache;
    private long _cacheAt, _hoverAt, _leaveAt, _demoUntil;
    private bool _shown, _down, _light;
    private int _slot = -1;
    private Color _bg;

    public StartLauncher(AppSettings settings)
    {
        _settings = settings;
        _window.Message += OnMouse;
        LoadItems();
        if (Measure() is { } g) LoadIcons(g.Icon);   // load icons now so the first hover is instant too
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
    }

    /// <summary>Re-reads the folder (after a settings change).</summary>
    public void Reload()
    {
        Hide();
        LoadItems();
        _iconPx = 0;
    }

    /// <summary>Shows the bar for a few seconds without the mouse (aestoys.exe --demo-launcher).</summary>
    public void Demo(double seconds)
    {
        _demoUntil = Environment.TickCount64 + (long)(seconds * 1000);
        if (Measure() is { } g) Show(g);
    }

    // ---------------------------------------------------------------- items and icons

    private void LoadItems()
    {
        var items = new List<Item>();
        var s = _settings.Launcher;
        if (s.IncludeSettingsApp) items.Add(new Item("explorer.exe", SettingsApp, SettingsApp));
        try
        {
            if (Directory.Exists(s.Folder))
            {
                _folderStamp = Directory.GetLastWriteTimeUtc(s.Folder);
                foreach (var f in Directory.GetFiles(s.Folder, "*.lnk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    items.Add(new Item(f, null, LnkIconSource(f)));
            }
        }
        catch (Exception e) { App.Log("launcher: " + e.Message); }
        _items = items;
        _icons = new List<Bitmap?>(new Bitmap?[items.Count]);
    }

    private void LoadIcons(int px)
    {
        foreach (var b in _icons) b?.Dispose();
        _icons = _items.Select(i => ShellIcon(i.IconSource, px)).ToList();
        _iconPx = px;
    }

    /// <summary>
    /// Where to take a shortcut's icon from. If the shortcut's icon (or else its target) is a program,
    /// use the program directly: Windows' icon cache can hold a broken entry for the .lnk itself.
    /// A custom .ico (Chrome profiles, web apps) keeps the .lnk.
    /// </summary>
    private static string LnkIconSource(string lnk)
    {
        try
        {
            var link = (Win32.IShellLinkW)new Win32.ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetIconLocation(sb, sb.Capacity, out _);
            string loc = Environment.ExpandEnvironmentVariables(sb.ToString().Trim());
            if (loc != "" && !loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return lnk;
            if (loc == "")
            {
                sb.Clear();
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                loc = Environment.ExpandEnvironmentVariables(sb.ToString());
            }
            if (loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(loc)) return loc;
        }
        catch { }
        return lnk;
    }

    /// <summary>High-quality icon for any shell item (.lnk, exe, Store app) with alpha.</summary>
    private static Bitmap? ShellIcon(string parseName, int px)
    {
        var iid = typeof(Win32.IShellItemImageFactory).GUID;
        if (Win32.SHCreateItemFromParsingName(parseName, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new Win32.SIZE { cx = px, cy = px }, 0x4 /* SIIGBF_ICONONLY */, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            IntPtr ds = Marshal.AllocHGlobal(104);   // DIBSECTION
            try
            {
                if (Win32.GetObject(hbm, 104, ds) == 0) return null;
                int w = Marshal.ReadInt32(ds, 4), h = Marshal.ReadInt32(ds, 8), stride = Marshal.ReadInt32(ds, 12);
                short bpp = Marshal.ReadInt16(ds, 18);
                IntPtr bits = Marshal.ReadIntPtr(ds, 24);
                if (bits == IntPtr.Zero || bpp != 32) return Image.FromHbitmap(hbm);
                bool bottomUp = Marshal.ReadInt32(ds, 40) > 0;   // biHeight sign = row order

                var raw = new byte[stride * h];
                Marshal.Copy(bits, raw, 0, raw.Length);
                // The shell sometimes hands back an icon with no alpha at all (every alpha byte 0);
                // read as ARGB it would be invisible, so treat it as opaque.
                bool hasAlpha = false;
                for (int i = 3; i < raw.Length && !hasAlpha; i += 4) hasAlpha = raw[i] != 0;

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.BlockCopy(raw, (bottomUp ? h - 1 - y : y) * stride, row, 0, row.Length);
                        if (!hasAlpha) for (int i = 3; i < row.Length; i += 4) row[i] = 255;
                        Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                    }
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
            finally { Marshal.FreeHGlobal(ds); }
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) Win32.DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    // ---------------------------------------------------------------- geometry and hover tracking

    private Geometry? Measure()
    {
        IntPtr tray = ShellInfo.Taskbar();
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var r)) return null;
        double s = Win32.GetDpiForWindow(tray) / 96.0;
        if (s <= 0) return null;
        int startW = RoundA(55.33 * s), slotW = RoundA(44 * s);
        return new Geometry(r.Left, r.Top, r.Height, s, startW, slotW, RoundA(24 * s), r.Left + startW, slotW * _items.Count);
    }

    private void Tick()
    {
        // Taskbar geometry is cached for 1 s while the bar is hidden (this runs 50Ã—/s and the
        // taskbar almost never moves) and refreshed every tick while the bar is shown.
        long now = Environment.TickCount64;
        if (_shown || _cache is null || now - _cacheAt > 1000) { _cache = Measure(); _cacheAt = now; }
        var g = _cache;
        if (g is null) return;
        Native.GetCursorPos(out var pt);
        int mx = pt.X, my = pt.Y;
        bool inY = my >= g.Ty && my < g.Ty + g.Th;
        bool inStart = inY && mx >= g.Tx && mx < g.Tx + g.StartW;
        if (!_shown && !inStart) { _hoverAt = 0; return; }   // fast path: mouse not on Start

        if (!_shown)
        {
            if (_hoverAt == 0) _hoverAt = now;
            if (now - _hoverAt >= _settings.Launcher.HoverDelayMs && !ShellInfo.ForegroundIsFullscreen(ShellInfo.Taskbar()))
                Show(g);
            return;
        }
        bool inBar = inY && mx >= g.X && mx < g.X + g.W;
        if (inStart || inBar || now < _demoUntil)
        {
            _leaveAt = 0;
            int slot = inBar ? (mx - g.X) / g.SlotW : -1;
            if (slot != _slot) { _slot = slot; Draw(g); }
        }
        else
        {
            if (_leaveAt == 0) _leaveAt = now;
            if (now - _leaveAt >= _settings.Launcher.LeaveDelayMs) Hide();
        }
    }

    private void Show(Geometry g)
    {
        try
        {
            if (Directory.Exists(_settings.Launcher.Folder) && Directory.GetLastWriteTimeUtc(_settings.Launcher.Folder) != _folderStamp)
            { LoadItems(); _iconPx = 0; g = Measure() ?? g; }
        }
        catch { }
        if (_items.Count == 0) return;
        if (_iconPx != g.Icon) LoadIcons(g.Icon);    // first time, or scaling changed
        else for (int i = 0; i < _items.Count; i++)  // retry any icon the shell failed to give us
                _icons[i] ??= ShellIcon(_items[i].IconSource, g.Icon);

        // Match the taskbar's colour: sample an empty pixel at the Start button's top-left corner.
        IntPtr hdc = Win32.GetDC(IntPtr.Zero);
        uint c = Win32.GetPixel(hdc, g.Tx + RoundA(3 * g.S), g.Ty + RoundA(3 * g.S));
        Win32.ReleaseDC(IntPtr.Zero, hdc);
        int r = (int)(c & 0xFF), gr = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
        _bg = Color.FromArgb(255, r, gr, b);
        _light = r + gr + b > 384;

        _shown = true; _slot = -1; _leaveAt = 0; _geo = g;
        Draw(g);
    }

    private void Hide()
    {
        _shown = false; _hoverAt = 0; _leaveAt = 0; _slot = -1; _down = false;
        _window.Hide();
    }

    private void Draw(Geometry g)
    {
        // The bar is ~4 px shorter than the taskbar (2 px top and bottom at 150%), vertically centred.
        int inset = Math.Max(1, RoundA(2 * g.S / 1.5));
        int w = g.W, h = g.Th - 2 * inset;
        if (w <= 0 || h <= 0) return;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var gfx = Graphics.FromImage(bmp))
        {
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gfx.Clear(_bg);
            if (_slot >= 0)
            {
                using var hover = new SolidBrush(_light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 255, 255, 255));
                using var path = Toast.RoundRect((float)(_slot * g.SlotW + 3 * g.S), (float)(4 * g.S), (float)(g.SlotW - 6 * g.S), (float)(h - 8 * g.S), (float)(5 * g.S));
                gfx.FillPath(hover, path);
            }
            for (int i = 0; i < _icons.Count; i++)
                if (_icons[i] is { } icon)
                    gfx.DrawImage(icon, new Rectangle(i * g.SlotW + (g.SlotW - g.Icon) / 2, (h - g.Icon) / 2, g.Icon, g.Icon));
        }
        _window.Update(bmp, g.X, g.Ty + inset);
    }

    // ---------------------------------------------------------------- clicks

    private void OnMouse(int msg, int x, int y)
    {
        if (msg == 0x201) { _down = true; return; }                     // WM_LBUTTONDOWN
        if (msg != 0x202 || _geo is null) return;                        // WM_LBUTTONUP
        if (!_down) return;  // only a press AND release on the bar launches, not a drag that ends here
        _down = false;
        int i = x / _geo.SlotW;
        Hide();
        _demoUntil = 0;
        if (i < 0 || i >= _items.Count) return;
        // The bar never takes focus, so Windows wouldn't let the launched app come to the front.
        // This process just received the click, so it may hand the foreground on (ASFW_ANY).
        Win32.AllowSetForegroundWindow(-1);
        var item = _items[i];
        try
        {
            Process.Start(new ProcessStartInfo(item.Target) { Arguments = item.Args ?? "", UseShellExecute = true });
        }
        catch (Exception e) { App.Log("launcher: " + e.Message); }
    }

    private static int RoundA(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        _timer.Stop();
        _window.Dispose();
        foreach (var b in _icons) b?.Dispose();
    }
}
