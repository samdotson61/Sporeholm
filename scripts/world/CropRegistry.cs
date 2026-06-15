using System.Collections.Generic;
using Sporeholm.Simulation.Items;

namespace Sporeholm.World
{
    // v0.8.0 (Phase 8) — Botany-skill tier for crops. Gates which crops a colony
    // can plant: the Farm picker offers crops up to the colony's best Botany.
    public enum CropTier { Simple = 0, Medium = 1, Hard = 2 }

    // v0.8.0 (Phase 8) — data-driven crop definition. Adding a crop is one entry
    // here (+ its yield/seed item in ItemRegistry); the sow/grow/harvest loop is
    // generic and reads these fields, so no behaviour code changes per crop.
    public sealed class CropDef
    {
        public CropType Type            { get; init; }
        public string   DisplayName     { get; init; } = "";
        public string   Icon            { get; init; } = "🌱";
        public CropTier Tier            { get; init; } = CropTier.Simple;
        public int      BotanyMin       { get; init; } = 0;
        // Sim-ticks to advance one growth stage. Growth is AUTONOMOUS + flat
        // (LocalMap.TickCrops adds 1 tick/sim-tick; Botany + fertility gate sow
        // eligibility + harvest YIELD, not the rate). A crop reaches Ripe after
        // 4 stage-steps, so total grow time = GrowTicksPerStage × 4. The clock is
        // 60,000 ticks per in-game DAY (TicksPerHour 2,500 × 24), so a crop ripens
        // in (GrowTicksPerStage × 4 / 60,000) in-game days. v0.8.4 tuned every crop
        // to its design-spec day target (2–12 days) — i.e. GrowTicksPerStage =
        // targetDays × 15,000 — instead of the old sub-hour values that let a plot
        // out-yield its own shelf life many times per hour. Default = 4 days.
        public int      GrowTicksPerStage { get; init; } = 60000;   // 4 in-game days (× 4 stages = 240k ticks)
        // Harvest output: an ItemRegistry Kind + SubType + a count range (rolled,
        // then scaled by BotanyYieldFactor and the above/underground multiplier).
        public ItemKind YieldItemKind    { get; init; } = ItemKind.Food;
        public string   YieldItemSubType { get; init; } = "";
        public int      YieldMin         { get; init; } = 1;
        public int      YieldMax         { get; init; } = 3;
        // v0.8.0 stub: the above/underground emphasis. Fungal crops favour caves.
        public float    AboveGroundYieldMul { get; init; } = 1.0f;
        public float    UndergroundYieldMul { get; init; } = 1.0f;
        // Cave-exclusive crops (Cave Moss) can only be planted on roofed tiles.
        public bool     UndergroundOnly  { get; init; } = false;
        // v0.8.0 stub — season window bitmask (bit per season). 0xF = all seasons.
        // Seasonal failure is enforced from v0.8.3 once weather (Phase 10) lands.
        public int      SeasonMask       { get; init; } = 0xF;
    }

    public static class CropRegistry
    {
        public static readonly CropDef[] All =
        {
            // ── Simple (Botany 0) ───────────────────────────────────────────
            new() { Type = CropType.SmallMushroom, DisplayName = "Small Mushroom", Icon = "🍄",
                Tier = CropTier.Simple, BotanyMin = 0, GrowTicksPerStage = 30000,   // 2 in-game days
                YieldItemSubType = "SmallMushroom", YieldMin = 2, YieldMax = 4,
                AboveGroundYieldMul = 0.5f, UndergroundYieldMul = 1.5f },
            new() { Type = CropType.CaveMoss, DisplayName = "Cave Moss", Icon = "🟢",
                Tier = CropTier.Simple, BotanyMin = 0, GrowTicksPerStage = 30000,   // 2 in-game days
                YieldItemKind = ItemKind.Material, YieldItemSubType = "Mosslet", YieldMin = 2, YieldMax = 3,
                AboveGroundYieldMul = 0.5f, UndergroundYieldMul = 1.5f, UndergroundOnly = true },
            new() { Type = CropType.SpringGreens, DisplayName = "Spring Greens", Icon = "🥬",
                Tier = CropTier.Simple, BotanyMin = 0, GrowTicksPerStage = 30000,   // 2 in-game days
                YieldItemSubType = "SpringGreens", YieldMin = 2, YieldMax = 3 },
            // ── Medium (Botany 3-5) ─────────────────────────────────────────
            new() { Type = CropType.Capberry, DisplayName = "Capberry", Icon = "🫐",
                Tier = CropTier.Medium, BotanyMin = 3, GrowTicksPerStage = 75000,   // 5 in-game days
                YieldItemSubType = "Capberry", YieldMin = 3, YieldMax = 5 },
            new() { Type = CropType.Sunberry, DisplayName = "Sunberry", Icon = "🟠",
                Tier = CropTier.Medium, BotanyMin = 4, GrowTicksPerStage = 90000,   // 6 in-game days
                YieldItemSubType = "Sunberry", YieldMin = 3, YieldMax = 5 },
            new() { Type = CropType.Pumpkin, DisplayName = "Pumpkin", Icon = "🎃",
                Tier = CropTier.Medium, BotanyMin = 5, GrowTicksPerStage = 105000,   // 7 in-game days
                YieldItemSubType = "Pumpkin", YieldMin = 2, YieldMax = 4 },
            // ── Hard (Botany 6-9) ───────────────────────────────────────────
            new() { Type = CropType.MagicHerb, DisplayName = "Magic Herb", Icon = "🌿",
                Tier = CropTier.Hard, BotanyMin = 6, GrowTicksPerStage = 120000,   // 8 in-game days
                YieldItemSubType = "HerbCluster", YieldMin = 2, YieldMax = 3 },
            new() { Type = CropType.LargeMushroom, DisplayName = "Large Mushroom", Icon = "🍄",
                Tier = CropTier.Hard, BotanyMin = 7, GrowTicksPerStage = 180000,   // 12 in-game days
                YieldItemKind = ItemKind.Material, YieldItemSubType = "WoodLog", YieldMin = 2, YieldMax = 3,
                AboveGroundYieldMul = 0.5f, UndergroundYieldMul = 1.5f },
            new() { Type = CropType.MagicFlower, DisplayName = "Magic Flower", Icon = "🌺",
                Tier = CropTier.Hard, BotanyMin = 9, GrowTicksPerStage = 180000,   // 12 in-game days
                YieldItemSubType = "MagicBerry", YieldMin = 1, YieldMax = 2 },
        };

        private static readonly Dictionary<CropType, CropDef> _byType = Build();
        private static Dictionary<CropType, CropDef> Build()
        {
            var d = new Dictionary<CropType, CropDef>();
            foreach (var c in All) d[c.Type] = c;
            return d;
        }

        public static CropDef? Get(CropType t) => _byType.TryGetValue(t, out var c) ? c : null;

        public static CropTier TierForBotany(int botany) =>
            botany >= 6 ? CropTier.Hard : botany >= 3 ? CropTier.Medium : CropTier.Simple;

        // Can a colonist with this Botany level plant the given crop?
        public static bool CanPlant(CropType t, int botany)
        {
            var def = Get(t);
            return def != null && botany >= def.BotanyMin;
        }
    }
}
