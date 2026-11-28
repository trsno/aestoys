using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Aestoys.Services;
using Microsoft.Win32;

namespace Aestoys.Features.Tools;

/// <summary>
/// File Explorer add-ons (replaces PowerToys' File Explorer add-ons): preview-pane handlers and
/// thumbnails for SVG, Markdown, source code, PDF, G-code, STL and QOI. The handlers live in
/// aestoys.ShellExt (shellext\&lt;build&gt;\ next to aestoys.exe) and run inside Windows' own
/// isolated hosts (prevhost / COM Surrogate), never in aestoys or Explorer. aestoys only writes
/// the per-user registration (HKCU) and removes it again when an add-on is switched off.
/// </summary>
internal static class ExplorerAddons
{
    public enum Kind { Preview, Thumbnail }

    public sealed record Addon(string Key, string Name, Kind Kind, string Clsid, string[] Extensions);

    private const string PreviewIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string ThumbIid = "{e357fccd-a995-4576-b01f-234630154e96}";
    private const string PrevhostAppId = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";

    private static readonly string[] CodeExtensions =
    {
        ".cs", ".csx", ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".ino", ".java", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".mts",
        ".json", ".jsonc", ".py", ".pyw", ".ps1", ".psm1", ".psd1", ".sql", ".css", ".scss", ".less", ".php", ".vb", ".fs", ".fsx", ".hs",
        ".xaml", ".csproj", ".vbproj", ".props", ".targets", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".sh", ".bash", ".bat", ".cmd",
        ".go", ".rs", ".rb", ".lua", ".kt", ".swift", ".dart", ".r", ".ahk", ".gitignore", ".editorconfig", ".log",
    };

    public static readonly Addon[] All =
    {
        new("SvgPreview", "SVG preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a02}", new[] { ".svg" }),
        new("SvgThumbnail", "SVG thumbnails", Kind.Thumbnail, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a01}", new[] { ".svg" }),
        new("MarkdownPreview", "Markdown preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a0a}", new[] { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext" }),
        new("CodePreview", "Source code preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a0b}", CodeExtensions),
        new("PdfPreview", "PDF preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a09}", new[] { ".pdf" }),
        new("PdfThumbnail", "PDF thumbnails", Kind.Thumbnail, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a08}", new[] { ".pdf" }),
        new("GcodePreview", "G-code preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a06}", new[] { ".gcode", ".bgcode" }),
        new("GcodeThumbnail", "G-code thumbnails", Kind.Thumbnail, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a05}", new[] { ".gcode", ".bgcode" }),
        new("StlThumbnail", "STL thumbnails", Kind.Thumbnail, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a07}", new[] { ".stl" }),
        new("QoiPreview", "QOI preview", Kind.Preview, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a04}", new[] { ".qoi" }),
        new("QoiThumbnail", "QOI thumbnails", Kind.Thumbnail, "{6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a03}", new[] { ".qoi" }),
    };

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>The newest add-on build next to aestoys.exe, or null when there is none (e.g. a dev build).</summary>
    public static string? ComHost()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "shellext");
        if (!Directory.Exists(root)) return null;
        return new DirectoryInfo(root).GetDirectories()
            .Where(d => d.Name.Length == 14 && d.Name.All(char.IsAsciiDigit))   // build stamps (yyyyMMddHHmmss)
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => Path.Combine(d.FullName, "aestoys.ShellExt.comhost.dll"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Registers the switched-on add-ons, unregisters the rest. Safe to call repeatedly.</summary>
    public static void Apply(AppSettings settings)
    {
        var s = settings.Tools.ExplorerAddons;
        string? host = ComHost();
        if (host is null) return;   // nothing to point at: leave any existing registration alone
        bool changed = false;
        WriteOptions(s);
        foreach (var a in All)
        {
            bool want = s.Enabled && s.On.GetValueOrDefault(a.Key, true);
            changed |= want ? Register(a, host) : Unregister(a);
        }
        if (changed) SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        CleanOldBuilds(Path.GetDirectoryName(host)!);
    }

    private static void WriteOptions(ExplorerAddonsSettings s)
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\aestoys\ExplorerAddons");
        k.SetValue("StlColor", s.StlColor);
        k.SetValue("CodeWordWrap", s.CodeWordWrap ? 1 : 0, RegistryValueKind.DWord);
        k.SetValue("CodeFontSize", s.CodeFontSize, RegistryValueKind.DWord);
    }

    private static bool Register(Addon a, string host)
    {
        bool changed = false;
        using (var c = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{a.Clsid}"))
        {
            c.SetValue("", "aestoys " + a.Name);
            c.SetValue("DisplayName", "aestoys " + a.Name);
            if (a.Kind == Kind.Preview)
            {
                c.SetValue("AppID", PrevhostAppId);
                // The preview host normally runs at low integrity, where it can't write the WebView2
                // profile or read the previewed file's folder; PowerToys sets the same.
                c.SetValue("DisableLowILProcessIsolation", 1, RegistryValueKind.DWord);
            }
            using var inproc = c.CreateSubKey("InprocServer32");
            if (!string.Equals(inproc.GetValue("") as string, host, StringComparison.OrdinalIgnoreCase)) changed = true;
            inproc.SetValue("", host);
            inproc.SetValue("ThreadingModel", "Apartment");
        }
        string iid = a.Kind == Kind.Preview ? PreviewIid : ThumbIid;
        foreach (var ext in a.Extensions)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\ShellEx\{iid}");
            if (!string.Equals(k.GetValue("") as string, a.Clsid, StringComparison.OrdinalIgnoreCase)) { k.SetValue("", a.Clsid); changed = true; }
        }
        if (a.Kind == Kind.Preview)
            using (var p = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"))
                p.SetValue(a.Clsid, "aestoys " + a.Name);
        return changed;
    }

    /// <summary>Removes only what points at our own add-on, never another app's handler.</summary>
    private static bool Unregister(Addon a)
    {
        bool changed = false;
        string iid = a.Kind == Kind.Preview ? PreviewIid : ThumbIid;
        foreach (var ext in a.Extensions)
        {
            string path = $@"Software\Classes\{ext}\ShellEx\{iid}";
            using var k = Registry.CurrentUser.OpenSubKey(path);
            if (k is null || !string.Equals(k.GetValue("") as string, a.Clsid, StringComparison.OrdinalIgnoreCase)) continue;
            k.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            changed = true;
        }
        if (Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{a.Clsid}") is { } existing)
        {
            existing.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{a.Clsid}", false);
            changed = true;
        }
        using (var p = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers", writable: true))
            if (p?.GetValue(a.Clsid) is not null) p.DeleteValue(a.Clsid, false);
        return changed;
    }

    /// <summary>Old add-on builds are deleted once Windows' hosts no longer have them loaded.</summary>
    private static void CleanOldBuilds(string current)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetDirectoryName(current)!))
                if (!string.Equals(d, current, StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        // If a host still has this build loaded, its comhost can't be deleted: keep the
                        // whole folder (deleting the rest would break that host's next load) and retry later.
                        string comhost = Path.Combine(d, "aestoys.ShellExt.comhost.dll");
                        if (File.Exists(comhost)) File.Delete(comhost);
                        Directory.Delete(d, true);
                    }
                    catch { }
        }
        catch { }
    }

    /// <summary>Removes every aestoys add-on registration (used when aestoys is told to clean up).</summary>
    public static void UnregisterAll()
    {
        foreach (var a in All) Unregister(a);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
