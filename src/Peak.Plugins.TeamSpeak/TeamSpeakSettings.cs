namespace Peak.Plugins.TeamSpeak;

/// <summary>
/// Persistent settings for the TeamSpeak plugin. Stored as a JSON blob in
/// <c>AppSettings.PluginSettings["peak.plugins.teamspeak"]</c>.
/// </summary>
public class TeamSpeakSettings
{
    /// <summary>API key issued by the TeamSpeak Client Query plugin. Empty until the user authorises Peak.</summary>
    public string? ApiKey { get; set; }

    /// <summary>WebSocket port the TeamSpeak Client exposes the Client Query API on. Default 5899.</summary>
    public int Port { get; set; } = 5899;
}
