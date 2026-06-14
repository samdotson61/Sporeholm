using System;
using Godot;
using Sporeholm.World;

namespace Sporeholm.Simulation.Combat
{
    // v0.7.0 (Phase 7 — Combat System). Shared enums, structs, and tuning
    // constants for the body-part-driven combat engine. Combat treats both
    // Shroomps and wild Entities as ICombatant instances and routes every hit
    // through CombatSystem.ResolveExchange (the single shared damage helper).
    //
    // Design contract (mirrors SkillCurve.cs): every combat magic-number lives
    // in CombatTuning here; the engine and curves reference these so balancing
    // touches one place.

    // How a weapon deals damage. Drives the wound-type table (§7.7) and the
    // narrative verb. Unarmed = fists/bite when no weapon (or weapon broken).
    public enum WeaponType : byte
    {
        Unarmed = 0,
        Edged,      // sword / axe / knife — slashes, cuts, can sever
        Blunt,      // club / hammer — bruises, fractures
        Piercing,   // spear / fangs — punctures, deep wounds
        Ranged,     // sling / bow — piercing-at-distance
        Magical,    // staff / wisp — bypasses mundane armor
    }

    // Wound descriptor for the narrative log + feedback. v0.7.0 does NOT keep a
    // persistent Hediff object list — damage reduces the targeted body-part's
    // condition and the existing bleed / down / moving / vital-death machinery
    // does the rest. Wound type classifies the hit for flavour, and Sever zeroes
    // the part outright (full Hediff objects are a v0.7.1 follow-up).
    public enum CombatWound : byte
    {
        None = 0,
        Bruise,
        Cut,
        Fracture,
        Puncture,
        Mangle,
        Sever,
        Concussion,
    }

    // Which side a combatant fights for. v0.7.0 wires Colony (shroomps) vs
    // Wildlife (entities) only; raids / hostile shroomp factions are Phase 9.
    public enum CombatTeam : byte
    {
        Colony = 0,
        Wildlife = 1,
    }

    // Outcome of a single attack exchange. Ordered roughly worst→best for the
    // attacker; the UI picks floater colour off this.
    public enum CombatOutcome : byte
    {
        Miss = 0,
        Dodge,
        Block,
        Hit,
        Crit,
        Kill,
    }

    // The resolved offensive profile a combatant presents each swing — sourced
    // from the equipped weapon (ItemRegistry def) for shroomps, or the natural
    // weapon table for entities. RangeTiles is converted to pixels by the engine
    // using LocalMap.TileSize.
    public readonly struct CombatProfile
    {
        public readonly float Damage;        // base damage before skill/quality/material
        public readonly float Accuracy;      // 0..1 touch-range hit chance
        public readonly WeaponType Type;
        public readonly float RangeTiles;    // effective reach in tiles
        public readonly int CooldownTicks;   // ticks between swings
        public readonly string WeaponName;   // for the narrative log
        public readonly bool Venomous;        // v0.7.1 — applies a venom load on hit (snake / wasp)

        public CombatProfile(float damage, float accuracy, WeaponType type,
            float rangeTiles, int cooldownTicks, string weaponName, bool venomous = false)
        {
            Damage = damage;
            Accuracy = accuracy;
            Type = type;
            RangeTiles = rangeTiles;
            CooldownTicks = cooldownTicks;
            WeaponName = weaponName;
            Venomous = venomous;
        }

        // Bare fists / fallback bite — used when no weapon is equipped or it is
        // Broken. Modest damage so unarmed colonists can still defend.
        public static CombatProfile Unarmed =>
            new(CombatTuning.UnarmedDamage, 0.60f, WeaponType.Unarmed,
                CombatTuning.MeleeRangeTiles, CombatTuning.MeleeCooldownTicks, "fists");
    }

