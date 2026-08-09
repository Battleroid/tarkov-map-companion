using SkiaSharp;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Colors arrive from two places nobody validated: a settings file the README invites people to
/// edit, and, later, a peer over a socket. Neither may throw into a draw call.
/// </summary>
public class ColorCodecTests
{
    [Theory]
    [InlineData("#F5C942")]
    [InlineData("f5c942")]
    [InlineData("  #F5C942  ")]
    [InlineData("#FFF5C942")]
    public void EveryAcceptedSpellingOfOneColorReadsBackTheSame(string text)
    {
        Assert.True(ColorCodec.TryParse(text, out var color));
        Assert.Equal("#F5C942", ColorCodec.ToHex(color));
    }

    [Fact]
    public void ShorthandExpandsEachDigit()
    {
        Assert.True(ColorCodec.TryParse("#F5C", out var color));
        Assert.Equal("#FF55CC", ColorCodec.ToHex(color));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("red")]
    [InlineData("#GGGGGG")]
    [InlineData("#F5C9")]
    [InlineData("#F5C9420000")]
    public void AnythingElseIsRefusedRatherThanGuessedAt(string? text)
    {
        Assert.False(ColorCodec.TryParse(text, out _));
    }

    [Fact]
    public void ARefusedValueBecomesTheFallbackInsteadOfThrowing()
    {
        Assert.Equal(MarkerPalette.Player, ColorCodec.Parse("nonsense", MarkerPalette.Player));
    }

    /// <summary>
    /// A stored alpha would silently fight the transparency the overlays apply for staleness and
    /// floor dimming, and at zero it would hand someone an invisible marker.
    /// </summary>
    [Fact]
    public void AlphaIsAcceptedOnInputButNeverSurvives()
    {
        Assert.True(ColorCodec.TryParse("#00F5C942", out var transparent));
        Assert.Equal(0xFF, transparent.Alpha);

        Assert.Equal(0xFF, ColorCodec.Parse(null, new SKColor(0xF5, 0xC9, 0x42, 0x10)).Alpha);
    }

    [Fact]
    public void EveryOfferedChoiceRoundTripsThroughTheSettingsFile()
    {
        foreach (var (name, color) in MarkerPalette.PlayerChoices)
        {
            var text = ColorCodec.ToHex(color);
            Assert.True(ColorCodec.TryParse(text, out var parsed), name);
            Assert.Equal(color, parsed);
        }
    }

    [Fact]
    public void NormalizeHealsABadColorAndCanonicalizesAGoodOne()
    {
        var settings = new AppSettings { PlayerColor = "not a color", GuideLineColor = "4fc3f7" };

        settings.Normalize();

        Assert.Equal("#F5C942", settings.PlayerColor);
        Assert.Equal("#4FC3F7", settings.GuideLineColor);
    }

    [Theory]
    [InlineData(0.0, 10.0)]
    [InlineData(9.9, 10.0)]
    [InlineData(22.0, 22.0)]
    [InlineData(9999.0, 48.0)]
    public void MarkerSizeIsClampedToSomethingDrawable(double stored, double expected)
    {
        var settings = new AppSettings { PlayerMarkerSize = stored };

        settings.Normalize();

        Assert.Equal(expected, settings.PlayerMarkerSize);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(5, 5)]
    [InlineData(500, 20)]
    public void PeerTrailLengthIsClamped(int stored, int expected)
    {
        var settings = new AppSettings { PeerTrailLength = stored };

        settings.Normalize();

        Assert.Equal(expected, settings.PeerTrailLength);
    }
}
