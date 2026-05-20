using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // Static registry of the 13 canonical Shroomp biological traits.
    // Penetrance 0.0 = trait fully suppressed; 1.0 = fully expressed.
    //
    // v0.5.84t — renamed from the v0.4.x scientific-Latin set
    // (BluePigmentation / Miniaturization / HaemocyaninMetabolism / etc.)
    // to the mushroom-themed set below. Sam: "rework the biological
    // traits to match the mushroom aesthetic — this will be flavor only
    // for now, though ensure they do what they say on the tin." Active
    // traits (the 7 that drive gameplay effects) carry names that hint
    // at their mechanical effect:
    //   MyceliumAttuned / SporeResonant  → MagicResonance decays slower
    //   ClusterFruiting                  → Social decays slower
    //   EfficientGills                   → Nutrition decays slower
    //   RapidMetabolism                  → Nutrition decays FASTER (cost)
    //   CompactStature                   → Carry capacity reduced
    //   WispyFrame                       → Carry capacity reduced (lighter penalty)
    // The other 6 are flavor only — they sit in the dict but no system
    // reads them yet. Future phases (combat, magic, breeding) will hook
    // CopperHemolymph, RareFemales, StorkBorne, etc. as needed.
    //
    // Save-compat: pre-v0.5.84t saves carry the old key names in
    // Shroomp.Traits. SimulationManager's load path calls
    // MigrateLegacyTraitNames before reading, which renames the old keys
    // to the new ones so the need-decay system still finds the right
    // penetrance values.
    public static class TraitRegistry
    {
        // ── Trait name constants (use these everywhere) ─────────────────
        // Active traits — names describe their gameplay effect.
        public const string MyceliumAttuned   = "MyceliumAttuned";   // MagicResonance ↓ decay
        public const string ClusterFruiting   = "ClusterFruiting";   // Social ↓ decay
        public const string EfficientGills    = "EfficientGills";    // Nutrition ↓ decay
        public const string RapidMetabolism   = "RapidMetabolism";   // Nutrition ↑ decay (cost)
        public const string SporeResonant     = "SporeResonant";     // MagicResonance ↓ decay (secondary)
        public const string CompactStature    = "CompactStature";    // Carry capacity ↓ (-15 × p)
        public const string WispyFrame        = "WispyFrame";        // Carry capacity ↓ (-5 × p)
        // Flavor-only traits — no gameplay system reads them today.
        public const string BlueCap           = "BlueCap";
        public const string PerennialMycelium = "PerennialMycelium";
        public const string RareFemales       = "RareFemales";
        public const string StorkBorne        = "StorkBorne";
        public const string CopperHemolymph   = "CopperHemolymph";
        public const string PlasticHyphae     = "PlasticHyphae";

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
            [MyceliumAttuned] = new[] { ("MagicResonance", 0.35f) },
            [ClusterFruiting] = new[] { ("Social",         0.20f) },
            [EfficientGills]  = new[] { ("Nutrition",      0.12f) },
            [SporeResonant]   = new[] { ("MagicResonance", 0.20f) },
            [RapidMetabolism] = new[] { ("Nutrition",     -0.18f) },
        };

        // Ordered list of all 13 traits with Dawn Era penetrance bounds.
        public static readonly IReadOnlyList<TraitDef> All = new TraitDef[]
        {
            // ── Active traits (effects wired) ────────────────────────────
            new(MyceliumAttuned,
                "Attuned to the underground mycelial network — channels magic with less drain on personal reserves.",
                0.10f, 0.45f),
            new(ClusterFruiting,
                "Thrives in mushroom clusters — social need decays slowly when around colony-mates.",
                0.40f, 0.75f),
            new(EfficientGills,
                "Gills absorb more nutrition from fungal food — hunger decays slowly.",
                0.50f, 0.85f),
            new(RapidMetabolism,
                "Spore production burns through reserves fast — hunger decays faster than peers. Biological cost.",
                0.30f, 0.65f),
            new(SporeResonant,
                "Body resonates with ambient magical fields — secondary magic-resonance buffer.",
                0.05f, 0.30f),
            new(CompactStature,
                "Small truffle-like build — light and unobtrusive but carries less weight per trip.",
                0.15f, 0.40f),
            new(WispyFrame,
                "Slim, springy hyphae — agile and quick on the foot but a weaker frame for hauling.",
                0.35f, 0.70f),
            // ── Flavor-only traits (no system reads them yet) ───────────
            new(BlueCap,
                "Cap pigmented blue-violet by copper-rich substrate. Distinctive at a glance.",
                0.10f, 0.30f),
            new(PerennialMycelium,
                "Underground network is long-lived; this shroomp's roots run deep. ~550-year lifespan.",
                0.50f, 0.85f),
            new(RareFemales,
                "Carries the lineage trait behind the 49:1 male-to-female spore ratio.",
                0.70f, 0.95f),
            new(StorkBorne,
                "Born of stork-mediated spore dispersal. Pre-pact lineage; very low in Dawn Era.",
                0.00f, 0.10f),
            new(CopperHemolymph,
                "Copper-based blood chemistry — incompatible with iron-rich foods. Future combat hediff source.",
                0.60f, 0.90f),
            new(PlasticHyphae,
                "Hyphae remain neurologically plastic — skill acquisition possible across full lifespan.",
                0.30f, 0.65f),
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

        // v0.5.84t — legacy trait-name migration. Pre-v0.5.84t saves carry
        // the old scientific-Latin trait keys (MagicalAptitude etc.). Map
        // them forward to the new mushroom-themed keys so the need-decay
        // system + carry-capacity computation still find the right
        // penetrance values. Called by SimulationManager.LoadFromSave
        // right after restoring Traits.
        private static readonly Dictionary<string, string> _legacyKeyMap = new()
        {
            ["MagicalAptitude"]       = MyceliumAttuned,
            ["CommunalBonding"]       = ClusterFruiting,
            ["MycophagicDependency"]  = EfficientGills,
            ["LowThermalTolerance"]   = RapidMetabolism,
            ["ResonanceSensitivity"]  = SporeResonant,
            ["Miniaturization"]       = CompactStature,
            ["StatureAgility"]        = WispyFrame,
            ["BluePigmentation"]      = BlueCap,
            ["ExtremeLongevity"]      = PerennialMycelium,
            ["MaleSexBias"]           = RareFemales,
            ["StorkOviposition"]      = StorkBorne,
            ["HaemocyaninMetabolism"] = CopperHemolymph,
            ["CognitivelyPlastic"]    = PlasticHyphae,
        };

        public static void MigrateLegacyTraitNames(Shroomp s)
        {
            if (s?.Traits == null || s.Traits.Count == 0) return;
            // Walk a snapshot of the keys so we can mutate the dict mid-loop.
            var keys = new List<string>(s.Traits.Keys);
            foreach (var oldKey in keys)
            {
                if (!_legacyKeyMap.TryGetValue(oldKey, out var newKey)) continue;
                if (s.Traits.ContainsKey(newKey)) continue;   // already migrated
                s.Traits[newKey] = s.Traits[oldKey];
                s.Traits.Remove(oldKey);
            }
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
