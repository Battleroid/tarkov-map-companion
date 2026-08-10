using TarkovMapCompanion.GameLog;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// What the game's log can and cannot be asked: which quests are open, and how high your level is.
/// </summary>
public sealed class QuestFromLogTests
{
    private static AppSettings Settings() => new()
    {
        AllowNetwork = false,
        CurrentMap = "customs",
        CullMode = CullMode.Off,
        ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-questlog-noshots"),
    };

    private static MapSession NewSession(AppSettings settings)
    {
        var session = new MapSession(settings, MapCatalog.LoadEmbedded());

        session.MapData.LoadLocal();
        session.Tasks.LoadLocal();

        return session;
    }

    // ---- The level floor ----------------------------------------------------

    /// <summary>
    /// A quest with a level requirement cannot have been accepted below it.
    /// </summary>
    /// <remarks>
    /// The basis of the estimate, on a clean set where every entry agrees.
    /// </remarks>
    [Fact]
    public void TheEstimateFollowsTheQuestsSeen()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (low, high) = TwoTasksAtDifferentLevels(session);

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress>
        {
            [low.Id] = QuestProgress.Active,
            [high.Id] = QuestProgress.Completed,
        });

        Assert.Equal(high.MinPlayerLevel, session.EstimateLevelFromQuestLog());
    }

    /// <summary>
    /// One wild entry does not decide the answer.
    /// </summary>
    /// <remarks>
    /// The bug this replaced: a real account belonging to a level 25 player read as 52, because a
    /// single message named a level 52 quest whose reward items did not match the task the id
    /// claims. Roughly one entry in twelve in that stream was noise of some kind, and the maximum
    /// is decided entirely by the worst one.
    /// </remarks>
    [Fact]
    public void ASingleWildEntryDoesNotSetTheLevel()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        // Twenty quests at or below 25, and one outlier far above.
        var ordinary = session.Tasks.Tasks
            .Where(t => t.MinPlayerLevel is > 0 and <= 25)
            .Take(20)
            .ToArray();

        Assert.Equal(20, ordinary.Length);

        var wild = session.Tasks.Tasks.First(t => t.MinPlayerLevel >= 45);

        var state = ordinary.ToDictionary(t => t.Id, _ => QuestProgress.Completed, StringComparer.Ordinal);
        state[wild.Id] = QuestProgress.Completed;

        session.ApplyQuestStateForTesting(state);

        var estimate = session.EstimateLevelFromQuestLog();

        Assert.True(estimate <= 25, $"one outlier dragged the estimate to {estimate}");
        Assert.True(estimate > 0, "the estimate gave up entirely");
    }

    /// <summary>A failed quest still had to be accepted, so it still tells you a level.</summary>
    [Fact]
    public void FailingOneStillCounts()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (_, high) = TwoTasksAtDifferentLevels(session);

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress>
        {
            [high.Id] = QuestProgress.Failed,
        });

        Assert.Equal(high.MinPlayerLevel, session.EstimateLevelFromQuestLog());
    }

    [Fact]
    public void TheFloorRaisesTheStoredLevel()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (_, high) = TwoTasksAtDifferentLevels(session);

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress> { [high.Id] = QuestProgress.Active });

        Assert.True(session.ApplyLevelFloorFromQuestLog());
        Assert.Equal(high.MinPlayerLevel, settings.PlayerLevel);
    }

    /// <summary>
    /// It never lowers what is already there.
    /// </summary>
    /// <remarks>
    /// The estimate lags the truth by however far you have got since your hardest accepted quest.
    /// Someone at 60 whose highest requirement was 42 reads as 42, and pulling their level down to
    /// it would hide two thirds of the tasks they can actually take.
    /// </remarks>
    [Fact]
    public void ALevelYouTypedInIsNeverPulledDown()
    {
        var settings = Settings();
        settings.PlayerLevel = 70;

        using var session = NewSession(settings);

        var (_, high) = TwoTasksAtDifferentLevels(session);
        Assert.True(high.MinPlayerLevel < 70);

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress> { [high.Id] = QuestProgress.Active });

        Assert.False(session.ApplyLevelFloorFromQuestLog());
        Assert.Equal(70, settings.PlayerLevel);
    }

    [Fact]
    public void TheEstimateCanBeTurnedOff()
    {
        var settings = Settings();
        settings.PlayerLevelFromGameLog = false;

        using var session = NewSession(settings);

        var (_, high) = TwoTasksAtDifferentLevels(session);
        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress> { [high.Id] = QuestProgress.Active });

        Assert.False(session.ApplyLevelFloorFromQuestLog());
        Assert.Equal(1, settings.PlayerLevel);
    }

    /// <summary>An empty log says nothing about your level, rather than saying level 1.</summary>
    [Fact]
    public void NoLogIsNoOpinion()
    {
        var settings = Settings();
        settings.PlayerLevel = 30;

        using var session = NewSession(settings);

        Assert.Equal(0, session.EstimateLevelFromQuestLog());
        Assert.False(session.ApplyLevelFloorFromQuestLog());
        Assert.Equal(30, settings.PlayerLevel);
    }

    // ---- Syncing tracking ---------------------------------------------------

    [Fact]
    public void SyncingTracksExactlyWhatIsOpen()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (low, high) = TwoTasksAtDifferentLevels(session);

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress>
        {
            [low.Id] = QuestProgress.Active,
            [high.Id] = QuestProgress.Completed,
        });

        Assert.Equal(1, session.SyncTrackedFromQuestLog());
        Assert.Equal([low.Id], settings.TrackedTasks);
    }

    /// <summary>
    /// It throws the hand-picked list away, which is the point of it.
    /// </summary>
    /// <remarks>
    /// Following the log as events arrive cannot fix a list that has drifted: a quest handed in
    /// while the app was closed never produces an event to untick it. Being destructive is what
    /// makes this different from the ordinary path.
    /// </remarks>
    [Fact]
    public void SyncingDiscardsWhatWasTickedByHand()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (low, high) = TwoTasksAtDifferentLevels(session);

        session.SetTracked(high.Id, true);
        Assert.True(session.IsTracked(high.Id));

        session.ApplyQuestStateForTesting(new Dictionary<string, QuestProgress> { [low.Id] = QuestProgress.Active });
        session.SyncTrackedFromQuestLog();

        Assert.False(session.IsTracked(high.Id));
        Assert.True(session.IsTracked(low.Id));
    }

    [Fact]
    public void SyncingAnEmptyLogUntracksEverything()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var (low, _) = TwoTasksAtDifferentLevels(session);
        session.SetTracked(low.Id, true);

        Assert.Equal(0, session.SyncTrackedFromQuestLog());
        Assert.Empty(settings.TrackedTasks);
    }

    // ---- Objectives ticked off ----------------------------------------------

    [Fact]
    public void ATickedObjectiveIsStillDrawn()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var task = session.Tasks.Tasks.First(t =>
            t.Objectives.Any(o => o.Points.Any(p => session.IsOnCurrentMap(p.MapId))));

        session.SetTracked(task.Id, true);

        var before = session.Quests.Marks.Count;
        var objective = session.Quests.Marks[0].ObjectiveId;

        session.SetObjectiveDone(objective, true);

        Assert.Equal(before, session.Quests.Marks.Count);
        Assert.All(
            session.Quests.Marks.Where(m => m.ObjectiveId == objective),
            m => Assert.True(m.Done));
    }

    [Fact]
    public void TicksSurviveInSettingsAndCanBeCleared()
    {
        var settings = Settings();
        using var session = NewSession(settings);

        var task = session.Tasks.Tasks.First(t => t.Objectives.Count > 1);

        foreach (var objective in task.Objectives)
            session.SetObjectiveDone(objective.Id, true);

        Assert.Equal(task.Objectives.Count, settings.CompletedObjectives.Count);

        session.ClearObjectivesDone(task);

        Assert.Empty(settings.CompletedObjectives);
    }

    /// <summary>Two tasks the snapshot really has, with different level requirements.</summary>
    private static (Data.Models.TaskData Low, Data.Models.TaskData High) TwoTasksAtDifferentLevels(MapSession session)
    {
        var low = session.Tasks.Tasks.First(t => t.MinPlayerLevel is > 0 and < 15);
        var high = session.Tasks.Tasks.First(t => t.MinPlayerLevel is > 20 and < 60);

        return (low, high);
    }
}
