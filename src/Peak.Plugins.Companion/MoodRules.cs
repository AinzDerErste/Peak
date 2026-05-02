namespace Peak.Plugins.Companion;

/// <summary>
/// One declarative mood rule loaded from <c>moods.json</c>. Rules are evaluated
/// in priority order — the first whose <see cref="When"/> expression resolves
/// truthy wins, and its <see cref="Mood"/> is sent to the HTML.
///
/// Optional timing attributes:
/// <list type="bullet">
///   <item><b>Sustained</b> — the condition must hold for this many seconds
///         before the rule activates (e.g. high-CPU debounce).</item>
///   <item><b>Duration</b> — once the condition matches, the rule stays active
///         for at least this many seconds even if the condition flips back
///         (e.g. surprise on a notification edge).</item>
/// </list>
/// Sustained and Duration are mutually exclusive — set at most one.
/// </summary>
public class MoodRule
{
    /// <summary>Mood name passed to <c>window.companion.setMood(...)</c>.</summary>
    public string Mood { get; set; } = "idle";

    /// <summary>
    /// Condition expression. Identifiers resolve against the host's
    /// IslandViewModel (e.g. <c>HasMedia</c>, <c>CpuUsage</c>) plus a few
    /// synthetic variables: <c>Hour</c> (0–23), <c>Minute</c> (0–59).
    /// Supports <c>&amp;&amp; || ! == != &gt; &lt; &gt;= &lt;=</c>, parentheses,
    /// numeric/bool/string literals.
    /// </summary>
    public string When { get; set; } = "true";

    /// <summary>Higher priority rules win. Ties resolved by file order.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Condition must hold this many seconds before the rule activates
    /// (debounce). Useful for noisy signals like CPU usage.
    /// </summary>
    public double? Sustained { get; set; }

    /// <summary>
    /// Once the condition matches, the rule stays active this many seconds
    /// even if the condition flips back. Useful for edge events like a fresh
    /// notification.
    /// </summary>
    public double? Duration { get; set; }
}

/// <summary>
/// Top-level shape of <c>moods.json</c>. Lives at
/// <c>%AppData%\Peak\plugins\companion\moods.json</c>; overwriting it (or
/// deleting it to regenerate) is the supported way to customise moods
/// without recompiling the plugin.
/// </summary>
public class MoodRulesConfig
{
    public List<MoodRule> Moods { get; set; } = new();

    /// <summary>Mood when no rule matches (and auto-mood is on).</summary>
    public string Fallback { get; set; } = "idle";

    /// <summary>The factory defaults written on first run.</summary>
    public static MoodRulesConfig Default() => new()
    {
        Fallback = "idle",
        Moods = new List<MoodRule>
        {
            new() { Mood = "surprised",  When = "HasNotification",                                 Duration = 3,  Priority = 100 },
            new() { Mood = "angry",      When = "CpuUsage > 90",                                   Sustained = 3, Priority = 90  },
            new() { Mood = "love",       When = "DiscordCallCount > 0 || TeamSpeakCallCount > 0",                Priority = 70  },
            new() { Mood = "suspicious", When = "IsPomodoroRunning",                                             Priority = 60  },
            new() { Mood = "happy",      When = "HasMedia && IsPlaying",                                         Priority = 50  },
            new() { Mood = "sleepy",     When = "Hour >= 22 || Hour < 6",                                        Priority = 30  }
        }
    };
}
