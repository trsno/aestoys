using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aestoys.Interop;
using Aestoys.Services;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Aestoys.Features.Tools;

/// <summary>
/// Canvas zone editor (Win+Shift+`): covers the monitor's work area. Drag a zone to move it, drag
/// its corner to resize, Delete removes the selected zone. Saving stores the zones in this
/// monitor's pixels and assigns the layout to the monitor. Esc closes without saving.
/// </summary>
internal sealed class ZoneEditorWindow : Window
{
    private readonly FancyZones _fz;
    private readonly AppSettings _settings;
    private readonly FancyZones.Monitor _mon;
    private readonly Canvas _canvas = new();
    private readonly ComboBox _layouts = new() { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
    private readonly System.Windows.Controls.TextBox _name = new() { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0) };
    private readonly List<Border> _zones = new();
    private Border? _selected;
    private ZoneLayout _editing = new();

    private FancyZonesSettings S => _settings.Tools.FancyZones;

    public ZoneEditorWindow(FancyZones fz, AppSettings settings, FancyZones.Monitor mon)
    {
        _fz = fz;
        _settings = settings;
        _mon = mon;
        Title = "Zone editor";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        KeyDown += OnKeyDown;

        var root = new Grid();
        root.Children.Add(_canvas);
        _canvas.Background = Brushes.Transparent;
        _canvas.MouseLeftButtonDown += (_, e) => { if (e.Source == _canvas) Select(null); };
        root.Children.Add(Toolbar());
        Content = root;

        SourceInitialized += (_, _) =>
        {
            // Position in physical pixels: WPF's DIP placement is unreliable across mixed-DPI monitors.
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, Win32.HWND_TOPMOST, mon.Work.Left, mon.Work.Top, mon.Work.Width, mon.Work.Height, Win32.SWP_NOACTIVATE);
        };
        Loaded += (_, _) => { FillLayouts(_fz.LayoutFor(_mon)?.Id); Keyboard.Focus(this); };
    }

    private double Scale => _mon.Scale;

    private UIElement Toolbar()
    {
        var bar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        };
        bar.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _layouts.SelectionChanged += (_, _) => { if (_layouts.SelectedItem is ComboBoxItem { Tag: ZoneLayout l }) Load(l); };
        row.Children.Add(_layouts);
        row.Children.Add(_name);
        row.Children.Add(Btn("Add zone", Wpf.Ui.Controls.SymbolRegular.Add24, () => { var z = AddZone(new Rect(ActualWidth / 2 - 200, ActualHeight / 2 - 150, 400, 300)); Select(z); }));
        row.Children.Add(Btn("Delete zone", Wpf.Ui.Controls.SymbolRegular.Delete24, DeleteSelected));
        var save = Btn("Save and apply", Wpf.Ui.Controls.SymbolRegular.Save24, Save);
        save.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        row.Children.Add(save);
        row.Children.Add(Btn("Close", Wpf.Ui.Controls.SymbolRegular.Dismiss24, Close));
        var hint = new TextBlock { Text = "Drag zones to move · drag a corner to resize · Esc closes without saving", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(hint);
        bar.Child = stack;
        return bar;
    }

    private static Button Btn(string text, Wpf.Ui.Controls.SymbolRegular icon, Action click)
    {
        var b = new Button { Content = text, Icon = new Wpf.Ui.Controls.SymbolIcon(icon), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void FillLayouts(string? select)
    {
        _layouts.Items.Clear();
        foreach (var l in S.Layouts)
            _layouts.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l });
        _layouts.Items.Add(new ComboBoxItem { Content = "New layout", Tag = new ZoneLayout { Name = "New layout", RefWidth = _mon.Work.Width, RefHeight = _mon.Work.Height } });
        var pick = _layouts.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((ZoneLayout)i.Tag).Id == select) ?? (ComboBoxItem)_layouts.Items[0];
        _layouts.SelectedItem = pick;
    }

    private void Load(ZoneLayout layout)
    {
        _editing = layout;
        _name.Text = layout.Name;
        foreach (var z in _zones) _canvas.Children.Remove(z);
        _zones.Clear();
        Select(null);
        var work = new System.Drawing.Rectangle(0, 0, _mon.Work.Width, _mon.Work.Height);
        foreach (var r in FancyZones.ZonesFor(layout, work))
            AddZone(new Rect(r.X / Scale, r.Y / Scale, r.Width / Scale, r.Height / Scale));
    }

    private Border AddZone(Rect r)
    {
        var label = new TextBlock { FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, Opacity = 0.8 };
        var grip = new Thumb { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Margin = new Thickness(0, 0, 2, 2) };
        grip.Template = GripTemplate();
        var inner = new Grid();
        inner.Children.Add(label);
        inner.Children.Add(grip);
        var zone = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x20, 0x20)), BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            Child = inner, Cursor = Cursors.SizeAll,
        };
        Canvas.SetLeft(zone, r.X);
        Canvas.SetTop(zone, r.Y);

        Point? dragFrom = null;
        zone.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, grip)) return;
            Select(zone);
            dragFrom = e.GetPosition(_canvas);
            zone.CaptureMouse();
            e.Handled = true;
        };
        zone.MouseMove += (_, e) =>
        {
            if (dragFrom is not { } from || !zone.IsMouseCaptured) return;
            var p = e.GetPosition(_canvas);
            Canvas.SetLeft(zone, Math.Clamp(Canvas.GetLeft(zone) + p.X - from.X, 0, Math.Max(0, ActualWidth - zone.Width)));
            Canvas.SetTop(zone, Math.Clamp(Canvas.GetTop(zone) + p.Y - from.Y, 0, Math.Max(0, ActualHeight - zone.Height)));
            dragFrom = p;
        };
        zone.MouseLeftButtonUp += (_, _) => { dragFrom = null; zone.ReleaseMouseCapture(); };
        grip.DragStarted += (_, _) => Select(zone);
        grip.DragDelta += (_, e) =>
        {
            zone.Width = Math.Clamp(zone.Width + e.HorizontalChange, 60, ActualWidth - Canvas.GetLeft(zone));
            zone.Height = Math.Clamp(zone.Height + e.VerticalChange, 60, ActualHeight - Canvas.GetTop(zone));
        };

        _zones.Add(zone);
        _canvas.Children.Add(zone);
        Renumber();
        return zone;
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x)) if (x == ancestor) return true;
        return false;
    }

    private static ControlTemplate GripTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        t.VisualTree = f;
        return t;
    }

    private void Select(Border? zone)
    {
        _selected = zone;
        var accent = SystemParameters.WindowGlassColor;
        foreach (var z in _zones)
        {
            bool on = z == zone;
            z.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(accent.R, accent.G, accent.B) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            z.BorderThickness = new Thickness(on ? 3 : 2);
        }
        if (zone is not null) { _canvas.Children.Remove(zone); _canvas.Children.Add(zone); }
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        _canvas.Children.Remove(_selected);
        _zones.Remove(_selected);
        _selected = null;
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < _zones.Count; i++)
            if (_zones[i].Child is Grid g && g.Children[0] is TextBlock t) t.Text = (i + 1).ToString();
    }

    private void Save()
    {
        var layout = _editing;
        layout.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Layout" : _name.Text.Trim();
        layout.RefWidth = _mon.Work.Width;
        layout.RefHeight = _mon.Work.Height;
        layout.Zones = _zones.Select(z => new ZoneRect
        {
            X = (int)Math.Round(Canvas.GetLeft(z) * Scale), Y = (int)Math.Round(Canvas.GetTop(z) * Scale),
            Width = (int)Math.Round(z.Width * Scale), Height = (int)Math.Round(z.Height * Scale),
        }).ToList();
        if (!S.Layouts.Contains(layout)) S.Layouts.Add(layout);
        S.MonitorLayouts[_mon.Key] = layout.Id;
        _settings.Save();
        Shell.Toast.Show($"Saved \"{layout.Name}\" for this monitor");
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.TextBox) { DeleteSelected(); e.Handled = true; }
    }
}
