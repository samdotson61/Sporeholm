using System.Collections.Generic;
using Sporeholm.Simulation.Items;

namespace Sporeholm.Simulation.Crafting
{
    // v0.5.84s — Phase 5.5 Crafting Bills System. Static recipe catalog.
    // v0.5.84t — recipe restructure per Sam: BerryWine → BerryJuice;
    // ClothBolt → MossCloth (the only Mosslet output); GrassLinen NEW
    // (uses Grass Cuttings — formerly the dead-end Cuttings item);
    // BoneKnife → 4 "Carve Knife (X)" variants for Bone / Wood / Stone /
    // Fungal; FungalWoodPlank → 3 "Saw Plank (X)" variants producing
    // ~3× input wood; RefineStoneBlock → 5 "Refine Pebblestone (X)"
    // stone-subtype variants producing ~2× input stone. Pebblestone is
    // a buildable StructureMat (v0.5.84t) so refined stone has a real
    // gameplay use.
    public static class RecipeRegistry
    {
        public static readonly RecipeDef[] All =
        {
            // ── Food ────────────────────────────────────────────────────
            new RecipeDef(
                Id:           "CookMeal",
                DisplayName:  "Cook Meal",
                Description:  "Combine 4 raw foods of any kind (berries, mushrooms, herbs, future meats) into a Prepared Meal. Higher nutrition + longer freshness than the raw inputs. Cooks fastest at a Cooking Table; Bonfire fallback runs at half speed.",
                // v0.5.84t — MaterialFamily=null means "any family of ItemKind.Food",
                // so the cook will pull from Plant (berries/mushrooms/herbs) and
                // any future Food families (Meat, Insect, etc.) interchangeably.
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Food, null, 4),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Food, "PreparedMeal", "Plant", "Cooked", 1, RollQuality: true),
                },
                WorkTicks:    240,
                PrimarySkill: "Cooking",                // v0.6.2 — Phase 5.6 ships Cooking skill split.
                SkillMinimum: 0,
                XpReward:     100,
                Station:              RecipeStation.CookingTable,
                AllowBonfireFallback:  true,
                BonfireSpeedMul:       2.0f
            ),

            new RecipeDef(
                Id:           "JuiceBerries",
                DisplayName:  "Juice Berries",
                Description:  "Press 4 Capberries into 1 Berry Juice — a refreshing drink with longer freshness than the raw fruit. Runs at a Cooking Table; Bonfire fallback runs at half speed.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Food, "Plant", 4, RequiredSubType: "Capberry"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Food, "BerryJuice", "Plant", "Capberry", 1, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Cooking",                // v0.6.2 — Phase 5.6 ships Cooking skill split.
                SkillMinimum: 1,
                XpReward:     100,
                Station:              RecipeStation.CookingTable,
                AllowBonfireFallback:  true,
                BonfireSpeedMul:       2.0f
            ),

            // ── Cloth ───────────────────────────────────────────────────
            new RecipeDef(
                Id:           "MossCloth",
                DisplayName:  "Weave Moss Cloth",
                Description:  "Weave 4 Mosslets into 1 Moss Cloth. The Phase 7 §7.17 apparel system's primary cloth supply.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Plant", 4, RequiredSubType: "Mosslet"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "MossCloth", "Cloth", "Mossleaf", 1, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     100
            ),

            new RecipeDef(
                Id:           "GrassLinen",
                DisplayName:  "Weave Grass Linen",
                Description:  "Weave 4 Grass Cuttings into 1 Grass Linen — a lightweight cloth alternative to Moss Cloth, with a use for early-game apparel.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Plant", 4, RequiredSubType: "Cuttings"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "GrassLinen", "Cloth", "Grass", 1, RollQuality: true),
                },
                WorkTicks:    280,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     90
            ),

            // ── Knives (one per material family, single Knife item subtype) ──
            new RecipeDef(
                Id:           "CarveKnife_Bone",
                DisplayName:  "Carve Knife (Bone)",
                Description:  "Carve 2 Bone Fragments into a Bone Knife — light, sharp, good for plant work.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Knife", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     180
            ),

            new RecipeDef(
                Id:           "CarveKnife_Wood",
                DisplayName:  "Carve Knife (Wood)",
                Description:  "Carve 2 Wood Logs into a Wood Knife — soft edge, mostly for ceremonial / training use.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Knife", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     160
            ),

            new RecipeDef(
                Id:           "CarveKnife_Fungal",
                DisplayName:  "Carve Knife (Fungal)",
                Description:  "Carve 2 Fungal Wood logs into a Fungal Knife — spongy but tough.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "Fungal"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Knife", "Wood", "Fungal", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     160
            ),

            new RecipeDef(
                Id:           "CarveKnife_Stone",
                DisplayName:  "Carve Knife (Stone)",
                Description:  "Knap 2 Stone Blocks into a Stone Knife — heavy, durable, the classic blade.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Knife", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    600,
                PrimarySkill: "Crafting",
                SkillMinimum: 5,
                XpReward:     200
            ),

            // ── Magic herb medicine ─────────────────────────────────────
            new RecipeDef(
                Id:           "MagicHerbPoultice",
                DisplayName:  "Magic Herb Poultice",
                Description:  "Mix 2 Raw Essence + 1 Herb Cluster into a Magic Herb Poultice. The Phase 7 §7.18 Healer system's basic medicine item.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Magic, "Magic", 2, RequiredSubType: "RawEssence"),
                    new RecipeIngredient(ItemKind.Food,  "Plant", 1, RequiredSubType: "HerbCluster"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Magic, "MagicHerbPoultice", "Magic", "Essence", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill:   "Crafting",
                SkillMinimum:   3,
                SecondarySkill: "Healing",
                SecondaryMinimum: 2,
                XpReward:       140
            ),

            // ── Pebblestone (one per stone subtype) ─────────────────────
            // Polish raw stone blocks into Pebblestone — a cobblestone-like
            // refined material the build pipeline can consume for walls /
            // floors. Roughly 2× input (2 raw → 4 pebblestone).
            new RecipeDef(
                Id:           "Pebblestone_Granite",
                DisplayName:  "Refine Granite Pebblestone",
                Description:  "Polish 2 Granite blocks into 4 Granite Pebblestone — cobblestone refined for cleaner builds.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2, RequiredSubType: "Granite"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "Pebblestone", "Stone", "Granite", 4, RollQuality: true),
                },
                WorkTicks:    360,
                PrimarySkill: "Mining",
                SkillMinimum: 2,
                XpReward:     100
            ),
            new RecipeDef(
                Id:           "Pebblestone_Limestone",
                DisplayName:  "Refine Limestone Pebblestone",
                Description:  "Polish 2 Limestone blocks into 4 Limestone Pebblestone.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2, RequiredSubType: "Limestone"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "Pebblestone", "Stone", "Limestone", 4, RollQuality: true),
                },
                WorkTicks:    360,
                PrimarySkill: "Mining",
                SkillMinimum: 2,
                XpReward:     100
            ),
            new RecipeDef(
                Id:           "Pebblestone_Marble",
                DisplayName:  "Refine Marble Pebblestone",
                Description:  "Polish 2 Marble blocks into 4 Marble Pebblestone — premium polished stone.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2, RequiredSubType: "Marble"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "Pebblestone", "Stone", "Marble", 4, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Mining",
                SkillMinimum: 3,
                XpReward:     120
            ),
            new RecipeDef(
                Id:           "Pebblestone_Obsidian",
                DisplayName:  "Refine Obsidian Pebblestone",
                Description:  "Polish 2 Obsidian blocks into 4 Obsidian Pebblestone — striking near-black stone.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2, RequiredSubType: "Obsidian"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "Pebblestone", "Stone", "Obsidian", 4, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Mining",
                SkillMinimum: 3,
                XpReward:     120
            ),
            new RecipeDef(
                Id:           "Pebblestone_Quartz",
                DisplayName:  "Refine Quartz Pebblestone",
                Description:  "Polish 2 Quartz blocks into 4 Quartz Pebblestone — pale and crystalline.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2, RequiredSubType: "Quartz"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "Pebblestone", "Stone", "Quartz", 4, RollQuality: true),
                },
                WorkTicks:    360,
                PrimarySkill: "Mining",
                SkillMinimum: 2,
                XpReward:     100
            ),

            // ── Refined Planks (one per wood subtype) ───────────────────
            // Saw a wood log into 3 refined planks — premium wood-family
            // material with bonus durability + value. Triples input wood.
            new RecipeDef(
                Id:           "SawPlank_DeadWood",
                DisplayName:  "Saw Plank (Dead Wood)",
                Description:  "Saw 1 Dead Wood log into 3 Refined Planks — premium wood-family build material.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "RefinedPlank", "Wood", "DeadWood", 3, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     80
            ),
            new RecipeDef(
                Id:           "SawPlank_Fungal",
                DisplayName:  "Saw Plank (Fungal Wood)",
                Description:  "Saw 1 Fungal Wood log into 3 Refined Planks — spongy yet durable build material.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "Fungal"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "RefinedPlank", "Wood", "Fungal", 3, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     80
            ),
            new RecipeDef(
                Id:           "SawPlank_LivingWood",
                DisplayName:  "Saw Plank (Living Wood)",
                Description:  "Saw 1 Living Wood log into 3 Refined Planks — vital green wood, high beauty bonus.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "LivingWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Material, "RefinedPlank", "Wood", "LivingWood", 3, RollQuality: true),
                },
                WorkTicks:    340,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     100
            ),

            // ────────────────────────────────────────────────────────────────
            // v0.5.84t TOOL & WEAPON RECIPES (20 entries)
            // Sam: "Add crafting recipes for any tools that are currently in
            // the game. Pawns should be able to make things like spears, axes,
            // hammers, pickaxes, baskets etc."
            //
            // Material-variant pattern (mirrors v0.5.84t Knife): each tool
            // has 2-3 recipes covering the material families its AllowedFamilies
            // list permits. Output Material.SubType is the canonical name so
            // dropped icons / build-pipeline strict-consume still work
            // (StructureMatMeta.ConsumeSubType keys off these).
            // ────────────────────────────────────────────────────────────────

            // ── Pick (3 variants: Stone / Wood / Bone) ─────────────────
            new RecipeDef(
                Id:           "CraftPick_Stone",
                DisplayName:  "Craft Pick (Stone)",
                Description:  "Knap 2 Stone blocks and 1 Wood log into a Stone Pick. Pickaxes speed up mining significantly.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Pick", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     180
            ),
            new RecipeDef(
                Id:           "CraftPick_Wood",
                DisplayName:  "Craft Pick (Wood)",
                Description:  "Carve 3 Wood logs into a Wood Pick — soft head, modest mining bonus.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 3, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Pick", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     150
            ),
            new RecipeDef(
                Id:           "CraftPick_Bone",
                DisplayName:  "Craft Pick (Bone)",
                Description:  "Lash 2 Bone Fragments and 1 Wood log into a Bone Pick — lightweight, decent mining speed.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Pick", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     170
            ),

            // ── Hammer (3 variants: Stone / Wood / Bone) ───────────────
            new RecipeDef(
                Id:           "CraftHammer_Stone",
                DisplayName:  "Craft Hammer (Stone)",
                Description:  "Bind a 1 Stone block to a 1 Wood log to make a Stone Hammer. Speeds up structure framing.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Hammer", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     150
            ),
            new RecipeDef(
                Id:           "CraftHammer_Wood",
                DisplayName:  "Craft Hammer (Wood)",
                Description:  "Carve 2 Wood logs into a Wood Mallet — for delicate framing.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Hammer", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    340,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     120
            ),
            new RecipeDef(
                Id:           "CraftHammer_Bone",
                DisplayName:  "Craft Hammer (Bone)",
                Description:  "Lash a Bone Fragment to a 1 Wood log to make a Bone Hammer.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Hammer", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    380,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     140
            ),

            // ── Sickle (2 variants: Stone / Bone — need sharp edge) ────
            new RecipeDef(
                Id:           "CraftSickle_Stone",
                DisplayName:  "Craft Sickle (Stone)",
                Description:  "Chip a Stone block into a curved blade, lash to a Wood handle. Speeds up Cut/Chop tasks.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Sickle", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     160
            ),
            new RecipeDef(
                Id:           "CraftSickle_Bone",
                DisplayName:  "Craft Sickle (Bone)",
                Description:  "Sharpen a Bone Fragment into a sickle blade with a Wood handle.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Sickle", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     150
            ),

            // ── Focus (2 variants: Wood / Crystal) ─────────────────────
            new RecipeDef(
                Id:           "CraftFocus_Wood",
                DisplayName:  "Craft Sage Staff (Wood)",
                Description:  "Carve a Wood Sage Staff around 1 Raw Essence. Used by Sages for Attune/Meditate.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                    new RecipeIngredient(ItemKind.Magic, "Magic", 1, RequiredSubType: "RawEssence"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Focus", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                SecondarySkill: "Magic",
                SecondaryMinimum: 1,
                XpReward:     180
            ),
            new RecipeDef(
                Id:           "CraftFocus_Crystal",
                DisplayName:  "Craft Sage Staff (Crystal)",
                Description:  "Set a Crystal Shard into a Stone mount for a high-tier Sage Staff.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Magic, "Magic", 1, RequiredSubType: "CrystalShard"),
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Focus", "Magic", "Crystal", 1, RollQuality: true),
                },
                WorkTicks:    660,
                PrimarySkill: "Crafting",
                SkillMinimum: 5,
                SecondarySkill: "Magic",
                SecondaryMinimum: 2,
                XpReward:     220
            ),

            // ── Basket (2 variants: Wood / Cloth) ──────────────────────
            new RecipeDef(
                Id:           "CraftBasket_Wood",
                DisplayName:  "Craft Basket (Wood)",
                Description:  "Weave 4 Grass Cuttings into a Basket for hauling food. Speeds up Gather Food.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Plant", 4, RequiredSubType: "Cuttings"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Basket", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    280,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     100
            ),
            new RecipeDef(
                Id:           "CraftBasket_Cloth",
                DisplayName:  "Craft Basket (Cloth)",
                Description:  "Weave 2 Moss Cloth into a sturdier cloth-lined Basket.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Cloth", 2, RequiredSubType: "Mossleaf"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Tool, "Basket", "Cloth", "Mossleaf", 1, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     110
            ),

            // ── Spear (3 variants: Wood / Stone-tipped / Bone-tipped) ──
            new RecipeDef(
                Id:           "CraftSpear_Wood",
                DisplayName:  "Craft Spear (Wood)",
                Description:  "Sharpen 2 Wood logs into a Wood Spear — basic Guardian weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Spear", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     150
            ),
            new RecipeDef(
                Id:           "CraftSpear_Stone",
                DisplayName:  "Craft Spear (Stone-tipped)",
                Description:  "Knap a Stone tip onto a Wood haft — sharper, deadlier Spear.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Spear", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     180
            ),
            new RecipeDef(
                Id:           "CraftSpear_Bone",
                DisplayName:  "Craft Spear (Bone-tipped)",
                Description:  "Lash a Bone Fragment tip to a Wood haft — light, fast Spear.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Spear", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     170
            ),

            // ── Club (3 variants: Wood / Stone / Bone) ─────────────────
            new RecipeDef(
                Id:           "CraftClub_Wood",
                DisplayName:  "Craft Club (Wood)",
                Description:  "Shape 2 Wood logs into a Wood Club — simple blunt weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Club", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    340,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     120
            ),
            new RecipeDef(
                Id:           "CraftClub_Stone",
                DisplayName:  "Craft Club (Stone)",
                Description:  "Lash a Stone head to a Wood haft — heavy Stone Club, more damage.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Club", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     150
            ),
            new RecipeDef(
                Id:           "CraftClub_Bone",
                DisplayName:  "Craft Club (Bone)",
                Description:  "Carve a Bone Fragment into a knobbed cudgel.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Club", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    380,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     140
            ),

            // ── Sling (2 variants: Moss Cloth / Grass Linen) ───────────
            new RecipeDef(
                Id:           "CraftSling_Moss",
                DisplayName:  "Craft Sling (Moss Cloth)",
                Description:  "Braid 2 Moss Cloth into a Sling — ranged weapon for hunters.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Cloth", 2, RequiredSubType: "Mossleaf"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Sling", "Cloth", "Mossleaf", 1, RollQuality: true),
                },
                WorkTicks:    360,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     140
            ),
            new RecipeDef(
                Id:           "CraftSling_Linen",
                DisplayName:  "Craft Sling (Grass Linen)",
                Description:  "Braid 2 Grass Linen into a lighter, lower-quality Sling.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Cloth", 2, RequiredSubType: "Grass"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Sling", "Cloth", "Grass", 1, RollQuality: true),
                },
                WorkTicks:    340,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     120
            ),

            // ────────────────────────────────────────────────────────────────
            // v0.5.84t EXTENDED WEAPON RECIPES (13 entries)
            // Sam: "Add bows, crossbows, and atlatls (spear throwers) into the
            // game as well as craftable/spawnable ranged weapons. We also want
            // to add swords, axes, and shields."
            // ────────────────────────────────────────────────────────────────

            // ── Bow (2 variants: Wood / Bone) ──────────────────────────
            new RecipeDef(
                Id:           "CraftBow_Wood",
                DisplayName:  "Craft Bow (Wood)",
                Description:  "Bend a Wood stave and string it with Cuttings — a ranged hunter's weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 2, RequiredSubType: "DeadWood"),
                    new RecipeIngredient(ItemKind.Material, "Plant", 2, RequiredSubType: "Cuttings"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Bow", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    600,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     200
            ),
            new RecipeDef(
                Id:           "CraftBow_Bone",
                DisplayName:  "Craft Bow (Bone-laminated)",
                Description:  "Laminate Bone Fragments to a Wood core for a sturdier, harder-hitting Bow.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                    new RecipeIngredient(ItemKind.Material, "Plant", 2, RequiredSubType: "Cuttings"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Bow", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    720,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     240
            ),

            // ── Crossbow (1 variant — complex; Wood frame + Stone trigger) ──
            new RecipeDef(
                Id:           "CraftCrossbow",
                DisplayName:  "Craft Crossbow",
                Description:  "Assemble a Wood frame, Stone trigger, and Cuttings string — slow but devastating ranged weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 3, RequiredSubType: "DeadWood"),
                    new RecipeIngredient(ItemKind.Material, "Stone", 1),
                    new RecipeIngredient(ItemKind.Material, "Plant", 2, RequiredSubType: "Cuttings"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Crossbow", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    900,
                PrimarySkill: "Crafting",
                SkillMinimum: 5,
                XpReward:     320
            ),

            // ── Atlatl (2 variants: Wood / Bone — simple spear thrower) ──
            new RecipeDef(
                Id:           "CraftAtlatl_Wood",
                DisplayName:  "Craft Atlatl (Wood)",
                Description:  "Carve a Wood spear-thrower — primitive lever weapon that hurls spears farther.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Atlatl", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    300,
                PrimarySkill: "Crafting",
                SkillMinimum: 1,
                XpReward:     110
            ),
            new RecipeDef(
                Id:           "CraftAtlatl_Bone",
                DisplayName:  "Craft Atlatl (Bone)",
                Description:  "Carve a Bone Fragment into a lightweight, springy atlatl.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 1),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Atlatl", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    340,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     130
            ),

            // ── Sword (2 variants: Stone-blade / Bone-blade) ────────────
            new RecipeDef(
                Id:           "CraftSword_Stone",
                DisplayName:  "Craft Sword (Stone)",
                Description:  "Knap 2 Stone blades onto a Wood haft — a balanced melee weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Sword", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    660,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     220
            ),
            new RecipeDef(
                Id:           "CraftSword_Bone",
                DisplayName:  "Craft Sword (Bone)",
                Description:  "Sharpen 2 Bone Fragments into a slashing blade on a Wood haft.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Sword", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    600,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     200
            ),

            // ── Axe (3 variants: Stone / Bone / Wood) ──────────────────
            new RecipeDef(
                Id:           "CraftAxe_Stone",
                DisplayName:  "Craft Axe (Stone)",
                Description:  "Bind a Stone head to a Wood haft — heavy chopping weapon.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Axe", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 3,
                XpReward:     180
            ),
            new RecipeDef(
                Id:           "CraftAxe_Bone",
                DisplayName:  "Craft Axe (Bone)",
                Description:  "Carve a Bone Fragment into an axe head, lashed to a Wood haft.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 1),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Axe", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    480,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     170
            ),
            new RecipeDef(
                Id:           "CraftAxe_Wood",
                DisplayName:  "Craft Axe (Wood)",
                Description:  "Whittle 3 Wood logs into a crude improvised axe — soft head, modest damage.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 3, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Weapon, "Axe", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     140
            ),

            // ── Shield (3 variants: Wood / Stone / Bone) ───────────────
            new RecipeDef(
                Id:           "CraftShield_Wood",
                DisplayName:  "Craft Shield (Wood)",
                Description:  "Plank 3 Wood logs into a Wood Shield — basic off-hand defence.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Wood", 3, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Apparel, "Shield", "Wood", "DeadWood", 1, RollQuality: true),
                },
                WorkTicks:    420,
                PrimarySkill: "Crafting",
                SkillMinimum: 2,
                XpReward:     150
            ),
            new RecipeDef(
                Id:           "CraftShield_Stone",
                DisplayName:  "Craft Shield (Stone-faced)",
                Description:  "Mount Stone plating to a Wood frame — heavy but very effective Shield.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Stone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Apparel, "Shield", "Stone", "Granite", 1, RollQuality: true),
                },
                WorkTicks:    600,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     200
            ),
            new RecipeDef(
                Id:           "CraftShield_Bone",
                DisplayName:  "Craft Shield (Bone-laminated)",
                Description:  "Bind Bone Fragments to a Wood frame for a lighter, magic-resistant Shield.",
                Ingredients:  new[] {
                    new RecipeIngredient(ItemKind.Material, "Bone", 2),
                    new RecipeIngredient(ItemKind.Material, "Wood", 1, RequiredSubType: "DeadWood"),
                },
                Outputs:      new[] {
                    new RecipeOutput(ItemKind.Apparel, "Shield", "Bone", "Bone", 1, RollQuality: true),
                },
                WorkTicks:    540,
                PrimarySkill: "Crafting",
                SkillMinimum: 4,
                XpReward:     180
            ),
        };

        private static readonly Dictionary<string, RecipeDef> _byId;

        static RecipeRegistry()
        {
            _byId = new Dictionary<string, RecipeDef>(All.Length);
            foreach (var r in All) _byId[r.Id] = r;
        }

        public static RecipeDef? Get(string id) =>
            _byId.TryGetValue(id, out var r) ? r : null;
    }
}
