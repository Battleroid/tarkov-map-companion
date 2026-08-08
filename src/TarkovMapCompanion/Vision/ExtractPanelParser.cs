using System.Text.RegularExpressions;

namespace TarkovMapCompanion.Vision;

public enum PanelRowKind
{
    /// <summary>The "Find an extraction point" banner above the list.</summary>
    ListHeader,

    /// <summary>The "Stay in the extraction point" banner shown while extracting.</summary>
    ActiveHeader,

    /// <summary>An <c>EXIT<i>nn</i></c> row.</summary>
    Extract,

    /// <summary>A <c>TRANSIT<i>nn</i></c> row.</summary>
    Transit,

    /// <summary>An <c>EXFIL<i>nn</i></c> row: the exit being used right now.</summary>
    Active,

    /// <summary>Inside the panel, but with no id we recognized.</summary>
    Unknown,
}

public sealed record PanelRow(PanelRowKind Kind, string Name, string RawText);

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

    /// <summary>The exit currently being stood in, when the screenshot caught an extraction.</summary>
    public string? ActiveExtractName =>
        Rows.FirstOrDefault(r => r.Kind == PanelRowKind.Active)?.Name;
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
    /// At least one digit-like character is required. Without that, "Transit to Reserve" would
    /// match the TRANSIT keyword and get its first word eaten.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<kind>EXTRACT|TRANSIT|EXFIL|EXIT)[\s\-_.]*[0-9OØQDIl|]{1,4}(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdPrefix();

    /// <summary>A trailing countdown, e.g. "0:00:54".</summary>
    [GeneratedRegex(
        @"\s*[0-9OØ]{1,2}[:.][0-9OØ]{2}([:.][0-9OØ]{2})?\s*$",
        RegexOptions.CultureInvariant)]
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

        var inPanel = usable.Where(l => l.Bounds.X >= panelLeft - slack).ToArray();

        var rows = new List<PanelRow>();
        var prefixedExits = 0;
        var sawListHeader = false;

        foreach (var group in GroupIntoRows(inPanel))
        {
            var raw = string.Join(" ", group.OrderBy(l => l.Bounds.X).Select(l => l.Text.Trim()));
            var row = Classify(raw);

            if (row.Kind == PanelRowKind.ListHeader)
                sawListHeader = true;

            if (row.Kind is PanelRowKind.Extract or PanelRowKind.Transit)
                prefixedExits++;

            rows.Add(row);
        }

        // Two independent ways to be sure: the list's own header, or several rows that are
        // unmistakably list rows. One EXFIL row on its own is the banner shown while extracting,
        // and must not be mistaken for "this is the only exit you have".
        var found = sawListHeader || prefixedExits >= 2;

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
        var name = text;

        if (IdPrefix().Match(text) is { Success: true } match)
        {
            kind = match.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "TRANSIT" => PanelRowKind.Transit,
                "EXFIL" => PanelRowKind.Active,
                _ => PanelRowKind.Extract,
            };

            name = text[match.Length..];
        }

        name = TrailingTimer().Replace(name, "").Trim();

        return new PanelRow(kind, name, text);
    }
}
