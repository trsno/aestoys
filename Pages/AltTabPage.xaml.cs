using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Aestoys.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Aestoys.Pages;

public partial class AltTabPage : Page
{
    private bool _loading = true;
    private readonly List<(FrameworkElement row, Func<bool> visible)> _conditional = new();

    public AltTabPage()
    {
        InitializeComponent();
        var s = App.Settings;
        EnabledToggle.IsChecked = s.AltTabPerMonitorEnabled;
        ModeCombo.SelectedIndex = s.MonitorMode == MonitorMode.Cursor ? 0 : 1;
        MinimizedToggle.IsChecked = s.IncludeMinimized;
        CoverflowToggle.IsChecked = s.Coverflow.Enabled;
        CoverflowExpander.IsExpanded = s.Coverflow.Enabled;
        BuildCoverflowSettings();
        _loading = false;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        s.AltTabPerMonitorEnabled = EnabledToggle.IsChecked == true;
        s.MonitorMode = ModeCombo.SelectedIndex == 1 ? MonitorMode.ActiveWindow : MonitorMode.Cursor;
        s.IncludeMinimized = MinimizedToggle.IsChecked == true;
        s.Save();
    }

    private void OnCoverflowToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Coverflow.Enabled = CoverflowToggle.IsChecked == true;
        App.Settings.Save();
        if (App.Settings.Coverflow.Enabled) CoverflowExpander.IsExpanded = true;
    }

    // ------------------------------------------------------------------ sub-settings

    private static CoverflowSettings C => App.Settings.Coverflow;

    private void Changed()
    {
        if (_loading) return;
        App.Settings.Save();
        foreach (var (row, visible) in _conditional) row.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildCoverflowSettings()
    {
        var p = CoverflowPanel;

        Section(p, "Switcher");
        Combo(p, "Style", "Coverflow tilts windows to the sides; Timeline stacks them into the distance",
            new[] { "Coverflow", "Timeline" }, (int)C.Style, i => C.Style = (CoverflowStyle)i);
        Slider(p, "Vertical offset", "Positive moves everything down, negative up", -500, 500, 1, C.VerticalOffset, v => C.VerticalOffset = (int)v, "0");
        Combo(p, "Window title position", null, new[] { "Bottom", "Top" }, (int)C.TitlePosition, i => C.TitlePosition = (TitlePosition)i);
        Slider(p, "Background dim", "Bigger means darker", 0, 1, 0.01, C.DimFactor, v => C.DimFactor = v, "0.00");
        Toggle(p, "Blur background", null, C.BlurBackground, v => C.BlurBackground = v);
        Slider(p, "Window size to monitor size ratio", null, 0.1, 1, 0.01, C.PreviewToMonitorRatio, v => C.PreviewToMonitorRatio = v, "0.00");

        Section(p, "Animation");
        Combo(p, "Easing", null, Enum.GetNames<Easing>(), (int)C.Easing, i => C.Easing = (Easing)i);
        Slider(p, "Duration (seconds)", null, 0.01, 2, 0.01, C.AnimationTime, v => C.AnimationTime = v, "0.00");
        Toggle(p, "Randomize durations", "Each window animates at a slightly different speed", C.RandomizeAnimationTimes, v => C.RandomizeAnimationTimes = v);

        Section(p, "Icons");
        Combo(p, "Application icon style", "Classic puts the icon next to the title; Overlay draws a large icon over the selected window",
            new[] { "Classic", "Overlay", "None" }, (int)C.IconStyle, i => C.IconStyle = (IconStyle)i);
        Conditional(Slider(p, "Overlay icon size", null, 16, 512, 1, C.OverlayIconSize, v => C.OverlayIconSize = v, "0"), () => C.IconStyle == IconStyle.Overlay);
        Conditional(Slider(p, "Overlay icon opacity", null, 0, 1, 0.01, C.OverlayIconOpacity, v => C.OverlayIconOpacity = v, "0.00"), () => C.IconStyle == IconStyle.Overlay);

        Conditional(Section(p, "Coverflow"), () => C.Style == CoverflowStyle.Coverflow);
        Conditional(Combo(p, "Looping method", "Flip Stack keeps the order; Carousel keeps the selection centred and wraps around",
            new[] { "Flip Stack", "Carousel" }, (int)C.LoopingMethod, i => C.LoopingMethod = (LoopingMethod)i), () => C.Style == CoverflowStyle.Coverflow);
        Conditional(Slider(p, "Window scaling factor", "Size of the side windows compared to the selected one", 0.3, 1, 0.01, C.CoverflowScalingFactor, v => C.CoverflowScalingFactor = v, "0.00"), () => C.Style == CoverflowStyle.Coverflow);
        Conditional(Slider(p, "Window angle", "How far the side windows are turned", 0, 89, 0.5, C.CoverflowWindowAngle, v => C.CoverflowWindowAngle = v, "0.0°"), () => C.Style == CoverflowStyle.Coverflow);
        Conditional(Slider(p, "Stack spacing", "Minimum gap between side windows when a side has too many to fit", 0, 400, 1, C.CoverflowOffsetWidth, v => C.CoverflowOffsetWidth = v, "0"), () => C.Style == CoverflowStyle.Coverflow);

        Conditional(Section(p, "Timeline"), () => C.Style == CoverflowStyle.Timeline);
        Conditional(Slider(p, "Window layout distance", null, 0, 1024, 1, C.TimelineDistance, v => C.TimelineDistance = v, "0"), () => C.Style == CoverflowStyle.Timeline);
        Conditional(Slider(p, "Window layout angle", null, 0, 60, 0.5, C.TimelineAngle, v => C.TimelineAngle = v, "0.0Â°"), () => C.Style == CoverflowStyle.Timeline);
        Conditional(Slider(p, "Window tilt angle", null, 0, 90, 0.5, C.TimelineTiltAngle, v => C.TimelineTiltAngle = v, "0.0Â°"), () => C.Style == CoverflowStyle.Timeline);
        Conditional(Toggle(p, "Enable window scaling", "Windows further back get smaller", C.TimelineScaleWithDistance, v => C.TimelineScaleWithDistance = v), () => C.Style == CoverflowStyle.Timeline);
        Conditional(Slider(p, "Window scaling factor", null, 0.3, 1, 0.01, C.TimelineScalingFactor, v => C.TimelineScalingFactor = v, "0.00"), () => C.Style == CoverflowStyle.Timeline);

        Section(p, "Keys while the switcher is open");
        var keys = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            Text = "Q or F4 closes the selected window Â· D minimizes every window on this monitor Â· mouse wheel moves the selection Â· click a window to switch to it, click the background to cancel",
        };
        keys.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        p.Children.Add(keys);

        var reset = new Wpf.Ui.Controls.Button { Content = "Reset Coverflow settings", Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) =>
        {
            bool enabled = C.Enabled;
            App.Settings.Coverflow = new CoverflowSettings { Enabled = enabled };
            App.Settings.Save();
            _loading = true;
            _conditional.Clear();
            CoverflowPanel.Children.Clear();
            BuildCoverflowSettings();
            _loading = false;
        };
        p.Children.Add(reset);

        Changed();
    }

    private void Conditional(FrameworkElement row, Func<bool> visible)
    {
        _conditional.Add((row, visible));
        row.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement Section(Panel p, string title)
    {
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        p.Children.Add(t);
        return t;
    }

    private static Grid Row(Panel p, string label, string? description, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4), MinHeight = 40 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var l = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        text.Children.Add(l);
        if (description is not null)
        {
            var d = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            text.Children.Add(d);
        }
        g.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        p.Children.Add(g);
        return g;
    }

    private Grid Toggle(Panel p, string label, string? description, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value };
        t.Checked += (_, _) => { set(true); Changed(); };
        t.Unchecked += (_, _) => { set(false); Changed(); };
        return Row(p, label, description, t);
    }

    private Grid Combo(Panel p, string label, string? description, string[] options, int selected, Action<int> set)
    {
        var c = new ComboBox { MinWidth = 200, ItemsSource = options, SelectedIndex = selected };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); Changed(); } };
        return Row(p, label, description, c);
    }

    private Grid Slider(Panel p, string label, string? description, double min, double max, double step, double value, Action<double> set, string format)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var valueText = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var s = new System.Windows.Controls.Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, TickFrequency = step,
            IsSnapToTickEnabled = true, Value = Math.Clamp(value, min, max), Width = 220, VerticalAlignment = VerticalAlignment.Center,
        };
        valueText.Text = s.Value.ToString(format);
        s.ValueChanged += (_, e) => { valueText.Text = e.NewValue.ToString(format); set(e.NewValue); Changed(); };
        panel.Children.Add(valueText);
        panel.Children.Add(s);
        return Row(p, label, description, panel);
    }
}
