using System;
using Godot;

namespace Sporeholm.Simulation.Entities
{
    // v0.6.0 (Phase 6 — Roadmap §6.1) — game-world creature. Mirrors
    // the Shroomp data shape but stripped down: no skills, no traits, no
    // mood, no equipment, no work priorities. Just a position, a health
    // bar, an AI state, and a species reference.
    //
    // Threading: lives entirely on the sim thread. Renderer reads via
    // EntitySnapshot — never touches Entity instances directly.
    //
    // Per-individual jitter: stats like MaxHealth / Speed are seeded with
    // ±10 % variance against the EntityDef baseline so a pack of three
    // wolves isn't three clones. The variance is rolled once at spawn
    // and persists through save / load.
    public enum EntityState : byte
    {
        Wander  = 0,
        Flee    = 1,
        Hunt    = 2,
        Graze   = 3,
        Tamed   = 4,
        Dead    = 5,
    }

    public sealed class Entity
    {
        // ── identity ──────────────────────────────────────────────────
        public Guid       Id   { get; init; } = Guid.NewGuid();
        public EntityKind Kind { get; init; }
        // Snapshot-cached per-instance baseline (jittered ±10 % from
        // EntityRegistry.Get(Kind) at spawn). All systems read THESE
        // values, not the def's, so per-individual variance is honoured.
        public float MaxHealth      { get; set; }
        public float Speed          { get; set; }   // px/sec, pre-state mul
        public float AttackPower    { get; set; }
        // ── live state ────────────────────────────────────────────────
        public Vector2     SimPos    { get; set; }
        public Vector2     SimTarget { get; set; }
        public float       Health    { get; set; }
        public EntityState State     { get; set; } = EntityState.Wander;
        // RNG seed local to this entity — keeps wander targets deterministic
        // per-individual across save/load (re-seeds with same value).
        public int RandomSeed { get; set; }
        // When State == Hunt, this is the shroomp Guid being chased.
        // When State == Flee, the source of the threat.
        public Guid? TargetShroompId { get; set; }
        // v0.6.0 — cooldown ticks between attacks (Phase 7 combat hooks
        // here when it lands). Decremented per sim tick.
        public int AttackCooldownTicks { get; set; }
        // v0.6.0 — Phase 8 husbandry hook: tamed entity follows a
        // designated shroomp. Untamed entities ignore this field.
        public bool   IsTamed     { get; set; }
        public string? TamedByName { get; set; }
        // v0.6.0 — wander state. The entity picks a target tile within
        // a small radius of its current pos and walks to it; on arrival
        // or stuck, picks a new one. Decoupled from the shroomp pathfind
        // pipeline since entities don't navigate built structures.
        public Vector2 WanderHome { get; set; }   // anchor — set at spawn, used for ambient wander radius
        public int     WanderHopsRemaining { get; set; }

        // v0.6.2 — simplified needs system. Wild entities track Nutrition +
        // Rest only (no Magic / Social / Joy — they can't Attune or Converse).
        // Decay is purely display today: EntityCardPanel reads these so the
        // player can see fauna state. No behaviour impact yet — Phase 8
        // husbandry adds Hungry → Graze transitions and Tired → Sleep.
        // Range 0-100. Spawn at 70 (default fed + rested). Decay rates
        // tuned slow so a wild entity doesn't visibly starve in the first
        // in-game day; full Nutrition→0 in ~4 in-game days at 0.6/in-game-hr.
        public float Nutrition { get; set; } = 70f;
        public float Rest      { get; set; } = 70f;
        // v0.6.2 — derived "mood" classification. Not stored; computed
        // on demand from Health %, Nutrition, Rest, and AI state so the
        // EntityCardPanel can surface a single human-readable label.
        // Phase 8 husbandry will replace this with a real Tamed-creature
        // mood model alongside the trainability system.
        public string MoodLabel
        {
            get
            {
                if (!IsAlive) return "Dead";
                float healthFrac = MaxHealth > 0f ? Health / MaxHealth : 1f;
                if (State == EntityState.Flee)  return "Fleeing";
                if (State == EntityState.Hunt)  return "Aggressive";
                if (healthFrac < 0.30f)         return "Badly Wounded";
                if (healthFrac < 0.60f)         return "Wounded";
                if (Nutrition < 25f)            return "Starving";
                if (Nutrition < 50f)            return "Hungry";
                if (Rest      < 30f)            return "Exhausted";
                if (IsTamed)                    return "Content (Tamed)";
                return "Calm";
            }
        }

        public Entity()
        {
            RandomSeed = (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
        }

        // Per-individual jittered spawn. EntitySpawnSystem calls this when
        // instantiating from an AnimalSpawnPoint marker or via ambient
        // ambient respawn. The jitter is fixed-seeded against (kind, rng)
        // so the same call site produces stable values for tests.
        public static Entity SpawnAt(EntityKind kind, Vector2 pos, Random rng)
        {
            var def = EntityRegistry.Get(kind);
            float JitterMul() => 0.90f + (float)rng.NextDouble() * 0.20f;   // [0.9, 1.1)
            var e = new Entity
            {
                Kind         = kind,
                MaxHealth    = def.MaxHealth   * JitterMul(),
                Speed        = def.BaseSpeedPxPerSec * JitterMul(),
                AttackPower  = def.AttackPower * JitterMul(),
                SimPos       = pos,
                SimTarget    = pos,
                WanderHome   = pos,
                RandomSeed   = rng.Next(),
            };
            e.Health = e.MaxHealth;
            return e;
        }

        public bool IsAlive => Health > 0f && State != EntityState.Dead;
    }
}
