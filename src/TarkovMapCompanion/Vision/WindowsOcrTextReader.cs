using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TarkovMapCompanion.Vision;

/// <summary>
/// Reads text using the OCR engine built into Windows.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over bundling Tesseract because it adds nothing to the download and needs no native
/// libraries of our own. Measured on a real 2560x1440 Tarkov frame it reads the extraction panel
/// in about 25 ms, and the only character it got wrong was the U in "RUAF", which the fuzzy match
/// absorbs.
/// </para>
/// <para>
/// The engine needs an OCR language pack. English is present on a stock en-US Windows, but that is
/// not guaranteed everywhere, so construction never throws: it records why it could not start and
/// the feature reports itself unavailable rather than failing later, one screenshot at a time.
/// </para>
/// </remarks>
public sealed class WindowsOcrTextReader : IScreenTextReader
{
    private readonly OcrEngine? _engine;

    public WindowsOcrTextReader()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Reading exits from screenshots needs Windows.";
            return;
        }

        try
        {
            // Prefer English: the extract names we match against are the English ones. Fall back to
            // whatever the user has so a non-English install still gets a chance.
            _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                      ?? OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"OCR engine unavailable: {ex.Message}");
            UnavailableReason = "Windows OCR could not be started.";
            return;
        }

        if (_engine is null)
        {
            UnavailableReason =
                "Windows has no OCR language pack installed. Add English under "
                + "Settings > Time & language > Language & region.";
        }
    }

    public bool IsAvailable => _engine is not null;

    public string? UnavailableReason { get; }

    public async Task<IReadOnlyList<OcrLine>> ReadAsync(
        byte[] image,
        RelativeRegion region,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null || image.Length == 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        await WriteAsync(stream, image, cancellationToken).ConfigureAwait(false);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        var (x, y, width, height) = region.ToPixels((int)decoder.PixelWidth, (int)decoder.PixelHeight);

        // Crop during decode rather than after: the panel is a fraction of the frame, and there is
        // no reason to materialize eight megapixels to read eight rows of text.
        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds
            {
                X = (uint)x,
                Y = (uint)y,
                Width = (uint)width,
                Height = (uint)height,
            },
        };

        using var bitmap = await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

        var lines = new List<OcrLine>(result.Lines.Count);

        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text))
                continue;

            // A recognized line carries no box of its own, only its words do.
            TextBox? box = null;
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                var wordBox = new TextBox(rect.X, rect.Y, rect.Width, rect.Height);
                box = box?.Union(wordBox) ?? wordBox;
            }

            // Back into full-frame coordinates so callers are not left reasoning about the crop.
            lines.Add(new OcrLine(line.Text, box!.Value.Offset(x, y)));
        }

        return lines;
    }

    private static async Task WriteAsync(
        IRandomAccessStream stream,
        byte[] image,
        CancellationToken cancellationToken)
    {
        var writer = new DataWriter(stream);
        try
        {
            writer.WriteBytes(image);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Detach before disposing, otherwise the writer takes the stream down with it and the
            // decoder gets a closed handle.
            writer.DetachStream();
            writer.Dispose();
        }

        stream.Seek(0);
    }
}
