using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // v0.3.43 — Preferences (Dwarf-Fortress-style persistent likes and
    // dislikes).
    //
    // Where Thoughts are *temporal* — a mood reaction that decays — Preferences
    // are *permanent traits of who the shroomp is*. They drive task selection,
    // social affinity, and the thoughts emitted on completion. Every shroomp
    // rolls a small set at creation time and carries them for life:
    //
    //   • LikedItems / DislikedItems        — food, materials, future Phase 4
    //                                          procedural item kinds.
    //   • LikedActivities / DislikedActivities — task types they preferentially
    //                                          pick or avoid (Foraging,
    //                                          Excavating, Socializing, …).
    //   • LikedShroomps / DislikedShroomps       — built up at runtime from social
    //                                          encounters; not rolled on
    //                                          creation. Bounded to ~8 entries
    //                                          per list to keep lookups cheap.
    //
    // The priority engine in BehaviorSystem queries `Preferences.LikesActivity(...)`
    // and `Preferences.LikesItem(...)`; positive matches bump task priority,
    // negative matches penalise. Sensible defaults (everyone's neutral about
    // most things) keep this from dominating the existing logic — the
    // preferences just *colour* otherwise-equal choices.
    public sealed class Preferences
    {
        // Persistent, rolled-at-creation.
        public List<string> LikedItems        { get; set; } = new();
        public List<string> DislikedItems     { get; set; } = new();
        public List<string> LikedActivities   { get; set; } = new();
        public List<string> DislikedActivities{ get; set; } = new();

        // Runtime-mutable, built from social events.
        public List<string> LikedShroomps       { get; set; } = new();
        public List<string> DislikedShroomps    { get; set; } = new();

        // Cap social lists so they don't grow unbounded over an Elder's lifetime.
        // Older entries fall off when new ones push past this size.
        public const int SocialCap = 8;

        public bool LikesItem(string item)        => item != null && LikedItems   .Contains(item);
        public bool DislikesItem(string item)     => item != null && DislikedItems.Contains(item);
        public bool LikesActivity(string act)     => act  != null && LikedActivities   .Contains(act);
        public bool DislikesActivity(string act)  => act  != null && DislikedActivities.Contains(act);
        public bool LikesShroomp(string name)       => name != null && LikedShroomps   .Contains(name);
        public bool DislikesShroomp(string name)    => name != null && DislikedShroomps.Contains(name);

        public void Befriend(string name)
        {
            if (name == null || LikedShroomps.Contains(name)) return;
            DislikedShroomps.Remove(name);
            LikedShroomps.Add(name);
            while (LikedShroomps.Count > SocialCap) LikedShroomps.RemoveAt(0);
        }

        public void Sour(string name)
        {
            if (name == null || DislikedShroomps.Contains(name)) return;
            LikedShroomps.Remove(name);
            DislikedShroomps.Add(name);
            while (DislikedShroomps.Count > SocialCap) DislikedShroomps.RemoveAt(0);
        }
    }

    public static class PreferenceRegistry
    {
        // Item names that can appear in Liked/Disliked at creation. Mirrors
        // the Phase 4 procedural-item categories plus the existing Phase 3
        // vegetation yields so the lists are meaningful today (Capberry,
        // Pineshroom, …) and stay meaningful as Phase 4 introduces material
        // / quality variation. Keep this list small at first — adding more
        // entries here is free, but breaking values that already appear in
        // a savefile would corrupt continuity, so prefer additive edits.
        // v0.4.2 — aligned with the new item taxonomy. Food sub-types
        // are the four real ones (Capberry / SmallMushroom /
        // HerbCluster / MagicBerry); material entries cover the three
        // wood + the seven stone variants that the player actually
        // sees in the HUD. Removed Pineshroom / PalmShroom / Oak / Pine
        // / Willow / FieldStone — they don't exist in the v0.4.2
        // registry and would never trigger a preference match.
        public static readonly string[] ItemPool =
        {
            "Capberry", "SmallMushroom", "HerbCluster", "MagicBerry",
            "DeadWood", "LivingWood", "Fungal",
            "Granite", "Limestone", "Marble", "Obsidian", "Quartz", "Magicstone",
        };

        public static readonly string[] ActivityPool =
        {
            "Foraging", "Excavating", "ChoppingWood", "CuttingPlants",
            "Socializing", "Meditating", "Wandering", "Observing",
        };

        // Assigns 1–3 liked + 0–2 disliked items, 1–2 liked + 0–1 disliked
        // activities, weighted by personality. Introverts skew toward
        // disliking Socializing; Greedy-Guts skew toward liking foods;
        // Mages and Daydreamers skew toward Meditating / Observing.
        // Defaults are gentle: most shroomps will roll close to neutral.
        public static Preferences Assign(Random rng, IReadOnlyList<string> personality)
        {
            var p = new Preferences();

            // Items
            int likeItems    = rng.Next(1, 4);   // 1–3
            int dislikeItems = rng.Next(0, 3);   // 0–2
            FillUnique(p.LikedItems,    ItemPool, likeItems,    rng, Array.Empty<string>());
            FillUnique(p.DislikedItems, ItemPool, dislikeItems, rng, p.LikedItems);

            // Activities
            int likeAct    = rng.Next(1, 3);     // 1–2
            int dislikeAct = rng.Next(0, 2);     // 0–1
            FillUnique(p.LikedActivities,    ActivityPool, likeAct,    rng, Array.Empty<string>());
            FillUnique(p.DislikedActivities, ActivityPool, dislikeAct, rng, p.LikedActivities);

            // Personality nudges — gentle, one-shot tweaks after the random
            // baseline so a Mage usually but not always likes Meditating.
            if (personality != null)
            {
                foreach (var trait in personality)
                {
                    ApplyPersonalityNudge(p, trait);
                }
            }

            return p;
        }

        private static void FillUnique(List<string> dest, string[] pool, int count,
            Random rng, IReadOnlyList<string> exclude)
        {
            int safety = pool.Length * 3;
            while (dest.Count < count && safety-- > 0)
            {
                string pick = pool[rng.Next(pool.Length)];
                if (dest.Contains(pick)) continue;
                bool excluded = false;
                for (int i = 0; i < exclude.Count; i++)
                    if (exclude[i] == pick) { excluded = true; break; }
                if (excluded) continue;
                dest.Add(pick);
            }
        }

        private static void ApplyPersonalityNudge(Preferences p, string trait)
        {
            switch (trait)
            {
                case "Introvert":
                    if (!p.DislikedActivities.Contains("Socializing")) p.DislikedActivities.Add("Socializing");
                    if (!p.LikedActivities.Contains("Observing"))      p.LikedActivities.Add("Observing");
                    break;
                case "Gossip":
                    if (!p.LikedActivities.Contains("Socializing"))    p.LikedActivities.Add("Socializing");
                    p.DislikedActivities.Remove("Socializing");
                    break;
                case "Daydreamer":
                    if (!p.LikedActivities.Contains("Observing"))      p.LikedActivities.Add("Observing");
                    break;
                case "Greedy Gut":
                    if (!p.LikedActivities.Contains("Foraging"))       p.LikedActivities.Add("Foraging");
                    if (!p.LikedItems.Contains("Capberry"))          p.LikedItems.Add("Capberry");
                    break;
                case "Mushroom Whisperer":
                    // v0.4.2 — SmallMushroom is the only food sub-type
                    // remaining for mushrooms; the Large variant rolls
                    // through the material axis. Liking SmallMushroom
                    // covers every mushroom the shroomp encounters.
                    if (!p.LikedItems.Contains("SmallMushroom"))       p.LikedItems.Add("SmallMushroom");
                    if (!p.LikedItems.Contains("Fungal"))              p.LikedItems.Add("Fungal");
                    break;
                case "Brawny":
                    if (!p.LikedActivities.Contains("ChoppingWood"))   p.LikedActivities.Add("ChoppingWood");
                    if (!p.LikedActivities.Contains("Excavating"))     p.LikedActivities.Add("Excavating");
                    break;
                case "Sleepyhead":
                    if (!p.DislikedActivities.Contains("ChoppingWood"))p.DislikedActivities.Add("ChoppingWood");
                    if (!p.DislikedActivities.Contains("Excavating"))  p.DislikedActivities.Add("Excavating");
                    break;
                case "Thrill-Seeker":
                    if (!p.LikedActivities.Contains("Wandering"))      p.LikedActivities.Add("Wandering");
                    break;
            }
        }

        // Cross-walk: which item/activity name corresponds to a given task type.
        // Used by BehaviorSystem to apply preference bumps on the same axis
        // the preference is stored against.
        public static string? ActivityNameFor(TaskType t) => t switch
        {
            TaskType.GatherFood     => "Foraging",
            TaskType.GatherMaterial => "Excavating",
            TaskType.ChopWood       => "ChoppingWood",
            TaskType.CutVegetation  => "CuttingPlants",
            TaskType.Socialize      => "Socializing",
            TaskType.Converse       => "Socializing",
            TaskType.Attune         => "Meditating",
            TaskType.Meditate       => "Meditating",
            TaskType.Wander         => "Wandering",
            TaskType.Loiter         => "Wandering",
            TaskType.Observe        => "Observing",
            TaskType.VisitFavorite  => "Wandering",
            _ => null,
        };
    }
}
