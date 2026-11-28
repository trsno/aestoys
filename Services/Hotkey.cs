using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Aestoys.Services;

/// <summary>A key with modifiers, e.g. Win+Ctrl+T. Stored in settings as text ("Win+Ctrl+T").</summary>
[JsonConverter(typeof(HotkeyJsonConverter))]
public readonly record struct Hotkey(bool Win, bool Ctrl, bool Alt, bool Shift, int Vk)
{
    public static readonly Hotkey None = default;
    public bool IsNone => Vk == 0;

    public override string ToString()
    {
        if (IsNone) return "None";
        var parts = new List<string>();
        if (Win) parts.Add("Win");
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyName(Vk));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("None", StringComparison.OrdinalIgnoreCase)) return None;
        bool win = false, ctrl = false, alt = false, shift = false;
        int vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": win = true; break;
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: vk = KeyFromName(raw); break;
            }
        }
        // "Win+Shift++" style: the key itself is "+"
        if (vk == 0 && text.EndsWith("++")) vk = 0xBB;
        return new Hotkey(win, ctrl, alt, shift, vk);
    }

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x2C] = "PrintScreen",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
        [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
    };

    public static string KeyName(int vk)
    {
        if (Names.TryGetValue(vk, out var n)) return n;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        return "0x" + vk.ToString("X2");
    }

    private static int KeyFromName(string name)
    {
        foreach (var (vk, n) in Names) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return vk;
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length > 1 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24) return 0x6F + f;
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out int d) && d is >= 0 and <= 9) return 0x60 + d;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[2..], System.Globalization.NumberStyles.HexNumber, null, out int h)) return h;
        return 0;
    }

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}

public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref System.Text.Json.Utf8JsonReader reader, Type t, System.Text.Json.JsonSerializerOptions o) => Hotkey.Parse(reader.GetString());
    public override void Write(System.Text.Json.Utf8JsonWriter writer, Hotkey value, System.Text.Json.JsonSerializerOptions o) => writer.WriteStringValue(value.ToString());
}
