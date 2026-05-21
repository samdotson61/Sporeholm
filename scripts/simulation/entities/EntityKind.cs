namespace Sporeholm.Simulation.Entities
{
    // v0.6.0 (Phase 6 — Roadmap §6.2) — canonical species identifier.
    // Stored on Entity rows + on Phase 8 husbandry tags + on
    // SaveManager.EntitySaveData. Adding a species ALWAYS requires a new
    // enum value (never reuse a slot) so old saves can round-trip.
    //
    // The 15 species below are the v0.6.0 Phase 6 baseline drawn from
    // Roadmap §6.2's Non-Hostile / Neutral / Hostile tables. Each was
    // selected to (a) cover the full disposition spread, (b) span the
    // major biome groups (forest, plains, hills, mountains, caves,
    // coast, swamp, desert, magic grove), and (c) include at least one
    // canon "original Sporeholm creation" per Sam's directive. Roster
    // additions land in v0.6.0+ once the core data model + AI + spawn
    // pipeline + sprite rendering have all been play-tested. Event-only
    // ≥10× species (Bear / Leopard Tortoise / Mauler / Dragon /
    // Mushroom Drake) deliberately deferred — they spawn via Phase 9
    // Storyteller events, not the wild-spawn pathway.
    public enum EntityKind : byte
    {
        // Non-Hostile (Friendly / Passive)
        Glowbunny       = 0,   // F/G/H  herbivore (Phase 8 livestock candidate)
        Shroomgoat      = 1,   // H/F/G  herbivore (Phase 8 primary livestock)
        Shroomalo       = 2,   // ALL biomes; very friendly Sporeholm canon
        Mouse           = 3,   // F/P/H/V tiny passive scavenger
        Ladybug         = 4,   // F/P/G  friendly insect pest control
        HermitCrab      = 5,   // C/I    coastal forager carrying shell
        // Neutral
        Squirrel        = 6,   // F/H    skittish; defends when cornered
        BonecrestBeetle = 7,   // P/H/M  Sporeholm canon; low-threat scavenger
        ForestBoar      = 8,   // F/H    medium-threat charge predator
        CaveLizard      = 9,   // V/M/K  cave ambush predator
        // Hostile
        AntSoldier      = 10,  // F/P/D  swarm attacker
        WaspRenegade    = 11,  // F/P/G  aerial venomous
        Snake           = 12,  // F/P/D  ambush predator
        Wolf            = 13,  // F/H/M  pack hunter
        MagicWisp       = 14,  // G      rare floating MagicResonance drain
    }
}
