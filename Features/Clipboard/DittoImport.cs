using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Aestoys.Services;
using Microsoft.Win32;

namespace Aestoys.Features.Clipboard;

/// <summary>
/// One-time import of Ditto's clipboard history. aestoys uses Ditto's table layout, so the
/// database is copied as-is; afterwards every clipboard format is compared by row count and
/// total bytes against the original, and the import only counts as done when they all match.
/// </summary>
internal static class DittoImport
{
    public static string? SourcePath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Ditto");
        var p = k?.GetValue("DBPath3") as string;
        if (string.IsNullOrEmpty(p)) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ditto", "Ditto.db");
        return File.Exists(p) ? p : null;
    }

    public static string Run(AppSettings settings)
    {
        string? source = SourcePath();
        if (source is null) return "Ditto database not found";
        if (Process.GetProcessesByName("Ditto").Length > 0) return "Ditto is still running; close it first so its database is complete";

        string target = ClipboardStore.DefaultPath;
        if (File.Exists(target))
        {
            using (var existing = new ClipboardStore(target))
                if (existing.Count() > 0) return $"aestoys already has {existing.Count()} clips; not overwriting them";
            foreach (var f in new[] { target, target + "-wal", target + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".importing";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target);

        var before = ClipboardStore.Census(source);
        // Open once through the store (switches the copy to WAL and adds any missing index), then recount.
        using (new ClipboardStore(target)) { }
        var after = ClipboardStore.Census(target);

        var mismatches = before.Where(b => !after.Contains(b)).Select(b =>
        {
            var a = after.FirstOrDefault(x => x.format == b.format);
            return $"{b.format}: Ditto {b.rows} rows/{b.bytes} bytes, aestoys {a.rows} rows/{a.bytes} bytes";
        }).ToList();
        string table = string.Join("; ", before.Select(b => $"{b.format} {b.rows}" + (b.bytes > 0 ? $" ({b.bytes:N0} B)" : "")));
        if (mismatches.Count > 0)
        {
            string fail = "Ditto import MISMATCH: " + string.Join("; ", mismatches);
            App.Log(fail);
            return fail;
        }

        var c = settings.Tools.Clipboard;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Ditto"))
            if (k?.GetValue("MaxEntries") is int max && max > 0) c.MaxEntries = max;
        c.Enabled = true;
        c.DittoImported = true;
        settings.Save();
        string ok = "Ditto import verified, all formats identical: " + table;
        App.Log(ok);
        return ok;
    }
}
