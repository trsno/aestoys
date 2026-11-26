using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Aestoys.Services;
using Wpf.Ui.Controls;

namespace Aestoys.Pages;

public partial class GeneralPage : Page
{
    private bool _loading = true;

    public GeneralPage()
    {
        InitializeComponent();
        var s = App.Settings;
        StartupToggle.IsChecked = s.StartWithWindows;
        AdminToggle.IsChecked = s.RunAsAdmin;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"aestoys {v?.ToString(3)} · {(Elevation.IsElevated ? "running as administrator" : "running as standard user")}";
        _loading = false;
    }

    private void Set(bool loadingValue, ToggleSwitch toggle, bool value)
    {
        _loading = true;
        toggle.IsChecked = value;
        _loading = loadingValue;
    }

    private void Status(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        bool want = StartupToggle.IsChecked == true;
        bool hadTask = s.StartWithWindows && s.RunAsAdmin;
        if (Startup.Apply(want, s.RunAsAdmin, hadTask))
        {
            s.StartWithWindows = want;
            s.Save();
            StatusBar.IsOpen = false;
        }
        else
        {
            Set(false, StartupToggle, s.StartWithWindows);
            Status("Couldn't change the startup setting. Administrator permission is needed when \"Run as administrator\" is on.", InfoBarSeverity.Warning);
        }
    }

    private void OnAdminChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        bool want = AdminToggle.IsChecked == true;

        // Switch the startup entry between the Run key (normal) and a scheduled task (elevated).
        if (s.StartWithWindows && !Startup.Apply(true, want, hadTask: s.RunAsAdmin))
        {
            Set(false, AdminToggle, s.RunAsAdmin);
            Status("Windows didn't grant administrator permission, so nothing was changed.", InfoBarSeverity.Warning);
            return;
        }

        s.RunAsAdmin = want;
        s.Save();

        if (want && !Elevation.IsElevated)
        {
            if (!((App)Application.Current).RestartElevated(tray: false))
                Status("Saved. aestoys will ask for administrator permission next time it starts.", InfoBarSeverity.Informational);
        }
        else if (!want && Elevation.IsElevated)
        {
            Status("Saved. aestoys is still running as administrator until you quit and start it again.", InfoBarSeverity.Informational);
        }
        else StatusBar.IsOpen = false;
    }

    private void OnQuit(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();
}
