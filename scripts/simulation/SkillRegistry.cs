using System;
using System.Collections.Generic;
using System.Linq;

namespace Sporeholm.Simulation
{
    public sealed record SkillDef(string Name, string Domain, string Description);

    public static class SkillRegistry
    {
        // v0.5.84r — skill list compacted from 16 → 11 skills per Sam's
        // playtest-prep restructure. Merges + renames:
        //   • Foraging + Botany → Botany (all plant/chop work)
        //   • Arcane + Ritual + Lore → Magic (Attune + future magic system)
        //   • Empathy + Leadership → Social (Empathy/Leadership covered the
        //     same conversational ground)
        //   • Medicine → Healing (the role-driving skill the Healer job uses)
        //   • Research → Study (aesthetic rename)
        // Cap budget proportionally reduced 320 → 220 (11 × 20 max).
        // Legacy saves migrate via SkillNameMigration applied at Shroomp load.
        public static readonly IReadOnlyList<SkillDef> All = new[]
        {
            // Survival
            new SkillDef("Botany",       "Survival",  "Foraging, planting, cutting, and chopping — all plant work."),
            new SkillDef("Mining",       "Survival",  "Excavating stone, ore, and earth."),
            new SkillDef("Athletics",    "Survival",  "Speed, stamina, and physical endurance. Levels via hauling and walking; raises move speed, carry capacity, and disease resistance."),
            // Combat
            new SkillDef("Melee",        "Combat",    "Close-quarters fighting with weapons or fists."),
            new SkillDef("Ranged",       "Combat",    "Attacking at distance with thrown or launched projectiles."),
            // Crafting
            new SkillDef("Crafting",     "Crafting",  "Making tools, goods, meals, and equipment at workbenches."),
            new SkillDef("Construction", "Crafting",  "Building and repairing structures."),
            // Magic (placeholder — full system in a future phase)
            new SkillDef("Magic",        "Magic",     "Channeling magic essence, performing rituals, and Shroomp lore. Levels via Attune until the full magic system ships."),
            // Social
            new SkillDef("Social",       "Social",    "Conversation, empathy, leadership, and community influence."),
            // Knowledge
            new SkillDef("Study",        "Knowledge", "Systematic investigation and discovery (Phase 11)."),
            new SkillDef("Healing",      "Knowledge", "Treating wounds and illness. Drives the Healer job's tend speed and quality."),
        };

        // v0.5.84r — legacy skill name migration. Applied at Shroomp load
        // to rename keys in s.Skills / s.SkillsXp / s.SkillsXpToday from
        // the pre-restructure names. Multiple old skills can merge into
        // one new skill — values are summed (Skills clamped to 20).
        public static readonly Dictionary<string, string> SkillNameMigration = new()
        {
            { "Foraging",   "Botany" },
            { "Arcane",     "Magic" },
            { "Ritual",     "Magic" },
            { "Lore",       "Magic" },
            { "Empathy",    "Social" },
            { "Leadership", "Social" },
            { "Medicine",   "Healing" },
            { "Research",   "Study" },
            { "Cooking",    "Crafting" },
        };

