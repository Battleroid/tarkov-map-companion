using TarkovMapCompanion.Data;

namespace TarkovMapCompanion.Vision;

/// <summary>
/// The set of exits the game said were available, read off one screenshot.
/// </summary>
/// <remarks>
/// <para>
/// Tarkov only offers a subset of a map's exits in any given raid, and which subset depends on
/// where you spawned. That list is on screen whenever the player brings the panel up, and it is
/// information the game is already showing them -- this just carries it over to the map so they do
/// not have to hold eight names in their head.
/// </para>
/// <para>
/// Holds names, not POI references. The POI objects are rebuilt whenever map data refreshes, and a
/// reading that silently stopped matching after a background refresh would be a miserable bug to
/// find. Names also handle the case where one name covers two POIs, which Customs does: its PMC
/// and Scav "RUAF Roadblock" are one place with two rule sets, and a row naming it means both.
/// </para>
/// </remarks>
public sealed class ExitAvailability
{
    private readonly HashSet<string> _names;

    private ExitAvailability(HashSet<string> names) => _names = names;

    /// <summary>The map this was read on. A reading does not survive a map change.</summary>
    public required string MapNormalizedName { get; init; }

    /// <summary>Capture time of the screenshot it came from, for the status line.</summary>
    public required DateTime TakenAt { get; init; }

    /// <summary>
    /// Rows that were read but could not be pinned to a known exit, shown to the user so a bad
    /// read looks like a bad read rather than like a missing exit.
    /// </summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];

    /// <summary>Distinct exit names recognized.</summary>
    public int NameCount => _names.Count;

    public bool Includes(MapPoi poi) => _names.Contains(NameMatcher.Normalize(poi.Name));

    /// <summary>
    /// Folds in an earlier reading from the same raid.
    /// </summary>
    /// <remarks>
    /// Which exits a raid offers is fixed when it starts, so two readings taken during one raid are
    /// two looks at the same list and the union of them is the better answer. This matters because
    /// a screenshot can catch the panel part-way through opening: one of Casey's caught it with the
    /// first three rows not yet drawn, and left on its own that reading would have dimmed three
    /// exits he really had. Combining means a partial look can only ever add.
    /// </remarks>
    public ExitAvailability MergedWith(ExitAvailability? earlier)
    {
        if (earlier is null
            || !string.Equals(earlier.MapNormalizedName, MapNormalizedName, StringComparison.Ordinal))
        {
            return this;
        }

        var names = new HashSet<string>(_names, StringComparer.Ordinal);
        names.UnionWith(earlier._names);

        // Anything the earlier reading could not place has either been resolved since or is still
        // unplaceable in this one; either way the newer list is the one worth reporting.
        return new ExitAvailability(names)
        {
            MapNormalizedName = MapNormalizedName,
            TakenAt = TakenAt,
            Unresolved = Unresolved,
        };
    }

    /// <summary>
    /// Matches a panel reading against the exits known for a map.
    /// </summary>
    /// <returns>
    /// Null when there is nothing trustworthy to say: no panel, or a panel whose rows all failed to
    /// resolve. Returning an empty filter instead would dim every exit on the map, turning a failed
    /// read into a confident-looking lie.
    /// </returns>
    public static ExitAvailability? Resolve(
        ExtractPanelReading reading,
        IEnumerable<MapPoi> exits,
        string mapNormalizedName,
        DateTime takenAt)
    {
        if (!reading.PanelFound)
            return null;

        var candidates = exits
            .Select(p => NameMatcher.Normalize(p.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        if (candidates.Count == 0)
            return null;

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var row in reading.Exits)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                continue;

            if (Best(row, candidates) is { } match)
                matched.Add(match.NormalizedName);
            else if (row.LooksLikeAnExitRow)
                unresolved.Add(row.Name);

            // A row with no id keyword that matches nothing is almost certainly not a row at all --
            // a hotbar label the panel happens to sit near. Reporting those as unrecognized exits
            // would train the user to ignore the one message that means something.
        }

        if (matched.Count == 0)
            return null;

        return Build(matched, mapNormalizedName, takenAt, unresolved);
    }

    /// <summary>
    /// Best match across every plausible reading of a row, or null when none of them resolve.
    /// </summary>
    /// <remarks>
    /// A row can be read more than one way when the id column is mangled, and only one of those
    /// readings will look like an exit name. Trying all of them and keeping the strongest costs a
    /// few string comparisons and turns an unreadable id into a non-event.
    /// </remarks>
    private static NameMatch? Best(PanelRow row, IReadOnlySet<string> candidates)
    {
        NameMatch? best = null;

        foreach (var reading in row.NameCandidates)
        {
            if (string.IsNullOrWhiteSpace(reading))
                continue;

            if (NameMatcher.Match(reading, candidates) is { } match
                && (best is null || match.Score > best.Score))
            {
                best = match;
            }
        }

        return best;
    }

    private static ExitAvailability Build(
        HashSet<string> matched,
        string mapNormalizedName,
        DateTime takenAt,
        IReadOnlyList<string> unresolved)
    {
        return new ExitAvailability(matched)
        {
            MapNormalizedName = mapNormalizedName,
            TakenAt = takenAt,
            Unresolved = unresolved,
        };
    }
}
