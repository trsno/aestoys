using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aestoys.Interop;

/// <summary>
/// Windows 11 look for the tray menu: follows the taskbar's light/dark setting, rounded corners,
/// rounded highlights, Fluent icons. Every row in the main menu and submenus uses the same columns:
/// [icon, or a dot for the chosen option] [text] [badge] [chevron].
/// A checked item with an icon is a switch that is on and shows an "On" badge;
/// a checked item without one is the chosen option of a group and shows a dot.
/// Put an icon on an item with <see cref="Icon"/>.
/// </summary>
internal static class TrayMenuStyle
{
    public const string Settings = "", Awake = "", Eye = "", Exit = "", Screen = "";
    private const string Chevron = "";

    // Layout in logical pixels.
    private const int Inset = 5;          // highlight and separator inset from the window edge
    private const int IconCenter = 22;
    private const int TextX = 45;
    private const int ChevronCol = 30;    // chevron column at the right edge
    private const int RightPad = 16;      // space after text/badge when there's no chevron
    private const int RowHeight = 38;
    private const int SeparatorHeight = 7;
    private const int MinWidth = 160;
    private const int SubmenuOverlap = 4;
    private const string BadgeText = "On";

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static T Icon<T>(this T item, string glyph) where T : ToolStripItem { item.Tag = glyph; return item; }

    public static void Apply(ContextMenuStrip menu)
    {
        var renderer = new Renderer();
        menu.ShowItemToolTips = true;
        menu.Opening += (_, _) => { renderer.Refresh(); Style(menu, renderer); };
        Style(menu, renderer);
    }

    private static bool Badge(ToolStripItem item) => item is ToolStripMenuItem { Checked: true } && item.Tag is string;

