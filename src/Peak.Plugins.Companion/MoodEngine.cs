using System.ComponentModel;
using System.Reflection;
using System.Windows.Threading;

namespace Peak.Plugins.Companion;

/// <summary>
/// Decides which Companion mood ("idle", "happy", "focused", …) should be
/// active right now and pushes the result through <see cref="MoodChanged"/>.
///
/// Driven entirely by the rule set in <see cref="MoodRulesConfig"/> (loaded
/// from <c>moods.json</c>) — this class no longer has any hard-coded mood
/// logic, so users can rewire reactions without editing C# code.
///
/// Subscribes to the host's <c>IslandViewModel</c> via reflection — plugins
/// can't reference the concrete VM type, but we can read its public properties
/// (HasMedia, IsPomodoroRunning, CpuUsage, …) and listen on
/// <see cref="INotifyPropertyChanged"/>. A 1-second poll timer also handles
/// time-of-day rules and Sustained/Duration debouncing.
/// </summary>
internal class MoodEngine : IDisposable, IMoodContext
{
    private readonly object _viewModel;
    private readonly Type _vmType;
    private readonly DispatcherTimer _tick;

    private CompanionSettings _settings;
    private MoodRulesConfig _rules;
    private List<CompiledRule> _compiled = new();
    private string _currentMood = "";

    private record CompiledRule(MoodRule Source, MoodExpression? Expr, string? CompileError)
    {
        public DateTime SustainedSince { get; set; } = DateTime.MinValue;
        public DateTime DurationUntil  { get; set; } = DateTime.MinValue;
    }

    /// <summary>Fired (on the UI thread) whenever the resolved mood changes.</summary>
    public event Action<string>? MoodChanged;

    /// <summary>Fired whenever the engine logs a notable event — used by the
    /// plugin to mirror to <c>plugin.log</c>. Compile errors and timer faults
    /// surface here rather than being swallowed.</summary>
    public event Action<string>? Diagnostic;

    public MoodEngine(object viewModel, CompanionSettings settings, MoodRulesConfig rules)
    {
        _viewModel = viewModel;
        _vmType = viewModel.GetType();
        _settings = settings;
        _rules = rules;
        Compile();

        if (viewModel is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnVmPropertyChanged;

        // 1 s poll handles rules whose triggers don't fire VM events:
        // wall-clock time (Hour-based rules), Sustained debouncing,
        // Duration latch decay.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Reevaluate();
        _tick.Start();
    }

    /// <summary>Push a new settings object — re-evaluates immediately.</summary>
    public void UpdateSettings(CompanionSettings settings)
    {
        _settings = settings;
        Reevaluate();
    }

    /// <summary>Replace the rule set (e.g. after moods.json is edited).</summary>
    public void UpdateRules(MoodRulesConfig rules)
    {
        _rules = rules;
        Compile();
        Reevaluate();
    }

    /// <summary>Force an immediate re-evaluation. Useful when the view first attaches.</summary>
    public void Reevaluate()
    {
        var mood = ComputeMood();
        if (mood != _currentMood)
        {
            _currentMood = mood;
            MoodChanged?.Invoke(mood);
        }
    }

    private void Compile()
    {
        _compiled = _rules.Moods
            .OrderByDescending(r => r.Priority)
            .Select(r =>
            {
                try { return new CompiledRule(r, new MoodExpression(r.When), null); }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke($"Mood rule '{r.Mood}': bad expression \"{r.When}\" — {ex.Message}");
                    return new CompiledRule(r, null, ex.Message);
                }
            })
            .ToList();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Reevaluate();
    }

    /// <summary>
    /// Walks the compiled rules in priority order, evaluating Sustained/
    /// Duration windows, and returns the first matching mood. With auto-mood
    /// off, returns <see cref="CompanionSettings.ManualMood"/> directly.
    /// </summary>
    private string ComputeMood()
    {
        if (!_settings.AutoMoodEnabled)
            return string.IsNullOrWhiteSpace(_settings.ManualMood) ? "idle" : _settings.ManualMood;

        var now = DateTime.UtcNow;

        foreach (var rule in _compiled)
        {
            if (rule.Expr == null) continue; // skip invalid rules silently

            bool match;
            try { match = rule.Expr.Evaluate(this); }
            catch { match = false; }

            // Duration: latch once condition matches; rule stays active until
            // DurationUntil even if the condition flips false.
            if (rule.Source.Duration is double dur)
            {
                if (match) rule.DurationUntil = now.AddSeconds(dur);
                if (now < rule.DurationUntil) return rule.Source.Mood;
                continue; // duration rules don't fall through to "match" gate below
            }

            // Sustained: condition must hold continuously for N seconds first.
            if (rule.Source.Sustained is double sus)
            {
                if (match)
                {
                    if (rule.SustainedSince == DateTime.MinValue) rule.SustainedSince = now;
                    if ((now - rule.SustainedSince).TotalSeconds >= sus) return rule.Source.Mood;
                }
                else
                {
                    rule.SustainedSince = DateTime.MinValue;
                }
                continue;
            }

            // Plain rule — match → activate immediately.
            if (match) return rule.Source.Mood;
        }

        // No rule matched — fall back to configured default.
        var fallback = _rules.Fallback;
        if (string.IsNullOrWhiteSpace(fallback)) fallback = _settings.ManualMood;
        return string.IsNullOrWhiteSpace(fallback) ? "idle" : fallback;
    }

    // ─── IMoodContext ─────────────────────────────────────────────

    /// <summary>
    /// Resolves an identifier in a rule expression. Synthetic variables come
    /// first (Hour, Minute, …); everything else falls through to public
    /// properties on the host's IslandViewModel via reflection.
    /// </summary>
    public object? Get(string name)
    {
        switch (name)
        {
            case "Hour":   return (double)DateTime.Now.Hour;
            case "Minute": return (double)DateTime.Now.Minute;
            case "Second": return (double)DateTime.Now.Second;
        }

        var prop = _vmType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return null;
        var v = prop.GetValue(_viewModel);
        // Normalise numerics so comparisons against numeric literals work.
        if (v is float f) return (double)f;
        if (v is int i)   return (double)i;
        if (v is long l)  return (double)l;
        if (v is short s) return (double)s;
        if (v is byte b)  return (double)b;
        return v;
    }

    public void Dispose()
    {
        _tick.Stop();
        if (_viewModel is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= OnVmPropertyChanged;
    }
}
