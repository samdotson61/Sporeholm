using System;
using System.Collections.Generic;
using SmurfulationC.Simulation;

namespace SmurfulationC.Simulation.Systems
{
    // Recalculates each smurf's mood score from need satisfaction each tick.
    // Mood score drives behavioral state; see Smurf.MoodState for thresholds.
    public static class MoodSystem
    {
        // v0.3.36 (B.14) — change threshold below which the mood recompute is
        // skipped. NeedsContribution is a fixed linear combination of the
        // five needs (weights 0.25 / 0.20 / 0.20 / 0.20 / 0.15); the largest
        // weight × the smallest meaningful change in a need (0.2) gives a
        // mood-score delta of 0.05. Use 0.1 to be conservative — if every
        // need has moved less than that the resulting mood score change is
        // imperceptible (mood is a 0–100 integer-ish display value) and the
        // skip is correct.
        private const float MoodEpsilon = 0.1f;

        public static void Tick(IReadOnlyList<Smurf> smurfs)
        {
            foreach (var s in smurfs)
            {
                if (!s.IsAlive) continue;

                // Fast-path skip: if no need changed enough since last
                // recompute, the mood score is essentially unchanged. Keep
                // the previous MoodScore / MoodRaw / MoodModifier values.
                if (!NeedsChangedEnough(s))
                    continue;

                float raw = NeedsContribution(s);
                float mod = PersonalityModifier(s);

                s.MoodRaw      = raw;
                // v0.3.43 — fold thought mood-contribution into the
                // personality modifier so existing MoodRaw / MoodModifier
                // semantics stay consistent. ThoughtSystem.Tick keeps
                // s.MoodFromThoughts clamped to ±50 so it can never
                // dominate the needs blend.
                s.MoodModifier = mod + s.MoodFromThoughts;
                s.MoodScore    = Math.Clamp(raw + s.MoodModifier, 0f, 100f);

                // Update the cache snapshot so the next tick can short-circuit.
                s.MoodCacheNutrition = s.Nutrition;
                s.MoodCacheRest      = s.Rest;
                s.MoodCacheSocial    = s.Social;
                s.MoodCacheMagic     = s.MagicResonance;
                s.MoodCacheSafety    = s.Safety;
                s.MoodCacheJoy       = s.Joy;     // v0.4.63 (G4)
            }
        }

        private static bool NeedsChangedEnough(Smurf s)
        {
            // NaN cache means "never computed" — force the first pass.
            if (float.IsNaN(s.MoodCacheNutrition)) return true;
            if (Math.Abs(s.Nutrition      - s.MoodCacheNutrition) > MoodEpsilon) return true;
            if (Math.Abs(s.Rest           - s.MoodCacheRest)      > MoodEpsilon) return true;
            if (Math.Abs(s.Social         - s.MoodCacheSocial)    > MoodEpsilon) return true;
            if (Math.Abs(s.MagicResonance - s.MoodCacheMagic)     > MoodEpsilon) return true;
            if (Math.Abs(s.Safety         - s.MoodCacheSafety)    > MoodEpsilon) return true;
            if (Math.Abs(s.Joy            - s.MoodCacheJoy)       > MoodEpsilon) return true;   // v0.4.63
            return false;
        }

        // Weighted average of the six needs (sums to 1.0).
        // v0.4.63 (G4) — Joy added at 0.10 weight (carved from prior weights:
        // Nutrition 0.25→0.22, Rest 0.20→0.18, Social 0.20→0.18, Magic 0.20→0.17,
        // Safety 0.15→0.15). Mood now reflects whether the colony works
        // without joy as well as whether they're physically fed/rested.
        public static float NeedsContribution(Smurf s) =>
            Math.Clamp(
                s.Nutrition      * 0.22f +
                s.Rest           * 0.18f +
                s.Social         * 0.18f +
                s.MagicResonance * 0.17f +
                s.Safety         * 0.15f +
                s.Joy            * 0.10f,
                0f, 100f);

        // Flat mood offset from personality traits.
        public static float PersonalityModifier(Smurf s)
        {
            float total = 0f;
            foreach (var name in s.Personality)
            {
                var def = PersonalityRegistry.Get(name);
                if (def != null) total += def.MoodModifier;
            }
            return total;
        }
    }
}
