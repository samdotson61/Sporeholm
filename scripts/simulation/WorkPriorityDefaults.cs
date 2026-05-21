using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // v0.3.47 (Phase 4 sub-B) — RimWorld-style work priority categories +
    // per-role defaults. The Jobs tab grid renders one column per Category
    // and one row per shroomp; SelectTask in BehaviorSystem queries the per-
    // shroomp dict to gate which tasks the shroomp considers (off = blacklist;
    // 1 = always, 4 = only when nothing else available).
    //
    // Category names are stable strings — never change once a save exists,
    // or pre-edit saves will lose their per-shroomp priorities. New
    // categories can be appended without breaking back-compat.
    public static class WorkPriorityDefaults
    {
        // Categories in the order they appear left-to-right in the Jobs grid.
        // Matches the RimWorld convention loosely; reorder if the player
        // workflow needs it.
        public static readonly string[] Categories =
        {
            "Patient",     // be a patient when injured — Phase 7+
            "BedRest",     // rest in bed when low Rest — Phase 5+
            "Healer",      // heal others — Caretaker
            "Construct",   // build structures — Phase 5+
            "Mine",        // excavate
            "PlantCut",    // cut/clear plants
            "Grow",        // farm — Phase 8+
            "Cook",        // cook meals — Phase 5+
            "Hunt",        // hunt — Phase 8+
            "Forage",      // gather wild food
            "Chop",        // chop wood
            "Haul",        // carry items to stockpiles — Phase 5+
            "Clean",       // clean — Phase 5+
            "Study",    // research — Phase 11+
            "Attune",      // Mage attunement
            "Craft",       // v0.5.84s — Phase 5.5 bills at workbenches
        };

        // Default priority table per role. Empty entry = "off". Shroomps
        // inherit these at creation; the player can override per-shroomp
        // in the Jobs tab.
        public static readonly Dictionary<string, Dictionary<string, byte>> ByRole = new()
        {
            ["Forager"]    = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Forage"] = 1, ["PlantCut"] = 2, ["Chop"] = 3,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Crafter"]    = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Construct"] = 1, ["Craft"] = 1,   // v0.5.84s Phase 5.5
                ["Mine"] = 1, ["Chop"] = 2,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Scholar"]    = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Study"] = 1, ["Healer"] = 3,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Sage"]       = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Attune"] = 1, ["Study"] = 2,
                ["Haul"] = 4, ["Clean"] = 4,
            },
            ["Caretaker"]  = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Healer"] = 1, ["Cook"] = 2, ["Craft"] = 2,   // v0.5.84s — Caretaker brews medicine
                ["Haul"] = 3, ["Clean"] = 3,
            },
            ["Guardian"]   = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Hunt"] = 1, ["Mine"] = 3, ["Chop"] = 3,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Elder"]      = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Cook"] = 1, ["Healer"] = 2,
                ["Haul"] = 4, ["Clean"] = 4, ["Study"] = 2,
            },
            ["Unassigned"] = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Haul"] = 3, ["Clean"] = 4,
            },
        };

        // v0.5.40 — display label per category. Internal keys stay stable
        // (Mine / PlantCut / Forage / Construct / Chop) so save-loaded
        // shroomps keep their priorities, but the JobsPanel header shows
        // the in-game verb the player recognises (Excavate / Cut /
        // Gather / Build / Chop). Tooltip text explains what the column
        // actually gates so a player who sets Excavate=2 can be sure
        // their Crafter prioritises boulders accordingly.
        public static string DisplayLabel(string category) => category switch
        {
            "Construct" => "Build",
            "Mine"      => "Excavate",
            "PlantCut"  => "Cut",
            "Forage"    => "Gather",
            "Chop"      => "Chop",
            "Haul"      => "Haul",
            "Cook"      => "Cook",
            "Healer"    => "Healer",
            "Craft"     => "Craft",   // v0.5.84s
            "Patient"   => "Patient",
            "BedRest"   => "BedRest",
            "Grow"      => "Grow",
            "Hunt"      => "Hunt",
            "Clean"     => "Clean",
            "Study"  => "Study",
            "Attune"    => "Attune",
            _           => category,
        };

        // v0.5.40 — per-column tooltip explaining what the priority gates.
        // Surfaces the underlying task type + the designation it responds
        // to so the player can map "Excavate=2" → "this shroomp does the
        // boulder/dead-log/living-wood Excavate work at priority 2."
        public static string DisplayTooltip(string category) => category switch
        {
            "Construct" => "Build blueprints (walls / floors / doors / shelves / workbenches / hearths / beds / joy furniture / tables). Set to '-' to forbid this shroomp from any construction.",
            "Mine"      => "Excavate impassable terrain (boulders, dead logs, living wood, skeletons) — produces Stone Blocks, Wood Logs, Bone. Set to '-' to forbid mining.",
            "PlantCut"  => "Cut vegetation (clear the tile + drop its relevant resource — Fungal Wood from large shrooms, food from food-yielders, Cuttings from undergrowth/moss). Used both for clearing plant tiles and for prepping a tile under a blueprint. Set to '-' to forbid cutting.",
            "Forage"    => "Gather wild food (Capberry Bush, Small Mushroom, Herb Cluster, Magic Flower). Set to '-' to forbid foraging.",
            "Chop"      => "Chop wood-yielding shrooms (Large Mushroom, Palm Shroom, Large Sandshroom) — produces Wood Logs. Set to '-' to forbid chopping.",
            "Haul"      => "Carry items to stockpile zones or shelves. No haul work runs when no storage exists (v0.5.39). Set to '-' to forbid hauling.",
            "Cook"      => "Cook prepared meals at a workbench from raw food. Set to '-' to forbid cooking.",
            "Healer"    => "Heal others when they are injured. Set to '-' to forbid doctoring (Phase 7 hediffs land here).",
            "Craft"     => "Take recipes from workbench bills queues and produce items (food / cloth / tools / medicine). Set to '-' to forbid this shroomp from crafting at workbenches.",
            "Patient"   => "Be a patient when injured (rest, accept tending). Set to '-' if this shroomp should not rest when injured.",
            "BedRest"   => "Sleep in a bed when Rest is low. v0.5.35 wired bed-routing through this priority.",
            "Grow"      => "Plant + harvest farm plots. Set to '-' to forbid farming (Phase 8 lands the full surface).",
            "Hunt"      => "Hunt wild creatures for meat / hide. Set to '-' to forbid (Phase 8 lands the full surface).",
            "Clean"     => "Clean dirty tiles. Set to '-' to forbid (Phase 5+ polish; currently a stub).",
            "Study"  => "Research at a research bench. Set to '-' to forbid (Phase 11 lands the tech tree).",
            "Attune"    => "Mage attunement — restores MagicResonance. Set to '-' to forbid.",
            _           => $"Set this shroomp's '{category}' priority. Click cycles 1-2-3-4-off; right-click sets off.",
        };

        // Map task type → category string. BehaviorSystem uses this to
        // look up the shroomp's priority for the task it's considering.
        public static string? CategoryFor(TaskType t) => t switch
        {
            TaskType.GatherFood     => "Forage",
            TaskType.GatherMaterial => "Mine",
            TaskType.ChopWood       => "Chop",
            TaskType.CutVegetation  => "PlantCut",
            TaskType.Build          => "Construct",
            TaskType.BuildHaul      => "Haul",        // v0.5.60 — material delivery
            TaskType.Heal           => "Healer",
            TaskType.Research       => "Study",
            TaskType.Guard          => "Hunt",
            TaskType.Attune         => "Attune",
            TaskType.Sleep          => "BedRest",
            TaskType.Haul           => "Haul",        // v0.4.36
            TaskType.Cook           => "Cook",
            TaskType.DoBill         => "Craft",       // v0.5.84s — Phase 5.5
            _ => null,
        };

        // Apply the role defaults to a shroomp at creation. Existing entries
        // are left alone so save-loaded shroomps don't get overwritten.
        public static void ApplyRoleDefaults(Shroomp s)
        {
            if (s.WorkPriorities == null) s.WorkPriorities = new();
            if (!ByRole.TryGetValue(s.Role, out var defaults))
            {
                ByRole.TryGetValue("Unassigned", out defaults);
            }
            if (defaults == null) return;
            foreach (var (cat, prio) in defaults)
                if (!s.WorkPriorities.ContainsKey(cat))
                    s.WorkPriorities[cat] = prio;
        }

        // v0.5.84r — legacy category-key migration. Sam: "change Doctor
        // job to Healer ... Research should be changed to Study." Applied
        // at Shroomp load so old saves' WorkPriorities dicts rename their
        // keys without losing the player's per-shroomp priority settings.
        public static readonly Dictionary<string, string> LegacyCategoryMigration = new()
        {
            { "Doctor",   "Healer" },
            { "Research", "Study" },
        };

        public static void MigrateLegacyCategoryKeys(Dictionary<string, byte> priorities)
        {
            foreach (var (oldKey, newKey) in LegacyCategoryMigration)
            {
                if (priorities.TryGetValue(oldKey, out byte v) && !priorities.ContainsKey(newKey))
                {
                    priorities[newKey] = v;
                    priorities.Remove(oldKey);
                }
            }
        }
    }
}
