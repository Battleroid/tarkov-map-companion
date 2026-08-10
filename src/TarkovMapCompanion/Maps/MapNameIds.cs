namespace TarkovMapCompanion.Maps;

/// <summary>
/// Turns the names Tarkov calls its own maps into the maps this app renders.
/// </summary>
/// <remarks>
/// <para>
/// Two different vocabularies end up here. tarkov.dev records a <c>nameId</c> per map, which is
/// BSG's internal location id and is exactly what the game log's <c>Location:</c> field carries.
/// The log's earlier signal, <c>scene preset path:</c>, carries something else: a bundle name and a
/// resource id that agree with the <c>nameId</c> for some maps and not others.
/// </para>
/// <para>
/// Measured across every log on the development machine, the scene line says <c>bigmap</c> for
/// Customs (a real nameId) but <c>Shopping_Mall</c> for Interchange, <c>factory_day</c> rather than
/// <c>factory4_day</c>, and <c>Sandbox_SL</c> rather than <c>Sandbox_start</c>. So the scene line
/// cannot be treated as a nameId, which is the trap this class exists to absorb: every candidate
/// token is offered here, and anything that does not resolve is reported rather than guessed at.
/// </para>
/// <para>
/// Variants fold onto the map that is actually shipped. Night Factory, Ground Zero's level-gated and
/// tutorial versions and the dark Lab are separate tarkov.dev maps but the same geometry, so their
/// markers belong on the base map rather than on nothing.
/// </para>
/// </remarks>
public static class MapNameIds
{
    /// <summary>
    /// tarkov.dev's <c>nameId</c> for each upstream map, mapped to the shipped map it draws on.
    /// </summary>
    /// <remarks>
    /// Pinned by a test against the embedded snapshot, so a Tarkov patch that renames a location
    /// fails loudly here instead of quietly declining to switch maps.
    /// </remarks>
    private static readonly Dictionary<string, string> ByNameId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory4_day"] = "factory",
        ["factory4_night"] = "factory",
        ["bigmap"] = "customs",
        ["Woods"] = "woods",
        ["Lighthouse"] = "lighthouse",
        ["Shoreline"] = "shoreline",
        ["RezervBase"] = "reserve",
        ["Interchange"] = "interchange",
        ["TarkovStreets"] = "streets-of-tarkov",
        ["laboratory"] = "the-lab",
        ["laboratory_dark"] = "the-lab",
        ["Sandbox"] = "ground-zero",
        ["Sandbox_high"] = "ground-zero",
        ["Sandbox_start"] = "ground-zero",
        ["Terminal"] = "terminal",
        ["Labyrinth"] = "the-labyrinth",
        ["Icebreaker"] = "icebreaker",
    };

    /// <summary>
    /// Scene tokens that are neither a <c>nameId</c> nor a map's normalized name.
    /// </summary>
    /// <remarks>
    /// Every entry was read out of a real log rather than inferred. Deliberately short: a wrong
    /// entry here switches somebody's map mid-raid, which is worse than not switching at all, and
    /// the authoritative <c>Location:</c> line follows within a minute either way.
    /// </remarks>
    private static readonly Dictionary<string, string> SceneAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["factory_day"] = "factory",
        ["factory_night"] = "factory",
        ["shopping_mall"] = "interchange",
        ["sandbox_sl"] = "ground-zero",

        // Streets. Found by --find-logs after a real raid: the scene is called "city" and nothing
        // else in the data is, so the map only switched when the authoritative line arrived five
        // minutes later. Exactly the gap the diagnostic exists to surface.
        ["city"] = "streets-of-tarkov",
    };

    /// <summary>
    /// The normalized name of the map <paramref name="token"/> refers to, or null.
    /// </summary>
    /// <remarks>
    /// A token that is already a map's normalized name is not handled here; the catalog resolves
    /// that directly, which is what makes a scene token such as <c>customs</c> work without an
    /// entry of its own and keeps this table to the genuine oddities.
    /// </remarks>
    public static string? NormalizedNameFor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var trimmed = token.Trim();

        if (ByNameId.TryGetValue(trimmed, out var byId))
            return byId;

        return SceneAliases.TryGetValue(trimmed, out var alias) ? alias : null;
    }

    /// <summary>Every known <c>nameId</c>, for the pinning test and for diagnostics.</summary>
    public static IReadOnlyCollection<string> KnownNameIds => ByNameId.Keys;
}