    /// <summary>Sizes every row of a menu and its submenus (call again after items change).</summary>
    private static void Style(ToolStripDropDownMenu menu, Renderer r)
    {
        int D(int v) => menu.LogicalToDeviceUnits(v);
        menu.Renderer = r;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = false;
        menu.Font = r.Font;
        menu.BackColor = r.Back;
        menu.Padding = new Padding(0, D(4), 0, D(4));
        if (menu.Tag is not true)
        {
            menu.Tag = true;
            menu.HandleCreated += (_, _) => Round(menu, r);
            if (menu.IsHandleCreated) Round(menu, r);
        }
        else if (menu.IsHandleCreated) Round(menu, r);

        // One width for the whole menu: the widest row's text, badge and chevron plus the fixed columns.
        int width = D(MinWidth);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator || !item.Available) continue;
            int w = D(TextX) + TextRenderer.MeasureText(item.Text ?? "", r.Font).Width;
            if (Badge(item)) w += D(10) + r.BadgeWidth(menu);
            w += item is ToolStripMenuItem { HasDropDownItems: true } ? D(ChevronCol) : D(RightPad);
            width = Math.Max(width, w + D(Inset));
        }

        int height = menu.Padding.Vertical;
        foreach (ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            item.Margin = Padding.Empty;
            item.Padding = Padding.Empty;
            item.Size = new Size(width, D(item is ToolStripSeparator ? SeparatorHeight : RowHeight));
            if (item.Available) height += item.Height;   // Available = will be shown (Visible is false while the menu is closed)
            if (item is ToolStripMenuItem { HasDropDownItems: true } sub && sub.DropDown is ToolStripDropDownMenu dd)
            {
                if (dd.Tag is not true)
                {
                    dd.Opening += (_, _) => Style(dd, r);
                    dd.Opened += (_, _) => Tuck(dd, menu);
                }
                Style(dd, r);
            }
        }
        // The menu would size itself from its own text measurement and cut the rows short.
        menu.AutoSize = false;
        menu.Size = new Size(width, height);
    }

    /// <summary>Submenus overlap their parent a little, and their first row lines up with the parent row.</summary>
    private static void Tuck(ToolStripDropDown dd, ToolStripDropDown parent)
    {
        int overlap = parent.LogicalToDeviceUnits(SubmenuOverlap);
        var b = dd.Bounds;
        int x = b.Right <= parent.Left + overlap ? b.X + overlap + (parent.Left - b.Right) : b.X - overlap - (b.Left - parent.Right);
        // WinForms ignores Bounds on an open drop-down; move the window itself.
        SetWindowPos(dd.Handle, IntPtr.Zero, x, b.Y - dd.Padding.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
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
        public Color Back, Fore, Dim, Hover, Line, Border, Accent, BadgeFill;
        public readonly Font Font = new("Segoe UI", 9.75f);
        private readonly Font _badgeFont = new("Segoe UI", 9f);
        private static readonly string IconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        public Renderer() => Refresh();

        public int BadgeWidth(Control c) => TextRenderer.MeasureText(BadgeText, _badgeFont).Width + c.LogicalToDeviceUnits(16);

        public void Refresh()
        {
            bool dark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) == 0;
            Back = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
            Fore = dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);
            Dim = dark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(140, 140, 140);
            Hover = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(234, 234, 234);
            Line = dark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(224, 224, 224);
            Border = dark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(214, 214, 214);
            // Accent colour (ABGR); a lighter tint reads better on dark, a darker one on light.
            int abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD77800));
            var a = Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            Accent = dark ? Mix(a, Color.White, 0.3) : Mix(a, Color.Black, 0.15);
            BadgeFill = Color.FromArgb(dark ? 40 : 28, Accent);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }   // DWM draws the rounded border
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { }

        /// <summary>The whole row: highlight, icon or dot, text, badge and chevron.</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item || item.Owner is not { } o) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Math.Min(item.Width, o.ClientSize.Width - item.Bounds.X), h = item.Height;
            int D(int v) => o.LogicalToDeviceUnits(v);

            if ((item.Selected || item.DropDown.Visible && item.HasDropDownItems) && item.Enabled)
            {
                using var path = RoundRect(new Rectangle(D(Inset), D(1), w - 2 * D(Inset), h - D(2)), D(5));
                using var brush = new SolidBrush(Hover);
                g.FillPath(brush, path);
            }

            var color = item.Enabled ? Fore : Dim;
            var iconBox = new Rectangle(D(IconCenter - 16), 0, D(32), h);
            if (item.Tag is string glyph) Glyph(g, glyph, iconBox, color, 12f);
            else if (item.Checked)
            {
                int dot = D(6);
                using var b = new SolidBrush(color);
                g.FillEllipse(b, D(IconCenter) - dot / 2f, h / 2f - dot / 2f, dot, dot);
            }

            int right = w - D(Inset);
            if (item.HasDropDownItems)
            {
                Glyph(g, Chevron, new Rectangle(w - D(ChevronCol), 0, D(ChevronCol - Inset - 6), h), Dim, 7.5f);
                right = w - D(ChevronCol);
            }
            else right = w - D(RightPad) + D(4);

            if (Badge(item))
            {
                int bw = BadgeWidth(o), bh = D(24);
                var badge = new Rectangle(right - bw - D(4), (h - bh) / 2, bw, bh);
                using (var path = RoundRect(badge, D(6)))
                {
                    using var fill = new SolidBrush(BadgeFill);
                    g.FillPath(fill, path);
                    using var pen = new Pen(Accent, Math.Max(1, D(1)));
                    g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, BadgeText, _badgeFont, badge, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                right = badge.X - D(6);
            }

            var text = new Rectangle(D(TextX), 0, Math.Max(0, right - D(TextX)), h);
            TextRenderer.DrawText(g, item.Text, item.Font, text, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = e.Item.Owner?.LogicalToDeviceUnits(Inset) ?? Inset;
            int w = Math.Min(e.Item.Width, (e.Item.Owner?.ClientSize.Width ?? e.Item.Width) - e.Item.Bounds.X);
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, inset, y, w - inset - 1, y);
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
            // A hairline outline gives the thin Fluent strokes the weight of app menu icons.
            using var pen = new Pen(color, 0.6f * g.DpiY / 96f);
            g.DrawPath(pen, path);
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
