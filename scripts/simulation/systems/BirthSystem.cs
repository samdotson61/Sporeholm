using System;
using System.Collections.Generic;
using System.Linq;

namespace SmurfulationC.Simulation.Systems
{
    // Handles yearly birth checks and newborn Smurf creation.
    // Called from SimulationCore on year boundaries.
    //
    // Birth rules (per GDD §1.1, refined in Phase 4 sub-B / v0.3.47):
    //   - Each eligible mother rolls *independently* per season. The legacy
    //     v0.3.12 colony-wide single roll capped birth rate at ≈ 1/year
    //     even with multiple mothers; the refinement here scales linearly
    //     with the female count so a 3-mother colony averages 3 births/yr.
    //   - Food capacity is now computed from the colony stockpile total
    //     (Phase 4 sub-A inventory) rather than the legacy `Foragers × 5`
    //     placeholder. A colony with ≥ 30 days of food per smurf passes.
    //   - 1:49 female birth ratio preserved (per Smurfs canon).
    //   - Traits inherited from parents with ±0.1 penetrance variance.
    public static class BirthSystem
    {
        // Per-mother per-season birth probability when food capacity allows.
        // Calibrated so a 1-female colony averages ~1 birth/year (4 seasons
        // × 0.25 = 1.0 expected) and a 3-female colony averages ~3/year.
        private const double BirthChance = 0.25;

        // Food capacity formula — Phase 4 sub-A inventory aware. A colony
        // with food items totalling ≥ 30 per living smurf is considered
        // fed enough to support births; below that, the gate suspends
        // births until reserves recover. The legacy `Foragers × 5`
        // placeholder is preserved as a floor so very early colonies (no
        // stockpile yet) still see the first generation arrive.
        public static int ComputeFoodCapacity(IReadOnlyList<Smurf> living)
        {
            int foragers = living.Count(s => s.IsAlive && s.Role == "Forager");
            return Math.Max(30, 30 + foragers * 5);
        }

        // v0.3.47 — food-stockpile-aware capacity check. Returns true when
        // the colony has ≥ 30 units of food per living smurf in the
        // inventory; nascent colonies with zero stockpile fall back to the
        // legacy capacity floor so the very first birth isn't blocked
        // pending a food economy that doesn't exist yet.
        public static bool PassesFoodGate(IReadOnlyList<Smurf> living, int foodTotal)
        {
            int alive = living.Count(s => s.IsAlive);
            if (alive == 0) return true;
            int perSmurfTarget = 30;
            int requiredTotal  = perSmurfTarget * alive;
            // Founding-grace: under 10 smurfs and < 1 month of game time
            // would otherwise block forever. Allow if food >= half target
            // OR colony is below the legacy capacity floor.
            if (foodTotal >= requiredTotal) return true;
            if (alive <= 10 && foodTotal >= requiredTotal / 2) return true;
            return false;
        }

        // Returns a new Smurf if birth conditions are met, null otherwise.
        // All state mutation (adding to _smurfs list) is done by the caller.
        // v0.3.47 — per-mother independent roll (each eligible mother gets
        // her own chance this season); previously a single colony-wide
        // roll capped birth rate regardless of mother count.
        public static Smurf? TryBirth(IReadOnlyList<Smurf> living, Random rng, int foodTotal = -1)
        {
            int capacity = ComputeFoodCapacity(living);
            if (living.Count >= capacity) return null;

            // v0.3.47 — food-stockpile gate. -1 sentinel means "caller
            // didn't supply, fall back to legacy capacity check".
            if (foodTotal >= 0 && !PassesFoodGate(living, foodTotal)) return null;

            // Eligible parents: Adult or Elder (Sprout/Juvenile too young).
            var mothers = living.Where(s => s.IsAlive && s.Sex == Sex.Female &&
                                            s.LifeStage is LifeStage.Adult or LifeStage.Elder).ToList();
            var fathers = living.Where(s => s.IsAlive && s.Sex == Sex.Male &&
                                            s.LifeStage is LifeStage.Adult or LifeStage.Elder).ToList();

            if (mothers.Count == 0 || fathers.Count == 0) return null;

            // v0.3.47 — per-mother roll: roll once per mother. First success
            // wins; the rest of the mothers wait for the next season. This
            // converges to "births per season ≈ mothers × BirthChance" in
            // expectation while still producing only ≤ 1 birth per season
            // (matches the rest of the codebase's once-per-season cadence).
            Smurf? motherPick = null;
            foreach (var m in mothers)
            {
                if (rng.NextDouble() < BirthChance) { motherPick = m; break; }
            }
            if (motherPick == null) return null;

            var mother = motherPick;
            var father = fathers[rng.Next(fathers.Count)];

            // 1:49 female ratio: 1 in 49 births is female.
            var sex = rng.Next(49) == 0 ? Sex.Female : Sex.Male;

            var existingNames = living.Select(s => s.Name);
            string name = SmurfNameGenerator.Generate(existingNames, rng, sex);

            var child = new Smurf
            {
                Name              = name,
                AgeInYears        = 0,
                Sex               = sex,
                Role              = "Unassigned",
                BirthdayDayOfYear = rng.Next(0, 120),
                Nutrition         = 90f,
                Rest              = 90f,
                Social            = 90f,
                MagicResonance    = 85f,
                Safety            = 90f,
            };

            InheritTraits(child, mother, father, rng);
            child.Personality = PersonalityRegistry.Assign(rng, child.AgeInYears);
            child.BodyParts   = BodyPartRegistry.CreateHealthy();
            // v0.3.43 — newborns roll a fresh preference set (DF-style
            // likes/dislikes). Personality-aware so a child rolled with
            // Introvert tends to dislike Socializing.
            child.Preferences = PreferenceRegistry.Assign(rng, child.Personality);
            SkillRegistry.Distribute(child, rng);
            // v0.3.47 — newborns inherit role-based default Jobs grid prios.
            WorkPriorityDefaults.ApplyRoleDefaults(child);

            // v0.4.4 — handedness rolled per-smurf. Parents do not directly
            // determine the child's handedness; population-level rate
            // matches Roll's ~10 % left-handed split.
            child.Handedness = HandednessMeta.Roll(rng);

            // v0.3.47 — spawn near the mother instead of at (0,0).
            float ox = (float)((rng.NextDouble() - 0.5) * 32);
            float oy = (float)((rng.NextDouble() - 0.5) * 32);
            child.SimPos    = mother.SimPos + new Godot.Vector2(ox, oy);
            child.SimTarget = child.SimPos;
            child.SimSpeed  = mother.SimSpeed * 0.7f;   // sprouts move slower

            return child;
        }

        // Each biological trait is averaged from both parents with ±0.1 variance.
        private static void InheritTraits(Smurf child, Smurf mother, Smurf father, Random rng)
        {
            foreach (var def in TraitRegistry.All)
            {
                float mv = mother.Traits.TryGetValue(def.Name, out var m) ? m : 0.5f;
                float fv = father.Traits.TryGetValue(def.Name, out var f) ? f : 0.5f;
                float avg = (mv + fv) * 0.5f;
                float variance = (float)(rng.NextDouble() * 0.2 - 0.1); // ±0.1
                child.Traits[def.Name] = Math.Clamp(avg + variance, 0f, 1f);
            }
        }
    }
}
