using System.Collections.Generic;
using SmurfulationC.Simulation.Items;

namespace SmurfulationC.Simulation.Systems
{
    // v0.4.4 — auto-equips tools / weapons into a smurf's dominant hand
    // based on the task they're about to start. Called by BehaviorSystem
    // immediately after `SelectTask` lands a new CurrentTask. The
    // resolver:
    //
    //   1. Reads the task type and looks up `PreferredForTasks` in the
    //      ItemRegistry to find the matching tool sub-types (Pick →
    //      GatherMaterial, Sickle → Chop/Cut, Basket → GatherFood,
    //      Focus → Attune/Meditate, Hammer → Build).
    //   2. Checks if the smurf already has a matching tool equipped in
    //      either hand — if yes, nothing to do.
    //   3. Otherwise scans the colony inventory for the best matching
    //      tool (highest quality × best material × best condition),
    //      consumes one unit, and slots it in the dominant hand.
    //   4. If both hands hold non-matching items, the dominant-hand
    //      occupant bounces back to the colony inventory so the right
    //      tool gets priority. (Phase 7 combat: shields stay in off-
    //      hand even when a tool comes in.)
    //
    // The off-hand stays free for shields + dual-wield once combat
    // lands. Phase 5 stockpile zones replace the "magic grab from
    // global inventory" with a "walk to the tool stockpile, then to
    // the work site" path.
    public static class EquipmentSystem
    {
        // v0.4.6 — precomputed (TaskType → preferred-tool defs) lookup.
        // Built once on first use. The previous per-call walk over
        // ItemRegistry.All was small today (a dozen entries) but
        // BehaviorSystem calls AutoEquipForTask on every task transition;
        // at 1000 smurfs that's hundreds of calls per second.
        private static readonly Dictionary<TaskType, ItemSubTypeDef[]> _preferredByTask
            = BuildPreferredByTask();

        private static Dictionary<TaskType, ItemSubTypeDef[]> BuildPreferredByTask()
        {
            var byTask = new Dictionary<TaskType, List<ItemSubTypeDef>>();
            foreach (var def in ItemRegistry.All)
            {
                if (def.PreferredForTasks == null) continue;
                foreach (var t in def.PreferredForTasks)
                {
                    if (!byTask.TryGetValue(t, out var list)) byTask[t] = list = new List<ItemSubTypeDef>();
                    list.Add(def);
                }
            }
            var dict = new Dictionary<TaskType, ItemSubTypeDef[]>(byTask.Count);
            foreach (var (t, list) in byTask) dict[t] = list.ToArray();
            return dict;
        }

        private static readonly EquipSlot[] _handSlots = { EquipSlot.LeftHand, EquipSlot.RightHand };

        public static void AutoEquipForTask(Smurf s, BehaviorTask task, ColonyResources resources)
        {
            if (s == null || resources == null) return;

            // Fast lookup of tools preferred for this task type.
            if (!_preferredByTask.TryGetValue(task.Type, out var preferred)) return;
            if (preferred.Length == 0) return;

            // Already wielding a matching tool? Skip. (No allocation —
            // uses the singleton-built `_handSlots` array.)
            foreach (var slot in _handSlots)
            {
                if (!s.Equipment.TryGetValue(slot, out var have)) continue;
                for (int i = 0; i < preferred.Length; i++)
                {
                    var pref = preferred[i];
                    if (have.Kind == pref.Kind && have.SubType == pref.SubType)
                        return;
                }
            }

            // Find the best matching tool in the colony inventory.
            var inv = resources.Inventory;
            Item? bestItem  = null;
            float bestScore = float.MinValue;
            foreach (var it in inv.Items)
            {
                if (it.State == ItemState.Broken) continue;
                bool isPreferred = false;
                for (int i = 0; i < preferred.Length; i++)
                {
                    var pref = preferred[i];
                    if (it.Kind == pref.Kind && it.SubType == pref.SubType)
                    {
                        isPreferred = true; break;
                    }
                }
                if (!isPreferred) continue;
                float score = (float)QualityMeta.ValueMul(it.Quality)
                            * (it.AvgCondition / System.Math.Max(it.DurabilityCap, 1f));
                if (score > bestScore) { bestScore = score; bestItem = it; }
            }
            if (bestItem == null) return;

            // Split one unit off (consume reads inv-locked).
            var single = bestItem;
            if (bestItem.Quantity > 1)
            {
                single = new Item
                {
                    Kind          = bestItem.Kind,
                    SubType       = bestItem.SubType,
                    Material      = bestItem.Material,
                    Quality       = bestItem.Quality,
                    State         = bestItem.State,
                    AvgCondition  = bestItem.AvgCondition,
                    DurabilityCap = bestItem.DurabilityCap,
                    AvgBirthTick  = bestItem.AvgBirthTick,
                    Quantity      = 1,
                };
                inv.Consume(bestItem, 1);
            }
            else
            {
                inv.Consume(bestItem, 1);
            }

            // Bounce the dominant-hand occupant (if any) back to the pool
            // so the right tool gets priority; off-hand stays untouched.
            var dom = HandednessMeta.DominantHand(s.Handedness);
            if (s.Equipment.TryGetValue(dom, out var displaced))
            {
                displaced.OwnerSmurfId = null;
                inv.Add(displaced);
            }
            single.OwnerSmurfId = s.Id;
            s.Equipment[dom] = single;
        }
    }
}
