namespace Sporeholm.Simulation.Crafting
{
    // v0.5.84s — Phase 5.5 Crafting Bills System. A `Bill` is one entry
    // in a workbench's bill queue: which recipe + how to repeat + when
    // to skip. Class (not record) because BillSystem mutates ProgressTicks
    // mid-craft. Each workbench tile owns a `List<Bill>` stored in
    // LocalMap._workbenchBills.
    //
    // RimWorld parallel: `Bill_Production`. The fields below map: recipe
    // → RecipeId, repeatMode → RepeatMode, targetCount → TargetCount,
    // ingredientFilter → strict-subtype (recipe ingredients already define
    // their family; the bill can add further constraint via a future
    // patch — for v0.5.84s the recipe filter is used unmodified).
    public sealed class Bill
    {
        public string RecipeId  { get; init; } = "";
        public BillRepeatMode Mode { get; set; } = BillRepeatMode.Forever;
        public int    RepeatCount { get; set; } = 1;        // Mode == RepeatCount
        public int    TargetCount { get; set; } = 10;       // Mode == TargetCount (skip if inventory ≥ this)
        public int    Suspended   { get; set; } = 0;        // 0 = active, 1 = paused by player

        // v0.5.84s — runtime progress when a pawn is mid-craft on this
        // specific bill. Persists across BehaviorSystem ticks until the
        // recipe completes (matches RimWorld's per-bill work-left field).
        public int    ProgressTicks { get; set; } = 0;

        // v0.5.84s — completion counter for RepeatCount mode. Decremented
        // each successful craft; bill auto-removes when it hits 0.
        public int    RepeatsRemaining { get; set; } = 0;
    }

    public enum BillRepeatMode : byte
    {
        Forever,         // re-queue indefinitely as long as ingredients hold
        RepeatCount,     // run RepeatCount times then auto-remove
        TargetCount,     // skip if colony inventory of output ≥ TargetCount, else run
    }
}
