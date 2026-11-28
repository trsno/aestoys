using System;
using System.Collections.Generic;

namespace Aestoys.Services;

// Utilities carried over from PowerToys. Defaults are the values the user had in PowerToys; every
// utility starts switched off and is turned on by the one-time PowerToys import (PowerToysImport).

public enum AwakeMode { Off, Indefinite, Timed, Until }

public sealed class AwakeSettings
{
    public bool Enabled { get; set; } = false;
    public AwakeMode Mode { get; set; } = AwakeMode.Off;
    public bool KeepDisplayOn { get; set; } = true;
    public int IntervalHours { get; set; } = 1;
    public int IntervalMinutes { get; set; } = 0;
    /// <summary>When a Timed/Until session ends (absolute, so it survives restarts).</summary>
    public DateTime? ExpiresAt { get; set; }
}

public sealed class AlwaysOnTopSettings
{
    public bool Enabled { get; set; } = false;
    public Hotkey Hotkey { get; set; } = Hotkey.Parse("Win+Ctrl+T");
    public Hotkey IncreaseOpacity { get; set; } = Hotkey.Parse("Win+Ctrl+=");
    public Hotkey DecreaseOpacity { get; set; } = Hotkey.Parse("Win+Ctrl+-");
    public bool ShowFrame { get; set; } = true;
    public int FrameThickness { get; set; } = 7;
    public string FrameColor { get; set; } = "#0058CC";
    public int FrameOpacity { get; set; } = 40;
    public bool FrameUsesAccentColor { get; set; } = false;
    public bool RoundCorners { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool SkipInGameMode { get; set; } = true;
    public string ExcludedApps { get; set; } = "";
}

public enum ColorPickerActivation { OpenEditor, OpenColorPicker }
public enum ColorPickerClick { PickColorThenEditor, PickColorAndClose, Close }

public sealed class ColorPickerSettings
{
    public bool Enabled { get; set; } = false;
    public Hotkey Hotkey { get; set; } = Hotkey.Parse("Win+Shift+C");
    public ColorPickerActivation Activation { get; set; } = ColorPickerActivation.OpenColorPicker;
    public ColorPickerClick PrimaryClick { get; set; } = ColorPickerClick.PickColorAndClose;
    public ColorPickerClick MiddleClick { get; set; } = ColorPickerClick.PickColorThenEditor;
    public ColorPickerClick SecondaryClick { get; set; } = ColorPickerClick.Close;
    /// <summary>Format copied to the clipboard (one of the format names below).</summary>
    public string CopiedFormat { get; set; } = "HEX";
    public bool ShowColorName { get; set; } = true;
    public int HistoryLimit { get; set; } = 20;
    public List<string> History { get; set; } = new();
    /// <summary>Format name → (shown in the editor, template). PowerToys template syntax.</summary>
    public Dictionary<string, ColorFormat> Formats { get; set; } = ColorFormat.Defaults();
}

public sealed class ColorFormat
{
    public bool Visible { get; set; }
    public string Template { get; set; } = "";

    public static Dictionary<string, ColorFormat> Defaults() => new()
    {
        ["HEX"] = new() { Visible = true, Template = "%Rex%Grx%Blx" },
        ["RGB"] = new() { Visible = false, Template = "rgb(%Re, %Gr, %Bl)" },
        ["HSL"] = new() { Visible = false, Template = "hsl(%Hu, %Sl%, %Ll%)" },
        ["HSV"] = new() { Visible = false, Template = "hsv(%Hu, %Sb%, %Va%)" },
        ["CMYK"] = new() { Visible = false, Template = "cmyk(%Cy%, %Ma%, %Ye%, %Bk%)" },
        ["HSB"] = new() { Visible = false, Template = "hsb(%Hu, %Sb%, %Br%)" },
        ["HSI"] = new() { Visible = false, Template = "hsi(%Hu, %Si%, %In%)" },
        ["HWB"] = new() { Visible = false, Template = "hwb(%Hu, %Wh%, %Bn%)" },
        ["NCol"] = new() { Visible = false, Template = "%Hn, %Wh%, %Bn%" },
        ["Decimal"] = new() { Visible = false, Template = "%Dv" },
        ["HEX Int"] = new() { Visible = false, Template = "0xFF%ReX%GrX%BlX" },
        ["VEC4"] = new() { Visible = false, Template = "(%Reff, %Grff, %Blff, 1f)" },
    };
}

public sealed class CropAndLockSettings
{
    public bool Enabled { get; set; } = false;
    public Hotkey ThumbnailHotkey { get; set; } = Hotkey.Parse("Win+Ctrl+Shift+T");
}

public sealed class LocksmithSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Only in the Shift+right-click menu (PowerToys: showInExtendedContextMenu).</summary>
    public bool ExtendedMenuOnly { get; set; } = true;
}

