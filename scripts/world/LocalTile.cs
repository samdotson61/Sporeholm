namespace SmurfulationC.World
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

        // Populated in later phases:
        public int ResourceId;    // Phase 4
        public int StructureId;   // Phase 5
        public int OccupantId;    // Phase 6
    }
}
