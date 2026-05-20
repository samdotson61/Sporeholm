using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    public static class BodyPartRegistry
    {
        public record BodyPartDef(string Name, string Parent, bool Vital);

        // Ordered template for a standard Shroomp body.
        // v0.5.84q — mushroom-themed rename pass. Sam: "Head → Cap,
        // Torso → Stalk, Lungs → Gills, etc." Six anatomical renames
        // (Head → Cap, Nose → Spore Vent, Torso → Stalk, Left/Right Lung
        // → Left/Right Gill, Liver → Filter). Brain, Eyes, Jaw, Heart,
        // Stomach, Arms / Hands / Legs / Feet kept as-is for readability
        // — anthropomorphic shroomp has visible eyes / mouth / limbs in
        // the sprite, and "Brain / Heart" are too crucial to alienate
        // a new player. Parent == "" means root. LegacyNameMigration
        // below maps the old keys at save-load time so existing colonies
        // don't lose their body-part condition values.
        public static readonly BodyPartDef[] Template =
        {
            // Cap (head-region)
            new("Cap",         "",        true),
            new("Brain",       "Cap",     true),
            new("Left Eye",    "Cap",     false),
            new("Right Eye",   "Cap",     false),
            new("Spore Vent",  "Cap",     false),
            new("Jaw",         "Cap",     false),

            // Stalk (torso-region)
            new("Stalk",       "",        true),
            new("Heart",       "Stalk",   true),
            new("Left Gill",   "Stalk",   false),
            new("Right Gill",  "Stalk",   false),
            new("Filter",      "Stalk",   true),
            new("Stomach",     "Stalk",   false),

            // Arms (shroomp anthropomorphic limbs)
            new("Left Arm",    "Stalk",   false),
            new("Left Hand",   "Left Arm",false),
            new("Right Arm",   "Stalk",   false),
            new("Right Hand",  "Right Arm",false),

            // Legs
            new("Left Leg",    "Stalk",   false),
            new("Left Foot",   "Left Leg",false),
            new("Right Leg",   "Stalk",   false),
            new("Right Foot",  "Right Leg",false),
        };

        // v0.5.84q — save-migration map. Old colonies' BodyParts dicts
        // are keyed by the pre-rename strings. On Shroomp load we walk
        // the loaded dict and rename matching keys to the new template
        // names. Zero ongoing overhead — only iterates the keys that
        // exist in the dict, and only on the first load of an old save.
        // New games skip this entirely (the registry already creates
        // the body with new names via CreateHealthy).
        public static readonly Dictionary<string, string> LegacyNameMigration = new()
        {
            { "Head",       "Cap" },
            { "Nose",       "Spore Vent" },
            { "Torso",      "Stalk" },
            { "Left Lung",  "Left Gill" },
            { "Right Lung", "Right Gill" },
            { "Liver",      "Filter" },
        };

        // Creates a full healthy body (all parts at 100 %).
        public static Dictionary<string, float> CreateHealthy()
        {
            var parts = new Dictionary<string, float>();
            foreach (var def in Template)
                parts[def.Name] = 100f;
            return parts;
        }

        public static BodyPartDef? Get(string name)
        {
            foreach (var def in Template)
                if (def.Name == name) return def;
            return null;
        }

        // v0.5.84q — apply the legacy-rename migration to an in-memory
        // BodyParts dict. Returns the same dict (mutated) so the caller
        // can chain. Safe to call repeatedly — it's a no-op once the
        // dict has new names.
        public static Dictionary<string, float> MigrateLegacyNames(Dictionary<string, float> parts)
        {
            foreach (var (oldName, newName) in LegacyNameMigration)
            {
                if (parts.TryGetValue(oldName, out var v) && !parts.ContainsKey(newName))
                {
                    parts[newName] = v;
                    parts.Remove(oldName);
                }
            }
            return parts;
        }
    }
}
