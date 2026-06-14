using System;
using Godot;
using Sporeholm.World;
using Sporeholm.Simulation.Entities;

namespace Sporeholm.Simulation.Combat
{
    // v0.7.0 (Phase 7 §7.1, §7.8a) — the single shared damage helper. One code
    // path resolves every attack exchange, whether shroomp→entity,
    // entity→shroomp, or (future) shroomp→shroomp. Static, sim-thread-only.
    //
    // A per-tick CombatContext (RNG + map + event sink + kill delegate) is set
    // once at the top of SimulationCore.Tick via BeginTick — this mirrors the
    // sim-thread-only static scratch state BehaviorSystem already uses, so we
    // avoid threading a context object through every call site.
    //
    // The 9-stage exchange (§7.1): range → hit roll → block → crit → hit-part →
    // armor → damage → wound → apply → death → narrative event.
    public static class CombatSystem
    {
        private static CombatContext _ctx;
        private static readonly Random _fallbackRng = new();

        public static void BeginTick(in CombatContext ctx) => _ctx = ctx;

        private static Random Rng => _ctx.Rng ?? _fallbackRng;

        // Attempt one attack from `attacker` against `defender`. Returns true if
        // an exchange was resolved (in range + off cooldown). Callers invoke this
        // every tick while pursuing; it self-gates on range and cooldown, so a
        // pursuer that hasn't closed simply keeps moving.
        public static bool TryAttack(ICombatant attacker, ICombatant defender)
        {
            if (attacker == null || defender == null) return false;
            if (!attacker.IsAlive || !attacker.CanAct) return false;
            if (!defender.IsAlive) return false;
            if (attacker.AttackCooldownTicks > 0) return false;

            CombatProfile profile = attacker.GetAttackProfile();
            float rangePx = profile.RangeTiles * LocalMap.TileSize;
            if (attacker.Pos.DistanceSquaredTo(defender.Pos) > rangePx * rangePx)
                return false;   // not yet in reach — keep closing

            ResolveExchange(attacker, defender, profile);
            attacker.AttackCooldownTicks = profile.CooldownTicks;
            return true;
        }

        private static void ResolveExchange(ICombatant attacker, ICombatant defender,
            in CombatProfile profile)
        {
            Random rng = Rng;
            bool ranged = CombatProfiles.IsRanged(profile.Type);
            int atkSkill = attacker.AttackSkill(ranged);

            // 1. hit roll (weapon accuracy × skill − defender dodge)
            float dodge = Mathf.Clamp(defender.DodgeChance(), 0f, CombatTuning.MaxDodgeChance);
            float hitChance = SkillCurve.HitChance(atkSkill, profile.Accuracy, dodge, ranged);
            if (attacker.Pain > 60f) hitChance *= 0.7f;   // v0.7.1 — pain spoils aim
            if (rng.NextDouble() > hitChance)
            {
                // distinguish a dodge from a plain miss for flavour
                CombatOutcome o = (dodge > 0f && rng.NextDouble() < 0.5)
                    ? CombatOutcome.Dodge : CombatOutcome.Miss;
                Emit(attacker, defender, profile, o, 0f, "", CombatWound.None);
                return;
            }

            // 2. shield block
            float block = Mathf.Clamp(defender.BlockChance(), 0f, CombatTuning.MaxBlockChance);
            if (block > 0f && rng.NextDouble() < block)
            {
                Emit(attacker, defender, profile, CombatOutcome.Block, 0f, "", CombatWound.None);
                return;
            }

            // 3. crit
            bool crit = rng.NextDouble() < CritChance(atkSkill);

            // 4. hit-part + 5. armor (magic bypasses mundane armor)
            string part = defender.PickHitPart(rng);
            float armor = profile.Type == WeaponType.Magical ? 0f
                : Mathf.Clamp(defender.ArmorFractionAt(part) * ArmorEffectivenessVs(profile.Type), 0f, 0.90f);

            // 6. damage = profile (already material/quality/condition-scaled by the
            //    attacker) × skill factor × crit
            float skillMul = SkillCurve.MeleeDamageFactor(atkSkill);
            float raw = profile.Damage * skillMul;
            if (crit) raw *= CombatTuning.CritDamageMul;
            float net = Mathf.Max(0f, raw * (1f - armor));

            // 7. wound classification
            CombatWound wound = ClassifyWound(profile.Type, net, crit, part);

            // 8. apply + death
            bool fatal = defender.ApplyDamage(part, net, wound, _ctx.GlobalTick);
            if (profile.Venomous && net > 0f && defender is Shroomp venomTarget)
                venomTarget.Venom = Mathf.Min(100f, venomTarget.Venom + CombatTuning.VenomDosePerHit);
            CombatOutcome outcome = fatal ? CombatOutcome.Kill
                : (crit ? CombatOutcome.Crit : CombatOutcome.Hit);
            if (fatal) HandleDeath(defender);

            // 9. narrative + feedback event
            Emit(attacker, defender, profile, outcome, net, part, wound);
        }

