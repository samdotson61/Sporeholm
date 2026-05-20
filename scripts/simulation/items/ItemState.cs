namespace Sporeholm.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — runtime state of an item, separate from
    // quality. Quality is *what was made*; State is *how it's holding up*.
    //
    //   Fresh    — Condition >= 70 % of Durability. Default for new items.
    //   Stale    — 30–70 % Condition. Food gives reduced nutrition + bad
    //              thought; tools / apparel function but with stat penalty.
    //   Spoiled  — Condition < 30 %. Food is inedible (eating emits the
    //              AteDisliked thought); tools / apparel are unusable.
    //   Depleted — Bottomed out for a non-food item that doesn't break
    //              (Magic Essence consumed at a Mage Circle, etc.).
    //   Broken   — Tool / weapon / apparel hit 0 condition mid-use; can
    //              be disassembled for half-material at a workshop.
    public enum ItemState : byte
    {
        Fresh,
        Stale,
        Spoiled,
        Depleted,
        Broken,
    }

    public static class ItemStateMeta
    {
        // Derive state from a condition / durability ratio. Used by the
        // deterioration system after applying decay each tick — single
        // mapping so the threshold values live in one place.
        public static ItemState FromCondition(float condition, float durability)
        {
            if (durability <= 0f) return ItemState.Broken;
            float ratio = condition / durability;
            if (condition <= 0f) return ItemState.Broken;
            if (ratio < 0.30f)   return ItemState.Spoiled;
            if (ratio < 0.70f)   return ItemState.Stale;
            return ItemState.Fresh;
        }

        public static string Symbol(ItemState s) => s switch
        {
            ItemState.Fresh    => "✓",
            ItemState.Stale    => "~",
            ItemState.Spoiled  => "✗",
            ItemState.Depleted => "·",
            ItemState.Broken   => "✗",
            _                  => "·",
        };
    }
}
