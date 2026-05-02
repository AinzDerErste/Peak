using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Peak.Plugins.Companion;

/// <summary>
/// Lightweight in-app editor for the Companion's user-editable files
/// (<c>moods.json</c> and the optional <c>companion.html</c> override).
/// Built code-only so the plugin needs no XAML compilation pipeline beyond
/// what <c>UseWPF</c> already gives us.
///
/// Features:
/// <list type="bullet">
///   <item>Monospace dark theme with a line-number gutter that stays in sync
///         with the editor's vertical scroll position.</item>
///   <item>Optional JSON validation before saving — invalid input keeps the
///         window open and shows the parse error in the footer.</item>
///   <item>"Reset to defaults" pulls a fresh template from a caller-supplied
///         provider (used for both regenerating <c>moods.json</c> and seeding
///         a blank <c>companion.html</c> from the embedded original).</item>
/// </list>
/// </summary>
internal class MoodEditorWindow : Window
{
    private readonly TextBox _editor;
    private readonly TextBlock _statusText;
    private readonly TextBlock _gutter;
    private readonly ScrollViewer _gutterScroll;
    private readonly string _filePath;
    private readonly bool _validateJson;
    private readonly Func<string>? _defaultsProvider;

    public MoodEditorWindow(string filePath, string title, bool validateJson, Func<string>? defaultsProvider)
    {
        _filePath = filePath;
        _validateJson = validateJson;
        _defaultsProvider = defaultsProvider;

        Title = title;
        Width = 900;
        Height = 700;
        MinWidth = 500;
        MinHeight = 300;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        rootGrid.Children.Add(BuildHeader(filePath));

        var (editorHost, editor, gutter, gutterScroll) = BuildEditorArea();
        Grid.SetRow(editorHost, 1);
        rootGrid.Children.Add(editorHost);
        _editor = editor;
        _gutter = gutter;
        _gutterScroll = gutterScroll;

        var (footer, status) = BuildFooter();
        rootGrid.Children.Add(footer);
        _statusText = status;

        Content = rootGrid;
        Loaded += (_, _) => { LoadFromFile(); _editor.Focus(); };

        // Ctrl+S → Save
        InputBindings.Add(new KeyBinding(new SaveCommand(this), Key.S, ModifierKeys.Control));
    }

    // ─── Layout builders ─────────────────────────────────────────────

    private UIElement BuildHeader(string filePath)
    {
        var header = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            LastChildFill = false
        };
        Grid.SetRow(header, 0);

        var pathLabel = new TextBlock
        {
            Text = filePath,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 10, 12, 10),
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        DockPanel.SetDock(pathLabel, Dock.Left);
        header.Children.Add(pathLabel);

