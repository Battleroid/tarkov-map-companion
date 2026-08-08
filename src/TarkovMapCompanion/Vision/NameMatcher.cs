using System.Text;

namespace TarkovMapCompanion.Vision;

/// <summary>The best candidate for a read line, or the reason there wasn't one.</summary>
public sealed record NameMatch(string NormalizedName, double Score, double RunnerUp)
{
    public double Margin => Score - RunnerUp;
}

/// <summary>
/// Resolves a line of read text to one of a known set of names.
/// </summary>
/// <remarks>
/// <para>
/// This is a closed-set problem, not open-ended text recognition: the game can only be showing an
/// exit that exists on the current map, and we have that list. So the reader does not have to be
/// character-perfect, it only has to get close enough that one candidate stands out. "RIJAF
/// Roadblock" is 87% similar to "RUAF Roadblock" and 69% similar to the next best thing, which is
/// a comfortable decision.
/// </para>
/// <para>
/// The margin requirement is the important half. Customs has both "Warehouse 17" and "Warehouse 4",
/// and Reserve has "ZB-1011" and "ZB-1012" -- one character apart. When a misread lands between two
/// candidates, refusing to choose is correct; guessing would put a marker on the wrong side of the
/// map, which is worse than admitting we could not read it.
/// </para>
/// </remarks>
public static class NameMatcher
{
    /// <summary>Minimum similarity before a line is considered to name anything at all.</summary>
    public const double DefaultFloor = 0.78;

    /// <summary>How far the best candidate must beat the next distinct one.</summary>
    public const double DefaultMargin = 0.08;

    /// <summary>
    /// Casefolds and strips punctuation so "Boiler Room Basement (Co-op)" and
    /// "boiler room basement co-op" compare equal.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                // Punctuation and whitespace both collapse to a single separator, so a stray comma
                // the reader invents cannot change the shape of the string.
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Similarity in 0..1, where 1 is an exact match after normalization.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1.0;

        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        var distance = Levenshtein(a, b);
        return 1.0 - ((double)distance / Math.Max(a.Length, b.Length));
    }

    /// <summary>
    /// Picks the candidate that <paramref name="text"/> names, or null when nothing is close enough
    /// or two candidates are too close to separate.
    /// </summary>
    /// <param name="candidates">Already-normalized names. Duplicates are harmless.</param>
    public static NameMatch? Match(
        string text,
        IEnumerable<string> candidates,
        double floor = DefaultFloor,
        double margin = DefaultMargin)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return null;

        string? best = null;
        var bestScore = 0.0;
        var runnerUp = 0.0;

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
                continue;

            var score = Similarity(normalized, candidate);

            if (score > bestScore)
            {
                // Two POIs can share a display name -- Customs lists both a PMC and a Scav "RUAF
                // Roadblock" -- and that is not an ambiguity, it is one place with two rules. Only
                // a genuinely different name counts as a rival.
                if (best is not null && !string.Equals(best, candidate, StringComparison.Ordinal))
                    runnerUp = bestScore;

                bestScore = score;
                best = candidate;
            }
            else if (score > runnerUp && !string.Equals(best, candidate, StringComparison.Ordinal))
            {
                runnerUp = score;
            }
        }

        if (best is null || bestScore < floor || bestScore - runnerUp < margin)
            return null;

        return new NameMatch(best, bestScore, runnerUp);
    }

    /// <summary>
    /// Standard edit distance, two rows rather than a full matrix. The strings here are short
    /// enough that the allocation matters more than the algorithm.
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;

                current[j] = Math.Min(substitution, Math.Min(deletion, insertion));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
