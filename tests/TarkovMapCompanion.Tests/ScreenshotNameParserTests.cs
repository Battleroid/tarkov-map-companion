using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Screenshots;
using Xunit;

namespace TarkovMapCompanion.Tests;

public sealed class ScreenshotNameParserTests
{
    private const string Real =
        @"C:\shots\2026-08-07[19-31]_-720.10, -48.62, 430.51_-0.03195, 0.66069, -0.02987, -0.74938_11.67 (0).png";

    [Fact]
    public void TryParse_ReadsEveryFieldOutOfARealCapture()
    {
        Assert.True(ScreenshotNameParser.TryParse(Real, out var fix));

        Assert.Equal(-720.10, fix!.Position.X, 6);
        Assert.Equal(-48.62, fix.Position.Y, 6);
        Assert.Equal(430.51, fix.Position.Z, 6);

        Assert.Equal(-0.03195, fix.Rotation.X, 6);
        Assert.Equal(0.66069, fix.Rotation.Y, 6);
        Assert.Equal(-0.02987, fix.Rotation.Z, 6);
        Assert.Equal(-0.74938, fix.Rotation.W, 6);

        Assert.Equal(11.67, fix.RaidTimeHours, 6);
        Assert.Equal(new DateTime(2026, 8, 7, 19, 31, 0), fix.TakenAt);
        Assert.Equal(Real, fix.FilePath);
    }

    [Fact]
    public void RaidTimeDisplay_RendersTheInGameClock()
    {
        // 11.67 hours is 11:40, which is what the reference app stored as 42012 seconds.
        Assert.True(ScreenshotNameParser.TryParse(Real, out var fix));
        Assert.Equal("11:40", fix!.RaidTimeDisplay);
    }

    [Theory]
    // Pure rotation about Y: qy = sin(t/2), qw = cos(t/2), so t = 2*asin(0.99758) = 172.03 degrees.
    [InlineData(0.0, 0.99758, 0.0, 0.06950, 172.03)]
    // The double-cover case: the game writes both q and -q for the same orientation.
    [InlineData(-0.00552, 0.98017, -0.01613, -0.19740, 202.76)]
    [InlineData(0.0, 0.0, 0.0, 1.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0, 0.0, 180.0)]
    // qy = qw = sin(45) gives a quarter turn.
    [InlineData(0.0, 0.70711, 0.0, 0.70711, 90.0)]
    public void YawFromQuaternion_MatchesTheValuesVerifiedAgainstRealCaptures(
        double qx, double qy, double qz, double qw, double expected)
    {
        Assert.Equal(expected, ScreenshotNameParser.YawFromQuaternion(qx, qy, qz, qw), 2);
    }

    [Fact]
    public void YawFromQuaternion_AgreesWithTheDirectionThePlayerActuallyWalked()
    {
        // Two consecutive real captures 20 seconds apart. The player moved almost due -Z;
        // the yaw decoded from the second one must point the same way.
        var from = new GamePosition(-183.70, -45.27, 324.72);
        var to = new GamePosition(-184.11, -45.26, 319.41);

        var yaw = ScreenshotNameParser.YawFromQuaternion(-0.00552, 0.98017, -0.01613, -0.19740);
        var travelBearing = MapProjection.BearingDegrees(from, to);

        // Walking forward is not perfectly aligned with facing, but it should be well within a
        // quadrant -- a sign or axis error would show up as roughly 90 or 180 degrees off.
        Assert.True(
            Math.Abs(MapProjection.NormalizeSigned(yaw - travelBearing)) < 45,
            $"yaw {yaw:F1} disagrees with travel bearing {travelBearing:F1}");
    }

    [Fact]
    public void TryParse_AcceptsANameWithoutTheDuplicateCounter()
    {
        var path = @"C:\s\2026-08-07[17-12]_-206.52, 5.67, -290.56_0.02804, 0.62613, -0.02254, 0.77889_7.47.png";

        Assert.True(ScreenshotNameParser.TryParse(path, out var fix));
        Assert.Equal(7.47, fix!.RaidTimeHours, 6);
    }

