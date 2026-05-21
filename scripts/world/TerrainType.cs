namespace Sporeholm.World
{
    public enum TerrainType
    {
        Water,
        Mud,
        Sand,
        Grass,
        ForestFloor,
        Boulder,     // pebble/rock face at Shroomp scale; impassable; yields Stone on excavation
        MagicGrove,
        DeadLog,     // fallen branch/log; impassable; yields Dead Wood on excavation
        LivingWood,  // living stump/log; impassable; yields Living Wood on excavation; rarer than DeadLog
        // v0.4.37 — passable shallow water. Pads every Water tile with a
        // 1-tile ring so shroomps can wade between deep channels without
        // a Shroombridge. Modelled after RimWorld's shallow / moving
        // water tiles. Shroombridges (§5.11.d) can still be built over
        // Shallows to remove the wading speed penalty entirely. Fords
        // on river crossings are explicit Shallows so the Crossing
        // river subtype reads visually as "wadeable shallows" instead
        // of the old "sand strip across blue water" treatment.
        Shallows,
        // v0.5.16 — partial buried skeletons. Impassable like Boulder /
        // DeadLog but drops Bone material on excavation. Visually
        // suggests rib-bones / partial skull poking out of the ground
        // (renderer treats as off-white tile with bone glyph). Placed
        // by LocalMapGenerator.ScatterSkeletons in 1-3-tile clusters
        // representing fragments of larger creatures, scaled to shroomp
        // perspective. Sam: "imitate the look of a rib bone or partial
        // animal skull poking out of the ground." Provides early-game
        // Bone material before Phase 8 animal butchery lands.
        Skeleton,
    }
}
