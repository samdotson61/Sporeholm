using Godot;
using System;

namespace Sporeholm.Simulation
{
    // v0.5.30 (Phase 5 polish — RimWorld-parity skill curves). Centralised
    // multipliers for every work-speed and yield calculation in the sim.
    // Pulled from RimWorld's published per-stat formulas (rimworldwiki.com
    // Construction_Speed / Mining_Yield / Mining_Speed / Plant_Work_Speed),
    // tuned slightly so a level-0 Sporeholm novice ↔ level-20 master
    // hits the ~10× spread Sam asked for ("low-skill shroomp takes 10s, a
    // master finishes in 1s").
    //
    // Design contract: every callsite that scales work by skill goes through
    // a method here. Future skill-balancing passes touch this file only.
    //
    // Skill levels:
    //   0  — untrained novice
    //   8  — competent (RimWorld's "100 % multiplier" anchor)
    //   20 — RimWorld's effective cap (passion + decay equilibrium)
    public static class SkillCurve
    {
        // ── Construction ──────────────────────────────────────────────────
        // RimWorld: 0.30 + 0.0875 × level. lvl 0 = 30 %, lvl 8 = 100 %,
        // lvl 20 = 205 %. We adopt the same curve verbatim so Sporeholm
        // build speed feels identical to RimWorld's.
        public static float ConstructionSpeedFactor(int skillLevel) =>
            0.30f + 0.0875f * Mathf.Clamp(skillLevel, 0, 20);

        // RimWorld: 0.75 + 0.025 × level (capped at 1.0). Lvl 0 = 75 %,
        // lvl 8 = 95 %, lvl 10 = 100 %. Construct failure wastes some
        // material + resets BuildProgress. We use this for the per-completion
        // "did this frame botch?" roll.
        public static float ConstructSuccessChance(int skillLevel) =>
            Mathf.Clamp(0.75f + 0.025f * skillLevel, 0.50f, 1.0f);

        // Quality bell at completion. Mirrors RimWorld's per-skill quality
        // table (Awful/Poor/Normal/Good/Excellent/Masterwork). We re-use
        // the existing Items.Quality enum (Crude/Normal/Fine/Superior/
        // Masterwork/Legendary) so the renderer + tooltips don't need a new
        // type. Mapping: Awful→Crude, Poor→(missing — we collapse to Crude
        // at low skill), Normal→Normal, Good→Fine, Excellent→Superior,
        // Masterwork→Masterwork. Legendary intentionally unrolled (RimWorld
        // requires Inspired Creativity which we haven't shipped).
        public static Items.Quality RollStructureQuality(int skillLevel, Random rng)
        {
            // Bell centre slides up with skill.
            // lvl 0  → centre  ≈ Crude / Normal split
            // lvl 4  → centre  ≈ Normal
            // lvl 8  → centre  ≈ Normal+ (Fine likely)
            // lvl 14 → centre  ≈ Fine / Superior
            // lvl 20 → centre  ≈ Superior, Masterwork tail
            float roll = (float)rng.NextDouble();           // [0,1)
            float jitter = (float)rng.NextDouble() - 0.5f;  // [-0.5, 0.5)
            // Score is a continuous "quality value" the buckets read off.
            // Skill contributes linearly; jitter widens the bell.
            float score = (skillLevel * 0.18f) + (roll * 1.4f) + (jitter * 0.6f);
            if (score < 0.40f) return Items.Quality.Crude;
            if (score < 1.30f) return Items.Quality.Normal;
            if (score < 2.20f) return Items.Quality.Fine;
            if (score < 3.10f) return Items.Quality.Superior;
            return Items.Quality.Masterwork;
        }

        // ── Mining ────────────────────────────────────────────────────────
        // RimWorld: speed = 0.04 + 0.12 × level. Lvl 0 = 4 %, lvl 8 = 100 %.
        // Sporeholm excavate is one-shot today (no per-tick mining
        // progress), so this multiplier is reserved for a future v0.6+
        // "ticks-to-mine" rewrite. Kept here so the API exists.
        public static float MiningSpeedFactor(int skillLevel) =>
            Mathf.Max(0.04f, 0.04f + 0.12f * Mathf.Clamp(skillLevel, 0, 20));

