using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Aestoys.Pages;

/// <summary>
/// The settings look (like phone settings): consecutive cards become one rounded panel with thin
/// lines between rows, and every switch gets a short divider line before it. Applied after a XAML
/// page is built, so the pages stay plain cards in XAML.
/// </summary>
internal static class SettingsStyle
{
    public static void Apply(Page page)
    {
        if (page.Content is ScrollViewer { Content: StackPanel root }) Apply(root);
    }

    public static void Apply(StackPanel root)
    {
        var items = root.Children.Cast<UIElement>().ToList();
        root.Children.Clear();
        StackPanel? group = null;
        foreach (var item in items)
        {
            if (item is CardControl or CardExpander or Card)
            {
                var card = (Control)item;
                if (group is null)
                {
                    group = new StackPanel();
                    root.Children.Add(Panel(group));
                }
                else group.Children.Add(Line());
                Flatten(card);
                group.Children.Add(card);
            }
            else
            {
                group = null;
                root.Children.Add(item);
            }
        }
    }

    public static Border Panel(UIElement child)
    {
        var panel = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(0, 4, 0, 4), Child = child, Margin = new Thickness(0, 0, 0, 8) };
        panel.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return panel;
    }

    public static Border Line()
    {
        var line = new Border { Height = 1, Margin = new Thickness(56, 0, 16, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    public static Border Bar()
    {
        var bar = new Border { Width = 1, Height = 28, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return bar;
    }

    /// <summary>
    /// An expander's template paints its open body as a separate lighter box with its own border,
    /// which shows as a stray line at the bottom of the panel. Clear the template's own boxes.
    /// </summary>
    public static void FlattenTemplate(Control card)
    {
        if (card is not CardExpander) return;
        void Clear()
        {
            foreach (var b in Descendants(card).OfType<Border>())
                if (ReferenceEquals(b.TemplatedParent, card))
                {
                    b.Background = Brushes.Transparent;
                    b.BorderThickness = new Thickness(0);
                }
        }
        card.Loaded += (_, _) => Clear();
        if (card is CardExpander ex) ex.Expanded += (_, _) => ex.Dispatcher.BeginInvoke(Clear, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static void Flatten(Control card)
    {
        card.Background = Brushes.Transparent;
        card.BorderThickness = new Thickness(0);
        card.Margin = new Thickness(0);
        FlattenTemplate(card);

        switch (card)
        {
            // A switch as the card's content: room where an expander has its arrow (the divider is for expanders only).
            case CardControl { Content: ToggleSwitch t } c:
                c.Content = null;
                t.Margin = new Thickness(0, 0, 30, 0);
                c.Content = t;   // no divider: there's no expand arrow to separate it from
                break;
            // A switch in an expander's header grid.
            case CardExpander { Header: Grid g }:
                foreach (var t in g.Children.OfType<ToggleSwitch>().ToList())
                {
                    int col = Grid.GetColumn(t), row = Grid.GetRow(t);
                    int index = g.Children.IndexOf(t);
                    g.Children.Remove(t);
                    t.Margin = new Thickness(0, 0, 12, 0);
                    var wrap = WithBar(t);
                    Grid.SetColumn(wrap, col);
                    Grid.SetRow(wrap, row);
                    g.Children.Insert(index, wrap);
                }
                break;
        }
    }

    private static StackPanel WithBar(ToggleSwitch t)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Bar());
        right.Children.Add(t);
        return right;
    }
}
