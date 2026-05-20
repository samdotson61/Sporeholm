using System.Collections.Generic;

namespace Sporeholm.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — sub-type catalogue. An ItemSubTypeDef
    // describes one "kind of thing you can make/find" — Capberry, Pick,
    // Cloak, Capberry Jam, Workbench. The instance-level data (its
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
        // v0.5.84t — Phase 7 combat stubs. Damage per hit (RimWorld-scale:
        // typical melee weapons 6-15, blunt 4-10, ranged projectiles
        // 5-12). Accuracy is hit-chance multiplier at touch range (1.0 =
        // never miss, 0.0 = always miss). Phase 7 CombatSystem will read
        // both at swing-resolution time. Defaults 0 = item isn't a weapon.
        // Non-weapon tools (Pick / Hammer / Sickle / Knife) carry
        // improvised damage values so a Crafter caught without a real
        // weapon can still defend themselves.
        public float    BaseDamage   { get; init; } = 0f;
        public float    BaseAccuracy { get; init; } = 0f;
        // v0.5.84t — Shield block-chance stub for the Phase 7 combat
        // system. Per-hit roll against an attacker's swing decides
        // whether the shield deflects the blow (zero damage) or fails.
        // RimWorld doesn't have shields; this is a DF-style addition.
        // 0.0 = no block; ~0.25 (default wood) = a quarter of incoming
        // attacks deflected; up to ~0.45 for masterwork Marine-grade
        // shields. Phase 7 combat reads at swing-resolution time.
        public float    BaseBlockChance { get; init; } = 0f;
    }

    public static class ItemRegistry
    {
        public static readonly ItemSubTypeDef[] All =
        {
            // ── Food ────────────────────────────────────────────────────
            // v0.4.2 — taxonomy aligned with the player brief: Capberry,
            // SmallMushroom (the species; rolled at gather time with a
            // sub-variant in the Material axis — Plant/SmallMushroom for
            // forest, Plant/Sandshroom for arid, Plant/Pineshroom for
            // coastal/island), HerbCluster, MagicBerry (replaces the old
            // MagicFlower — magic plants yield both essence AND food).
            // Removed: standalone Pineshroom / PalmShroom / Sandshroom
            // sub-types; those are now material variants of SmallMushroom.
            new() { Kind = ItemKind.Food, SubType = "Capberry",    DisplayName = "Capberry",      Icon = "🫐", BaseNutrition = 5f,  BaseDurability = 30f, BaseFreshDays = 5f,  BaseValue = 1.0f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "SmallMushroom", DisplayName = "Small Mushroom",  Icon = "🍄", BaseNutrition = 4f,  BaseDurability = 35f, BaseFreshDays = 6f,  BaseValue = 0.9f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "HerbCluster",   DisplayName = "Herb Cluster",    Icon = "🌿", BaseNutrition = 3f,  BaseDurability = 30f, BaseFreshDays = 4f,  BaseValue = 1.1f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Food, SubType = "MagicBerry",    DisplayName = "Magic Berry",     Icon = "🌺", BaseNutrition = 4f,  BaseDurability = 40f, BaseFreshDays = 7f,  BaseValue = 2.0f, AllowedFamilies = new[]{"Plant"} },
            // v0.5.22 (Phase 5E) — prepared meal. Output of CookSystem when
            // a Crafter uses a Workbench with raw food ingredients. Higher
            // nutrition + fresh-days + value than the raw inputs (RimWorld
            // pattern: cooking adds substantial nutrition value). Freshness
            // longer than raw because cooking partially preserves food.
            new() { Kind = ItemKind.Food, SubType = "PreparedMeal",  DisplayName = "Prepared Meal",   Icon = "🍲", BaseNutrition = 12f, BaseDurability = 45f, BaseFreshDays = 10f, BaseValue = 3.0f, AllowedFamilies = new[]{"Plant"} },
            // v0.5.84s+ — Phase 5.5 Crafting Bills outputs.
            // v0.5.84t — restructured per Sam: ClothBolt → MossCloth (moss-
            // specific cloth), added GrassLinen (woven from Grass Cuttings),
            // BerryJuice replaces BerryWine, Knife consolidated to a single
            // "Knife" item subtype with material variants (Bone / Wood /
            // Stone / FungalWood), RefinedPlank stays + adds Pebblestone
            // refined-stone item subtype. Migration in
            // ItemRegistry.LegacySubTypeMigration maps old saves' SubTypes
            // to new names so inventories load cleanly.
            new() { Kind = ItemKind.Food,     SubType = "BerryJuice",        DisplayName = "Berry Juice",         Icon = "🧃", BaseNutrition = 6f, BaseDurability = 70f, BaseFreshDays = 18f, BaseValue = 3.0f, AllowedFamilies = new[]{"Plant"} },
            new() { Kind = ItemKind.Material, SubType = "MossCloth",         DisplayName = "Moss Cloth",          Icon = "🧵", BaseDurability = 80f, BaseValue = 2.0f, AllowedFamilies = new[]{"Cloth","Plant"} },
            new() { Kind = ItemKind.Material, SubType = "GrassLinen",        DisplayName = "Grass Linen",         Icon = "🧶", BaseDurability = 70f, BaseValue = 1.6f, AllowedFamilies = new[]{"Cloth","Plant"} },
            new() { Kind = ItemKind.Tool,     SubType = "Knife",             DisplayName = "Knife",               Icon = "🗡", BaseDurability = 110f, BaseValue = 3.0f, AllowedFamilies = new[]{"Bone","Wood","Stone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.CutVegetation },
                BaseDamage = 5f, BaseAccuracy = 0.85f },
            new() { Kind = ItemKind.Magic,    SubType = "MagicHerbPoultice", DisplayName = "Magic Herb Poultice", Icon = "🧪", BaseDurability = 60f, BaseValue = 5.0f, AllowedFamilies = new[]{"Magic","Plant"} },
            new() { Kind = ItemKind.Material, SubType = "RefinedPlank",      DisplayName = "Refined Plank",       Icon = "🪵", BaseDurability = 140f, BaseValue = 2.2f, AllowedFamilies = new[]{"Wood"} },
            new() { Kind = ItemKind.Material, SubType = "Pebblestone",       DisplayName = "Pebblestone",         Icon = "🪨", BaseDurability = 150f, BaseValue = 1.5f, AllowedFamilies = new[]{"Stone"} },
            new() { Kind = ItemKind.Material, SubType = "BoneFragment",      DisplayName = "Bone Fragment",       Icon = "🦴", BaseDurability = 80f, BaseValue = 1.0f, AllowedFamilies = new[]{"Bone"} },

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
            // Cuttings — produced by the Cut Plants order on Underbrush.
            // Compostable (Phase 5+ fertiliser) and flammable kindling.
            // v0.5.84t — Cuttings repurposed as "Grass Cuttings" with the
            // GrassLinen recipe now consuming them. SubType kept stable
            // for save compat; only the DisplayName changed.
            new() { Kind = ItemKind.Material, SubType = "Cuttings",  DisplayName = "Grass Cuttings", Icon = "🌱", BaseDurability = 30f,  BaseValue = 0.2f, AllowedFamilies = new[]{"Plant"} },
            // v0.5.70 — Mosslet: cut from MossPatch tiles. Spongy moss
            // tufts; held separately from Cuttings so a future Phase 5+
            // system can branch on it (Sam: "we'll use later").
            new() { Kind = ItemKind.Material, SubType = "Mosslet",   DisplayName = "Mosslet",    Icon = "🟢", BaseDurability = 35f,  BaseValue = 0.25f, AllowedFamilies = new[]{"Plant"} },

            // ── Magic ───────────────────────────────────────────────────
            // Both subtypes now have production paths: RawEssence drops
            // from MagicBerry plants (alongside the food item) and
            // CrystalShard from MagicCrystal stone-ore-vein excavation.
            new() { Kind = ItemKind.Magic, SubType = "RawEssence",   DisplayName = "Raw Essence",   Icon = "✨", BaseDurability = 50f, BaseValue = 1.5f, AllowedFamilies = new[]{"Magic"} },
            new() { Kind = ItemKind.Magic, SubType = "CrystalShard", DisplayName = "Crystal Shard", Icon = "💎", BaseDurability = 100f, BaseValue = 2.5f, AllowedFamilies = new[]{"Magic"} },

            // ── Tool ────────────────────────────────────────────────────
            // v0.4.4 — Tools wear in either Hand slot; auto-equip in the
            // shroomp's dominant hand when the matching task fires.
            // v0.5.16 — Metal family removed from all AllowedFamilies arrays
            // (shroomps don't have refined metalworking; Hematite stone fills
            // the iron-analog role per v0.5.15 lore revision). Stone /
            // Wood / Bone cover the relevant weight + cutting roles.
            new() { Kind = ItemKind.Tool, SubType = "Pick",   DisplayName = "Pick",          Icon = "⛏",  BaseDurability = 120f, BaseValue = 3.0f, AllowedFamilies = new[]{"Stone","Wood","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.GatherMaterial },
                BaseDamage = 4f, BaseAccuracy = 0.70f },   // v0.5.84t — improvised weapon stats
            new() { Kind = ItemKind.Tool, SubType = "Basket", DisplayName = "Basket",        Icon = "🧺", BaseDurability = 70f,  BaseValue = 1.5f, AllowedFamilies = new[]{"Wood","Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.GatherFood } },   // no weapon stats — baskets aren't combat-viable
            new() { Kind = ItemKind.Tool, SubType = "Focus",  DisplayName = "Sage Staff",    Icon = "🔮", BaseDurability = 80f,  BaseValue = 4.0f, AllowedFamilies = new[]{"Wood","Magic","Stone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.Attune, TaskType.Meditate } },
            new() { Kind = ItemKind.Tool, SubType = "Sickle", DisplayName = "Sickle",        Icon = "🪚", BaseDurability = 100f, BaseValue = 2.5f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.ChopWood, TaskType.CutVegetation },
                BaseDamage = 4f, BaseAccuracy = 0.70f },
            new() { Kind = ItemKind.Tool, SubType = "Hammer", DisplayName = "Smith's Hammer",Icon = "🔨", BaseDurability = 130f, BaseValue = 3.5f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                PreferredForTasks = new[]{ TaskType.Build },
                BaseDamage = 4f, BaseAccuracy = 0.70f },

            // ── Weapon (Phase 7 — registered for scenario rolls) ───────
            // v0.5.84t — BaseDamage + BaseAccuracy populated per RimWorld
            // scale (touch-range melee 8-12; ranged ~6 at lower accuracy).
            new() { Kind = ItemKind.Weapon, SubType = "Spear", DisplayName = "Spear",         Icon = "🔱", BaseDurability = 100f, BaseValue = 4.0f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 12f, BaseAccuracy = 0.70f },
            new() { Kind = ItemKind.Weapon, SubType = "Club",  DisplayName = "Club",          Icon = "🏏", BaseDurability = 110f, BaseValue = 2.5f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 8f,  BaseAccuracy = 0.80f },
            new() { Kind = ItemKind.Weapon, SubType = "Sling", DisplayName = "Sling",         Icon = "🪢", BaseDurability = 70f,  BaseValue = 2.0f, AllowedFamilies = new[]{"Cloth","Hide"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 6f,  BaseAccuracy = 0.55f },   // ranged — lower accuracy

            // v0.5.84t — extended weapon catalogue (Sam: bows / crossbows /
            // atlatls + swords / axes / shields). Phase 7 combat reads
            // BaseDamage / BaseAccuracy / BaseBlockChance at swing-resolution
            // time. Stats per RimWorld scale (melee 14-15, ranged bow 14,
            // crossbow 20, atlatl 10, shield block 0.25).
            new() { Kind = ItemKind.Weapon, SubType = "Bow",      DisplayName = "Bow",          Icon = "🏹", BaseDurability = 90f,  BaseValue = 5.0f, AllowedFamilies = new[]{"Wood","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 14f, BaseAccuracy = 0.65f },   // ranged — better than sling
            new() { Kind = ItemKind.Weapon, SubType = "Crossbow", DisplayName = "Crossbow",     Icon = "🎯", BaseDurability = 120f, BaseValue = 8.0f, AllowedFamilies = new[]{"Wood","Bone","Stone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 20f, BaseAccuracy = 0.70f },   // ranged — higher dmg + acc, slower (Phase 7)
            new() { Kind = ItemKind.Weapon, SubType = "Atlatl",   DisplayName = "Atlatl",       Icon = "🪃", BaseDurability = 70f,  BaseValue = 2.5f, AllowedFamilies = new[]{"Wood","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 10f, BaseAccuracy = 0.50f },   // ranged — primitive spear-thrower
            new() { Kind = ItemKind.Weapon, SubType = "Sword",    DisplayName = "Sword",        Icon = "⚔",  BaseDurability = 130f, BaseValue = 6.0f, AllowedFamilies = new[]{"Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 14f, BaseAccuracy = 0.80f },   // melee — balanced
            new() { Kind = ItemKind.Weapon, SubType = "Axe",      DisplayName = "Axe",          Icon = "🪓", BaseDurability = 140f, BaseValue = 5.5f, AllowedFamilies = new[]{"Stone","Bone","Wood"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseDamage = 15f, BaseAccuracy = 0.75f },   // melee — heavy + slow


            // ── Apparel ─────────────────────────────────────────────────
            // v0.4.4 — Apparel maps to one of the body-part slots.
            // Cloaks/robes are Torso, Hats are Head, Boots are Foot (one
            // boot per foot slot; equipping a "Boots" item slots one
            // foot — paired-item logic lands with Phase 7 combat).
            new() { Kind = ItemKind.Apparel, SubType = "Cloak", DisplayName = "Cloak",        Icon = "🧥", BaseDurability = 80f, BaseValue = 2.0f, AllowedFamilies = new[]{"Cloth","Hide"},
                BodyClass = EquipSlotMeta.BodyClass.Torso },
            new() { Kind = ItemKind.Apparel, SubType = "Hat",   DisplayName = "Shroomp Hat",    Icon = "👒", BaseDurability = 60f, BaseValue = 1.2f, AllowedFamilies = new[]{"Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Head },
            new() { Kind = ItemKind.Apparel, SubType = "Boots", DisplayName = "Boots",        Icon = "🥾", BaseDurability = 90f, BaseValue = 1.8f, AllowedFamilies = new[]{"Hide","Cloth"},
                BodyClass = EquipSlotMeta.BodyClass.Foot },
            // v0.5.84t — Shield. Off-hand defensive equipment for Phase 7
            // combat. BodyClass.Hand so the existing dual-wield slotting
            // can park it in the off hand alongside a primary weapon. Block
            // chance ~25% baseline; per-material durability + value vary.
            new() { Kind = ItemKind.Apparel, SubType = "Shield", DisplayName = "Shield",       Icon = "🛡", BaseDurability = 130f, BaseValue = 4.0f, AllowedFamilies = new[]{"Wood","Stone","Bone"},
                BodyClass = EquipSlotMeta.BodyClass.Hand,
                BaseBlockChance = 0.25f },

            // ── Trinket ─────────────────────────────────────────────────
            // No body class — trinkets don't occupy an equipment slot;
            // a future "accessories" axis (Phase 11 trade goods) may
            // give them a dedicated slot. For now they live in colony
            // inventory only.
            new() { Kind = ItemKind.Trinket, SubType = "Pendant", DisplayName = "Pendant",    Icon = "🌟", BaseDurability = 100f, BaseValue = 5.0f, AllowedFamilies = new[]{"Stone","Wood","Bone","Magic"} },
            new() { Kind = ItemKind.Trinket, SubType = "Ring",    DisplayName = "Ring",       Icon = "💍", BaseDurability = 100f, BaseValue = 4.0f, AllowedFamilies = new[]{"Stone","Magic"} },
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
