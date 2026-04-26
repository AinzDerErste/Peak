using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Peak.App.ViewModels;

namespace Peak.App.Views.Widgets;

public partial class VolumeMixerWidget : UserControl
{
    public VolumeMixerWidget() => InitializeComponent();

    private IslandViewModel? Vm => DataContext as IslandViewModel;

    private void OnMuteClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string sessionId)
        {
            Vm?.ToggleMuteCommand.Execute(sessionId);
            e.Handled = true;
        }
    }

    private void OnVolumeBarClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ProgressBar bar && bar.Tag is string sessionId)
        {
            var pos = e.GetPosition(bar);
            var ratio = Math.Clamp(pos.X / bar.ActualWidth, 0, 1);
            Vm?.SetSessionVolumeCommand.Execute((sessionId, (float)ratio));
            e.Handled = true;
        }
    }

    private void OnVolumeBarScroll(object sender, MouseWheelEventArgs e)
    {
        if (sender is ProgressBar bar && bar.Tag is string sessionId)
        {
            var step = e.Delta > 0 ? 0.02f : -0.02f;
            var current = (float)bar.Value;
            var newVol = Math.Clamp(current + step, 0f, 1f);
            Vm?.SetSessionVolumeCommand.Execute((sessionId, newVol));
            e.Handled = true;
        }
    }

    private void OnListScroll(object sender, MouseWheelEventArgs e)
    {
        // Check if mouse is directly over a ProgressBar — adjust volume
        if (sender is ScrollViewer sv)
        {
            var pos = e.GetPosition(sv);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(sv, pos);
            if (hit?.VisualHit is DependencyObject hitObj)
            {
                var bar = FindParent<ProgressBar>(hitObj);
                if (bar != null)
                {
                    OnVolumeBarScroll(bar, e);
                    return;
                }
            }

            // Scroll the list
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T t) return t;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
