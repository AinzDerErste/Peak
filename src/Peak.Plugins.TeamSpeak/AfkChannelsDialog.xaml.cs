using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Peak.Plugins.TeamSpeak;

/// <summary>
/// Modal picker that lets the user flag up to <see cref="MaxAfk"/> channels of
/// the currently-connected TeamSpeak server as AFK. The TS plugin opens this
/// from <c>SetSettingValue("ManageAfkChannels", …)</c> when the user clicks
/// the matching button in Peak's plugin-settings panel.
///
/// Built fully in code (no XAML) on purpose — WPF's InitializeComponent uses
/// pack-URIs to load BAML, and those don't always resolve when the assembly
/// is loaded into a plugin AssemblyLoadContext separate from the host.
/// </summary>
public class AfkChannelsDialog : Window
{
    /// <summary>Hard cap on how many channels can be marked AFK per server.</summary>
    private const int MaxAfk = 3;

    private readonly ObservableCollection<ChannelRow> _rows = new();
    private readonly TextBlock _countText;

    /// <summary>Channel IDs the user picked. Set on Save, valid only when DialogResult==true.</summary>
    public IReadOnlyList<string> SelectedChannelIds { get; private set; } = new List<string>();

    public AfkChannelsDialog(string serverName,
                              IReadOnlyList<KeyValuePair<string, string>> channels,
                              IReadOnlyList<string> currentlySelected)
    {
        // ── Window chrome ────────────────────────────────────────────
        Title = "AFK Channels";
        Width = 460;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        // Click-anywhere-and-drag to move the borderless window.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // ── Outer rounded card ───────────────────────────────────────
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Title row: pin icon + server name ────────────────────────
        var titlePanel = new DockPanel { LastChildFill = true };
        titlePanel.Children.Add(BuildPinIcon());
        var titleText = new TextBlock
        {
            Text = $"AFK Channels — {serverName}",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titlePanel.Children.Add(titleText);
        Grid.SetRow(titlePanel, 0);
        grid.Children.Add(titlePanel);

        // ── Subtitle ─────────────────────────────────────────────────
        var subtitle = new TextBlock
        {
            Text = $"Pick up to {MaxAfk} channels. While you're sitting in one, the call counter and visualizer stay quiet.",
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(subtitle, 1);
        grid.Children.Add(subtitle);

        // ── Channel list ─────────────────────────────────────────────
        var alreadySelected = new HashSet<string>(currentlySelected, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in channels.Select(c => (c.Key, c.Value)))
            _rows.Add(new ChannelRow(id, name) { IsSelected = alreadySelected.Contains(id) });

        var listBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        var listBox = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            ItemsSource = _rows,
            ItemTemplate = BuildRowTemplate()
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);
        listBorder.Child = listBox;
        Grid.SetRow(listBorder, 2);
        grid.Children.Add(listBorder);

        // ── "X of 3 selected" counter ─────────────────────────────────
        _countText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(_countText, 3);
        grid.Children.Add(_countText);

        // ── Footer (Cancel / Save) ───────────────────────────────────
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelBtn = new Button
        {
            Content = "Cancel", Width = 90, Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        footer.Children.Add(cancelBtn);

        var saveBtn = new Button
        {
            Content = "Save", Width = 90, Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)),
            Foreground = Brushes.Black, BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold
        };
        saveBtn.Click += (_, _) =>
        {
            SelectedChannelIds = _rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
            DialogResult = true;
            Close();
        };
        footer.Children.Add(saveBtn);

        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        card.Child = grid;
        Content = card;

        UpdateRowEnabledState();
        UpdateCountText();
    }

    /// <summary>Small location-pin SVG glyph rendered next to the title.</summary>
    private static FrameworkElement BuildPinIcon()
    {
        // FontAwesome map-pin (location), viewBox 0 0 384 512.
        const string PinPath =
            "M215.7 499.2C267 435 384 279.4 384 192C384 86 298 0 192 0S0 86 0 192" +
            "c0 87.4 117 243 168.3 307.2c12.3 15.3 35.1 15.3 47.4 0z" +
            "M192 128a64 64 0 1 1 0 128 64 64 0 1 1 0-128z";

        var canvas = new Canvas { Width = 384, Height = 512 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(PinPath),
            Fill = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF))
        });
        return new Viewbox
        {
            Child = canvas,
            Width = 18, Height = 18,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
    }

    /// <summary>
    /// Builds the per-row DataTemplate (CheckBox bound to IsSelected/IsEnabled
    /// + Content=Name). Done in code so we avoid XAML BAML loading entirely.
    /// </summary>
    private DataTemplate BuildRowTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(CheckBox.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(ChannelRow.IsSelected)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        factory.SetBinding(CheckBox.IsEnabledProperty,
            new System.Windows.Data.Binding(nameof(ChannelRow.IsEnabled)));
        factory.SetBinding(ContentControl.ContentProperty,
            new System.Windows.Data.Binding(nameof(ChannelRow.Name)));
        factory.SetValue(Control.ForegroundProperty, (Brush)Brushes.White);
        factory.SetValue(Control.FontSizeProperty, 13.0);
        factory.SetValue(Control.PaddingProperty, new Thickness(8, 6, 8, 6));
        factory.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        factory.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnRowToggled));
        factory.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnRowToggled));
        return new DataTemplate { VisualTree = factory };
    }

    private void OnRowToggled(object sender, RoutedEventArgs e)
    {
        UpdateRowEnabledState();
        UpdateCountText();
    }

    /// <summary>
    /// Re-runs enabled-state for all rows. Once the cap is hit, every UNSELECTED
    /// row is greyed out so the user can't pick a 4th. Already-selected rows
    /// stay enabled so they can still be unchecked.
    /// </summary>
    private void UpdateRowEnabledState()
    {
        var atCap = _rows.Count(r => r.IsSelected) >= MaxAfk;
        foreach (var r in _rows)
            r.IsEnabled = !atCap || r.IsSelected;
    }

    private void UpdateCountText()
    {
        var n = _rows.Count(r => r.IsSelected);
        _countText.Text = $"{n} of {MaxAfk} selected";
    }

    /// <summary>
    /// Row-level VM for the channel list. Implements INotifyPropertyChanged so
    /// IsEnabled toggles update the UI when the cap-enforcement logic flips it.
    /// </summary>
    private sealed class ChannelRow : INotifyPropertyChanged
    {
        public string Id { get; }
        public string Name { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; Notify(); } }
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; Notify(); } }
        }

        public ChannelRow(string id, string name) { Id = id; Name = name; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));
    }
}
