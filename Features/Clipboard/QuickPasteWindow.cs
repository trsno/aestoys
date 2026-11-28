using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aestoys.Interop;
using Aestoys.Services;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Aestoys.Features.Clipboard;

/// <summary>One row of the list. The thumbnail is decoded only when the row is on screen.</summary>
internal sealed class ClipItem
{
    private readonly ClipboardStore _store;
    private ImageSource? _thumb;
    private bool _thumbTried;

    public ClipItem(ClipRow row, int index, ClipboardStore store)
    {
        _store = store;
        Row = row;
        Number = index < 10 ? ((index + 1) % 10).ToString() : "";
        string t = row.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        string first = t.TrimStart('\n', ' ').Split('\n')[0];
        if (first.Length > 300) first = first[..300];
        int more = t.Count(c => c == '\n');
        Text = row.IsImage && !row.IsFiles && (row.Text is "CF_DIB" or "PNG" or "") ? "Image" : first + (more > 0 && t.Trim().Contains('\n') ? "  …" : "");
    }

    public ClipRow Row { get; }
    public long Id => Row.Id;
    public string Number { get; }
    public string Text { get; }
    public bool Pinned => Row.Pinned;
    public Visibility PinVisibility => Row.Pinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => Row.IsImage ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Image clips show only the thumbnail; file and text clips keep their text.</summary>
    public Visibility TextVisibility => Text == "Image" ? Visibility.Collapsed : Visibility.Visible;
    public string When => Relative(Row.Date);

    public ImageSource? Thumb
    {
        get
        {
            if (_thumbTried || !Row.IsImage) return _thumb;
            _thumbTried = true;
            try { _thumb = Decode(_store.Format(Row.Id, "PNG", "CF_DIB"), 80); } catch { }
            return _thumb;
        }
    }

    internal static ImageSource? Decode(byte[]? data, int height)
    {
        if (data is null || data.Length < 40) return null;
        byte[] file = data;
        if (!(data[0] == 0x89 && data[1] == 0x50))   // not PNG: DIB → BMP file
        {
            int headerSize = BitConverter.ToInt32(data, 0);
            int bitCount = BitConverter.ToUInt16(data, 14);
            int compression = BitConverter.ToInt32(data, 16);
            int clrUsed = BitConverter.ToInt32(data, 32);
            int palette = (clrUsed != 0 ? clrUsed : bitCount <= 8 ? 1 << bitCount : 0) * 4;
            int masks = headerSize == 40 && compression == 3 ? 12 : 0;
            file = new byte[14 + data.Length];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(file, 10);
            data.CopyTo(file, 14);
        }
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelHeight = height;
        img.StreamSource = new MemoryStream(file);
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d";
        return t.ToString("d MMM");
    }
}

