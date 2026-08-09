using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovMapCompanion.Party;

[JsonConverter(typeof(TolerantMessageKindConverter))]
public enum PartyMessageKind
{
    /// <summary>
    /// Something a later build said that this one does not speak. Never sent; only ever read.
    /// </summary>
    /// <remarks>
    /// Zero on purpose, so a frame with no kind at all reads as unknown rather than as a Hello.
    /// </remarks>
    Unknown = 0,

    /// <summary>First thing a joining client sends. Doubles as proof it holds the secret.</summary>
    Hello,

    /// <summary>A client's own position, sent as each screenshot is read.</summary>
    Position,

    /// <summary>The host's complete picture of the squad, sent to everyone whenever it changes.</summary>
    Roster,

    /// <summary>
    /// A "look here" mark. An event rather than state, so it is passed straight along and never
    /// joins the roster -- somebody arriving later should not be shown a ping from before they
    /// were there, pointing at something that has long since moved.
    /// </summary>
    Ping,

    /// <summary>A player's chosen marker color, when it changes without a screenshot to ride on.</summary>
    Color,

    /// <summary>One player's whole route, replacing whatever we last heard from them.</summary>
    Route,

    /// <summary>Every route the host knows, fanned out the way the roster is.</summary>
    Routes,
}

/// <summary>
/// Reads a kind by name, and treats anything unrecognized as <see cref="PartyMessageKind.Unknown"/>.
/// </summary>
/// <remarks>
/// The stock string-enum converter throws on a name it has never seen, and that exception comes
/// straight out of ReadAsync -- where the host's catch drops the peer and the guest's tears the
/// session down. So without this, a build that has never heard of a message kind does not skip it,
/// it disconnects over it. Both loops already ignore kinds they do not handle; this is the one thing
/// that stood between that and being reachable, and it is what makes the next addition additive
/// instead of another flag day.
/// </remarks>
internal sealed class TolerantMessageKindConverter : JsonConverter<PartyMessageKind>
{
    public override PartyMessageKind Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions __)
    {
        // Malformed rather than merely newer, but it still has to be consumed whole or the reader
        // is left pointing into the middle of a value.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
            return PartyMessageKind.Unknown;
        }

        return reader.TokenType == JsonTokenType.String
               && Enum.TryParse<PartyMessageKind>(reader.GetString(), ignoreCase: true, out var kind)
            ? kind
            : PartyMessageKind.Unknown;
    }

    public override void Write(Utf8JsonWriter writer, PartyMessageKind value, JsonSerializerOptions _) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>One point on somebody's route, in game coordinates.</summary>
public sealed class RoutePoint
{
    public double X { get; set; }

    public double Z { get; set; }
}

/// <summary>
/// A player's route as it stands right now, replacing whatever was last heard from them.
/// </summary>
/// <remarks>
/// Whole thing every time, never a diff. Same argument as the roster: it makes a mid-raid join and a
/// mid-raid update the identical code path, and a route has no stable ids to diff against anyway --
/// the pins renumber every time one is removed.
/// </remarks>
public sealed class PeerRoute
{
    public string Name { get; set; } = "";

    /// <summary>Normalized map name. A route from another map is held but not drawn.</summary>
    public string Map { get; set; } = "";

    public List<RoutePoint> Points { get; set; } = [];
}

/// <summary>Where one member of the squad was, and how long ago.</summary>
public sealed class PeerPosition
{
    public string Name { get; set; } = "";

    /// <summary>Normalized map name. Peers elsewhere are listed but not drawn.</summary>
    public string Map { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double Yaw { get; set; }

    /// <summary>
    /// How stale this was when the host sent it, in seconds.
    /// </summary>
    /// <remarks>
    /// An age rather than a timestamp, deliberately. Two players' clocks can disagree by minutes,
    /// and a timestamp interpreted against the wrong clock would report a fresh position as ancient
    /// or -- far worse -- an ancient one as fresh. An age measured entirely on the host's own clock
    /// cannot be wrong that way; the receiver just adds however long it has held it.
    /// </remarks>
    public double AgeSeconds { get; set; }

    /// <summary>
    /// The sender's chosen marker color as <c>#RRGGBB</c>, or null when they have not picked one.
    /// </summary>
    /// <remarks>
    /// Carried rather than derived. The fallback works out a color from a peer's index in the
    /// roster, which is only stable while nobody leaves -- and two clients holding slightly
    /// different rosters will draw the same teammate in two different colors, which is precisely
    /// the confusion the colors exist to prevent.
    /// </remarks>
    public string? Color { get; set; }
}

public sealed class PartyMessage
{
    public PartyMessageKind Kind { get; set; }

    /// <summary>
    /// What the sender speaks. Absent means a build from before versions were declared.
    /// </summary>
    /// <remarks>
    /// Nothing gates on this. It exists so a mixed-version squad leaves an explanation in the log
    /// rather than a mystery, and so the next change has a version to compare against.
    /// </remarks>
    public int Version { get; set; }

    public string? Name { get; set; }

    /// <summary>Chosen marker color as <c>#RRGGBB</c>. Rides on Hello, and on its own kind.</summary>
    public string? Color { get; set; }

    public PeerPosition? Position { get; set; }

    public List<PeerPosition>? Roster { get; set; }

    /// <summary>One route, guest to host.</summary>
    public PeerRoute? Route { get; set; }

