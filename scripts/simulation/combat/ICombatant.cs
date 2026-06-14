using System;
using Godot;

namespace Sporeholm.Simulation.Combat
{
    // v0.7.0 (Phase 7) — the single combat abstraction implemented by BOTH
    // Shroomp (rich body-part model) and Entity (single-HP wildlife). The shared
    // CombatSystem.ResolveExchange driver operates only against this interface,
    // so one code path handles shroomp-vs-entity, entity-vs-shroomp, and (later)
    // shroomp-vs-shroomp raids without branching on concrete types.
    //
    // ALL members are read/written on the SIM THREAD only.
    public interface ICombatant
    {
        // ── identity ──────────────────────────────────────────────────────
        Guid CombatId { get; }
        string CombatName { get; }      // display name for the narrative log
        CombatTeam Team { get; }
        Vector2 Pos { get; }            // world-pixel position
        bool IsAlive { get; }

        // Can this combatant still initiate / continue attacking? False when
        // dead or downed (shroomps) — a downed combatant is out of the fight.
        bool CanAct { get; }

        // 0..1 weighted health, drives flee thresholds + target prioritisation.
        float HealthFraction { get; }

        // 0..100 pain. High pain spoils attack rolls and can knock a colonist
        // unconscious. Entities don't model pain (return 0).
        float Pain { get; }

        // ── offense ───────────────────────────────────────────────────────
        // The weapon/natural-attack profile for this swing (damage, accuracy,
        // type, reach, cadence).
        CombatProfile GetAttackProfile();

        // Skill level (0..20) feeding the attack roll + damage factor.
        // ranged=true selects the ranged skill, false the melee skill.
        int AttackSkill(bool ranged);

        // ── defense ───────────────────────────────────────────────────────
        float DodgeChance();            // 0..1 evasion (capped by tuning)
        float BlockChance();            // 0..1 shield block (capped by tuning)

        // Pick which body part this incoming hit lands on. Real body-part key
        // for shroomps (used for armor + wound routing); flavour part name for
        // entities (narrative only — entity damage is a single HP pool).
        string PickHitPart(Random rng);

        // Damage reduction fraction (0..1) from armor covering `part`. 0 = no
        // mitigation. Entities return their natural-hide value; shroomps read
        // worn apparel over the slot mapping to `part`.
        float ArmorFractionAt(string part);

        // ── apply ─────────────────────────────────────────────────────────
        // Apply already-resolved net damage to `part`. Returns true if this hit
        // was immediately LETHAL (a vital part reached 0 for shroomps, or HP hit
        // 0 for entities). The engine then runs the kill/death handling.
        bool ApplyDamage(string part, float amount, CombatWound wound, long globalTick);

        // Blood-spatter colour (packed 0xRRGGBBAA) for the feedback overlay.
        uint BloodRgba { get; }

        // Shared per-combatant swing cooldown (decremented each tick by the
        // owning system). Maps to the existing Entity.AttackCooldownTicks and a
        // new Shroomp.AttackCooldownTicks.
        int AttackCooldownTicks { get; set; }
    }
}
