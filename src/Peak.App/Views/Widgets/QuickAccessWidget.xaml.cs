using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Peak.App.ViewModels;
using Peak.Core.Models;

namespace Peak.App.Views.Widgets;

public partial class QuickAccessWidget : UserControl
{
    public static readonly IValueConverter IconConverter = new FileIconConverter();

    public QuickAccessWidget()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        if (DataContext is IslandViewModel vm)
        {
            EmptyHint.Visibility = vm.QuickAccessItems.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            vm.QuickAccessItems.CollectionChanged += (_, _) =>
                EmptyHint.Visibility = vm.QuickAccessItems.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QuickAccessItem item
            && DataContext is IslandViewModel vm)
        {
            if (e.ChangedButton == MouseButton.Right)
                vm.RemoveQuickAccessCommand.Execute(item);
            else
                vm.OpenQuickAccessCommand.Execute(item);
        }
    }

    private void OnItemHover(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    }

    private void OnItemLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = Brushes.Transparent;
    }

    private void OnAddClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is IslandViewModel vm)
            vm.AddQuickAccessCommand.Execute(null);
    }

    public void SetDropHighlight(bool highlight)
    {
        DropHighlight.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
        ContentGrid.Visibility = highlight ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (DataContext is not IslandViewModel vm) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var path in files)
        {
            if (vm.QuickAccessItems.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var isFolder = Directory.Exists(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;

            vm.QuickAccessItems.Add(new QuickAccessItem
            {
                Path = path,
                Name = name,
                IsFolder = isFolder
            });
        }

        vm.SaveQuickAccess();
    }

    private class FileIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string name) return "📄";
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".xls" or ".xlsx" => "📗",
                ".ppt" or ".pptx" => "📙",
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".svg" => "🖼",
                ".mp3" or ".wav" or ".flac" or ".aac" => "🎵",
                ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
                ".zip" or ".rar" or ".7z" or ".tar" => "📦",
                ".exe" or ".msi" => "⚙",
                ".txt" or ".md" => "📝",
                ".cs" or ".js" or ".py" or ".ts" or ".html" or ".css" => "💻",
                "" => "📁", // Folder (no extension)
                _ => "📄"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
