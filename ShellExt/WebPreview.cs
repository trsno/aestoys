using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ColorCode;
using ColorCode.Styling;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Aestoys.ShellExt;

/// <summary>
/// Preview built from HTML in WebView2. Scripts are off and a content policy blocks anything from
/// the network, so a previewed file can't phone home; clicked links open in the default browser.
/// </summary>
public abstract class WebPreviewBase : PreviewHandlerBase
{
    protected bool IsDark => Back.GetBrightness() < 0.5f;

    protected abstract string BodyHtml();

    protected virtual string ExtraCss => "";

    protected override Control CreatePreview()
    {
        string bg = ColorTranslator.ToHtml(Back), fg = ColorTranslator.ToHtml(Fore);
        string html = $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'">
            <style>
              :root { color-scheme: {{(IsDark ? "dark" : "light")}}; }
              html, body { background: {{bg}}; color: {{fg}}; margin: 0; }
              body { font: 14px/1.55 "Segoe UI", sans-serif; padding: 12px 16px; }
              a { color: {{(IsDark ? "#7cb7ff" : "#0b62c4")}}; }
              {{ExtraCss}}
            </style></head><body>{{BodyHtml()}}</body></html>
            """;

        // Large documents go through a temporary file (NavigateToString is limited to 2 MB).
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aestoys", "preview");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.html")) { try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(old); } catch { } }
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(file, html, Encoding.UTF8);

        var view = new WebView2 { DefaultBackgroundColor = Back };
        // COM calls us on the preview host's UI thread; make async continuations come back to it.
        if (SynchronizationContext.Current is null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Start(view, file);
        return view;
    }

    private static async void Start(WebView2 view, string file)
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aestoys", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            if (view.IsDisposed) return;
            await view.EnsureCoreWebView2Async(env);
            if (view.IsDisposed) return;
            var s = view.CoreWebView2.Settings;
            s.IsScriptEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            string start = new Uri(file).AbsoluteUri;
            view.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri == start) return;
                e.Cancel = true;
                if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            view.CoreWebView2.Navigate(start);
        }
        catch { }
    }
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class MarkdownPreview : WebPreviewBase, IInitializeWithStream
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a0a";
    protected override int MaxBytes => 8 << 20;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    protected override string BodyHtml() => Markdown.ToHtml(Text.Decode(Data), Pipeline);

    protected override string ExtraCss => $$"""
        body { max-width: 900px; }
        h1, h2 { border-bottom: 1px solid {{(IsDark ? "#3d3d3d" : "#d8d8d8")}}; padding-bottom: .3em; }
        code, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
        code { background: {{(IsDark ? "#2d2d2d" : "#f0f0f0")}}; padding: .1em .35em; border-radius: 4px; }
        pre { background: {{(IsDark ? "#2a2a2a" : "#f5f5f5")}}; padding: 10px 12px; border-radius: 6px; overflow: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; } th, td { border: 1px solid {{(IsDark ? "#444" : "#ccc")}}; padding: 4px 8px; }
        blockquote { margin: 0; padding-left: 1em; border-left: 3px solid {{(IsDark ? "#555" : "#ccc")}}; opacity: .85; }
        img { max-width: 100%; }
        """;
}

[ComVisible(true), Guid(Clsid), ClassInterface(ClassInterfaceType.None)]
public sealed class CodePreview : WebPreviewBase, IInitializeWithFile
{
    public const string Clsid = "6a9c3f0e-5b1d-4c8e-9f2a-7d4e1b3c5a0b";
    private const int MaxChars = 2 << 20;
    protected override int MaxBytes => 8 << 20;

    protected override string BodyHtml()
    {
        string text = Text.Decode(Data);
        bool cut = text.Length > MaxChars;
        if (cut) text = text[..MaxChars];
        var lang = ByExtension(Path.GetExtension(FilePath ?? "")) ?? Guess(text);
        string code;
        if (lang is null) code = WebUtility.HtmlEncode(text);
        else
        {
            var formatter = new HtmlFormatter(IsDark ? StyleDictionary.DefaultDark : StyleDictionary.DefaultLight);
            code = formatter.GetHtmlString(text, lang);
            // GetHtmlString wraps in its own <div style=...><pre>; keep only the coloured spans' container.
        }
        return "<div class=\"code\">" + code + "</div>" + (cut ? "<p class=\"note\">Only the first 2 MB are shown.</p>" : "");
    }

    protected override string ExtraCss => $$"""
        body { padding: 8px 10px; }
        .code, .code pre, .code div { font-family: "Cascadia Mono", Consolas, monospace !important; font-size: {{Options.Int("CodeFontSize", 14)}}px !important;
                background: transparent !important; margin: 0; white-space: {{(Options.Bool("CodeWordWrap", true) ? "pre-wrap" : "pre")}}; word-break: break-word; }
        .note { opacity: .6; font-size: 12px; }
        """;

    private static ILanguage? ByExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" or ".csx" => Languages.CSharp,
        ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".ino" => Languages.Cpp,
        ".java" => Languages.Java,
        ".js" or ".mjs" or ".cjs" or ".jsx" or ".json" or ".jsonc" => Languages.JavaScript,
        ".ts" or ".tsx" or ".mts" => Languages.Typescript,
        ".py" or ".pyw" => Languages.Python,
        ".ps1" or ".psm1" or ".psd1" => Languages.PowerShell,
        ".sql" => Languages.Sql,
        ".css" or ".scss" or ".less" => Languages.Css,
        ".php" => Languages.Php,
        ".vb" => Languages.VbDotNet,
        ".fs" or ".fsx" => Languages.FSharp,
        ".hs" => Languages.Haskell,
        ".xaml" or ".csproj" or ".vbproj" or ".props" or ".targets" or ".config" or ".resx" or ".svg" => Languages.Xml,
        ".md" or ".markdown" => Languages.Markdown,
        _ => null,
    };

    /// <summary>Fallback for other extensions: simple content clues.</summary>
    private static ILanguage? Guess(string t)
    {
        string head = t.Length > 4000 ? t[..4000] : t;
        bool Has(string s) => head.Contains(s, StringComparison.Ordinal);
        if (head.TrimStart().StartsWith("<?xml") || head.TrimStart().StartsWith("<Project") || head.TrimStart().StartsWith("<Window")) return Languages.Xml;
        if (head.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || head.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return Languages.Html;
        if (Has("namespace ") && Has(";") && (Has("using ") || Has("class "))) return Languages.CSharp;
        if (Has("#include") || Has("std::")) return Languages.Cpp;
        if (Has("def ") && Has(":") && (Has("import ") || Has("self"))) return Languages.Python;
        if (Has("param(") || Has("$env:") || Has("Get-") || Has("Write-Host")) return Languages.PowerShell;
        if (Has("public class ") && Has("import java")) return Languages.Java;
        if (Has("interface ") && Has(": string") || Has(": number")) return Languages.Typescript;
        if (Has("function ") || Has("const ") || Has("=>") || Has("require(")) return Languages.JavaScript;
        if (Has("SELECT ") || Has("CREATE TABLE")) return Languages.Sql;
        if (Has("{") && Has(":") && Has(";") && (Has("px") || Has("color"))) return Languages.Css;
        if (Has("<?php")) return Languages.Php;
        return null;
    }
}

internal static class Text
{
    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, else the ANSI code page.</summary>
    public static string Decode(byte[] d)
    {
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) return Encoding.Unicode.GetString(d, 2, d.Length - 2);
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(d).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(d);
        }
    }
}
