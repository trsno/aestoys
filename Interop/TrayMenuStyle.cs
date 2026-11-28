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
/// Put an icon on an item with <see cref="Icon"/>.
/// </summary>
internal static class TrayMenuStyle
{
    public const string Settings = "", Awake = "", Eye = "", Exit = "", Screen = "", Off = "", Clock = "", Infinite = "";

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static T Icon<T>(this T item, string glyph) where T : ToolStripItem { item.Tag = glyph; return item; }

    public static void Apply(ContextMenuStrip menu)
    {
        var renderer = new Renderer();
        menu.Renderer = renderer;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = true;
        menu.Font = new Font("Segoe UI", 9.75f);
        menu.ShowItemToolTips = true;
        menu.Opening += (_, _) => { renderer.Refresh(); Style(menu, renderer); };
        Style(menu, renderer);
    }

    /// <summary>Sizing and window corners for a menu and its submenus (call again after items change).</summary>
    private static void Style(ToolStripDropDownMenu menu, Renderer r)
    {
        menu.Renderer = r;
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = true;
        menu.Font = new Font("Segoe UI", 9.75f);
        menu.BackColor = r.Back;
        menu.Padding = new Padding(0, menu.LogicalToDeviceUnits(4), 0, menu.LogicalToDeviceUnits(4));
        menu.ImageScalingSize = new Size(menu.LogicalToDeviceUnits(20), menu.LogicalToDeviceUnits(20));
        if (!menu.IsHandleCreated) menu.HandleCreated += (_, _) => Round(menu, r);
        else Round(menu, r);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator) continue;
            int v = menu.LogicalToDeviceUnits(6);
            item.Padding = new Padding(0, v, menu.LogicalToDeviceUnits((item is ToolStripMenuItem { HasDropDownItems: true } ? 8 : 12) + Shift), v);
            if (item is ToolStripMenuItem { HasDropDownItems: true } sub && sub.DropDown is ToolStripDropDownMenu dd)
            {
                if (dd.Tag is not true) { dd.Tag = true; dd.Opening += (_, _) => Style(dd, r); }
                Style(dd, r);
            }
        }
    }

    /// <summary>Extra room between the icon column and the text (logical pixels).</summary>
    private const int Shift = 14;

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

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            var g = e.Graphics;
            if (item.Selected && item.Enabled)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int inset = item.Owner!.LogicalToDeviceUnits(4);
                var rect = new Rectangle(inset, 1, Visible(item) - 2 * inset - 1, item.Height - 2);
                using var path = RoundRect(rect, item.Owner.LogicalToDeviceUnits(4));
                using var brush = new SolidBrush(Hover);
                g.FillPath(brush, path);
            }
            // Items with an icon show their on/off state as a check on the right (submenus tint the icon instead).
            // Drawn here because the text pass is clipped to the text area.
            if (item is ToolStripMenuItem { Checked: true, HasDropDownItems: false } && item.Tag is string)
            {
                var o = item.Owner!;
                Glyph(g, "\uE73E", new Rectangle(Visible(item) - o.LogicalToDeviceUnits(36), 0, o.LogicalToDeviceUnits(24), item.Height), Accent, 11f);
            }
        }

        /// <summary>The item's width inside the menu window (items can be laid out wider than the window).</summary>
        private static int Visible(ToolStripItem item) => item.Owner is { } o ? Math.Min(item.Width, o.ClientSize.Width - item.Bounds.X - o.Padding.Right) : item.Width;

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }   // drawn with the text
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item;
            var owner = item.Owner!;
            var color = item.Enabled ? Fore : Dim;
            int shift = owner.LogicalToDeviceUnits(Shift);
            var text = new Rectangle(e.TextRectangle.X + shift, 0, e.TextRectangle.Width, item.Height);
            if (item is ToolStripMenuItem mi && owner is ToolStripDropDownMenu)
            {
                var iconBox = new Rectangle(owner.LogicalToDeviceUnits(4), 0, text.X - owner.LogicalToDeviceUnits(10), item.Height);
                bool on = mi.Checked;
                if (item.Tag is string glyph)
                    Glyph(e.Graphics, glyph, iconBox, !item.Enabled ? Dim : on && mi.HasDropDownItems ? Accent : color, 11f);
                else if (on)
                    Glyph(e.Graphics, "\uE73E", iconBox, Accent, 11f);

            }
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, text, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var r = e.ArrowRectangle;
            Glyph(e.Graphics, "", new Rectangle(r.X - 8, 0, r.Width, e.Item?.Height ?? r.Height), e.Item?.Enabled == false ? Dim : Fore, 7f);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = e.Item.Owner?.LogicalToDeviceUnits(12) ?? 12;
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
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
