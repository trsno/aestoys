using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Aestoys.Interop;
using Aestoys.Services;

namespace Aestoys.Features.AltTab;

/// <summary>
/// Full-monitor 3D switcher with two layouts, modeled on the GNOME CoverflowAltTab extension:
/// Coverflow (selected window flat in the middle, the rest tilted to the sides) and
/// Timeline (windows stacked diagonally into the distance).
/// </summary>
internal sealed class CoverflowWindow : Window, ISwitcherView
{
    public event Action<int>? ItemClicked;
    public event Action? CancelRequested;
    public event Action<int>? Scrolled;

    private struct Pose
    {
        public double X, Y, Z, Angle, Pivot, Scale, Opacity;
        public static Pose Lerp(Pose a, Pose b, double t) => new()
        {
            X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t, Z = a.Z + (b.Z - a.Z) * t,
            Angle = a.Angle + (b.Angle - a.Angle) * t, Pivot = a.Pivot + (b.Pivot - a.Pivot) * t,
            Scale = a.Scale + (b.Scale - a.Scale) * t, Opacity = a.Opacity + (b.Opacity - a.Opacity) * t,
        };
    }

    private sealed class Node
    {
        public required SwitchItem Item;
        public required ModelVisual3D Visual;
        public required ImageBrush Brush;
        public required TranslateTransform3D PivotShift, Position;
        public required ScaleTransform3D ScaleT;
        public required AxisAngleRotation3D Rotation;
        public required GeometryModel3D Model;
        public double W, H;
        public Pose From, To, Cur;
        public double Duration;
        public bool InScene;
    }

