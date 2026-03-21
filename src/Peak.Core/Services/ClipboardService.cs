using System.Text.Json;
using Peak.Core.Models;

namespace Peak.Core.Services;

public class ClipboardService : IDisposable
{
    private readonly string _historyPath;
    private readonly string _imagesDir;
    private readonly List<ClipboardEntry> _history = new();
    private string? _lastTextHash;
    private bool _disposed;

    public int MaxEntries { get; set; } = 25;

    public event Action? HistoryChanged;

    /// <summary>
    /// Delegate set by the UI layer to read clipboard text (must run on STA thread).
    /// </summary>
    public Func<string?>? GetClipboardText { get; set; }

    /// <summary>
    /// Delegate set by the UI layer to check if clipboard has an image.
    /// </summary>
    public Func<bool>? ClipboardHasImage { get; set; }

    /// <summary>
    /// Delegate set by the UI layer to save clipboard image to a file and return the path.
    /// </summary>
    public Func<string, string?>? SaveClipboardImage { get; set; }

    /// <summary>
    /// Delegate set by the UI layer to check if clipboard has file drop list.
    /// </summary>
    public Func<string[]?>? GetClipboardFiles { get; set; }

    /// <summary>
    /// Delegate set by the UI layer to set clipboard text.
    /// </summary>
    public Action<string>? SetClipboardText { get; set; }

    public ClipboardService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak");
        Directory.CreateDirectory(appData);

        _historyPath = Path.Combine(appData, "clipboard-history.json");
        _imagesDir = Path.Combine(appData, "clipboard-images");
        Directory.CreateDirectory(_imagesDir);

        Load();
    }

    /// <summary>
    /// Called from UI thread DispatcherTimer to poll clipboard changes.
    /// </summary>
    public void Poll()
    {
        if (_disposed) return;

        try
        {
            // Check text
            var text = GetClipboardText?.Invoke();
            if (!string.IsNullOrEmpty(text))
            {
                var hash = text.GetHashCode().ToString();
                if (hash != _lastTextHash)
                {
                    _lastTextHash = hash;
                    AddEntry(new ClipboardEntry
                    {
                        ContentType = "text",
                        TextContent = text,
                        DisplayText = Truncate(text, 120)
                    });
                    return;
                }
            }

            // Check image
            if (ClipboardHasImage?.Invoke() == true)
            {
                var imgPath = SaveClipboardImage?.Invoke(_imagesDir);
                if (imgPath != null)
                {
                    // Avoid re-adding the same image
                    if (_history.Count > 0 && _history[0].FilePath == imgPath) return;

                    AddEntry(new ClipboardEntry
                    {
                        ContentType = "image",
                        FilePath = imgPath,
                        DisplayText = "Image"
                    });
                    return;
                }
            }

            // Check files
            var files = GetClipboardFiles?.Invoke();
            if (files is { Length: > 0 })
            {
                var display = string.Join(", ", files.Select(Path.GetFileName));
                var fileHash = display.GetHashCode().ToString();
                if (fileHash != _lastTextHash)
                {
                    _lastTextHash = fileHash;
                    AddEntry(new ClipboardEntry
                    {
                        ContentType = "file",
                        TextContent = string.Join("\n", files),
                        DisplayText = Truncate(display, 120)
                    });
                }
            }
        }
        catch
        {
            // Clipboard access can throw — ignore
        }
    }

    private void AddEntry(ClipboardEntry entry)
    {
        _history.Insert(0, entry);
        while (_history.Count > MaxEntries)
        {
            var removed = _history[^1];
            // Clean up saved image if removing an image entry
            if (removed.ContentType == "image" && !string.IsNullOrEmpty(removed.FilePath))
            {
                try { File.Delete(removed.FilePath); } catch { }
            }
            _history.RemoveAt(_history.Count - 1);
        }
        Save();
        HistoryChanged?.Invoke();
    }

    public IReadOnlyList<ClipboardEntry> GetHistory() => _history.AsReadOnly();

    public void CopyToClipboard(ClipboardEntry entry)
    {
        if (entry.ContentType == "text" && entry.TextContent != null)
        {
            _lastTextHash = entry.TextContent.GetHashCode().ToString();
            SetClipboardText?.Invoke(entry.TextContent);
        }
        else if (entry.ContentType == "file" && entry.TextContent != null)
        {
            _lastTextHash = entry.TextContent.GetHashCode().ToString();
            SetClipboardText?.Invoke(entry.TextContent);
        }
    }

    public void Remove(ClipboardEntry entry)
    {
        if (entry.ContentType == "image" && !string.IsNullOrEmpty(entry.FilePath))
        {
            try { File.Delete(entry.FilePath); } catch { }
        }
        _history.Remove(entry);
        Save();
        HistoryChanged?.Invoke();
    }

    public void ClearAll()
    {
        foreach (var e in _history.Where(e => e.ContentType == "image" && !string.IsNullOrEmpty(e.FilePath)))
        {
            try { File.Delete(e.FilePath!); } catch { }
        }
        _history.Clear();
        _lastTextHash = null;
        Save();
        HistoryChanged?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                var json = File.ReadAllText(_historyPath);
                var items = JsonSerializer.Deserialize<List<ClipboardEntry>>(json,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (items != null)
                {
                    _history.AddRange(items);
                    if (_history.Count > 0 && _history[0].TextContent != null)
                        _lastTextHash = _history[0].TextContent.GetHashCode().ToString();
                }
            }
        }
        catch { /* corrupted file — start fresh */ }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_history,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch { }
    }

    private static string Truncate(string text, int maxLen)
    {
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length > maxLen ? singleLine[..maxLen] + "..." : singleLine;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
