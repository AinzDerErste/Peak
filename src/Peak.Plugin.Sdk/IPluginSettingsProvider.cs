namespace Peak.Plugin.Sdk;

/// <summary>
/// Optional interface plugins implement to expose editable settings
/// fields in Peak's Settings window. Peak will auto-generate UI for
/// the returned schema.
/// </summary>
public interface IPluginSettingsProvider
{
    /// <summary>
    /// Returns the list of editable fields (in display order) with their
    /// current values.
    /// </summary>
    IReadOnlyList<PluginSettingField> GetSettingsSchema();

    /// <summary>
    /// Called when the user saves the Settings window. The plugin should
    /// apply <paramref name="value"/> to its internal state; Peak will
    /// afterwards call <c>SaveSettings()</c> to persist.
    /// </summary>
    void SetSettingValue(string key, string? value);
}

public enum PluginSettingFieldKind
{
    Text,
    Password,
    Bool,
    Number,
    Button
}

public class PluginSettingField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public PluginSettingFieldKind Kind { get; set; } = PluginSettingFieldKind.Text;
    public string? CurrentValue { get; set; }
    public string? Placeholder { get; set; }
}
