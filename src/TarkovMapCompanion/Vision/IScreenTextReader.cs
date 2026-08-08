namespace TarkovMapCompanion.Vision;

/// <summary>
/// Reads text out of a screenshot that is already in memory.
/// </summary>
/// <remarks>
/// <para>
/// Takes bytes rather than a path on purpose. Culling can send a screenshot to the Recycle Bin the
/// instant its position has been read, so whoever wants to look at the pixels has to take a copy
/// first; handing the reader a path would make the two features race.
/// </para>
/// <para>
/// An interface mostly so the panel parsing can be tested without an OCR engine. The parsing is
/// where the decisions live, and it should not need a particular Windows build to run.
/// </para>
/// </remarks>
public interface IScreenTextReader
{
    /// <summary>False when this machine has no usable OCR engine; the feature then stays off.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the reader is unavailable, phrased for the status bar. Null when it works.</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<OcrLine>> ReadAsync(
        byte[] image,
        RelativeRegion region,
        CancellationToken cancellationToken = default);
}
