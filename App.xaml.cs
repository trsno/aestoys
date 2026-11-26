using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Aestoys.Features.AltTab;
using Aestoys.Services;
using Wpf.Ui.Appearance;
using Forms = System.Windows.Forms;

namespace Aestoys;

public partial class App : Application
{
    private const string MutexName = @"Local\aestoys.single-instance";
    private const string ShowEventName = @"Local\aestoys.show-settings";
    private const string DemoEventName = @"Local\aestoys.demo";
    private const string DemoAllEventName = @"Local\aestoys.demo-all";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _main;

    internal static AppSettings Settings { get; private set; } = new();
    internal static AltTabController? AltTab { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool waitForPrevious = e.Args.Contains("--wait-mutex", StringComparer.OrdinalIgnoreCase);
        _mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = _mutex.WaitOne(waitForPrevious ? 15000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Already running: ask that instance to show its settings window.
            bool demoAll = e.Args.Contains("--demo-all", StringComparer.OrdinalIgnoreCase);
            bool demo = demoAll || e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
            string evt = demoAll ? DemoAllEventName : demo ? DemoEventName : ShowEventName;
            try { EventWaitHandle.OpenExisting(evt).Set(); } catch { }
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception.ToString()); ex.Handled = true; };
        Settings = AppSettings.Load();
        if (!Settings.StartWithWindows && Services.Startup.RunKeyExists) Settings.StartWithWindows = true; // migrate 0.1 setting

        // "Run as administrator": hand over to an elevated copy. If UAC is declined, keep running normally.
        if (Settings.RunAsAdmin && !Elevation.IsElevated && !e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            if (RestartElevated(e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))) return;
        }

        try { AltTab = new AltTabController(Settings, Dispatcher); }
        catch (Exception ex) { MessageBox.Show("Could not install the keyboard hook:\n" + ex.Message, "aestoys"); }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowSettings);
        }) { IsBackground = true };
        listener.Start();

        var demoEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DemoEventName);
        new Thread(() =>
        {
            while (demoEvent.WaitOne())
                Dispatcher.BeginInvoke(() => AltTab?.Demo(3));
        }) { IsBackground = true }.Start();
        var demoAllEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DemoAllEventName);
        new Thread(() =>
        {
            while (demoAllEvent.WaitOne())
                Dispatcher.BeginInvoke(() => AltTab?.Demo(3, allMonitors: true));
        }) { IsBackground = true }.Start();

        CreateTray();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowSettings();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Text = "aestoys",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowSettings(); };
    }

    internal void ShowSettings()
    {
        if (_main is null)
        {
            ApplicationThemeManager.ApplySystemTheme();
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    internal static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Starts an elevated copy and shuts this one down. Returns false if UAC was declined.</summary>
    internal bool RestartElevated(bool tray)
    {
        if (!Elevation.RelaunchElevated(tray ? "--tray --wait-mutex" : "--wait-mutex")) return false;
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex = null;
        ExitApp();
        return true;
    }

    internal void ExitApp()
    {
        AltTab?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AltTab?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
