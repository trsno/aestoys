using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aestoys.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Aestoys.Pages;

/// <summary>
/// "Utilities" settings page: one expandable card per tool with its on/off switch in the header
/// and the main options inside. Built in code because the cards share one shape.
/// </summary>
public sealed class ToolsPage : Page
{
    private readonly StackPanel _root = new() { Margin = new Thickness(32, 24, 32, 32), MaxWidth = 900 };
    private bool _loading = true;

    private static AppSettings Settings => App.Settings;
    private static ToolsSettings T => App.Settings.Tools;

    public ToolsPage()
    {
        Title = "Utilities";
        var sv = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = sv;
        // Scroll inside the page; without this the navigation host measures it at full height and nothing scrolls.
        ScrollViewer.SetCanContentScroll(this, false);
        var title = new TextBlock { Text = "Utilities", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _root.Children.Add(title);
        var from = new List<string>();
        if (T.PowerToysImported) from.Add("PowerToys");
        if (T.Clipboard.DittoImported) from.Add("Ditto");
        if (T.Transparency.Glass2kImported) from.Add("Glass2k");
        if (T.EyeRest.EyeRestImported) from.Add("EyeRest");
        _root.Children.Add(Note(from.Count > 0
            ? $"Settings, layouts and history were imported from {(from.Count > 1 ? string.Join(", ", from.Take(from.Count - 1)) + " and " + from[^1] : from[0])}."
            : "Run aestoys with --import-powertoys, --import-ditto, --import-glass2k or --import-eyerest once to bring over your old settings."));

        Section("Windows");
        Tool(SymbolRegular.Pin24, "Always On Top", $"{T.AlwaysOnTop.Hotkey} pins the active window on top with a coloured frame; {T.AlwaysOnTop.IncreaseOpacity} and {T.AlwaysOnTop.DecreaseOpacity} change its opacity",
            () => T.AlwaysOnTop.Enabled, v => T.AlwaysOnTop.Enabled = v, body =>
            {
                body.Children.Add(Switch("Show a frame", () => T.AlwaysOnTop.ShowFrame, v => T.AlwaysOnTop.ShowFrame = v));
                body.Children.Add(Slider("Frame thickness", 1, 15, () => T.AlwaysOnTop.FrameThickness, v => T.AlwaysOnTop.FrameThickness = v, "px"));
                body.Children.Add(Slider("Frame opacity", 10, 100, () => T.AlwaysOnTop.FrameOpacity, v => T.AlwaysOnTop.FrameOpacity = v, "%"));
                body.Children.Add(Switch("Use the accent colour for the frame", () => T.AlwaysOnTop.FrameUsesAccentColor, v => T.AlwaysOnTop.FrameUsesAccentColor = v));
                body.Children.Add(Switch("Rounded frame", () => T.AlwaysOnTop.RoundCorners, v => T.AlwaysOnTop.RoundCorners = v));
                body.Children.Add(Switch("Play a sound", () => T.AlwaysOnTop.Sound, v => T.AlwaysOnTop.Sound = v));
                body.Children.Add(Switch("Not while a fullscreen game runs", () => T.AlwaysOnTop.SkipInGameMode, v => T.AlwaysOnTop.SkipInGameMode = v));
            });

        Tool(SymbolRegular.Grid24, "FancyZones", $"Hold Shift while dragging a window to drop it into a zone; Win+Left/Right moves between zones; {T.FancyZones.EditorHotkey} edits zones",
            () => T.FancyZones.Enabled, v => T.FancyZones.Enabled = v, body =>
            {
                body.Children.Add(Action("Edit zones on this monitor", "Open editor", () => App.Zones?.OpenEditor()));
                body.Children.Add(Switch("Zones only while Shift is held", () => T.FancyZones.ShiftDrag, v => T.FancyZones.ShiftDrag = v));
                body.Children.Add(Switch("Win+Left/Right move between zones", () => T.FancyZones.OverrideSnapKeys, v => T.FancyZones.OverrideSnapKeys = v));
                body.Children.Add(Switch("Open apps in their last zone", () => T.FancyZones.MoveToLastZone, v => T.FancyZones.MoveToLastZone = v));
                body.Children.Add(Switch("Put windows back after display changes", () => T.FancyZones.MoveOnDisplayChange, v => T.FancyZones.MoveOnDisplayChange = v));
                body.Children.Add(Switch("See-through window while dragging over zones", () => T.FancyZones.MakeDraggedWindowTransparent, v => T.FancyZones.MakeDraggedWindowTransparent = v));
                body.Children.Add(Switch("Show zone numbers", () => T.FancyZones.ShowZoneNumbers, v => T.FancyZones.ShowZoneNumbers = v));
                body.Children.Add(Slider("Zone opacity", 0, 100, () => T.FancyZones.HighlightOpacity, v => T.FancyZones.HighlightOpacity = v, "%"));
            });

        Tool(SymbolRegular.ArrowMove24, "Grab And Move", "Hold the key and drag anywhere in a window to move it; drag with the right button to resize it",
            () => T.GrabAndMove.Enabled, v => T.GrabAndMove.Enabled = v, body =>
            {
                body.Children.Add(Choice("Hold", new[] { "Alt", "Win" }, () => (int)T.GrabAndMove.Modifier, v => T.GrabAndMove.Modifier = (GrabModifier)v));
                body.Children.Add(Switch("Right-drag resizes", () => T.GrabAndMove.ResizeWithRightButton, v => T.GrabAndMove.ResizeWithRightButton = v));
                body.Children.Add(Switch("Not while a fullscreen game runs", () => T.GrabAndMove.SkipInGameMode, v => T.GrabAndMove.SkipInGameMode = v));
            });

        Tool(SymbolRegular.Crop24, "Crop And Lock", $"{T.CropAndLock.ThumbnailHotkey}, then drag over the active window: the chosen part opens as its own live window",
            () => T.CropAndLock.Enabled, v => T.CropAndLock.Enabled = v, null);

        Tool(SymbolRegular.Blur24, "Window transparency", $"{T.Transparency.Modifiers}+1…9 makes the active window see-through (1 = most), {T.Transparency.Modifiers}+0 makes it solid again",
            () => T.Transparency.Enabled, v => T.Transparency.Enabled = v, body =>
            {
                var mods = new[] { "Ctrl+Shift", "Ctrl+Alt", "Alt+Shift", "Ctrl+Alt+Shift" };
                body.Children.Add(Choice("Hold with a number key", mods, () => Math.Max(0, Array.IndexOf(mods, T.Transparency.Modifiers)), v => T.Transparency.Modifiers = mods[v]));
                body.Children.Add(Switch("Remember the level for that app's windows", () => T.Transparency.Remember, v => T.Transparency.Remember = v));
                if (T.Transparency.Rules.Count > 0)
                {
                    body.Children.Add(Note("Remembered:"));
                    foreach (var rule in T.Transparency.Rules.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList())
                    {
                        var parts = rule.Key.Split('|');
                        string name = System.IO.Path.GetFileNameWithoutExtension(parts[0]);
                        UIElement row = null!;
                        row = Action($"{name}  ·  level {rule.Value}  ({100 * Features.Tools.WindowTransparency.Alpha(rule.Value) / 255}% visible)", "Forget", () =>
                        {
                            Changed(() => T.Transparency.Rules.Remove(rule.Key));
                            ((Panel)((FrameworkElement)row).Parent).Children.Remove(row);
                        });
                        body.Children.Add(row);
                    }
                }
            });
        Section("Tools");
        Tool(SymbolRegular.ClipboardPaste24, "Clipboard history", $"{T.Clipboard.Hotkey} lists everything you've copied (text, images, files); Enter pastes, Shift+Enter pastes plain text",
            () => T.Clipboard.Enabled, v => T.Clipboard.Enabled = v, body =>
            {
                body.Children.Add(Action("Your saved clips", "Open", () => App.Clips?.Show()));
                body.Children.Add(Slider("Clips to keep (pinned ones are always kept)", 100, 5000, () => T.Clipboard.MaxEntries, v => T.Clipboard.MaxEntries = v, ""));
                body.Children.Add(Note("Copies that password managers mark as private aren't saved."));
            });

        Tool(SymbolRegular.Eyedropper24, "Color Picker", $"{T.ColorPicker.Hotkey} picks a colour from anywhere on screen and copies it as {T.ColorPicker.CopiedFormat}",
            () => T.ColorPicker.Enabled, v => T.ColorPicker.Enabled = v, body =>
            {
                body.Children.Add(Action("Picked colours and formats", "Open editor", () => App.ColorPick?.OpenEditor()));
                body.Children.Add(Choice("Hotkey opens", new[] { "The editor", "The picker" }, () => (int)T.ColorPicker.Activation, v => T.ColorPicker.Activation = (ColorPickerActivation)v));
                var formats = T.ColorPicker.Formats.Keys.ToArray();
                body.Children.Add(Choice("Copy as", formats, () => Math.Max(0, Array.IndexOf(formats, T.ColorPicker.CopiedFormat)), v => T.ColorPicker.CopiedFormat = formats[v]));
                body.Children.Add(Switch("Show the colour name", () => T.ColorPicker.ShowColorName, v => T.ColorPicker.ShowColorName = v));
            });

        Tool(SymbolRegular.LockOpen24, "File Locksmith", "Right-click a file or folder > Unlock with File Locksmith to see which apps are using it",
            () => T.Locksmith.Enabled, v => T.Locksmith.Enabled = v, body =>
            {
                body.Children.Add(Switch("Only on Shift+right-click", () => T.Locksmith.ExtendedMenuOnly, v => T.Locksmith.ExtendedMenuOnly = v));
            });

        Tool(SymbolRegular.WeatherMoon24, "Awake", "Keeps the PC from sleeping; choose how long from the tray icon's Keep awake menu",
            () => T.Awake.Enabled, v => T.Awake.Enabled = v, body =>
            {
                body.Children.Add(Choice("Mode", new[] { "Off (use the power plan)", "Indefinitely" }, () => T.Awake.Mode == AwakeMode.Indefinite ? 1 : 0,
                    v => { if (v == 1) App.Awake?.SetIndefinite(); else App.Awake?.SetOff(); }));
                body.Children.Add(Switch("Keep the screen on", () => T.Awake.KeepDisplayOn, v => T.Awake.KeepDisplayOn = v));
            });

        Tool(SymbolRegular.Eye24, "Eye rest reminders", $"Every {T.EyeRest.IntervalMinutes} minutes, a reminder to look away from the screen for 20 seconds (also in the tray menu)",
            () => T.EyeRest.Enabled, v => T.EyeRest.Enabled = v, body =>
            {
                body.Children.Add(Slider("Remind every", 1, 120, () => T.EyeRest.IntervalMinutes, v => T.EyeRest.IntervalMinutes = v, "min"));
                body.Children.Add(Switch("Show the reminder", () => T.EyeRest.ShowMessage, v => T.EyeRest.ShowMessage = v));
                body.Children.Add(Action("See what it looks like", "Test", () => App.Notify(Features.Tools.EyeRest.Title, Features.Tools.EyeRest.Message)));
                body.Children.Add(Note("Nothing is shown while the PC is locked; unlocking starts the countdown again."));
            });
        Loaded += (_, _) => _loading = false;
    }

    // ------------------------------------------------------------------ building blocks

    private void Section(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _root.Children.Add(t);
        // One rounded panel per section, rows separated by thin lines (like phone settings).
        _group = new StackPanel();
        var panel = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(0, 4, 0, 4), Child = _group, Margin = new Thickness(0, 0, 0, 8) };
        panel.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _root.Children.Add(panel);
    }

