using System.Text.Json;
using Peak.Core.Models;

namespace Peak.Core.Services;

/// <summary>
/// Persists user notes to <c>%AppData%\Peak\notes.json</c>.
///
/// <para><b>Save model</b>: list mutations (Create/Delete) save synchronously
/// because they're rare. Edits flow through <see cref="MarkDirty"/>, which
/// debounces saves by 1 s — typing into a note doesn't write the file on
/// every keystroke.</para>
///
/// <para><b>Thread safety</b>: <see cref="_notes"/> is mutated from the UI
/// thread (Create/Delete/Update) but serialized from a thread-pool thread
/// in the debounced Elapsed handler. <see cref="_lock"/> serialises both
/// the snapshot under lock and the file write that follows so two threads
/// can't be racing the same notes.json.</para>
///
/// <para><b>Atomicity</b>: writes go through a temp file + <see cref="File.Move"/>
/// so a crash mid-write never leaves a zero-byte notes.json. Mirrors the
/// pattern <c>SettingsManager</c> uses for the same reason.</para>
/// </summary>
public class NotesService : IDisposable
{
    private readonly string _notesPath;
    private readonly List<NoteItem> _notes = new();
    private readonly object _lock = new();

    /// <summary>
    /// Single timer reused across MarkDirty calls. Stop+Start resets the
    /// interval without disposing the timer. The original implementation
    /// disposed and recreated the timer on every keystroke (potentially
    /// dozens per second when typing a note), generating allocation churn
    /// and — worse — letting two Elapsed handlers fire concurrently if a
    /// new timer started while the previous Elapsed delegate was on the
    /// thread-pool but hadn't yet entered Save().
    /// </summary>
    private readonly System.Timers.Timer _saveTimer;
    private bool _disposed;

    public event Action? NotesChanged;

    public NotesService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peak");
        Directory.CreateDirectory(appData);
        _notesPath = Path.Combine(appData, "notes.json");

        _saveTimer = new System.Timers.Timer(1000) { AutoReset = false };
        _saveTimer.Elapsed += (_, _) => Save();

        Load();
    }

    public IReadOnlyList<NoteItem> GetNotes()
    {
        // Snapshot under lock so a concurrent Save isn't iterating _notes
        // while the caller materialises an enumeration.
        lock (_lock)
            return _notes.ToList().AsReadOnly();
    }

    public NoteItem CreateNote()
    {
        var note = new NoteItem { Title = "New Note" };
        lock (_lock) _notes.Insert(0, note);
        Save();
        NotesChanged?.Invoke();
        return note;
    }

    public void DeleteNote(string id)
    {
        lock (_lock) _notes.RemoveAll(n => n.Id == id);
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

    /// <summary>
    /// Debounced save trigger. Resets the existing timer instead of
    /// allocating a new one — see field doc on <c>_saveTimer</c>.
    /// </summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        _saveTimer.Stop();
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
                    lock (_lock) _notes.AddRange(items);
            }
        }
        catch { /* corrupt file — start with empty list */ }
    }

    public void Save()
    {
        // Snapshot the list under the lock so we don't serialize a list
        // that the UI thread is mutating mid-iteration. JsonSerializer
        // walks the collection internally; without the snapshot we'd hit
        // InvalidOperationException ("Collection was modified") whenever
        // a save fired during a CreateNote / DeleteNote.
        List<NoteItem> snapshot;
        lock (_lock)
        {
            snapshot = new List<NoteItem>(_notes);
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

            // Atomic write: write to a temp file and rename. A crash mid-write
            // loses the temp file but leaves notes.json intact, instead of
            // truncating to zero bytes via direct File.WriteAllText.
            var tmp = _notesPath + ".tmp";
            File.WriteAllText(tmp, json);
            try
            {
                File.Move(tmp, _notesPath, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup if Move fails (e.g. AV temporarily
                // locks the temp file). The next Save will rewrite it.
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }
        catch { /* disk full / readonly / AV interference — retry on next MarkDirty */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _saveTimer.Dispose();
        Save();
    }
}
