using System;
using System.Diagnostics;
using System.IO;

namespace Aestoys.Services;

/// <summary>Timing log for the switcher, written to %AppData%\aestoys\perf.log. Only on while a --demo runs.</summary>
internal static class Perf
{
    public static volatile bool Enabled;
    private static readonly object Gate = new();

    public static long Now => Stopwatch.GetTimestamp();
    public static double Ms(long since) => Stopwatch.GetElapsedTime(since).TotalMilliseconds;

    public static void Log(string message)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(AppSettings.Dir, "perf.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}
