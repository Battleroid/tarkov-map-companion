using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Loading the screenshot pixels for the exit reader.
/// </summary>
/// <remarks>
/// The folder watcher fires the moment a file appears, and Tarkov's screenshots are several
/// megabytes, so the first look very often catches one half written. Positions never needed this
/// because the filename is complete from the start; the pixels are not.
/// </remarks>
public sealed class ScreenshotImageReadTests : IDisposable
{
    private static readonly byte[] PngTrailer =
        [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    private readonly string _folder;

    public ScreenshotImageReadTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tmc-image", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static byte[] Png(bool complete)
    {
        var bytes = new byte[64];

        // Signature, then filler, then the closing chunk that marks a finished file.
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);

        if (!complete)
            return bytes[..40];

        PngTrailer.CopyTo(bytes, bytes.Length - PngTrailer.Length);
        return bytes;
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void ReadsAFinishedFile()
    {
        var path = Write("done.png", Png(complete: true));

        var bytes = MapSession.TryReadImage(path);

        Assert.NotNull(bytes);
        Assert.Equal(64, bytes.Length);
    }

    [Fact]
    public void RefusesAFileTheGameHasNotFinishedWriting()
    {
        // Half a PNG decodes to nothing useful, and treating it as a failed read of the exit panel
        // would quietly mean "no exits found" on the very screenshot taken to show them.
        var path = Write("partial.png", Png(complete: false));

        Assert.Null(MapSession.TryReadImage(path));
    }

    [Fact]
    public void ReturnsNullForAFileThatIsNotThere()
    {
        Assert.Null(MapSession.TryReadImage(Path.Combine(_folder, "never-existed.png")));
    }

    [Fact]
    public void ReadsAFileThatIsStillOpenForWriting()
    {
        // Culling and the game itself can both have the file open; a sharing violation must not be
        // the reason a read fails.
        var path = Write("open.png", Png(complete: true));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.NotNull(MapSession.TryReadImage(path));
    }
}
