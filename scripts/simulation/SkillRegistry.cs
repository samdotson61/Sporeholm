using System;
using System.Collections.Generic;
using System.Linq;

namespace SmurfulationC.Simulation
{
    public sealed record SkillDef(string Name, string Domain, string Description);

    public static class SkillRegistry
    {
        public static readonly IReadOnlyList<SkillDef> All = new[]
        {
            // Survival
            new SkillDef("Foraging",     "Survival",  "Locating and harvesting wild food and materials."),
            new SkillDef("Botany",       "Survival",  "Growing crops and tending plants."),
            new SkillDef("Mining",       "Survival",  "Excavating stone, ore, and earth."),
            new SkillDef("Athletics",    "Survival",  "Speed, stamina, and physical endurance."),
            // Combat
            new SkillDef("Melee",        "Combat",    "Close-quarters fighting with weapons or fists."),
            new SkillDef("Ranged",       "Combat",    "Attacking at distance with thrown or launched projectiles."),
            // Crafting
            new SkillDef("Crafting",     "Crafting",  "Making tools, goods, and equipment."),
            new SkillDef("Construction", "Crafting",  "Building and repairing structures."),
            // Magic
            new SkillDef("Arcane",       "Magic",     "Channeling and manipulating magic essence."),
            new SkillDef("Ritual",       "Magic",     "Performing Smurf ceremonies and enchantments."),
            // Social
            new SkillDef("Social",       "Social",    "Building community bonds and raising morale."),
            new SkillDef("Empathy",      "Social",    "Reading and soothing the feelings of others."),
            new SkillDef("Leadership",   "Social",    "Inspiring and coordinating the colony."),
            // Knowledge
            new SkillDef("Lore",         "Knowledge", "Understanding of Smurf history and nature."),
            new SkillDef("Research",     "Knowledge", "Systematic investigation and discovery."),
            new SkillDef("Medicine",     "Knowledge", "Treating wounds and illness."),
        };

        // Flat bonuses roles grant to their primary skills during role work.
        // Shown in the Skills tab UI and will feed Phase 3 effective-skill calculations.
        // Primary (+3) · Secondary (+2) · Supporting (+1)
        private static readonly Dictionary<string, Dictionary<string, int>> _roleBonuses = new()
        {
            ["Forager"]   = new() { ["Foraging"] = 3, ["Athletics"] = 2, ["Botany"]        = 1 },
            ["Crafter"]   = new() { ["Crafting"] = 3, ["Construction"] = 2, ["Mining"]     = 1 },
            ["Scholar"]   = new() { ["Research"] = 3, ["Lore"] = 2, ["Botany"]             = 1 },
            ["Mage"]      = new() { ["Arcane"]   = 3, ["Ritual"] = 2, ["Lore"]             = 1 },
            ["Caretaker"] = new() { ["Medicine"] = 3, ["Empathy"] = 2, ["Social"]          = 1 },
            ["Guardian"]  = new() { ["Melee"]    = 3, ["Ranged"] = 2, ["Athletics"]        = 1 },
            ["Elder"]     = new() { ["Leadership"] = 3, ["Lore"] = 2, ["Social"]           = 1 },
        };

        public static int GetRoleBonus(string role, string skill) =>
            _roleBonuses.TryGetValue(role, out var b) && b.TryGetValue(skill, out int v) ? v : 0;

        // ── v0.4.62 (G3) — Skill XP gain ──────────────────────────────────────
        //
        // RimWorld parallel: `SkillRecord.Learn(float xp, ignoreLearningSaturation)`.
        // XP accumulates per relevant work tick / completion event; level
        // increments when `SkillsXp[name] >= LevelThreshold(level)`, with
        // excess carrying over so partial-level progress isn't lost.
        //
        // Daily cap: above 4000 XP gained today, further gains scale by
        // 0.2 (matches RimWorld's `SaturatedLearningFactor`). The daily
        // window is cleared by SimulationCore on day boundary. Levels are
        // capped at 20.

        public const int   MaxLevel              = 20;
        public const float MaxFullRateXpPerDay   = 4000f;
        public const float SaturatedLearningFactor = 0.2f;

        // RimWorld curve: `1000 + 100 × level²`. Level 0→1 = 1000 XP;
        // level 19→20 = 1000 + 100·361 = 37 100 XP. With ~80 XP per
        // boulder mined, leveling 0→1 in Mining takes ~13 boulders;
        // mid-game 5→6 takes ~35 boulders; mastery 19→20 takes ~460.
        public static float LevelThreshold(int level) =>
            1000f + 100f * level * level;

