namespace Sporeholm.World
{
    // v0.5.14 (Phase 5C — rimport.md N19) — wildlife spawn-point stub.
    // Full Animal system (creature data + AI + behaviour) lands in Phase 9
    // per rimport.md N12 ("Animals can ship as a strict subset of Shroomp
    // — same BodyParts + Needs + simplified BehaviorSystem"). Until then,
    // generation places `AnimalSpawnPoint` markers per biome's faunal
    // table; the markers are inert (no creature actually spawns yet) but
    // serve as anchored locations the Phase 9 system will read when it
    // populates the map.
    //
    // v0.5.15 — species aligned with the Phase 9 roadmap roster
    // (Roadmap §9.1, lines 2020-2029) instead of v0.5.14's generic
    // placeholders (MushroomGrazer / ShroombackBeetle / WildRat). Using
    // the roadmap's specific species names means the spawn points placed
    // now will be consumed directly by the Phase 9 implementation
    // without renaming.
    //
    // Shroomp-flavoured fauna roster:
    //
    //   MushroomGoat    — Tameable, Grazer, Milkable (Fungal Milk),
    //                     Shearable (Spore Wool), Butcherable, Breeds.
    //                     The colony's primary husbandry animal.
    //   BonecrestBeetle — Tameable, Pack (post-tame hauler), Carnivore.
    //                     Replaces the v0.5.14 ShroombackBeetle stub.
    //   CaveLizard      — Tameable (hard), Hunt Animal, Carnivore, Breeds.
    //                     The wild predator role; future hunter
    //                     companion.
    //   GlowBunny       — Tameable, Pet, Butcherable (Meat, Glow-Fur),
    //                     Grazer, Breeds. Light passive prey + cosmetic
    //                     pet.
    //   ForestBoar      — Tameable (hard), War Animal, Omnivore,
    //                     Butcherable, Breeds. Aggressive forest predator
    //                     that becomes a war mount when tamed.
    //
    // Additional Phase 9 species (HoneyBeeSwarm, ShoreFrog, Pegasus,
    // SkyPony) defer to the full Phase 9 implementation — they need
    // beekeeper sub-skill, biome-specific spawning rules, or late-game
    // era unlocks that don't fit the v0.5.x stub layer.
    public enum AnimalKind : byte
    {
        MushroomGoat,
        BonecrestBeetle,
        CaveLizard,
        GlowBunny,
        ForestBoar,
    }

    // Tile-anchored spawn point. The Phase 9 system will read this list
    // when populating the map and roll an actual creature at each point
    // with AnimalKind-appropriate stats. For v0.5.14 the list is just
    // generation output; nothing else consumes it yet.
    public readonly record struct AnimalSpawnPoint(int X, int Y, AnimalKind Kind);
}
