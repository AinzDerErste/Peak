using System.IO;

namespace Peak.Plugins.TeamSpeak;

/// <summary>
/// Simple file logger for the TeamSpeak plugin.
/// Writes to %APPDATA%\Peak\plugins\teamspeak\plugin.log using a buffered StreamWriter.
/// </summary>
internal static class TeamSpeakLog
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;

    static TeamSpeakLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Peak", "plugins", "teamspeak");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "plugin.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch { }
    }

    public static void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
            }
        }
        catch { }
    }
}
