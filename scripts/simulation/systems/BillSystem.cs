using Godot;
using Sporeholm.Simulation.Crafting;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.5.84s — Phase 5.5 Crafting Bills System driver. Modeled on
    // CookSystem (the v0.5.22 single-recipe auto-cook). Bills extend
    // CookSystem with player-queued explicit recipes, repeat modes,
    // skill gates, and multiple ingredient consumption.
    //
    // v0.6.2 (Phase 5.6 + audit fixes):
    //   • Scans both Workbench AND CookingTable tiles (renamed terminology
    //     from "workbench-only" to "production-station").
    //   • Station-typed routing: Workbench gets Crafting recipes, Cooking
    //     Table gets Cooking recipes (Bonfire fallback handled by CookSystem
    //     auto-cook, not bills today — but the math hook is here).
    //   • Reachability-gated at SelectTarget (audit Fix 3): bills on
    //     unreachable tiles never get assigned (mirror v0.5.83 idle gate).
    //   • Recipe-id-based atomic remove on bill exhaustion (audit Fix 1):
    //     the cached index could shift if the player edits the queue mid-
    //     work; the new map.RemoveWorkbenchBillByRecipeId locks + finds +
    //     removes in one critical section.
    //
    // Flow:
    //   1. SelectTarget — scan every production-station tile's bills list.
    //      For each active bill (not Suspended), check (a) shroomp meets
    //      skill requirements, (b) recipe.Station matches the structure,
    //      (c) recipe ingredients available in colony inventory,
    //      (d) TargetCount mode: colony doesn't already have ≥ TargetCount,
    //      (e) station is reachable (Pathfinder). First match wins; return
    //      a DoBill task pointing at the tile with the bill's RecipeId as
    //      TargetId.
    //
    //   2. Apply — on arrival, advance the bill's ProgressTicks by 1 tick ×
    //      skill speed factor. When ProgressTicks ≥ effective WorkTicks
    //      (recipe.WorkTicks scaled by Bonfire fallback multiplier when
    //      applicable), consume ingredients, produce outputs on the tile
    //      (join the haul flow), grant XP, decrement RepeatsRemaining if
    //      RepeatCount mode. Auto-remove bill via RecipeId-atomic when
    //      RepeatsRemaining hits 0.
    //
    // The pre-Phase 5.5 CookSystem auto-cook remains as the fallback for
    // stations with no bills — players who don't queue any recipes still
    // get the prepared-meals loop from v0.5.22 (with v0.6.2's Bonfire-
    // fallback speed penalty applied per-tick).
    public static class BillSystem
    {
        // Try to find a satisfiable bill for this shroomp. Returns a
        // DoBill task or null if no work available. Encodes the chosen
        // bill's RecipeId in TargetId; Apply re-finds the bill by id since
        // index may shift between SelectTarget and Apply.
        public static BehaviorTask? SelectTarget(Shroomp s, LocalMap? map, ColonyResources r)
        {
            if (map == null || r == null) return null;

            // Iterate every production-station tile with at least one bill.
            // AllWorkbenchBills() now returns a thread-safe snapshot (Fix 1).
            foreach (var ((tx, ty), bills) in map.AllWorkbenchBills())
            {
                // Verify the structure is still present (could be demolished
                // between AddWorkbenchBill and now). Accept either Workbench
                // (Crafting bills) or CookingTable (Cooking bills).
                var slot = map.GetStructure(tx, ty);
                if (slot.Type != StructureType.Workbench && slot.Type != StructureType.CookingTable) continue;

                // v0.6.2 audit Fix 3 — reachability gate. Mirrors v0.5.83
                // wander/loiter/observe/visit-fav gating: don't assign
                // bills the shroomp can't physically get to (player builds
                // a Workbench on an island, etc.).
                int sxBill = (int)(s.SimPos.X / LocalMap.TileSize);
                int syBill = (int)(s.SimPos.Y / LocalMap.TileSize);
                if (!map.AreReachable(sxBill, syBill, tx, ty)) continue;

                for (int i = 0; i < bills.Count; i++)
                {
                    var bill = bills[i];
                    if (bill.Suspended != 0) continue;
                    var recipe = RecipeRegistry.Get(bill.RecipeId);
                    if (recipe == null) continue;

                    // Station compatibility — Workbench only runs Workbench
                    // recipes; CookingTable only runs CookingTable recipes.
                    if (!StationCompatible(slot.Type, recipe.Station)) continue;

                    // Skill gate.
                    if (SkillLevelSafe(s, recipe.PrimarySkill) < recipe.SkillMinimum) continue;
                    if (recipe.SecondarySkill != null
                        && SkillLevelSafe(s, recipe.SecondarySkill) < recipe.SecondaryMinimum) continue;

                    // TargetCount: skip if colony inventory already has enough output.
                    if (bill.Mode == BillRepeatMode.TargetCount)
                    {
                        int have = 0;
                        foreach (var output in recipe.Outputs)
                            have += r.Inventory.TotalBySubType(output.Kind, output.SubType);
                        if (have >= bill.TargetCount) continue;
                    }

                    // Ingredient availability.
                    if (!IngredientsAvailable(r, recipe)) continue;

                    var px = new Vector2(
                        tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    return new BehaviorTask(TaskType.DoBill, px, 60f,
                        interruptible: true,
                        tileX: tx, tileY: ty,
                        targetId: bill.RecipeId);
                }
            }
            return null;
        }

        public static void Apply(Shroomp s, BehaviorTask t, LocalMap? map, ColonyResources r)
        {
            if (map == null || r == null) return;
            if (t.TargetTileX < 0 || t.TargetTileY < 0) return;

            // Production station still present? Accept either Workbench or
            // CookingTable; bills are station-typed.
            var slot = map.GetStructure(t.TargetTileX, t.TargetTileY);
            if (slot.Type != StructureType.Workbench && slot.Type != StructureType.CookingTable)
            {
                s.CurrentTask = null;
                return;
            }

            // Re-find the bill by RecipeId (the player may have reordered
            // since SelectTarget). If not present, the player removed it
            // — drop the task. GetWorkbenchBills now returns a defensive
            // copy (Fix 1) — bill mutations below still propagate because
            // Bill is a class and the copy holds the same object refs.
            var bills = map.GetWorkbenchBills(t.TargetTileX, t.TargetTileY);
            int idx = -1;
            for (int i = 0; i < bills.Count; i++)
                if (bills[i].RecipeId == t.TargetId) { idx = i; break; }
            if (idx < 0)
            {
                s.CurrentTask = null;
                return;
            }
            var bill = bills[idx];
            var recipe = RecipeRegistry.Get(bill.RecipeId);
            if (recipe == null) { s.CurrentTask = null; return; }

            // Ingredients vanished since SelectTarget? Drop and re-pick.
            if (!IngredientsAvailable(r, recipe))
            {
                s.CurrentTask = null;
                return;
            }

            // Advance work. v1: 1 tick per Apply, scaled by skill factor.
            // Effective recipe WorkTicks scales by station: full speed at
            // the proper station, multiplied by BonfireSpeedMul at Bonfire
            // (so a 240-tick CookMeal becomes 480 at a Bonfire fallback).
            int skill = SkillLevelSafe(s, recipe.PrimarySkill);
            float speedMul = 0.5f + skill * 0.05f;   // lvl 0 = 0.5×, lvl 20 = 1.5×
            int advance = Mathf.Max(1, (int)(1 * speedMul + 0.5f));
            bill.ProgressTicks += advance;
            s.TaskDidWork = true;

            int effectiveWorkTicks = recipe.WorkTicks;
            if (recipe.AllowBonfireFallback && slot.Type == StructureType.Bonfire)
                effectiveWorkTicks = (int)(recipe.WorkTicks * recipe.BonfireSpeedMul);
            if (bill.ProgressTicks < effectiveWorkTicks) return;

            // ── Complete the craft ──
            // Consume ingredients.
            foreach (var ing in recipe.Ingredients)
            {
                if (ing.RequiredSubType != null && ing.MaterialFamily != null)
                    r.Inventory.ConsumeByMaterial(ing.Kind, ing.MaterialFamily, ing.RequiredSubType, ing.Amount);
                else if (ing.MaterialFamily != null)
                    r.Inventory.ConsumeByFamily(ing.Kind, ing.MaterialFamily, ing.Amount);
                else
                    r.Inventory.ConsumeByKind(ing.Kind, ing.Amount);
            }
            // Produce outputs at the station tile (joins the haul flow).
            var rng = new System.Random();
            foreach (var output in recipe.Outputs)
            {
                var item = ItemFactory.Create(
                    output.Kind, output.SubType,
                    new MaterialKey(output.MaterialFamily, output.MaterialSubType),
                    rng,
                    0,
                    skillLevel: skill,
                    quantity: output.Amount);
                item.TilePos = new Vector2(
                    t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                    t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                map.DropItem(item);
            }
            // XP — primary skill always; secondary skill at half rate.
            SkillRegistry.GainXp(s, recipe.PrimarySkill, recipe.XpReward);
            if (recipe.SecondarySkill != null)
                SkillRegistry.GainXp(s, recipe.SecondarySkill, recipe.XpReward * 0.5f);

            // Bill bookkeeping.
            bill.ProgressTicks = 0;
            if (bill.Mode == BillRepeatMode.RepeatCount)
            {
                if (bill.RepeatsRemaining > 0) bill.RepeatsRemaining--;
                if (bill.RepeatsRemaining == 0)
                {
                    // v0.6.2 Fix 1 — atomic remove-by-RecipeId. The cached
                    // idx from above may be stale if the player edited the
                    // queue between SelectTarget and now; the by-id remove
                    // locks + finds + removes in one critical section, so
                    // we always remove THIS bill (not its index-neighbour).
                    map.RemoveWorkbenchBillByRecipeId(t.TargetTileX, t.TargetTileY, bill.RecipeId);
                }
            }

            // Task ends here — SelectTask will pick the next bill on the
            // next tick (same or different station).
            s.CurrentTask = null;
        }

        private static bool IngredientsAvailable(ColonyResources r, RecipeDef recipe)
        {
            foreach (var ing in recipe.Ingredients)
            {
                int have;
                if (ing.RequiredSubType != null)
                    have = r.Inventory.TotalBySubType(ing.Kind, ing.RequiredSubType);
                else if (ing.MaterialFamily != null)
                    have = r.Inventory.TotalByFamily(ing.Kind, ing.MaterialFamily);
                else
                    have = r.Inventory.TotalByKind(ing.Kind);
                if (have < ing.Amount) return false;
            }
            return true;
        }

        private static int SkillLevelSafe(Shroomp s, string skillName)
        {
            return s.Skills.TryGetValue(skillName, out int v) ? v : 0;
        }

        // Recipe station ↔ structure-type compatibility.
        //   Workbench    accepts only RecipeStation.Workbench recipes.
        //   CookingTable accepts RecipeStation.CookingTable recipes.
        //   Bonfire bills NOT accepted via the bill UI today — the auto-cook
        //   fallback in CookSystem handles Bonfire as a low-throughput
        //   convenience. (Hook AllowBonfireFallback is wired in Apply above
        //   so a future "Bonfire bill" UI just needs to add a case here.)
        private static bool StationCompatible(StructureType station, RecipeStation recipeStation)
        {
            return (station, recipeStation) switch
            {
                (StructureType.Workbench,    RecipeStation.Workbench)    => true,
                (StructureType.CookingTable, RecipeStation.CookingTable) => true,
                _                                                        => false,
            };
        }
    }
}
