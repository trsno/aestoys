using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Aestoys.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using DColor = System.Drawing.Color;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Aestoys.Features.Tools;

/// <summary>
/// Color Picker editor: the picked colours (newest first), the selected one in every enabled
/// format with a copy button, fine-tuning sliders, and a button to pick another colour.
/// </summary>
internal sealed class ColorEditorWindow : FluentWindow
{
    private readonly ColorPicker _picker;
    private readonly AppSettings _settings;
    private readonly WrapPanel _history = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly Border _swatch = new() { Height = 72, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) };
    private readonly StackPanel _formats = new();
    private readonly Slider[] _rgb = new Slider[3];
    private readonly TextBlock _empty = new() { Text = "No colours yet. Pick one with Win+Shift+C or the button above.", TextWrapping = TextWrapping.Wrap };
    private DColor _selected;
    private bool _updating;

    private ColorPickerSettings S => _settings.Tools.ColorPicker;

    public ColorEditorWindow(ColorPicker picker, AppSettings settings)
    {
        _picker = picker;
        _settings = settings;
        Title = "Color Picker";
        Width = 420; Height = 640; MinWidth = 360; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TitleBar { Title = "Color Picker", ShowMaximize = false, ShowMinimize = false });

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        var pick = new Button { Content = "Pick a colour", Icon = new SymbolIcon(SymbolRegular.Eyedropper24), Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Stretch };
        pick.Click += (_, _) => _picker.StartPicking();
        body.Children.Add(pick);
        body.Children.Add(Caption("Recent"));
        body.Children.Add(_empty);
        body.Children.Add(_history);
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        body.Children.Add(_swatch);
        _name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        body.Children.Add(_name);

        string[] labels = { "Red", "Green", "Blue" };
        for (int i = 0; i < 3; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(l);
            var s = _rgb[i] = new Slider { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 16 };
            s.ValueChanged += (_, _) => { if (!_updating) Select(DColor.FromArgb(255, (int)_rgb[0].Value, (int)_rgb[1].Value, (int)_rgb[2].Value), fromSliders: true); };
            Grid.SetColumn(s, 1);
            row.Children.Add(s);
            body.Children.Add(row);
        }
        body.Children.Add(new Border { Height = 12 });
        body.Children.Add(_formats);

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    public void Refresh()
    {
        _history.Children.Clear();
        var colors = S.History.Select(ColorPicker.ParseHistory).Where(c => c is not null).Select(c => c!.Value).ToList();
        _empty.Visibility = colors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in colors)
        {
            var b = new Border
            {
                Width = 32, Height = 32, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), Cursor = Cursors.Hand,
                ToolTip = _picker.Format(c, S.CopiedFormat) + "  ·  right-click to remove",
            };
            b.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            var color = c;
            b.MouseLeftButtonUp += (_, _) => Select(color);
            b.MouseRightButtonUp += (_, _) =>
            {
                S.History.Remove($"255|{color.R}|{color.G}|{color.B}");
                _settings.Save();
                Refresh();
            };
            _history.Children.Add(b);
        }
        Select(colors.Count > 0 ? colors[0] : DColor.FromArgb(255, 32, 32, 32));
    }

    private void Select(DColor c, bool fromSliders = false)
    {
        _selected = c;
        _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        _name.Text = ColorPicker.Name(c);
        if (!fromSliders)
        {
            _updating = true;
            _rgb[0].Value = c.R; _rgb[1].Value = c.G; _rgb[2].Value = c.B;
            _updating = false;
        }
        _formats.Children.Clear();
        foreach (var (name, f) in S.Formats.Where(f => f.Value.Visible || f.Key == S.CopiedFormat))
        {
            string value = _picker.Format(c, name);
            var card = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            card.Children.Add(l);
            var v = new System.Windows.Controls.TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            v.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid.SetColumn(v, 1);
            card.Children.Add(v);
            var copy = new Button { Icon = new SymbolIcon(SymbolRegular.Copy24), ToolTip = "Copy", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8) };
            copy.Click += (_, _) =>
            {
                ClipboardText.SetText(value);
                _picker.AddToHistory(_selected);
                copy.Icon = new SymbolIcon(SymbolRegular.Checkmark24);
            };
            Grid.SetColumn(copy, 2);
            card.Children.Add(copy);
            _formats.Children.Add(card);
        }
    }
}
