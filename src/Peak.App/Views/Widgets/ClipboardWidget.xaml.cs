using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Peak.App.ViewModels;
using Peak.Core.Models;

namespace Peak.App.Views.Widgets;

public partial class ClipboardWidget : UserControl
{
    public static readonly IValueConverter TypeIconConverter = new ContentTypeToIconConverter();
    public static readonly IValueConverter EmptyConverter = new CountToVisibilityConverter();

    public ClipboardWidget()
    {
        InitializeComponent();
    }

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ClipboardEntry entry }) return;
        if (DataContext is not IslandViewModel vm) return;

        if (e.ChangedButton == MouseButton.Right)
            vm.RemoveClipboardEntryCommand.Execute(entry);
        else
            vm.CopyClipboardEntryCommand.Execute(entry);
    }

    private void OnEntryHover(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    private void OnEntryLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
    }

    private void OnClearAllClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is IslandViewModel vm)
            vm.ClearClipboardHistoryCommand.Execute(null);
    }

    private class ContentTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value?.ToString() switch
            {
                "image" => "\U0001F5BC",  // framed picture
                "file" => "\U0001F4C1",   // folder
                _ => "\U0001F4CB"          // clipboard
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    private class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
