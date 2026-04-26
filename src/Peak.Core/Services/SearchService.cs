using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Peak.Core.Models;

namespace Peak.Core.Services;

/// <summary>
/// Spotlight-style search. Indexes Start Menu shortcuts on startup (and refreshes
/// periodically) so type-ahead queries return ranked matches without disk hits.
///
/// Ranking, descending: exact match → prefix → word-prefix → substring. Items
/// scoring 0 are filtered out.
/// </summary>
public class SearchService : IDisposable
{
    private readonly ILogger<SearchService>? _logger;
    private readonly object _lock = new();
    private List<SearchResult> _index = new();
    private CancellationTokenSource? _refreshCts;

    /// <summary>Number of apps in the current index. Useful for diagnostics in the UI.</summary>
    public int IndexedCount
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>Last index rebuild error message — empty when index is healthy.</summary>
    public string LastIndexError { get; private set; } = "";

    /// <summary>True once the first index rebuild has completed (success OR failure).</summary>
    public bool HasInitialIndex { get; private set; }

    /// <summary>Fires after every index rebuild attempt — UI can refresh its diagnostic display.</summary>
    public event Action? IndexUpdated;

    /// <summary>
    /// Path of the diagnostic log file. Set on construction; written on every
    /// rebuild so we have a permanent trace independent of the in-process logger.
    /// </summary>
    private readonly string _diagLogPath;

    /// <summary>How often the Start Menu index is rebuilt while the app runs.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public SearchService(ILogger<SearchService>? logger = null)
    {
        _logger = logger;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Peak", "logs");
        try { Directory.CreateDirectory(dir); } catch { }
        _diagLogPath = Path.Combine(dir, "search.log");
    }

    /// <summary>Append a single line to %AppData%\Peak\logs\search.log. Failures are silent.</summary>
    private void DiagLog(string message)
    {
        try
        {
            File.AppendAllText(_diagLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* disk full / locked — ignore */ }
    }

    /// <summary>
    /// Kicks off the initial index build (non-blocking) and the periodic refresh loop.
    /// Safe to call once at startup.
    /// </summary>
    public void Start()
    {
        _refreshCts = new CancellationTokenSource();
        _ = RefreshLoopAsync(_refreshCts.Token);
    }

    /// <summary>
    /// Returns the top <paramref name="max"/> matches for <paramref name="query"/>,
    /// sorted by score descending. Empty query returns an empty list. Thread-safe.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<SearchResult>();
        query = query.Trim();

        List<SearchResult> snapshot;
        lock (_lock) snapshot = _index;

        var matches = new List<SearchResult>();
        foreach (var item in snapshot)
        {
            var score = ScoreMatch(item.Name, query);
            if (score > 0)
                matches.Add(item with { Score = score });
        }

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        return matches.Count > max ? matches.GetRange(0, max) : matches;
    }

    /// <summary>
    /// Launches a result via the OS shell. Win32 apps and files go through ShellExecute
    /// directly. UWP apps (paths starting with <c>shell:appsFolder\</c>) are launched
    /// via explorer.exe so the AppX activation path runs.
    /// </summary>
    public void Launch(SearchResult result)
    {
        try
        {
            ProcessStartInfo psi;
            if (result.Path.StartsWith("shell:appsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                // UWP / packaged app — explorer.exe is the only reliable launcher.
                psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = result.Path,
                    UseShellExecute = true
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = result.Path,
                    UseShellExecute = true
                };
            }
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to launch {ResultPath}", result.Path);
        }
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        DiagLog("RefreshLoopAsync starting — kicking off initial RebuildIndex");

        // First build immediately so the index is ready ASAP. Wrapped in try/catch
        // so an unexpected throw doesn't kill the entire loop silently — without
        // this, the fire-and-forget `_ = RefreshLoopAsync(...)` swallows everything.
        try { await Task.Run(() => RebuildIndex(), ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            DiagLog($"INITIAL RebuildIndex threw: {ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(ex, "Initial search index build failed");
            LastIndexError = ex.Message;
            HasInitialIndex = true;
            IndexUpdated?.Invoke();
        }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RefreshInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { await Task.Run(() => RebuildIndex(), ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                DiagLog($"Refresh RebuildIndex threw: {ex.GetType().Name}: {ex.Message}");
                _logger?.LogWarning(ex, "Search index refresh failed");
            }
        }
    }

    /// <summary>
    /// Builds the index from the Windows Shell <c>shell:appsFolder</c> namespace
    /// — Windows' own "All apps" virtual folder, covering Win32 desktop programs
    /// and UWP/Microsoft Store apps in one pass. Falls back to a direct .lnk scan
    /// if the Shell COM call fails.
    /// </summary>
    private void RebuildIndex()
    {
        DiagLog("RebuildIndex called");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<SearchResult>();
        Exception? shellError = null;
        int shellCount = 0;

        // Shell.Application is COM/STA-only. Task.Run uses MTA pool threads, so we
        // spin a dedicated STA worker, run the enumeration, and join back here.
        var staThread = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    DiagLog("Shell.Application ProgID not found");
                    return;
                }

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic? folder = shell.NameSpace("shell:appsFolder");
                if (folder == null)
                {
                    DiagLog("shell:appsFolder NameSpace returned null");
                    return;
                }

                foreach (dynamic item in folder.Items())
                {
                    string? name = item.Name as string;
                    string? parsing = item.Path as string;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing))
                        continue;
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seen.Add(name)) continue;