    private StackPanel? _group;

    private void AddRow(Control card)
    {
        card.Background = System.Windows.Media.Brushes.Transparent;
        card.BorderThickness = new Thickness(0);
        card.Margin = new Thickness(0);
        if (_group is null) { _root.Children.Add(card); return; }
        if (_group.Children.Count > 0)
        {
            var line = new Border { Height = 1, Margin = new Thickness(56, 0, 16, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
            _group.Children.Add(line);
        }
        _group.Children.Add(card);
    }

    private static TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private void Tool(SymbolRegular icon, string name, string description, Func<bool> get, Action<bool> set, Action<StackPanel>? fill)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var n = new TextBlock { Text = name };
        n.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var d = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        d.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(n);
        text.Children.Add(d);
        header.Children.Add(text);
        var toggle = new ToggleSwitch { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, fill is null ? 30 : 12, 0) };   // rows without an arrow keep their switch in line
        toggle.Checked += (_, _) => Changed(() => set(true));
        toggle.Unchecked += (_, _) => Changed(() => set(false));
        var bar = new Border { Width = 1, Height = 28, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(bar);
        right.Children.Add(toggle);
        Grid.SetColumn(right, 1);
        header.Children.Add(right);

        if (fill is null)
        {
            var card = new CardControl { Icon = new SymbolIcon(icon), Header = header, Margin = new Thickness(0, 0, 0, 4) };
            AddRow(card);
            return;
        }
        var body = new StackPanel { Margin = new Thickness(40, 0, 0, 0) };
        fill(body);
        AddRow(new CardExpander { Icon = new SymbolIcon(icon), Header = header, Content = body });
    }

    private void Changed(Action apply)
    {
        if (_loading) return;
        apply();
        Settings.Save();
        App.ApplyFeatureSettings();
    }

    private static Grid Row(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4), MinHeight = 36 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private UIElement Switch(string label, Func<bool> get, Action<bool> set)
    {
        var s = new ToggleSwitch { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
        s.Checked += (_, _) => Changed(() => set(true));
        s.Unchecked += (_, _) => Changed(() => set(false));
        return Row(label, s);
    }

    private UIElement Slider(string label, int min, int max, Func<int> get, Action<int> set, string unit)
    {
        var value = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = $"{get()} {unit}" };
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var s = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = get(), Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        s.ValueChanged += (_, e) => { value.Text = $"{(int)e.NewValue} {unit}"; Changed(() => set((int)e.NewValue)); };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(s);
        p.Children.Add(value);
        return Row(label, p);
    }

    private UIElement Choice(string label, IReadOnlyList<string> options, Func<int> get, Action<int> set)
    {
        var c = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        foreach (var o in options) c.Items.Add(o);
        c.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1);
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) Changed(() => set(c.SelectedIndex)); };
        return Row(label, c);
    }

    private static UIElement Action(string label, string button, Action click)
    {
        var b = new Wpf.Ui.Controls.Button { Content = button, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return Row(label, b);
    }
}
