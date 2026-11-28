using System;
using System.Collections.Generic;
using Aestoys.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Aestoys;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Nav.SetPageProviderService(new PageProvider());
Loaded += (_, _) => Nav.Navigate(App.StartPage?.ToLowerInvariant() switch
        {
            "desktops" => typeof(DesktopsPage),
            "tools" => typeof(ToolsPage),
            "general" => typeof(GeneralPage),
            _ => typeof(AltTabPage),
        });
    }

    private sealed class PageProvider : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _cache = new();
        public object? GetPage(Type pageType)
        {
            if (!_cache.TryGetValue(pageType, out var page))
                _cache[pageType] = page = Activator.CreateInstance(pageType)!;
            return page;
        }
    }
}
