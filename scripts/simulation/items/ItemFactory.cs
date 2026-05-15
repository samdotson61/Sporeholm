using System;
using System.Collections.Generic;

namespace SmurfulationC.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — procedural item generation. Every new item
    // — from a gathered Smurfberry through a scenario-rolled Masterwork
    // Pick — flows through this single entry point so quality rolls,
    // material rolls, condition seeding, and age stamps stay consistent.
    //
    // The flow per the Phase 4 spec:
    //   1. Resolve sub-type def (Kind + SubType → ItemSubTypeDef).
    //   2. Resolve / roll material — caller may pass an explicit material
    //      (a Forager picking a Smurfberry knows the Plant/Smurfberry
    //      material), or pass null to let the factory roll from the
    //      sub-type's AllowedFamilies list.
    //   3. Roll quality — bell-curve centred on Normal; skill nudges
    //      it upward when supplied (Phase 5+ Crafter integration).
    //   4. Seed Condition = DurabilityCap with a small randomised dip
    //      so brand-new items don't all read exactly 100.
    //   5. Stamp BirthTick from SimulationCore.GlobalTick.
    public static class ItemFactory
    {
        // Quality probability table — no skill modifier. Crude / Normal /
        // Fine / Superior / Masterwork ~= 15 / 60 / 20 / 4 / 1 %.
        // Legendary is never rolled at creation (storyteller-awarded).
        private static readonly (Quality Q, double Pct)[] DefaultQualityTable =
        {
            (Quality.Crude,      0.15),
            (Quality.Normal,     0.60),
            (Quality.Fine,       0.20),
            (Quality.Superior,   0.04),
            (Quality.Masterwork, 0.01),
        };

        // Create a single item. Quantity defaults to 1; pass quantity > 1
        // to start a stack at construction (used for scenario starting
        // items where the player allocates "1 week of food rations").
        // skillLevel (0–20) shifts the quality bell upward — every 5
        // skill bumps the average roll by ~one tier. Pass 0 for "no
        // crafter, just nature."
        public static Item Create(
            ItemKind kind,
            string subType,
            MaterialKey? material,
            Random rng,
            long globalTick,
            int skillLevel = 0,
            int quantity   = 1)
        {
            var def = ItemRegistry.Get(kind, subType);
            if (def == null)
            {
                // Caller asked for an unknown sub-type. Return a stub so
                // the game keeps running; log via Godot for visibility.
                Godot.GD.PushWarning($"ItemFactory: unknown sub-type {kind}/{subType}");
                return new Item
                {
                    Kind = kind, SubType = subType, Quantity = quantity,
                    AvgBirthTick = globalTick,
                };
            }

            var mat = material ?? RollMaterial(def, rng);
            var matDef = MaterialRegistry.Get(mat);

            float durabilityMul = matDef?.DurabilityMul ?? 1f;
            float durability    = def.BaseDurability * durabilityMul;

            var q = RollQuality(rng, skillLevel);

            // Condition seeded near full, lightly randomised so a fresh
            // gather doesn't always print exactly 100. Tools land at
            // 90–100; food at 95–100 (about-to-decay anyway).
            float condition = durability * (float)(0.92 + rng.NextDouble() * 0.08);

            return new Item
            {
                Kind          = kind,
                SubType       = subType,
                Material      = mat,
                Quality       = q,
                State         = ItemState.Fresh,
                AvgCondition  = condition,
                DurabilityCap = durability,
                AvgBirthTick  = globalTick,
                Quantity      = quantity,
            };
        }

        // Picks a random material respecting the sub-type's AllowedFamilies.
        // If AllowedFamilies is empty, picks from every registered material.
        public static MaterialKey RollMaterial(ItemSubTypeDef def, Random rng)
        {
            if (def.AllowedFamilies.Length > 0)
            {
                string fam = def.AllowedFamilies[rng.Next(def.AllowedFamilies.Length)];
                var pick = MaterialRegistry.RollInFamily(fam, rng);
                if (pick != null) return pick.Key;
            }
            // Fallback: any material in the registry.
            var all = MaterialRegistry.All;
            return all[rng.Next(all.Length)].Key;
        }

        // Quality bell roll. skillLevel 0 hits the DefaultQualityTable
        // directly; every +5 skill shifts probability mass one tier up
        // (Crude → Normal → Fine → Superior → Masterwork) via a single
        // running offset, so a Crafter at skill 20 lands Masterworks far
        // more often than at skill 0. This is the canonical RimWorld /
        // DF formula simplified to one knob.
        public static Quality RollQuality(Random rng, int skillLevel = 0)
        {
            int shift = skillLevel / 5;        // 0, 1, 2, 3, 4 at skill 0..20
            double roll = rng.NextDouble();
            double cum = 0;
            for (int i = 0; i < DefaultQualityTable.Length; i++)
            {
                cum += DefaultQualityTable[i].Pct;
                if (roll < cum)
                {
                    int idx = i + shift;
                    if (idx >= DefaultQualityTable.Length) idx = DefaultQualityTable.Length - 1;
                    return DefaultQualityTable[idx].Q;
                }
            }
            return Quality.Normal;
        }

        // Maps a VegetationType (sim-thread harvest target) to the
        // (sub-type, material) pair the gather task should drop.
        //
        // v0.4.2 — taxonomy collapsed to four food sub-types
        // (Smurfberry / SmallMushroom / HerbCluster / MagicBerry). The
        // material axis carries the actual variant a player sees in the
        // breakdown — a Pineshroom harvest is a Plant/SmallMushroom
        // family with Plant/SmallMushroom material; a sandshroom harvest
        // is a Plant/SmallMushroom with Plant/SmallMushroom material —
        // i.e. functionally identical food but tagged by source. Magic
        // vegetation drops a MagicBerry food item PLUS a separate Magic
        // essence item handled in BehaviorSystem.ApplyTaskEffect.
        public static (string SubType, MaterialKey Material)? FoodFromVegetation(
            SmurfulationC.World.VegetationType v) => v switch
        {
            SmurfulationC.World.VegetationType.SmurfberryBush  => ("Smurfberry",    new MaterialKey("Plant","Smurfberry")),
            SmurfulationC.World.VegetationType.SmallMushroom   => ("SmallMushroom", new MaterialKey("Plant","SmallMushroom")),
            SmurfulationC.World.VegetationType.LargeMushroom   => ("SmallMushroom", new MaterialKey("Plant","LargeMushroom")),
            SmurfulationC.World.VegetationType.HerbCluster     => ("HerbCluster",   new MaterialKey("Plant","HerbCluster")),
            SmurfulationC.World.VegetationType.MagicFlower     => ("MagicBerry",    new MaterialKey("Plant","MagicBerry")),
            SmurfulationC.World.VegetationType.PineShroom      => ("SmallMushroom", new MaterialKey("Plant","SmallMushroom")),
            SmurfulationC.World.VegetationType.PalmShroom      => ("SmallMushroom", new MaterialKey("Plant","LargeMushroom")),
            SmurfulationC.World.VegetationType.SmallSandshroom => ("SmallMushroom", new MaterialKey("Plant","SmallMushroom")),
            SmurfulationC.World.VegetationType.LargeSandshroom => ("SmallMushroom", new MaterialKey("Plant","LargeMushroom")),
            _ => null,
        };

        // v0.4.2 — does this vegetation type also drop a Magic essence
        // alongside its food yield? Per the player brief: "magic plants
        // give both essence and food". MagicFlower (the canon "magic
        // grove" plant) is the only one today; HerbCluster could be
        // added later if Phase 4 wants a secondary magic source.
        public static bool VegetationYieldsMagicEssence(SmurfulationC.World.VegetationType v) =>
            v == SmurfulationC.World.VegetationType.MagicFlower;

        // Maps a VegetationType to a wood (Material, SubType) for the
        // ChopWood task. Material family is always Wood; the specific
        // sub-material depends on the vegetation source.
        //
        // v0.4.2 — every LargeMushroom variant (LargeMushroom,
        // LargeSandshroom, PalmShroom) drops Wood/Fungal. The old
        // Wood/Palm material is removed entirely. SmallMushroom variants
        // are too small to chop for wood and route through Cut instead
        // (producing Cuttings).
        public static MaterialKey WoodFromVegetation(SmurfulationC.World.VegetationType v) => v switch
        {
            SmurfulationC.World.VegetationType.LargeMushroom   => new MaterialKey("Wood","Fungal"),
            SmurfulationC.World.VegetationType.LargeSandshroom => new MaterialKey("Wood","Fungal"),
            SmurfulationC.World.VegetationType.PalmShroom      => new MaterialKey("Wood","Fungal"),
            _ => new MaterialKey("Wood","Fungal"),
        };

        // Maps a TerrainType excavation source to the resulting material.
        //
        // v0.4.2 — DeadLog → Wood/DeadWood, LivingWood → Wood/LivingWood
        // (replaces the v0.3.x Oak / Pine placeholders). Boulder still
        // rolls a sub-stone in the Stone family; the specific stone the
        // tile holds is read from LocalMap.GetTileStone() (set at
        // generation time) when available, otherwise rolled uniformly.
        public static (ItemKind Kind, string SubType, MaterialKey Material) MaterialFromTerrain(
            SmurfulationC.World.TerrainType t, Random rng) => t switch
        {
            SmurfulationC.World.TerrainType.Boulder    =>
                (ItemKind.Material, "StoneBlock",
                 MaterialRegistry.RollInFamily("Stone", rng)?.Key ?? new MaterialKey("Stone","Granite")),
            SmurfulationC.World.TerrainType.DeadLog    =>
                (ItemKind.Material, "WoodLog",   new MaterialKey("Wood","DeadWood")),
            SmurfulationC.World.TerrainType.LivingWood =>
                (ItemKind.Material, "WoodLog",   new MaterialKey("Wood","LivingWood")),
            _ =>
                (ItemKind.Material, "StoneBlock", new MaterialKey("Stone","Granite")),
        };

        // v0.4.2 — Cut Plants drop a Cuttings stack. Quantity scales with
        // the original plant's BaseYield so cutting a SmurfberryBush
        // gives more compostable biomass than cutting a single Small
        // Mushroom. Compostable (Phase 5+ fertiliser) and Phase 7
        // flammable kindling.
        public static (string SubType, MaterialKey Material, int Quantity) CuttingsFromVegetation(
            SmurfulationC.World.VegetationType v) => v switch
        {
            SmurfulationC.World.VegetationType.LargeMushroom   => ("Cuttings", new MaterialKey("Plant","Cuttings"), 3),
            SmurfulationC.World.VegetationType.LargeSandshroom => ("Cuttings", new MaterialKey("Plant","Cuttings"), 3),
            SmurfulationC.World.VegetationType.PalmShroom      => ("Cuttings", new MaterialKey("Plant","Cuttings"), 2),
            SmurfulationC.World.VegetationType.SmurfberryBush  => ("Cuttings", new MaterialKey("Plant","Cuttings"), 2),
            SmurfulationC.World.VegetationType.HerbCluster     => ("Cuttings", new MaterialKey("Plant","Cuttings"), 1),
            _ => ("Cuttings", new MaterialKey("Plant","Cuttings"), 1),
        };

        // v0.3.47 (Phase 4 sub-B) — role-based starting kit.
        //
        // Rolls a small bundle of starting items per smurf, sized for ~1 week
        // of personal supply. Replaces the v0.3.x "scenarios start with an
        // empty colony pool" behaviour spec'd in roadmap §4 Procedural
        // Starting Items. Budget allocation follows the 8-point spec:
        //   - 4 Food items (1 pt each)
        //   - 1-2 Materials (1 pt each)  → role: Crafter gets 2, others get 1
        //   - 1 Tool (2 pts)
        //   - 1 Trinket (1 pt)             → light flavour item
        //
        // Total: ~7-8 points. Trader / scenario-screen budget allocation UI
        // (per-category sliders + reroll-per-item) is queued for a future
        // patch; for v0.3.47 every smurf gets a balanced kit.
        public static List<Item> RollStartingKit(string role, Random rng, long globalTick)
        {
            var kit = new List<Item>();

            // 4 Food items, one of each major raw type.
            string[] foodPool = { "Smurfberry", "SmallMushroom", "HerbCluster", "Pineshroom" };
            for (int i = 0; i < 4; i++)
            {
                var sub = foodPool[i % foodPool.Length];
                kit.Add(Create(ItemKind.Food, sub, null, rng, globalTick, skillLevel: 0, quantity: 7));
            }

            // Material — 1 stack of Stone + (Crafter) 1 stack of Wood.
            kit.Add(Create(ItemKind.Material, "StoneBlock", null, rng, globalTick, skillLevel: 0, quantity: 4));
            if (role == "Crafter")
                kit.Add(Create(ItemKind.Material, "WoodLog", null, rng, globalTick, skillLevel: 0, quantity: 4));

            // Tool — role-appropriate.
            string toolSub = role switch
            {
                "Crafter"   => "Hammer",
                "Forager"   => "Basket",
                "Mage"      => "Focus",
                "Caretaker" => "Basket",
                "Guardian"  => "Hammer",
                "Scholar"   => "Focus",
                "Elder"     => "Focus",
                _           => "Basket",
            };
            kit.Add(Create(ItemKind.Tool, toolSub, null, rng, globalTick, skillLevel: 0, quantity: 1));

            // Trinket flavour item — 30 % chance to roll one.
            if (rng.NextDouble() < 0.30)
            {
                string trinketSub = rng.Next(2) == 0 ? "Pendant" : "Ring";
                kit.Add(Create(ItemKind.Trinket, trinketSub, null, rng, globalTick, skillLevel: 0, quantity: 1));
            }

            // v0.4.64 (E5 from rimport.md) — role-flavoured bonus item.
            // The base kit (food + stone + tool ± wood) already varies by
            // role (Crafter wood, role-specific tool), but Guardians had
            // no actual weapon and Mages/Scholars had no extra magic
            // resource to start research with — meaningful gameplay
            // beats since v0.4.30. Adds one role-canonical item per
            // smurf without changing the base kit.
            switch (role)
            {
                case "Guardian":
                    kit.Add(Create(ItemKind.Weapon, "Spear", null, rng, globalTick, skillLevel: 1, quantity: 1));
                    break;
                case "Mage":
                    kit.Add(Create(ItemKind.Magic, "RawEssence",
                        new MaterialKey("Magic","RawEssence"), rng, globalTick, skillLevel: 0, quantity: 5));
                    break;
                case "Caretaker":
                    kit.Add(Create(ItemKind.Food, "HerbCluster", null, rng, globalTick, skillLevel: 0, quantity: 4));
                    break;
                case "Scholar":
                    kit.Add(Create(ItemKind.Magic, "RawEssence",
                        new MaterialKey("Magic","RawEssence"), rng, globalTick, skillLevel: 0, quantity: 3));
                    break;
                // Crafter, Forager, Elder, Unassigned: no extra bonus —
                // their base kit already covers the role intent.
            }

            return kit;
        }
    }
}
