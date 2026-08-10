using TarkovMapCompanion.Diagnostics;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>
/// Pictures of items, fetched once and kept on disk.
/// </summary>
/// <remarks>
/// <para>
/// The whole address of an item's icon is its BSG id, which the task snapshot already carries, so
/// there is no index to download and nothing to keep in sync — a name that resolves to an id
/// resolves to a picture. Icons are about 2.6 KB each and the snapshot references 366 of them, so
/// a player who eventually sees every one has spent under a megabyte.
/// </para>
/// <para>
/// Bytes rather than a decoded image, so this layer stays free of the UI toolkit and can be tested
/// without one. The view decodes.
/// </para>
/// <para>
/// Nothing here throws. A missing icon is a missing icon: the name beside it is the part that
/// matters, and an item whose picture would not load must not take a panel down with it.
/// </para>
/// </remarks>
public sealed class ItemIconStore
{
    /// <summary>The icon of one item, addressed entirely by its id.</summary>
    public const string IconUrlFormat = "https://assets.tarkov.dev/{0}-icon.webp";

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly string _directory;

    /// <summary>
    /// One task per id, shared by every caller that wants it.
    /// </summary>
    /// <remarks>
    /// The same key can appear in a dozen tracked tasks and in the pane at the same time. Keyed on
    /// the work rather than the result so the second caller waits on the first fetch instead of
    /// starting a second one.
    /// </remarks>
    private readonly Dictionary<string, Task<byte[]?>> _pending = new(StringComparer.Ordinal);

    public ItemIconStore(AppSettings settings, HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _settings = settings;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _directory = cacheDirectory ?? Path.Combine(AppPaths.CacheDirectory, "icons");
    }

    /// <summary>How many icons this session has taken off the network.</summary>
    public int Downloaded { get; private set; }

    /// <summary>The icon for an item id, from memory, then disk, then the network. Null if none.</summary>
    public Task<byte[]?> GetAsync(string? id, CancellationToken cancellationToken = default)
    {
        if (id is not { Length: > 0 } || !LooksLikeAnId(id))
            return Task.FromResult<byte[]?>(null);

        lock (_pending)
        {
            if (_pending.TryGetValue(id, out var running))
                return running;

            var started = LoadAsync(id, cancellationToken);
            _pending[id] = started;
            return started;
        }
    }

    /// <summary>The cached file for an id, whether or not it exists.</summary>
    public string PathFor(string id) => Path.Combine(_directory, id + ".webp");

    private async Task<byte[]?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        var path = PathFor(id);

        try
        {
            if (File.Exists(path))
                return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable cache is the same as no cache.
        }

        if (!_settings.AllowNetwork)
            return null;

        try
        {
            var bytes = await _http
                .GetByteArrayAsync(string.Format(IconUrlFormat, id), cancellationToken)
                .ConfigureAwait(false);

            if (bytes.Length == 0)
                return null;

            Downloaded++;
            await WriteCacheAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warn($"[icons] {id}: {ex.Message}");

            // Forget the failure so a later attempt, on a better connection, can try again. A
            // cached null would make one offline moment permanent for the rest of the session.
            lock (_pending)
                _pending.Remove(id);

            return null;
        }
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Temp then move, so a half-written icon is never read as a whole one.
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"[icons] could not cache {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>
    /// A 24-character hex string, which is what every BSG id is.
    /// </summary>
    /// <remarks>
    /// Checked because the id ends up in a URL and in a file name. Nothing in the bundled snapshot
    /// is anything else, but the snapshot can be refreshed from the network, and neither the cache
    /// directory nor a request should be steerable by whatever comes back.
    /// </remarks>
    internal static bool LooksLikeAnId(string id) =>
        id.Length == 24 && id.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));
}
