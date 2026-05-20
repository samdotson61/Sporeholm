using System;
using System.Collections.Generic;
using System.Linq;

namespace Sporeholm.Simulation
{
    // v0.5.62 — Procedural Shroomp name generator (replaces the prior
    // canon-name pool). Syllable-mix design per shroomport.md §6:
    //
    //   Tier 1 (founding 7 + early births): curated "memorable" pool
    //         (Frungus, Mycell, Boletus, Trufflina, etc.) — gives the
    //         player recognizable lead characters their first session.
    //   Tier 2 (subsequent births, infinite supply): procedural syllable
    //         mix from mushroom + fungal-Latin roots + sex-appropriate
    //         suffix. Names like Sporon (m), Capberina (f), Russetta (f),
    //         Gillot (m) emerge from a small grammar with ~36 prefix
    //         syllables × ~30 optional connectors × 10 gendered suffixes.
    //   Tier 3 (collision fallback): numeric suffix on a curated name.
    //
    // Conflict filter rejects real-world English words the syllable mix
    // can stumble into (Pelican, Coral, Iris, etc.) so the colony reads
    // as a mushroom-being roster rather than a botany dictionary.
    public static class ShroompNameGenerator
    {
        // ── Curated founding-7 + early-birth pool ─────────────────────────────
        // Hand-picked so the first session has memorable leads. Once exhausted
        // (after ~30 births), Tier 2 procedural takes over indefinitely.
        // v0.5.77 — top 2003 US SSA baby names mixed in alongside the
        // mushroom-themed leads so the roster reads with more variety
        // (Sam: "Add most popular baby names 2003 to the random name
        // generator. We want a little more diversity."). Pre-v0.5.77 every
        // founding-name was fungal-Latin which read uniform; the mix puts
        // a Jacob next to a Frungus next to an Emily next to a Trufflina.
        private static readonly string[] MaleCurated =
        {
            // Mushroom-themed (originals — v0.5.62)
            "Frungus", "Mycell", "Boletus", "Stipen", "Capron",
            "Gillot", "Trufflot", "Sporon", "Hyphor", "Bracton",
            "Inkar", "Pezon", "Russet", "Morel", "Driftan",
            "Pelt",   "Caulin", "Mossix",
            // v0.5.81 — more mushroom-themed leads. Sam: "Add more
            // mushroom inspired names to the rolling random name
            // generator." Each maps to a real-world mycology root —
            // species (Cantharellus, Suillus, Coprinus, Galerina,
            // Marasmius, Pleurotus, Polyporus, Lentinula, Ganoderma,
            // Cordyceps, Volvariella, Cortinarius, Calvatia) or fungal
            // vocabulary (lichen, reishi, maitake).
            "Cantharel", "Suillus",  "Coprin",   "Galeron",  "Marasmen",
            "Pleurot",   "Polypor",  "Lentor",   "Reishel",  "Cordon",
            "Lichor",    "Volvar",   "Cortin",   "Calvar",   "Maitak",
            "Hericor",   "Tramon",   "Annulot",  "Veilis",   "Stipix",
            // Top 2003 SSA boys (1-20) — v0.5.77 diversity pass
            "Jacob",  "Michael", "Joshua", "Matthew", "Andrew",
            "Joseph", "Ethan",   "Daniel", "Christopher", "Anthony",
            "William","Ryan",    "Nicholas","David",  "Tyler",
            "Alexander","James", "John",   "Dylan",  "Nathan",
        };

        private static readonly string[] FemaleCurated =
        {
            // Mushroom-themed (originals — v0.5.62)
            "Trufflina", "Russella", "Mycella", "Sporetta", "Boletilla",
            "Capberina", "Gillona", "Trichina", "Pezilla", "Morellia",
            "Hyphilla", "Driftana", "Bractina",
            // v0.5.81 — more mushroom-themed leads (matched to the
            // male additions: same mycology roots, female suffixes
            // -a / -ella / -ia / -ana / -illa).
            "Chantera",   "Mycenella", "Lepiota",   "Galerina",  "Marasmia",
            "Reishilla",  "Cortina",   "Volvaria",  "Cremella",  "Enokina",
            "Cordycella", "Calvatia",  "Inocybia",  "Cantharella","Pleurota",
            "Annulina",   "Tramana",   "Veila",     "Hyphella",  "Capreana",
            // Top 2003 SSA girls (1-20) — v0.5.77 diversity pass
            "Emily",   "Emma",     "Madison",  "Hannah",   "Olivia",
            "Abigail", "Alexis",   "Ashley",   "Elizabeth","Samantha",
            "Isabella","Sarah",    "Anna",     "Grace",    "Allison",
            "Taylor",  "Brianna",  "Lauren",   "Sofia",    "Kayla",
        };

