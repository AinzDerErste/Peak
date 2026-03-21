namespace Peak.Core.Plugins;

/// <summary>
/// Describes a registered widget (built-in or plugin).
/// </summary>
public record WidgetDescriptor(string Id, string DisplayName, bool IsBuiltIn)
{
    public override string ToString() => DisplayName;
}
