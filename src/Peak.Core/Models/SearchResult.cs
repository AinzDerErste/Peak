namespace Peak.Core.Models;

/// <summary>
/// A single match from <see cref="Services.SearchService.Search"/>. The
/// <see cref="Score"/> is internal — items are returned pre-sorted descending.
/// </summary>
public record SearchResult(
    string Name,
    string Path,
    SearchResultType Type,
    int Score = 0);

/// <summary>
/// Source category of a <see cref="SearchResult"/>. Drives the icon used in the UI
/// and how the item is launched.
/// </summary>
public enum SearchResultType
{
    /// <summary>A Start Menu shortcut (.lnk) — launches the target executable.</summary>
    Application,

    /// <summary>A regular file path — opens with the default associated app.</summary>
    File,

    /// <summary>A URL — opens in the default browser.</summary>
    Url
}
