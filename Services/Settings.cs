using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aestoys.Services;

public enum MonitorMode { Cursor, ActiveWindow }
public enum CoverflowStyle { Coverflow, Timeline }
public enum LoopingMethod { FlipStack, Carousel }
public enum TitlePosition { Bottom, Top }
public enum IconStyle { Classic, Overlay, None }
public enum Easing { Linear, EaseOutQuad, EaseOutCubic, EaseInOutCubic, EaseOutQuint, EaseInOutQuint, EaseOutExpo, EaseOutBack, EaseOutElastic, EaseOutBounce }

public sealed class CoverflowSettings
{
    public bool Enabled { get; set; } = true;
    public CoverflowStyle Style { get; set; } = CoverflowStyle.Coverflow;

    // Switcher
    public int VerticalOffset { get; set; } = 0;
    public TitlePosition TitlePosition { get; set; } = TitlePosition.Bottom;
    public double DimFactor { get; set; } = 0.6;
    public bool BlurBackground { get; set; } = true;
    public double PreviewToMonitorRatio { get; set; } = 0.5;

    // Animation
    public Easing Easing { get; set; } = Easing.EaseOutCubic;
    public double AnimationTime { get; set; } = 0.25;
    public bool RandomizeAnimationTimes { get; set; } = false;

    // Icons
    public IconStyle IconStyle { get; set; } = IconStyle.Classic;
    public double OverlayIconSize { get; set; } = 128;
    public double OverlayIconOpacity { get; set; } = 1.0;

    // Coverflow
    public LoopingMethod LoopingMethod { get; set; } = LoopingMethod.FlipStack;
    public double CoverflowScalingFactor { get; set; } = 0.8;
    public double CoverflowWindowAngle { get; set; } = 60;
    public double CoverflowOffsetWidth { get; set; } = 50;

    // Timeline
    public double TimelineDistance { get; set; } = 96;
    public double TimelineAngle { get; set; } = 29.357753542791272;
    public double TimelineTiltAngle { get; set; } = 15;
    public bool TimelineScaleWithDistance { get; set; } = true;
    public double TimelineScalingFactor { get; set; } = 0.8;
}

public sealed class DesktopSettings
{
    /// <summary>Ctrl+Win+Right on the last desktop goes to the first, Ctrl+Win+Left on the first to the last.</summary>
    public bool WrapAround { get; set; } = true;
    /// <summary>Ctrl+Win+Shift+Left/Right takes the active window along to the previous/next desktop.</summary>
    public bool MoveWindowKeys { get; set; } = true;
    /// <summary>Ctrl+Win+Shift+D shows the active window on every desktop (blue border while pinned).</summary>
    public bool PinKey { get; set; } = true;
    /// <summary>Turns the Windows logo on the Start button into a 2×2 desktop indicator.</summary>
    public bool Indicator { get; set; } = true;
}

public sealed class WindowPlacementSettings
{
    /// <summary>New app windows open centred on their monitor.</summary>
    public bool CenterNewWindows { get; set; } = true;
    /// <summary>Alt+Space centres the active window (instead of opening the system menu).</summary>
    public bool AltSpaceCenters { get; set; } = true;
}

public sealed class LauncherSettings
{
    /// <summary>Resting the mouse on the Start button shows a bar of pinned apps next to it.</summary>
    public bool Enabled { get; set; } = true;
    public string Folder { get; set; } = @"C:\Warez\taskbar-launcher\shortcuts";
    public bool IncludeSettingsApp { get; set; } = true;
    public int HoverDelayMs { get; set; } = 0;
    public int LeaveDelayMs { get; set; } = 0;
}

public sealed class AppSettings
{
    public DesktopSettings Desktops { get; set; } = new();
    public WindowPlacementSettings Placement { get; set; } = new();
    public LauncherSettings Launcher { get; set; } = new();
    public ToolsSettings Tools { get; set; } = new();

    public bool AltTabPerMonitorEnabled { get; set; } = true;
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Cursor;
    public bool IncludeMinimized { get; set; } = true;
    public CoverflowSettings Coverflow { get; set; } = new();

    public bool StartWithWindows { get; set; } = false;
    public bool RunAsAdmin { get; set; } = false;

    internal static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aestoys");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
