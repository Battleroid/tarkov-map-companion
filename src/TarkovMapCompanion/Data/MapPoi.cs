using TarkovMapCompanion.Localization;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;

namespace TarkovMapCompanion.Data;

/// <summary>Maps an <see cref="ExitFilter"/> onto the exit layers it should show.</summary>
public static class ExitFilters
{
    public static bool Includes(this ExitFilter filter, PoiKind kind) => filter switch
    {
        ExitFilter.All => true,

        // A PMC can take PMC and shared exits, and transits are open to both.
        ExitFilter.AsPmc => kind is PoiKind.ExtractPmc or PoiKind.ExtractShared or PoiKind.Transit,
        ExitFilter.AsScav => kind is PoiKind.ExtractScav or PoiKind.ExtractShared or PoiKind.Transit,

        ExitFilter.PmcOnly => kind is PoiKind.ExtractPmc,
        ExitFilter.ScavOnly => kind is PoiKind.ExtractScav,
        ExitFilter.SharedOnly => kind is PoiKind.ExtractShared,

        _ => true,
    };

    public static string Label(this ExitFilter filter) => filter switch
    {
        ExitFilter.All => Localizer.Loc("ExitFilter.All"),
        ExitFilter.AsPmc => Localizer.Loc("ExitFilter.AsPmc"),
        ExitFilter.AsScav => Localizer.Loc("ExitFilter.AsScav"),
        ExitFilter.PmcOnly => Localizer.Loc("ExitFilter.PmcOnly"),
        ExitFilter.ScavOnly => Localizer.Loc("ExitFilter.ScavOnly"),
        ExitFilter.SharedOnly => Localizer.Loc("ExitFilter.SharedOnly"),
        _ => filter.ToString(),
    };
}

/// <summary>Layer a point of interest belongs to. Each is independently toggleable.</summary>
public enum PoiKind
{
    ExtractPmc,
    ExtractScav,
    ExtractShared,
    Transit,
    Spawn,
    BossZone,
    LootContainer,
    Hazard,
    Lock,
    Switch,
    StationaryWeapon,
    BtrStop,
}

/// <summary>
/// One thing drawn on the map, already resolved to display text and projected into base space.
/// </summary>
/// <remarks>
/// Deliberately flat and pre-computed. The renderer touches every POI on every frame, so nothing
/// here should require a dictionary lookup or a string format at draw time.
/// </remarks>
public sealed class MapPoi
{
    public required PoiKind Kind { get; init; }

    /// <summary>Stable id where the source provides one; used to persist the selected extract.</summary>
    public string? Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Simplified-Chinese name, when the data has one. Null means the English name is shown.
    /// </summary>
    /// <remarks>
    /// <see cref="Name"/> stays English and is what OCR matches against; this is only ever used for
    /// display, so the display-language switch can never remove the English name.
    /// </remarks>
    public string? NameZh { get; init; }

    /// <summary>The name to draw/read in the active language.</summary>
    public string DisplayName => Localization.Localizer.Pick(Name, NameZh);

    public required GamePosition Position { get; init; }

    /// <summary>Position in the map's base pixel space.</summary>
    public required MapPoint Base { get; init; }

    /// <summary>Footprint in base space, when the source gives one. Drawn for the selected extract.</summary>
    public IReadOnlyList<MapPoint>? Outline { get; init; }

    /// <summary>Height band this occupies, for dimming things on other floors.</summary>
    public (double Bottom, double Top)? Elevation { get; init; }

    /// <summary>
    /// Requirement lines for the detail panel, e.g. "Activated by: power switch".
    /// Empty when the POI has no conditions attached.
    /// </summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>
    /// The Chinese rendering of <see cref="Details"/>, index-for-index. Empty when the source has
    /// no Chinese for a line, in which case the display falls back to the English text.
    /// </summary>
    /// <remarks>
    /// Built alongside <see cref="Details"/> from the wiki overlay, so the English original is
    /// never lost: the display shows the Chinese line with the English one underneath it.
    /// </remarks>
    public IReadOnlyList<string> DetailsZh { get; init; } = [];

    /// <summary>
    /// The detail block as it should actually be drawn: the Chinese line first, with the English
    /// original underneath it while the active language is Simplified Chinese.
    /// </summary>
    /// <remarks>
    /// In English, or where a line has no Chinese, this is exactly <see cref="Details"/> and the
    /// row looks as it always has. A Chinese line identical to the English one (the programmatic
    /// lines, whose wrapper is already localized) collapses to a single line rather than repeating.
    /// </remarks>
    public IReadOnlyList<PoiDetailLine> DetailLines
    {
        get
        {
            if (Details.Count == 0)
                return [];

            var bilingual = Localizer.Language == AppLanguage.ChineseSimplified;
            var lines = new List<PoiDetailLine>(Details.Count);

            for (var i = 0; i < Details.Count; i++)
            {
                var english = Details[i];
                var chinese = i < DetailsZh.Count ? DetailsZh[i] : null;

                if (bilingual
                    && chinese is { Length: > 0 }
                    && !string.Equals(chinese, english, StringComparison.Ordinal))
                {
                    lines.Add(new PoiDetailLine(chinese, IsSecondary: false));
                    lines.Add(new PoiDetailLine(english, IsSecondary: true));
                }
                else
                {
                    lines.Add(new PoiDetailLine(english, IsSecondary: false));
                }
            }

            return lines;
        }
    }

