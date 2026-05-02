namespace Peak.Plugins.Companion;

/// <summary>
/// Persistent settings for the Companion plugin. Stored as a JSON blob in
/// <c>AppSettings.PluginSettings["peak.plugins.companion"]</c>.
///
/// Each <c>Enable*</c> flag toggles one mood-rule; with all of them off the
/// companion stays in <see cref="ManualMood"/> (or <c>idle</c> by default).
/// </summary>
public class CompanionSettings
{
    /// <summary>Master toggle — false stops all auto-mood logic.</summary>
    public bool AutoMoodEnabled { get; set; } = true;

    /// <summary>Forced mood; used when auto-mood is off (or as the fallback when no rule matches).</summary>
    public string ManualMood { get; set; } = "idle";

    /// <summary>Happy when music is playing (Spotify, YouTube, etc.).</summary>
    public bool EnableHappyOnMusic { get; set; } = true;

    /// <summary>Suspicious / concentrated-squint look while a Pomodoro work phase is running.</summary>
    public bool EnableSuspiciousOnPomodoro { get; set; } = true;

    /// <summary>Sleepy at night (22:00 – 06:00 local time).</summary>
    public bool EnableSleepyAtNight { get; set; } = true;

    /// <summary>Surprised briefly when a notification arrives (~3 s, then back to base mood).</summary>
    public bool EnableSurprisedOnNotification { get; set; } = true;

    /// <summary>Angry when CPU usage stays above 90 % for several seconds.</summary>
    public bool EnableAngryOnHighCpu { get; set; } = true;

    /// <summary>Love during an active Discord or TeamSpeak voice call.</summary>
    public bool EnableLoveOnVoiceCall { get; set; } = true;
}
