using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using Aestoys.Features.Shell;
using Aestoys.Interop;
using Aestoys.Services;
using ManagedCommon;

namespace Aestoys.Features.Tools;

/// <summary>
/// Screen colour picker (Win+Shift+C), after PowerToys Color Picker (MIT). While picking, a small
/// panel follows the cursor with the colour under it; the mouse wheel zooms in on the pixels, the
/// arrow keys nudge the cursor by one pixel, and Esc cancels. Clicks do what the settings say
/// (left: copy and close, middle: copy and open the editor, right: close by default). Clicks are
/// swallowed so the app underneath never sees them.
/// </summary>
internal sealed class ColorPicker : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private LayeredWindow? _panel;
    private volatile bool _picking;
    private int _zoom;                 // 0 = no magnifier, then 1..4
    private Color _color;
    private Native.POINT _last = new() { X = int.MinValue };
    private ColorEditorWindow? _editor;

    private ColorPickerSettings S => _settings.Tools.ColorPicker;

    public ColorPicker(AppSettings settings, HotkeyRouter hotkeys, KeyboardHook keyboard, MouseHook mouse)
    {
        _settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Track(false), _ui);
        hotkeys.Register(() => S.Hotkey, () => S.Enabled, Activate);
        keyboard.Add(OnKey);
        mouse.Add(OnMouse);
    }

    public void Activate()
    {
        if (_picking) return;
        if (S.Activation == ColorPickerActivation.OpenEditor) { OpenEditor(); return; }
        StartPicking();
    }

    public void StartPicking()
    {
        if (_picking) return;
        _editor?.Hide();
        _panel ??= new LayeredWindow(clickThrough: true);
        _zoom = 0;
        _last = new Native.POINT { X = int.MinValue };
        _picking = true;
        Track(true);
        _timer.Start();
    }

    private void Stop()
    {
        _picking = false;
        _timer.Stop();
        _panel?.Hide();
    }

    // ------------------------------------------------------------------ input (hook threads)

    private bool OnKey(int vk, bool down, bool alt, bool injected)
    {
        if (!_picking || injected) return false;
        switch (vk)
        {
            case 0x1B: // Esc
                if (down) _ui.BeginInvoke(Stop);
                return true;
            case 0x25 or 0x26 or 0x27 or 0x28: // arrows nudge one pixel
                if (down)
                {
                    Native.GetCursorPos(out var p);
                    SetCursorPos(p.X + (vk == 0x25 ? -1 : vk == 0x27 ? 1 : 0), p.Y + (vk == 0x26 ? -1 : vk == 0x28 ? 1 : 0));
                }
                return true;
            case 0x0D: // Enter = left click
                if (down) _ui.BeginInvoke(() => Click(S.PrimaryClick));
                return true;
        }
        return false;
    }

    private bool OnMouse(int msg, Native.POINT pt, int wheel, bool injected)
    {
        if (!_picking || injected) return false;
        switch (msg)
        {
            case MouseHook.WM_LBUTTONDOWN: _ui.BeginInvoke(() => Click(S.PrimaryClick)); return true;
            case MouseHook.WM_MBUTTONDOWN: _ui.BeginInvoke(() => Click(S.MiddleClick)); return true;
            case MouseHook.WM_RBUTTONDOWN: _ui.BeginInvoke(() => Click(S.SecondaryClick)); return true;
            case MouseHook.WM_LBUTTONUP or MouseHook.WM_MBUTTONUP or MouseHook.WM_RBUTTONUP: return true;
            case MouseHook.WM_MOUSEWHEEL:
                _ui.BeginInvoke(() => { _zoom = Math.Clamp(_zoom + (wheel > 0 ? 1 : -1), 0, 4); Track(true); });
                return true;
        }
        return false;
    }

    private void Click(ColorPickerClick action)
    {
        if (!_picking) return;
        Track(true);
        var c = _color;
        Stop();
        switch (action)
        {
            case ColorPickerClick.PickColorAndClose:
                Pick(c);
                break;
            case ColorPickerClick.PickColorThenEditor:
                Pick(c);
                OpenEditor();
                break;
        }
    }

    private void Pick(Color c)
    {
        AddToHistory(c);
        string text = Format(c, S.CopiedFormat);
        ClipboardText.SetText(text);
        Toast.Show($"Copied {text}");
    }

    public void OpenEditor()
    {
        if (_editor is null)
        {
            _editor = new ColorEditorWindow(this, _settings);
            _editor.Closed += (_, _) => _editor = null;
        }
        _editor.Refresh();
        _editor.Show();
        _editor.Activate();
    }

    // ------------------------------------------------------------------ history and formats

    internal void AddToHistory(Color c)
    {
        string entry = $"255|{c.R}|{c.G}|{c.B}";
        S.History.Remove(entry);
        S.History.Insert(0, entry);
        int limit = Math.Max(1, S.HistoryLimit);
        if (S.History.Count > limit) S.History.RemoveRange(limit, S.History.Count - limit);
        _settings.Save();
    }

    internal static Color? ParseHistory(string entry)
    {
        var p = entry.Split('|');
        if (p.Length == 4 && byte.TryParse(p[1], out var r) && byte.TryParse(p[2], out var g) && byte.TryParse(p[3], out var b))
            return Color.FromArgb(255, r, g, b);
        return null;
    }

    internal string Format(Color c, string formatName)
    {
        string template = S.Formats.TryGetValue(formatName, out var f) && f.Template.Length > 0 ? f.Template : "%Rex%Grx%Blx";
        string text;
        try { text = ColorFormatHelper.GetStringRepresentation(c, template); }
        catch { text = $"{c.R:x2}{c.G:x2}{c.B:x2}"; }
        return text.Replace(ColorFormatHelper.GetColorNameParameter(), Name(c));
    }

    internal static string Name(Color c)
    {
        string id;
        try { id = ColorNameHelper.GetColorNameIdentifier(c); } catch { return ""; }
        return Names.TryGetValue(id, out var n) ? n : id;
    }

    private static readonly Dictionary<string, string> Names = new()
    {
        ["TEXT_COLOR_AQUA"] = "Aqua", ["TEXT_COLOR_BLACK"] = "Black", ["TEXT_COLOR_BLUE"] = "Blue", ["TEXT_COLOR_BLUEGRAY"] = "Blue gray",
        ["TEXT_COLOR_BRIGHTGREEN"] = "Bright green", ["TEXT_COLOR_BROWN"] = "Brown", ["TEXT_COLOR_CORAL"] = "Coral", ["TEXT_COLOR_DARKBLUE"] = "Dark blue",
        ["TEXT_COLOR_DARKGRAY"] = "Dark gray", ["TEXT_COLOR_DARKGREEN"] = "Dark green", ["TEXT_COLOR_DARKPURPLE"] = "Dark purple", ["TEXT_COLOR_DARKRED"] = "Dark red",
        ["TEXT_COLOR_DARKTEAL"] = "Dark teal", ["TEXT_COLOR_DARKYELLOW"] = "Dark yellow", ["TEXT_COLOR_GOLD"] = "Gold", ["TEXT_COLOR_GRAY"] = "Gray",
        ["TEXT_COLOR_GREEN"] = "Green", ["TEXT_COLOR_ICEBLUE"] = "Ice blue", ["TEXT_COLOR_INDIGO"] = "Indigo", ["TEXT_COLOR_LAVENDER"] = "Lavender",
        ["TEXT_COLOR_LIGHTBLUE"] = "Light blue", ["TEXT_COLOR_LIGHTGRAY"] = "Light gray", ["TEXT_COLOR_LIGHTGREEN"] = "Light green", ["TEXT_COLOR_LIGHTORANGE"] = "Light orange",
        ["TEXT_COLOR_LIGHTTURQUOISE"] = "Light turquoise", ["TEXT_COLOR_LIGHTYELLOW"] = "Light yellow", ["TEXT_COLOR_LIME"] = "Lime", ["TEXT_COLOR_OLIVEGREEN"] = "Olive green",
        ["TEXT_COLOR_ORANGE"] = "Orange", ["TEXT_COLOR_PALEBLUE"] = "Pale blue", ["TEXT_COLOR_PERIWINKLE"] = "Periwinkle", ["TEXT_COLOR_PINK"] = "Pink",
        ["TEXT_COLOR_PLUM"] = "Plum", ["TEXT_COLOR_PURPLE"] = "Purple", ["TEXT_COLOR_RED"] = "Red", ["TEXT_COLOR_ROSE"] = "Rose",
        ["TEXT_COLOR_SKYBLUE"] = "Sky blue", ["TEXT_COLOR_TAN"] = "Tan", ["TEXT_COLOR_TEAL"] = "Teal", ["TEXT_COLOR_TURQUOISE"] = "Turquoise",
        ["TEXT_COLOR_WHITE"] = "White", ["TEXT_COLOR_YELLOW"] = "Yellow",
    };

    // ------------------------------------------------------------------ the panel

    private static readonly int[] ZoomCells = { 0, 15, 11, 9, 7 };   // pixels shown across per zoom level

    private void Track(bool force)
    {
        if (!_picking || _panel is null) return;
        Native.GetCursorPos(out var pt);
        if (!force && pt.X == _last.X && pt.Y == _last.Y) return;
        _last = pt;

        double scale = DpiAt(pt);
        int cells = ZoomCells[_zoom];
        Bitmap? grab = null;
        try
        {
            int n = Math.Max(cells, 1);
            grab = new Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(grab))
                g.CopyFromScreen(pt.X - n / 2, pt.Y - n / 2, 0, 0, new Size(n, n), CopyPixelOperation.SourceCopy);
            _color = Color.FromArgb(255, grab.GetPixel(n / 2, n / 2));
        }
        catch { grab?.Dispose(); return; }

        using (grab)
            Draw(pt, scale, grab, cells);
    }

    private void Draw(Native.POINT pt, double scale, Bitmap grab, int cells)
    {
        int S(double v) => (int)Math.Round(v * scale, MidpointRounding.AwayFromZero);
        bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() != Wpf.Ui.Appearance.SystemTheme.Light;
        var bg = dark ? Color.FromArgb(250, 44, 44, 44) : Color.FromArgb(250, 249, 249, 249);
        var fg = dark ? Color.White : Color.FromArgb(28, 28, 28);
        var sub = dark ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);
        var stroke = dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);

        string text = Format(_color, ShownFormat());
        string name = this.S.ShowColorName ? Name(_color) : "";

        using var fontMain = new Font("Segoe UI Variable Text", (float)(13 * scale), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var fontSub = new Font("Segoe UI Variable Text", (float)(11 * scale), System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        int textW = (int)Math.Ceiling(Math.Max(measure.MeasureString(text, fontMain).Width, name.Length > 0 ? measure.MeasureString(name, fontSub).Width : 0));

        int pad = S(8), sw = S(28), gap = S(10), shadow = S(6);
        int rowH = S(44);
        int zoomSide = cells > 0 ? S(132) : 0;
        int w = Math.Max(pad + sw + gap + textW + pad + S(4), zoomSide + 2 * pad);
        int h = rowH + (zoomSide > 0 ? zoomSide + pad : 0);
        int W = w + 2 * shadow, H = h + 2 * shadow;

        using var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float r = S(8);
            for (int i = shadow; i > 0; i--)
            {
                using var sp = Toast.RoundRect(shadow - i, shadow - i + S(1), w + 2 * i, h + 2 * i, r + i);
                using var sb = new SolidBrush(Color.FromArgb((int)(28.0 * (shadow - i + 1) / shadow / 2), 0, 0, 0));
                g.FillPath(sb, sp);
            }
            using (var path = Toast.RoundRect(shadow, shadow, w, h, r))
            {
                using var b = new SolidBrush(bg);
                g.FillPath(b, path);
                using var p = new Pen(stroke, 1);
                g.DrawPath(p, path);
            }

            int x0 = shadow, y0 = shadow;
            if (zoomSide > 0)
            {
                int zx = x0 + (w - zoomSide) / 2, zy = y0 + pad;
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(grab, new Rectangle(zx, zy, zoomSide, zoomSide));
                float cell = zoomSide / (float)cells;
                using var gridPen = new Pen(Color.FromArgb(40, 128, 128, 128), 1);
                for (int i = 1; i < cells; i++)
                {
                    g.DrawLine(gridPen, zx + i * cell, zy, zx + i * cell, zy + zoomSide);
                    g.DrawLine(gridPen, zx, zy + i * cell, zx + zoomSide, zy + i * cell);
                }
                float c0 = zx + (cells / 2) * cell, c1 = zy + (cells / 2) * cell;
                using var outer = new Pen(Color.Black, Math.Max(1, S(1)));
                using var inner = new Pen(Color.White, Math.Max(1, S(1)));
                g.DrawRectangle(outer, c0 - 1, c1 - 1, cell + 1, cell + 1);
                g.DrawRectangle(inner, c0, c1, cell - 1, cell - 1);
                g.PixelOffsetMode = PixelOffsetMode.Default;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                y0 += zoomSide + pad;
            }

            using (var swPath = Toast.RoundRect(x0 + pad, y0 + (rowH - sw) / 2, sw, sw, S(5)))
            {
                using var b = new SolidBrush(_color);
                g.FillPath(b, swPath);
                using var p = new Pen(stroke, 1);
                g.DrawPath(p, swPath);
            }
            float tx = x0 + pad + sw + gap;
            using var fb = new SolidBrush(fg);
            using var sbr = new SolidBrush(sub);
            if (name.Length > 0)
            {
                g.DrawString(text, fontMain, fb, tx, y0 + rowH / 2f - fontMain.Height + S(2));
                g.DrawString(name, fontSub, sbr, tx, y0 + rowH / 2f + S(1));
            }
            else g.DrawString(text, fontMain, fb, tx, y0 + (rowH - fontMain.Height) / 2f);
        }

        // Below-right of the cursor, flipped to stay on the monitor.
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(pt, 2), ref mi);
        int px = pt.X + S(18) - shadow, py = pt.Y + S(22) - shadow;
        if (px + W > mi.rcMonitor.Right) px = pt.X - S(18) - W + shadow;
        if (py + H > mi.rcMonitor.Bottom) py = pt.Y - S(18) - H + shadow;
        _panel!.Update(bmp, px, py);
    }

    /// <summary>The first visible format (what the panel shows); falls back to the copied one.</summary>
    private string ShownFormat() => S.Formats.FirstOrDefault(f => f.Value.Visible).Key ?? S.CopiedFormat;

    private static double DpiAt(Native.POINT pt)
    {
        try
        {
            IntPtr mon = Native.MonitorFromPoint(pt, 2);
            if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0) return dx / 96.0;
        }
        catch { }
        return 1;
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    /// <summary>Test hook: shows the panel at a fixed point for a few seconds (--demo-colorpicker).</summary>
    public void Demo(int zoom)
    {
        StartPicking();
        _zoom = Math.Clamp(zoom, 0, 4);
        Track(true);
        var t = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (s, _) => { ((DispatcherTimer)s!).Stop(); Stop(); }, _ui);
        t.Start();
    }

    public void Dispose()
    {
        Stop();
        _panel?.Dispose();
        _editor?.Close();
    }
}

/// <summary>Clipboard helper that retries briefly, since another app may have the clipboard open.</summary>
internal static class ClipboardText
{
    public static void SetText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try { System.Windows.Clipboard.SetDataObject(text, true); return; }
            catch { System.Threading.Thread.Sleep(30); }
        }
    }
}