    // A discrete combat event handed to the UI (one per resolved exchange that
    // the player should see). Edge-triggered — drained on the main thread and
    // turned into a MessageLog line + CombatFeedbackOverlay floater/decal. NOT
    // derived from the level-triggered snapshot (which would miss/duplicate
    // one-shot hits).
    public readonly struct CombatEventData
    {
        public readonly Vector2 Pos;          // defender world-pixel position
        public readonly string Text;          // narrative line for the message log
        public readonly float Damage;         // net damage dealt (0 on miss/dodge/block)
        public readonly CombatOutcome Outcome;
        public readonly uint BloodRgba;       // defender blood colour for decals
        public readonly bool ShowBlood;       // false for miss/dodge/block

        // v0.7.0 — combatant identity so the renderers can animate the right
        // sprites: the attacker lunges toward the defender on a swing; the
        // defender recoils + flashes when a blow lands. Shroomp sprites are keyed
        // by Name; entity sprites by Guid — so both are carried. *IsEntity tells
        // the UI which colony view owns each sprite.
        public readonly System.Guid AttackerId;
        public readonly bool AttackerIsEntity;
        public readonly string AttackerName;
        public readonly Vector2 AttackerPos;
        public readonly System.Guid DefenderId;
        public readonly bool DefenderIsEntity;
        public readonly string DefenderName;

        public CombatEventData(Vector2 pos, string text, float damage,
            CombatOutcome outcome, uint bloodRgba, bool showBlood,
            System.Guid attackerId, bool attackerIsEntity, string attackerName, Vector2 attackerPos,
            System.Guid defenderId, bool defenderIsEntity, string defenderName)
        {
            Pos = pos;
            Text = text;
            Damage = damage;
            Outcome = outcome;
            BloodRgba = bloodRgba;
            ShowBlood = showBlood;
            AttackerId = attackerId;
            AttackerIsEntity = attackerIsEntity;
            AttackerName = attackerName;
            AttackerPos = attackerPos;
            DefenderId = defenderId;
            DefenderIsEntity = defenderIsEntity;
            DefenderName = defenderName;
        }
    }

    // Per-tick combat context, set once at the top of SimulationCore.Tick via
    // CombatSystem.BeginTick. Carries the shared sim RNG, the map (for range /
    // line-of-sight), the global tick, an event sink for UI feedback, and the
    // canonical shroomp-kill delegate (KillShroomp lives on SimulationCore and
    // must run on the sim thread). All combat resolution happens on the sim
    // thread, so a single static context is safe (mirrors BehaviorSystem's
    // sim-thread-only static scratch state).
    public readonly struct CombatContext
    {
        public readonly Random Rng;
        public readonly LocalMap? Map;
        public readonly long GlobalTick;
        public readonly Action<CombatEventData>? Emit;
        public readonly Action<Shroomp, CauseOfDeath>? KillShroomp;

        public CombatContext(Random rng, LocalMap? map, long globalTick,
            Action<CombatEventData>? emit, Action<Shroomp, CauseOfDeath>? killShroomp)
        {
            Rng = rng;
            Map = map;
            GlobalTick = globalTick;
            Emit = emit;
            KillShroomp = killShroomp;
        }
    }

    // All combat balance constants in one place (per the SkillCurve design
    // contract). Tuned against the existing weapon BaseDamage values (Spear 12,
    // Sword 14, Club 8) and entity AttackPower (Wolf 12, Snake 8) so combat
    // feels consistent with the v0.6 contact-damage baseline but now resolves
    // through real rolls, body parts, and bleeding.
    public static class CombatTuning
    {
        // Reach (tiles).
        public const float MeleeRangeTiles = 1.5f;
        public const float RangedRangeTiles = 6.0f;
        public const float MagicalRangeTiles = 7.0f;

        // Swing cadence (ticks @ 60Hz sim → 60 = 1 in-game second).
        public const int MeleeCooldownTicks = 60;
        public const int RangedCooldownTicks = 96;

        // Unarmed fallback.
        public const float UnarmedDamage = 4f;

        // Crit: doubles raw damage and bumps the wound table one severity tier.
        public const float CritDamageMul = 2.0f;
        public const float BaseCritChance = 0.03f;        // before skill/quality
        public const float CritChancePerSkill = 0.004f;   // +0.4%/level (→ +8% at 20)

        // Block (shield) and dodge ceilings so combat never becomes unhittable.
        public const float MaxBlockChance = 0.60f;
        public const float MaxDodgeChance = 0.50f;

        // Engage radius (tiles) for a colonist to auto-defend against a hostile
        // entity that wanders close. Guardians use the larger ring.
        public const float SelfDefenseEngageTiles = 2.2f;
        public const float GuardianEngageTiles = 7.0f;

        // Health fraction below which a wounded colonist breaks off to flee
        // rather than press a fight. Kept well above the ~30% down threshold so
        // there's a real window to run before collapsing.
        public const float FleeHealthFraction = 0.50f;

        // Max ticks a colonist chases an ORDERED target without landing a hit
        // before giving up — stops a colonist chasing a faster fleeing creature
        // across the whole map forever (soft-lock). ~10s @ 60Hz.
        public const int MaxPursuitTicks = 600;

        // Material damage multiplier is derived from MaterialDef.DurabilityMul
        // clamped to this band (no dedicated DamageMul field exists; Hematite's
        // 1.40 durability → the "iron-tier" 1.4× damage the spec wants, Wood
        // ~0.95, Cloth ~0.65→floored to 0.80).
        public const float MinMaterialDamageMul = 0.80f;
        public const float MaxMaterialDamageMul = 1.40f;

        // v0.7.1 — venom load added to a shroomp per venomous hit (snake / wasp).
        public const float VenomDosePerHit = 12f;
    }
}
