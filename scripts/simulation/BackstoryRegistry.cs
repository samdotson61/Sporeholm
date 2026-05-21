using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // v0.4.64 (G6 from rimport.md) — Backstories. RimWorld parallel:
    // `BackstoryDef` with skillGains + work-disable + bodyType filter.
    // Every shroomp gets a childhood (always) and, if old enough, an
    // adulthood (Juvenile+). Each backstory is one short label string
    // plus a small dictionary of additive skill modifiers applied at
    // generation time AFTER `SkillRegistry.Distribute`.
    //
    // Skill bumps are modest (+1 to +3 per skill) so backstories layer
    // on top of the existing right-skewed budget rather than dominating
    // it. A shroomp with backstory "Wandering Berry-Picker" gets a small
    // Foraging boost regardless of role; a Crafter with that childhood
    // is a slightly better forager than the average Crafter.
    //
    // Phase 5 will plug backstories into the ShroompCardPanel (Main tab,
    // under the shroomp's name). For now they live as data only.
    public static class BackstoryRegistry
    {
        public sealed record BackstoryDef(
            string Key,
            string Label,           // shown on the shroomp card / hover
            string Description,
            Dictionary<string, int> SkillBumps);

        // Childhood backstories — apply to every shroomp at generation.
        // Themes lean on canon Shroomp flavour: foraging fields, bonfire
        // chores, mushroom-house life, observing elders.
        public static readonly BackstoryDef[] Childhoods =
        {
            new("WanderingBerryPicker",
                "Wandering Berry-Picker",
                "Spent every summer combing the hills for capberries.",
                new() { ["Botany"] = 3, ["Athletics"] = 1 }),
            new("HearthApprentice",   // v0.6.2 — ID kept for save compat; display renamed Hearth → Bonfire
                "Bonfire Apprentice",
                "Helped tend the village bonfire before they could walk.",
                new() { ["Crafting"] = 2, ["Construction"] = 1 }),
            new("MushroomLetter",
                "Mushroom-House Letter",
                "Hand-delivered mail between mushroom-houses.",
                new() { ["Athletics"] = 2, ["Social"] = 1 }),
            new("ObservantSprout",
                "Observant Sprout",
                "Watched the elders work and asked too many questions.",
                new() { ["Magic"] = 2, ["Social"] = 1 }),
            new("StreamSplasher",
                "Stream-Splasher",
                "Played in the rivers; learned which fish are friendly.",
                new() { ["Athletics"] = 2, ["Botany"] = 1 }),
            new("StarGazer",
                "Star-Gazer",
                "Slept under the open sky charting the constellations.",
                new() { ["Study"] = 2, ["Magic"] = 1 }),
            new("WorkshopShadow",
                "Workshop Shadow",
                "Tagged along behind every Crafter, fetching tools.",
                new() { ["Crafting"] = 2, ["Mining"] = 1 }),
            new("HerbGardener",
                "Herb Gardener",
                "Tended the village medicinal herb plot.",
                new() { ["Botany"] = 2, ["Healing"] = 1 }),
        };

        // Adulthood backstories — only assigned to Juvenile+ shroomps (≥20).
        // Heavier skill modifiers because they represent a chosen vocation.
        public static readonly BackstoryDef[] Adulthoods =
        {
            new("ForestScout",
                "Forest Scout",
                "Patrolled the colony perimeter against Gargamel's surprises.",
                new() { ["Athletics"] = 3, ["Botany"] = 2, ["Ranged"] = 1 }),
            new("LibraryMouse",
                "Library Mouse",
                "Catalogued every scroll in the elder's archive.",
                new() { ["Magic"] = 3, ["Study"] = 2 }),
            new("BattleVeteran",
                "Battle Veteran",
                "Survived a raid; wears the scar with quiet pride.",
                new() { ["Melee"] = 3, ["Athletics"] = 2 }),
            new("RitualSinger",
                "Ritual Singer",
                "Led the seasonal ceremonies at the Magic Grove.",
                new() { ["Magic"] = 3 }),   // v0.5.84r: Arcane + Ritual collapsed into Magic; keep higher bonus
            new("CauldronKeeper",
                "Cauldron Keeper",
                "Kept the great pot bubbling through three winters.",
                new() { ["Crafting"] = 3, ["Botany"] = 2 }),
            new("StonemasonsAssistant",
                "Stonemason's Assistant",
                "Cut the bricks for half the village's mushroom-houses.",
                new() { ["Mining"] = 2, ["Construction"] = 3 }),
            new("HealersApprentice",
                "Healer's Apprentice",
                "Mixed poultices in the back of the infirmary for years.",
                new() { ["Healing"] = 3, ["Social"] = 2 }),
            new("HearthOrator",   // v0.6.2 — ID kept for save compat; display renamed Hearth → Bonfire
                "Bonfire Orator",
                "Held the colony rapt with stories of the Old Shroomps.",
                new() { ["Social"] = 3, ["Magic"] = 1 }),   // v0.5.84r: Social + Leadership collapsed; bumped to 3
            new("RoamingTinker",
                "Roaming Tinker",
                "Wandered between settlements trading small repairs.",
                new() { ["Crafting"] = 2, ["Social"] = 2, ["Athletics"] = 1 }),
            new("GargamelSurvivor",
                "Gargamel Survivor",
                "Was once nearly bagged. Still wakes from the dream.",
                new() { ["Athletics"] = 2, ["Social"] = 1 }),
        };

        // Lookup by key for save/load round-trip and UI display.
        private static readonly Dictionary<string, BackstoryDef> _byKey = Build();
        private static Dictionary<string, BackstoryDef> Build()
        {
            var d = new Dictionary<string, BackstoryDef>(Childhoods.Length + Adulthoods.Length);
            foreach (var b in Childhoods) d[b.Key] = b;
            foreach (var b in Adulthoods) d[b.Key] = b;
            return d;
        }

        public static BackstoryDef? Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return _byKey.TryGetValue(key, out var def) ? def : null;
        }

        // Picks a Childhood + (if Juvenile+) Adulthood, writes them onto the
        // shroomp, and applies their skill bumps. Idempotent: if either field
        // is already populated (e.g. from a save load) the existing key is
        // kept and bumps are NOT re-applied (would double-count). Caller
        // should invoke this AFTER `SkillRegistry.Distribute` so the bumps
        // layer on top of the right-skewed budget.
        public static void AssignAndApply(Shroomp s, Random rng)
        {
            if (string.IsNullOrEmpty(s.Childhood))
            {
                var ch = Childhoods[rng.Next(Childhoods.Length)];
                s.Childhood = ch.Key;
                ApplyBumps(s, ch.SkillBumps);
            }
            // Adulthood requires Juvenile+ (age ≥ 20). Sprouts skip.
            if (string.IsNullOrEmpty(s.Adulthood) && s.AgeInYears >= 20)
            {
                var ad = Adulthoods[rng.Next(Adulthoods.Length)];
                s.Adulthood = ad.Key;
                ApplyBumps(s, ad.SkillBumps);
            }
        }

        private static void ApplyBumps(Shroomp s, Dictionary<string, int> bumps)
        {
            foreach (var (skill, delta) in bumps)
            {
                if (!s.Skills.ContainsKey(skill)) continue;
                s.Skills[skill] = Math.Min(SkillRegistry.MaxLevel, s.Skills[skill] + delta);
            }
        }
    }
}
