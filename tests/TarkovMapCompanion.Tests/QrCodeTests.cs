using TarkovMapCompanion.WebSync;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Pins the WebSync QR helper: a real PNG for a real URL, nothing for an empty one, and a different
/// image when the URL changes.
/// </summary>
public sealed class QrCodeTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void AUrlRendersToAPng()
    {
        var png = QrCode.RenderPng("https://example/");

        Assert.NotNull(png);
        Assert.NotEmpty(png!);
        Assert.Equal(PngSignature, png![..PngSignature.Length]);
    }

    [Fact]
    public void EmptyTextRendersNothing()
    {
        Assert.Null(QrCode.RenderPng(string.Empty));
    }

    [Fact]
    public void DifferentUrlsRenderDifferentImages()
    {
        var first = QrCode.RenderPng("https://example/a")!;
        var second = QrCode.RenderPng("https://example/b")!;

        Assert.NotEqual(first, second);
    }
}
