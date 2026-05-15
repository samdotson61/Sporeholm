using System.Collections.Generic;
using SmurfulationC.Simulation.Items;

namespace SmurfulationC.World
{
    // v0.5.0 (Phase 5A — rimport N1) — RimWorld-style storage priority. Six
    // levels in RimWorld's enum, but Phase 5A ships with four to keep the
    // initial UI simple. Two more (Preferred / Unstored) can land in a
    // later sub-phase if the colony micro-management surface grows.
    //
    // Hauler decision: walks `LocalMap.GetStockpileZones()` ordered by
    // priority descending; for each zone whose AcceptedKinds contains the
    // item's Kind, picks the closest cell that has spare stack capacity
    // under the existing v0.4.30 250-cap + type-lock per-tile rules.
    public enum StoragePriority
    {
        Low      = 0,
        Normal   = 1,
        Important = 2,
        Critical = 3,
    }

    // A single player-painted stockpile zone. Cells are an arbitrary set
    // (not a rectangle) so the painter can extend an existing zone in any
    // shape. AcceptedKinds is the v0.5.0 minimum-viable filter — Phase 5C
    // can layer a recursive ItemFilter category tree on top per the rimport
    // adoption notes; the full filter is just a more specific set membership
    // test from a hauler's POV, so this API doesn't need to change to
    // accommodate it later.
    public sealed class StockpileZone
    {
        public int    Id   { get; }
        public string Name { get; set; }

        // Cells in this zone. Order is insertion order; haul-target
        // selection iterates and computes per-cell distance, so order
        // doesn't matter for correctness. A cell may belong to AT MOST
        // ONE zone (enforced at paint time by `LocalMap`).
        public List<(int X, int Y)> Cells { get; } = new();

        // Which item Kinds this zone accepts. Empty = accept all (matches
        // the RimWorld "default storage settings" everything-allowed
        // baseline). Player can edit via the stockpile panel (Phase 5C).
        public HashSet<ItemKind> AcceptedKinds { get; } = new();

        public StoragePriority Priority { get; set; } = StoragePriority.Normal;

        public StockpileZone(int id, string name = "Stockpile")
        {
            Id   = id;
            Name = name;
        }

        public bool Accepts(Item item)
        {
            if (item == null) return false;
            if (AcceptedKinds.Count == 0) return true;   // empty = accept all
            return AcceptedKinds.Contains(item.Kind);
        }
    }
}
