using Avalonia.Threading;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// An overlay whose appearance changes with time rather than with input.
/// </summary>
public interface IAnimatedOverlay : IMapOverlay
{
    /// <summary>
    /// Folds in the passage of time, and says whether anything is still moving.
    /// </summary>
    /// <remarks>
    /// Returning false is a promise that this overlay will look identical next frame, and the clock
    /// takes it literally: once every source says no, repainting stops until something wakes it.
    /// Anything that starts moving again on its own -- rather than in response to an event that can
    /// call <see cref="MapClock.Wake"/> -- must keep returning true.
    /// </remarks>
    bool Advance();
}

/// <summary>
/// One repaint timer for every overlay that animates, which stops as soon as none of them do.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a timer that existed solely for pings. A second animated overlay would have meant
/// a second timer with its own start and stop conditions, two of them independently invalidating
/// the same canvas, and no single place that could answer "should anything be redrawing right now".
/// </para>
/// <para>
/// Stopping matters more here than in most apps. This one sits on a second monitor for hours next
/// to a game that wants the GPU, so a map with nothing happening on it has to cost nothing. That is
/// also why <see cref="Suspended"/> exists: a minimized window animating at twenty frames a second
/// is pure waste, and nothing on screen suffers for having missed the frames.
/// </para>
/// <para>
/// The clock's only job is to cause frames. No animation's correctness may depend on how many it
/// caused -- every animated overlay derives its phase from wall time, so a dropped frame under load
/// shows up as a stutter rather than as an animation that has silently fallen behind.
/// </para>
/// </remarks>
public sealed class MapClock : IDisposable
{
    /// <summary>20 fps. Enough for a pulse to read as motion, cheap enough to leave running.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    private readonly List<IAnimatedOverlay> _sources = [];
    private readonly Action _invalidate;
    private readonly DispatcherTimer _timer;

    public MapClock(Action invalidate)
    {
        _invalidate = invalidate;
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Render, (_, _) => Tick());
        _timer.Stop();
    }

    /// <summary>
    /// Set while the window is minimized. Frames nobody can see are not worth the wake-ups.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>True while the timer is running. Exposed for diagnostics and tests.</summary>
    public bool IsRunning => _timer.IsEnabled;

    public void Register(IAnimatedOverlay source) => _sources.Add(source);

    /// <summary>
    /// Starts the clock if anything wants frames. Call after whatever might have started moving.
    /// </summary>
    public void Wake()
    {
        if (Suspended || _timer.IsEnabled || _sources.Count == 0)
            return;

        _timer.Start();
    }

    /// <summary>
    /// Whether a set of sources still needs frames.
    /// </summary>
    /// <remarks>
    /// Separated from the timer so the stopping rule can be tested without a dispatcher, which is
    /// the part worth pinning: a rule that never stops wastes a laptop battery, and one that stops
    /// too eagerly freezes a pulse halfway through with no obvious cause.
    /// </remarks>
    internal static bool ShouldKeepRunning(IReadOnlyList<IAnimatedOverlay> sources)
    {
        // Deliberately not short-circuiting. Advance() is how an overlay retires expired state, so
        // every source has to get the call even once one of them has already asked for more frames.
        var wanted = false;

        foreach (var source in sources)
        {
            var moving = source.Advance();
            wanted |= moving && source.IsVisible;
        }

        return wanted;
    }

    private void Tick()
    {
        var keepGoing = ShouldKeepRunning(_sources);

        _invalidate();

        // One last frame after the final Advance, so whatever just expired actually leaves the
        // screen rather than staying painted until the next unrelated repaint.
        if (!keepGoing || Suspended)
            _timer.Stop();
    }

    public void Dispose() => _timer.Stop();
}
