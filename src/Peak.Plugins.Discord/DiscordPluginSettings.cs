namespace Peak.Plugins.Discord;

/// <summary>
/// Persistent settings for the Discord plugin. Stored as a JSON blob in
/// <c>AppSettings.PluginSettings["peak.plugins.discord"]</c>.
/// </summary>
public class DiscordPluginSettings
{
    /// <summary>OAuth2 Client ID from the Discord Developer Portal.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth2 Client Secret — required to exchange the auth code for an access token.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Cached OAuth2 access token. Refreshed automatically before expiry.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Approximate UTC time when <see cref="AccessToken"/> stops being valid.</summary>
    public DateTime? TokenExpiresAt { get; set; }
}
