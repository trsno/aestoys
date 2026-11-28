using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Threading;
using Aestoys.Features.Shell;
using Aestoys.Interop;

namespace Aestoys.Features.Desktops;

/// <summary>
/// Turns the Windows logo on the Start button into a 2Ã—2 desktop indicator:
/// <list type="bullet">
/// <item>each logo pane is one desktop, with its number inside;</item>
/// <item>inactive desktops are darkened (black 50%), the active one is left clear so the logo shows;</item>
/// <item>with more than 4 desktops it shows a page of 4 (1-4, 5-8â€¦), the last page always full.</item>
/// </list>
/// Geometry follows the taskbar's own DPI every tick, so changes of main monitor, resolution or
/// scaling are picked up. Click-through, always on top, hidden for fullscreen apps. The registry is
/// polled ~7Ã—/s and it only redraws on change. Assumes left-aligned taskbar icons.
/// </summary>
internal sealed class DesktopIndicator : IDisposable
{
    private readonly LayeredWindow _window = new(clickThrough: true);
    private readonly DispatcherTimer _timer;
    private IntPtr _tray, _lastForeground;
    private string _key = "";


    public DesktopIndicator()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        Tick();
    }

    /// <summary>Redraw now (after a programmatic switch) instead of waiting for the next poll.</summary>
    public void Tick()
    {
        try
        {
            IntPtr tray = ShellInfo.Taskbar();
            if (tray == IntPtr.Zero) return;
            if (tray != _tray) { _tray = tray; _key = ""; }
            var (count, idx) = VirtualDesktops.State();
            Native.GetWindowRect(tray, out var r);
            uint dpi = Win32.GetDpiForWindow(tray);
            bool full = ShellInfo.ForegroundIsFullscreen(tray);
            string key = $"{count}|{idx}|{r.Left}|{r.Top}|{r.Width}|{r.Height}|{dpi}|{full}";
            if (key == _key)
            {
                // The taskbar only rises above us when something gets focus, so re-assert topmost
                // only when the foreground window changes.
                IntPtr fg = Native.GetForegroundWindow();
                if (!full && fg != _lastForeground) _window.BringToTop();
                _lastForeground = fg;
                return;
            }
            _key = key;
            if (full || count < 1 || dpi == 0) { _window.Hide(); return; }
            Draw(count, idx, r.Left, r.Top, r.Height, dpi / 96.0);
        }
        catch (Exception e) { App.Log("indicator: " + e.Message); }
    }

    // AutoHotkey's Round() sends .5 away from zero; C#'s default goes to even and lands 1 px off the logo.
    private static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private void Draw(int count, int idx, int tx, int ty, int th, double s)
    {
        // Windows logo geometry, measured at 150%: panes 17 px, gap 1 px, centre x = 32 logical px.
        const double LogoCx = 32, LogoPane = 17 / 1.5, LogoGap = 1 / 1.5;
        int pane = R(LogoPane * s);
        int gap = Math.Max(1, R(LogoGap * s));
        int size = 2 * pane + gap;
        int x = tx + R(LogoCx * s - size / 2.0);
        int y = ty + R(th / 2.0 - size / 2.0);

        // Which 4 desktops to show: pages of 4, the last page always full.
        int first = Math.Min(Math.Max(idx, 0) / 4 * 4, Math.Max(count - 4, 0));

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            using var dim = new SolidBrush(Color.FromArgb(0x80, 0, 0, 0));
            using var text = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", (float)(pane * 0.72), FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < 4; i++)
            {
                int px = i % 2 * (pane + gap), py = i / 2 * (pane + gap);
                int d = first + i;
                if (d >= count) { g.FillRectangle(dim, px, py, pane, pane); continue; }
                if (d != idx) g.FillRectangle(dim, px, py, pane, pane);
                g.DrawString((d + 1).ToString(), font, text, new RectangleF(px, py + pane * 0.04f, pane, pane), fmt);
            }
        }
        _window.Update(bmp, x, y);
    }

    public void Dispose()
    {
        _timer.Stop();
        _window.Dispose();
    }
}
