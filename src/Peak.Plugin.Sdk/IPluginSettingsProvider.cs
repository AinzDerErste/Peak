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

/// <summary>
/// Determines how a <see cref="PluginSettingField"/> is rendered in the Settings window.
/// </summary>
public enum PluginSettingFieldKind
{
    /// <summary>Plain text input.</summary>
    Text,
    /// <summary>Masked text input — content is obscured (e.g. for API keys).</summary>
    Password,
    /// <summary>Boolean toggle (CheckBox).</summary>
    Bool,
    /// <summary>Numeric input.</summary>
    Number,
    /// <summary>Action button — clicking invokes <see cref="IPluginSettingsProvider.SetSettingValue"/> with a null value.</summary>
    Button,
    /// <summary>Dropdown — renders as a ComboBox populated from <see cref="PluginSettingField.Options"/>.
    /// The <c>CurrentValue</c> must match one of the options' values.</summary>
    Choice,
    /// <summary>Path to a single file — renders as a TextBox + Browse button that opens an OpenFileDialog.
    /// Use <see cref="PluginSettingField.FileFilter"/> to constrain the picker (e.g. "Executable|*.exe").</summary>
    FilePath,
    /// <summary>Path to a directory — renders as a TextBox + Browse button that opens a folder picker.</summary>
    FolderPath
}

/// <summary>
/// Schema description for a single plugin setting field. Returned by
/// <see cref="IPluginSettingsProvider.GetSettingsSchema"/> and rendered into
/// the Peak Settings window.
/// </summary>
public class PluginSettingField
{
    /// <summary>Stable identifier for this field; passed back to <see cref="IPluginSettingsProvider.SetSettingValue"/>.</summary>
    public string Key { get; set; } = "";

    /// <summary>Human-readable label shown in the Settings window.</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional secondary description / hint text shown below the label.</summary>
    public string? Description { get; set; }

    /// <summary>Field kind — determines the UI control type.</summary>
    public PluginSettingFieldKind Kind { get; set; } = PluginSettingFieldKind.Text;

    /// <summary>Current value to pre-populate the control with (null for empty).</summary>
    public string? CurrentValue { get; set; }

    /// <summary>Optional placeholder text shown when the field is empty.</summary>
    public string? Placeholder { get; set; }

    /// <summary>
    /// Options for <see cref="PluginSettingFieldKind.Choice"/>. Each entry
    /// is a (value, label) pair where the value is what gets passed to
    /// <see cref="IPluginSettingsProvider.SetSettingValue"/> and the label
    /// is what the user sees in the dropdown. Ignored for other kinds.
    /// </summary>
    public IReadOnlyList<PluginSettingChoice>? Options { get; set; }

    /// <summary>
    /// File filter for <see cref="PluginSettingFieldKind.FilePath"/>. WPF
    /// OpenFileDialog format: <c>"Executable|*.exe"</c>. Pipe-separated
    /// pairs of label and pattern; multiple filters separated by another
    /// pipe. Ignored for other kinds.
    /// </summary>
    public string? FileFilter { get; set; }
}

/// <summary>
/// One entry in a <see cref="PluginSettingFieldKind.Choice"/> dropdown.
/// Kept as a tiny record-style class so plugins can construct lists in
/// place: <c>new[] { new PluginSettingChoice("mp3", "MP3 (audio)"), ... }</c>.
/// </summary>
public class PluginSettingChoice
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    public PluginSettingChoice() { }
    public PluginSettingChoice(string value, string label) { Value = value; Label = label; }
}
