using System;
using System.Collections.Generic;

namespace SmurfulationC.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — material identity for an item. DF-style:
    // "Wood/Oak", "Stone/Granite", "Plant/Smurfberry", "Cloth/Linen".
    // Stored as a struct so equality / hashing is cheap and items can
    // stack in the inventory when their MaterialKey matches.
    //
    // Family identifies the broad material class (Wood, Stone, Cloth,
    // Plant, Magic, Bone, Hide, Metal). SubType is the specific variety
    // within the family (Oak, Granite, Linen, Smurfberry, etc.). Most
    // gameplay code branches on Family; UI / preference lookups use
    // SubType strings exclusively.
    public readonly record struct MaterialKey(string Family, string SubType)
    {
        public override string ToString() => $"{Family}/{SubType}";
    }

    // Per-material multipliers + display data. Looked up by MaterialKey
    // in MaterialRegistry. The Phase 4 deterioration system uses
    // DecayRateMul on every daily tick; the value system uses ValueMul;
    // procedural visuals use Tint.
    public sealed class MaterialDef
    {
        public MaterialKey Key             { get; init; }
        public string      DisplayName     { get; init; } = "";
        public string      Icon            { get; init; } = "";
        public float       DurabilityMul   { get; init; } = 1.0f;
        public float       DecayRateMul    { get; init; } = 1.0f;
        public float       ValueMul        { get; init; } = 1.0f;
        // Used by future visual tinting; stored as RGBA float quartet
        // packed into a single uint so MaterialDef stays a tiny struct
        // when boxed for transport.
        public uint        TintRgba        { get; init; } = 0xFFFFFFFF;
    }

    public static class MaterialRegistry
    {
        // Initial catalogue — every material currently produceable by the
        // game's tasks, plus a handful of Phase 5+ entries that the
        // ItemFactory can already roll against (so a Scholar's "Bronze
        // sextant" trinket can land in a scenario reroll without waiting
        // for Phase 5 to define Bronze formally). The DurabilityMul /
        // DecayRateMul values mirror the spec in roadmap §4 → "Material"
        // line: Magicwood × 0.5 decay, Cloth × 1.2, Food × 4.0, etc.
        // v0.4.2 — taxonomy aligned with the miniaturised Smurf theme.
        //   Wood: DeadWood (from DeadLog terrain), LivingWood (from LivingWood
        //         terrain), Fungal (from any LargeMushroom variant
        //         — LargeMushroom, LargeSandshroom, PalmShroom).
        //         Removed real-world species (Oak / Pine / Willow / Palm)
        //         and the unused Magicwood. Smurfs harvest the world they
        //         actually live in: dead logs, fresh-cut living timber,
        //         and the mushroom caps the canon describes.
        //   Stone: Granite / Limestone / Marble / Obsidian / Quartz +
        //          Magicstone + MagicCrystal (rare ore vein). Removed the
        //          generic "Field Stone" — every Boulder now carries a
        //          specific stone subtype assigned at generation time.
        //   Plant materials: covers food sub-types (Smurfberry, Small
        //          Mushroom, Magic Berry, etc.). MagicFlower replaced by
        //          MagicBerry per the player brief — a magic plant that
        //          yields both food AND essence.
        public static readonly MaterialDef[] All =
        {
            // ── Wood ────────────────────────────────────────────────────
            new() { Key = new("Wood","DeadWood"),    DisplayName = "Dead Wood",    Icon = "🪵", DurabilityMul = 0.95f, DecayRateMul = 1.10f, ValueMul = 0.85f },
            new() { Key = new("Wood","LivingWood"),  DisplayName = "Living Wood",  Icon = "🌿", DurabilityMul = 1.15f, DecayRateMul = 0.90f, ValueMul = 1.20f },
            new() { Key = new("Wood","Fungal"),      DisplayName = "Fungal Wood",  Icon = "🍄", DurabilityMul = 1.00f, DecayRateMul = 1.00f, ValueMul = 1.00f },

            // ── Stone ───────────────────────────────────────────────────
            new() { Key = new("Stone","Granite"),     DisplayName = "Granite",      Icon = "🪨", DurabilityMul = 1.30f, DecayRateMul = 0.50f, ValueMul = 1.20f },
            new() { Key = new("Stone","Limestone"),   DisplayName = "Limestone",    Icon = "◻", DurabilityMul = 1.00f, DecayRateMul = 0.70f, ValueMul = 0.90f },
            new() { Key = new("Stone","Marble"),      DisplayName = "Marble",       Icon = "◼", DurabilityMul = 1.10f, DecayRateMul = 0.60f, ValueMul = 1.80f },
            new() { Key = new("Stone","Obsidian"),    DisplayName = "Obsidian",     Icon = "⬛", DurabilityMul = 0.80f, DecayRateMul = 0.30f, ValueMul = 1.50f },
            new() { Key = new("Stone","Quartz"),      DisplayName = "Quartz",       Icon = "◇", DurabilityMul = 1.05f, DecayRateMul = 0.40f, ValueMul = 1.40f },
            new() { Key = new("Stone","Magicstone"),  DisplayName = "Magic Stone",  Icon = "✨", DurabilityMul = 1.60f, DecayRateMul = 0.30f, ValueMul = 3.00f },
            new() { Key = new("Stone","MagicCrystal"),DisplayName = "Magic Crystal",Icon = "💎", DurabilityMul = 1.80f, DecayRateMul = 0.20f, ValueMul = 4.50f },

            // ── Plant / food materials ──────────────────────────────────
            // Food deterioration: × 4.0 baseline per the roadmap spec.
            // Quality keeps mattering as a separate axis (Fine berries decay
            // at the same rate as Crude ones, just buy more nutrition).
            new() { Key = new("Plant","Smurfberry"),     DisplayName = "Smurfberry",     Icon = "🫐", DurabilityMul = 0.30f, DecayRateMul = 4.00f, ValueMul = 1.00f },
            new() { Key = new("Plant","SmallMushroom"),  DisplayName = "Small Mushroom", Icon = "🍄", DurabilityMul = 0.35f, DecayRateMul = 3.50f, ValueMul = 0.85f },
            new() { Key = new("Plant","LargeMushroom"),  DisplayName = "Large Mushroom", Icon = "🍄", DurabilityMul = 0.40f, DecayRateMul = 3.20f, ValueMul = 1.40f },
            new() { Key = new("Plant","HerbCluster"),    DisplayName = "Herb Cluster",   Icon = "🌿", DurabilityMul = 0.30f, DecayRateMul = 4.50f, ValueMul = 1.10f },
            new() { Key = new("Plant","MagicBerry"),     DisplayName = "Magic Berry",    Icon = "🌺", DurabilityMul = 0.40f, DecayRateMul = 3.00f, ValueMul = 2.00f },
            new() { Key = new("Plant","Cuttings"),       DisplayName = "Cuttings",       Icon = "🌱", DurabilityMul = 0.25f, DecayRateMul = 5.00f, ValueMul = 0.20f },

            // ── Cloth (Phase 5+ — registered now for procedural rolls) ──
            new() { Key = new("Cloth","Linen"),       DisplayName = "Linen",      Icon = "🧵", DurabilityMul = 0.65f, DecayRateMul = 1.20f, ValueMul = 0.85f },
            new() { Key = new("Cloth","Smurfwool"),   DisplayName = "Smurfwool",  Icon = "🧶", DurabilityMul = 0.75f, DecayRateMul = 1.10f, ValueMul = 1.00f },
            new() { Key = new("Cloth","SpiderSilk"),  DisplayName = "Spider Silk",Icon = "🕸",  DurabilityMul = 0.85f, DecayRateMul = 0.90f, ValueMul = 1.80f },
            new() { Key = new("Cloth","Magicweave"),  DisplayName = "Magicweave", Icon = "✨", DurabilityMul = 1.10f, DecayRateMul = 0.50f, ValueMul = 3.00f },

            // ── Magic ───────────────────────────────────────────────────
            new() { Key = new("Magic","RawEssence"),   DisplayName = "Raw Essence",   Icon = "✨", DurabilityMul = 0.50f, DecayRateMul = 1.50f, ValueMul = 1.50f },
            new() { Key = new("Magic","CrystalShard"), DisplayName = "Crystal Shard", Icon = "💎", DurabilityMul = 1.50f, DecayRateMul = 0.40f, ValueMul = 2.50f },

            // ── Misc placeholders (Phase 7+ / Phase 9+) ────────────────
            new() { Key = new("Bone","Generic"),  DisplayName = "Bone",  Icon = "🦴", DurabilityMul = 1.00f, DecayRateMul = 0.80f, ValueMul = 0.80f },
            new() { Key = new("Hide","Generic"),  DisplayName = "Hide",  Icon = "🟫", DurabilityMul = 0.70f, DecayRateMul = 1.30f, ValueMul = 0.90f },
            new() { Key = new("Metal","Iron"),    DisplayName = "Iron",  Icon = "⚙", DurabilityMul = 1.80f, DecayRateMul = 0.70f, ValueMul = 1.80f },
            new() { Key = new("Metal","Bronze"),  DisplayName = "Bronze",Icon = "⚙", DurabilityMul = 1.50f, DecayRateMul = 0.60f, ValueMul = 1.50f },
        };

        private static readonly Dictionary<MaterialKey, MaterialDef> _byKey;
        private static readonly Dictionary<string, List<MaterialDef>> _byFamily;

        static MaterialRegistry()
        {
            _byKey    = new Dictionary<MaterialKey, MaterialDef>(All.Length);
            _byFamily = new Dictionary<string, List<MaterialDef>>(16);
            foreach (var def in All)
            {
                _byKey[def.Key] = def;
                if (!_byFamily.TryGetValue(def.Key.Family, out var list))
                    _byFamily[def.Key.Family] = list = new List<MaterialDef>();
                list.Add(def);
            }
        }

        public static MaterialDef? Get(MaterialKey k) =>
            _byKey.TryGetValue(k, out var def) ? def : null;

        public static IReadOnlyList<MaterialDef> InFamily(string family) =>
            _byFamily.TryGetValue(family, out var list) ? list : System.Array.Empty<MaterialDef>();

        // Picks a random material in the given family weighted by 1/ValueMul
        // (cheap materials are more common). Used by the gathering tasks
        // when they need to roll a sub-material for Stone / Wood without
        // depending on biome data. Returns null only if the family is empty.
        public static MaterialDef? RollInFamily(string family, Random rng)
        {
            var list = InFamily(family);
            if (list.Count == 0) return null;
            double totalWeight = 0;
            foreach (var d in list) totalWeight += 1.0 / System.Math.Max(d.ValueMul, 0.1f);
            double roll = rng.NextDouble() * totalWeight;
            foreach (var d in list)
            {
                roll -= 1.0 / System.Math.Max(d.ValueMul, 0.1f);
                if (roll <= 0) return d;
            }
            return list[list.Count - 1];
        }
    }
}
