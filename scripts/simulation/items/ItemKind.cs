namespace Sporeholm.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — top-level item categorisation. Mirrors the
    // 9-category taxonomy laid out in §3.x.8 / Phase 4 of the roadmap.
    // Adding a new category later is one enum entry plus an HUD/Resources
    // panel glyph; the rest of the system reads via this enum so a new
    // kind is automatically visible everywhere items are listed.
    public enum ItemKind : byte
    {
        Food,
        Material,
        Tool,
        Weapon,
        Apparel,
        Furniture,
        Magic,
        TradeGood,
        Trinket,
        // v0.4.33 — Corpse item. Spawned on shroomp death; carries the
        // dead shroomp's biographical data in Item.CorpseInfo. Decays
        // like food but at a roadmap-spec ~7-day timeline before the
        // tile becomes empty again. Non-stackable (each corpse is one
        // unique shroomp).
        Corpse,
    }

    // Display glyph + name, kept next to the enum so adding a new ItemKind
    // is impossible without picking an icon. UI consumers walk this table.
    public static class ItemKindMeta
    {
        public static string Icon(ItemKind k) => k switch
        {
            ItemKind.Food      => "🍓",
            ItemKind.Material  => "🪨",
            ItemKind.Tool      => "🔨",
            ItemKind.Weapon    => "⚔",
            ItemKind.Apparel   => "🧥",
            ItemKind.Furniture => "🪑",
            ItemKind.Magic     => "✨",
            ItemKind.TradeGood => "💰",
            ItemKind.Trinket   => "🌟",
            ItemKind.Corpse    => "💀",
            _                  => "?",
        };

        public static string Name(ItemKind k) => k switch
        {
            ItemKind.TradeGood => "Trade Good",
            _                  => k.ToString(),
        };

        // Stackable kinds merge identical (SubType, Material, Quality) items
        // into a single inventory entry with a Quantity field. Non-stackable
        // kinds (tools, weapons, apparel, furniture, trinkets) keep one
        // Item per object because their Condition diverges over use.
        public static bool IsStackable(ItemKind k) => k switch
        {
            ItemKind.Food      => true,
            ItemKind.Material  => true,
            ItemKind.Magic     => true,
            ItemKind.TradeGood => true,
            _                  => false,
        };
    }
}
