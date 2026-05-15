using System.Collections.Generic;

namespace SmurfulationC.Simulation
{
    public static class BodyPartRegistry
    {
        public record BodyPartDef(string Name, string Parent, bool Vital);

        // Ordered template for a standard Smurf body. Parent == "" means root.
        public static readonly BodyPartDef[] Template =
        {
            // Head
            new("Head",       "",        true),
            new("Brain",      "Head",    true),
            new("Left Eye",   "Head",    false),
            new("Right Eye",  "Head",    false),
            new("Nose",       "Head",    false),
            new("Jaw",        "Head",    false),

            // Torso
            new("Torso",      "",        true),
            new("Heart",      "Torso",   true),
            new("Left Lung",  "Torso",   false),
            new("Right Lung", "Torso",   false),
            new("Liver",      "Torso",   true),
            new("Stomach",    "Torso",   false),

            // Arms
            new("Left Arm",   "Torso",   false),
            new("Left Hand",  "Left Arm",false),
            new("Right Arm",  "Torso",   false),
            new("Right Hand", "Right Arm",false),

            // Legs
            new("Left Leg",   "Torso",   false),
            new("Left Foot",  "Left Leg",false),
            new("Right Leg",  "Torso",   false),
            new("Right Foot", "Right Leg",false),
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
    }
}
