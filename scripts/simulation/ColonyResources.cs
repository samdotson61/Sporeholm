using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

namespace SmurfulationC.Simulation
{
    // v0.3.46 (Phase 4 core) — the float ledger is now a derived aggregate
    // over the colony Inventory. Existing call sites read Food / Stone /
    // Wood / MagicEssence and get the live total quantity across all
    // matching items; legacy writers (`r.Food += 4f` patterns in
    // pre-v0.3.46 code) are still supported via a small "unstored
    // buffer" that floats outside the item system so older code paths
    // don't break mid-refactor. New code should call `Inventory.Add(...)`
    // directly.
    //
    // v0.4.36 — HUD totals also sum items lying on the LocalMap as
    // visible stockpiles. The v0.4.30 HaulSystem used to dual-write
    // every haul deposit into both the colony pool AND the map, which
    // accumulated catastrophically: after enough haul cycles the pool
    // counts overflowed int (negative HUD values seen in Sam's wood
    // log report). Dual-write removed in v0.4.36; the map walk replaces
    // it as the authoritative ground-stored aggregate, with the pool
    // kept for scenario starting inventory + future Phase-5 stockpile-
    // zone bookkeeping that hasn't landed yet.
    //
    // Snapshot() returns a value-only copy that the main thread can read
    // without lock contention — the same pattern as v0.3.0.
    public sealed class ColonyResources
    {
        public Inventory Inventory { get; } = new();
        public LocalMap? Map { get; set; }

        // Per-resource "unstored" floats. Mirrors the v0.3.0 ledger so
        // legacy gather-paths (`r.Stone += 4f`) keep working. The HUD's
        // "(unstored — Phase 5 will gate)" qualifier already calls this
        // out as transitional. v0.3.46 keeps these only for Stone / Wood /
        // Magic write paths in case something we missed adds via float;
        // Food is fully migrated to items.
        private float _unstoredFood, _unstoredStone, _unstoredWood, _unstoredMagic;

        public float Food
        {
            get => Inventory.TotalByKind(ItemKind.Food)
                 + MapCountByKind(ItemKind.Food)
                 + _unstoredFood;
            set => _unstoredFood = value;
        }
        public float Stone
        {
            get => Inventory.TotalByFamily(ItemKind.Material, "Stone")
                 + MapCountByFamily(ItemKind.Material, "Stone")
                 + _unstoredStone;
            set => _unstoredStone = value;
        }
        public float Wood
        {
            get => Inventory.TotalByFamily(ItemKind.Material, "Wood")
                 + MapCountByFamily(ItemKind.Material, "Wood")
                 + _unstoredWood;
            set => _unstoredWood = value;
        }
        public float MagicEssence
        {
            get => Inventory.TotalByKind(ItemKind.Magic)
                 + MapCountByKind(ItemKind.Magic)
                 + _unstoredMagic;
            set => _unstoredMagic = value;
        }

        // v0.4.50 — O(1) cached-total reads. Was a full EnumerateDroppedItems
        // walk per access (v0.4.36 baseline), which the HUD called eight
        // times per frame (4 kind getters × 2 callers — ResourceHUD +
        // HUDController). At 200+ dropped tiles under heavy gather/excavate,
        // each walk allocated an `(int,int,Item[])[]` of size N plus a
        // per-tile `Item[]`, so ~1600 array allocations / frame burned
        // main-thread time for sums that change only on Drop/Remove. The
        // totals are now maintained in LocalMap alongside `_droppedItems`
        // under the same lock; these getters reduce to one dictionary
        // lookup each.
        private int MapCountByKind(ItemKind kind) =>
            Map?.SumDroppedByKind(kind) ?? 0;

        private int MapCountByFamily(ItemKind kind, string family) =>
            Map?.SumDroppedByFamily(kind, family) ?? 0;

        // For UI snapshot copies — same shape as v0.3.0 (just the floats).
        // The actual item list is not duplicated here; the snapshot path
        // for the Resources panel walks Inventory.Items directly under
        // the SimulationCore lock.
        public ColonyResources Snapshot()
        {
            var copy = new ColonyResources();
            copy._unstoredFood  = Food;          // = inventory total + buffer
            copy._unstoredStone = Stone;
            copy._unstoredWood  = Wood;
            copy._unstoredMagic = MagicEssence;
            return copy;
        }
    }
}
