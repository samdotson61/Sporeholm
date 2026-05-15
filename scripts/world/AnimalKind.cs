namespace SmurfulationC.World
{
    // v0.5.14 (Phase 5C — rimport.md N19) — wildlife spawn-point stub.
    // Full Animal system (creature data + AI + behaviour) lands in Phase 9
    // per rimport.md N12 ("Animals can ship as a strict subset of Smurf
    // — same BodyParts + Needs + simplified BehaviorSystem"). Until then,
    // generation places `AnimalSpawnPoint` markers per biome's faunal
    // table; the markers are inert (no creature actually spawns yet) but
    // serve as anchored locations the Phase 9 system will read when it
    // populates the map.
    //
    // Smurf-flavoured fauna roster (rimport.md §21):
    //   • MushroomGrazer  — passive food source (small herd grazer,
    //                       comparable to RimWorld's deer/alpaca)
    //   • ShroombackBeetle — passive hauler post-tame (slow-moving
    //                       beetle that smurfs can train to carry items)
    //   • WildRat         — predator (stealth-attacks corpses, harasses
    //                       small smurfs)
    //
    // Three is enough for the first faunal pass. Phase 9 can extend to
    // half a dozen per biome with proper spawn weights.
    public enum AnimalKind : byte
    {
        MushroomGrazer,
        ShroombackBeetle,
        WildRat,
    }

    // Tile-anchored spawn point. The Phase 9 system will read this list
    // when populating the map and roll an actual creature at each point
    // with AnimalKind-appropriate stats. For v0.5.14 the list is just
    // generation output; nothing else consumes it yet.
    public readonly record struct AnimalSpawnPoint(int X, int Y, AnimalKind Kind);
}
