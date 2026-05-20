namespace Sporeholm.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — DF-style item quality tier. Multiplies item
    // value and (for tools) effectiveness. Crafted items roll quality from
    // the crafter's relevant skill; gathered items (raw food / raw
    // materials) usually roll Normal but can hit Fine on a lucky pick.
    // Legendary is reserved for named artifacts — never rolled at
    // creation, only awarded by Phase 8 storyteller events.
    public enum Quality : byte
    {
        Crude,
        Normal,
        Fine,
        Superior,
        Masterwork,
        Legendary,
    }

    public static class QualityMeta
    {
        // Value / effectiveness multiplier. The default Normal = 1.0 anchor
        // means existing code that doesn't know about Quality still gets
        // sensible numbers.
        public static float ValueMul(Quality q) => q switch
        {
            Quality.Crude      => 0.50f,
            Quality.Normal     => 1.00f,
            Quality.Fine       => 1.40f,
            Quality.Superior   => 2.00f,
            Quality.Masterwork => 3.50f,
            Quality.Legendary  => 6.00f,
            _                  => 1.00f,
        };

        // For food items, quality multiplies the nutrition restored.
        // Smaller spread than ValueMul because hunger doesn't care about
        // Masterwork as much as a trader does.
        public static float NutritionMul(Quality q) => q switch
        {
            Quality.Crude      => 0.85f,
            Quality.Normal     => 1.00f,
            Quality.Fine       => 1.15f,
            Quality.Superior   => 1.30f,
            Quality.Masterwork => 1.50f,
            Quality.Legendary  => 1.80f,
            _                  => 1.00f,
        };

        // Mood thought emitted on eating an item of this quality.
        // v0.4.61 (E2) — `isCooked` distinguishes raw (capberry, mushroom)
        // from prepared meals. Raw eating produces AteSimple at default
        // quality; only Cook-task output (Phase 5) passes isCooked=true
        // and unlocks the TastyMeal/AteFavorite tier. Without this gate,
        // raw capberries fired the same "Had a tasty meal" thought as
        // a Cook would, which left no aspirational delta for the kitchen.
        public static string MealThoughtKey(Quality q, bool isCooked = false)
        {
            if (!isCooked)
            {
                // Raw food. Exceptional quality still feels like a treat
                // (a Masterwork wild berry); awful raw food is a chore.
                return q switch
                {
                    Quality.Crude      => "AteHungry",     // gritty raw food
                    Quality.Masterwork => "AteFavorite",
                    Quality.Legendary  => "AteFavorite",
                    _                  => "AteSimple",     // Normal / Fine / Superior raw → modest +1
                };
            }
            // Cooked food — the prepared-meal tier. Phase 5 Cook task
            // calls this with isCooked=true.
            return q switch
            {
                Quality.Crude      => "AteHungry",
                Quality.Fine       => "TastyMeal",
                Quality.Superior   => "TastyMeal",
                Quality.Masterwork => "AteFavorite",
                Quality.Legendary  => "AteFavorite",
                _                  => "TastyMeal",
            };
        }

        public static string Symbol(Quality q) => q switch
        {
            Quality.Crude      => "−",
            Quality.Normal     => "·",
            Quality.Fine       => "+",
            Quality.Superior   => "★",
            Quality.Masterwork => "✦",
            Quality.Legendary  => "✯",
            _                  => "·",
        };
    }
}
