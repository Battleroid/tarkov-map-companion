using System.Text.RegularExpressions;

namespace TarkovMapCompanion.Vision;

public enum PanelRowKind
{
    /// <summary>The "Find an extraction point" banner above the list.</summary>
    ListHeader,

    /// <summary>The "Stay in the extraction point" banner shown while extracting.</summary>
    ActiveHeader,

    /// <summary>
    /// An exit row: <c>EXFIL<i>nn</i></c> on a PMC raid, <c>EXIT<i>nn</i></c> on a Scav one.
    /// </summary>
    Extract,

    /// <summary>A <c>TRANSIT<i>nn</i></c> row.</summary>
    Transit,

    /// <summary>Inside the panel, but with no id we recognized.</summary>
    Unknown,
}

public sealed record PanelRow(
    PanelRowKind Kind,
    string Name,
    string RawText,
    IReadOnlyList<string>? Readings = null)
{
    /// <summary>
    /// Every plausible reading of this row's name, best guess first.
    /// </summary>
    /// <remarks>
    /// The id column is the least legible thing on the panel: short, set in a heavy face, and made
    /// of exactly the characters a reader invents. At low resolutions it comes back as "EXIT u" or
    /// "TRANSIT Q", which no pattern strips without also eating the first word of names that
    /// legitimately begin with "Transit". Offering both readings and letting the name match decide
    /// is safer than making the pattern cleverer.
    /// </remarks>
    public IReadOnlyList<string> NameCandidates => Readings is { Count: > 0 } ? Readings : [Name];

    /// <summary>Whether the row carries an id keyword, however mangled.</summary>
    public bool HasIdKeyword { get; init; }

    /// <summary>
    /// Whether failing to identify this row is worth telling the user about.
    /// </summary>
    /// <remarks>
    /// Text can land inside the panel's bounds without being part of it -- a hotbar label, when
    /// the panel opens lower down the screen. Those are picked up opportunistically and failing to
    /// match one is not news; a row that plainly says EXFIL and still matches nothing is.
    /// </remarks>
    public bool LooksLikeAnExitRow => Kind is PanelRowKind.Extract or PanelRowKind.Transit || HasIdKeyword;
}

/// <summary>What one screenshot's extraction panel said.</summary>
public sealed class ExtractPanelReading
{
    public static readonly ExtractPanelReading NotFound = new();

    /// <summary>
    /// True only when we are confident we were looking at the exit list. Everything downstream is
    /// gated on this, because an empty reading and a reading of an empty list are very different
    /// things: one means "no information", the other would mean "you have no exits".
    /// </summary>
    public bool PanelFound { get; init; }

    public IReadOnlyList<PanelRow> Rows { get; init; } = [];

    /// <summary>Rows that name an exit the player could walk to.</summary>
    public IReadOnlyList<PanelRow> Exits =>
        Rows.Where(r => r.Kind is PanelRowKind.Extract or PanelRowKind.Transit or PanelRowKind.Unknown).ToArray();
}

/// <summary>
/// Turns the text read off a screenshot into the rows of Tarkov's extraction panel.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the reader, because this is where every judgement call lives and it
/// needs to be testable against captured text without an OCR engine in the loop.
/// </para>
/// <para>
/// The panel is a table. Its rows are found geometrically -- fragments sharing a vertical band are
/// one row -- rather than by trusting the reader's own line breaking, which splits the id column
/// away from the name for some rows and merges them for others. Nothing assumes a fixed number of
/// rows: the list is different every raid, and transits may not be there at all.
/// </para>
/// </remarks>
public static partial class ExtractPanelParser
{
    /// <summary>
    /// The id in the left column: EXIT01, TRANSIT02, EXFIL05. The digits are deliberately loose
    /// because Tarkov's font uses a slashed zero, which readers report as O, Ø, D or Q.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At least one digit-like character is required. Without that, "Transit to Reserve" would
    /// match the TRANSIT keyword and get its first word eaten.
    /// </para>
    /// <para>
    /// The trailing lookahead is what makes the loose character class safe. "EXIT Sniper Roadblock"
    /// cannot match, because S is followed by more letters rather than a space, so widening the
    /// class to cover the reader's inventions does not let it start eating real names.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<kind>EXTRACT|TRANSIT|EXFIL|EXIT)[\s\-_.]*[0-9OØQDIl|uUMmGSBZ]{1,4}(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdPrefix();

    /// <summary>Keywords that begin the id column, for the fallback readings.</summary>
    private static readonly string[] IdKeywords = ["EXTRACT", "TRANSIT", "EXFIL", "EXIT"];

