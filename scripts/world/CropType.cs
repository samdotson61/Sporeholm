namespace Sporeholm.World
{
    // v0.8.0 (Phase 8 — Agricultural) — sown crop species. Distinct from the
    // wild VegetationType enum: crops are player-planted on grow-zone tiles and
    // run a sow → grow → harvest lifecycle (CropSlot), gated by the Botany skill
    // tier. Fungal crops (SmallMushroom, LargeMushroom, CaveMoss) yield more
    // underground (roofed cave tiles) than above ground — the strategic
    // mushroom-farming path; every other crop is the reverse.
    public enum CropType
    {
        None = 0,
        // Simple tier (Botany 0)
        SmallMushroom,
        CaveMoss,
        SpringGreens,
        // Medium tier (Botany 3-5)
        Capberry,
        Sunberry,
        Pumpkin,
        // Hard tier (Botany 6-9)
        MagicHerb,
        LargeMushroom,
        MagicFlower,
    }

    // v0.8.0 — per-tile crop growth stage. Advanced by LocalMap.TickCrops each
    // sim tick (Sown → Sprouting → Growing → Ripening → Ripe over 4 stage-steps).
    // The crop overlay tints per stage.
    public enum CropStage : byte
    {
        Empty = 0,     // tilled grow-zone tile, nothing planted
        Sown,          // seed in the ground
        Sprouting,
        Growing,
        Ripening,
        Ripe,          // harvestable
        // Reserved for v0.8.3 seasonal failure (Phase 10 weather). NOT written
        // in v0.8.0 — TickCrops stops at Ripe. When the writer lands in v0.8.3
        // it must also wire the clear/re-sow path (a Wilted slot is currently
        // neither harvestable nor sowable, so it would otherwise soft-lock).
        Wilted,
    }
}
