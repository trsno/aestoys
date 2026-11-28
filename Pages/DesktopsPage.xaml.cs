using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Aestoys.Features.Desktops;
using Wpf.Ui.Controls;

namespace Aestoys.Pages;

public partial class DesktopsPage : Page
{
    private bool _loading = true;

    public DesktopsPage()
    {
        InitializeComponent();
        var s = App.Settings;
        WrapToggle.IsChecked = s.Desktops.WrapAround;
        MoveToggle.IsChecked = s.Desktops.MoveWindowKeys;
        PinToggle.IsChecked = s.Desktops.PinKey;
        IndicatorToggle.IsChecked = s.Desktops.Indicator;
        CenterToggle.IsChecked = s.Placement.CenterNewWindows;
        AltSpaceToggle.IsChecked = s.Placement.AltSpaceCenters;
        LauncherToggle.IsChecked = s.Launcher.Enabled;
        LauncherExpander.IsExpanded = s.Launcher.Enabled;
        FolderBox.Text = s.Launcher.Folder;
        SettingsAppToggle.IsChecked = s.Launcher.IncludeSettingsApp;
        HoverSlider.Value = s.Launcher.HoverDelayMs;
        LeaveSlider.Value = s.Launcher.LeaveDelayMs;
        HoverText.Text = $"{s.Launcher.HoverDelayMs} ms";
        LeaveText.Text = $"{s.Launcher.LeaveDelayMs} ms";

        if (VirtualDesktops.DllLoaded)
        {
            DllBar.Severity = InfoBarSeverity.Success;
            DllBar.Title = "Direct switching available";
            DllBar.Message = $"Using VirtualDesktopAccessor.dll ({VirtualDesktops.DllPath}). It is checked against Windows before every jump.";
        }
        else
        {
            DllBar.Severity = InfoBarSeverity.Warning;
            DllBar.Title = "VirtualDesktopAccessor.dll not found";
            DllBar.Message = "Wrapping still works by stepping through the desktops behind a snapshot, but moving and pinning windows need the DLL next to aestoys.exe.";
        }
        _loading = false;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.Desktops.WrapAround = WrapToggle.IsChecked == true;
        s.Desktops.MoveWindowKeys = MoveToggle.IsChecked == true;
        s.Desktops.PinKey = PinToggle.IsChecked == true;
        s.Desktops.Indicator = IndicatorToggle.IsChecked == true;
        s.Placement.CenterNewWindows = CenterToggle.IsChecked == true;
        s.Placement.AltSpaceCenters = AltSpaceToggle.IsChecked == true;
        bool launcherWasOn = s.Launcher.Enabled;
        s.Launcher.Enabled = LauncherToggle.IsChecked == true;
        s.Launcher.IncludeSettingsApp = SettingsAppToggle.IsChecked == true;
        if (s.Launcher.Enabled && !launcherWasOn) LauncherExpander.IsExpanded = true;
        s.Save();
        App.ApplyFeatureSettings();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var s = App.Settings.Launcher;
        s.HoverDelayMs = (int)HoverSlider.Value;
        s.LeaveDelayMs = (int)LeaveSlider.Value;
        HoverText.Text = $"{s.HoverDelayMs} ms";
        LeaveText.Text = $"{s.LeaveDelayMs} ms";
        App.Settings.Save();
    }

    private void OnFolderChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || FolderBox.Text == App.Settings.Launcher.Folder) return;
        App.Settings.Launcher.Folder = FolderBox.Text.Trim();
        App.Settings.Save();
        App.ApplyFeatureSettings();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder with the launcher's shortcuts",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null,
        };
        if (dlg.ShowDialog() != true) return;
        FolderBox.Text = dlg.FolderName;
        OnFolderChanged(sender, e);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = FolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log("open folder: " + ex.Message); }
    }
}
