using SkiaSharp;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Supplies the base map imagery for one map. Implemented once over tarkov.dev's vector maps and
/// once over their raster tile pyramids, because neither covers all thirteen maps on its own.
/// </summary>
public interface IMapImageSource : IDisposable
{
    /// <summary>Human-readable source name for the About screen and the imagery picker.</summary>
    string Name { get; }

    /// <summary>True once there is something to draw. Until then <see cref="Draw"/> is a no-op.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Fetches whatever the source needs before it can draw. Safe to call more than once; later
    /// calls return the same completed work.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Draws the map into the current clip. The canvas is in screen space; use
    /// <paramref name="viewport"/> to place base-space geometry.
    /// </summary>
    /// <param name="activeFloorNames">Floors the user has switched on, by <c>MapFloor.Name</c>.</param>
    /// <param name="includeBase">
    /// Whether to draw the ground level. Turning it off is what makes an underground floor
    /// visible, since the artwork stacks floors as opaque geometry.
    /// </param>
    void Draw(
        SKCanvas canvas,
        Viewport viewport,
        IReadOnlyCollection<string> activeFloorNames,
        bool includeBase = true);

    /// <summary>Raised when previously unavailable imagery has arrived and a repaint is worthwhile.</summary>
    event EventHandler? Invalidated;
}
