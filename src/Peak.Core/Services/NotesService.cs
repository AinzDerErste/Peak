using System.Text.Json;
using Peak.Core.Models;

namespace Peak.Core.Services;

public class NotesService : IDisposable
{
    private readonly string _notesPath;
    private readonly List<NoteItem> _notes = new();
    private System.Timers.Timer? _saveTimer;

    public event Action? NotesChanged;

    public NotesService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak");
        Directory.CreateDirectory(appData);
        _notesPath = Path.Combine(appData, "notes.json");
        Load();
    }

    public IReadOnlyList<NoteItem> GetNotes() => _notes.AsReadOnly();

    public NoteItem CreateNote()
    {
        var note = new NoteItem { Title = "New Note" };
        _notes.Insert(0, note);
        Save();
        NotesChanged?.Invoke();
        return note;
    }

    public void DeleteNote(string id)
    {
        _notes.RemoveAll(n => n.Id == id);
        Save();
        NotesChanged?.Invoke();
    }

    public void UpdateNote(NoteItem note)
    {
        note.ModifiedAt = DateTime.Now;

        // Auto-title from first line
        if (string.IsNullOrWhiteSpace(note.Title) || note.Title == "New Note")
        {
            var firstLine = note.Content.Split('\n', 2)[0].Trim();
            if (!string.IsNullOrEmpty(firstLine))
                note.Title = firstLine.Length > 40 ? firstLine[..40] + "..." : firstLine;
        }

        MarkDirty();
        NotesChanged?.Invoke();
    }

    public void MarkDirty()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        _saveTimer = new System.Timers.Timer(1000) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) => Save();
        _saveTimer.Start();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_notesPath))
            {
                var json = File.ReadAllText(_notesPath);
                var items = JsonSerializer.Deserialize<List<NoteItem>>(json,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                if (items != null)
                    _notes.AddRange(items);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_notes,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
            File.WriteAllText(_notesPath, json);
        }
        catch { }
    }

    public void Dispose()
    {
        _saveTimer?.Stop();
        _saveTimer?.Dispose();
        Save();
    }
}
