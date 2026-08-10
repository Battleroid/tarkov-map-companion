using SkiaSharp;

namespace TarkovMapCompanion.Rendering;

/// <summary>
/// Finds each label somewhere to sit where no other label is already sitting.
/// </summary>
/// <remarks>
/// <para>
/// One of these is shared by every overlay for the length of one frame, because the problem is not
/// one overlay's: an exit name, a quest name and a teammate's name can land on the same six pixels
/// having each been drawn by code that knows nothing about the other two. Whoever asks first gets
/// the spot they wanted; everybody after that is nudged to the nearest free one.
/// </para>
/// <para>
/// Deliberately greedy rather than optimal. Proper label placement is a hard combinatorial problem
/// and this is a map that redraws twenty times a second next to a game that wants the GPU: a first
/// fit over a fixed ladder of candidates costs a few dozen rectangle intersections per label and
/// is right often enough that the wrong answers are not worth the frame time.
/// </para>
/// <para>
/// The marker keeps its own square reserved as well, so a label never lands on top of a marker
/// that has already been drawn. That is most of the visible improvement on a crowded map; labels
/// colliding with each other is the loud case, labels sitting on markers is the frequent one.
/// </para>
/// </remarks>
public sealed class LabelPlacer
{
    /// <summary>How far a label may be nudged before a line is drawn back to its marker.</summary>
    /// <remarks>
    /// Below this it is still obviously the nearest marker's. Above it, two candidates are equally
    /// plausible and a leader line is the only honest way to say which one it belongs to.
    /// </remarks>
    public const float LeaderAfterPixels = 15f;

    /// <summary>Padding around a reserved box, so labels are separated rather than merely disjoint.</summary>
    private const float Breathing = 2f;

    /// <summary>Half-width of the square kept clear around a marker.</summary>
    private const float MarkerHalf = 7f;

    /// <summary>
    /// Vertical nudges to try, in order, on the right of the marker and then on the left.
    /// </summary>
    /// <remarks>
    /// Zero first, so an uncontested label does not move at all. Then alternating up and down in
    /// steps of about one line, which keeps a displaced label near enough to read as its marker's
    /// even before the leader line is drawn.
    /// </remarks>
    private static readonly float[] Ladder = [0, -13, 13, -26, 26, -39, 39, -52, 52, -65, 65];

    /// <summary>Right of the marker first, then left.</summary>
    private static readonly int[] Sides = [1, -1];

    private readonly List<SKRect> _taken = [];
    private SKRect _bounds;

    /// <summary>Off puts every label exactly where its overlay asked for it.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Clears the frame and sets the area labels have to stay inside.</summary>
    public void BeginFrame(SKRect bounds)
    {
        _taken.Clear();
        _bounds = bounds;
    }

    /// <summary>Keeps an area clear, for something drawn that is not a label.</summary>
    public void Block(SKRect rect) => _taken.Add(Pad(rect));

    /// <summary>
    /// Where to draw a label whose natural home is to the right of a marker.
    /// </summary>
    /// <param name="anchorX">Marker center, x.</param>
    /// <param name="anchorY">Marker center, y.</param>
    /// <param name="gap">Distance from the marker center to where the text should start.</param>
    /// <param name="text">The label.</param>
    /// <param name="paint">Used to measure; not drawn with.</param>
    /// <param name="reserveMarker">
    /// Whether to also keep the marker's own square clear. False for a label whose marker was
    /// already blocked by its overlay.
    /// </param>
    /// <returns>
    /// Where to draw it, or null when there is nowhere left. Null means skip the label: the marker
    /// is still drawn, and hovering it still names it. A name printed through three other names is
    /// worse than no name, and one of the four is going to be unreadable either way.
    /// </returns>
    public LabelSpot? Place(
        float anchorX,
        float anchorY,
        float gap,
        string text,
        SKPaint paint,
        bool reserveMarker = true)
    {
        var width = paint.MeasureText(text);
        var height = paint.TextSize;

        if (!IsEnabled)
            return new LabelSpot(anchorX + gap, anchorY + height * 0.35f, false);

        var marker = new SKRect(
            anchorX - MarkerHalf, anchorY - MarkerHalf,
            anchorX + MarkerHalf, anchorY + MarkerHalf);

        foreach (var side in Sides)
        {
            foreach (var drop in Ladder)
            {
                // Baseline sits a third of the cap height below center, which is what puts text
                // level with a marker rather than hanging off its top.
                var baseline = anchorY + height * 0.35f + drop;

                var left = side > 0
                    ? anchorX + gap
                    : anchorX - gap - width;

                var box = new SKRect(left, baseline - height, left + width, baseline + height * 0.25f);

                if (!Fits(box))
                    continue;

                _taken.Add(Pad(box));

                if (reserveMarker)
                    _taken.Add(Pad(marker));

                // The leader is worth drawing when it has moved vertically, or when it has flipped
                // to the far side of the marker where "the nearest text" is no longer the answer.
                var moved = Math.Abs(drop) >= LeaderAfterPixels || side < 0;

                return new LabelSpot(left, baseline, moved);
            }
        }

        return null;
    }

