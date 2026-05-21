using System.Collections.Generic;

namespace Sporeholm.Simulation.Entities
{
    // v0.6.0 (Phase 6 — Roadmap §6.1 / §6.2) — static per-species
    // definition. One row per EntityKind. Read by EntityRegistry +
    // EntitySpawnSystem + EntityColonyView. All values are species-level
    // baselines; per-individual jitter (within ±10 %) is applied at
    // instantiation in Entity.SpawnAt so two Wolves of the same species
    // aren't identical clones.
    public enum Disposition : byte
    {
        Friendly = 0,   // Glowbunny / Shroomalo: never attack; flee on damage
        Neutral  = 1,   // BonecrestBeetle / Squirrel: ignore shroomps, fight when cornered
        Hostile  = 2,   // Wolf / Snake: hunt shroomps on sight within Aggro range
    }

    public enum EntityClass : byte
    {
        Mammal      = 0,
        Insect      = 1,
        Reptile     = 2,
        Crustacean  = 3,
        Bird        = 4,
        Mythical    = 5,
    }

    // Bitmask of biome ids — kept narrow so a row's HabitatBiomes can
    // declare "F | G | H" inline.
    [System.Flags]
    public enum BiomeTag : ushort
    {
        None        = 0,
        Forest      = 1 <<  0,
        Plains      = 1 <<  1,
        Hills       = 1 <<  2,
        Mountains   = 1 <<  3,
        Peaks       = 1 <<  4,
        Desert      = 1 <<  5,
        Swamp       = 1 <<  6,
        Coast       = 1 <<  7,
        Island      = 1 <<  8,
        MagicGrove  = 1 <<  9,
        Caves       = 1 << 10,   // tile-feature, not biome — populated post-gen
        AllOutdoor  = Forest | Plains | Hills | Mountains | Peaks | Desert | Swamp | Coast | Island | MagicGrove,
    }

    public sealed record EntityDef(
        EntityKind   Kind,
        string       DisplayName,
        string       Description,        // v0.6.2 — one-sentence flavor blurb shown on EntityCardPanel
        EntityClass  Class,
        Disposition  Disposition,
        BiomeTag     HabitatBiomes,
        // ── physical stats ─────────────────────────────────────────────
        float        MaxHealth,
        float        BaseSpeedPxPerSec,
        float        BodyRadiusPx,      // sprite logical half-size; drives selection hit-box
        float        AttackPower,       // 0 = non-combatant
        float        AggroRangePx,      // 0 = doesn't proactively chase; > 0 = Hostile chase range
        float        FleeRangePx,       // distance from threat that triggers Flee
        // ── ecology + spawning ─────────────────────────────────────────
        int          MinGroupSize,      // pack size lower bound at spawn
        int          MaxGroupSize,      // upper bound
        int          PopulationCapPerMap, // soft cap; EntitySpawnSystem won't exceed
        float        SpawnWeight,       // ambient-respawn weight if no spawn point
        // ── drops (Phase 9 hook; consulted by EntitySystem.Butcher) ────
        // Each (itemSubType, minCount, maxCount) line drops on butcher.
        IReadOnlyList<(string SubType, int Min, int Max)> ButcherDrops
    );
}