public enum GrabModifier { Alt, Win }

public sealed class GrabAndMoveSettings
{
    public bool Enabled { get; set; } = false;
    public GrabModifier Modifier { get; set; } = GrabModifier.Win;
    /// <summary>Modifier + right-drag resizes from the nearest edge or corner.</summary>
    public bool ResizeWithRightButton { get; set; } = true;
    public bool SkipInGameMode { get; set; } = true;
    public string ExcludedApps { get; set; } = "";
}

public sealed class ZoneRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A canvas layout: zones in reference coordinates, scaled to each monitor's work area.</summary>
public sealed class ZoneLayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("B").ToUpperInvariant();
    public string Name { get; set; } = "Layout";
    public int RefWidth { get; set; } = 1920;
    public int RefHeight { get; set; } = 1080;
    public int SensitivityRadius { get; set; } = 20;
    public List<ZoneRect> Zones { get; set; } = new();
}

public sealed class ZoneHistoryEntry
{
    public string AppPath { get; set; } = "";
    public string Monitor { get; set; } = "";
    public List<int> Zones { get; set; } = new();
}

public enum OverlapAlgorithm { Smallest, Largest, Positional, ClosestCenter }

public sealed class FancyZonesSettings
{
    public bool Enabled { get; set; } = false;
    public bool ShiftDrag { get; set; } = true;
    public bool OverrideSnapKeys { get; set; } = true;
    public OverlapAlgorithm OverlapAlgorithm { get; set; } = OverlapAlgorithm.Positional;
    public bool MoveOnDisplayChange { get; set; } = true;
    public bool MoveToLastZone { get; set; } = true;
    public bool MakeDraggedWindowTransparent { get; set; } = true;
    public bool UseSystemTheme { get; set; } = true;
    public string HighlightColor { get; set; } = "#4E26D7";
    public string ZoneColor { get; set; } = "#F5FCFF";
    public string BorderColor { get; set; } = "#FFFFFF";
    public int HighlightOpacity { get; set; } = 20;
    public bool ShowZoneNumbers { get; set; } = false;
    public Hotkey EditorHotkey { get; set; } = Hotkey.Parse("Win+Shift+`");
    public string ExcludedApps { get; set; } = "";
    public List<ZoneLayout> Layouts { get; set; } = new();
    /// <summary>Layout used on every monitor unless overridden below.</summary>
    public string DefaultLayoutId { get; set; } = "";
    /// <summary>Monitor device id → layout id ("" = no zones on that monitor).</summary>
    public Dictionary<string, string> MonitorLayouts { get; set; } = new();
    public List<ZoneHistoryEntry> History { get; set; } = new();
}

public sealed class ToolsSettings
{
    /// <summary>Set once the PowerToys settings have been imported (so it only happens once).</summary>
    public bool PowerToysImported { get; set; }
    public AwakeSettings Awake { get; set; } = new();
    public AlwaysOnTopSettings AlwaysOnTop { get; set; } = new();
    public ColorPickerSettings ColorPicker { get; set; } = new();
    public CropAndLockSettings CropAndLock { get; set; } = new();
    public LocksmithSettings Locksmith { get; set; } = new();
    public GrabAndMoveSettings GrabAndMove { get; set; } = new();
    public FancyZonesSettings FancyZones { get; set; } = new();
}
