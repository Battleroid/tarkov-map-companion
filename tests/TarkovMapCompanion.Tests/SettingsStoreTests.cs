using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tmc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var settings = new SettingsStore(_path).Load();

        Assert.Equal(CullMode.Off, settings.CullMode);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(14.0, settings.FontSize);
        Assert.True(settings.CullToRecycleBin);
        Assert.False(settings.AutoSwitchMap);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryFieldWeCareAbout()
    {
        var store = new SettingsStore(_path);

        var original = new AppSettings
        {
            ScreenshotFolder = @"D:\shots",
            CullMode = CullMode.KeepLatest,
            CullKeepCount = 42,
            CurrentMap = "woods",
            DefaultZoom = 3.5,
            SelectedExtractId = "extract-123",
            ExtractFocusMode = true,
            ExtractFocusPadding = 0.3,
            ShowHeatmap = true,
            HeatmapRadiusMeters = 65,
            HistoryTrailLength = 30,
            Theme = AppTheme.Light,
            FontSize = 18,
            AlwaysOnTop = true,
            Window = new WindowPlacement { X = 100, Y = 200, Width = 900, Height = 700, Maximized = true },
        };
        original.PoiLayers["Hazard"] = true;
        original.HeatmapCategories["Boss"] = true;

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(@"D:\shots", loaded.ScreenshotFolder);
        Assert.Equal(CullMode.KeepLatest, loaded.CullMode);
        Assert.Equal(42, loaded.CullKeepCount);
        Assert.Equal("woods", loaded.CurrentMap);
        Assert.Equal(3.5, loaded.DefaultZoom);
        Assert.Equal("extract-123", loaded.SelectedExtractId);
        Assert.True(loaded.ExtractFocusMode);
        Assert.Equal(0.3, loaded.ExtractFocusPadding);
        Assert.True(loaded.ShowHeatmap);
        Assert.Equal(65, loaded.HeatmapRadiusMeters);
        Assert.Equal(30, loaded.HistoryTrailLength);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(18, loaded.FontSize);
        Assert.True(loaded.AlwaysOnTop);
        Assert.True(loaded.PoiLayers["Hazard"]);
        Assert.True(loaded.HeatmapCategories["Boss"]);

        Assert.NotNull(loaded.Window);
        Assert.Equal(900, loaded.Window!.Width);
        Assert.True(loaded.Window.Maximized);
    }

    [Fact]
    public void Save_WritesEnumsAsNamesSoTheFileStaysHandEditable()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { Theme = AppTheme.Light, CullMode = CullMode.DeleteAfterRead });

        var json = File.ReadAllText(_path);

        Assert.Contains("\"Light\"", json);
        Assert.Contains("\"DeleteAfterRead\"", json);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        File.WriteAllText(_path, """
            {
              // hand-edited
              "currentMap": "reserve",
              "fontSize": 16,
            }
            """);

        var loaded = new SettingsStore(_path).Load();

        Assert.Equal("reserve", loaded.CurrentMap);
        Assert.Equal(16, loaded.FontSize);
    }

    [Fact]
    public void Load_WithPartialFile_FillsMissingKeysWithDefaults()
    {
        File.WriteAllText(_path, """{ "currentMap": "lighthouse" }""");

        var loaded = new SettingsStore(_path).Load();

        Assert.Equal("lighthouse", loaded.CurrentMap);
        Assert.Equal(CullMode.Off, loaded.CullMode);
        Assert.Equal(14.0, loaded.FontSize);
        Assert.NotEmpty(loaded.PoiLayers);
    }

    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaultsAndKeepsTheBrokenFile()
    {
        File.WriteAllText(_path, "{ this is not json");

        var loaded = new SettingsStore(_path).Load();

        Assert.Equal("customs", loaded.CurrentMap);
        Assert.True(File.Exists(_path + ".corrupt"), "the unreadable file should be moved aside, not discarded");
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData(0)]        // a zero keep-count would mean "delete everything"
    [InlineData(-5)]
    [InlineData(50_000)]
    public void Normalize_ClampsCullKeepCountIntoRange(int given)
    {
        var settings = new AppSettings { CullKeepCount = given };
        settings.Normalize();

        Assert.InRange(settings.CullKeepCount, 1, 10_000);
    }

    [Fact]
    public void Normalize_KeepsZoomBoundsOrdered()
    {
        var settings = new AppSettings { MinZoom = 10, MaxZoom = 2, DefaultZoom = 0.001 };
        settings.Normalize();

        Assert.True(settings.MaxZoom > settings.MinZoom);
        Assert.InRange(settings.DefaultZoom, settings.MinZoom, settings.MaxZoom);
    }

    [Fact]
    public void Normalize_RestoresBlankScreenshotFolder()
    {
        var settings = new AppSettings { ScreenshotFolder = "   " };
        settings.Normalize();

        Assert.Equal(AppSettings.DefaultScreenshotFolder(), settings.ScreenshotFolder);
    }

    [Fact]
    public void DefaultScreenshotFolder_PointsAtTheTarkovFolder()
    {
        var folder = AppSettings.DefaultScreenshotFolder();

        Assert.EndsWith(Path.Combine("Escape from Tarkov", "Screenshots"), folder);
    }

    /// <summary>
    /// The exit filter's control moved into Settings, so a narrowed one is reset once on the way in.
    /// </summary>
    /// <remarks>
    /// Without this, somebody who had picked "Running as Scav" while the dropdown was in the panel
    /// opens the new build to eight missing exits and nothing on screen saying why.
    /// </remarks>
    [Fact]
    public void Migrate_ShowsEveryExitOnceWhenComingFromVersionOne()
    {
        var settings = new AppSettings { Version = 1, ExitFilter = ExitFilter.AsScav };
        settings.Migrate();

        Assert.Equal(ExitFilter.All, settings.ExitFilter);
        Assert.Equal(AppSettings.Current, settings.Version);
    }

    /// <summary>Once migrated, the preference is the user's again.</summary>
    [Fact]
    public void Migrate_LeavesACurrentFileAlone()
    {
        var settings = new AppSettings { Version = AppSettings.Current, ExitFilter = ExitFilter.AsScav };
        settings.Migrate();

        Assert.Equal(ExitFilter.AsScav, settings.ExitFilter);
    }

    /// <summary>A new file is already current and never sees the migration.</summary>
    [Fact]
    public void ANewSettingsFileIsAtTheCurrentVersion()
    {
        Assert.Equal(AppSettings.Current, new AppSettings().Version);
    }
}