    /// <summary>Destination map for a transit.</summary>
    public string? DestinationMap { get; init; }

    /// <summary>
    /// True when this exit is not simply open: it needs a payment, an item, a switch, a flare,
    /// a partner, or only works for part of the raid. Drives the warning styling.
    /// </summary>
    public bool IsConditional { get; init; }

    /// <summary>Usable once per raid, so a squad cannot all follow you out.</summary>
    public bool IsSingleUse { get; init; }

    /// <summary>
    /// Ground distance from the last known player position, in meters, or null before the first
    /// screenshot places you.
    /// </summary>
    /// <remarks>
    /// Mutable and settable, unlike the rest of this type, because it changes on every fix while
    /// the POI itself does not. The exit list is rebuilt whenever it changes, so no change
    /// notification is needed.
    /// </remarks>
    public double? DistanceMeters { get; set; }

    /// <summary>Distance formatted for the list, or empty when the player has not been placed.</summary>
    public string DistanceLabel => DistanceMeters is { } d ? Localizer.Loc("Poi.DistanceMeters", d) : "";

    /// <summary>
    /// Whether the game listed this exit for the current raid, or null when nobody has told us.
    /// </summary>
    /// <remarks>
    /// Mutable for the same reason as <see cref="DistanceMeters"/>: it belongs to the raid, not to
    /// the exit, and the list is rebuilt whenever it changes.
    /// </remarks>
    public bool? AvailableThisRaid { get; set; }

    /// <summary>Fades exits the current raid does not offer, without removing them.</summary>
    public double ListOpacity => AvailableThisRaid == false ? 0.4 : 1.0;

    /// <summary>
    /// Whether this row is showing its requirements underneath the name.
    /// </summary>
    /// <remarks>
    /// Mutable for the same reason as <see cref="DistanceMeters"/>, and refreshed the same way: the
    /// list is rebuilt when it changes. Per-row rather than global so collapsing the requirements on
    /// one exit does not hide them on the next one you look at.
    /// </remarks>
    public bool DetailsExpanded { get; set; }

    /// <summary>True when there is anything to show under the name at all.</summary>
    public bool HasDetails => Details.Count > 0 || SubtitleLabel.Length > 0;

    /// <summary>Shown only while the row is expanded and there is something to say.</summary>
    public bool ShowDetails => DetailsExpanded && HasDetails;

    /// <summary>
    /// The line under the name: who may use it, or where a transit goes.
    /// </summary>
    /// <remarks>
    /// Empty when the name already carries it. "Transit to Shoreline" does not need "Transit to
    /// Shoreline" written underneath it -- the name of a transit is a complete sentence about where
    /// it goes, and repeating it was pure column width.
    /// </remarks>
    public string SubtitleLabel
    {
        get
        {
            if (DestinationMap is not { } destination)
                return FactionLabel;

            var where = MapNames.Display(destination);

            return Name.Contains(where, StringComparison.OrdinalIgnoreCase)
                ? ""
                : Localizer.Loc("Poi.LeadsTo", where);
        }
    }

    public bool IsExtract => Kind is PoiKind.ExtractPmc or PoiKind.ExtractScav or PoiKind.ExtractShared;

    /// <summary>Extracts usable by both factions, which is how co-op extracts are modeled.</summary>
    public bool IsShared => Kind is PoiKind.ExtractShared;

    /// <summary>Who may use this, in words. Empty for non-extracts.</summary>
    public string FactionLabel => Kind switch
    {
        PoiKind.ExtractPmc => Localizer.Loc("Faction.PmcOnly"),
        PoiKind.ExtractScav => Localizer.Loc("Faction.ScavOnly"),
        PoiKind.ExtractShared => Localizer.Loc("Faction.PmcScav"),
        PoiKind.Transit => Localizer.Loc("Faction.Transit"),
        _ => "",
    };

    public override string ToString() => Name;
}

/// <summary>One line of a POI's detail block, already resolved for the active language.</summary>
/// <remarks>
/// <see cref="IsSecondary"/> marks the faded English echo shown under a Chinese line in bilingual
/// mode; a primary line is rendered in the normal (warning-tinted) style.
/// </remarks>
public sealed record PoiDetailLine(string Text, bool IsSecondary)
{
    public bool IsPrimary => !IsSecondary;
}

