using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Peak.App.ViewModels;
using Peak.Core.Models;

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
}
