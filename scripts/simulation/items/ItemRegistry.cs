using System.Collections.Generic;

namespace SmurfulationC.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — sub-type catalogue. An ItemSubTypeDef
    // describes one "kind of thing you can make/find" — Smurfberry, Pick,
    // Cloak, Smurfberry Jam, Workbench. The instance-level data (its
    // material, quality, condition) lives on Item.
    //
    // For Phase 4 sub-phase A we seed only the sub-types that the current
    // game can actually produce: raw foods from gathering, raw materials
    // from excavation/chop, plus a small Tool / Apparel / Trinket pool
    // for scenario starting items. Crafted items (cooked meals, weapons,
    // furniture) join the registry as their workshops land in Phase 5+.
    public sealed class ItemSubTypeDef
    {
        public ItemKind Kind            { get; init; }
        public string   SubType         { get; init; } = "";
        public string   DisplayName     { get; init; } = "";
        public string   Icon            { get; init; } = "";
        // For food: nutrition restored when eaten (before quality multiplier).
        public float    BaseNutrition   { get; init; } = 0f;
        // Base condition cap (multiplied by Material.DurabilityMul).
        public float    BaseDurability  { get; init; } = 100f;
        // For food: how many sim *days* this item is "fresh" at baseline
        // material before stale-onset. Multiplied by 1/Material.DecayRateMul.
        public float    BaseFreshDays   { get; init; } = 0f;
        // Trade value baseline (multiplied by Material × Quality).
        public float    BaseValue       { get; init; } = 1f;
        // Materials this sub-type is allowed to be made of. Empty array =
        // any material in the registry; non-empty = must be in this list.
        // Used by ItemFactory.Create to constrain material rolls.
        public string[] AllowedFamilies { get; init; } = System.Array.Empty<string>();
        // v0.4.4 — body-class an item is built for. Picks / Hammers / etc.
        // are `Hand` (equipable in either LeftHand or RightHand); Hats
        // are `Head`; Cloaks are `Torso`; Boots are `Foot`. None = item
        // is not equipment (food, materials, magic, trinkets).
        public EquipSlotMeta.BodyClass BodyClass { get; init; } = EquipSlotMeta.BodyClass.None;
        // v0.4.4 — which task types prefer this tool. Auto-equip in
        // BehaviorSystem reads this set to pick "the best tool for the
        // current task" out of the colony inventory. Empty array = not
        // a task-specific tool.
        public TaskType[] PreferredForTasks { get; init; } = System.Array.Empty<TaskType>();
    }

    public static class ItemRegistry
    {
        public static readonly ItemSubTypeDef[] All =
        {
            // ── Food ────────────────────────────────────────────────────
            // v0.4.2 — taxonomy aligned with the player brief: Smurfberry,
            // SmallMushroom (the species; rolled at gather time with a
            // sub-variant in the Material axis — Plant/SmallMushroom for
            // forest, Plant/Sandshroom for arid, Plant/Pineshroom for
            // coastal/island), HerbCluster, MagicBerry (replaces the old
            // MagicFlower — magic plants yield both essence AND food).
            // Removed: standalone Pineshroom / PalmShroom / Sandshroom
            // sub-types; those are now material variants of SmallMushroom.
            new() { Kind = ItemKind.Food, SubType = "Smurfberry",    DisplayName = "Smurfberry",      Icon = "🫐", BaseNutrition = 5f,  BaseDurability = 30f, BaseFreshDays = 5f,  BaseValue = 1.0f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "SmallMushroom", DisplayName = "Small Mushroom",  Icon = "🍄", BaseNutrition = 4f,  BaseDurability = 35f, BaseFreshDays = 6f,  BaseValue = 0.9f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "HerbCluster",   DisplayName = "Herb Cluster",    Icon = "🌿", BaseNutrition = 3f,  BaseDurability = 30f, BaseFreshDays = 4f,  BaseValue = 1.1f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "MagicBerry",    DisplayName = "Magic Berry",     Icon = "🌺", BaseNutrition = 4f,  BaseDurability = 40f, BaseFreshDays = 7f,  BaseValue = 2.0f, AllowedFamilies = new[]{"Plant"} },

            // ── Material — Wood ────────────────────────────────────────
            // v0.4.2 — single WoodLog sub-type carrying the wood family on
            // the Material axis: DeadWood (from DeadLog terrain),
            // LivingWood (from LivingWood terrain), Fungal (from any
            // LargeMushroom variant — LargeMushroom / LargeSandshroom /
            // PalmShroom). UI groups by material so the player sees
            // separate DeadWood / LivingWood / Fungal rows.
            new() { Kind = ItemKind.Material, SubType = "WoodLog",   DisplayName = "Wood Log",   Icon = "🪵", BaseDurability = 100f, BaseValue = 1.0f, AllowedFamilies = new[]{"Wood"} },
            // Stone block from Excavate. Material axis carries the stone
            // variant: Granite / Limestone / Marble / Obsidian / Quartz /
            // Magicstone / MagicCrystal.
            new() { Kind = ItemKind.Material, SubType = "StoneBlock",DisplayName = "Stone Block",Icon = "🪨", BaseDurability = 120f, BaseValue = 1.0f, AllowedFamilies = new[]{"Stone"} },
            // Cuttings — produced by the Cut Plants order. Compostable
            // (Phase 5+ fertiliser) and flammable kindling.
            new() { Kind = ItemKind.Material, SubType = "Cuttings",  DisplayName = "Cuttings",   Icon = "🌱", BaseDurability = 30f,  BaseValue = 0.2f, AllowedFamilies = new[]{"Plant"} },

            // ── Magic ───────────────────────────────────────────────────
            // Both subtypes now have production paths: RawEssence drops
            // from MagicBerry plants (alongside the food item) and
            // CrystalShard from MagicCrystal stone-ore-vein excavation.
            new() { Kind = ItemKind.Magic, SubType = "RawEssence",   DisplayName = "Raw Essence",   Icon = "✨", BaseDurability = 50f, BaseValue = 1.5f, AllowedFamilies = new[]{"Magic"} },
            new() { Kind = ItemKind.Magic, SubType = "CrystalShard", DisplayName = "Crystal Shard", Icon = "💎", BaseDurability = 100f, BaseValue = 2.5f, AllowedFamilies = new[]{"Magic"} },

            // ── Tool ────────────────────────────────────────────────────
            // v0.4.4 — Tools wear in either Hand slot; auto-equip in the
            // smurf's dominant hand when the matching task fires.
            new() { Kind = ItemKind.Tool, SubType = "Pick",   DisplayName = "Pick",          Icon = "⛏",  BaseDurability = 120f, BaseValue = 3.0f, AllowedFamilies = new[]{"Stone","Wood","Metal","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.GatherMaterial } },
            new() { Kind = ItemKind.Tool, SubType = "Basket", DisplayName = "Basket",        Icon = "🧺", BaseDurability = 70f,  BaseValue = 1.5f, AllowedFamilies = new[]{"Wood","Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.GatherFood } },
            new() { Kind = ItemKind.Tool, SubType = "Focus",  DisplayName = "Mage Focus",    Icon = "🔮", BaseDurability = 80f,  BaseValue = 4.0f, AllowedFamilies = new[]{"Wood","Magic","Stone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.Attune, TaskType.Meditate } },
            new() { Kind = ItemKind.Tool, SubType = "Sickle", DisplayName = "Sickle",        Icon = "🪚", BaseDurability = 100f, BaseValue = 2.5f, AllowedFamilies = new[]{"Wood","Stone","Metal"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.ChopWood, TaskType.CutVegetation } },
            new() { Kind = ItemKind.Tool, SubType = "Hammer", DisplayName = "Smith's Hammer",Icon = "🔨", BaseDurability = 130f, BaseValue = 3.5f, AllowedFamilies = new[]{"Wood","Stone","Metal","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.Build } },

            // ── Weapon (Phase 7 — registered for scenario rolls) ───────
            new() { Kind = ItemKind.Weapon, SubType = "Spear", DisplayName = "Spear",         Icon = "🔱", BaseDurability = 100f, BaseValue = 4.0f, AllowedFamilies = new[]{"Wood","Stone","Metal","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand },
            new() { Kind = ItemKind.Weapon, SubType = "Club",  DisplayName = "Club",          Icon = "🏏", BaseDurability = 110f, BaseValue = 2.5f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand },
            new() { Kind = ItemKind.Weapon, SubType = "Sling", DisplayName = "Sling",         Icon = "🪢", BaseDurability = 70f,  BaseValue = 2.0f, AllowedFamilies = new[]{"Cloth","Hide"},
                BodyClass = EquipSlotMeta.BodyClass.Hand },

            // ── Apparel ─────────────────────────────────────────────────
            // v0.4.4 — Apparel maps to one of the body-part slots.
            // Cloaks/robes are Torso, Hats are Head, Boots are Foot (one
            // boot per foot slot; equipping a "Boots" item slots one
            // foot — paired-item logic lands with Phase 7 combat).
            new() { Kind = ItemKind.Apparel, SubType = "Cloak", DisplayName = "Cloak",        Icon = "🧥", BaseDurability = 80f, BaseValue = 2.0f, AllowedFamilies = new[]{"Cloth","Hide"},
                BodyClass = EquipSlotMeta.BodyClass.Torso },
            new() { Kind = ItemKind.Apparel, SubType = "Hat",   DisplayName = "Smurf Hat",    Icon = "👒", BaseDurability = 60f, BaseValue = 1.2f, AllowedFamilies = new[]{"Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Head },
            new() { Kind = ItemKind.Apparel, SubType = "Boots", DisplayName = "Boots",        Icon = "🥾", BaseDurability = 90f, BaseValue = 1.8f, AllowedFamilies = new[]{"Hide","Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Foot },

            // ── Trinket ─────────────────────────────────────────────────
            // No body class — trinkets don't occupy an equipment slot;
            // a future "accessories" axis (Phase 11 trade goods) may
            // give them a dedicated slot. For now they live in colony
            // inventory only.
            new() { Kind = ItemKind.Trinket, SubType = "Pendant", DisplayName = "Pendant",    Icon = "🌟", BaseDurability = 100f, BaseValue = 5.0f, AllowedFamilies = new[]{"Stone","Wood","Metal","Bone","Magic"} },
            new() { Kind = ItemKind.Trinket, SubType = "Ring",    DisplayName = "Ring",       Icon = "💍", BaseDurability = 100f, BaseValue = 4.0f, AllowedFamilies = new[]{"Stone","Metal","Magic"} },
        };

        private static readonly Dictionary<(ItemKind, string), ItemSubTypeDef> _byKey;
        private static readonly Dictionary<ItemKind, List<ItemSubTypeDef>>     _byKind;

        static ItemRegistry()
        {
            _byKey  = new Dictionary<(ItemKind, string), ItemSubTypeDef>(All.Length);
            _byKind = new Dictionary<ItemKind, List<ItemSubTypeDef>>(9);
            foreach (var def in All)
            {
                _byKey[(def.Kind, def.SubType)] = def;
                if (!_byKind.TryGetValue(def.Kind, out var list))
                    _byKind[def.Kind] = list = new List<ItemSubTypeDef>();
                list.Add(def);
            }
        }

        public static ItemSubTypeDef? Get(ItemKind kind, string subType) =>
            _byKey.TryGetValue((kind, subType), out var d) ? d : null;

        public static IReadOnlyList<ItemSubTypeDef> InKind(ItemKind kind) =>
            _byKind.TryGetValue(kind, out var list) ? list : System.Array.Empty<ItemSubTypeDef>();
    }
}
