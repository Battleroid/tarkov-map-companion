namespace TarkovMapCompanion.Screenshots;

/// <summary>
/// Splits a stream of position fixes into separate raids.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the map draws one continuous trail through every screenshot ever taken, stitching
/// last night's Customs run to this morning's. Raids are also bounded -- most run 40 to 50 minutes --
/// so anything older than that cannot belong to the raid in progress.
/// </para>
/// <para>
/// The screenshot name gives us two clocks, and their relationship is the tell: Tarkov's in-raid
/// clock advances at <see cref="GameClockRate"/> times real time, so within a single raid
/// <c>Δraid ≈ 7 × Δwall</c>. Verified against real captures:
/// </para>
/// <list type="bullet">
///   <item><description>19:19 → 19:20, 20 s apart, raid 10.33 → 10.37 h: 144 s of game time, 7.2x.</description></item>
///   <item><description>19:20 → 19:29, 9 min apart, raid 10.37 → 11.43 h: 3816 s, 7.07x.</description></item>
///   <item><description>18:17 → 19:19, raid 15.07 → 10.33 h: the clock runs <em>backwards</em>, so
///     these are plainly different raids.</description></item>
/// </list>
/// <para>
/// The wall clock in a filename only has minute resolution, so two timestamps a stated <c>n</c>
/// seconds apart are really anywhere in <c>n ± 120</c> s. The tolerance below accounts for that
/// rather than pretending the timestamps are exact.
/// </para>
/// </remarks>
public static class RaidSession
{
    /// <summary>How fast Tarkov's in-raid clock runs relative to real time.</summary>
    public const double GameClockRate = 7.0;

    /// <summary>
    /// Uncertainty in a wall-clock delta, in seconds. Filenames carry <c>[HH-mm]</c>, so each of
    /// the two timestamps can be off by up to a minute in either direction.
    /// </summary>
    private const double WallClockSlackSeconds = 120.0;

    /// <summary>Extra slack in game-seconds, absorbing rounding in the two-decimal raid clock.</summary>
    private const double GameClockSlackSeconds = 300.0;

    /// <summary>
    /// Longest a raid can run before we stop treating new fixes as part of it. Deliberately
    /// generous: the real ceiling varies by map (Factory is 20 minutes, Streets 50) and comes from
    /// the tarkov.dev API, which is optional. Overrunning is harmless; cutting a raid short is not.
    /// </summary>
    public static readonly TimeSpan DefaultMaxRaidLength = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Whether <paramref name="next"/> belongs to the same raid as <paramref name="previous"/>.
    /// </summary>
    public static bool IsSameRaid(PlayerFix previous, PlayerFix next, TimeSpan? maxRaidLength = null)
    {
        var wallSeconds = (next.TakenAt - previous.TakenAt).TotalSeconds;

        // Out-of-order arrivals are not a raid boundary in themselves; compare on magnitude.
        if (wallSeconds < 0)
            return false;

        if (wallSeconds > (maxRaidLength ?? DefaultMaxRaidLength).TotalSeconds)
            return false;

        var gameSeconds = (next.RaidTimeHours - previous.RaidTimeHours) * 3600.0;

        // A raid clock that goes backwards is the clearest signal there is.
        var lowerBound = GameClockRate * Math.Max(0, wallSeconds - WallClockSlackSeconds) - GameClockSlackSeconds;
        var upperBound = GameClockRate * (wallSeconds + WallClockSlackSeconds) + GameClockSlackSeconds;

        return gameSeconds >= lowerBound && gameSeconds <= upperBound;
    }

    /// <summary>
    /// Splits fixes into raids, oldest first within each. Input need not be sorted.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<PlayerFix>> Split(
        IEnumerable<PlayerFix> fixes,
        TimeSpan? maxRaidLength = null)
    {
        var ordered = InChronologicalOrder(fixes);

        var raids = new List<IReadOnlyList<PlayerFix>>();
        var current = new List<PlayerFix>();

        foreach (var fix in ordered)
        {
            if (current.Count > 0 && !IsSameRaid(current[^1], fix, maxRaidLength))
            {
                raids.Add(current);
                current = [];
            }

            current.Add(fix);
        }

        if (current.Count > 0)
            raids.Add(current);

        return raids;
    }

    /// <summary>
    /// Orders fixes by capture time.
    /// </summary>
    /// <remarks>
    /// The wall clock in a filename has only minute resolution, so several shots routinely share a
    /// timestamp -- Casey's 17:52 captures are three separate moments. The in-raid clock has finer
    /// resolution and breaks those ties correctly; sorting on the filename alone reverses them.
    /// </remarks>
    public static IReadOnlyList<PlayerFix> InChronologicalOrder(IEnumerable<PlayerFix> fixes) =>
        fixes
            .OrderBy(f => f.TakenAt)
            .ThenBy(f => f.RaidTimeHours)
            .ToArray();

    /// <summary>
    /// How long the raid has been running at <paramref name="fix"/>, measured in real time from
    /// the first fix of its raid.
    /// </summary>
    public static TimeSpan ElapsedIn(IReadOnlyList<PlayerFix> raid, PlayerFix fix)
    {
        if (raid.Count == 0)
            return TimeSpan.Zero;

        // Real elapsed time from the in-raid clock, which is finer than the minute-resolution
        // wall clock and is not affected by the app being started mid-raid.
        var gameHours = fix.RaidTimeHours - raid[0].RaidTimeHours;
        return TimeSpan.FromHours(gameHours / GameClockRate);
    }
}
