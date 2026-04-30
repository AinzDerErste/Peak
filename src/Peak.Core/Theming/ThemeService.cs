using System.Text.Json;
using Microsoft.Extensions.Logging;
using Peak.Core.Configuration;

namespace Peak.Core.Theming;

/// <summary>
/// Single source of truth for the theme list. Combines:
///   1. Built-in presets registered in <see cref="ThemePresets"/> (compile-time list).
///   2. User-supplied JSON files in <c>%AppData%\Peak\themes\*.json</c>.
///
/// Settings UI binds against <see cref="GetAll"/>; theme switching looks up via
/// <see cref="GetById"/>. Call <see cref="Refresh"/> after the user drops a new
/// JSON into the themes folder (or just restart Peak).
/// </summary>
public class ThemeService
{
    private readonly ILogger<ThemeService>? _logger;
    private readonly object _lock = new();
    private readonly List<Theme> _themes = new();

    /// <summary>Absolute path to the user-themes folder (auto-created on first refresh).</summary>
    public string ThemesDirectory { get; }

    /// <summary>Fires after a Refresh completes. UI can subscribe to update its theme list.</summary>
    public event Action? ThemesUpdated;

    public ThemeService(ILogger<ThemeService>? logger = null)
    {
        _logger = logger;
        ThemesDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Peak", "themes");
        Refresh();
    }

    /// <summary>Returns the full theme list — built-ins first, user themes alphabetised.</summary>
    public IReadOnlyList<Theme> GetAll()
    {
        lock (_lock) return _themes.ToList();
    }

    /// <summary>Lookup a single theme by <see cref="Theme.Id"/>; returns null if missing.</summary>
    public Theme? GetById(string id)
    {
        lock (_lock) return _themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Re-scans the user themes folder and rebuilds the merged list. Safe to call
    /// repeatedly; built-ins always come first, user themes are deduped against
    /// built-in IDs (built-in wins).
    /// </summary>
    public void Refresh()
    {
        try { Directory.CreateDirectory(ThemesDirectory); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Could not create themes directory at {Path}", ThemesDirectory); }

        var fresh = new List<Theme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Built-ins from the compiled presets table (default, midnight, ocean, …).
        foreach (var (id, (bg, accent)) in ThemePresets.GetAll())
        {
            fresh.Add(new Theme
            {
                Id = id,
                Name = char.ToUpper(id[0]) + id[1..],
                Background = bg,
                Accent = accent,
                IsBuiltIn = true
            });
            seen.Add(id);
        }

        // User-supplied JSON files. One theme per file. Files whose Id collides
        // with a built-in are skipped — built-ins are sacred so the dropdown
        // always has the defaults available.
        if (Directory.Exists(ThemesDirectory))
        {
            foreach (var path in EnumerateUserThemeFiles())
            {
                var theme = TryLoadThemeFile(path);
                if (theme == null) continue;
                if (string.IsNullOrWhiteSpace(theme.Id)) theme.Id = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(theme.Name)) theme.Name = theme.Id;
                if (!seen.Add(theme.Id))
                {
                    _logger?.LogInformation("Skipping user theme '{File}' — Id '{Id}' is reserved by a built-in.", path, theme.Id);
                    continue;
                }
                theme.IsBuiltIn = false;
                fresh.Add(theme);
            }
        }

        // User themes sorted alphabetically (after built-ins which keep insert order).
        var builtInCount = fresh.Count(t => t.IsBuiltIn);
        fresh.Sort((a, b) =>
        {
            if (a.IsBuiltIn != b.IsBuiltIn) return a.IsBuiltIn ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        lock (_lock)
        {
            _themes.Clear();
            _themes.AddRange(fresh);
        }
        ThemesUpdated?.Invoke();
    }

    private IEnumerable<string> EnumerateUserThemeFiles()
    {
        try
        {
            return Directory.EnumerateFiles(ThemesDirectory, "*.json",
                new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "User themes folder enumeration failed");
            return Array.Empty<string>();
        }
    }

    private Theme? TryLoadThemeFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Theme>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Skipping malformed theme file {Path}", path);
            return null;
        }
    }
}