    /// <summary>Every route the host knows, host to guests.</summary>
    public List<PeerRoute>? Routes { get; set; }
}

// UseStringEnumConverter is gone: it existed for the one enum here, and the type-level converter
// on PartyMessageKind supersedes it.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PartyMessage))]
internal sealed partial class PartyJsonContext : JsonSerializerContext;

/// <summary>
/// Frames messages onto a TCP stream, encrypted with the key from the session code.
/// </summary>
/// <remarks>
/// <para>
/// Frame layout: a four-byte length, a twelve-byte nonce, a sixteen-byte tag, then the ciphertext.
/// AES-GCM, so a frame that has been tampered with fails to authenticate and the connection is
/// dropped rather than acted on.
/// </para>
/// <para>
/// The key doubles as the only credential. Anyone holding the secret half of the code can talk to
/// the host and nobody else can, so there is no separate password, account or handshake to get
/// wrong. Being unable to decrypt the first frame is exactly what "wrong code" means.
/// </para>
/// </remarks>
public static class PartyProtocol
{
    /// <summary>Refuses anything larger, so a hostile or confused peer cannot ask for a huge buffer.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>What this build speaks. Stamped on everything it sends.</summary>
    public const int Version = 2;

    /// <summary>
    /// Most points one route will carry, truncated on both send and receive.
    /// </summary>
    /// <remarks>
    /// Nothing bounded a route before, and an over-size frame is rejected by the <em>receiver</em>,
    /// which on the host means dropping the peer. A cap on each end is cheaper than a mystery
    /// disconnect. Far beyond any real route: a point every 64 is already more than anybody plans.
    /// </remarks>
    public const int MaxRoutePoints = 64;

    /// <summary>Most routes the host will fan out at once, for the same reason.</summary>
    public const int MaxSharedRoutes = 8;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>
    /// Stretches the short session secret into a real key.
    /// </summary>
    /// <remarks>
    /// The secret is only 48 bits, which is small enough to grind through offline if each guess is
    /// cheap. Running it through a hundred thousand PBKDF2 iterations makes each guess cost about a
    /// millisecond, which puts a brute force well past the point of being worth it -- particularly
    /// for a plaintext whose entire value is "where somebody stood thirty seconds ago".
    /// </remarks>
    public static byte[] DeriveKey(ReadOnlySpan<byte> secret)
    {
        // A fixed salt is acceptable here because the secret is random per session; the salt is
        // only separating this key from any other use of the same bytes.
        //
        // Bumped to v2 with the colors-and-routes change. It is the only version marker the wire
        // has, and moving it means an older build cannot decrypt a single frame -- so a mixed
        // squad fails immediately and completely at the handshake, rather than connecting, looking
        // healthy, and then dropping somebody the first time a route is shared. Given the change
        // was going to break them either way, failing at the door is the kinder half of it.
        var salt = "TarkovMapCompanion/party/v2"u8.ToArray();

        return Rfc2898DeriveBytes.Pbkdf2(secret, salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    public static async Task WriteAsync(
        Stream stream,
        byte[] key,
        PartyMessage message,
        CancellationToken cancellationToken = default)
    {
        message.Version = Version;

        var json = JsonSerializer.SerializeToUtf8Bytes(message, PartyJsonContext.Default.PartyMessage);

        // Refused here rather than left for the receiver. An over-size frame reads as corruption at
        // the far end, and the host answers corruption by dropping the peer -- so sending one is a
        // way to disconnect somebody by accident. The caps above should make this unreachable; if
        // it fires, one of them is wrong.
        if (NonceBytes + TagBytes + json.Length > MaxFrameBytes)
        {
            Diagnostics.Log.Warn(
                $"refusing to send an over-size {message.Kind} frame ({json.Length} bytes)");
            return;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[json.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, json, cipher, tag);

        var frame = new byte[4 + NonceBytes + TagBytes + cipher.Length];
        var length = NonceBytes + TagBytes + cipher.Length;

        frame[0] = (byte)(length >> 24);
        frame[1] = (byte)(length >> 16);
        frame[2] = (byte)(length >> 8);
        frame[3] = (byte)length;

        nonce.CopyTo(frame, 4);
        tag.CopyTo(frame, 4 + NonceBytes);
        cipher.CopyTo(frame, 4 + NonceBytes + TagBytes);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Returns null at end of stream; throws on a frame that will not authenticate.
    /// </summary>
    public static async Task<PartyMessage?> ReadAsync(
        Stream stream,
        byte[] key,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;

        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

        if (length is < NonceBytes + TagBytes or > MaxFrameBytes)
            throw new InvalidDataException($"implausible frame length {length}");

        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false))
            return null;

        // Spans cannot live across an await, so the unwrapping is its own synchronous step.
        return JsonSerializer.Deserialize(Decrypt(key, body), PartyJsonContext.Default.PartyMessage);
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the frame will not authenticate.</summary>
    private static byte[] Decrypt(byte[] key, byte[] frame)
    {
        var nonce = frame.AsSpan(0, NonceBytes);
        var tag = frame.AsSpan(NonceBytes, TagBytes);
        var cipher = frame.AsSpan(NonceBytes + TagBytes);

        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);

        return plain;
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream
                .ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
                return false;

            read += got;
        }

        return true;
    }

    /// <summary>Trims a name to something that fits on a marker and cannot smuggle control characters.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Player";

        var builder = new StringBuilder(16);

        foreach (var c in name.Trim())
        {
            if (builder.Length == 16)
                break;

            var safe = char.IsControl(c) ? ' ' : c;

            // Collapse runs of whitespace, so a name carrying a line break does not come out with
            // a gap in it where the control characters were.
            if (safe == ' ' && builder.Length > 0 && builder[^1] == ' ')
                continue;

            builder.Append(safe);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "Player" : cleaned;
    }
}