/// <summary>
/// Win+V list. Type to search; Enter pastes, Shift+Enter pastes as plain text, Ctrl+1…0 pastes
/// one of the first ten, Delete removes a clip (when the search box is empty), Esc closes.
/// Right-click a clip for pin/unpin. Opens at the text cursor of the app you're typing in.
/// </summary>
internal sealed class QuickPasteWindow : FluentWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public Native.RECT rcCaret; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT pt);

    private readonly ClipboardHistory _history;
    private readonly AppSettings _settings;
    private readonly TextBox _search = new() { PlaceholderText = "Search clips", Icon = new SymbolIcon(SymbolRegular.Search24), Margin = new Thickness(10, 10, 10, 6), ClearButtonEnabled = true };
    private readonly ListBox _list = new();
    private readonly System.Windows.Controls.TextBlock _footer = new() { FontSize = 11, Margin = new Thickness(14, 2, 14, 8) };
    private readonly DispatcherTimer _searchDelay;
    private IntPtr _target;
    private int _loadVersion;

    public QuickPasteWindow(ClipboardHistory history, AppSettings settings)
    {
        _history = history;
        _settings = settings;
        Title = "Clipboard";
        Width = settings.Tools.Clipboard.Width;
        Height = settings.Tools.Clipboard.Height;
        MinWidth = 300; MinHeight = 240;
        ShowInTaskbar = false;
        Topmost = true;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        _list.ItemTemplate = (DataTemplate)XamlReader.Parse(Template);
        _list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        // Compact one-line rows like Ditto (the theme's list items are tall).
        var item = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)));
        item.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 2, 8, 2)));
        item.Setters.Add(new Setter(MinHeightProperty, 0.0));
        item.Setters.Add(new Setter(MarginProperty, new Thickness(0, 1, 0, 1)));
        _list.ItemContainerStyle = item;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.Margin = new Thickness(6, 0, 6, 0);
        _list.MouseDoubleClick += (_, e) => { if (Selected is { } c) PasteItem(c, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); };
        _list.ContextMenu = BuildMenu();
        _footer.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        _footer.Text = "Enter paste · Shift+Enter plain text · Ctrl+1…0 quick paste · Del remove";

        var root = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(_footer);
        root.Children.Add(_list);
        Content = root;

        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => { _searchDelay!.Stop(); Load(); }, Dispatcher);
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Hide();
        Closing += (_, e) => { e.Cancel = true; Hide(); };   // kept around for instant reopening
        // Remember the size only when the user resizes it (WM_EXITSIZEMOVE); DPI moves don't count.
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0232 /* WM_EXITSIZEMOVE */)
            {
                _settings.Tools.Clipboard.Width = Math.Round(ActualWidth);
                _settings.Tools.Clipboard.Height = Math.Round(ActualHeight);
                _settings.Save();
            }
            return IntPtr.Zero;
        });
        // Undo sizes saved by the first version, which shrank the window on high-DPI monitors.
        if (_settings.Tools.Clipboard.Width < 380 || _settings.Tools.Clipboard.Height < 420)
        {
            _settings.Tools.Clipboard.Width = 460;
            _settings.Tools.Clipboard.Height = 560;
        }
    }

    private const string Template = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid Margin="0,1,0,1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="22" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Number}" FontSize="11" Opacity="0.55" VerticalAlignment="Center" />
                <StackPanel Grid.Column="1">
                    <Image Source="{Binding Thumb}" Visibility="{Binding ImageVisibility}" Height="40" MaxWidth="200" HorizontalAlignment="Left" Stretch="Uniform" Margin="0,2,0,2" />
                    <TextBlock Text="{Binding Text}" Visibility="{Binding TextVisibility}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                </StackPanel>
                <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center" Margin="8,0,2,0">
                    <TextBlock Text="&#xE718;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="11" Visibility="{Binding PinVisibility}" Margin="0,0,6,0" Opacity="0.8" />
                    <TextBlock Text="{Binding When}" FontSize="11" Opacity="0.5" />
                </StackPanel>
            </Grid>
        </DataTemplate>
        """;

    private ClipItem? Selected => _list.SelectedItem as ClipItem;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action<ClipItem> act)
        {
            var m = new MenuItem { Header = text };
            m.Click += (_, _) => { if (Selected is { } c) act(c); };
            menu.Items.Add(m);
            return m;
        }
        Item("Paste", c => PasteItem(c, false));
        Item("Paste as plain text", c => PasteItem(c, true));
        var pin = Item("Pin", c => { _history.Store.SetPinned(c.Id, !c.Pinned); Load(); });
        Item("Delete", Remove);
        menu.Opened += (_, _) => pin.Header = Selected?.Pinned == true ? "Unpin" : "Pin to the top";
        return menu;
    }

    // ------------------------------------------------------------------ show / load

    public void Open(IntPtr target)
    {
        _target = target;
        _search.Text = "";
        Load();
        if (!IsLoaded) { Show(); Hide(); }   // create the handle so it can be placed in pixels
        Place();
        Show();
        AltTab.AltTabController.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        _search.Focus();
        Keyboard.Focus(_search);
    }

    public void ReloadIfVisible()
    {
        if (IsVisible) Load();
    }

    private async void Load()
    {
        int version = ++_loadVersion;
        string q = _search.Text;
        var store = _history.Store;
        int limit = Math.Max(100, _settings.Tools.Clipboard.MaxEntries + 200);
        List<ClipRow> rows;
        try { rows = await Task.Run(() => store.List(q, limit)); }
        catch (Exception e) { App.Log("clipboard list: " + e.Message); return; }
        if (version != _loadVersion) return;
        _list.ItemsSource = rows.Select((r, i) => new ClipItem(r, i, store)).ToList();
        if (_list.Items.Count > 0) { _list.SelectedIndex = 0; _list.ScrollIntoView(_list.Items[0]); }
    }

    /// <summary>At the text cursor of the target app; otherwise at the mouse. Kept on the monitor.</summary>
    private void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        Native.POINT pt;
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        uint thread = _target == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(_target, out _);
        if (thread != 0 && GetGUIThreadInfo(thread, ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            pt = new Native.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
            ClientToScreen(gti.hwndCaret, ref pt);
            pt.Y += 4;
        }
        else Native.GetCursorPos(out pt);

        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(pt, 2), ref mi);
        double scale = Math.Max(1, Win32.GetDpiForWindow(h)) / 96.0;
        // Use the target monitor's DPI for the size (the window adopts it once moved there).
        try { if (GetDpiForMonitor(Native.MonitorFromPoint(pt, 2), 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { }
        double dipW = _settings.Tools.Clipboard.Width, dipH = _settings.Tools.Clipboard.Height;
        int w = (int)(dipW * scale), hgt = (int)(dipH * scale);
        var work = mi.rcWork;
        int x = Math.Clamp(pt.X, work.Left, Math.Max(work.Left, work.Right - w));
        int y = pt.Y + hgt > work.Bottom ? Math.Max(work.Top, pt.Y - hgt - 28) : pt.Y;
        // Move first so the window takes on the target monitor's DPI, then size it in DIPs:
        // sizing in pixels before the DPI change made Windows rescale it again.
        Native.SetWindowPos(h, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE);
        Width = dipW;
        Height = dipH;
    }

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    // ------------------------------------------------------------------ keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                Hide(); e.Handled = true; return;
            case Key.Enter:
                if (Selected is { } c) PasteItem(c, mods.HasFlag(ModifierKeys.Shift));
                e.Handled = true; return;
            case Key.Down: Move(1); e.Handled = true; return;
            case Key.Up: Move(-1); e.Handled = true; return;
            case Key.PageDown: Move(10); e.Handled = true; return;
            case Key.PageUp: Move(-10); e.Handled = true; return;
            case Key.Delete when string.IsNullOrEmpty(_search.Text):
                if (Selected is { } d) Remove(d);
                e.Handled = true; return;
        }
        if (mods == ModifierKeys.Control && e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9)
        {
            int n = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
            int index = n == 0 ? 9 : n - 1;
            if (index < _list.Items.Count) PasteItem((ClipItem)_list.Items[index], false);
            e.Handled = true;
        }
    }

    private void Move(int by)
    {
        if (_list.Items.Count == 0) return;
        int i = Math.Clamp(_list.SelectedIndex + by, 0, _list.Items.Count - 1);
        _list.SelectedIndex = i;
        _list.ScrollIntoView(_list.Items[i]);
    }

    private void Remove(ClipItem c)
    {
        int i = _list.SelectedIndex;
        _history.Store.Delete(c.Id);
        var items = ((List<ClipItem>)_list.ItemsSource).Where(x => x.Id != c.Id).ToList();
        _list.ItemsSource = items;
        if (items.Count > 0) _list.SelectedIndex = Math.Min(i, items.Count - 1);
    }

    private void PasteItem(ClipItem c, bool plain)
    {
        Hide();
        _history.Paste(c.Id, plain, _target);
    }
}
