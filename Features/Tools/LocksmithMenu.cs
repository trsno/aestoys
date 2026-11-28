using System;
using Microsoft.Win32;

namespace Aestoys.Features.Tools;

/// <summary>
/// The "Unlock with File Locksmith" entry in Explorer's right-click menu, for files, folders and
/// drives (per user, HKCU). On Windows 11 classic entries live under "Show more options";
/// with "extended only" it appears only on Shift+right-click, as in PowerToys.
/// </summary>
internal static class LocksmithMenu
{
    private const string Verb = "aestoys.locksmith";
    private static readonly string[] Targets = { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell" };

    public static void Apply(bool enabled, bool extendedOnly)
    {
        foreach (var t in Targets)
        {
            try
            {
                if (!enabled) { Registry.CurrentUser.DeleteSubKeyTree($@"{t}\{Verb}", false); continue; }
                using var key = Registry.CurrentUser.CreateSubKey($@"{t}\{Verb}");
                key.SetValue("MUIVerb", "Unlock with File Locksmith");
                key.SetValue("Icon", $"\"{Environment.ProcessPath}\",0");
                if (extendedOnly) key.SetValue("Extended", ""); else key.DeleteValue("Extended", false);
                using var cmd = key.CreateSubKey("command");
                cmd.SetValue("", $"\"{Environment.ProcessPath}\" --locksmith \"%1\"");
            }
            catch (Exception e) { App.Log("locksmith menu: " + e.Message); }
        }
    }
}