        if (_defaultsProvider != null)
        {
            var resetBtn = MakeFlatButton("Reset to defaults");
            resetBtn.Margin = new Thickness(6, 6, 12, 6);
            resetBtn.Click += (_, _) =>
            {
                _editor.Text = _defaultsProvider();
                _statusText.Text = "Loaded defaults — click Save to write them to disk.";
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0x88));
            };
            DockPanel.SetDock(resetBtn, Dock.Right);
            header.Children.Add(resetBtn);
        }

        return header;
    }

    private (UIElement Host, TextBox Editor, TextBlock Gutter, ScrollViewer GutterScroll) BuildEditorArea()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Gutter — line numbers, scrolls in lockstep with the editor below.
        var gutterScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Padding = new Thickness(10, 12, 8, 12),
            CanContentScroll = false
        };
        var gutter = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            TextAlignment = TextAlignment.Right,
            MinWidth = 30,
            LineHeight = 17.4   // matches TextBox default for 13pt Consolas
        };
        gutterScroll.Content = gutter;
        Grid.SetColumn(gutterScroll, 0);
        grid.Children.Add(gutterScroll);

        var editor = new TextBox
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 13,
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
            CaretBrush = new SolidColorBrush(Colors.White),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 12, 8, 12),
            UndoLimit = 200
        };
        editor.SpellCheck.IsEnabled = false;

        // Sync vertical scroll: editor → gutter. Listen for ScrollChanged on
        // the editor (TextBox embeds a ScrollViewer at runtime) so the gutter
        // matches even on keyboard caret-driven scrolls, not just mouse-wheel.
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (Math.Abs(e.VerticalChange) > 0.01)
                gutterScroll.ScrollToVerticalOffset(e.VerticalOffset);
        }));

        editor.TextChanged += (_, _) => UpdateGutter();

        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        return (grid, editor, gutter, gutterScroll);
    }

    private (UIElement Footer, TextBlock Status) BuildFooter()
    {
        var footer = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            LastChildFill = true
        };
        Grid.SetRow(footer, 2);

        var saveBtn = MakeFlatButton("Save", isAccent: true);
        saveBtn.Margin = new Thickness(4, 8, 12, 8);
        saveBtn.IsDefault = true;
        saveBtn.Click += (_, _) => Save();
        DockPanel.SetDock(saveBtn, Dock.Right);
        footer.Children.Add(saveBtn);

        var cancelBtn = MakeFlatButton("Cancel");
        cancelBtn.Margin = new Thickness(4, 8, 4, 8);
        cancelBtn.IsCancel = true;
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        DockPanel.SetDock(cancelBtn, Dock.Right);
        footer.Children.Add(cancelBtn);

        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 8, 12, 8),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "Ready — Ctrl+S to save."
        };
        footer.Children.Add(status);

        return (footer, status);
    }

    private static Button MakeFlatButton(string text, bool isAccent = false)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Thickness(18, 5, 18, 5),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(isAccent
                ? Color.FromRgb(0x4C, 0xAF, 0x50)
                : Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(0)
        };
        // Replace the default rounded-by-OS chrome with a flat template so the
        // button blends with the dark editor surface.
        btn.Template = BuildFlatButtonTemplate();
        return btn;
    }

    private static ControlTemplate BuildFlatButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        // Hover effect — subtle brightness lift via Background trigger.
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.OpacityProperty, 0.85, "Bd"));
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    // ─── Behaviour ───────────────────────────────────────────────────

    private void UpdateGutter()
    {
        // Count newlines + 1 for the trailing line. A LineCount property exists
        // on TextBox but it's only valid after layout, so we use a manual count
        // here — fast enough for files measured in dozens of KB.
        int lines = 1;
        var t = _editor.Text;
        for (int i = 0; i < t.Length; i++) if (t[i] == '\n') lines++;

        var sb = new System.Text.StringBuilder(lines * 4);
        for (int i = 1; i <= lines; i++) sb.Append(i).Append('\n');
        _gutter.Text = sb.ToString();
    }

    private void LoadFromFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                _editor.Text = File.ReadAllText(_filePath);
                _statusText.Text = $"Loaded {_editor.Text.Length:N0} chars — Ctrl+S to save.";
            }
            else if (_defaultsProvider != null)
            {
                // Override file is optional — seed from defaults so the user
                // has something to edit. Saying "doesn't exist yet" sounded
                // alarming; reframe it as a normal starting state.
                _editor.Text = _defaultsProvider();
                _statusText.Text = "Editing the built-in default — save to create your override.";
            }
            else
            {
                _editor.Text = string.Empty;
                _statusText.Text = "New file — Ctrl+S to save.";
            }
            _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
        catch (Exception ex)
        {
            ShowError("Load failed: " + ex.Message);
        }
    }

    private void Save()
    {
        if (_validateJson)
        {
            try { JsonDocument.Parse(_editor.Text); }
            catch (Exception ex)
            {
                ShowError("Invalid JSON: " + ex.Message);
                return;
            }
        }
        try
        {
            // Ensure the directory exists — the user might have nuked it.
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_filePath, _editor.Text);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError("Save failed: " + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        _statusText.Text = message;
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x66));
    }

    /// <summary>Routed Ctrl+S binding target — InputBinding requires ICommand.</summary>
    private sealed class SaveCommand : System.Windows.Input.ICommand
    {
        private readonly MoodEditorWindow _w;
        public SaveCommand(MoodEditorWindow w) { _w = w; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _w.Save();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
