using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Peak.Core.Services;

namespace Peak.App.Views.Widgets;

public partial class NetworkWidget : UserControl
{
    private readonly Polyline _dlLine;
    private readonly Polyline _ulLine;
    private NetworkMonitorService? _networkService;

    public NetworkWidget()
    {
        InitializeComponent();

        _dlLine = new Polyline
        {
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _ulLine = new Polyline
        {
            StrokeThickness = 1.0,
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };

        GraphCanvas.Children.Add(_ulLine);
        GraphCanvas.Children.Add(_dlLine); // download on top

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bind dl line color to AccentBrush
        _dlLine.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

        // Resolve the service from the app's DI
        if (Application.Current is App app)
        {
            _networkService = app.Services.GetService(typeof(NetworkMonitorService)) as NetworkMonitorService;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Redraw graph whenever speed updates
        if (e.PropertyName == "DownloadSpeed" || e.PropertyName == "UploadSpeed")
            Dispatcher.Invoke(RedrawGraph);
    }

    private void RedrawGraph()
    {
        if (_networkService == null) return;

        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var dlData = _networkService.GetDownloadHistory();
        var ulData = _networkService.GetUploadHistory();

        // Find shared max for scale (so both lines use same Y axis)
        double max = 1024; // minimum 1 KB/s scale
        foreach (var v in dlData) if (v > max) max = v;
        foreach (var v in ulData) if (v > max) max = v;
        max *= 1.15; // 15% headroom

        DrawLine(_dlLine, dlData, w, h, max);
        DrawLine(_ulLine, ulData, w, h, max);
    }

    private static void DrawLine(Polyline line, double[] data, double canvasW, double canvasH, double maxValue)
    {
        line.Points.Clear();
        if (data.Length < 2) return;

        var count = data.Length;
        var step = canvasW / (NetworkMonitorService.HistorySize - 1);

        // Offset so recent data is on the right edge
        var startX = canvasW - (count - 1) * step;

        for (int i = 0; i < count; i++)
        {
            var x = startX + i * step;
            var y = canvasH - (data[i] / maxValue * canvasH);
            if (y < 0) y = 0;
            line.Points.Add(new Point(x, y));
        }
    }
}
