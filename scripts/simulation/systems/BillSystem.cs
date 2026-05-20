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
    // Flow:
    //   1. SelectTarget — scan every workbench's bills list. For each
    //      active bill (not Suspended), check (a) shroomp meets skill
    //      requirements, (b) recipe ingredients are available in colony
    //      inventory, (c) TargetCount mode: colony doesn't already have
    //      ≥ TargetCount of output. First match wins, return a DoBill
    //      task pointing at that workbench tile with the bill index as
    //      TargetId.
    //
    //   2. Apply — on arrival at the workbench, advance the bill's
    //      ProgressTicks by 1 tick × SkillCurve.CraftingSpeedFactor(skill).
    //      When ProgressTicks ≥ recipe.WorkTicks, consume the ingredients
    //      from colony inventory, produce the output items on the
    //      workbench tile (join the haul flow), grant XP, decrement
    //      RepeatsRemaining (if RepeatCount mode), reset ProgressTicks.
    //      Auto-remove bill if RepeatCount hits 0.
    //
    // The pre-Phase 5.5 CookSystem auto-cook remains as the fallback for
    // workbenches with no bills — players who don't queue any recipes
    // still get the prepared-meals loop from v0.5.22.
    public static class BillSystem
    {
        // Try to find a satisfiable bill for this shroomp. Returns a
        // DoBill task or null if no work available. Encodes the chosen
        // bill index in TargetId so Apply can locate it without
        // re-walking the bills list (the player could re-order between
        // SelectTarget and Apply; the index is a fingerprint, not a
        // reservation — if it shifts, Apply re-finds by RecipeId).
        public static BehaviorTask? SelectTarget(Shroomp s, LocalMap? map, ColonyResources r)
        {
            if (map == null || r == null) return null;

            // Iterate every workbench with at least one bill.
            foreach (var ((wx, wy), bills) in map.AllWorkbenchBills())
            {
                // Verify the workbench is still present (could be demolished
                // between AddWorkbenchBill and now).
                var slot = map.GetStructure(wx, wy);
                if (slot.Type != StructureType.Workbench) continue;

                for (int i = 0; i < bills.Count; i++)
                {
                    var bill = bills[i];
                    if (bill.Suspended != 0) continue;
                    var recipe = RecipeRegistry.Get(bill.RecipeId);
                    if (recipe == null) continue;

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
                        wx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        wy * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    // Encode bill INDEX in TargetId for Apply lookup.
                    return new BehaviorTask(TaskType.DoBill, px, 60f,
                        interruptible: true,
                        tileX: wx, tileY: wy,
                        targetId: bill.RecipeId);   // recipe id is the stable identifier; index is recoverable from it
                }
            }
            return null;
        }

        public static void Apply(Shroomp s, BehaviorTask t, LocalMap? map, ColonyResources r)
        {
            if (map == null || r == null) return;
            if (t.TargetTileX < 0 || t.TargetTileY < 0) return;

            // Workbench still present?
            var slot = map.GetStructure(t.TargetTileX, t.TargetTileY);
            if (slot.Type != StructureType.Workbench)
            {
                s.CurrentTask = null;
                return;
            }

            // Re-find the bill by RecipeId (the player may have reordered
            // since SelectTarget). If not present, the player removed it
            // — drop the task.
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
            // (Future polish: per-tick speed factor reading SkillCurve
            // proper — for now a simple linear bonus.)
            int skill = SkillLevelSafe(s, recipe.PrimarySkill);
            float speedMul = 0.5f + skill * 0.05f;   // lvl 0 = 0.5×, lvl 20 = 1.5×
            int advance = Mathf.Max(1, (int)(1 * speedMul + 0.5f));
            bill.ProgressTicks += advance;
            s.TaskDidWork = true;

            if (bill.ProgressTicks < recipe.WorkTicks) return;

            // ── Complete the craft ──
            // Consume ingredients.
            foreach (var ing in recipe.Ingredients)
            {
                // v0.5.84t — null MaterialFamily means "any family of this Kind".
                if (ing.RequiredSubType != null && ing.MaterialFamily != null)
                    r.Inventory.ConsumeByMaterial(ing.Kind, ing.MaterialFamily, ing.RequiredSubType, ing.Amount);
                else if (ing.MaterialFamily != null)
                    r.Inventory.ConsumeByFamily(ing.Kind, ing.MaterialFamily, ing.Amount);
                else
                    r.Inventory.ConsumeByKind(ing.Kind, ing.Amount);
            }
            // Produce outputs at the workbench tile (joins the haul flow).
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
                    // Auto-remove on exhaustion.
                    map.RemoveWorkbenchBill(t.TargetTileX, t.TargetTileY, idx);
                }
            }

            // Task ends here — SelectTask will pick the next bill on the
            // next tick (same or different workbench).
            s.CurrentTask = null;
        }

        private static bool IngredientsAvailable(ColonyResources r, RecipeDef recipe)
        {
            foreach (var ing in recipe.Ingredients)
            {
                int have;
                // v0.5.84t — null MaterialFamily means "any family of this Kind".
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
    }
}
