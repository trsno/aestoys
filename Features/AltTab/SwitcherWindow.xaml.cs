using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aestoys.Interop;
using Microsoft.Win32;

namespace Aestoys.Features.AltTab;

internal partial class SwitcherWindow : Window, ISwitcherView
{
    public event Action<int>? ItemClicked;
#pragma warning disable CS0067
    public event Action? CancelRequested;
    public event Action<int>? Scrolled;
#pragma warning restore CS0067
    public bool IsShown => IsVisible;
    public void HideView() => Hide();
    public void CloseView() => Close();
    private IntPtr _hwnd;

    public SwitcherWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
    }


    public void ShowOn(IntPtr monitor, IReadOnlyList<SwitchItem> items, int index)
    {
        ApplyTheme();

        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        var work = mi.rcWork;
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        Scroller.MaxWidth = work.Width / scale * 0.85;
        Scroller.MaxHeight = work.Height / scale * 0.8;
        // Make whole tiles wrap evenly: tile is 156 + 2*4 margin.
        const double tile = 164;
        int perRow = Math.Max(1, (int)(Scroller.MaxWidth / tile));
        List.MaxWidth = perRow * tile;

        List.ItemsSource = items;
        bool empty = items.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        Opacity = 0;
        if (!IsVisible) Show();

        // Move onto the target monitor first so WPF picks up that monitor's DPI, then center.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();
        Native.GetWindowRect(_hwnd, out var r);
        int x = work.Left + (work.Width - r.Width) / 2;
        int y = work.Top + (work.Height - r.Height) / 2;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        Select(index);
        Opacity = 1;
    }

    public void Select(int index)
    {
        List.SelectedIndex = index;
        if (index >= 0 && List.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe)
            fe.BringIntoView();
    }

    private void List_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem item)
            ItemClicked?.Invoke(List.ItemContainerGenerator.IndexFromContainer(item));
    }

    private void ApplyTheme()
    {
        bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v == 1;
        Color accent = Color.FromRgb(0x4C, 0xC2, 0xFF);
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            accent = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));

        Resources["PanelBrush"] = new SolidColorBrush(light ? Color.FromArgb(0xF2, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xEE, 0x20, 0x20, 0x20));
        Resources["PanelBorderBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Resources["TextBrush"] = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);
        Resources["HoverBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Resources["SelectedBrush"] = new SolidColorBrush(light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Resources["AccentBrush"] = new SolidColorBrush(accent);
    }
}
