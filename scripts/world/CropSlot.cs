namespace Sporeholm.World
{
    // v0.8.0 (Phase 8) — per-tile sown-crop state on a grow-zone cell. Mirrors
    // VegetationSlot's value-type-on-the-map model. GrowthTicks accumulates
    // toward the crop's GrowTicksPerStage × stage thresholds; LocalMap.TickCrops
    // advances Stage. An Empty slot is a tilled grow-zone tile awaiting sowing.
    public struct CropSlot
    {
        public CropType  Crop;
        public CropStage Stage;
        public int       GrowthTicks;   // sim-ticks accumulated since sown

        public bool IsPlanted => Crop != CropType.None && Stage != CropStage.Empty;
        public bool IsRipe    => Stage == CropStage.Ripe;
        public bool IsEmpty   => Stage == CropStage.Empty || Crop == CropType.None;

        public static CropSlot Empty => new() { Crop = CropType.None, Stage = CropStage.Empty, GrowthTicks = 0 };
    }
}
