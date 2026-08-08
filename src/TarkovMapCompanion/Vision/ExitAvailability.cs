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

    /// <summary>Exit being used at the moment the screenshot was taken, if any.</summary>
    public string? ActiveExtractName { get; init; }

    /// <summary>Distinct exit names recognized.</summary>
    public int NameCount => _names.Count;

    public bool Includes(MapPoi poi) => _names.Contains(NameMatcher.Normalize(poi.Name));

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

            if (NameMatcher.Match(row.Name, candidates) is { } match)
                matched.Add(match.NormalizedName);
            else
                unresolved.Add(row.Name);
        }

        if (matched.Count == 0)
            return null;

        // The exit being stood in is worth naming even though it is already in the list.
        string? active = null;
        if (reading.ActiveExtractName is { Length: > 0 } activeRow
            && NameMatcher.Match(activeRow, candidates) is { } activeMatch)
        {
            active = activeMatch.NormalizedName;
        }

        return new ExitAvailability(matched)
        {
            MapNormalizedName = mapNormalizedName,
            TakenAt = takenAt,
            Unresolved = unresolved,
            ActiveExtractName = active,
        };
    }
}
