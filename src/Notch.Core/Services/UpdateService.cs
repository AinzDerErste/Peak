using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Notch.Core.Services;

public class UpdateService
{
    private readonly HttpClient _httpClient;
    private System.Threading.Timer? _checkTimer;
    private string? _downloadedInstallerPath;

    // TODO: Set this to the actual GitHub repo when created
    public const string GitHubOwner = "OWNER";
    public const string GitHubRepo = "Peak";

    public event Action? UpdateStatusChanged;

    public bool UpdateAvailable { get; private set; }
    public string NewVersion { get; private set; } = "";
    public string ReleaseNotes { get; private set; } = "";
    public bool IsDownloading { get; private set; }
    public bool IsDownloaded { get; private set; }
    public int DownloadProgress { get; private set; }

    public static string CurrentVersion
    {
        get
        {
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        }
    }

    public UpdateService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Update");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Peak-App");
    }

    public void StartPeriodicCheck()
    {
        // Check immediately, then every 6 hours
        _ = CheckForUpdateAsync();
        _checkTimer = new System.Threading.Timer(
            _ => _ = CheckForUpdateAsync(),
            null,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(6));
    }

    public async Task CheckForUpdateAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(url);
            if (release == null) return;

            var remoteVersion = release.TagName.TrimStart('v');
            if (!Version.TryParse(remoteVersion, out var remote)) return;
            if (!Version.TryParse(CurrentVersion, out var current)) return;

            if (remote > current)
            {
                NewVersion = remoteVersion;
                ReleaseNotes = release.Body ?? "";
                UpdateAvailable = true;
                UpdateStatusChanged?.Invoke();

                // Auto-download
                await DownloadUpdateAsync(release);
            }
        }
        catch
        {
            // GitHub not reachable or repo not found — silently ignore
        }
    }

    private async Task DownloadUpdateAsync(GitHubRelease release)
    {
        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset == null) return;

        try
        {
            IsDownloading = true;
            UpdateStatusChanged?.Invoke();

            var tempDir = Path.Combine(Path.GetTempPath(), "PeakUpdate");
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, asset.Name);

            // Delete old downloads
            foreach (var old in Directory.GetFiles(tempDir, "*.exe"))
            {
                try { File.Delete(old); } catch { }
            }

            using var response = await _httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(filePath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    DownloadProgress = (int)(totalRead * 100 / totalBytes);
                    UpdateStatusChanged?.Invoke();
                }
            }

            _downloadedInstallerPath = filePath;
            IsDownloading = false;
            IsDownloaded = true;
            DownloadProgress = 100;
            UpdateStatusChanged?.Invoke();
        }
        catch
        {
            IsDownloading = false;
            UpdateStatusChanged?.Invoke();
        }
    }

    public void InstallUpdate()
    {
        if (!IsDownloaded || _downloadedInstallerPath == null || !File.Exists(_downloadedInstallerPath))
            return;

        // Launch installer silently and exit app
        Process.Start(new ProcessStartInfo
        {
            FileName = _downloadedInstallerPath,
            Arguments = "/SILENT",
            UseShellExecute = true
        });

        // App will be closed by the caller
    }

    public void Stop()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }
}

public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}