    [Fact]
    public void TryParse_AcceptsASecondsComponentInTheTimestamp()
    {
        var path = @"C:\s\2026-08-07[17-12-45]_1.0, 2.0, 3.0_0, 0, 0, 1_7.47 (0).png";

        Assert.True(ScreenshotNameParser.TryParse(path, out var fix));
        Assert.Equal(new DateTime(2026, 8, 7, 17, 12, 45), fix!.TakenAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("screenshot.png")]
    [InlineData("2026-08-07[19-31].png")]
    // Missing the raid-time field.
    [InlineData("2026-08-07[19-31]_1, 2, 3_0, 0, 0, 1.png")]
    // Three rotation components instead of four.
    [InlineData("2026-08-07[19-31]_1, 2, 3_0, 0, 1_7.47.png")]
    // Two position components instead of three.
    [InlineData("2026-08-07[19-31]_1, 2_0, 0, 0, 1_7.47.png")]
    // Impossible clock time.
    [InlineData("2026-08-07[29-31]_1, 2, 3_0, 0, 0, 1_7.47.png")]
    [InlineData("2026-13-45[19-31]_1, 2, 3_0, 0, 0, 1_7.47.png")]
    // Right shape, wrong file type.
    [InlineData("2026-08-07[19-31]_1, 2, 3_0, 0, 0, 1_7.47.txt")]
    // Trailing junk after the counter.
    [InlineData("2026-08-07[19-31]_1, 2, 3_0, 0, 0, 1_7.47 (0) copy.png")]
    public void TryParse_RejectsAnythingThatIsNotAScreenshotName(string name)
    {
        Assert.False(ScreenshotNameParser.TryParse(name, out var fix));
        Assert.Null(fix);
    }

    [Fact]
    public void TryParse_HandlesNegativeCoordinatesInEveryPosition()
    {
        var path = @"C:\s\2026-08-07[19-31]_-1.5, -2.5, -3.5_-0.1, -0.2, -0.3, -0.9_-1.25.png";

        Assert.True(ScreenshotNameParser.TryParse(path, out var fix));
        Assert.Equal(-1.5, fix!.Position.X, 6);
        Assert.Equal(-3.5, fix.Position.Z, 6);
    }

    [Fact]
    public void IsScreenshotFileName_IsStrictEnoughToGuardDeletion()
    {
        // This gate is what stops the culler touching files it does not own.
        Assert.True(ScreenshotNameParser.IsScreenshotFileName(Real));

        Assert.False(ScreenshotNameParser.IsScreenshotFileName(@"C:\shots\important-notes.png"));
        Assert.False(ScreenshotNameParser.IsScreenshotFileName(@"C:\shots\2026-08-07 screenshot.png"));
        Assert.False(ScreenshotNameParser.IsScreenshotFileName(@"C:\shots\config.json"));
        Assert.False(ScreenshotNameParser.IsScreenshotFileName(""));
    }

    /// <summary>
    /// Runs against the real folder when it is present. Skipped on machines without it so the
    /// suite stays portable, but on Casey's box this is the check that actually matters.
    /// </summary>
    [Fact]
    public void EveryRealScreenshotInTheTarkovFolderParses()
    {
        var folder = TarkovMapCompanion.Settings.AppSettings.DefaultScreenshotFolder();
        if (!Directory.Exists(folder))
            return;

        var pngs = Directory.GetFiles(folder, "*.png");
        if (pngs.Length == 0)
            return;

        // Tarkov leaves the coordinates out entirely when you are not in a raid: a screenshot from
        // the menu or the hideout is just a date, a time and a frozen clock. Those are real Tarkov
        // screenshots that genuinely cannot be placed, so the parser is right to refuse them and
        // this test has to tell the two cases apart rather than demanding everything parse.
        var positional = pngs.Where(p => Path.GetFileName(p).Contains(", ", StringComparison.Ordinal)).ToArray();
        var failures = positional.Where(p => !ScreenshotNameParser.TryParse(p, out _)).ToArray();

        Assert.True(
            failures.Length == 0,
            $"{failures.Length} of {positional.Length} in-raid screenshots failed to parse:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Select(Path.GetFileName)));
    }

    [Fact]
    public void AScreenshotTakenOutsideARaidIsRefusedRatherThanMisread()
    {
        // Seen on a real machine: eleven of twenty-one files looked like this, all sharing one
        // frozen clock value. Guessing a position for them would put a marker somewhere arbitrary.
        Assert.False(ScreenshotNameParser.TryParse(@"C:\s\2026-08-08[23-06]_11.05 (3).png", out _));
        Assert.False(ScreenshotNameParser.TryParse(@"C:\s\2026-08-08[21-05]_10.68 (0).png", out _));
    }
}