        // Weapon-type vs armor (§7.3): blunt transmits force through plate so
        // armor stops less of it; edged is well-stopped; piercing / ranged find
        // some gaps. Magical bypasses entirely (handled at the call site).
        private static float ArmorEffectivenessVs(WeaponType t) => t switch
        {
            WeaponType.Blunt    => 0.60f,
            WeaponType.Piercing => 0.85f,
            WeaponType.Ranged   => 0.90f,
            WeaponType.Edged    => 1.00f,
            _                   => 1.00f,
        };

        private static float CritChance(int skill) =>
            Mathf.Clamp(CombatTuning.BaseCritChance + CombatTuning.CritChancePerSkill * skill,
                0f, 0.35f);

        // §7.7 wound table — weapon type × net damage (parts are 0-100). Crit
        // bumps severity. Head/Cap blunt or crit hits concuss.
        private static CombatWound ClassifyWound(WeaponType type, float net, bool crit, string part)
        {
            if (net <= 0f) return CombatWound.None;

            // Head trauma → concussion for blunt always, and for non-edged crits.
            // Edged crits fall through to the Sever branch below (a decapitating
            // slash is a sever, not a concussion).
            bool head = part == "Cap" || part == "Brain" || part == "head";
            if (head && net >= 10f && (type == WeaponType.Blunt || (crit && type != WeaponType.Edged)))
                return CombatWound.Concussion;

            switch (type)
            {
                case WeaponType.Edged:
                    if (crit && net >= 22f) return CombatWound.Sever;
                    if (net >= 28f)         return CombatWound.Mangle;
                    return CombatWound.Cut;
                case WeaponType.Blunt:
                    if (net >= 20f) return CombatWound.Fracture;
                    return CombatWound.Bruise;
                case WeaponType.Piercing:
                case WeaponType.Ranged:
                    return CombatWound.Puncture;
                case WeaponType.Magical:
                    return CombatWound.Cut;   // energy burn — open wound
                default:
                    return CombatWound.Bruise;
            }
        }

        private static void HandleDeath(ICombatant defender)
        {
            if (defender is Shroomp s)
            {
                _ctx.KillShroomp?.Invoke(s, CauseOfDeath.Combat);
            }
            else if (defender is Entity e)
            {
                e.Health = 0f;
                e.State = EntityState.Dead;   // pruned + PendingEntityRemovals next tick
            }
        }

        private static void Emit(ICombatant attacker, ICombatant defender,
            in CombatProfile profile, CombatOutcome outcome, float net, string part,
            CombatWound wound)
        {
            if (_ctx.Emit == null) return;
            string text = CombatNarrator.Describe(attacker.CombatName, profile, wound,
                part, defender.CombatName, outcome);
            bool showBlood = outcome == CombatOutcome.Hit
                || outcome == CombatOutcome.Crit
                || outcome == CombatOutcome.Kill;
            uint blood = showBlood ? defender.BloodRgba : 0u;
            _ctx.Emit(new CombatEventData(defender.Pos, text, net, outcome, blood, showBlood,
                attacker.CombatId, attacker.Team == CombatTeam.Wildlife, attacker.CombatName, attacker.Pos,
                defender.CombatId, defender.Team == CombatTeam.Wildlife, defender.CombatName));
        }
    }
}