        // v0.5.84r — role bonuses re-mapped to new skill set.
        // Primary (+3) · Secondary (+2) · Supporting (+1)
        //
        // Forager   — Botany (food + plant + chop), Athletics (lots of walking), Mining (occasional excavation)
        // Crafter   — Crafting (workbench-driven), Construction (build), Mining (resource collection)
        // Scholar   — Study, Magic (lore consolidated into magic), Healing (medical knowledge overlap)
        // Mage      — Magic, Study, Social (mystical communication)
        // Caretaker — Healing, Social (empathy consolidated), Crafting (medicine prep)
        // Guardian  — Melee, Ranged, Athletics
        // Elder     — Social (leadership consolidated), Study, Healing
        private static readonly Dictionary<string, Dictionary<string, int>> _roleBonuses = new()
        {
            ["Forager"]   = new() { ["Botany"]   = 3, ["Athletics"]    = 2, ["Mining"]   = 1 },
            ["Crafter"]   = new() { ["Crafting"] = 3, ["Construction"] = 2, ["Mining"]   = 1 },
            ["Scholar"]   = new() { ["Study"]    = 3, ["Magic"]        = 2, ["Healing"]  = 1 },
            ["Sage"]      = new() { ["Magic"]    = 3, ["Study"]        = 2, ["Social"]   = 1 },
            ["Caretaker"] = new() { ["Healing"]  = 3, ["Social"]       = 2, ["Crafting"] = 1 },
            ["Guardian"]  = new() { ["Melee"]    = 3, ["Ranged"]       = 2, ["Athletics"] = 1 },
            ["Elder"]     = new() { ["Social"]   = 3, ["Study"]        = 2, ["Healing"]  = 1 },
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
        public static void GainXp(Shroomp s, string skillName, float amount)
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
        public static void ResetDailyXp(Shroomp s)
        {
            s.SkillsXpToday.Clear();
        }

        // v0.5.84r — apply the legacy-skill rename map to a loaded
        // Skills dict (and matching XP dicts). For 1:1 renames the
        // value moves cleanly; for many-to-one merges (Arcane + Ritual
        // + Lore → Magic), the highest level wins for s.Skills and the
        // XP buckets sum. Safe to call on a fresh dict — no-op if no
        // legacy keys present.
        public static void MigrateLegacySkillKeys(
            Dictionary<string, int>   skills,
            Dictionary<string, float> xp,
            Dictionary<string, float> xpToday)
        {
            foreach (var (oldName, newName) in SkillNameMigration)
            {
                if (skills.TryGetValue(oldName, out int lvl))
                {
                    if (skills.TryGetValue(newName, out int existing))
                        skills[newName] = Math.Min(MaxLevel, Math.Max(existing, lvl));
                    else
                        skills[newName] = Math.Min(MaxLevel, lvl);
                    skills.Remove(oldName);
                }
                if (xp.TryGetValue(oldName, out float oldXp))
                {
                    xp.TryGetValue(newName, out float existingXp);
                    xp[newName] = existingXp + oldXp;
                    xp.Remove(oldName);
                }
                if (xpToday.TryGetValue(oldName, out float oldToday))
                {
                    xpToday.TryGetValue(newName, out float existingToday);
                    xpToday[newName] = existingToday + oldToday;
                    xpToday.Remove(oldName);
                }
            }
        }

        // v0.5.84r — total skill point cap. Was 320 (16 skills × 20 max);
        // now 220 (11 × 20). Sam: "ensure that skill point allocation
        // weight on shroomp creation is adjusted for the new total skill
        // point reduction (now 11 skills for 220 total skill points —
        // reduce allocation by the appropriate percentage)."
        public const int SkillBudgetCap = 220;

        // Distributes random skill points using a right-skewed budget.
        //
        // Budget: u = min(rand, rand, rand) ∈ [0,1] — gives a cubic CDF so ~65% of
        // Sprouts land below SkillBudgetCap×0.31 points and the full cap is exceedingly rare.
        // Older shroomps receive a guaranteed age floor that raises their minimum.
        // Role-primary skills receive 4× allocation weight over all other skills.
        // v0.5.84r — budget reduced 320 → 220 to match the 11-skill set.
        // Age floors scaled by the same 220/320 = 0.69 ratio to keep
        // the relative spread.
        public static void Distribute(Shroomp s, Random rng)
        {
            foreach (var def in All)
                s.Skills[def.Name] = 0;

            double u = Math.Min(Math.Min(rng.NextDouble(), rng.NextDouble()), rng.NextDouble());
            int floor = s.LifeStage switch
            {
                LifeStage.Juvenile   => 14,   // was 20 (20×0.69 ≈ 14)
                LifeStage.Adult      => 34,   // was 50
                LifeStage.Elder      => 48,   // was 70
                LifeStage.LastSeason => 55,   // was 80
                _                    =>  0,
            };
            int budget = Math.Min(SkillBudgetCap, (int)(u * (SkillBudgetCap - floor)) + floor);
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
