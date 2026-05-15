namespace SmurfulationC.World
{
    public struct WorldTile
    {
        public float Elevation;
        public float Rainfall;
        public float Temperature;
        public float MagicDensity;
        public BiomeType Biome;
        public int LocalSeed;
        public bool Explored;
        public bool Passable;    // false for Pondsea tiles — colony cannot land here
        public bool IsCoastal;   // true when cardinally adjacent to a Pondsea tile, or when Biome == Island;
                                 // grants +0.15 fertility on all local map tiles and unlocks coastal vegetation
        public bool HasRiver;    // Phase 2.6: river tile — local map carves a meandering Water+Mud channel through it
        // v0.4.39 — set on river tiles whose four cardinal neighbours are NOT
        // also HasRiver. Marks the tile as a single-cell river seed rather
        // than part of a snaking chain — the local map renders these as
        // "Creek" subtype (1-3 thin rocky Shallows-bed streams) instead of
        // a full deep river channel.
        public bool IsRiverOrphan;
    }
}