                    fresh.Add(new SearchResult(name, $"shell:appsFolder\\{parsing}",
                                               SearchResultType.Application));
                    shellCount++;
                }
            }
            catch (Exception ex) { shellError = ex; }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        staThread.Join();

        if (shellError != null)
        {
            DiagLog($"Shell enumeration FAILED: {shellError.GetType().Name}: {shellError.Message}");
            _logger?.LogWarning(shellError, "Shell.Application enumeration failed");
        }
        else
        {
            DiagLog($"Shell enumeration OK — {shellCount} apps via shell:appsFolder");
        }

        // Supplement: scan Start Menu .lnk files directly so shortcuts the
        // AppsFolder namespace might miss (rare custom installs) still appear.
        // EnumerationOptions.IgnoreInaccessible silently skips junctions / locked
        // dirs (e.g. localized "Programme" reparse points on German Windows)
        // that would otherwise throw UnauthorizedAccessException mid-iteration.
        int lnkCount = 0;
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots)
        {
            DiagLog($"Start Menu root: '{root}' — exists={Directory.Exists(root)}");
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", enumerationOptions); }
            catch (Exception ex)
            {
                DiagLog($"EnumerateFiles failed for '{root}': {ex.Message}");
                continue;
            }

            // The enumerator itself can still throw on a per-item basis if a folder
            // becomes inaccessible mid-walk. Wrap the iteration in try/catch so a
            // single bad item doesn't trash the whole .lnk supplement.
            try
            {
                foreach (var lnk in files)
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name)) continue;

                    fresh.Add(new SearchResult(name, lnk, SearchResultType.Application));
                    lnkCount++;
                }
            }
            catch (Exception ex)
            {
                DiagLog($".lnk iteration aborted on '{root}': {ex.Message}");
            }
        }

        DiagLog($"Start Menu .lnk scan: {lnkCount} new entries");

        lock (_lock) _index = fresh;
        LastIndexError = shellError?.Message ?? "";
        HasInitialIndex = true;
        DiagLog($"RebuildIndex done — total {fresh.Count} entries indexed");
        IndexUpdated?.Invoke();
    }

    /// <summary>
    /// Scores a candidate name against the query. Higher = better match. Zero means
    /// no match. Designed so common prefix typing ("vsc" → "Visual Studio Code")
    /// surfaces the right thing.
    /// </summary>
    private static int ScoreMatch(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            // Shorter names rank higher when they share a prefix — "Word" beats "WordPad".
            return 800 - name.Length;
        }

        // Word-prefix match: any whitespace-separated token starts with the query.
        // Lets "code" find "Visual Studio Code" and "vs" find both "Visual Studio" entries.
        var tokens = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 600 - name.Length;
        }

        // Acronym match: query letters appear as the first letter of consecutive tokens
        // ("vsc" → "Visual Studio Code").
        if (tokens.Length >= query.Length)
        {
            var match = true;
            for (int i = 0; i < query.Length; i++)
            {
                if (tokens[i].Length == 0 ||
                    char.ToLowerInvariant(tokens[i][0]) != char.ToLowerInvariant(query[i]))
                {
                    match = false;
                    break;
                }
            }
            if (match) return 500 - name.Length;
        }

        // Fallback: substring anywhere in the name.
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 300 - name.Length;

        return 0;
    }

    public void Dispose()
    {
        try { _refreshCts?.Cancel(); } catch { }
        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}
