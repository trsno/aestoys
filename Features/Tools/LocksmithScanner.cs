using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Aestoys.Features.Tools;

internal sealed record LockingProcess(int Pid, string Name, string? ExePath, string User, List<string> Files);

/// <summary>
/// Finds the processes that hold files or folders open, the way PowerToys File Locksmith does
/// (MIT, see ThirdParty/PowerToys): walk every open handle in the system
/// (NtQuerySystemInformation), keep the File handles, read their kernel names, and match them
/// against the targets (anything under a target folder counts); then add processes that have a
/// matching module (DLL or exe) loaded. Name lookups can hang on some handles, so they run on a
/// helper thread that is abandoned when it stalls.
/// </summary>
internal static class LocksmithScanner
{
    // ---------------------------------------------------------------- native
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int ret);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint QueryDosDevice(string device, StringBuilder target, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EnumProcessModulesEx(IntPtr process, IntPtr[] modules, int cb, out int needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder name, int size);

    private const int SystemExtendedHandleInformation = 64, ObjectNameInformation = 1;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint PROCESS_DUP_HANDLE = 0x40, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_QUERY_INFORMATION = 0x400, PROCESS_VM_READ = 0x10;
    private const uint DUPLICATE_SAME_ACCESS = 2, FILE_TYPE_DISK = 1;
    private const int EntrySize = 40; // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX on x64: Object 0, Pid 8, Handle 16, Access 24, BackTrace 28, TypeIndex 30

    // ---------------------------------------------------------------- public

    public static List<LockingProcess> Find(IReadOnlyList<string> paths)
    {
        // Kernel names (\Device\HarddiskVolume3\...) of the targets.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            string full;
            try { full = Path.GetFullPath(p).TrimEnd('\\'); } catch { continue; }
            if (full.Length == 2 && full[1] == ':') full += "\\";
            var k = KernelName(full);
            if (k is null) continue;
            (Directory.Exists(full) ? dirs : files)[k.TrimEnd('\\')] = full;
        }

        string? Match(string kernel)
        {
            if (files.TryGetValue(kernel, out var f)) return f;
            if (dirs.TryGetValue(kernel, out var d)) return d;
            foreach (var (dk, dp) in dirs)
                if (kernel.Length > dk.Length && kernel.StartsWith(dk, StringComparison.OrdinalIgnoreCase) && kernel[dk.Length] == '\\')
                    return dp.TrimEnd('\\') + kernel[dk.Length..];
            return null;
        }

        var found = new Dictionary<int, SortedSet<string>>();
        void Add(int pid, string path)
        {
            if (!found.TryGetValue(pid, out var set)) found[pid] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(path);
        }

        foreach (var (pid, kernel) in FileHandles())
            if (Match(kernel) is { } m) Add(pid, m);

        // Loaded modules (a running exe or DLL inside the target).
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                if (proc.Id is 0 or 4) continue;
                foreach (var module in Modules(proc.Id))
                {
                    var k = KernelName(module);
                    if (k is not null && Match(k) is { } m) Add(proc.Id, m);
                }
            }
        }

        var result = new List<LockingProcess>();
        foreach (var (pid, set) in found)
        {
            if (pid == Environment.ProcessId) continue;
            string name = "", user = "";
            string? exe = null;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = $"PID {pid}"; }
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) exe = sb.ToString();
                user = UserOf(h);
                CloseHandle(h);
            }
            result.Add(new LockingProcess(pid, name, exe, user, set.ToList()));
        }
        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- handles

    private static IEnumerable<(int pid, string kernel)> FileHandles()
    {
        int len = 1 << 20;
        IntPtr buf = IntPtr.Zero;
        try
        {
            while (true)
            {
                buf = Marshal.AllocHGlobal(len);
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out int needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (status != STATUS_INFO_LENGTH_MISMATCH || len > 1 << 30) yield break;
                len = Math.Max(len * 2, needed + (1 << 16));
            }

            long count = Marshal.ReadIntPtr(buf).ToInt64();
            IntPtr first = buf + 2 * IntPtr.Size;
            int fileType = FileTypeIndex(first, count);
            int me = Environment.ProcessId;
            var processes = new Dictionary<int, IntPtr>();
            using var namer = new NameQuery();
            try
            {
                for (long i = 0; i < count; i++)
                {
                    IntPtr e = first + (int)(i * EntrySize);
                    int pid = (int)Marshal.ReadIntPtr(e, 8).ToInt64();
                    int type = (ushort)Marshal.ReadInt16(e, 30);
                    if (pid == me || (fileType >= 0 && type != fileType)) continue;
                    IntPtr value = Marshal.ReadIntPtr(e, 16);

                    if (!processes.TryGetValue(pid, out var ph))
                        processes[pid] = ph = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    if (ph == IntPtr.Zero) continue;
                    if (!DuplicateHandle(ph, value, GetCurrentProcess(), out IntPtr dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                    string? name = namer.Query(dup);   // closes dup unless the query stalled
                    if (!string.IsNullOrEmpty(name)) yield return (pid, name);
                }
            }
            finally
            {
                foreach (var ph in processes.Values) if (ph != IntPtr.Zero) CloseHandle(ph);
            }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>The object type number Windows uses for File handles, found from a handle of our own.</summary>
    private static int FileTypeIndex(IntPtr first, long count)
    {
        try
        {
            using var fs = new FileStream(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            IntPtr mine = fs.SafeFileHandle.DangerousGetHandle();
            int me = Environment.ProcessId;
            for (long i = 0; i < count; i++)
            {
                IntPtr e = first + (int)(i * EntrySize);
                if ((int)Marshal.ReadIntPtr(e, 8).ToInt64() == me && Marshal.ReadIntPtr(e, 16) == mine)
                    return (ushort)Marshal.ReadInt16(e, 30);
            }
        }
        catch { }
        return -1;
    }

    /// <summary>
    /// GetFileType / NtQueryObject can block forever on some handles (e.g. synchronous pipes with
    /// pending I/O). They run on a helper thread; when one stalls the thread is abandoned (never
    /// killed) and a fresh one takes over.
    /// </summary>
    private sealed class NameQuery : IDisposable
    {
        private sealed class Worker
        {
            public readonly AutoResetEvent Request = new(false), Done = new(false);
            public IntPtr Handle;
            public string? Result;
            public volatile bool Quit;
        }

        private Worker? _worker;

        public string? Query(IntPtr handle)
        {
            _worker ??= Start();
            var w = _worker;
            w.Handle = handle;
            w.Result = null;
            w.Request.Set();
            if (!w.Done.WaitOne(200))
            {
                _worker = null;   // stalled: abandon it (it closes the handle if it ever wakes up)
                return null;
            }
            return w.Result;
        }

        private static Worker Start()
        {
            var w = new Worker();
            var t = new Thread(() =>
            {
                IntPtr buf = Marshal.AllocHGlobal(4096);
                try
                {
                    while (true)
                    {
                        w.Request.WaitOne();
                        if (w.Quit) return;
                        string? name = null;
                        try
                        {
                            if (GetFileType(w.Handle) == FILE_TYPE_DISK && NtQueryObject(w.Handle, ObjectNameInformation, buf, 4096, out _) == 0)
                            {
                                int len = (ushort)Marshal.ReadInt16(buf);
                                IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                                if (len > 0 && str != IntPtr.Zero) name = Marshal.PtrToStringUni(str, len / 2);
                            }
                        }
                        catch { }
                        CloseHandle(w.Handle);
                        w.Result = name;
                        w.Done.Set();
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            })
            { IsBackground = true, Name = "locksmith name query" };
            t.Start();
            return w;
        }

        public void Dispose()
        {
            if (_worker is { } w) { w.Quit = true; w.Request.Set(); }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Dictionary<string, string?> DeviceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>C:\x → \Device\HarddiskVolume3\x (UNC paths → \Device\Mup\server\share\x).</summary>
    private static string? KernelName(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\Device\Mup\" + path[2..];
        if (path.Length < 2 || path[1] != ':') return null;
        string drive = path[..2];
        if (!DeviceCache.TryGetValue(drive, out var device))
        {
            var sb = new StringBuilder(1024);
            device = QueryDosDevice(drive, sb, sb.Capacity) > 0 ? sb.ToString() : null;
            DeviceCache[drive] = device;
        }
        return device is null ? null : device + path[2..];
    }

    private static IEnumerable<string> Modules(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) yield break;
        try
        {
            var mods = new IntPtr[1024];
            if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out int needed, 3 /* LIST_MODULES_ALL */)) yield break;
            int n = Math.Min(mods.Length, needed / IntPtr.Size);
            var sb = new StringBuilder(1024);
            for (int i = 0; i < n; i++)
            {
                sb.Clear();
                if (GetModuleFileNameEx(h, mods[i], sb, sb.Capacity) > 0) yield return sb.ToString();
            }
        }
        finally { CloseHandle(h); }
    }

    private static string UserOf(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x8 /* TOKEN_QUERY */, out IntPtr token)) return "";
        try
        {
            using var id = new WindowsIdentity(token);
            return id.Name;
        }
        catch { return ""; }
        finally { CloseHandle(token); }
    }
}
