using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aestoys.Services;

namespace Aestoys.Features.Tools;

/// <summary>
/// Keeps the PC (and optionally the display) awake: off, indefinitely, for a while, or until a
/// time. Execution state belongs to the thread that set it, so it is always set from the UI thread,
/// and it is re-applied from settings on start (the end time is stored as an absolute time).
/// </summary>
internal sealed class Awake : IDisposable
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;

    public event Action? Changed;

    public Awake(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Normal, (_, _) => CheckExpiry(), Dispatcher.CurrentDispatcher);
        Apply();
    }

    private AwakeSettings S => _settings.Tools.Awake;

    public bool Active => S.Enabled && S.Mode != AwakeMode.Off;

    public string Describe() => !S.Enabled ? "Off" : S.Mode switch
    {
        AwakeMode.Indefinite => "Keeping awake indefinitely",
        AwakeMode.Timed or AwakeMode.Until when S.ExpiresAt is { } t => $"Keeping awake until {t.ToLocalTime():t}" + (t.ToLocalTime().Date != DateTime.Today ? $" on {t.ToLocalTime():d MMM}" : ""),
        _ => "Off (using the power plan)",
    };

    public void SetOff() => Set(AwakeMode.Off, null);
    public void SetIndefinite() => Set(AwakeMode.Indefinite, null);
    public void SetFor(TimeSpan span) => Set(AwakeMode.Timed, DateTime.UtcNow + span);
    public void SetUntil(DateTime localTime) => Set(AwakeMode.Until, localTime.ToUniversalTime());

    public void SetKeepDisplayOn(bool on)
    {
        S.KeepDisplayOn = on;
        _settings.Save();
        Apply();
    }

    private void Set(AwakeMode mode, DateTime? expiresUtc)
    {
        S.Mode = mode;
        S.ExpiresAt = expiresUtc;
        if (expiresUtc is { } e && mode == AwakeMode.Timed)
        {
            var span = e - DateTime.UtcNow;
            S.IntervalHours = (int)span.TotalHours;
            S.IntervalMinutes = span.Minutes;
        }
        _settings.Save();
        Apply();
    }

    /// <summary>Re-applies the stored mode (also after the feature is switched on or off).</summary>
    public void Apply()
    {
        CheckExpiry(save: false);
        if (Active)
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (S.KeepDisplayOn ? ES_DISPLAY_REQUIRED : 0));
            if (S.ExpiresAt is not null) _timer.Start(); else _timer.Stop();
        }
        else
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            _timer.Stop();
        }
        Changed?.Invoke();
    }

    private void CheckExpiry() => CheckExpiry(save: true);

    private void CheckExpiry(bool save)
    {
        if (S.Mode is AwakeMode.Timed or AwakeMode.Until && (S.ExpiresAt is null || S.ExpiresAt <= DateTime.UtcNow))
        {
            S.Mode = AwakeMode.Off;
            S.ExpiresAt = null;
            if (save) { _settings.Save(); Apply(); }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        SetThreadExecutionState(ES_CONTINUOUS);
    }
}
