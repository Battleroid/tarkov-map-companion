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
    /// Items this task wants you carrying, with how many, for objectives that happen where
    /// <paramref name="onMap"/> accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An objective with no position at all is skipped rather than counted everywhere: "plant this
    /// somewhere" with no somewhere cannot be pinned to a raid, and guessing would put it on the
    /// list for every map.
    /// </para>
    /// <para>
    /// An objective that names several items names alternatives, not a set -- eight kinds of
    /// 7.62x51 ammo pack, any one of which will do -- so the count belongs to each of them rather
    /// than being divided between them. In the real data every such objective wants exactly one,
    /// so this never has to render "3 of these, or 3 of those".
    /// </para>
    /// </remarks>
    public static IEnumerable<TaskItemNeed> CarriedItemsFor(TaskData task, Func<string?, bool> onMap) =>
        task.Objectives
            .Where(o => CarriedTypes.Contains(o.Type) && o.Items.Count > 0)
            .Where(o => o.Points.Any(p => onMap(p.MapId)))
            .SelectMany(o => o.Items.Select(i => new TaskItemNeed(i, Math.Max(1, o.Count ?? 1))));

    /// <summary>
    /// Everything the given tasks want brought to one map, deduplicated and in reading order.
    /// </summary>
    public static TaskKit Gather(IEnumerable<TaskData> tasks, Func<string?, bool> onMap)
    {
        var keys = new List<TaskItemData>();
        var items = new List<TaskItemNeed>();

        foreach (var task in tasks)
        {
            // Keys do not add up. Three tasks behind the same door still want one key, and telling
            // somebody to bring three of it would be worse than saying nothing.
            foreach (var key in KeysFor(task, onMap))
            {
                if (!keys.Any(k => string.Equals(k.Id, key.Id, StringComparison.Ordinal)))
                    keys.Add(key);
            }

            // Items do. Planting is consuming, so two tasks that each want a WI-FI camera on Woods
            // want two cameras.
            foreach (var need in CarriedItemsFor(task, onMap))
            {
                var at = items.FindIndex(i => string.Equals(i.Item.Id, need.Item.Id, StringComparison.Ordinal));

                if (at < 0)
                    items.Add(need);
                else
                    items[at] = items[at] with { Count = items[at].Count + need.Count };
            }
        }

        keys.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        items.Sort((a, b) => string.Compare(a.Item.Name, b.Item.Name, StringComparison.OrdinalIgnoreCase));

        return new TaskKit(keys, items);
    }
}

/// <summary>One item and how many of it.</summary>
/// <param name="Count">At least one. Only worth showing when it is more.</param>
public readonly record struct TaskItemNeed(TaskItemData Item, int Count)
{
    /// <summary>The name, with a multiplier when there is one worth mentioning.</summary>
    public string Label => Count > 1 ? $"{Count}x {Item.Name}" : Item.Name;
}

/// <summary>What to bring to one map.</summary>
/// <param name="Keys">
/// Keys, which is the part that cannot be improvised in the raid. No quantities: a key is not
/// spent by using it.
/// </param>
/// <param name="Items">Items to carry in, with how many.</param>
public sealed record TaskKit(IReadOnlyList<TaskItemData> Keys, IReadOnlyList<TaskItemNeed> Items)
{
    public bool IsEmpty => Keys.Count == 0 && Items.Count == 0;
}
