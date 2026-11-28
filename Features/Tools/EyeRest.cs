using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Aestoys.Services;
using Microsoft.Win32;

namespace Aestoys.Features.Tools;

/// <summary>
/// Eye rest reminders (replaces EyeRest): every N minutes a notification says to look away for
/// 20 seconds (the 20-20-20 rule). Like EyeRest, the first reminder comes one full interval after
/// it starts. Unlike EyeRest, nothing is shown while the PC is locked, and unlocking restarts the
/// countdown, since you've just had a break.
/// </summary>
internal sealed class EyeRest : IDisposable
{
    public const string Title = "EyeRest reminder";
    public const string Message = "You should take rest for at least 20 seconds!";

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _locked;

    private EyeRestSettings S => _settings.Tools.EyeRest;

    public EyeRest(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) ;
        _timer.Tick += (_, _) => Remind();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        ApplySettings();
    }

    public bool Active => _timer.IsEnabled;

    public DateTime? Next { get; private set; }

    public void ApplySettings()
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(S.IntervalMinutes, 1, 240));
        if (!S.Enabled) { _timer.Stop(); Next = null; return; }
        if (_timer.IsEnabled && _timer.Interval == interval) return;
        Restart(interval);
    }

    private void Restart(TimeSpan interval)
    {
        _timer.Stop();
        _timer.Interval = interval;
        _timer.Start();
        Next = DateTime.Now + interval;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _locked = false;
            App.Current?.Dispatcher.BeginInvoke(() => { if (S.Enabled) Restart(_timer.Interval); });
        }
    }

    private void Remind()
    {
        Next = DateTime.Now + _timer.Interval;
        if (_locked || !S.ShowMessage) return;
        App.Notify(Title, Message);
    }

    /// <summary>Copies EyeRest's interval and on/off choices from its EyeRest.ini.</summary>
    public static string ImportEyeRest(AppSettings settings)
    {
        string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeRest", "EyeRest.ini");
        if (!File.Exists(ini)) return "EyeRest settings not found";
        var values = File.ReadAllLines(ini).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var s = settings.Tools.EyeRest;
        if (values.TryGetValue("ReminderIntervalMinutes", out var m) && int.TryParse(m, out int minutes) && minutes > 0) s.IntervalMinutes = minutes;
        if (values.TryGetValue("ShowMessage", out var show)) s.ShowMessage = show != "0";
        s.Enabled = true;
        s.EyeRestImported = true;
        settings.Save();
        return $"EyeRest import: every {s.IntervalMinutes} min, messages {(s.ShowMessage ? "on" : "off")}";
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
