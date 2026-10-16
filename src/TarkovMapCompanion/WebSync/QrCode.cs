using QRCoder;

namespace TarkovMapCompanion.WebSync;

/// <summary>Renders a QR code for the WebSync access URL.</summary>
internal static class QrCode
{
    /// <summary>
    /// Renders <paramref name="text"/> as a PNG image, or returns <c>null</c> when the text is empty.
    /// Uses QRCoder's PNG encoder (MIT, pure managed) so nothing here touches Skia.
    /// </summary>
    /// <param name="text">The URL to encode; null or empty yields <c>null</c>.</param>
    /// <returns>The PNG bytes, or <c>null</c> when <paramref name="text"/> is empty.</returns>
    public static byte[]? RenderPng(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // 8 pixels per module: big enough to scan off a screen at arm's length, small enough that a
        // long URL stays a few hundred pixels wide. The renderer keeps its default 4-module quiet
        // zone, which the scanner needs to find the code's edges. ECC level M tolerates screen glare.
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
    }
}
