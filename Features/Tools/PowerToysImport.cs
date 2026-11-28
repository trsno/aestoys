using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Aestoys.Services;

namespace Aestoys.Features.Tools;

/// <summary>
/// One-time import from PowerToys (%LOCALAPPDATA%\Microsoft\PowerToys): which of the seven
/// utilities were on, FancyZones canvas layouts, per-monitor layout assignments and app zone
/// history, Color Picker history, and the settings that differ per user. The settings defaults
/// already match PowerToys' own, so values that are missing simply stay as they are.
/// </summary>
internal static class PowerToysImport
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");

    public static string Run(AppSettings settings)
    {
        var log = new List<string>();
        var t = settings.Tools;
        if (!Directory.Exists(Folder)) return "PowerToys settings not found";

        // --- which utilities were on
        using (var general = Open("settings.json"))
        {
            if (general?.RootElement.TryGetProperty("enabled", out var en) == true)
            {
                t.Awake.Enabled = Flag(en, "Awake");
                t.AlwaysOnTop.Enabled = Flag(en, "AlwaysOnTop");
                t.ColorPicker.Enabled = Flag(en, "ColorPicker");
                t.CropAndLock.Enabled = Flag(en, "CropAndLock");
                t.FancyZones.Enabled = Flag(en, "FancyZones");
                t.Locksmith.Enabled = Flag(en, "File Locksmith");
                t.GrabAndMove.Enabled = Flag(en, "GrabAndMove");
                log.Add("enabled: " + string.Join(", ", new[] { ("Awake", t.Awake.Enabled), ("AlwaysOnTop", t.AlwaysOnTop.Enabled), ("ColorPicker", t.ColorPicker.Enabled), ("CropAndLock", t.CropAndLock.Enabled), ("FancyZones", t.FancyZones.Enabled), ("Locksmith", t.Locksmith.Enabled), ("GrabAndMove", t.GrabAndMove.Enabled) }.Where(x => x.Item2).Select(x => x.Item1)));
            }
        }

        // --- Awake
        using (var d = Open(@"Awake\settings.json"))
            if (Props(d) is { } p)
            {
                if (Int(p, "mode") is int mode) t.Awake.Mode = mode switch { 1 => AwakeMode.Indefinite, _ => AwakeMode.Off }; // timed/expirable runs aren't carried over
                if (Bool(p, "keepDisplayOn") is bool kd) t.Awake.KeepDisplayOn = kd;
            }

        // --- Always On Top
        using (var d = Open(@"AlwaysOnTop\settings.json"))
            if (Props(d) is { } p)
            {
                if (Key(p, "hotkey") is { } hk) t.AlwaysOnTop.Hotkey = hk;
                if (Key(p, "increase-opacity-hotkey") is { } inc) t.AlwaysOnTop.IncreaseOpacity = inc;
                if (Key(p, "decrease-opacity-hotkey") is { } dec) t.AlwaysOnTop.DecreaseOpacity = dec;
                if (Bool(p, "frame-enabled") is bool fe) t.AlwaysOnTop.ShowFrame = fe;
                if (Int(p, "frame-thickness") is int th) t.AlwaysOnTop.FrameThickness = th;
                if (Str(p, "frame-color") is { } fc) t.AlwaysOnTop.FrameColor = fc;
                if (Int(p, "frame-opacity") is int fo) t.AlwaysOnTop.FrameOpacity = fo;
                if (Bool(p, "frame-accent-color") is bool fa) t.AlwaysOnTop.FrameUsesAccentColor = fa;
                if (Bool(p, "round-corners-enabled") is bool rc) t.AlwaysOnTop.RoundCorners = rc;
                if (Bool(p, "sound-enabled") is bool se) t.AlwaysOnTop.Sound = se;
                if (Bool(p, "do-not-activate-on-game-mode") is bool gm) t.AlwaysOnTop.SkipInGameMode = gm;
                if (Str(p, "excluded-apps") is { } ex) t.AlwaysOnTop.ExcludedApps = ex;
            }

        // --- Color Picker
        using (var d = Open(@"ColorPicker\settings.json"))
            if (Props(d) is { } p)
            {
                if (Key(p, "ActivationShortcut") is { } hk) t.ColorPicker.Hotkey = hk;
                if (Int(p, "activationaction") is int aa) t.ColorPicker.Activation = (ColorPickerActivation)Math.Clamp(aa, 0, 1);
                if (p.TryGetProperty("colorhistorylimit", out var hl) && Int(hl) is int lim) t.ColorPicker.HistoryLimit = lim;
                if (Bool(p, "showcolorname") is bool sn) t.ColorPicker.ShowColorName = sn;
                if (Str(p, "copiedcolorrepresentation") is { } cf) t.ColorPicker.CopiedFormat = cf;
                if (Int(p, "primaryclickaction") is int pc) t.ColorPicker.PrimaryClick = (ColorPickerClick)Math.Clamp(pc, 0, 2);
                if (Int(p, "middleclickaction") is int mc) t.ColorPicker.MiddleClick = (ColorPickerClick)Math.Clamp(mc, 0, 2);
                if (Int(p, "secondaryclickaction") is int sc) t.ColorPicker.SecondaryClick = (ColorPickerClick)Math.Clamp(sc, 0, 2);
                if (p.TryGetProperty("visiblecolorformats", out var vf) && vf.ValueKind == JsonValueKind.Object)
                    foreach (var f in vf.EnumerateObject())
                    {
                        // { "HEX": { "Key": true, "Value": "%Rex%Grx%Blx" } }
                        bool visible = f.Value.TryGetProperty("Key", out var k) && k.ValueKind == JsonValueKind.True;
                        string? template = f.Value.TryGetProperty("Value", out var v) ? v.GetString() : null;
                        if (!t.ColorPicker.Formats.TryGetValue(f.Name, out var fmt)) t.ColorPicker.Formats[f.Name] = fmt = new ColorFormat();
                        fmt.Visible = visible;
                        if (!string.IsNullOrEmpty(template)) fmt.Template = template;
                    }
            }
        using (var d = Open(@"ColorPicker\colorHistory.json"))
            if (d?.RootElement.ValueKind == JsonValueKind.Array)
            {
                var colors = d.RootElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
                foreach (var c in colors) if (!t.ColorPicker.History.Contains(c)) t.ColorPicker.History.Add(c);
                log.Add($"color history: {colors.Count}");
            }

        // --- Crop And Lock
        using (var d = Open(@"CropAndLock\settings.json"))
            if (Props(d) is { } p && Key(p, "thumbnail-hotkey") is { } hk) t.CropAndLock.ThumbnailHotkey = hk;

        // --- File Locksmith
        using (var d = Open(@"File Locksmith\file-locksmith-settings.json") ?? Open(@"File Locksmith\settings.json"))
            if (Props(d) is { } p && Bool(p, "showInExtendedContextMenu") is bool ext) t.Locksmith.ExtendedMenuOnly = ext;
            else if (d?.RootElement.TryGetProperty("showInExtendedContextMenu", out var e2) == true) t.Locksmith.ExtendedMenuOnly = e2.ValueKind == JsonValueKind.True;

        // --- Grab And Move
        using (var d = Open(@"GrabAndMove\settings.json"))
            if (Props(d) is { } p)
            {
                if (Int(p, "modifierKey") is int mk) t.GrabAndMove.Modifier = mk == 1 ? GrabModifier.Win : GrabModifier.Alt;
                if (Bool(p, "useAltResize") is bool ar) t.GrabAndMove.ResizeWithRightButton = ar;
                if (Bool(p, "doNotActivateOnGameMode") is bool gm) t.GrabAndMove.SkipInGameMode = gm;
            }

        // --- FancyZones settings
        var fz = t.FancyZones;
        using (var d = Open(@"FancyZones\settings.json"))
            if (Props(d) is { } p)
            {
                if (Bool(p, "fancyzones_shiftDrag") is bool a) fz.ShiftDrag = a;
                if (Bool(p, "fancyzones_overrideSnapHotkeys") is bool b) fz.OverrideSnapKeys = b;
                if (Int(p, "fancyzones_overlappingZonesAlgorithm") is int oa) fz.OverlapAlgorithm = (OverlapAlgorithm)Math.Clamp(oa, 0, 3);
                if (Bool(p, "fancyzones_displayOrWorkAreaChange_moveWindows") is bool c) fz.MoveOnDisplayChange = c;
                if (Bool(p, "fancyzones_appLastZone_moveWindows") is bool e) fz.MoveToLastZone = e;
                if (Bool(p, "fancyzones_makeDraggedWindowTransparent") is bool f) fz.MakeDraggedWindowTransparent = f;
                if (Bool(p, "fancyzones_systemTheme") is bool g) fz.UseSystemTheme = g;
                if (Str(p, "fancyzones_zoneHighlightColor") is { } hc) fz.HighlightColor = hc;
                if (Str(p, "fancyzones_zoneColor") is { } zc) fz.ZoneColor = zc;
                if (Str(p, "fancyzones_zoneBorderColor") is { } bc) fz.BorderColor = bc;
                if (Int(p, "fancyzones_highlight_opacity") is int ho) fz.HighlightOpacity = ho;
                if (Bool(p, "fancyzones_showZoneNumber") is bool sz) fz.ShowZoneNumbers = sz;
                if (Key(p, "fancyzones_editor_hotkey") is { } ek) fz.EditorHotkey = ek;
                if (Str(p, "fancyzones_excluded_apps") is { } xa) fz.ExcludedApps = xa;
            }

        // --- FancyZones layouts (canvas only; grid templates aren't ported)
        using (var d = Open(@"FancyZones\custom-layouts.json"))
            if (d?.RootElement.TryGetProperty("custom-layouts", out var layouts) == true)
                foreach (var l in layouts.EnumerateArray())
                {
                    if (l.GetProperty("type").GetString() != "canvas") { log.Add("skipped non-canvas layout " + l.GetProperty("name").GetString()); continue; }
                    var info = l.GetProperty("info");
                    var layout = new ZoneLayout
                    {
                        Id = l.GetProperty("uuid").GetString()!.ToUpperInvariant(),
                        Name = l.GetProperty("name").GetString() ?? "Layout",
                        RefWidth = info.GetProperty("ref-width").GetInt32(),
                        RefHeight = info.GetProperty("ref-height").GetInt32(),
                        SensitivityRadius = info.TryGetProperty("sensitivity-radius", out var sr) ? sr.GetInt32() : 20,
                        Zones = info.GetProperty("zones").EnumerateArray().Select(z => new ZoneRect
                        {
                            X = z.GetProperty("X").GetInt32(), Y = z.GetProperty("Y").GetInt32(),
                            Width = z.GetProperty("width").GetInt32(), Height = z.GetProperty("height").GetInt32(),
                        }).ToList(),
                    };
                    fz.Layouts.RemoveAll(x => x.Id.Equals(layout.Id, StringComparison.OrdinalIgnoreCase));
                    fz.Layouts.Add(layout);
                    log.Add($"layout {layout.Name}: {layout.Zones.Count} zones");
                }

        using (var d = Open(@"FancyZones\applied-layouts.json"))
            if (d?.RootElement.TryGetProperty("applied-layouts", out var applied) == true)
                foreach (var a in applied.EnumerateArray())
                {
                    string? mon = a.GetProperty("device").GetProperty("monitor").GetString();
                    var al = a.GetProperty("applied-layout");
                    string? uuid = al.GetProperty("uuid").GetString()?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(mon) || al.GetProperty("type").GetString() != "custom" || uuid is null) continue;
                    if (!fz.Layouts.Any(l => l.Id == uuid)) continue;
                    fz.MonitorLayouts.TryAdd(mon, uuid);
                }
        log.Add("monitors with a layout: " + string.Join(", ", fz.MonitorLayouts.Keys));

        using (var d = Open(@"FancyZones\app-zone-history.json"))
            if (d?.RootElement.TryGetProperty("app-zone-history", out var hist) == true)
            {
                int n = 0;
                foreach (var app in hist.EnumerateArray())
                {
                    string? path = app.GetProperty("app-path").GetString();
                    if (string.IsNullOrEmpty(path)) continue;
                    foreach (var h in app.GetProperty("history").EnumerateArray())
                    {
                        string? mon = h.GetProperty("device").GetProperty("monitor").GetString();
                        var zones = h.GetProperty("zone-index-set").EnumerateArray().Select(z => z.GetInt32()).ToList();
                        if (string.IsNullOrEmpty(mon) || zones.Count == 0) continue;
                        if (fz.History.Any(x => x.AppPath.Equals(path, StringComparison.OrdinalIgnoreCase) && x.Monitor == mon)) continue;
                        fz.History.Add(new ZoneHistoryEntry { AppPath = path, Monitor = mon, Zones = zones });
                        n++;
                    }
                }
                log.Add($"zone history: {n} entries");
            }

        t.PowerToysImported = true;
        settings.Save();
        string summary = string.Join("; ", log);
        App.Log("PowerToys import: " + summary);
        return summary;
    }

    // ------------------------------------------------------------------ helpers

    private static JsonDocument? Open(string relative)
    {
        string path = Path.Combine(Folder, relative);
        if (!File.Exists(path)) return null;
        try { return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (Exception e) { App.Log($"PowerToys import: {relative}: {e.Message}"); return null; }
    }

    private static JsonElement? Props(JsonDocument? d) =>
        d is not null && d.RootElement.TryGetProperty("properties", out var p) ? p : null;

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>PowerToys wraps values as { "value": x }; some are bare.</summary>
    private static JsonElement? Val(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) ? inner : v;
    }

    private static bool? Bool(JsonElement? p, string name) => p is { } e && Val(e, name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static int? Int(JsonElement? p, string name) => p is { } e && Val(e, name) is { } v ? Int(v) : null;
    private static int? Int(JsonElement v) => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i) ? i : null;
    private static string? Str(JsonElement? p, string name) => p is { } e && Val(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>{ "win": true, "ctrl": true, "alt": false, "shift": false, "code": 84 }</summary>
    private static Hotkey? Key(JsonElement? p, string name)
    {
        if (p is not { } e || Val(e, name) is not { ValueKind: JsonValueKind.Object } v) return null;
        if (!v.TryGetProperty("code", out var code) || code.GetInt32() == 0) return null;
        bool B(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.True;
        return new Hotkey(B("win"), B("ctrl"), B("alt"), B("shift"), code.GetInt32());
    }
}
