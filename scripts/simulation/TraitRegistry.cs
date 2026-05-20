using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // Static registry of the 13 canonical Homo mycelianus biological traits.
    // Penetrance 0.0 = trait fully suppressed; 1.0 = fully expressed.
    // Dawn Era penetrance ranges reflect the early colony's evolutionary state:
    // traits that require cultural development (StorkOviposition, MagicalAptitude)
    // start low; core physiological traits (ExtremeLongevity, HaemocyaninMetabolism) start high.
    // Source: Sporeholm_Entities.md §2
    public static class TraitRegistry
    {
        // Trait name constants — use these everywhere instead of raw strings.
        public const string BluePigmentation      = "BluePigmentation";
        public const string Miniaturization       = "Miniaturization";
        public const string ExtremeLongevity      = "ExtremeLongevity";
        public const string MaleSexBias           = "MaleSexBias";
        public const string StorkOviposition      = "StorkOviposition";
        public const string MagicalAptitude       = "MagicalAptitude";
        public const string CommunalBonding       = "CommunalBonding";
        public const string HaemocyaninMetabolism = "HaemocyaninMetabolism";
        public const string LowThermalTolerance   = "LowThermalTolerance";
        public const string MycophagicDependency  = "MycophagicDependency";
        public const string CognitivelyPlastic    = "CognitivelyPlastic";
        public const string StatureAgility        = "StatureAgility";
        public const string ResonanceSensitivity  = "ResonanceSensitivity";

        public record TraitDef(
            string Name,
            string Description,
            float DawnFloor,
            float DawnCeiling
        );

        // Need decay modifiers keyed by trait name.
        // Each entry is (needName, coefficient). Formula: mod = 1.0 - penetrance * coeff.
        // Positive coeff → higher penetrance reduces decay (beneficial expression).
        // Negative coeff → higher penetrance increases decay (biological cost).
        private static readonly Dictionary<string, (string Need, float Coeff)[]> _needEffects = new()
        {
            [MagicalAptitude]      = new[] { ("MagicResonance", 0.35f) },
            [CommunalBonding]      = new[] { ("Social",         0.20f) },
            [MycophagicDependency] = new[] { ("Nutrition",      0.12f) },
            [ResonanceSensitivity] = new[] { ("MagicResonance", 0.20f) },
            [LowThermalTolerance]  = new[] { ("Nutrition",     -0.18f) },
        };

        // Ordered list of all 13 traits with Dawn Era penetrance bounds.
        // Dawn Era values sourced from Sporeholm_Roadmap_2026.md §2.1 and Entities.md §2.
        public static readonly IReadOnlyList<TraitDef> All = new TraitDef[]
        {
            // Roadmap §2.1 explicitly sets haemocyanin (BluePigmentation) at 0.10–0.30 for Dawn Era.
            new(BluePigmentation,      "Haemocyanin copper circulatory pigment; copper-based oxygen transport",         0.10f, 0.30f),
            new(Miniaturization,       "Post-Great Shrinking body reduction; low caloric demand, slow construction",    0.15f, 0.40f),
            new(ExtremeLongevity,      "Telomere regulation enabling ~550-year lifespan; well established",             0.50f, 0.85f),
            new(MaleSexBias,           "49:1 male-to-female ratio; foundational reproductive biology",                  0.70f, 0.95f),
            new(StorkOviposition,      "Stork-mediated oviposition; resonance-gated; pre-pact era — very low",          0.00f, 0.10f),
            new(MagicalAptitude,       "Innate capacity to perceive and channel magical forces; developing",             0.10f, 0.45f),
            new(CommunalBonding,       "Hyper-social wiring; isolation causes rapid mood degradation",                  0.40f, 0.75f),
            new(HaemocyaninMetabolism, "Copper-based blood chemistry; incompatible with iron-rich foods",               0.60f, 0.90f),
            new(LowThermalTolerance,   "Small body mass; poor cold adaptation; elevated baseline caloric demand",       0.30f, 0.65f),
            new(MycophagicDependency,  "Diet evolved around fungal sources; mushrooms as primary nutrition",            0.50f, 0.85f),
            new(CognitivelyPlastic,    "High neuroplasticity; skill acquisition possible across full lifespan",         0.30f, 0.65f),
            new(StatureAgility,        "Small body enables high relative speed and evasion",                            0.35f, 0.70f),
            new(ResonanceSensitivity,  "Heightened sensitivity to magical fields and ley lines; rare in Dawn Era",      0.05f, 0.30f),
        };

        // Assigns all 13 traits to a shroomp using Dawn Era penetrance ranges.
        // v0.5.84t — also rolls personality-level flags (Pacifist) alongside
        // the biological-penetrance traits so every gen site picks them up
        // through the existing single-method call.
        public static void AssignDawnEraTraits(Shroomp shroomp, Random rng)
        {
            foreach (var def in All)
            {
                double t = rng.NextDouble();
                float penetrance = (float)(def.DawnFloor + t * (def.DawnCeiling - def.DawnFloor));
                shroomp.Traits[def.Name] = Math.Clamp(penetrance, 0f, 1f);
            }
            // v0.5.84t — Pacifist (~8% incidence, RimWorld NonViolent parity).
            // EquipmentSystem.AutoEquipBetterWeapon early-returns for pacifists
            // so they never pick up a weapon. Future combat (Phase 7) gates
            // drafting + violent task selection on this flag too.
            shroomp.IsPacifist = rng.NextDouble() < 0.08;
        }

        // Returns the combined trait need decay multiplier for a given need.
        // A value < 1.0 means traits collectively reduce decay; > 1.0 means they accelerate it.
        // Clamped to [0.10, 3.0] to prevent degenerate values.
        public static float GetNeedDecayMod(Shroomp s, string need)
        {
            float mod = 1f;
            foreach (var (traitName, effects) in _needEffects)
            {
                if (!s.Traits.TryGetValue(traitName, out float penetrance)) continue;
                foreach (var (effectNeed, coeff) in effects)
                {
                    if (effectNeed != need) continue;
                    mod *= 1f - penetrance * coeff;
                }
            }
            return Math.Clamp(mod, 0.10f, 3.0f);
        }
    }
}
