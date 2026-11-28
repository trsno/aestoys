using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Aestoys.Interop;
using Microsoft.Win32;

namespace Aestoys.Features.Desktops;

/// <summary>
/// Virtual desktop state and control.
/// <list type="bullet">
/// <item>State (count, current index) is read from the registry: documented-enough, cheap and safe
/// from any thread, so the keyboard hook can use it.</item>
/// <item>Switching / moving / pinning uses Ciantic's VirtualDesktopAccessor.dll (open source,
/// third-party, undocumented Windows internals). Its answers are checked against the registry
/// before every jump; when it disagrees or is missing, callers fall back.</item>
/// </list>
/// DLL and COM calls must be made from the UI (STA) thread.
/// </summary>
internal static class VirtualDesktops
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string Dll = "VirtualDesktopAccessor";

    public static bool DllLoaded { get; private set; }
    public static string? DllPath { get; private set; }
    private static Win32.IVirtualDesktopManager? _vdm;

    static VirtualDesktops()
    {
        NativeLibrary.SetDllImportResolver(typeof(VirtualDesktops).Assembly, (name, _, _) =>
            name == Dll && DllPath is not null && NativeLibrary.TryLoad(DllPath, out var h) ? h : IntPtr.Zero);
    }

    /// <summary>Loads the DLL (next to aestoys.exe, else C:\Warez\VirtualDesktopAccessor) and the COM helper.</summary>
    public static void Init()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, @"C:\Warez\VirtualDesktopAccessor" })
        {
            var p = Path.Combine(dir, Dll + ".dll");
            if (File.Exists(p)) { DllPath = p; break; }
        }
        if (DllPath is not null)
        {
            try { DllLoaded = GetDesktopCount() > 0; }
            catch (Exception e) { App.Log("VirtualDesktopAccessor.dll failed to load: " + e.Message); DllLoaded = false; }
        }
        try { _vdm ??= (Win32.IVirtualDesktopManager)new Win32.VirtualDesktopManager(); } catch { _vdm = null; }
    }

    // ---------------------------------------------------------------- registry state (any thread)

    /// <summary>Desktop count and current index from the registry; index is -1 when unknown.</summary>
    public static (int count, int index) State()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || key.GetValue("CurrentVirtualDesktop") is not byte[] cur || cur.Length != 16)
                return (0, -1);
            int count = ids.Length / 16;
            for (int i = 0; i < count; i++)
                if (ids.AsSpan(i * 16, 16).SequenceEqual(cur)) return (count, i);
            return (count, -1);
        }
        catch { return (0, -1); }
    }

    /// <summary>The DLL's view matches the registry (so we trust it to act).</summary>
    public static bool DllAgrees(int count, int index)
    {
        if (!DllLoaded) return false;
        try { return GetDesktopCount() == count && GetCurrentDesktopNumber() == index; }
        catch { return false; }
    }

    public static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (_vdm is null) return true;
        try { return _vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out int on) == 0 && on != 0; }
        catch { return true; }
    }

    public static bool HasManager => _vdm is not null;

    // ---------------------------------------------------------------- VirtualDesktopAccessor.dll

    [DllImport(Dll)] public static extern int GetDesktopCount();
    [DllImport(Dll)] public static extern int GetCurrentDesktopNumber();
    [DllImport(Dll)] public static extern int GoToDesktopNumber(int number);
    [DllImport(Dll)] public static extern int GetWindowDesktopNumber(IntPtr hwnd);
    [DllImport(Dll)] public static extern int MoveWindowToDesktopNumber(IntPtr hwnd, int number);
    [DllImport(Dll)] public static extern int PinWindow(IntPtr hwnd);
    [DllImport(Dll)] public static extern int UnPinWindow(IntPtr hwnd);
    [DllImport(Dll)] public static extern int IsPinnedWindow(IntPtr hwnd);
}
