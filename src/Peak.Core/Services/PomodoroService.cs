namespace Peak.Core.Services;

public enum PomodoroPhase
{
    Idle,
    Work,
    ShortBreak,
    LongBreak
}

/// <summary>
/// Pomodoro timer state machine: 25-min work → 5-min short break.
/// After every 4 completed work sessions, a 15-min long break is used instead.
/// </summary>
public class PomodoroService : IDisposable
{
    public TimeSpan WorkDuration { get; set; } = TimeSpan.FromMinutes(25);
    public TimeSpan ShortBreakDuration { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan LongBreakDuration { get; set; } = TimeSpan.FromMinutes(15);
    public int LongBreakEvery { get; set; } = 4;

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;
    public TimeSpan Remaining { get; private set; }
    public bool IsRunning { get; private set; }
    public int CompletedWorkSessions { get; private set; }

    public event Action? Tick;
    public event Action<PomodoroPhase>? PhaseChanged;
    public event Action<PomodoroPhase>? PhaseFinished;

    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private DateTime _phaseEndsAt;

    public void Start()
    {
        if (Phase == PomodoroPhase.Idle)
        {
            SetPhase(PomodoroPhase.Work);
        }

        if (IsRunning) return;
        IsRunning = true;
        _phaseEndsAt = DateTime.UtcNow + Remaining;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        _ = RunAsync(_cts.Token);
        Tick?.Invoke();
    }

    public void Pause()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Remaining = _phaseEndsAt - DateTime.UtcNow;
        if (Remaining < TimeSpan.Zero) Remaining = TimeSpan.Zero;
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        Tick?.Invoke();
    }

    public void Reset()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        CompletedWorkSessions = 0;
        Phase = PomodoroPhase.Idle;
        Remaining = WorkDuration;
        PhaseChanged?.Invoke(Phase);
        Tick?.Invoke();
    }

    /// <summary>Skip to the next phase without waiting for the timer.</summary>
    public void Skip()
    {
        AdvancePhase();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct))
            {
                Remaining = _phaseEndsAt - DateTime.UtcNow;
                if (Remaining <= TimeSpan.Zero)
                {
                    Remaining = TimeSpan.Zero;
                    Tick?.Invoke();
                    PhaseFinished?.Invoke(Phase);
                    AdvancePhase();
                    // AdvancePhase stops the loop; return.
                    return;
                }
                Tick?.Invoke();
            }
        }
        catch (OperationCanceledException) { /* expected on pause/reset */ }
    }

    private void AdvancePhase()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;

        if (Phase == PomodoroPhase.Work)
        {
            CompletedWorkSessions++;
            var isLong = CompletedWorkSessions % LongBreakEvery == 0;
            SetPhase(isLong ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak);
        }
        else
        {
            SetPhase(PomodoroPhase.Work);
        }

        // Auto-start the next phase so cycles flow naturally.
        IsRunning = false;
        Start();
    }

    private void SetPhase(PomodoroPhase phase)
    {
        Phase = phase;
        Remaining = phase switch
        {
            PomodoroPhase.Work => WorkDuration,
            PomodoroPhase.ShortBreak => ShortBreakDuration,
            PomodoroPhase.LongBreak => LongBreakDuration,
            _ => TimeSpan.Zero
        };
        PhaseChanged?.Invoke(phase);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }
}
