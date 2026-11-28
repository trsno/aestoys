using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Aestoys.Features.AltTab;
using Aestoys.Features.Desktops;
using Aestoys.Interop;
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
    private const string DemoLauncherEventName = @"Local\aestoys.demo-launcher";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _main;

    internal static AppSettings Settings { get; private set; } = new();
    internal static AltTabController? AltTab { get; private set; }
    internal static KeyboardHook? Hook { get; private set; }
    /// <summary>Settings page to open first (--page alttab|desktops|general).</summary>
    internal static string? StartPage { get; private set; }
    internal static DesktopController? Desktops { get; private set; }
    internal static WindowPlacement? Placement { get; private set; }
    internal static StartLauncher? Launcher { get; private set; }
    internal static HotkeyRouter? Hotkeys { get; private set; }
    internal static MouseHook? Mouse { get; private set; }
    internal static Features.Tools.Awake? Awake { get; private set; }
    internal static Features.Tools.AlwaysOnTop? OnTop { get; private set; }
    internal static Features.Tools.ColorPicker? ColorPick { get; private set; }
    internal static Features.Tools.CropAndLock? Crop { get; private set; }
    internal static Features.Tools.GrabAndMove? Grab { get; private set; }
    internal static Features.Tools.FancyZones? Zones { get; private set; }
    internal static Features.Clipboard.ClipboardHistory? Clips { get; private set; }
    internal static Features.Tools.WindowTransparency? Glass { get; private set; }
    private const string ClipboardEventName = @"Local\aestoys.clipboard";
    private const string DemoZonesEventName = @"Local\aestoys.demo-zones";
    private const string DemoColorEventName = @"Local\aestoys.demo-colorpicker";
    private const string ColorEditorEventName = @"Local\aestoys.color-editor";

    private const string ExitEventName = @"Local\aestoys.exit";

    protected override void OnStartup(StartupEventArgs e)
    {
        // File Locksmith runs as its own short-lived window, separate from the tray instance.
        int lockIdx = Array.FindIndex(e.Args, a => a.Equals("--locksmith", StringComparison.OrdinalIgnoreCase));
        if (lockIdx >= 0)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ex) => { Log("locksmith: " + ex.Exception); ex.Handled = true; };
            Settings = AppSettings.Load();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            if (!Features.Tools.LocksmithWindow.Run(e.Args.Skip(lockIdx + 1), out _)) Shutdown();
            return;
        }

        // --exit: ask the running copy to quit cleanly (undoing what it changed on other windows).
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { }
            Shutdown();
            return;
        }

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
            bool demoLauncher = e.Args.Contains("--demo-launcher", StringComparer.OrdinalIgnoreCase);
            bool demoColor = e.Args.Contains("--demo-colorpicker", StringComparer.OrdinalIgnoreCase);
            bool colorEditor = e.Args.Contains("--color-editor", StringComparer.OrdinalIgnoreCase);
            bool demoZones = e.Args.Contains("--demo-zones", StringComparer.OrdinalIgnoreCase);
            bool clipboard = e.Args.Contains("--clipboard", StringComparer.OrdinalIgnoreCase);
            string evt = clipboard ? ClipboardEventName : demoZones ? DemoZonesEventName : colorEditor ? ColorEditorEventName : demoColor ? DemoColorEventName : demoLauncher ? DemoLauncherEventName : demoAll ? DemoAllEventName : demo ? DemoEventName : ShowEventName;
            try { EventWaitHandle.OpenExisting(evt).Set(); } catch { }
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception.ToString()); ex.Handled = true; };
        Settings = AppSettings.Load();
        if (!Settings.StartWithWindows && Services.Startup.RunKeyExists) Settings.StartWithWindows = true; // migrate 0.1 setting
        if (e.Args.Contains("--import-powertoys", StringComparer.OrdinalIgnoreCase))
        {
            try { Log("import: " + Features.Tools.PowerToysImport.Run(Settings)); }
            catch (Exception ex) { Log("import failed: " + ex); }
        }
        if (e.Args.Contains("--import-ditto", StringComparer.OrdinalIgnoreCase))
        {
            try { Log("import: " + Features.Clipboard.DittoImport.Run(Settings)); }
            catch (Exception ex) { Log("Ditto import failed: " + ex); }
        }
        if (e.Args.Contains("--import-glass2k", StringComparer.OrdinalIgnoreCase))
        {
            try { Log(Features.Tools.WindowTransparency.ImportGlass2k(Settings)); }
            catch (Exception ex) { Log("Glass2k import failed: " + ex); }
        }

        // "Run as administrator": hand over to an elevated copy. If UAC is declined, keep running normally.
        if (Settings.RunAsAdmin && !Elevation.IsElevated && !e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase))
        {
            if (RestartElevated(e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))) return;
        }

        try
        {
            Hook = new KeyboardHook();
            AltTab = new AltTabController(Settings, Dispatcher, Hook);
        }
        catch (Exception ex) { MessageBox.Show("Could not install the keyboard hook:\n" + ex.Message, "aestoys"); }
        try
        {
            if (Hook is not null) Desktops = new DesktopController(Settings, Dispatcher, Hook);
            Placement = new WindowPlacement(Settings);
        }
        catch (Exception ex) { Log("desktop features: " + ex); }
        try
        {
            if (Hook is not null) Hotkeys = new HotkeyRouter(Hook, Dispatcher);
            Mouse = new MouseHook();
            Awake = new Features.Tools.Awake(Settings);
            if (Hotkeys is not null) OnTop = new Features.Tools.AlwaysOnTop(Settings, Hotkeys);
            if (Hotkeys is not null && Hook is not null && Mouse is not null) { ColorPick = new Features.Tools.ColorPicker(Settings, Hotkeys, Hook, Mouse); Crop = new Features.Tools.CropAndLock(Settings, Hotkeys, Hook, Mouse); Grab = new Features.Tools.GrabAndMove(Settings, Hook, Mouse); }
            if (Hotkeys is not null) Zones = new Features.Tools.FancyZones(Settings, Hotkeys);
            if (Hotkeys is not null) Clips = new Features.Clipboard.ClipboardHistory(Settings, Hotkeys);
            if (Hotkeys is not null) Glass = new Features.Tools.WindowTransparency(Settings, Hotkeys);
        }
        catch (Exception ex) { Log("tools: " + ex); }
        ApplyFeatureSettings();
        DesktopController.ExplorerRestarted += OnExplorerRestarted;

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

        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            if (exitEvent.WaitOne()) Dispatcher.BeginInvoke(ExitApp);
        }) { IsBackground = true }.Start();

        var demoLauncherEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DemoLauncherEventName);
        new Thread(() =>
        {
            while (demoLauncherEvent.WaitOne())
                Dispatcher.BeginInvoke(() => Launcher?.Demo(4));
        }) { IsBackground = true }.Start();

        var demoColorEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DemoColorEventName);
        int demoZoom = 0;
        new Thread(() =>
        {
            while (demoColorEvent.WaitOne())
                Dispatcher.BeginInvoke(() => ColorPick?.Demo(demoZoom++ % 2 == 0 ? 0 : 2));
        }) { IsBackground = true }.Start();

        var clipboardEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ClipboardEventName);
        new Thread(() =>
        {
            while (clipboardEvent.WaitOne())
                Dispatcher.BeginInvoke(() => Clips?.Show());
        }) { IsBackground = true }.Start();

        var demoZonesEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DemoZonesEventName);
        int zoneDemo = -1;
        new Thread(() =>
        {
            while (demoZonesEvent.WaitOne())
                Dispatcher.BeginInvoke(() => Zones?.Demo(zoneDemo++ % 3));
        }) { IsBackground = true }.Start();

        var colorEditorEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ColorEditorEventName);
        new Thread(() =>
        {
            while (colorEditorEvent.WaitOne())
                Dispatcher.BeginInvoke(() => ColorPick?.OpenEditor());
        }) { IsBackground = true }.Start();

        CreateTray();

        int page = Array.FindIndex(e.Args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        if (page >= 0 && page + 1 < e.Args.Length) StartPage = e.Args[page + 1];
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowSettings();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        var awake = new Forms.ToolStripMenuItem("Keep awake");
        menu.Items.Add(awake);
        menu.Opening += (_, _) => FillAwakeMenu(awake);
        FillAwakeMenu(awake);
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

    /// <summary>Keep awake submenu: the current state is checked, and picking a mode switches the feature on.</summary>
    private static void FillAwakeMenu(Forms.ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        var s = Settings.Tools.Awake;
        root.Visible = Awake is not null;
        if (Awake is null) return;
        root.Checked = Awake.Active;
        void Item(string text, bool check, Action act)
        {
            var i = new Forms.ToolStripMenuItem(text) { Checked = check };
            i.Click += (_, _) => { s.Enabled = true; act(); };
            root.DropDownItems.Add(i);
        }
        if (Awake.Active) { var info = new Forms.ToolStripMenuItem(Awake.Describe()) { Enabled = false }; root.DropDownItems.Add(info); root.DropDownItems.Add(new Forms.ToolStripSeparator()); }
        Item("Off", !Awake.Active, Awake.SetOff);
        Item("Indefinitely", s.Enabled && s.Mode == AwakeMode.Indefinite, Awake.SetIndefinite);
        Item("For 30 minutes", false, () => Awake.SetFor(TimeSpan.FromMinutes(30)));
        Item("For 1 hour", false, () => Awake.SetFor(TimeSpan.FromHours(1)));
        Item("For 2 hours", false, () => Awake.SetFor(TimeSpan.FromHours(2)));
        Item("For 4 hours", false, () => Awake.SetFor(TimeSpan.FromHours(4)));
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        var display = new Forms.ToolStripMenuItem("Keep screen on") { Checked = s.KeepDisplayOn };
        display.Click += (_, _) => Awake.SetKeepDisplayOn(!s.KeepDisplayOn);
        root.DropDownItems.Add(display);
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

    /// <summary>Starts or stops the desktop, placement and launcher features to match the settings.</summary>
    internal static void ApplyFeatureSettings()
    {
        Desktops?.ApplySettings();
        Placement?.ApplySettings();
        if (Settings.Launcher.Enabled) { if (Launcher is null) Launcher = new StartLauncher(Settings); else Launcher.Reload(); }
        else { Launcher?.Dispose(); Launcher = null; }
        Awake?.Apply();
        OnTop?.ApplySettings();
        Zones?.ApplySettings();
        Clips?.ApplySettings();
        Glass?.ApplySettings();
        Features.Tools.LocksmithMenu.Apply(Settings.Tools.Locksmith.Enabled, Settings.Tools.Locksmith.ExtendedMenuOnly);
    }

    private System.Windows.Threading.DispatcherTimer? _explorerTimer;

    /// <summary>
    /// Explorer restarted: the DLL and the COM objects still point at the old Explorer, and rebuilding
    /// them in place wasn't reliable (the AHK version learned this). Once Explorer has settled, start
    /// a fresh copy and end this one without the normal COM cleanup, which Windows can kill with a
    /// fail-fast in combase.dll. Repeated broadcasts push the timer back.
    /// </summary>
    private void OnExplorerRestarted()
    {
        _explorerTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => RestartAfterExplorer(), Dispatcher);
        _explorerTimer.Stop();
        _explorerTimer.Start();
    }

    private void RestartAfterExplorer()
    {
        _explorerTimer?.Stop();
        Log("Explorer restarted: restarting aestoys");
        try { Placement?.Dispose(); } catch { }
        try { Desktops?.Dispose(); } catch { }
        try { Launcher?.Dispose(); } catch { }
        try { OnTop?.Dispose(); } catch { }      // un-pin and restore opacity; the new copy starts clean
        try { Mouse?.Dispose(); } catch { }
        try { Hook?.Dispose(); } catch { }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        try
        {
            System.Diagnostics.Process.Start(Environment.ProcessPath!, "--tray --wait-mutex" + (Elevation.IsElevated ? "" : " --no-elevate"));
        }
        catch (Exception e) { Log("restart failed: " + e.Message); return; }
        Win32.TerminateProcess(Win32.GetCurrentProcess(), 0);
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
        DisposeFeatures();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    private static void DisposeFeatures()
    {
        try { AltTab?.Dispose(); } catch { }
        try { Desktops?.Dispose(); } catch { }
        try { Placement?.Dispose(); } catch { }
        try { Launcher?.Dispose(); } catch { }
        try { OnTop?.Dispose(); } catch { }
        try { ColorPick?.Dispose(); } catch { }
        try { Crop?.Dispose(); } catch { }
        try { Zones?.Dispose(); } catch { }
        try { Clips?.Dispose(); } catch { }
        try { Glass?.Dispose(); } catch { }
        try { Awake?.Dispose(); } catch { }
        try { Mouse?.Dispose(); } catch { }
        try { Hook?.Dispose(); } catch { }
        AltTab = null; Desktops = null; Placement = null; Launcher = null; Hook = null;
        OnTop = null; Awake = null; Mouse = null; Hotkeys = null; ColorPick = null; Crop = null; Zones = null; Grab = null; Clips = null; Glass = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeFeatures();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
