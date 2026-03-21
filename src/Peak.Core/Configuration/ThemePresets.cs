namespace Peak.Core.Configuration;

public static class ThemePresets
{
    private static readonly Dictionary<string, (string Background, string Accent)> Presets = new()
    {
        ["default"]  = ("#FF000000", "#FF60CDFF"),
        ["midnight"] = ("#FF0A0E1A", "#FF6C5CE7"),
        ["forest"]   = ("#FF0A1A0A", "#FF00B894"),
        ["sunset"]   = ("#FF1A0A0A", "#FFFD7955"),
        ["ocean"]    = ("#FF0A1520", "#FF0984E3"),
        ["rose"]     = ("#FF1A0A14", "#FFE84393"),
        ["snow"]     = ("#FF1A1A1A", "#FFDFE6E9"),
    };

    public static (string Background, string Accent)? GetPreset(string name)
    {
        return Presets.TryGetValue(name, out var preset) ? preset : null;
    }

    public static IReadOnlyList<string> GetAllNames() => Presets.Keys.ToList().AsReadOnly();

    public static IReadOnlyDictionary<string, (string Background, string Accent)> GetAll() => Presets;
}
