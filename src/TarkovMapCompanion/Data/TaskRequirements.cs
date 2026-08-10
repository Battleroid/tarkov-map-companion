using TarkovMapCompanion.Data.Models;

namespace TarkovMapCompanion.Data;

/// <summary>
/// What a set of tasks wants you to have brought into a particular raid.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the views because two of them ask the same question: the reading pane asks it
/// about one task, and the Quests tab asks it about everything you have ticked. Both want the same
/// answer, and it is the kind of rule that rots quietly if it is written twice.
/// </para>
/// <para>
/// The distinction that matters is <b>carried in</b> versus <b>handed over</b>. A task that wants
/// five MP-133s wants them at the trader's counter, and listing them beside "take to Customs"
/// would be advice to fill your rig with shotguns. A task that wants an MS2000 marker planted
/// wants it in your pocket before the raid starts, and forgetting it costs you the trip.
/// </para>
/// </remarks>
public static class TaskRequirements
{
    /// <summary>
    /// Objective types whose items go into your rig rather than onto a trader's counter.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Measured against the real data these four cover every objective that
    /// both names an item and has a place on a map; the rest — <c>giveItem</c>, <c>findItem</c>,
    /// <c>buildWeapon</c> and friends — are things you hand in or come back with.
    /// </remarks>
    public static readonly IReadOnlySet<string> CarriedTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "plantItem",
            "plantQuestItem",
            "useItem",
            "mark",
        };

    /// <summary>Keys this task needs on the maps <paramref name="onMap"/> accepts.</summary>
    public static IEnumerable<TaskItemData> KeysFor(TaskData task, Func<string?, bool> onMap) =>
        task.Keys.Where(k => onMap(k.MapId)).SelectMany(k => k.Keys);

    /// <summary>
    /// Items this task wants you carrying, for objectives that happen where
    /// <paramref name="onMap"/> accepts.
    /// </summary>
    /// <remarks>
    /// An objective with no position at all is skipped rather than counted everywhere: "plant this
    /// somewhere" with no somewhere cannot be pinned to a raid, and guessing would put it on the
    /// list for every map.
    /// </remarks>
    public static IEnumerable<TaskItemData> CarriedItemsFor(TaskData task, Func<string?, bool> onMap) =>
        task.Objectives
            .Where(o => CarriedTypes.Contains(o.Type) && o.Items.Count > 0)
            .Where(o => o.Points.Any(p => onMap(p.MapId)))
            .SelectMany(o => o.Items);

    /// <summary>
    /// Everything the given tasks want brought to one map, deduplicated and in reading order.
    /// </summary>
    public static TaskKit Gather(IEnumerable<TaskData> tasks, Func<string?, bool> onMap)
    {
        var keys = new List<TaskItemData>();
        var items = new List<TaskItemData>();

        foreach (var task in tasks)
        {
            Add(keys, KeysFor(task, onMap));
            Add(items, CarriedItemsFor(task, onMap));
        }

        keys.Sort(ByName);
        items.Sort(ByName);

        return new TaskKit(keys, items);

        static int ByName(TaskItemData a, TaskItemData b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        // Distinct by id, which is the identity that matters: two tasks naming the same key is the
        // common case, not the exception.
        static void Add(List<TaskItemData> into, IEnumerable<TaskItemData> found)
        {
            foreach (var item in found)
            {
                if (!into.Any(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal)))
                    into.Add(item);
            }
        }
    }
}

/// <summary>What to bring to one map.</summary>
/// <param name="Keys">Keys, which is the part that cannot be improvised in the raid.</param>
/// <param name="Items">Items to carry in.</param>
public sealed record TaskKit(IReadOnlyList<TaskItemData> Keys, IReadOnlyList<TaskItemData> Items)
{
    public bool IsEmpty => Keys.Count == 0 && Items.Count == 0;
}
