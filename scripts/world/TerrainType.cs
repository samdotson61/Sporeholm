namespace SmurfulationC.World
{
    public enum TerrainType
    {
        Water,
        Mud,
        Sand,
        Grass,
        ForestFloor,
        Boulder,     // pebble/rock face at Smurf scale; impassable; yields Stone on excavation
        MagicGrove,
        DeadLog,     // fallen branch/log; impassable; yields Dead Wood on excavation
        LivingWood,  // living stump/log; impassable; yields Living Wood on excavation; rarer than DeadLog
        // v0.4.37 — passable shallow water. Pads every Water tile with a
        // 1-tile ring so smurfs can wade between deep channels without
        // a Shroombridge. Modelled after RimWorld's shallow / moving
        // water tiles. Shroombridges (§5.11.d) can still be built over
        // Shallows to remove the wading speed penalty entirely. Fords
        // on river crossings are explicit Shallows so the Crossing
        // river subtype reads visually as "wadeable shallows" instead
        // of the old "sand strip across blue water" treatment.
        Shallows,
    }
}
