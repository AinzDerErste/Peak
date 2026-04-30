using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Peak.App.ViewModels;

namespace Peak.App.Views.Widgets;

public partial class MediaWidget : UserControl
{
    /// <summary>
    /// How long to interpolate between two MediaProgress samples. Slightly
    /// less than the MediaService poll interval (~1 s) so the bar finishes
    /// its glide before the next sample arrives — avoids the "hop" you'd
    /// see if animations queued faster than they completed.
    /// </summary>
    private static readonly Duration ProgressGlide = new(TimeSpan.FromMilliseconds(900));

    private static readonly IEasingFunction LinearEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private INotifyPropertyChanged? _vm;

    public MediaWidget()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachVm();
        // Re-snap the fill width when the track itself resizes (window /
        // slot resizes, theme change, etc.) so the visual position stays
        // accurate without waiting for the next MediaProgress tick.
        ProgressTrack.SizeChanged += (_, _) => SnapProgress();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is INotifyPropertyChanged inpc)
        {
            _vm = inpc;
            _vm.PropertyChanged += OnVmPropertyChanged;
            // Snap to current value on attach — no entry animation from 0.
            SnapProgress();
        }
    }

    private void DetachVm()
    {
        if (_vm == null) return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IslandViewModel.MediaProgress)) return;
        AnimateProgress();
    }

    /// <summary>
    /// Sets ProgressFill.Width directly to (track × MediaProgress) without
    /// animation. Used on first attach and on track resize so the bar lines
    /// up immediately.
    /// </summary>
    private void SnapProgress()
    {
        var track = ProgressTrack.ActualWidth;
        if (track <= 0 || _vm is not IslandViewModel ivm) return;
        ProgressFill.BeginAnimation(WidthProperty, null); // cancel any pending glide
        ProgressFill.Width = Math.Max(0, Math.Min(1, ivm.MediaProgress)) * track;
    }

    /// <summary>
    /// Animates ProgressFill.Width from its current value to the value that
    /// matches the latest MediaProgress sample. Cubic ease-out lands smoothly
    /// just before the next sample arrives.
    /// </summary>
    private void AnimateProgress()
    {
        var track = ProgressTrack.ActualWidth;
        if (track <= 0 || _vm is not IslandViewModel ivm) return;

        var anim = new DoubleAnimation
        {
            To = Math.Max(0, Math.Min(1, ivm.MediaProgress)) * track,
            Duration = ProgressGlide,
            EasingFunction = LinearEase,
            FillBehavior = FillBehavior.HoldEnd
        };
        ProgressFill.BeginAnimation(WidthProperty, anim);
    }
}
