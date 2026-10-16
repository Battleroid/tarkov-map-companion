using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Rendering;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The place-names layer: the saved setting has to reach the overlay the session hands to the map.
/// </summary>
public sealed class LandmarkTests
{
    private static MapSession Session(bool showLandmarks)
    {
        var settings = new AppSettings
        {
            AllowNetwork = false,
            CurrentMap = "ground-zero",
            CullMode = CullMode.Off,
            ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-landmarks-noshots"),
            ShowLandmarks = showLandmarks,
        };

        return new MapSession(settings, MapCatalog.LoadEmbedded());
    }

    [Fact]
    public void TheSavedSettingReachesTheOverlay()
    {
        using var on = Session(true);
        Assert.True(on.Landmarks.IsVisible);

        using var off = Session(false);
        Assert.False(off.Landmarks.IsVisible);
    }

    [Fact]
    public void TheOverlayIsPartOfTheSessionList()
    {
        // The list is what the windows add to their canvas, so an overlay outside it is never drawn.
        using var session = Session(true);

        Assert.Contains(session.Landmarks, session.Overlays);
    }

    [Fact]
    public void TheOverlayDrawsTheCurrentMapsLabels()
    {
        // Names are place data straight from the catalog, never routed through the localizer.
        using var session = Session(true);

        Assert.Same(session.CurrentMap, session.Landmarks.Map);
        Assert.NotEmpty(session.Landmarks.Map.Labels);
    }
}
