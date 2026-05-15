using System;
using System.Collections.Generic;
using System.Linq;

namespace SmurfulationC.Simulation
{
    // Tier 1: Smurf-canon names.
    // Tier 2: Most popular US baby names of 2003 (SSA data) — used when canon pool exhausted.
    // Tier 3: Numbered fallback ("Smurf N") — used when both pools exhausted.
    public static class SmurfNameGenerator
    {
        private static readonly string[] MaleCanon =
        {
            "Snappy", "Painter", "Harmony", "Baker", "Vanity",
            "Panicky", "Sloppy", "Jokey", "Tracker", "Poet",
            "Dreamer", "Gutsy", "Nosey", "Scaredy", "Timid",
            "Speedy", "Architect", "Tinkerer", "Wanderer", "Frosty",
            "Pebbles", "Acorn", "Brambly", "Mossy", "Ember",
            "Cobbler", "Plucky", "Sturdy", "Whistler", "Grumbly",
            "Cheerful", "Bouncy", "Nimble", "Sooty", "Freckles",
            "Tailor", "Miner", "Sailor", "Fiddler", "Blabber",
        };

        private static readonly string[] Female​Canon =
        {
            "Lily", "Violet", "Sassette", "Marina", "Lacey",
            "Blossom", "Meadow", "Dawn", "Clover", "Fern",
        };

        // US SSA top baby names, 2003.
        private static readonly string[] Male2003 =
        {
            "Jacob", "Michael", "Joshua", "Matthew", "Andrew",
            "Joseph", "Ethan", "Daniel", "Christopher", "Anthony",
            "William", "Ryan", "Nicholas", "Tyler", "Zachary",
            "Nathan", "Brandon", "Dylan", "Noah", "Justin",
            "Austin", "Logan", "Kevin", "Connor", "Christian",
            "Aidan", "Alexander", "Jonathan", "Chase", "Hunter",
            "Caleb", "Robert", "David", "Kyle", "Elijah",
            "Mason", "Jason", "Luke", "Cody", "Benjamin",
        };

        private static readonly string[] Female2003 =
        {
            "Emily", "Emma", "Madison", "Hannah", "Olivia",
            "Abigail", "Alexis", "Ashley", "Brianna", "Sarah",
            "Samantha", "Hailey", "Jessica", "Grace", "Taylor",
            "Kayla", "Elizabeth", "Alyssa", "Lauren", "Megan",
            "Sophia", "Victoria", "Jasmine", "Sydney", "Destiny",
            "Morgan", "Amber", "Chloe", "Savannah", "Claire",
            "Alexandra", "Anna", "Christina", "Mary", "Rachel",
            "Jordan", "Stephanie", "Rebecca", "Courtney", "Natalie",
        };

        public static string Generate(IEnumerable<string> usedNames, Random rng, Sex sex)
        {
            var used = new HashSet<string>(usedNames);

            // Tier 1: canon Smurf names.
            var pool = (sex == Sex.Female ? Female​Canon : MaleCanon)
                       .Where(n => !used.Contains(n)).ToArray();
            if (pool.Length > 0)
                return pool[rng.Next(pool.Length)];

            // Tier 2: 2003 baby names.
            pool = (sex == Sex.Female ? Female2003 : Male2003)
                   .Where(n => !used.Contains(n)).ToArray();
            if (pool.Length > 0)
                return pool[rng.Next(pool.Length)];

            // Tier 3: numbered fallback.
            int n = 1;
            string fallback;
            do { fallback = $"Smurf {n++}"; } while (used.Contains(fallback));
            return fallback;
        }
    }
}