        // Adds raw XP, applies saturation if today's gain exceeds the cap,
        // and rolls up level transitions. Idempotent against missing dict
        // keys (they default to 0). Caller responsibility: check that
        // `skillName` is a real skill — silent no-op otherwise.
        public static void GainXp(Smurf s, string skillName, float amount)
        {
            if (s == null || amount <= 0f) return;
            if (!s.Skills.ContainsKey(skillName)) return;   // not a real skill
            int level = s.Skills[skillName];
            if (level >= MaxLevel) return;                  // already maxed

            // Saturation: scale the portion of `amount` that lands above
            // today's cap. Lets the player still grind a bit past the cap
            // without it being free.
            s.SkillsXpToday.TryGetValue(skillName, out float todayXp);
            float effective;
            if (todayXp >= MaxFullRateXpPerDay)
            {
                effective = amount * SaturatedLearningFactor;
            }
            else if (todayXp + amount <= MaxFullRateXpPerDay)
            {
                effective = amount;
            }
            else
            {
                float fullPart = MaxFullRateXpPerDay - todayXp;
                float satPart  = (amount - fullPart) * SaturatedLearningFactor;
                effective = fullPart + satPart;
            }
            s.SkillsXpToday[skillName] = todayXp + amount;   // track raw, not effective

            // Roll into the per-level XP bucket and level up if threshold
            // crossed (loop because a single big chunk could level twice).
            s.SkillsXp.TryGetValue(skillName, out float xp);
            xp += effective;
            while (level < MaxLevel)
            {
                float threshold = LevelThreshold(level);
                if (xp < threshold) break;
                xp -= threshold;
                level++;
            }
            s.Skills[skillName]   = level;
            s.SkillsXp[skillName] = xp;
        }

        // Called from SimulationCore on day boundary to reset the
        // daily-cap window. Also a hook for future XP decay (RimWorld
        // patterns: skills 10-20 lose 0.1-12 XP/interval; we'll add
        // when Phase 5 / 6 introduce specialised colonists worth
        // protecting from rust).
        public static void ResetDailyXp(Smurf s)
        {
            s.SkillsXpToday.Clear();
        }

        // Distributes random skill points using a right-skewed budget.
        //
        // Budget: u = min(rand, rand, rand) ∈ [0,1] — gives a cubic CDF so ~65% of
        // Sprouts land below 100 points and 320 (full cap) is exceedingly rare.
        // Older smurfs receive a guaranteed age floor that raises their minimum.
        // Role-primary skills receive 4× allocation weight over all other skills.
        public static void Distribute(Smurf s, Random rng)
        {
            foreach (var def in All)
                s.Skills[def.Name] = 0;

            double u = Math.Min(Math.Min(rng.NextDouble(), rng.NextDouble()), rng.NextDouble());
            int floor = s.LifeStage switch
            {
                LifeStage.Juvenile   => 20,
                LifeStage.Adult      => 50,
                LifeStage.Elder      => 70,
                LifeStage.LastSeason => 80,
                _                    =>  0,
            };
            int budget = Math.Min(320, (int)(u * (320 - floor)) + floor);
            if (budget <= 0) return;

            // Role-primary skills get 4× weight; all others get 1×.
            var roleBonus = _roleBonuses.TryGetValue(s.Role, out var rb) ? rb : null;
            var weights = new float[All.Count];
            for (int i = 0; i < All.Count; i++)
                weights[i] = roleBonus != null && roleBonus.ContainsKey(All[i].Name) ? 4f : 1f;
            float totalW = weights.Sum();

            int spent = 0;
            while (spent < budget && totalW > 0f)
            {
                // Weighted random pick.
                float roll = (float)(rng.NextDouble() * totalW);
                int idx = 0;
                float cum = 0f;
                for (; idx < weights.Length - 1; idx++)
                {
                    cum += weights[idx];
                    if (roll < cum) break;
                }

                if (s.Skills[All[idx].Name] < 20)
                {
                    s.Skills[All[idx].Name]++;
                    spent++;
                }
                else
                {
                    // Skill maxed — remove it from future selection.
                    totalW    -= weights[idx];
                    weights[idx] = 0f;
                }
            }
        }
    }
}
