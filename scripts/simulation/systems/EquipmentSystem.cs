using System.Collections.Generic;
using Sporeholm.Simulation.Items;

namespace Sporeholm.Simulation.Systems
{
    // v0.4.4 — auto-equips tools / weapons into a shroomp's dominant hand
    // based on the task they're about to start. Called by BehaviorSystem
    // immediately after `SelectTask` lands a new CurrentTask. The
    // resolver:
    //
    //   1. Reads the task type and looks up `PreferredForTasks` in the
    //      ItemRegistry to find the matching tool sub-types (Pick →
    //      GatherMaterial, Sickle → Chop/Cut, Basket → GatherFood,
    //      Focus → Attune/Meditate, Hammer → Build).
    //   2. Checks if the shroomp already has a matching tool equipped in
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
        // at 1000 shroomps that's hundreds of calls per second.
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

        public static void AutoEquipForTask(Shroomp s, BehaviorTask task, ColonyResources resources)
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
                displaced.OwnerShroompId = null;
                inv.Add(displaced);
            }
            single.OwnerShroompId = s.Id;
            s.Equipment[dom] = single;
        }

        // v0.5.84t — opportunistic weapon upgrade. RimWorld parity
        // (JobGiver_PickUpOpportunisticWeapon): scan colony inventory for the
        // best Weapon, swap if it scores meaningfully higher than the current
        // wielded weapon. Pacifists never auto-equip a weapon (RimWorld's
        // Trait_NonViolent gates the same WorkGiver). Called from
        // BehaviorSystem on task transition + a periodic idle tick so
        // shroomps without a pending task still upgrade.
        // Sam: "they should generally want to pick up a better weapon
        // (suited to their skills) unless they're a pacifist."
        public static void AutoEquipBetterWeapon(Shroomp s, ColonyResources resources)
        {
            if (s == null || resources == null) return;
            if (s.IsPacifist) return;   // RimWorld parity — NonViolent gate
            var inv = resources.Inventory;
            // Find the currently-wielded weapon (either hand).
            Item? wielded = null;
            EquipSlot wieldedSlot = EquipSlot.LeftHand;
            foreach (var slot in _handSlots)
            {
                if (!s.Equipment.TryGetValue(slot, out var have)) continue;
                if (have.Kind == ItemKind.Weapon) { wielded = have; wieldedSlot = slot; break; }
            }
            float currentScore = wielded == null ? 0f : ScoreWeapon(s, wielded);

            // Scan inventory for a better weapon.
            Item? bestItem  = null;
            float bestScore = currentScore;
            foreach (var it in inv.Items)
            {
                if (it.Kind != ItemKind.Weapon) continue;
                if (it.State == ItemState.Broken) continue;
                float score = ScoreWeapon(s, it);
                if (score > bestScore) { bestScore = score; bestItem = it; }
            }
            // Hysteresis: require new score > current * 1.05 so a near-tie
            // doesn't churn the weapon every tick.
            if (bestItem == null) return;
            if (currentScore > 0f && bestScore < currentScore * 1.05f) return;

            // Consume one unit + bounce displaced weapon back to inventory.
            Item single = bestItem;
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
            if (wielded != null)
            {
                wielded.OwnerShroompId = null;
                inv.Add(wielded);
                s.Equipment.Remove(wieldedSlot);
            }
            single.OwnerShroompId = s.Id;
            // Prefer dominant hand for weapon; off-hand reserved for shield.
            var dom = HandednessMeta.DominantHand(s.Handedness);
            // If a tool occupies dominant hand, bounce it to inventory —
            // the weapon takes precedence (Guardian role intent).
            if (s.Equipment.TryGetValue(dom, out var domOcc))
            {
                domOcc.OwnerShroompId = null;
                inv.Add(domOcc);
            }
            s.Equipment[dom] = single;
        }

        // Weapon-score formula: damage × accuracy × QualityMul × (condition/cap).
        // Skill bias: if Melee > Ranged, prefer higher-damage; if Ranged > Melee,
        // prefer higher-accuracy. Mirrors RimWorld's coarse weapon ranking
        // without the full DPS calc.
        private static float ScoreWeapon(Shroomp s, Item it)
        {
            var def = ItemRegistry.Get(it.Kind, it.SubType);
            if (def == null) return 0f;
            float dmg = def.BaseDamage;
            float acc = def.BaseAccuracy;
            if (dmg <= 0f && acc <= 0f) return 0f;
            float quality = (float)QualityMeta.ValueMul(it.Quality);
            float cond    = it.AvgCondition / System.Math.Max(it.DurabilityCap, 1f);
            // Skill bias: lookup melee/ranged levels. Default 0 if absent.
            int melee  = s.Skills.TryGetValue("Melee",  out var m) ? m : 0;
            int ranged = s.Skills.TryGetValue("Ranged", out var r) ? r : 0;
            // Ranged weapons (Sling/Bow/Crossbow/Atlatl) have accuracy < 0.75.
            bool isRanged = acc > 0f && acc < 0.75f;
            float skillMul = isRanged
                ? 1.0f + 0.05f * ranged
                : 1.0f + 0.05f * melee;
            return dmg * acc * quality * cond * skillMul;
        }

        // v0.5.84t — drop tools the shroomp no longer needs. Called from
        // BehaviorSystem when a task ends; if the next-tick task doesn't
        // want the held tool either, drop it on the current tile (unforbidden)
        // so HaulSystem hauls it to a stockpile. Pacifism doesn't apply to
        // tools (only weapons). Sam: "they should drop them unless they're
        // forced."
        //
        // Skips weapons (Guardians keep their spear/sword permanently).
        // Skips Sage Staff (Focus) for Sages — it's their role-canonical
        // tool, kept even outside Attune/Meditate.
        public static void DropUnsuitableTool(Shroomp s, Sporeholm.World.LocalMap? map)
        {
            if (s == null || map == null) return;
            var dom = HandednessMeta.DominantHand(s.Handedness);
            if (!s.Equipment.TryGetValue(dom, out var held)) return;
            if (held.Kind != ItemKind.Tool) return;   // weapons stay

            // Role-canonical exceptions: don't drop the tool the shroomp's
            // role would always want.
            if (s.Role == "Sage" && held.SubType == "Focus") return;
            if (s.Role == "Crafter" && held.SubType == "Hammer") return;
            if (s.Role == "Forager" && held.SubType == "Basket") return;

            // Held tool is preferred for current task? Keep it.
            if (s.CurrentTask is { } ct)
            {
                var def = ItemRegistry.Get(held.Kind, held.SubType);
                if (def?.PreferredForTasks != null)
                {
                    for (int i = 0; i < def.PreferredForTasks.Length; i++)
                    {
                        if (def.PreferredForTasks[i] == ct.Type) return;
                    }
                }
            }

            // Drop on current tile as unforbidden so HaulSystem moves it.
            int tx = (int)(s.SimPos.X / Sporeholm.World.LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / Sporeholm.World.LocalMap.TileSize);
            var drop = new Item
            {
                Kind          = held.Kind,
                SubType       = held.SubType,
                Material      = held.Material,
                Quality       = held.Quality,
                State         = held.State,
                AvgCondition  = held.AvgCondition,
                DurabilityCap = held.DurabilityCap,
                AvgBirthTick  = held.AvgBirthTick,
                Quantity      = held.Quantity,
                TilePos       = new Godot.Vector2(
                    tx * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f,
                    ty * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f),
                IsForbidden = false,
            };
            map.DropItem(drop);
            s.Equipment.Remove(dom);
        }
    }
}
