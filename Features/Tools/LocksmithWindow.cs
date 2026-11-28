using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aestoys.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Aestoys.Features.Tools;

/// <summary>
/// "What's using this file?" window. Explorer starts one aestoys process per selected item, so the
/// first one becomes the window and the others hand their path over a pipe and exit.
/// </summary>
internal sealed class LocksmithWindow : FluentWindow
{
    private const string MutexName = @"Local\aestoys.locksmith";
    private static string PipeName => $"aestoys.locksmith.{Process.GetCurrentProcess().SessionId}";

    private static Mutex? _mutex;
    private readonly List<string> _paths = new();
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 14, 0) };   // room for the scrollbar
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _targets = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
    private CancellationTokenSource? _scan;
    private readonly System.Windows.Threading.DispatcherTimer _batch;

    /// <summary>Entry point for "--locksmith path…". Returns false if another window took the paths.</summary>
    public static bool Run(IEnumerable<string> paths, out Mutex? mutex)
    {
        mutex = _mutex = new Mutex(false, MutexName, out _);
        bool first;
        try { first = mutex.WaitOne(0); } catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            // Hand the paths to the window that is already open (retry briefly while it starts).
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(250);
                    using var w = new StreamWriter(client);
                    w.Write(string.Join("\n", paths));
                    return false;
                }
                catch { Thread.Sleep(100); }
            }
            return false;
        }
        var window = new LocksmithWindow(paths);
        window.Show();
        return true;
    }

    private LocksmithWindow(IEnumerable<string> paths)
    {
        Title = "File Locksmith";
        Width = 640; Height = 520; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        // Explorer passes drives as "C:\" which arrives as C:" (the backslash escapes the quote).
        _paths.AddRange(paths.Select(p => p.Trim().TrimEnd('"')).Select(p => p.Length == 2 && p[1] == ':' ? p + "\\" : p)
                             .Where(p => !string.IsNullOrWhiteSpace(p)));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TitleBar { Title = "File Locksmith", ShowMaximize = false });

        var body = new DockPanel { Margin = new Thickness(20, 8, 20, 16) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        var header = new TextBlock { Text = "What's using these files", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _targets.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        top.Children.Add(header);
        top.Children.Add(_targets);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var reload = new Button { Content = "Reload", Icon = new SymbolIcon(SymbolRegular.ArrowClockwise24), Margin = new Thickness(0, 0, 8, 0) };
        reload.Click += (_, _) => Scan();
        buttons.Children.Add(reload);
        var endAll = new Button { Content = "End all", Icon = new SymbolIcon(SymbolRegular.Dismiss24), Margin = new Thickness(0, 0, 8, 0) };
        endAll.Click += async (_, _) => await EndAll();
        buttons.Children.Add(endAll);
        if (!Elevation.IsElevated)
        {
            var admin = new Button { Content = "Restart as administrator", Icon = new SymbolIcon(SymbolRegular.ShieldKeyhole24) };
            admin.ToolTip = "See processes of other users and apps running as administrator";
            admin.Click += (_, _) => RestartElevated();
            buttons.Children.Add(admin);
        }
        top.Children.Add(buttons);
        top.Children.Add(_status);
        body.Children.Add(top);

        body.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _batch = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => { _batch!.Stop(); Scan(); }, Dispatcher);
        StartPipeServer();
        Loaded += (_, _) => _batch.Start();   // wait briefly for the other selected items to arrive
    }

    private void StartPipeServer()
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var more = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var p in more) if (!_paths.Contains(p, StringComparer.OrdinalIgnoreCase)) _paths.Add(p);
                        _batch.Stop(); _batch.Start();
                        Activate();
                    });
                }
                catch { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "locksmith pipe" }.Start();
    }

    private async void Scan()
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        _targets.Text = _paths.Count == 1 ? _paths[0] : $"{_paths.Count} items: " + string.Join(", ", _paths.Select(Path.GetFileName).Take(6)) + (_paths.Count > 6 ? "…" : "");
        _status.Text = "Looking for processes…";
        _list.Children.Clear();
        var paths = _paths.ToList();
        List<LockingProcess> result;
        try { result = await Task.Run(() => LocksmithScanner.Find(paths)); }
        catch (Exception e) { _status.Text = "Couldn't scan: " + e.Message; return; }
        if (cts.IsCancellationRequested) return;
        Show(result);
    }

    private void Show(List<LockingProcess> processes)
    {
        _list.Children.Clear();
        _status.Text = processes.Count == 0
            ? "Nothing is using these files." + (Elevation.IsElevated ? "" : " (Apps running as administrator are only visible after Restart as administrator.)")
            : $"{processes.Count} process{(processes.Count == 1 ? "" : "es")} using these files";
        foreach (var p in processes) _list.Children.Add(Card(p));
        if (_pendingNote is not null && processes.Count > 0) _status.Text = _pendingNote;
        _pendingNote = null;
    }

    private UIElement Card(LockingProcess p)
    {
        var files = new StackPanel();
        foreach (var f in p.Files.Take(50))
        {
            var t = new TextBlock { Text = f, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            files.Children.Add(t);
        }
        if (p.Files.Count > 50)
            files.Children.Add(new TextBlock { Text = $"…and {p.Files.Count - 50} more", FontSize = 12 });
        if (p.ExePath is not null)
        {
            var exe = new TextBlock { Text = p.ExePath, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            exe.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            files.Children.Add(exe);
        }

        // A quiet button with red text, rather than a solid red block next to every row.
        var endIcon = new SymbolIcon(SymbolRegular.Dismiss16) { FontSize = 12 };
        var endText = new TextBlock { Text = "End task", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var endContent = new StackPanel { Orientation = Orientation.Horizontal };
        endContent.Children.Add(endIcon);
        endContent.Children.Add(endText);
        var end = new Button
        {
            Content = endContent,
            Appearance = ControlAppearance.Secondary,
            Padding = new Thickness(12, 5, 12, 6),
            Margin = new Thickness(12, 0, 12, 0),
            MinWidth = 0,
        };
        endIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "SystemFillColorCriticalBrush");
        endText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        // Warning shown under the name when the polite close didn't work.
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        bool force = false;
        System.Windows.Automation.AutomationProperties.SetName(end, "End task");
        end.Click += async (_, _) =>
        {
            end.IsEnabled = false;
            if (force)
            {
                await Task.Run(() => Kill(p.Pid));
                Scan();
                return;
            }
            endText.Text = "Closing…";
            var outcome = await Task.Run(() => AskToClose(p.Pid));
            if (outcome == CloseOutcome.Closed) { Scan(); return; }
            // Still running: offer Force end as a separate, deliberate click.
            force = true;
            endText.Text = "Force end";
            System.Windows.Automation.AutomationProperties.SetName(end, "Force end");
            note.Text = (outcome == CloseOutcome.NoWindow
                            ? "This app has no window to close."
                            : "It didn't close. It may be asking to save; check its window first.")
                        + " Force end closes it immediately and loses unsaved work."
                        + (IsShell(p) ? " Ending Explorer also restarts the taskbar." : "");
            note.Visibility = Visibility.Visible;
            end.IsEnabled = true;
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new System.Windows.Controls.Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0), Source = IconOf(p.ExePath) };
        header.Children.Add(icon);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var sub = new TextBlock { Text = $"PID {p.Pid}" + (p.User != "" ? $" · {p.User}" : "") + $" · {p.Files.Count} file{(p.Files.Count == 1 ? "" : "s")}", FontSize = 12 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        title.Children.Add(name);
        title.Children.Add(sub);
        title.Children.Add(note);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(end, 2);
        end.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(end);

        return new CardExpander { Header = header, Content = files, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(16, 10, 12, 10), IsExpanded = false };
    }

    private static ImageSource? IconOf(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); p.WaitForExit(3000); }
        catch (Exception e) { App.Log($"locksmith: end {pid}: {e.Message}"); }
    }

    private enum CloseOutcome { Closed, StillRunning, NoWindow }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc fn, IntPtr l);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr l);

    // Closing these would close the taskbar/desktop or open the shut-down dialog.
    private static readonly string[] ShellClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" };

    private static bool IsShell(LockingProcess p) => p.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks the app to close the way clicking its X does (WM_CLOSE to its visible top-level windows),
    /// so it can offer to save. Waits up to 6 s for it to exit.
    /// </summary>
    private static CloseOutcome AskToClose(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            Aestoys.Interop.Native.GetWindowThreadProcessId(h, out uint owner);
            if (owner == (uint)pid && Aestoys.Interop.Native.IsWindowVisible(h)
                && Aestoys.Interop.Native.GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero
                && !ShellClasses.Contains(Aestoys.Interop.Win32.ClassName(h)))
                windows.Add(h);
            return true;
        }, IntPtr.Zero);
        if (windows.Count == 0) return CloseOutcome.NoWindow;
        foreach (var h in windows) PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.WaitForExit(6000)) return CloseOutcome.Closed;
            // Explorer keeps running after its folder windows close; that is enough to free the files.
            if (windows.All(h => !Aestoys.Interop.Native.IsWindow(h))) return CloseOutcome.Closed;
            return CloseOutcome.StillRunning;
        }
        catch { return CloseOutcome.Closed; }   // already gone
    }

    /// <summary>Politely closes every listed app at once, then rescans; whatever is left gets its own Force end button.</summary>
    private async Task EndAll()
    {
        var result = await LocksmithScannerSnapshot();
        if (result.Count == 0) { Scan(); return; }
        _status.Text = $"Asking {result.Count} app{(result.Count == 1 ? "" : "s")} to close…";
        var outcomes = await Task.WhenAll(result.Select(p => Task.Run(() => AskToClose(p.Pid))));
        int left = outcomes.Count(o => o != CloseOutcome.Closed);
        Scan();
        if (left > 0) _pendingNote = $"{left} app{(left == 1 ? "" : "s")} didn't close (no window, or waiting for you to save). Use End task on each to see the Force end option.";
    }

    private string? _pendingNote;

    private Task<List<LockingProcess>> LocksmithScannerSnapshot()
    {
        var paths = _paths.ToList();
        return Task.Run(() => LocksmithScanner.Find(paths));
    }

    private void RestartElevated()
    {
        try
        {
            var args = "--locksmith " + string.Join(" ", _paths.Select(p => "\"" + p.TrimEnd('\\') + "\""));
            // Let the elevated copy become the window instead of handing its paths back to us.
            try { _mutex?.ReleaseMutex(); } catch { }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch { /* UAC declined */ }
    }
}
