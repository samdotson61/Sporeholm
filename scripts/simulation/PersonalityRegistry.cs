using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    public static class PersonalityRegistry
    {
        // MoodModifier: flat offset applied to mood score after needs calculation.
        // Positive = more resilient; negative = lower effective mood / earlier break.
        public record PersonalityDef(string Name, string Description, float MoodModifier = 0f);

        public static readonly PersonalityDef[] All =
        {
            // ── Shroomp-canon archetypes ────────────────────────────────────────────
            new("Know-It-All",
                "Corrects everyone constantly — even when wrong.",      -2f),
            new("Grumpy",
                "Hates mornings. And afternoons. And evenings.",        -6f),
            new("Accident-Prone",
                "Trips over flat ground with alarming regularity.",     -6f),
            new("Daydreamer",
                "Stares at clouds when they should be foraging.",       -3f),
            new("Vain",
                "Checks their reflection in every puddle.",             -3f),
            new("Prankster",
                "Thinks exploding gift boxes are peak comedy.",         +4f),
            new("Greedy Gut",
                "What's yours is mine; what's mine is also mine.",      -4f),
            new("Brawny",
                "Never skips leg day. Or arm day. Or any day.",         +3f),
            new("Sleepyhead",
                "Conserves energy via heroic amounts of napping.",      -4f),
            new("Gossip",
                "Knows everyone's business before they do.",            +2f),

            // ── Real psychology with a Shroomp twist ────────────────────────────────
            new("Introvert",
                "Recharges alone inside a quiet mushroom.",             +3f),
            new("Optimist",
                "Sees the bright side of Gargamel attacks.",            +5f),
            new("Pessimist",
                "Convinced the sky is falling — and it might be.",      -8f),
            new("Perfectionist",
                "Won't rest until that wall is EXACTLY three apples tall.", -5f),
            new("Glutton",
                "Eats like tomorrow won't come. Extra sarsaparilla, please.", -2f),
            new("Night Owl",
                "Wide awake at midnight; useless before noon.",         -3f),
            new("Worrywart",
                "Every sneeze is the start of the Blue Plague.",        -10f),
            new("Stoic",
                "Emotions? Never heard of them.",                       +8f),
            new("Empath",
                "Feels everyone else's feelings. It's exhausting.",     -5f),
            new("Thrill-Seeker",
                "Runs TOWARD the cat.",                                 +4f),

            // ── Shroomp-specific humour ─────────────────────────────────────────────
            new("Sarsaparilla Snob",
                "Only the finest leaves. ONLY.",                        -1f),
            new("Mushroom Whisperer",
                "Claims to hear fungi. Nobody argues.",                 +2f),
            new("Cat Paranoid",
                "Azrael is behind every bush. EVERY bush.",             -8f),
            new("Hat Obsessed",
                "My hat defines me as a person.",                        0f),
            new("Three-Apples Complex",
                "Three apples tall and PROUD of it.",                   +1f),
        };

        // v0.4.64 (G8 from rimport.md) — pairs that can't coexist on the
        // same shroomp. Mirrors RimWorld's `TraitDef.conflictingTraits`. The
        // assignment loop below removes the conflicting partner from the
        // pool whenever it picks one of these. Conflict is symmetric —
        // we store ordered pairs and check both directions in the lookup.
        //
        // Picks are based on real psychological/lore contradictions:
        // Optimist ↔ Pessimist obvious; Stoic ↔ Empath (no feelings vs
        // every feeling); Sleepyhead ↔ Night Owl (always sleeping vs
        // chronically awake at the wrong hours); Introvert ↔ Gossip
        // (needs solitude vs needs the colony grapevine).
        private static readonly (string A, string B)[] ConflictPairs =
        {
            ("Optimist",   "Pessimist"),
            ("Stoic",      "Empath"),
            ("Stoic",      "Worrywart"),
            ("Sleepyhead", "Night Owl"),
            ("Introvert",  "Gossip"),
            ("Greedy Gut", "Sarsaparilla Snob"),  // gluttonous vs picky
        };

        private static bool ConflictsWithAny(string candidate, List<string> picked)
        {
            for (int i = 0; i < ConflictPairs.Length; i++)
            {
                var (a, b) = ConflictPairs[i];
                if (candidate == a && picked.Contains(b)) return true;
                if (candidate == b && picked.Contains(a)) return true;
            }
            return false;
        }

        // Assigns 1–5 personality traits weighted by age.
        // Young shroomps get fewer traits; elders accumulate more quirks over time.
        // v0.4.64 (G8) — conflict-aware: a candidate that contradicts an
        // already-picked trait is skipped and a different one is rolled.
        // The pool can run dry on small trait counts before reaching the
        // requested count; in that case the shroomp just gets fewer traits
        // (no infinite loop).
        public static List<string> Assign(Random rng, int ageInYears = 50)
        {
            int count = ageInYears switch
            {
                < 20  => rng.Next(1, 3),   // Sprout:  1-2
                < 50  => rng.Next(1, 4),   // Juvenile: 1-3
                < 200 => rng.Next(2, 4),   // Young adult: 2-3
                < 400 => rng.Next(2, 5),   // Adult: 2-4
                _     => rng.Next(3, 6),   // Elder/LastSeason: 3-5
            };

            var pool = new List<int>();
            for (int i = 0; i < All.Length; i++) pool.Add(i);

            var result = new List<string>();
            while (result.Count < count && pool.Count > 0)
            {
                int pickIdx = rng.Next(pool.Count);
                string candidate = All[pool[pickIdx]].Name;
                pool.RemoveAt(pickIdx);
                if (ConflictsWithAny(candidate, result)) continue;
                result.Add(candidate);
            }
            return result;
        }

        public static PersonalityDef? Get(string name)
        {
            foreach (var def in All)
                if (def.Name == name) return def;
            return null;
        }

        // v0.5.1 — multi-line tooltip with mood modifier + every concrete
        // gameplay effect the trait exercises. Sam's feedback: bare flavour
        // descriptions don't tell the player WHY the trait matters.
        // Surfaces:
        //   • MoodModifier (signed value)
        //   • Idle-task weighting nudges (BehaviorSystem.cs ~L1495+)
        //   • Need-threshold modifiers (BehaviorSystem.cs eatThreshold,
        //     socialThres etc.)
        //   • Conflict pairs (G8 ConflictPairs)
        // Effects with no per-trait special handling list only the mood
        // line. Format mirrors RimWorld's "Trait → Description → Effect
        // bullet list" tooltip.
        public static string BuildGameplayTooltip(PersonalityDef def)
        {
            if (def == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(def.Name).Append('\n').Append(def.Description);

            // Mood modifier (every trait has one, even if 0).
            sb.Append("\n\n");
            if (def.MoodModifier > 0f)
                sb.Append($"Mood: +{def.MoodModifier:0}");
            else if (def.MoodModifier < 0f)
                sb.Append($"Mood: {def.MoodModifier:0}");
            else
                sb.Append("Mood: ±0");

            // Per-trait gameplay effects. Hand-written per the trait so we
            // can phrase each one naturally rather than dumping enum values.
            string? effects = TraitEffects(def.Name);
            if (!string.IsNullOrEmpty(effects))
            {
                sb.Append('\n').Append(effects);
            }

            // Conflict-pair notice.
            string? conflicts = ConflictsFor(def.Name);
            if (!string.IsNullOrEmpty(conflicts))
            {
                sb.Append("\nIncompatible with: ").Append(conflicts);
            }

            return sb.ToString();
        }

        private static string? TraitEffects(string name) => name switch
        {
            "Introvert"          => "Idle: prefers Observe over Converse.\nEats earlier when alone (lower social-need threshold).",
            "Gossip"             => "Idle: strongly prefers Converse with nearby shroomps.",
            "Daydreamer"         => "Idle: prefers Observe and Loiter.",
            "Brawny"             => "Idle: prefers Wander; rarely Loiters.",
            "Sleepyhead"         => "Idle: prefers Loiter; rarely Wanders.",
            "Thrill-Seeker"      => "Idle: strongly prefers Wander.",
            "Mushroom Whisperer" => "Idle: more likely to Meditate.",
            "Glutton"            => "Eats sooner (Nutrition threshold 70 vs default 50).",
            "Night Owl"          => "Less effective during the colony's daylight work hours.",
            "Worrywart"          => "Heavy mood penalty translates into earlier mental-state slips.",
            "Optimist"           => "Bonus mood smooths over normal need decay.",
            "Pessimist"          => "Steep mood penalty — needs careful management.",
            "Stoic"              => "Strong mood resilience; rarely cracks under stress.",
            "Empath"             => "Mood drops when nearby shroomps are unhappy.",
            "Cat Paranoid"       => "Steep mood penalty; frightened-thoughts persist longer.",
            "Greedy Gut"         => "Lower mood; tends to grab the largest available food stack.",
            "Sarsaparilla Snob"  => "Refuses Crude-quality food more often.",
            _                    => null,
        };

        private static string? ConflictsFor(string name)
        {
            System.Text.StringBuilder? sb = null;
            for (int i = 0; i < ConflictPairs.Length; i++)
            {
                var (a, b) = ConflictPairs[i];
                string? other = null;
                if (name == a) other = b;
                else if (name == b) other = a;
                if (other == null) continue;
                if (sb == null) sb = new System.Text.StringBuilder();
                else            sb.Append(", ");
                sb.Append(other);
            }
            return sb?.ToString();
        }
    }
}
