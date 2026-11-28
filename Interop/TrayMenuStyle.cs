using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aestoys.Interop;

/// <summary>
/// Windows 11 look for the tray menu: follows the taskbar's light/dark setting, rounded corners,
/// roomy rows with rounded highlights, Fluent icons and accent-coloured check marks.
/// Every row uses the same columns, in the main menu and in submenus:
/// [icon or check] [text] [chevron or check]. Put an icon on an item with <see cref="Icon"/>.
/// </summary>
internal static class TrayMenuStyle
{
    public const string Settings = "", Awake = "", Eye = "", Exit = "", Screen = "";
    private const string Check = "", Chevron = "";

    // Column layout in logical pixels.
    private const int Inset = 4;        // highlight inset from the window edge
    private const int TextX = 46;       // text starts here; the icon is centred in 8..40
    private const int RightCol = 38;    // chevron / check column at the right edge
    private const int RowHeight = 40;
    private const int SeparatorHeight = 9;
    private const int MinWidth = 180;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static T Icon<T>(this T item, string glyph) where T : ToolStripItem { item.Tag = glyph; return item; }

    public static void Apply(ContextMenuStrip menu)
    {
        var renderer = new Renderer();
        menu.ShowItemToolTips = true;
        menu.Opening += (_, _) => { renderer.Refresh(); Style(menu, renderer); };
        Style(menu, renderer);
    }

    /// <summary>Sizes every row of a menu and its submenus (call again after items change).</summary>
    private static void Style(ToolStripDropDownMenu menu, Renderer r)
    {
        menu.Renderer = r;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = false;
        menu.Font = r.Font;
        menu.BackColor = r.Back;
        menu.Padding = new Padding(0, menu.LogicalToDeviceUnits(4), 0, menu.LogicalToDeviceUnits(4));
        if (menu.Tag is not true)
        {
            menu.Tag = true;
            menu.HandleCreated += (_, _) => Round(menu, r);
            if (menu.IsHandleCreated) Round(menu, r);
        }
        else if (menu.IsHandleCreated) Round(menu, r);

        // One width for the whole menu: the widest text plus the fixed columns.
        int width = menu.LogicalToDeviceUnits(MinWidth);
        foreach (ToolStripItem item in menu.Items)
            if (item is not ToolStripSeparator)
                width = Math.Max(width, menu.LogicalToDeviceUnits(TextX + RightCol + 8) + TextRenderer.MeasureText(item.Text ?? "", r.Font).Width);

        int height = menu.Padding.Vertical;
        foreach (ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = Padding.Empty;
            item.Size = new Size(width, menu.LogicalToDeviceUnits(item is ToolStripSeparator ? SeparatorHeight : RowHeight));
            if (item.Available) height += item.Height;   // Available = will be shown (Visible is false while the menu is closed)
            if (item is ToolStripMenuItem { HasDropDownItems: true } sub && sub.DropDown is ToolStripDropDownMenu dd)
            {
                if (dd.Tag is not true) dd.Opening += (_, _) => Style(dd, r);
                Style(dd, r);
            }
        }
        // The menu would size itself from its own text measurement and cut the rows short.
        menu.AutoSize = false;
        menu.Size = new Size(width, height);
    }

    private static void Round(Control c, Renderer r)
    {
        int round = 2;   // DWMWCP_ROUND
        DwmSetWindowAttribute(c.Handle, 33, ref round, 4);
        int border = ColorTranslator.ToWin32(r.Border);
        DwmSetWindowAttribute(c.Handle, 34, ref border, 4);
    }

    private sealed class Renderer : ToolStripRenderer
    {
        public Color Back, Fore, Dim, Hover, Line, Border, Accent;
        public readonly Font Font = new(FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI", 10.5f);
        private static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public Renderer() => Refresh();

        public void Refresh()
        {
            bool dark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) == 0;
            Back = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            Fore = dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);
            Dim = dark ? Color.FromArgb(130, 130, 130) : Color.FromArgb(140, 140, 140);
            Hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
            Line = dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(222, 222, 222);
            Border = dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(214, 214, 214);
            // Accent colour (ABGR); a lighter tint reads better on dark, a darker one on light.
            int abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD77800));
            var a = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            Accent = dark ? Mix(a, Color.White, 0.35) : Mix(a, Color.Black, 0.15);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }   // DWM draws the rounded border
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { }

        /// <summary>The whole row: highlight, icon, text and the right-hand chevron or check.</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item || item.Owner is not { } o) return;
            var g = e.Graphics;
            int w = Math.Min(item.Width, o.ClientSize.Width - item.Bounds.X), h = item.Height;
            int D(int v) => o.LogicalToDeviceUnits(v);

            if (item.Selected && item.Enabled)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundRect(new Rectangle(D(Inset), D(2), w - 2 * D(Inset) - 1, h - D(4)), D(5));
                using var brush = new SolidBrush(Hover);
                g.FillPath(brush, path);
            }

            var color = item.Enabled ? Fore : Dim;
            bool on = item.Checked, sub = item.HasDropDownItems;
            var left = new Rectangle(D(8), 0, D(32), h);
            var right = new Rectangle(w - D(RightCol), 0, D(RightCol - 12), h);

            if (item.Tag is string glyph) Glyph(g, glyph, left, on && sub && item.Enabled ? Accent : color, 12.5f);
            else if (on) Glyph(g, Check, left, Accent, 11.5f);

            if (sub) Glyph(g, Chevron, right, color, 7f);
            else if (on && item.Tag is string) Glyph(g, Check, right, Accent, 11.5f);

            var text = new Rectangle(D(TextX), 0, right.X - D(TextX), h);
            TextRenderer.DrawText(g, item.Text, item.Font, text, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = e.Item.Owner?.LogicalToDeviceUnits(12) ?? 12;
            int w = Math.Min(e.Item.Width, (e.Item.Owner?.ClientSize.Width ?? e.Item.Width) - e.Item.Bounds.X);
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, inset, y, w - inset, y);
        }

        private static void Glyph(Graphics g, string glyph, Rectangle box, Color color, float size)
        {
            // Icon fonts have tall line metrics, so centre the glyph's actual outline instead of its line box.
            using var family = new FontFamily(IconFont);
            using var path = new GraphicsPath();
            path.AddString(glyph, family, (int)FontStyle.Regular, size * g.DpiY / 72f, Point.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            using (var m = new Matrix())
            {
                m.Translate(MathF.Round(box.X + (box.Width - b.Width) / 2f - b.X), MathF.Round(box.Y + (box.Height - b.Height) / 2f - b.Y));
                path.Transform(m);
            }
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
            g.SmoothingMode = mode;
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        private static int ReadDword(string key, string name, int fallback)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name) is int v ? v : fallback; }
            catch { return fallback; }
        }

        private static bool FontExists(string name)
        {
            using var f = new Font(name, 10f);
            return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