        // ── Procedural syllable pools ─────────────────────────────────────────
        private static readonly string[] Prefixes =
        {
            // Original prefixes (v0.5.62)
            "Myc",   "Hyph",  "Spor",  "Cap",   "Stem",
            "Bol",   "Mor",   "Russ",  "Lact",  "Truffl",
            "Trem",  "Pez",   "Trich", "Psily", "Ink",
            "Port",  "Glob",  "Hum",   "Tan",   "Rust",
            "Drift", "Vein",  "Root",  "Dust",  "Mold",
            "Frung", "Shroom","Fruit", "Peel",  "Mush",
            "Sap",   "Agar",  "Por",   "Gill",  "Rim",
            "Bract",
            // v0.5.81 — additional mycology roots so Tier-2 procedural
            // generation has more fungal variety. Each is a real-world
            // species genus (Cantharellus / Suillus / Coprinus / Galerina /
            // Marasmius / Pleurotus / Polyporus / Lentinula / Hericium /
            // Lepiota / Ganoderma / Volvariella / Cortinarius / Calvatia /
            // Inocybe / Mycena / Cordyceps) or fungal-anatomy term
            // (universal veil / annulus / trama / hymenium). Sam: "Add
            // more mushroom inspired names to the rolling random name
            // generator."
            "Chant",  "Suil",   "Cord",   "Mait",   "Enok",
            "Reish",  "Polyp",  "Galer",  "Inoc",   "Maras",
            "Lich",   "Lent",   "Copr",   "Volv",   "Lepiot",
            "Pleur",  "Heric",  "Cantha", "Ganod",  "Cremn",
            "Mycen",  "Veil",   "Annul",  "Tram",   "Hymen",
        };

        private static readonly string[] Connectors =
        {
            "o", "a", "i", "e", "u",
            "ell", "ill", "ull", "oll", "all", "em", "im", "um", "om",
            "en", "on", "in", "un", "et", "it", "ot", "ar", "or", "er",
            "ic", "ix", "al", "il", "ul", "ed",
        };

        private static readonly string[] MaleSuffixes =
        {
            "us", "on", "en", "ix", "ot", "el", "ar", "or", "is", "er",
        };

        private static readonly string[] FemaleSuffixes =
        {
            "ina", "ella", "ia", "aria", "etta", "illa", "ona", "isa", "ana", "ette",
        };

        // Real-world conflict filter. The syllable mix can stumble into
        // English nouns (esp. via the Connector + Suffix combinatorics).
        // Reject these and retry — names should read as fungal, not as
        // a vocabulary spelling list.
        private static readonly HashSet<string> ConflictWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Pelican", "Coral", "Iris", "Marina", "Felicia", "Anita",
            "Anna", "Hannah", "Olivia", "Sophia", "Maria", "Maxima",
            "Solid", "Liquid", "Carbon", "Helium", "Argon", "Krypton",
            "Boris", "Doris", "Morris", "Norris", "Harris", "Paris",
            "Tarot", "Carrot", "Parrot", "Marriott", "Patriot",
        };

        public static string Generate(IEnumerable<string> usedNames, Random rng, Sex sex)
        {
            var used = new HashSet<string>(usedNames);

            // Tier 1 — curated pool. Drains over the first ~30 births.
            var curated = (sex == Sex.Female ? FemaleCurated : MaleCurated)
                          .Where(n => !used.Contains(n)).ToArray();
            if (curated.Length > 0)
                return curated[rng.Next(curated.Length)];

            // Tier 2 — procedural syllable mix. Infinite supply (per-session
            // collisions retried up to 16 attempts before falling through).
            for (int attempt = 0; attempt < 16; attempt++)
            {
                string name = BuildProcedural(rng, sex);
                if (used.Contains(name)) continue;
                if (ConflictWords.Contains(name)) continue;
                if (name.Length > 12) continue;   // length-clamp safety
                return name;
            }

            // Tier 3 — numeric suffix on a curated name as fallback. Almost
            // never fires (16 procedural retries is generous) but keeps the
            // generator total — no infinite loops.
            string baseName = (sex == Sex.Female ? FemaleCurated : MaleCurated)[
                rng.Next(sex == Sex.Female ? FemaleCurated.Length : MaleCurated.Length)];
            int n = 2;
            string suffixed;
            do { suffixed = $"{baseName} {n++}"; } while (used.Contains(suffixed));
            return suffixed;
        }

        private static string BuildProcedural(Random rng, Sex sex)
        {
            string prefix = Prefixes[rng.Next(Prefixes.Length)];
            string connector = rng.NextDouble() < 0.4
                ? Connectors[rng.Next(Connectors.Length)]
                : "";
            string[] suffixes = sex == Sex.Female ? FemaleSuffixes : MaleSuffixes;
            string suffix = suffixes[rng.Next(suffixes.Length)];
            return prefix + connector + suffix;
        }
    }
}
