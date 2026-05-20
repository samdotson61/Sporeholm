namespace Sporeholm.World
{
    // Per-tile vegetation state stored in the parallel VegetationSlot[,] array on LocalMap.
    // All types except Underbrush and MossPatch have harvestable yield.
    // Base yields: CapberryBush=4, SmallMushroom=5, LargeMushroom=3, HerbCluster=3, MagicFlower=2, SmallSandshroom=2, LargeSandshroom=1.
    // Regrowth timers (in-game days): CapberryBush=6, SmallMushroom=3, LargeMushroom=18, HerbCluster=10, MagicFlower=20, SmallSandshroom=6, LargeSandshroom=28.
    public struct VegetationSlot
    {
        public VegetationType Type;
        public byte Health;          // 0–100
        public byte YieldRemaining;  // harvest charges remaining
        public ushort RegrowthTimer; // sim days until yield restores (0 = not regrowing)

        public bool IsPresent  => Type != VegetationType.None;
        public bool IsDepleted => IsPresent && YieldRemaining == 0
                                  && Type is not VegetationType.Underbrush
                                             and not VegetationType.MossPatch;

        public static VegetationSlot Empty => default;

        public static VegetationSlot Create(VegetationType type) => new()
        {
            Type           = type,
            Health         = 100,
            YieldRemaining = BaseYield(type),
            RegrowthTimer  = 0,
        };

        public static byte BaseYield(VegetationType type) => type switch
        {
            VegetationType.CapberryBush  => 4,
            VegetationType.SmallMushroom   => 5,
            VegetationType.LargeMushroom   => 3,
            VegetationType.HerbCluster     => 3,
            VegetationType.MagicFlower     => 2,
            VegetationType.SmallSandshroom => 2,   // 1/3 of SmallMushroom (5), rounded up
            VegetationType.LargeSandshroom => 1,
            VegetationType.PalmShroom      => 2,   // 2/3 of LargeMushroom (3)
            VegetationType.PineShroom      => 8,   // 1.5× SmallMushroom (5 × 1.5 = 7.5 → 8)
            _                              => 0,
        };

        // In-game days until depleted slot regrows.
        public static ushort RegrowthDays(VegetationType type) => type switch
        {
            VegetationType.CapberryBush  => 6,
            VegetationType.SmallMushroom   => 3,
            VegetationType.LargeMushroom   => 18,
            VegetationType.HerbCluster     => 10,
            VegetationType.MagicFlower     => 20,
            VegetationType.SmallSandshroom => 6,   // slower regrowth in harsh arid conditions
            VegetationType.LargeSandshroom => 28,
            VegetationType.PalmShroom      => 24,  // between LargeMushroom (18) and LargeSandshroom (28)
            VegetationType.PineShroom      => 5,   // slightly slower than SmallMushroom (3); coastal moisture helps
            _                              => 0,
        };
    }
}
