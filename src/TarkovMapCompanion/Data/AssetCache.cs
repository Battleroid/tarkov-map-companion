using System.Security.Cryptography;
using System.Text;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Fetch-once-and-keep storage for remote assets (SVG maps, map tiles).
/// </summary>
/// <remarks>
/// Everything here is regenerable, so it lives under %LOCALAPPDATA% and can be deleted freely.
/// A cached copy always wins over the network: these assets change only when a map is reworked,
/// and a player mid-raid is better served by a stale map than by a spinner.
/// </remarks>
public sealed class AssetCache(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? CreateDefaultClient();
    private readonly SemaphoreSlim _networkLimit = new(6, 6);

    /// <summary>Set false to keep the app entirely offline.</summary>
    public bool AllowNetwork { get; set; } = true;

    public string Root { get; init; } = AppPaths.CacheDirectory;

    /// <summary>
    /// Returns the asset bytes, from disk if present and from the network otherwise.
    /// Returns null when it is not cached and cannot be fetched.
    /// </summary>
    public async Task<byte[]?> GetAsync(string url, string category, CancellationToken cancellationToken = default)
    {
        var path = PathFor(url, category);

        if (File.Exists(path))
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Torn or locked cache entry: fall through and refetch.
            }
        }

        if (!AllowNetwork)
            return null;

        await _networkLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            await WriteCacheAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
        finally
        {
            _networkLimit.Release();
        }
    }

    public async Task<string?> GetStringAsync(string url, string category, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(url, category, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public bool IsCached(string url, string category) => File.Exists(PathFor(url, category));

    /// <summary>
    /// Cache path for a URL. The readable tail helps when poking around the cache by hand; the
    /// hash prefix is what actually guarantees uniqueness.
    /// </summary>
    public string PathFor(string url, string category)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();

        var tail = Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).LocalPath);
        if (string.IsNullOrWhiteSpace(tail))
            tail = "asset";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            tail = tail.Replace(invalid, '_');

        var directory = Path.Combine(Root, category);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{hash}-{tail}");
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            // Write beside the target and move into place: a partial file that another process
            // reads as complete would be cached corruption that survives restarts.
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache miss next time is not worth failing the fetch over.
        }
    }

    private static HttpClient CreateDefaultClient() =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "TarkovMapCompanion/1.0 (+https://github.com/)" },
            },
        };
}
