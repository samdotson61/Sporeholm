using System.Collections.Generic;

namespace SmurfulationC.Simulation
{
    // v0.3.47 (Phase 4 sub-B) — RimWorld-style work priority categories +
    // per-role defaults. The Jobs tab grid renders one column per Category
    // and one row per smurf; SelectTask in BehaviorSystem queries the per-
    // smurf dict to gate which tasks the smurf considers (off = blacklist;
    // 1 = always, 4 = only when nothing else available).
    //
    // Category names are stable strings — never change once a save exists,
    // or pre-edit saves will lose their per-smurf priorities. New
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
            "Doctor",      // heal others — Caretaker
            "Construct",   // build structures — Phase 5+
            "Mine",        // excavate
            "PlantCut",    // cut/clear plants
            "Grow",        // farm — Phase 9+
            "Cook",        // cook meals — Phase 5+
            "Hunt",        // hunt — Phase 9+
            "Forage",      // gather wild food
            "Chop",        // chop wood
            "Haul",        // carry items to stockpiles — Phase 5+
            "Clean",       // clean — Phase 5+
            "Research",    // research — Phase 11+
            "Attune",      // Mage attunement
        };

        // Default priority table per role. Empty entry = "off". Smurfs
        // inherit these at creation; the player can override per-smurf
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
                ["Construct"] = 1, ["Mine"] = 1, ["Chop"] = 2,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Scholar"]    = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Research"] = 1, ["Doctor"] = 3,
                ["Haul"] = 3, ["Clean"] = 4,
            },
            ["Mage"]       = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Attune"] = 1, ["Research"] = 2,
                ["Haul"] = 4, ["Clean"] = 4,
            },
            ["Caretaker"]  = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Doctor"] = 1, ["Cook"] = 2,
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
                ["Cook"] = 1, ["Doctor"] = 2,
                ["Haul"] = 4, ["Clean"] = 4, ["Research"] = 2,
            },
            ["Unassigned"] = new()
            {
                ["Patient"] = 1, ["BedRest"] = 1,
                ["Haul"] = 3, ["Clean"] = 4,
            },
        };

        // Map task type → category string. BehaviorSystem uses this to
        // look up the smurf's priority for the task it's considering.
        public static string? CategoryFor(TaskType t) => t switch
        {
            TaskType.GatherFood     => "Forage",
            TaskType.GatherMaterial => "Mine",
            TaskType.ChopWood       => "Chop",
            TaskType.CutVegetation  => "PlantCut",
            TaskType.Build          => "Construct",
            TaskType.Heal           => "Doctor",
            TaskType.Research       => "Research",
            TaskType.Guard          => "Hunt",
            TaskType.Attune         => "Attune",
            TaskType.Sleep          => "BedRest",
            TaskType.Haul           => "Haul",        // v0.4.36
            _ => null,
        };

        // Apply the role defaults to a smurf at creation. Existing entries
        // are left alone so save-loaded smurfs don't get overwritten.
        public static void ApplyRoleDefaults(Smurf s)
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
    }
}