        // RimWorld: yield = 0.60 + 0.025 × level, capped at 1.25 (lvl 26).
        // Lvl 0 = 60 %, lvl 8 = 80 %, lvl 16 = 100 %, lvl 20 = 110 %.
        // A level-0 novice extracts 60 % of what a level-16 expert does
        // from the same boulder — never zero, just less.
        public static float MiningYieldFactor(int skillLevel) =>
            Mathf.Clamp(0.60f + 0.025f * skillLevel, 0.50f, 1.25f);

        // ── Plant work (Foraging / ChopWood) ──────────────────────────────
        // RimWorld plant work speed = 0.08 + 0.115 × level. Lvl 0 = 8 %,
        // lvl 8 = 100 %, lvl 20 = 238 %. Sporeholm forage is one-shot
        // today; speed factor reserved for future per-tick harvesting.
        public static float PlantSpeedFactor(int skillLevel) =>
            Mathf.Max(0.08f, 0.08f + 0.115f * Mathf.Clamp(skillLevel, 0, 20));

        // RimWorld plant harvest yield: low-skill plant harvest can fail
        // (per-plant roll — pawn destroys a stalk). We approximate that with
        // a yield-fraction curve that mimics the "effective extraction"
        // rate. Lvl 0 = 50 %, lvl 4 = 70 %, lvl 8 = 100 %, lvl 20 = 130 %.
        public static float PlantYieldFactor(int skillLevel) =>
            Mathf.Clamp(0.50f + 0.0625f * skillLevel, 0.40f, 1.30f);

        // RimWorld: per-plant chance to RUIN the stalk on harvest at low
        // skill (yields nothing). Lvl 0 ≈ 25 % ruin, lvl 4 ≈ 12 %, lvl 8 = 0 %.
        // Our gather is per-tile (single-shot), so we apply this once: if
        // the roll fails AND the shroomp is unskilled, yield drops to ~50 %
        // of what the table multiplier suggests. Non-failure is the norm.
        public static bool HarvestBotch(int skillLevel, Random rng)
        {
            float ruinChance = Mathf.Max(0f, 0.25f - 0.03f * skillLevel);
            return rng.NextDouble() < ruinChance;
        }

        // ── Cooking / Crafting ────────────────────────────────────────────
        // RimWorld cooking speed bonus factor = 0.06 per level over base.
        // Approximation: 0.40 base + 0.075 per level. Lvl 0 = 40 %,
        // lvl 8 = 100 %, lvl 20 = 190 %.
        public static float CookingSpeedFactor(int skillLevel) =>
            Mathf.Max(0.10f, 0.40f + 0.075f * Mathf.Clamp(skillLevel, 0, 20));

        // ── Generic helper ────────────────────────────────────────────────
        // Apply yield multiplier to a base integer yield, with a randomised
        // floor/ceil split so a 4 × 0.85 = 3.4 yield rolls 3 or 4 (not
        // always rounded to 3). Keeps low-skill yields perceptibly worse
        // without hard-locking them to specific integers.
        public static int ApplyYieldMul(int baseYield, float mul, Random rng)
        {
            float scaled = baseYield * mul;
            int floor = (int)scaled;
            float frac = scaled - floor;
            if (rng.NextDouble() < frac) floor++;
            return Mathf.Max(0, floor);
        }

        // ── Tool quality bonus ───────────────────────────────────────────
        // v0.5.84t — equipment-tier multiplier on top of the base tool bonus.
        // A pawn holding a tool whose PreferredForTasks includes the current
        // task type gets a 1.30× base multiplier (BehaviorSystem.ToolBaseBonus);
        // this curve adds an extra tier scaling so Masterwork picks really
        // sing vs Crude ones. Numbers chosen so Crude is a 10 % drag (made
        // hastily, slows you down a touch), Normal is neutral, and Legendary
        // (the rare inspired-creativity tier) doubles up to +50 % on top.
        // Applied multiplicatively: total tool factor = ToolBaseBonus × ToolQualityFactor.
        public static float ToolQualityFactor(Items.Quality q) => q switch
        {
            Items.Quality.Crude      => 0.90f,
            Items.Quality.Normal     => 1.00f,
            Items.Quality.Fine       => 1.10f,
            Items.Quality.Superior   => 1.20f,
            Items.Quality.Masterwork => 1.35f,
            Items.Quality.Legendary  => 1.50f,
            _                        => 1.00f,
        };
    }
}