    /// <summary>
    /// Where to draw a label whose natural home is above its marker.
    /// </summary>
    /// <remarks>
    /// Map notes sit above their dot rather than beside it, because the dot marks a spot somebody
    /// chose and words to the right of it would cover the thing being named. Only the height is
    /// negotiable here; sliding a note sideways would put it over the wrong doorway.
    /// </remarks>
    public LabelSpot? PlaceAbove(float anchorX, float anchorY, float rise, string text, SKPaint paint)
    {
        var width = paint.MeasureText(text);
        var height = paint.TextSize;

        if (!IsEnabled)
            return new LabelSpot(anchorX, anchorY - rise, false);

        foreach (var drop in Ladder)
        {
            // Upward first: the ladder alternates, and above is where a note wants to be.
            var baseline = anchorY - rise + (drop <= 0 ? drop : -drop - rise);
            var box = new SKRect(anchorX, baseline - height, anchorX + width, baseline + height * 0.25f);

            if (!Fits(box))
                continue;

            _taken.Add(Pad(box));
            _taken.Add(Pad(new SKRect(
                anchorX - MarkerHalf, anchorY - MarkerHalf,
                anchorX + MarkerHalf, anchorY + MarkerHalf)));

            return new LabelSpot(anchorX, baseline, Math.Abs(drop) >= LeaderAfterPixels);
        }

        return null;
    }

    private bool Fits(SKRect box)
    {
        if (box.Left < _bounds.Left || box.Right > _bounds.Right
            || box.Top < _bounds.Top || box.Bottom > _bounds.Bottom)
        {
            return false;
        }

        foreach (var taken in _taken)
        {
            if (box.IntersectsWith(taken))
                return false;
        }

        return true;
    }

    private static SKRect Pad(SKRect rect) => new(
        rect.Left - Breathing, rect.Top - Breathing,
        rect.Right + Breathing, rect.Bottom + Breathing);
}

/// <summary>Where a label ended up.</summary>
/// <param name="X">Left edge of the text.</param>
/// <param name="Y">Baseline.</param>
/// <param name="NeedsLeader">Whether it moved far enough to need a line back to its marker.</param>
public readonly record struct LabelSpot(float X, float Y, bool NeedsLeader)
{
    /// <summary>
    /// Draws the line from a displaced label back to the marker it belongs to.
    /// </summary>
    /// <remarks>
    /// From the label's near edge rather than its middle, so the line never crosses its own text,
    /// and stopping short of the marker so it points at it instead of stabbing it.
    /// </remarks>
    public void DrawLeader(SKCanvas canvas, float anchorX, float anchorY, float width, SKPaint paint)
    {
        if (!NeedsLeader)
            return;

        var fromX = X < anchorX ? X + width : X;
        var fromY = Y - paint.TextSize * 0.3f;

        var dx = anchorX - fromX;
        var dy = anchorY - fromY;
        var length = (float)Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1f)
            return;

        const float StopShort = 6f;

        if (length <= StopShort)
            return;

        var scale = (length - StopShort) / length;

        canvas.DrawLine(fromX, fromY, fromX + (dx * scale), fromY + (dy * scale), paint);
    }
}
