using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Peak.Core.Configuration;
using Peak.Core.Services;

namespace Peak.App.Views.Widgets;

public partial class NetworkWidget : UserControl
{
    private const int BarCount = 40;                 // bar-mode: number of bars
    private const double BarGap = 1.0;               // bar-mode: gap between bars
    private const int LinePointCount = 60;           // line-mode: downsampled points
    private NetworkMonitorService? _networkService;
    private SettingsManager? _settingsManager;
    private readonly Brush _ulBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    public NetworkWidget()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        GraphCanvas.SizeChanged += (_, _) => RedrawGraph();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _networkService = app.Services.GetService(typeof(NetworkMonitorService)) as NetworkMonitorService;
            _settingsManager = app.Services.GetService(typeof(SettingsManager)) as SettingsManager;
        }

        RedrawGraph();
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
        if (e.PropertyName == "DownloadSpeed" || e.PropertyName == "UploadSpeed")
            Dispatcher.Invoke(RedrawGraph);
    }

    public void RedrawGraph()
    {
        if (_networkService == null) return;

        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        GraphCanvas.Children.Clear();

        var style = _settingsManager?.Settings.NetworkGraphStyle ?? NetworkGraphStyle.Line;
        var accent = (Brush?)Application.Current.Resources["AccentBrush"] ?? Brushes.DeepSkyBlue;

        if (style == NetworkGraphStyle.Bars)
            DrawBars(w, h, accent);
        else
            DrawLines(w, h, accent);
    }

    private void DrawBars(double w, double h, Brush accent)
    {
        var dlData = TakeLast(_networkService!.GetDownloadHistory(), BarCount);
        var ulData = TakeLast(_networkService!.GetUploadHistory(), BarCount);

        double dlMax = GetMax(dlData);
        double ulMax = GetMax(ulData);

        var barW = (w - BarGap * (BarCount - 1)) / BarCount;
        if (barW < 1) barW = 1;

        var startIndex = BarCount - dlData.Length;

        for (int i = 0; i < BarCount; i++)
        {
            var x = i * (barW + BarGap);
            double dlVal = 0, ulVal = 0;
            var dataIdx = i - startIndex;
            if (dataIdx >= 0 && dataIdx < dlData.Length)
            {
                dlVal = dlData[dataIdx];
                ulVal = ulData[dataIdx];
            }

            var dlHeight = dlMax > 0 ? (dlVal / dlMax) * h : 0;
            var ulHeight = ulMax > 0 ? (ulVal / ulMax) * (h * 0.45) : 0;
            if (dlHeight < 0) dlHeight = 0;
            if (ulHeight < 0) ulHeight = 0;

            if (dlHeight > 0.5)
            {
                var dlBar = new Rectangle
                {
                    Width = barW,
                    Height = dlHeight,
                    Fill = accent,
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                Canvas.SetLeft(dlBar, x);
                Canvas.SetTop(dlBar, h - dlHeight);
                GraphCanvas.Children.Add(dlBar);
            }

            if (ulHeight > 0.5)
            {
                var ulBar = new Rectangle
                {
                    Width = Math.Max(1, barW * 0.55),
                    Height = ulHeight,
                    Fill = _ulBrush,
                    RadiusX = 0.5,
                    RadiusY = 0.5
                };
                Canvas.SetLeft(ulBar, x + (barW - barW * 0.55) / 2);
                Canvas.SetTop(ulBar, h - ulHeight);
                GraphCanvas.Children.Add(ulBar);
            }
        }
    }

    private void DrawLines(double w, double h, Brush accent)
    {
        var dlData = Downsample(_networkService!.GetDownloadHistory(), LinePointCount);
        var ulData = Downsample(_networkService!.GetUploadHistory(), LinePointCount);
        if (dlData.Length < 2) return;

        double dlMax = GetMax(dlData);
        double ulMax = GetMax(ulData);

        // Download: filled polygon + top line (accent)
        var dlFill = accent.Clone();
        if (dlFill.CanFreeze)
        {
            if (dlFill is SolidColorBrush scb)
                dlFill = new SolidColorBrush(Color.FromArgb(0x55, scb.Color.R, scb.Color.G, scb.Color.B));
            dlFill.Freeze();
        }

        var dlPolygon = new Polygon { Fill = dlFill, IsHitTestVisible = false };
        var dlLine = new Polyline
        {
            Stroke = accent,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };

        BuildLinePoints(dlData, dlMax, w, h, dlLine.Points, dlPolygon.Points, fillBaseline: true);

        // Upload: thinner semi-white line on top, no fill, uses bottom 45% of canvas
        var ulLine = new Polyline
        {
            Stroke = _ulBrush,
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        BuildLinePoints(ulData, ulMax, w, h * 0.45, ulLine.Points, null, fillBaseline: false, yOffset: h * 0.55);

        GraphCanvas.Children.Add(dlPolygon);
        GraphCanvas.Children.Add(dlLine);
        GraphCanvas.Children.Add(ulLine);
    }

    private static void BuildLinePoints(double[] data, double max, double w, double drawH,
        PointCollection linePts, PointCollection? polyPts, bool fillBaseline, double yOffset = 0)
    {
        var n = data.Length;
        if (n < 2 || max <= 0) return;

        var step = w / (n - 1);
        for (int i = 0; i < n; i++)
        {
            var x = i * step;
            var y = yOffset + drawH - (data[i] / max * drawH);
            if (y < yOffset) y = yOffset;
            if (y > yOffset + drawH) y = yOffset + drawH;
            linePts.Add(new Point(x, y));
        }

        if (polyPts != null && fillBaseline)
        {
            foreach (var p in linePts) polyPts.Add(p);
            polyPts.Add(new Point(w, yOffset + drawH));
            polyPts.Add(new Point(0, yOffset + drawH));
        }
    }

    private static double[] Downsample(double[] src, int targetCount)
    {
        if (src.Length <= targetCount) return src;
        var result = new double[targetCount];
        double bucketSize = (double)src.Length / targetCount;
        for (int i = 0; i < targetCount; i++)
        {
            int start = (int)(i * bucketSize);
            int end = (int)((i + 1) * bucketSize);
            if (end > src.Length) end = src.Length;
            double sum = 0;
            int cnt = 0;
            for (int j = start; j < end; j++) { sum += src[j]; cnt++; }
            result[i] = cnt > 0 ? sum / cnt : 0;
        }
        return result;
    }

    private static double[] TakeLast(double[] src, int n)
    {
        if (src.Length <= n) return src;
        var result = new double[n];
        Array.Copy(src, src.Length - n, result, 0, n);
        return result;
    }

    private static double GetMax(double[] data)
    {
        double max = 10 * 1024; // 10 KB/s floor so idle graph stays near baseline
        foreach (var v in data)
            if (v > max) max = v;
        return max * 1.15;
    }
}
