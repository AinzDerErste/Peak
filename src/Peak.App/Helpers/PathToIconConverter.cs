using System.Globalization;
using System.Windows.Data;

namespace Peak.App.Helpers;

/// <summary>
/// Binds a parsing path (file path, .lnk, or shell:appsFolder URI) to its
/// associated app icon for use in WPF Image controls. Lookup goes through
/// <see cref="IconExtractor"/>, which caches results so repeated queries
/// for the same app are free.
///
/// Use <c>ConverterParameter</c> to override the default 32px size:
/// <c>{Binding Path, Converter={StaticResource PathToIconConverter}, ConverterParameter=24}</c>.
/// </summary>
public class PathToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return null;

        int size = 32;
        if (parameter is string sizeStr && int.TryParse(sizeStr, out var parsed))
            size = parsed;
        else if (parameter is int sizeInt)
            size = sizeInt;

        return IconExtractor.GetIcon(path, size);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
