using System.Net;
using System.Security.Cryptography;

namespace TarkovMapCompanion.Party;

/// <summary>
/// The string one player pastes to the rest of the squad.
/// </summary>
/// <remarks>
/// <para>
/// The code is the address. It carries the host's endpoint and a shared secret, which is why the
/// feature needs nothing running anywhere: there is no directory to look a session up in, because
/// everything needed to reach it is in the twenty characters themselves. The squad already has a
/// channel for passing it along -- Discord, or the game's own chat -- so the humans are the
/// rendezvous the design would otherwise need a server for.
/// </para>
/// <para>
/// Crockford's base32 alphabet, which drops I, L, O and U so nothing looks like anything else, and
/// folds the confusable characters on the way back in. Codes get read aloud and retyped.
/// </para>
/// </remarks>
public static class SessionCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>IPv4 (4) + port (2) + secret (6).</summary>
    private const int PayloadBytes = 12;

    private const int SecretBytes = 6;

    private const int CodeChars = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>Builds the code a host hands out.</summary>
    public static string Format(IPAddress address, int port, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("only IPv4 addresses fit in a session code", nameof(address));

        if (secret.Length != SecretBytes)
            throw new ArgumentException($"secret must be {SecretBytes} bytes", nameof(secret));

        Span<byte> payload = stackalloc byte[PayloadBytes];

        address.TryWriteBytes(payload[..4], out _);
        payload[4] = (byte)(port >> 8);
        payload[5] = (byte)(port & 0xFF);
        secret.CopyTo(payload[6..]);

        return Group(Encode(payload));
    }

    /// <summary>
    /// Reads a code back, tolerating the ways people mangle one in transit: lowercase, missing or
    /// extra hyphens, stray spaces, and O typed for zero.
    /// </summary>
    public static bool TryParse(string? code, out IPEndPoint endPoint, out byte[] secret)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);
        secret = [];

        if (string.IsNullOrWhiteSpace(code))
            return false;

        Span<char> cleaned = stackalloc char[CodeChars];
        var length = 0;

        foreach (var raw in code)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
                continue;

            if (length == CodeChars)
                return false;

            var value = Value(raw);
            if (value < 0)
                return false;

            cleaned[length++] = Alphabet[value];
        }

        if (length != CodeChars)
            return false;

        var payload = Decode(cleaned);

        var port = (payload[4] << 8) | payload[5];
        if (port is <= 0 or > 65535)
            return false;

        endPoint = new IPEndPoint(new IPAddress(payload[..4]), port);
        secret = payload[6..].ToArray();

        return true;
    }

    /// <summary>Splits into groups of five, which is the difference between readable and not.</summary>
    private static string Group(string code) =>
        string.Join('-', Enumerable.Range(0, code.Length / 5).Select(i => code.Substring(i * 5, 5)));

    private static string Encode(ReadOnlySpan<byte> data)
    {
        var chars = new char[CodeChars];

        int buffer = 0, bits = 0, written = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                chars[written++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        // 96 bits does not divide into fives; pad the last character with zeros.
        if (bits > 0)
            chars[written] = Alphabet[(buffer << (5 - bits)) & 31];

        return new string(chars);
    }

    private static byte[] Decode(ReadOnlySpan<char> code)
    {
        var bytes = new byte[PayloadBytes];

        int buffer = 0, bits = 0, written = 0;

        foreach (var c in code)
        {
            buffer = (buffer << 5) | Value(c);
            bits += 5;

            if (bits < 8)
                continue;

            if (written < PayloadBytes)
                bytes[written++] = (byte)((buffer >> (bits - 8)) & 0xFF);

            bits -= 8;
        }

        return bytes;
    }

    private static int Value(char c)
    {
        var upper = char.ToUpperInvariant(c);

        // Crockford's leniency: these are read back as the digits they resemble.
        upper = upper switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            _ => upper,
        };

        return Alphabet.IndexOf(upper, StringComparison.Ordinal);
    }
}