    /// <summary>
    /// A trailing countdown, e.g. "0:00:54".
    /// </summary>
    /// <remarks>
    /// The digit class has to cover both cases of the slashed zero. The reader returns the
    /// uppercase Ø at 1440p and the lowercase ø when the text is smaller, and a class carrying only
    /// one of them leaves "Transit to Factory ø:øø:54" as the name -- which then falls below the
    /// match floor and reports a perfectly legible row as unreadable. It looked like a resolution
    /// limit and was nothing of the kind.
    /// </remarks>
    [GeneratedRegex(
        @"\s*[0-9OØQDIl|]{1,2}[:.;][0-9OØQDIl|]{2}([:.;][0-9OØQDIl|]{2})?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingTimer();

    public static ExtractPanelReading Parse(IReadOnlyList<OcrLine> lines)
    {
        var usable = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToArray();
        if (usable.Length == 0)
            return ExtractPanelReading.NotFound;

        // Anchor on the id column. Every row of the panel starts with one, so the leftmost id tells
        // us where the panel begins -- which is the only reliable way to discard the hotbar and
        // weapon labels that share the top of the frame and would otherwise be merged into a row.
        var idLines = usable.Where(l => IdPrefix().IsMatch(l.Text.TrimStart())).ToArray();
        if (idLines.Length == 0)
            return ExtractPanelReading.NotFound;

        var panelLeft = idLines.Min(l => l.Bounds.X);
        var slack = Math.Max(12.0, idLines.Average(l => l.Bounds.Height) * 0.75);

        // Judged on the middle of the text, not its left edge. A row whose id picks up a stray
        // glyph from the panel border starts slightly outside and would be dropped whole, while a
        // hotbar label that merely reaches the panel's edge is still mostly outside it.
        var inPanel = usable
            .Where(l => l.Bounds.X + (l.Bounds.Width / 2.0) >= panelLeft - slack)
            .ToArray();

        var rows = new List<PanelRow>();
        var prefixedExits = 0;
        var sawListHeader = false;
        var sawActiveHeader = false;

        foreach (var group in GroupIntoRows(inPanel))
        {
            var raw = string.Join(" ", group.OrderBy(l => l.Bounds.X).Select(l => l.Text.Trim()));
            var row = Classify(raw);

            if (row.Kind == PanelRowKind.ListHeader)
                sawListHeader = true;

            if (row.Kind == PanelRowKind.ActiveHeader)
                sawActiveHeader = true;

            if (row.Kind is PanelRowKind.Extract or PanelRowKind.Transit)
                prefixedExits++;

            rows.Add(row);
        }

        // Two independent ways to be sure: the list's own header, or several rows that are
        // unmistakably list rows.
        //
        // "Stay in the extraction point" vetoes the second of those. That banner shows the exit
        // being used rather than the ones on offer, and its rows are numbered exactly like the
        // list's, so counting rows cannot tell them apart -- only the wording can.
        var found = sawListHeader || (prefixedExits >= 2 && !sawActiveHeader);

        return found
            ? new ExtractPanelReading { PanelFound = true, Rows = rows }
            : ExtractPanelReading.NotFound;
    }

    /// <summary>
    /// Groups fragments that share a vertical band into table rows.
    /// </summary>
    /// <remarks>
    /// Banding rather than exact tops: the id column is set in a heavier, taller face than the
    /// name beside it, so the two never share a baseline and grouping on y alone would split every
    /// row in half.
    /// </remarks>
    private static List<List<OcrLine>> GroupIntoRows(IReadOnlyList<OcrLine> lines)
    {
        var ordered = lines.OrderBy(l => l.Bounds.CenterY).ToArray();

        var heights = ordered.Select(l => l.Bounds.Height).OrderBy(h => h).ToArray();
        var medianHeight = heights[heights.Length / 2];
        var tolerance = Math.Max(6.0, medianHeight * 0.7);

        var groups = new List<List<OcrLine>>();

        foreach (var line in ordered)
        {
            var current = groups.Count > 0 ? groups[^1] : null;

            if (current is not null && Math.Abs(line.Bounds.CenterY - current.Average(l => l.Bounds.CenterY)) <= tolerance)
                current.Add(line);
            else
                groups.Add([line]);
        }

        return groups;
    }

    private static PanelRow Classify(string raw)
    {
        var text = raw.Trim();

        // Headers first: they carry no id, and the wording says whether this is the list of exits
        // or the banner for the one being used.
        var normalized = NameMatcher.Normalize(text);
        if (normalized.Contains("extraction point", StringComparison.Ordinal))
        {
            return normalized.Contains("stay", StringComparison.Ordinal)
                ? new PanelRow(PanelRowKind.ActiveHeader, "", text)
                : new PanelRow(PanelRowKind.ListHeader, "", text);
        }

        var kind = PanelRowKind.Unknown;
        var readings = new List<string>();

        if (IdPrefix().Match(text) is { Success: true } match)
        {
            // EXFIL and EXIT are the same thing seen from different sides: Tarkov numbers a PMC's
            // exits EXFILnn and a Scav's EXITnn. Reading EXFIL as "the exit being used right now"
            // -- which is how it looks in the extracting banner -- silently threw away every row
            // of every PMC raid's list.
            kind = match.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "TRANSIT" => PanelRowKind.Transit,
                _ => PanelRowKind.Extract,
            };

            // Stripping the id worked, so that is the best guess.
            readings.Add(Clean(text[match.Length..]));
        }
        else
        {
            // It did not, so the whole row is the best guess -- and it may genuinely be one, for a
            // row whose id the reader dropped completely.
            readings.Add(Clean(text));
        }

        // Fallbacks for an id mangled past recognition: drop the first token, and the first two
        // when the second is short enough to be a stray digit rather than part of a name.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Contains rather than StartsWith: a stray glyph in front of the id ("WEXFIL01") is common
        // enough, and it defeats an anchored pattern while leaving the keyword perfectly visible.
        var hasKeyword = tokens.Length > 0
            && IdKeywords.Any(k => tokens[0].Contains(k, StringComparison.OrdinalIgnoreCase));

        if (tokens.Length > 1 && hasKeyword)
        {
            readings.Add(Clean(string.Join(' ', tokens.Skip(1))));

            if (tokens.Length > 2 && tokens[1].Length <= 3)
                readings.Add(Clean(string.Join(' ', tokens.Skip(2))));
        }

        readings.Add(Clean(text));

        var distinct = readings
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return distinct.Length == 0
            ? new PanelRow(kind, "", text) { HasIdKeyword = hasKeyword }
            : new PanelRow(kind, distinct[0], text, distinct) { HasIdKeyword = hasKeyword };
    }

    private static string Clean(string value) => TrailingTimer().Replace(value, "").Trim();
}
