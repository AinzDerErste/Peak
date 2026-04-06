namespace Peak.Plugins.Discord;

public class DiscordPluginSettings
{
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
}
