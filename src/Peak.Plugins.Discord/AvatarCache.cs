using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace Peak.Plugins.Discord;

/// <summary>
/// Downloads and caches Discord user avatars to disk + memory.
/// </summary>
public class AvatarCache : IDisposable
{
    private readonly string _cacheDir;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<string, BitmapImage> _memory = new();

    public AvatarCache()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Peak", "plugins", "discord", "avatars");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<BitmapImage?> GetAvatarAsync(string userId, string? avatarHash, int discriminator = 0)
    {
        var key = avatarHash ?? $"default-{discriminator % 5}";
        if (_memory.TryGetValue(key, out var cached))
            return cached;

        var filePath = Path.Combine(_cacheDir, $"{userId}_{key}.png");

        if (!File.Exists(filePath))
        {
            string url = avatarHash != null
                ? $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png?size=128"
                : $"https://cdn.discordapp.com/embed/avatars/{discriminator % 5}.png";

            try
            {
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(filePath, bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Avatar download failed for {userId}: {ex.Message}");
                return null;
            }
        }

        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(filePath);
            bi.EndInit();
            bi.Freeze();
            _memory[key] = bi;
            return bi;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
