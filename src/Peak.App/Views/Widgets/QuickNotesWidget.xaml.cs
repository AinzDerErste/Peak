using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Peak.App.ViewModels;
using Peak.Core.Models;

namespace Peak.App.Views.Widgets;

public partial class QuickNotesWidget : UserControl
{
    public static readonly IValueConverter EmptyConverter = new CountToVisibilityConverter();

    public QuickNotesWidget()
    {
        InitializeComponent();
    }

    private void OnAddNoteClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not IslandViewModel vm) return;
        vm.CreateNoteCommand.Execute(null);
        ShowEditor();
    }

    private void OnNoteClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: NoteItem note }) return;
        if (DataContext is not IslandViewModel vm) return;
        vm.SelectNoteCommand.Execute(note);
        ShowEditor();
    }

    private void OnDeleteNoteClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NoteItem note }) return;
        if (DataContext is not IslandViewModel vm) return;
        vm.DeleteNoteCommand.Execute(note);
        e.Handled = true; // prevent OnNoteClick from firing
    }

    private void OnBackClick(object sender, MouseButtonEventArgs e)
    {
        ShowList();
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is IslandViewModel vm)
            vm.SaveNoteCommand.Execute(null);
    }

    private void ShowEditor()
    {
        ListView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        NoteEditor.Focus();
    }

    private void ShowList()
    {
        EditorView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
    }

    private void OnNoteHover(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    private void OnNoteLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
    }

    private class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
