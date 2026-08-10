using TarkovMapCompanion.Data;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// The quest snapshot, and turning it into marks on the map in front of you.
/// </summary>
public sealed class QuestTests
{
    private static AppSettings Settings(params string[] tracked) => new()
    {
        AllowNetwork = false,
        CurrentMap = "customs",
        CullMode = CullMode.Off,
        ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-quests-noshots"),
        TrackedTasks = [.. tracked],
    };

    private static MapSession NewSession(AppSettings settings)
    {
        var session = new MapSession(settings, MapCatalog.LoadEmbedded());

        // StartAsync would also fetch imagery. The stores are what these tests need.
        session.MapData.LoadLocal();
        session.Tasks.LoadLocal();

        return session;
    }

    [Fact]
    public void TheSnapshotLoads()
    {
        var snapshot = TaskStore.EmbeddedSnapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Tasks.Count > 400, $"only {snapshot.Tasks.Count} tasks in the snapshot");
    }

    /// <summary>
    /// Every name came through the translation file.
    /// </summary>
    /// <remarks>
    /// Upstream stores names as localization keys and the projection resolves them. An unresolved
    /// one comes through as its own key, which looks like <c>657315ddab5a49b71f098853 name</c> and
    /// would be shipped without anything else complaining.
    /// </remarks>
    [Fact]
    public void EveryTaskHasARealName()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        var unresolved = snapshot.Tasks
            .Where(t => t.Name.EndsWith(" name", StringComparison.Ordinal) || t.Name.Length == 0)
            .Select(t => t.Name)
            .ToArray();

        Assert.True(unresolved.Length == 0, $"unresolved task names: {string.Join(", ", unresolved.Take(5))}");
    }

    [Fact]
    public void EveryTaskHasATrader()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        // An unresolved trader comes through as its id, which is a 24-character hex blob. A real
        // name never is, so that is the whole test.
        var unresolved = snapshot.Tasks
            .Where(t => t.Trader.Length == 24 && t.Trader.All(Uri.IsHexDigit))
            .Select(t => t.Trader)
            .Distinct()
            .ToArray();

        Assert.True(unresolved.Length == 0, $"unresolved trader ids: {string.Join(", ", unresolved)}");
        Assert.All(snapshot.Tasks, t => Assert.NotEmpty(t.Trader));
    }

    /// <summary>
    /// Every position in the snapshot belongs to a map the app can draw.
    /// </summary>
    /// <remarks>
    /// The join that makes the whole feature work: quest zones are keyed by BSG map id, which has to
    /// go through the location id to fold Ground Zero's variants onto the map that ships. A miss
    /// here means objectives that quietly never appear.
    /// </remarks>
    [Fact]
    public void EveryObjectivePositionLandsOnAMap()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var catalog = MapCatalog.LoadEmbedded();

        var unresolved = session.Tasks.Tasks
            .SelectMany(t => t.Objectives)
            .SelectMany(o => o.Points)
            .Select(p => p.MapId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => catalog.ResolveByNameId(session.MapData.NameIdForId(id)) is null)
            .ToArray();

        Assert.True(unresolved.Length == 0, $"map ids with no map: {string.Join(", ", unresolved)}");
    }

    /// <summary>Tasks with nothing to draw are still in the list, because their text is worth reading.</summary>
    [Fact]
    public void TasksWithNoPositionAreStillListed()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        var textOnly = snapshot.Tasks
            .Where(t => t.Objectives.All(o => o.Points.Count == 0))
            .ToArray();

        Assert.NotEmpty(textOnly);
        Assert.All(textOnly, t => Assert.NotEmpty(t.Objectives));
    }

    [Fact]
    public void NothingIsDrawnUntilATaskIsTracked()
    {
        using var session = NewSession(Settings());

        session.RebuildQuestMarks();

        Assert.Empty(session.Quests.Marks);
    }

    [Fact]
    public void TrackingATaskDrawsItsObjectivesOnThisMap()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var task = FirstTaskOn(session, "customs");

        session.SetTracked(task.Id, true);

        Assert.NotEmpty(session.Quests.Marks);
        Assert.All(session.Quests.Marks, m => Assert.Equal(task.Id, m.TaskId));
    }

    /// <summary>
    /// The same tracked task draws different objectives depending on which map is shown.
    /// </summary>
    /// <remarks>
    /// Two sessions rather than switching one, so this stays a test of the filter rather than of
    /// map loading, which would want imagery and a network.
    /// </remarks>
    [Fact]
    public void OnlyTheObjectivesOnThisMapAreDrawn()
    {
        var catalog = MapCatalog.LoadEmbedded();

        using var probe = NewSession(Settings());

        // A task with objectives on two maps that both ship, so each session has something to draw
        // and they must not draw the same thing. Distinct by resolved map rather than by upstream
        // id: Ground Zero's three ids all fold onto one map and would not be a second place.
        var found = probe.Tasks.Tasks
            .Select(task => (task, maps: task.Objectives
                .SelectMany(o => o.Points)
                .Select(p => catalog.ResolveByNameId(probe.MapData.NameIdForId(p.MapId)))
                .Where(m => m is not null)
                .Select(m => m!.NormalizedName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()))
            .FirstOrDefault(x => x.maps.Length > 1);

        Assert.NotNull(found.task);

        var first = NewSession(Settings(found.task.Id));
        var second = NewSession(Settings(found.task.Id));

        using (first)
        using (second)
        {
            SetMap(first, found.maps[0]);
            SetMap(second, found.maps[1]);

            Assert.NotEmpty(first.Quests.Marks);
            Assert.NotEmpty(second.Quests.Marks);

            // Same task, different map, so no objective can appear on both.
            var here = first.Quests.Marks.Select(m => m.ObjectiveId + m.Position.X).ToHashSet();
            var there = second.Quests.Marks.Select(m => m.ObjectiveId + m.Position.X).ToHashSet();

            Assert.Empty(here.Intersect(there));
        }
    }

    /// <summary>
    /// Points a session at a map without loading its imagery.
    /// </summary>
    /// <remarks>
    /// <c>SetMapAsync</c> would rasterize an SVG and reach for the network, neither of which these
    /// tests are about. The quest filter reads <c>CurrentMap</c>, so this is enough.
    /// </remarks>
    private static void SetMap(MapSession session, string normalizedName)
    {
        session.SetMapForTesting(MapCatalog.LoadEmbedded().Resolve(normalizedName));
        session.RebuildQuestMarks();
    }

    /// <summary>
    /// A zone that upstream lists once per map variant is drawn once.
    /// </summary>
    /// <remarks>
    /// Ground Zero is three maps upstream and one here, so its zones arrive in duplicate. Two
    /// markers stacked at identical coordinates read as one darker marker, which is exactly the
    /// sort of thing nobody would ever work out from looking at it.
    /// </remarks>
    [Fact]
    public void DuplicatedVariantZonesAreDrawnOnce()
    {
        var settings = Settings();
        settings.CurrentMap = "ground-zero";

        using var session = NewSession(settings);

        foreach (var task in session.Tasks.Tasks)
            settings.TrackedTasks.Add(task.Id);

        session.RebuildQuestMarks();

        var duplicates = session.Quests.Marks
            .GroupBy(m => $"{m.ObjectiveId}|{m.Position.X}|{m.Position.Z}|{m.OneOf}")
            .Where(g => g.Count() > 1)
            .ToArray();

        Assert.True(duplicates.Length == 0, $"{duplicates.Length} objective positions drawn more than once");
        Assert.NotEmpty(session.Quests.Marks);
    }

    /// <summary>
    /// A single objective is not numbered; several from one task are.
    /// </summary>
    /// <remarks>
    /// A lone marker with a "1" on it invites the question of where 2 is.
    /// </remarks>
    [Fact]
    public void MarksAreNumberedOnlyWhenThereIsMoreThanOne()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        foreach (var task in session.Tasks.Tasks)
            settings.TrackedTasks.Add(task.Id);

        session.RebuildQuestMarks();

        foreach (var group in session.Quests.Marks.GroupBy(m => m.TaskId))
        {
            if (group.Count() == 1)
                Assert.Equal(0, group.Single().Index);
            else
                Assert.Equal(Enumerable.Range(1, group.Count()), group.Select(m => m.Index));
        }
    }

    [Fact]
    public void UntrackingStopsDrawing()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var task = FirstTaskOn(session, "customs");

        session.SetTracked(task.Id, true);
        Assert.NotEmpty(session.Quests.Marks);

        session.SetTracked(task.Id, false);
        Assert.Empty(session.Quests.Marks);
    }

    [Fact]
    public void ClearingUntracksEverything()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        foreach (var task in session.Tasks.Tasks.Take(20))
            session.SetTracked(task.Id, true);

        session.ClearTrackedTasks();

        Assert.Empty(session.Quests.Marks);
        Assert.Empty(settings.TrackedTasks);
    }

    /// <summary>Tracking the same task twice does not double its markers.</summary>
    [Fact]
    public void TrackingTwiceIsNotTrackingTwice()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var task = FirstTaskOn(session, "customs");

        session.SetTracked(task.Id, true);
        var first = session.Quests.Marks.Count;

        session.SetTracked(task.Id, true);

        Assert.Equal(first, session.Quests.Marks.Count);
        Assert.Single(settings.TrackedTasks);
    }

    /// <summary>A task id from an older settings file that upstream has dropped is ignored.</summary>
    [Fact]
    public void AnUnknownTrackedIdIsIgnored()
    {
        var settings = Settings("not-a-task-id");
        using var session = NewSession(settings);

        session.RebuildQuestMarks();

        Assert.Empty(session.Quests.Marks);
    }

    private static Data.Models.TaskData FirstTaskOn(MapSession session, string normalizedName)
    {
        var task = session.Tasks.Tasks.FirstOrDefault(t =>
            t.Objectives.Any(o => o.Points.Any(p => session.IsOnCurrentMap(p.MapId))));

        Assert.NotNull(task);
        Assert.Equal(normalizedName, session.CurrentMap.NormalizedName);

        return task;
    }
}