    private readonly Grid _root = new();
    private readonly Rectangle _dim = new() { Fill = Brushes.Black };
    private readonly Viewport3D _viewport = new() { ClipToBounds = false };
    private readonly ModelVisual3D _scene = new();
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 50, NearPlaneDistance = 1 };
    private readonly StackPanel _titlePanel = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Image _titleIcon = new() { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new()
    {
        Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 },
    };
    private readonly Image _overlayIcon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };

    private readonly List<Node> _nodes = new();
    private readonly Random _rng = new();
    private IntPtr _hwnd;
    private int _selected;
    private DateTime _animStart;
    private bool _animating;
    private volatile int _session;
    private double _scale = 1;

    private static CoverflowSettings S => App.Settings.Coverflow;
    public bool IsShown => IsVisible;

    public CoverflowWindow()
    {
        Title = "aestoys coverflow";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;

        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        _viewport.Children.Add(_scene);

        _titlePanel.Children.Add(_titleIcon);
        _titlePanel.Children.Add(_title);
        _titlePanel.IsHitTestVisible = false;

        _root.Children.Add(_dim);
        _root.Children.Add(_viewport);
        _root.Children.Add(_overlayIcon);
        _root.Children.Add(_titlePanel);
        _root.Background = Brushes.Transparent; // so background clicks are caught
        Content = _root;

        _root.MouseLeftButtonUp += OnClick;
        _root.MouseWheel += (_, e) => Scrolled?.Invoke(e.Delta > 0 ? -1 : 1);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
    }

    // ---------------------------------------------------------------- show / hide

    public void ShowOn(IntPtr monitor, IReadOnlyList<SwitchItem> items, int index)
    {
        _session++;
        ClearScene();

        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        var mon = mi.rcMonitor;
        _scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        Opacity = 0;
        if (!IsVisible) Show();
        Native.SetBlurBehind(_hwnd, S.BlurBackground);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, mon.Left, mon.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, mon.Left, mon.Top, mon.Width, mon.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Width = mon.Width / _scale;
        Height = mon.Height / _scale;
        UpdateLayout();

        _dim.Opacity = Math.Clamp(S.DimFactor, 0, 1) * 0.85;
        double w = Width, h = Height;
        _camera.Position = new Point3D(0, 0, w / 2 / Math.Tan(25 * Math.PI / 180));
        _camera.LookDirection = new Vector3D(0, 0, -1);
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FarPlaneDistance = _camera.Position.Z * 30;

        _titlePanel.VerticalAlignment = S.TitlePosition == TitlePosition.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        double edge = h * 0.08;
        _titlePanel.Margin = S.TitlePosition == TitlePosition.Top
            ? new Thickness(0, edge + S.VerticalOffset, 0, 0)
            : new Thickness(0, 0, 0, Math.Max(0, edge - S.VerticalOffset));
        _title.MaxWidth = w - 200;
        _titleIcon.Visibility = S.IconStyle == IconStyle.Classic ? Visibility.Visible : Visibility.Collapsed;
        _overlayIcon.Visibility = S.IconStyle == IconStyle.Overlay ? Visibility.Visible : Visibility.Collapsed;
        _overlayIcon.Width = _overlayIcon.Height = S.OverlayIconSize;
        _overlayIcon.Opacity = S.OverlayIconOpacity;
        _overlayIcon.Margin = new Thickness(0, 2 * S.VerticalOffset, 0, 0);

        WindowCapture.Prune();
        foreach (var item in items) _nodes.Add(CreateNode(item));

        _selected = index < 0 ? 0 : index;
        Layout(initial: true);
        UpdateCaption();
        Opacity = 1;

        StartCaptures(mon);
    }

    /// <summary>
    /// Refreshes the snapshots on two worker threads, nearest to the selection first, so the UI
    /// thread (animation and the keyboard hook) never waits on PrintWindow.
    /// </summary>
    private void StartCaptures(Native.RECT mon)
    {
        int session = _session;
        // Textures bigger than the selected window is drawn at only cost time.
        double r = Math.Clamp(S.PreviewToMonitorRatio, 0.05, 1);
        int maxSide = (int)Math.Clamp(Math.Max(mon.Width, mon.Height) * r * 1.15, 480, 1600);
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(Node node, IntPtr hwnd)>(
            _nodes.Select((n, i) => (n, i))
                  .Where(t => t.n.To.Opacity > 0)
                  .OrderBy(t => Math.Min(Math.Abs(t.i - _selected), _nodes.Count - Math.Abs(t.i - _selected)))
                  .Select(t => (t.n, t.n.Item.Hwnd)));
        long start = Perf.Now;
        int pending = queue.Count;
        for (int w = 0; w < 2; w++)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                while (session == _session && queue.TryDequeue(out var job))
                {
                    long c0 = Perf.Now;
                    var bmp = WindowCapture.Capture(job.hwnd, maxSide);
                    Perf.Log($"capture {Perf.Ms(c0):0.0} ms (worker) {(bmp is null ? "none" : $"{bmp.PixelWidth}x{bmp.PixelHeight}")}");
                    if (bmp is not null)
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { if (session == _session) job.node.Brush.ImageSource = bmp; });
                    if (System.Threading.Interlocked.Decrement(ref pending) == 0) Perf.Log($"all captures done after {Perf.Ms(start):0.0} ms");
                }
            });
        }
    }

    public void Select(int index)
    {
        if (_nodes.Count == 0) return;
        _selected = index;
        Layout(initial: false);
        UpdateCaption();
    }

    public void HideView()
    {
        _session++;
        StopAnimation();
        FlushFrameStats();
        Hide();
        ClearScene();
    }

    public void CloseView()
    {
        HideView();
        Close();
    }

    private void ClearScene()
    {
        _scene.Children.Clear();
        _nodes.Clear();
        _title.Text = string.Empty;
    }

    private void UpdateCaption()
    {
        if (_selected < 0 || _selected >= _nodes.Count) { _title.Text = "No windows on this monitor"; _titleIcon.Source = null; _overlayIcon.Source = null; return; }
        var item = _nodes[_selected].Item;
        _title.Text = item.Title;
        _titleIcon.Source = item.Icon;
        _overlayIcon.Source = item.Icon;
    }

    // ---------------------------------------------------------------- scene

    private Node CreateNode(SwitchItem item)
    {
        // Natural size in DIPs, from the visible frame (or restore position when minimized).
        double w = 800, h = 500;
        if (!item.IsMinimized && Native.DwmGetWindowAttribute(item.Hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT fr, Marshal.SizeOf<Native.RECT>()) == 0 && fr.Width > 0)
        { w = fr.Width / _scale; h = fr.Height / _scale; }
        else
        {
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(item.Hwnd, ref wp) && wp.rcNormalPosition.Width > 0)
            { w = wp.rcNormalPosition.Width / _scale; h = wp.rcNormalPosition.Height / _scale; }
        }
        w = Math.Max(w, 120); h = Math.Max(h, 80);

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(-w / 2, -h / 2, 0), new(w / 2, -h / 2, 0), new(w / 2, h / 2, 0), new(-w / 2, h / 2, 0) },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        mesh.Freeze();
        var brush = new ImageBrush(WindowCapture.Cached(item.Hwnd, w / h) ?? WindowCapture.Placeholder(item, w / h)) { Stretch = Stretch.Fill };
        var material = new DiffuseMaterial(brush);
        var pivot = new TranslateTransform3D();
        var scale = new ScaleTransform3D();
        var rot = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var pos = new TranslateTransform3D();
        var group = new Transform3DGroup();
        group.Children.Add(pivot);
        group.Children.Add(scale);
        group.Children.Add(new RotateTransform3D(rot));
        group.Children.Add(pos);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material, Transform = group };
        return new Node
        {
            Item = item, Visual = new ModelVisual3D { Content = model }, Brush = brush, Model = model,
            PivotShift = pivot, ScaleT = scale, Rotation = rot, Position = pos, W = w, H = h,
        };
    }

    private double Fit(Node n)
    {
        double r = Math.Clamp(S.PreviewToMonitorRatio, 0.05, 1);
        return Math.Min(Math.Min(r * Width / n.W, r * Height / n.H), 1.5);
    }

    private void Layout(bool initial)
    {
        int count = _nodes.Count;
        if (count == 0) return;
        double yc = -S.VerticalOffset;

        for (int i = 0; i < count; i++)
        {
            var n = _nodes[i];
            var to = S.Style == CoverflowStyle.Timeline ? TimelinePose(i, count, yc) : CoverflowPose(i, count, yc);

            n.From = initial ? to with { Scale = to.Scale * 0.85, Opacity = 0 } : n.Cur;
            if (initial) n.Cur = n.From;
            n.To = to;
            n.Duration = Math.Max(0.01, S.AnimationTime) * (S.RandomizeAnimationTimes ? 0.5 + _rng.NextDouble() * 0.5 : 1);
        }
        _animStart = DateTime.UtcNow;
        StartAnimation();
        Apply(0);
    }

    private Pose CoverflowPose(int i, int count, double yc)
    {
        int k = i - _selected;
        if (S.LoopingMethod == LoopingMethod.Carousel)
        {
            if (k > count / 2) k -= count;
            if (k < -(count - 1) / 2) k += count;
        }
        var n = _nodes[i];
        double fitSel = Fit(_nodes[_selected]);
        double s = Fit(n) * Math.Pow(Math.Clamp(S.CoverflowScalingFactor, 0.05, 1), Math.Abs(k));
        if (k == 0) return new Pose { X = 0, Y = yc, Z = 0, Angle = 0, Pivot = 0, Scale = s, Opacity = 1 };

        // Classic coverflow: every side window has the same size and the same tilt (outer edge
        // toward the viewer, inner edge away). The first one sits right next to the selected
        // window; the rest follow side by side while there is room and only start to overlap
        // ("stack spacing") when a side holds more windows than fit on screen.
        int d = Math.Abs(k);
        double camZ = _camera.Position.Z;
        double a = Math.Clamp(S.CoverflowWindowAngle, 0, 89) * Math.PI / 180;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        double halfSel = _nodes[_selected].W * fitSel / 2;
        double limit = Width / 2 - Width * 0.02;   // outer edges stay on screen
        int sideCount = S.LoopingMethod == LoopingMethod.Carousel
            ? (k < 0 ? (count - 1) / 2 : count / 2)
            : (k < 0 ? _selected : count - 1 - _selected);

        // Same scale for all side windows: the selected window's fit times the scaling factor,
        // capped so a tilted window's outer edge can't run off the screen.
        double f = Math.Clamp(S.CoverflowScalingFactor, 0.05, 1);
        double sideH = _nodes[_selected].H * fitSel * f;              // common height
        double sDesired = sideH / n.H;
        double hw = n.W * sDesired / 2;                               // half width of this window
        double maxHalf = Math.Max(40, (limit - halfSel) / (cos + sin * limit / camZ) / 2);
        if (hw > maxHalf) { sDesired *= maxHalf / hw; hw = maxHalf; }
        s = sDesired;

        // Windows are lined up by their inner edges so a wider window further back can't poke
        // out in front of a narrower one. The inner edge sits back at z = -hw·sin, so perspective
        // pulls it toward the centre; correct for that so it lands just outside the selected window.
        var first = _nodes[(_selected + (k < 0 ? -1 : 1) + count) % count];
        double hw1 = Math.Min(first.W * (sideH / first.H) / 2, maxHalf);
        double inner1 = (halfSel + 12) * (camZ + hw1 * sin) / camZ;
        // Furthest inner edge that still keeps a window's outer edge on screen.
        double innerMax = limit * (camZ - hw1 * sin) / camZ - 2 * hw1 * cos;
        double sideBySide = 2 * hw1 * cos + 8;
        double spacing = sideCount > 1
            ? Math.Min(sideBySide, Math.Max(Math.Max(1, S.CoverflowOffsetWidth), (innerMax - inner1) / (sideCount - 1)))
            : 0;
        double x = inner1 + (d - 1) * spacing + hw * cos;
        return new Pose
        {
            X = k < 0 ? -x : x, Y = yc, Z = 0,
            Angle = k < 0 ? S.CoverflowWindowAngle : -S.CoverflowWindowAngle, Pivot = 0, Scale = s,
            Opacity = d > 12 ? 0 : 1,
        };
    }

    private Pose TimelinePose(int i, int count, double yc)
    {
        int k = (i - _selected + count) % count;
        var n = _nodes[i];
        double f = Math.Clamp(S.TimelineScalingFactor, 0.05, 1);
        double step = S.TimelineScaleWithDistance ? Math.Sqrt(k) : k;
        double s = Fit(n) * (S.TimelineScaleWithDistance ? Math.Pow(f, k) : 1);
        double ang = S.TimelineAngle * Math.PI / 180;
        double d = S.TimelineDistance;

        // Windows are anchored by their top-left corner (like the GNOME extension), so the
        // ones behind peek out up and to the left of the selected window.
        // The anchor is a fixed box (monitor size × ratio) centred on screen, independent of
        // which window is selected, so the stack stays put while windows move through it.
        double r = Math.Clamp(S.PreviewToMonitorRatio, 0.05, 1);
        double baseLeft = -r * Width / 2 + Width * 0.04;
        double baseTop = yc + r * Height / 2 - Height * 0.02;
        double left = baseLeft - d * Math.Cos(ang) * step;
        double top = baseTop + d * Math.Sin(ang) * step;
        return new Pose
        {
            // Pivot on the left edge and tilt the right edge away, so no window pokes
            // forward through the ones in front of it.
            X = left,
            Y = top - n.H * s / 2,
            Z = -k * 12,
            Angle = S.TimelineTiltAngle, Pivot = -0.5, Scale = s,
            Opacity = k > 10 ? 0 : 1,
        };
    }

    // ---------------------------------------------------------------- animation

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        _lastFrame = 0;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = Perf.Now;
        if (Perf.Enabled && _lastFrame != 0)
        {
            double dt = (now - _lastFrame) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _frames++; _maxFrame = Math.Max(_maxFrame, dt); if (dt > 20) _slowFrames++;
        }
        _lastFrame = now;
        double elapsed = (DateTime.UtcNow - _animStart).TotalSeconds;
        if (Apply(elapsed)) StopAnimation();
    }

    private long _lastFrame;
    private int _frames, _slowFrames;
    private double _maxFrame;

    private void FlushFrameStats()
    {
        if (_frames > 0) Perf.Log($"frames {_frames}, max {_maxFrame:0.0} ms, >20ms {_slowFrames}");
        _frames = _slowFrames = 0; _maxFrame = 0; _lastFrame = 0;
    }

    /// <summary>Advances all nodes; returns true when every animation has finished.</summary>
    private bool Apply(double elapsed)
    {
        bool done = true;
        foreach (var n in _nodes)
        {
            double t = Math.Min(1, elapsed / n.Duration);
            if (t < 1) done = false;
            n.Cur = Pose.Lerp(n.From, n.To, Ease(S.Easing, t));
            var p = n.Cur;

            n.PivotShift.OffsetX = -p.Pivot * n.W;
            n.ScaleT.ScaleX = n.ScaleT.ScaleY = n.ScaleT.ScaleZ = p.Scale;
            n.Rotation.Angle = p.Angle;
            n.Position.OffsetX = p.X; n.Position.OffsetY = p.Y; n.Position.OffsetZ = p.Z;
            n.Brush.Opacity = Math.Clamp(p.Opacity, 0, 1);

            bool visible = p.Opacity > 0.01;
            if (visible && !n.InScene) { _scene.Children.Add(n.Visual); n.InScene = true; }
            else if (!visible && n.InScene) { _scene.Children.Remove(n.Visual); n.InScene = false; }
        }
        return done;
    }

    private static double Ease(Easing e, double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return e switch
        {
            Easing.Linear => t,
            Easing.EaseOutQuad => 1 - (1 - t) * (1 - t),
            Easing.EaseOutCubic => 1 - Math.Pow(1 - t, 3),
            Easing.EaseInOutCubic => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            Easing.EaseOutQuint => 1 - Math.Pow(1 - t, 5),
            Easing.EaseInOutQuint => t < 0.5 ? 16 * Math.Pow(t, 5) : 1 - Math.Pow(-2 * t + 2, 5) / 2,
            Easing.EaseOutExpo => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t),
            Easing.EaseOutBack => 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2),
            Easing.EaseOutElastic => t is 0 or 1 ? t : Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1,
            Easing.EaseOutBounce => Bounce(t),
            _ => t,
        };

        static double Bounce(double x)
        {
            const double n1 = 7.5625, d1 = 2.75;
            if (x < 1 / d1) return n1 * x * x;
            if (x < 2 / d1) return n1 * (x -= 1.5 / d1) * x + 0.75;
            if (x < 2.5 / d1) return n1 * (x -= 2.25 / d1) * x + 0.9375;
            return n1 * (x -= 2.625 / d1) * x + 0.984375;
        }
    }

    // ---------------------------------------------------------------- input

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport)) as RayMeshGeometry3DHitTestResult;
        if (hit is not null)
        {
            int idx = _nodes.FindIndex(n => n.Visual == hit.VisualHit);
            if (idx >= 0) { ItemClicked?.Invoke(idx); return; }
        }
        CancelRequested?.Invoke();
    }
}
