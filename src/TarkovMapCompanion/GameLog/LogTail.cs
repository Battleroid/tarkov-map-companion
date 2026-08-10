using System.Text;

namespace TarkovMapCompanion.GameLog;

/// <summary>
/// Follows the newest of a set of rolling log files and hands back complete lines.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because two watchers need it and the details are the kind that look optional right up
/// until they are not. Copying them would mean two places to get the UTF-8 boundary handling wrong.
/// </para>
/// <para>
/// Not thread safe on its own; each watcher owns one and calls it from a single sweep at a time.
/// </para>
/// </remarks>
internal sealed class LogTail
{
    private readonly Func<string, string?> _pickNewest;

    private string? _folder;
    private string? _currentFile;
    private long _offset;
    private string _partialLine = "";
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _attached;

    /// <param name="pickNewest">Chooses which file in a folder is the live one.</param>
    public LogTail(Func<string, string?> pickNewest)
    {
        _pickNewest = pickNewest;
    }

    /// <summary>The file being followed, for diagnostics.</summary>
    public string? CurrentFile => _currentFile;

    /// <summary>
    /// Points at a folder, forgetting everything about the last one.
    /// </summary>
    /// <param name="fromStart">
    /// Whether the first file attached to is read from the beginning rather than from its end.
    /// </param>
    /// <remarks>
    /// The choice is not cosmetic. A watcher that only cares what happens next wants the end, or
    /// starting the app replays the whole session. One rebuilding state from history wants the
    /// beginning, because the history <em>is</em> the state.
    /// </remarks>
    public void Reset(string folder, bool fromStart)
    {
        _folder = folder;
        _currentFile = null;
        _offset = 0;
        _partialLine = "";
        _decoder = Encoding.UTF8.GetDecoder();

        // Attached means "we have already chosen a starting point once". Every file after the
        // first is a fresh launch or a rolled suffix, and is always read whole.
        _attached = fromStart;
    }

    /// <summary>Complete lines appended since the last call, following a rollover if one happened.</summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        if (_folder is null || _pickNewest(_folder) is not { } newest)
            return [];

        if (!string.Equals(newest, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            _offset = _attached ? 0 : SafeLength(newest);
            _currentFile = newest;
            _partialLine = "";
            _decoder = Encoding.UTF8.GetDecoder();
            _attached = true;
        }

        return ReadFrom(newest);
    }

    private IReadOnlyList<string> ReadFrom(string path)
    {
        try
        {
            // Shared read: the game holds the file open for the whole session.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // A shorter file than last time means it was truncated or replaced under us.
            if (stream.Length < _offset)
            {
                _offset = 0;
                _partialLine = "";
                _decoder = Encoding.UTF8.GetDecoder();
            }

            if (stream.Length <= _offset)
                return [];

            stream.Seek(_offset, SeekOrigin.Begin);

            var buffer = new byte[stream.Length - _offset];
            var read = stream.Read(buffer, 0, buffer.Length);

            _offset += read;

            // The decoder is kept across reads on purpose. Holding back the trailing text is not
            // enough by itself: a multi-byte character split across the read boundary has already
            // been decoded by then, and half a UTF-8 sequence becomes a replacement character that
            // no amount of later text repairs.
            var chars = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
            _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);

            var text = _partialLine + new string(chars);
            var lines = text.Split('\n');

            // Whatever came after the final newline is held back. The game writes a line in pieces,
            // and half of one parses to nothing at best and to the wrong thing at worst.
            _partialLine = lines[^1];

            return lines[..^1].Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The game holds this file open and rotates it out from under us; both are ordinary.
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
