using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Aestoys.Interop;

namespace Aestoys.Features.Shell;

/// <summary>Taskbar and foreground-window facts shared by the indicator and the launcher.</summary>
internal static class ShellInfo
{
    public static IntPtr Taskbar() => Win32.FindWindow("Shell_TrayWnd", null);

    private static readonly Regex ShellClasses = new(
        "^(Shell_TrayWnd|Shell_SecondaryTrayWnd|Progman|WorkerW|Windows\\.UI\\.Core\\.CoreWindow|XamlExplorerHostIslandWindow|MultitaskingViewFrame|ForegroundStaging|TaskListThumbnailWnd|TopLevelWindowForOverflowXamlIsland|NotifyIconOverflowWindow)$",
        RegexOptions.Compiled);
    private static readonly Regex ShellProcesses = new(
        "^(explorer|StartMenuExperienceHost|SearchHost|SearchApp|ShellExperienceHost|ShellHost)\\.exe$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True only for a real fullscreen app (game, video, presentation) on the taskbar's monitor.
    /// All three checks must hold, because each one alone misfired:
    /// Windows reports a fullscreen state; the foreground window isn't part of the shell
    /// (taskbar, Start, Search, Task View, Alt+Tab…); and it really covers that whole monitor.
    /// </summary>
    public static bool ForegroundIsFullscreen(IntPtr tray)
    {
        if (Win32.SHQueryUserNotificationState(out int state) != 0) return false;
        if (state is not (2 or 3 or 4)) return false; // QUNS_BUSY / D3D_FULL_SCREEN / PRESENTATION_MODE
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (ShellClasses.IsMatch(Win32.ClassName(fg))) return false;
        var path = Native.GetProcessPath(fg);
        if (path is not null && ShellProcesses.IsMatch(System.IO.Path.GetFileName(path))) return false;
        IntPtr mon = Native.MonitorFromWindow(tray, Native.MONITOR_DEFAULTTONEAREST);
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != mon) return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}

/// <summary>A small tooltip-style message near the mouse (what the AHK script's ToolTip did).</summary>
internal static class Toast
{
    private static LayeredWindow? _window;
    private static DispatcherTimer? _timer;

    public static void Show(string text, int ms = 1200)
    {
        _window ??= new LayeredWindow(clickThrough: true);
        Native.GetCursorPos(out var pt);
        uint dpi = 96;
        try { Native.GetDpiForMonitor(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST), 0, out dpi, out _); } catch { }
        float s = dpi / 96f;

        using var font = new Font("Segoe UI", 9.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        var size = System.Windows.Forms.TextRenderer.MeasureText(text, font);
        int pad = (int)(10 * s), w = size.Width + pad * 2, h = size.Height + (int)(12 * s);
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = RoundRect(0.5f, 0.5f, w - 1, h - 1, 6 * s);
            using var bg = new SolidBrush(Color.FromArgb(240, 44, 44, 44));
            using var border = new Pen(Color.FromArgb(90, 255, 255, 255), 1);
            g.FillPath(bg, path);
            g.DrawPath(border, path);
            using var fg = new SolidBrush(Color.White);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, fg, new RectangleF(0, 0, w, h), fmt);
        }
        _window.Update(bmp, pt.X + (int)(16 * s), pt.Y + (int)(20 * s));

        _timer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher);
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(ms);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(object? s, EventArgs e) { _timer!.Stop(); _window?.Hide(); }

    public static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
