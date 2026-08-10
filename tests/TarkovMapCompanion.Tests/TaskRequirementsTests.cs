using TarkovMapCompanion.Data;
using TarkovMapCompanion.Data.Models;
using TarkovMapCompanion.Maps;
using TarkovMapCompanion.Settings;
using Xunit;

namespace TarkovMapCompanion.Tests;

/// <summary>
/// Keys and items: what the snapshot carries, and what "take this to Customs" comes to.
/// </summary>
public sealed class TaskRequirementsTests
{
    private const string Customs = "56f40101d2720b2a4d8b45d6";
    private const string Woods = "5704e3c2d2720bac5b8b4567";

    // ---- The snapshot -------------------------------------------------------

    /// <summary>
    /// Keys survived the projection, resolved to names rather than to ids.
    /// </summary>
    /// <remarks>
    /// The failure this pins is silent: an unresolved item id projects to a 24-character hex
    /// string that renders perfectly happily in the pane and means nothing to anybody.
    /// </remarks>
    [Fact]
    public void TheSnapshotCarriesKeyNames()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        var withKeys = snapshot.Tasks.Where(t => t.Keys.Count > 0).ToArray();

        Assert.True(withKeys.Length > 20, $"only {withKeys.Length} tasks name a key");

        Assert.All(withKeys, task => Assert.All(task.Keys, group =>
        {
            Assert.NotEmpty(group.MapId);
            Assert.NotEmpty(group.Keys);
            Assert.All(group.Keys, key =>
            {
                Assert.False(LooksLikeAnId(key.Name), key.Name);
                Assert.True(ItemIconStore.LooksLikeAnId(key.Id), key.Id);
            });
        }));
    }

    [Fact]
    public void TheSnapshotCarriesObjectiveItemNames()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        var items = snapshot.Tasks
            .SelectMany(t => t.Objectives)
            .Where(o => o.Items.Count > 0)
            .ToArray();

        Assert.True(items.Length > 200, $"only {items.Length} objectives name an item");

        Assert.All(items, o => Assert.All(o.Items, item =>
        {
            Assert.False(LooksLikeAnId(item.Name), item.Name);

            // The id is the whole address of the item's picture, so an item carrying a name and no
            // id is an icon that can never load.
            Assert.True(ItemIconStore.LooksLikeAnId(item.Id), $"{item.Name} has id '{item.Id}'");
        }));
    }

    /// <summary>
    /// A list past the cap is a category, and the snapshot drops it rather than shipping it.
    /// </summary>
    /// <remarks>
    /// One upstream objective names 3,493 items. Embedding that would cost more than the whole
    /// rest of the snapshot to say something its own description already says better.
    /// </remarks>
    [Fact]
    public void LongItemListsAreNotShipped()
    {
        var snapshot = TaskStore.EmbeddedSnapshot()!;

        Assert.All(
            snapshot.Tasks.SelectMany(t => t.Objectives),
            o => Assert.True(
                o.Items.Count <= TaskObjectiveData.MaxNamedItems + 1,
                $"{o.Id} names {o.Items.Count} items"));
    }

    /// <summary>Every key group points at a map the app can name.</summary>
    [Fact]
    public void KeyMapIdsResolve()
    {
        var settings = new AppSettings
        {
            AllowNetwork = false,
            CurrentMap = "customs",
            CullMode = CullMode.Off,
            ScreenshotFolder = Path.Combine(Path.GetTempPath(), "tmc-keys-noshots"),
        };

        using var session = new MapSession(settings, MapCatalog.LoadEmbedded());
        session.MapData.LoadLocal();
        session.Tasks.LoadLocal();

        var unresolved = session.Tasks.Tasks
            .SelectMany(t => t.Keys)
            .Select(k => k.MapId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => session.MapNameFor(id) is null)
            .ToArray();

        Assert.True(unresolved.Length == 0, $"key maps with no map: {string.Join(", ", unresolved)}");
    }

    // ---- Gathering ----------------------------------------------------------

    [Fact]
    public void KeysAreScopedToTheMapBeingAskedAbout()
    {
        var task = new TaskData
        {
            Keys =
            [
                new TaskKeyData { MapId = Customs, Keys = [Item("Dorm room 314 marked key")] },
                new TaskKeyData { MapId = Woods, Keys = [Item("Wooden ladder")] },
            ],
        };

        var kit = TaskRequirements.Gather([task], id => id == Customs);

        Assert.Equal(["Dorm room 314 marked key"], Names(kit.Keys));
    }

    /// <summary>
    /// Handing something over is not packing it.
    /// </summary>
    /// <remarks>
    /// The distinction this file exists for. A <c>giveItem</c> objective wanting five shotguns
    /// wants them at the counter; putting them under "take to Customs" would be advice to fill
    /// your rig with them.
    /// </remarks>
    [Fact]
    public void OnlyCarriedItemsAreListed()
    {
        var task = new TaskData
        {
            Objectives =
            [
                Objective("plantItem", ["WI-FI Camera"], Customs),
                Objective("giveItem", ["MP-133 12ga shotgun"], Customs),
            ],
        };

        var kit = TaskRequirements.Gather([task], id => id == Customs);

        Assert.Equal(["WI-FI Camera"], Names(kit.Items));
    }

    [Fact]
    public void ItemsForAnotherMapAreNotListed()
    {
        var task = new TaskData { Objectives = [Objective("mark", ["MS2000 Marker"], Woods)] };

        Assert.Empty(TaskRequirements.Gather([task], id => id == Customs).Items);
        Assert.Equal(["MS2000 Marker"], Names(TaskRequirements.Gather([task], id => id == Woods).Items));
    }

    /// <summary>An objective with no position at all belongs to no raid in particular.</summary>
    [Fact]
    public void AnObjectiveWithNoPositionIsNotPackedForEveryMap()
    {
        var task = new TaskData
        {
            Objectives = [new TaskObjectiveData { Type = "plantItem", Items = [Item("Bronze pocket watch")] }],
        };

        Assert.Empty(TaskRequirements.Gather([task], _ => true).Items);
    }

    /// <summary>Deduplicated by id, which is the identity that survives a rename upstream.</summary>
    [Fact]
    public void TwoTasksWantingTheSameKeyAskForItOnce()
    {
        var key = Item("Dorm overseer key");

        var one = new TaskData { Keys = [new TaskKeyData { MapId = Customs, Keys = [key] }] };
        var two = new TaskData
        {
            Keys = [new TaskKeyData { MapId = Customs, Keys = [new TaskItemData { Id = key.Id, Name = "dorm overseer KEY" }] }],
        };

        var kit = TaskRequirements.Gather([one, two], id => id == Customs);

        Assert.Equal(["Dorm overseer key"], Names(kit.Keys));
    }

    [Fact]
    public void NothingTrackedIsAnEmptyKit()
    {
        var kit = TaskRequirements.Gather([], _ => true);

        Assert.True(kit.IsEmpty);
    }

    private static TaskObjectiveData Objective(string type, string[] items, string mapId) => new()
    {
        Type = type,
        Items = [.. items.Select(Item)],
        Points = [new TaskPointData { MapId = mapId }],
    };

    /// <summary>An item with a plausible id, since the ids only have to be distinct here.</summary>
    private static TaskItemData Item(string name) => new()
    {
        Id = string.Concat(name.Where(char.IsAsciiLetterOrDigit))
            .ToLowerInvariant()
            .PadRight(24, '0')[..24]
            .Select(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') ? c : 'a')
            .Aggregate("", (acc, c) => acc + c),
        Name = name,
    };

    private static string[] Names(IEnumerable<TaskItemData> items) => [.. items.Select(i => i.Name)];

    private static bool LooksLikeAnId(string name) =>
        name.Length == 24 && name.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));
}
