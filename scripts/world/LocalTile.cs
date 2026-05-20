namespace Sporeholm.World
{
    public struct LocalTile
    {
        public TerrainType Terrain;
        public float Fertility;
        public bool Passable;

        // Phase 3.21 — player-issued designation flags. The behavior system
        // scans these to pick task targets; the renderer draws an overlay on
        // every flagged tile. Only one designation type may be active per tile —
        // setting one clears the others (enforced by LocalMap.SetXDesignation).
        public bool DesignatedForExcavation; // Boulder / DeadLog / LivingWood
        public bool DesignatedForGather;     // food-yielding vegetation
        // v0.3.38 — wood-yielding shroom "Chop Wood" order (LargeMushroom,
        // LargeSandshroom, PalmShroom). Same harvest mechanic as Gather but
        // produces Fungal Wood and may flip tile passability when the cap
        // clears (LargeMushroom variants are impassable until depleted).
        public bool DesignatedForChopWood;
        // v0.3.38 — generic "Cut plants" order — any vegetation in the rect.
        // Harvests + clears the vegetation slot. No resource drop (intended
        // for clearing playfield space, not gathering).
        public bool DesignatedForCut;

        // v0.5.84t — per-tile roof flag (RimWorld parity: RoofGrid.cs stores
        // a parallel per-tile RoofDef grid). True for any tile inside a
        // solid mass at worldgen: Boulder / DeadLog / LivingWood / Skeleton
        // terrain, plus any passable cave-interior tile (≥3 impassable
        // cardinal neighbours) carved by LocalMapGenerator.CarveUniversalCaves.
        // PERSISTS through mining — when a Boulder is excavated, the
        // resulting passable Mud tile keeps IsRoofed=true, producing
        // RimWorld's "you dug a cave with a natural roof" effect.
        // Constructed roofs (player-built ceilings) deferred to v0.6+.
        // Read by ItemDeteriorationSystem.ResolveInsulationMul +
        // future weather/rain dodge logic. Renderer applies a subtle
        // dark tint multiplier on roofed tiles so players can see the
        // ceiling at a glance.
        public bool IsRoofed;

        // Populated in later phases:
        public int ResourceId;    // Phase 4
        public int StructureId;   // Phase 5
        public int OccupantId;    // Phase 6
    }
}
