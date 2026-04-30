namespace Peak.Core.Theming;

/// <summary>
/// One Peak theme. Themes can be built-in (registered in code) or user-supplied
/// (a JSON file in <c>%AppData%\Peak\themes\</c>). The full list — built-ins
/// merged with user themes — is exposed by <see cref="ThemeService"/>.
///
/// Color values are <c>#AARRGGBB</c> hex strings (alpha first); they're parsed
/// by <see cref="System.Windows.Media.ColorConverter"/> in the host.
/// </summary>
public class Theme
{
    /// <summary>Stable identifier — used as the lookup key in settings.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name shown in the Settings UI. Falls back to <see cref="Id"/> when empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>Pill background colour (the dark surface behind everything).</summary>
    public string Background { get; set; } = "#FF000000";

    /// <summary>Accent colour (progress bars, active highlights, links).</summary>
    public string Accent { get; set; } = "#FF60CDFF";

    /// <summary>True for themes shipped with the app; false for user JSON files.</summary>
    public bool IsBuiltIn { get; set; }
}
