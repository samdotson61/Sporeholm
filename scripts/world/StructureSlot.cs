using Sporeholm.Simulation.Items;

namespace Sporeholm.World
{
    // v0.5.19 (Phase 5B foundation — Sporeholm_Roadmap_2026.md §5.1).
    // Per-tile structure state stored in the parallel StructureSlot[,]
    // array on LocalMap. Same pattern as VegetationSlot: every tile has
    // a slot (mostly Type=None) and the renderer / pathfinder consult it
    // alongside Terrain to determine what's actually on the tile.
    //
    // Three structure states matter at this version:
    //
    //   Blueprint  — designation flag. Player painted this tile as a
    //                planned wall / floor. No physical impact: terrain
    //                stays passable, shroomps can walk through it. The
    //                BuildSystem looks for blueprints and assigns Build
    //                tasks. Visually shown as a translucent ghost.
    //   Frame      — construction in progress. After the Crafter delivers
    //                materials, the blueprint becomes a frame and accumulates
    //                BuildProgress (0-100). Walls remain passable while
    //                frame; floors stay passable always. Visually shown
    //                with a partial-fill ghost + numeric percent.
    //   Built      — completed. Walls become impassable (Terrain=Wall set
    //                via the existing `_passable[idx]` recompute). Floors
    //                stay passable but render with the chosen StructureMat
    //                colour. Doors / Furniture follow in v0.5.20+.
    //
    // RoomId stays at 0 (unassigned) until v0.5.20's RoomDetector lands —
    // included as a field now so save/load doesn't need a schema bump.
    public struct StructureSlot
    {
        public StructureType  Type;          // None / WallPlanned / Wall / FloorPlanned / Floor / etc.
        public StructureMat   Material;      // FungalWood / DeadWood / LivingWood / Stone / etc.
        // v0.5.30 — widened from byte to ushort. The new SkillCurve-driven
        // build cadence increments BuildProgress per tick by `10 ×
        // ConstructionSpeedFactor(skill)` (≈ 3 at lvl 0, 10 at lvl 8,
        // 20 at lvl 20). Threshold raised to BuildProgressTarget = 600 so
        // a level-0 novice takes ~3-5 sec per wall while a level-20 master
        // finishes in ~0.5 sec — the ~10× spread Sam asked for. byte (max
        // 255) couldn't hold the higher target without per-tick rounding
        // artefacts at low skill (3 × ⌊⌋ = 3 at lvl 0, 10 at lvl 8 — fine
        // — but tuning headroom for future material/quality multipliers
        // wanted more bits).
        public ushort         BuildProgress; // 0 = blueprint, 600 = complete
        public ushort         RoomId;        // 0 = no room (Phase 5C v0.5.20)
        // v0.5.34 — haul-to-site delivery counter. The Build task now
        // delivers materials one unit per tick (consuming from colony
        // Inventory) instead of atomically deducting the whole cost on
        // first arrival. MaterialsDelivered must reach
        // BuildMaterialCost(Type) before BuildProgress starts ticking;
        // the blueprint visibly progresses through delivery → frame →
        // built rather than skipping straight to frame. A v0.6 polish
        // pass will add the physical-haul-from-stockpile leg so a
        // Crafter actually walks to a Wood/Stone tile and picks up
        // before walking to the blueprint.
        public byte           MaterialsDelivered;
        // v0.5.30 — Quality rolled at Frame → Built completion via
        // SkillCurve.RollStructureQuality. Re-uses the existing
        // Items.Quality enum (Crude/Normal/Fine/Superior/Masterwork/
        // Legendary). Drives BeautyScore + future bed RestEffectiveness +
        // any Quality-keyed display in TilePropertiesPanel.
        public Quality        Quality;       // Default Normal (rolled at completion)

        // v0.5.84c — floor underneath. Sam: "Furniture and its blueprints
        // should also appear/be able to be built on top of flooring for
        // gameplay purposes." Pre-fix, placing furniture (Shelf/Workbench/
        // Bed/etc.) on an existing Floor tile overwrote the Floor slot
        // and the floor visually disappeared. Now Floor info is preserved
        // in this second material field whenever a non-floor structure
        // sits on a previously-floored tile. The renderer emits a floor
        // instance (using FloorBeneath as the material tint) before
        // emitting the main Type, producing the floor-under-furniture
        // visual stack. Removal of the furniture restores Floor as Type.
        // HasFloorBeneath is the live flag — using a separate bool rather
        // than overloading FloorBeneath=Wood-as-sentinel so a legitimate
        // legacy Wood-floor underneath stays distinguishable from no-floor.
        public StructureMat   FloorBeneath;
        public bool           HasFloorBeneath;

        // v0.5.30 — universal build target (work units). Per-structure-type
        // multipliers (e.g. Wall × 1, Floor × 0.4) live in the Build apply
        // path so different structures take proportionally different work
        // amounts. Walls feel substantial; floors snap down quickly.
        public const int BuildProgressTarget = 600;

        // v0.5.34 — material cost per structure type (in Wood/Stone units).
        // Walls cost 4, doors / shelves / workbenches / hearths cost 2,
        // floors cost 1 — matches the v0.5.19 Build consume table.
        public static byte BuildMaterialCost(StructureType type) => type switch
        {
            StructureType.WallPlanned       or StructureType.Wall      => 4,
            StructureType.DoorPlanned       or StructureType.Door      => 2,
            StructureType.ShelfPlanned      or StructureType.Shelf     => 2,
            StructureType.WorkbenchPlanned  or StructureType.Workbench => 2,
            StructureType.HearthPlanned     or StructureType.Hearth    => 2,
            // v0.5.84t — Torch: 1 wood unit. Cheapest furniture.
            StructureType.TorchPlanned      or StructureType.Torch     => 1,
            // v0.5.35 — Beds are 3-unit structures (RimWorld bed cost ≈ 45
            // wood; we use a simplified per-tile-cost so colony bookkeeping
            // stays consistent with the Wall=4 anchor).
            StructureType.BedPlanned        or StructureType.Bed       => 3,
            // v0.5.36 — Joy furniture all cost 2 units (modest investment).
            StructureType.MeditationShrinePlanned or StructureType.MeditationShrine => 2,
            StructureType.ShroomBoardPlanned      or StructureType.ShroomBoard      => 2,
            StructureType.GossipBenchPlanned      or StructureType.GossipBench      => 2,
            // v0.5.37 — Tables: 2 units (4 chairs nearby is implicit).
            StructureType.TablePlanned      or StructureType.Table     => 2,
            StructureType.FloorPlanned      or StructureType.Floor     => 1,
            _ => 1,
        };

        public bool IsPresent  => Type != StructureType.None;
        public bool IsBlueprint => Type == StructureType.WallPlanned
                                || Type == StructureType.FloorPlanned
                                || Type == StructureType.DoorPlanned    // v0.5.20
                                || Type == StructureType.ShelfPlanned   // v0.5.21
                                || Type == StructureType.WorkbenchPlanned   // v0.5.22
                                || Type == StructureType.HearthPlanned      // v0.5.24
                                || Type == StructureType.BedPlanned         // v0.5.35
                                || Type == StructureType.MeditationShrinePlanned    // v0.5.36
                                || Type == StructureType.ShroomBoardPlanned         // v0.5.36
                                || Type == StructureType.GossipBenchPlanned         // v0.5.36
                                || Type == StructureType.TablePlanned              // v0.5.37
                                || Type == StructureType.TorchPlanned;             // v0.5.84t
        public bool IsBuilt    => Type == StructureType.Wall
                               || Type == StructureType.Floor
                               || Type == StructureType.Door            // v0.5.20
                               || Type == StructureType.Shelf           // v0.5.21
                               || Type == StructureType.Workbench       // v0.5.22
                               || Type == StructureType.Hearth          // v0.5.24
                               || Type == StructureType.Bed             // v0.5.35
                               || Type == StructureType.MeditationShrine            // v0.5.36
                               || Type == StructureType.ShroomBoard                 // v0.5.36
                               || Type == StructureType.GossipBench                 // v0.5.36
                               || Type == StructureType.Table                      // v0.5.37
                               || Type == StructureType.Torch;                     // v0.5.84t
        public bool IsImpassable => Type == StructureType.Wall;   // Floors / Doors / Shelves are passable

        public static StructureSlot Empty => default;

        public static StructureSlot Blueprint(StructureType plannedType, StructureMat mat) => new()
        {
            Type           = plannedType,
            Material       = mat,
            BuildProgress  = 0,
            RoomId         = 0,
        };
    }

    // v0.5.19 — structure types. Wall + Floor are the two Phase 5B
    // primitives; Door / Furniture / Workbench / Hearth / etc. land in
    // v0.5.20-v0.5.24 as the rest of Phase 5 ships.
    public enum StructureType : byte
    {
        None          = 0,
        WallPlanned   = 1,   // blueprint, walkable, awaiting Crafter
        Wall          = 2,   // built, impassable
        FloorPlanned  = 3,   // blueprint, walkable, awaiting Crafter
        Floor         = 4,   // built, walkable + cosmetic
        // v0.5.20 Phase 5C — passable to shroomps (slight cost), blocks
        // line-of-sight (Phase 7 combat will use that). For v0.5.20 the
        // door is purely passable terrain — same passability as Floor —
        // but tagged distinctly so future tiers can add open/closed
        // animation, owner-restriction, etc.
        DoorPlanned   = 5,
        Door          = 6,
        // v0.5.21 Phase 5D — Furniture (Shelf, Chest, Workbench, Hearth, etc.)
        // shipped as separate StructureType values per furniture kind so
        // the renderer can pick distinct sprites without a sub-enum.
        ShelfPlanned  = 10,
        Shelf         = 11,
        // v0.5.22 (Phase 5E) — Workbench. Crafters use workbenches to run
        // bills (Cook is the first wired recipe). Future Phase 5E polish:
        // per-workbench Bill queue UI, ingredient filters, repeat modes.
        WorkbenchPlanned = 12,
        Workbench        = 13,
        // v0.5.24 (Phase 5G) — Hearth. Heat source for room temperature
        // simulation + cooking-quality bonus when adjacent to workbench.
        HearthPlanned    = 14,
        Hearth           = 15,
        // v0.5.35 (Phase 5 arc) — Bed. Sleep target for the Sleep task;
        // 1.0× RestEffectiveness vs 0.8× for sleeping on the ground.
        // Triggers WellRested mood thought on wake (vs SleptOnGround).
        BedPlanned       = 16,
        Bed              = 17,
        // v0.5.36 (Phase 5 arc) — Joy / recreation furniture. Three
        // starter structures, one per Joy category:
        //   MeditationShrine — Solitary (silent contemplation)
        //   ShroomBoard      — Cerebral (mushroom-themed board game)
        //   GossipBench      — Social   (two-shroomp chat-encouraging seat)
        MeditationShrinePlanned = 18,
        MeditationShrine        = 19,
        ShroomBoardPlanned      = 20,
        ShroomBoard             = 21,
        GossipBenchPlanned      = 22,
        GossipBench             = 23,
        // v0.5.37 (Phase 5 arc) — Table. Shroomps prefer to eat at tables;
        // eating without a table triggers AteWithoutTable mood penalty.
        TablePlanned     = 24,
        Table            = 25,
        // v0.5.84t — Torch. Buildable on any floor tile. Smaller than a
        // Hearth (+2°C per torch vs +10°C/Hearth), no cooking-quality bonus,
        // but cheap (1 wood unit) so the player can scatter them through
        // rooms + corridors for ambience + warmth. Light emission is a
        // stub today — Room.TorchCount tracks for the future glow grid;
        // visual is an animated flame sprite (no scene-tinting yet).
        TorchPlanned     = 26,
        Torch            = 27,
    }

    // v0.5.19 — structure material. Mirrors the v0.5.16 MaterialKey families
    // but as a flat enum because StructureSlot is a value-type stored in a
    // dense per-tile array (~12k tiles default; using the full MaterialKey
    // string-pair would inflate slot size). The Crafter task picks whichever
    // material the colony has more of at construction time, keeping the
    // player-side mental model simple ("we have lots of stone, use stone").
    public enum StructureMat : byte
    {
        Wood          = 0,   // legacy generic Wood (pre-v0.5.32 default; behaves as DeadWood)
        Stone         = 1,   // generic Stone (granite-grey baseline)
        // v0.5.32 (Phase 5 Stuff system) — concrete wood sub-materials so
        // the player can pick "Fungal Wood Wall" vs "Dead Wood Wall" vs
        // "Living Wood Wall" at blueprint placement time, and v0.5.33's
        // per-material colour tint can pick the right palette.
        // Sam: "buildings allowing choices of what material to make them
        // from (fungal wood, deadwood, livingwood, stone, etc.) with
        // different textures for each."
        FungalWood    = 2,   // purple-tinted; from LargeMushroom / PalmShroom / etc.
        DeadWood      = 3,   // brown; from DeadLog terrain
        LivingWood    = 4,   // green-tinted; from LivingWood terrain
        // v0.5.70 — per-stone build subtypes (Sam: "allow stone walls to be
        // made from the player's choice of material like rimworld, so allow
        // 'obsidian wall', 'quartz wall', 'granite wall', etc."). Each
        // bridges to a matching MaterialRegistry Stone/<SubType> via
        // StructureMatMeta.ConsumeSubType so build strictly consumes the
        // right stone-block stack. Pre-v0.5.70 generic Stone was the only
        // option and the brick sprite was tinted with a single grey colour.
        Granite       = 5,
        Limestone     = 6,
        Marble        = 7,
        Obsidian      = 8,
        Quartz        = 9,
        // v0.5.84t — Pebblestone: cobblestone-like refined-stone build
        // material produced by the "Refine *Material* Pebblestone" recipes
        // (RecipeRegistry: Pebblestone_Granite / _Limestone / _Marble /
        // _Obsidian / _Quartz). One enum value rather than five so the
        // BuildPanel stays compact — the colour-of-source variation is
        // collapsed into a generic cobblestone tint. The consume pipeline
        // pulls from any "Pebblestone" Item.SubType stack regardless of
        // which stone family produced it (ConsumeItemSubType = "Pebblestone",
        // ConsumeSubType = null = no Material.SubType filter).
        Pebblestone   = 10,
    }

    // v0.5.32 — helpers used by BuildPanel material picker, ConsumeByFamily,
    // and v0.5.33 sprite tinting. Family selects between Wood / Stone for
    // inventory consume; per-material colour drives the visual variant.
    public static class StructureMatMeta
    {
        public static bool IsWoodFamily(StructureMat m) =>
            m == StructureMat.Wood       ||
            m == StructureMat.FungalWood ||
            m == StructureMat.DeadWood   ||
            m == StructureMat.LivingWood;

        // v0.5.70 — generic Stone + 5 stone subtypes (Granite/Limestone/
        // Marble/Obsidian/Quartz) all sit in the Stone build-family.
        // v0.5.84t — Pebblestone joins the stone family.
        public static bool IsStoneFamily(StructureMat m) =>
            m == StructureMat.Stone       ||
            m == StructureMat.Granite     ||
            m == StructureMat.Limestone   ||
            m == StructureMat.Marble      ||
            m == StructureMat.Obsidian    ||
            m == StructureMat.Quartz      ||
            m == StructureMat.Pebblestone;

        // Inventory family token consumed at build time. Wood-family
        // sub-materials all draw from the colony's Wood pool; Stone
        // draws from Stone.
        public static string ConsumeFamily(StructureMat m) =>
            IsWoodFamily(m) ? "Wood" : "Stone";

        // v0.5.43 — strict-consume subtype. When the player picks a
        // wood sub-material (FungalWood / DeadWood / LivingWood), the
        // build must consume logs of that exact subtype — not any wood.
        // v0.5.70 extended to stone subtypes (Granite/Limestone/Marble/
        // Obsidian/Quartz) — picking Obsidian Wall must consume Obsidian
        // stone blocks specifically.
        //
        // Note the FungalWood → "Fungal" mismatch — ItemFactory.WoodFromVegetation
        // uses "Fungal" as the MaterialKey subtype for fungal-shroom-derived
        // logs while the StructureMat enum value is "FungalWood" for
        // player-readable display. The mapping bridges the two.
        public static string? ConsumeSubType(StructureMat m) => m switch
        {
            StructureMat.FungalWood  => "Fungal",
            StructureMat.DeadWood    => "DeadWood",
            StructureMat.LivingWood  => "LivingWood",
            StructureMat.Granite     => "Granite",
            StructureMat.Limestone   => "Limestone",
            StructureMat.Marble      => "Marble",
            StructureMat.Obsidian    => "Obsidian",
            StructureMat.Quartz      => "Quartz",
            // v0.5.84t — Pebblestone accepts ANY stone-family pebblestone
            // (Granite/Limestone/Marble/Obsidian/Quartz). The visible
            // result is a single "Pebblestone" build option — the
            // underlying source-stone variety is intentionally invisible.
            StructureMat.Pebblestone => null,
            _                        => null,   // generic Wood / Stone — no strict subtype
        };

        // v0.5.84t — Item.SubType discriminator. Most StructureMats are
        // satisfied by a single material item (Wood → WoodLog, Stone →
        // StoneBlock). Pebblestone introduces a second Stone-family item
        // subtype ("Pebblestone"), so the build pipeline needs to filter
        // on Item.SubType as well as Material.Family/SubType to avoid
        // accidentally consuming StoneBlocks for a Pebblestone wall (or
        // vice versa). Returning null = no filter (legacy behaviour).
        public static string? ConsumeItemSubType(StructureMat m) => m switch
        {
            StructureMat.Pebblestone => "Pebblestone",
            // Wood family → WoodLog stacks
            StructureMat.Wood        => "WoodLog",
            StructureMat.FungalWood  => "WoodLog",
            StructureMat.DeadWood    => "WoodLog",
            StructureMat.LivingWood  => "WoodLog",
            // Stone family (non-Pebblestone) → StoneBlock stacks
            StructureMat.Stone       => "StoneBlock",
            StructureMat.Granite     => "StoneBlock",
            StructureMat.Limestone   => "StoneBlock",
            StructureMat.Marble      => "StoneBlock",
            StructureMat.Obsidian    => "StoneBlock",
            StructureMat.Quartz      => "StoneBlock",
            _                        => null,
        };

        public static string DisplayName(StructureMat m) => m switch
        {
            StructureMat.FungalWood  => "Fungal Wood",
            StructureMat.DeadWood    => "Dead Wood",
            StructureMat.LivingWood  => "Living Wood",
            StructureMat.Stone       => "Stone",
            StructureMat.Wood        => "Wood",
            StructureMat.Granite     => "Granite",
            StructureMat.Limestone   => "Limestone",
            StructureMat.Marble      => "Marble",
            StructureMat.Obsidian    => "Obsidian",
            StructureMat.Quartz      => "Quartz",
            StructureMat.Pebblestone => "Pebblestone",
            _                        => m.ToString(),
        };

        // v0.5.33 — sprite tint applied to StructureOverlay sprites.
        // RimWorld pattern: identical sprite geometry, per-material
        // colour multiply. Values picked so each material reads at a
        // glance: stone is neutral-grey, dead-wood is warm brown,
        // living-wood is leafy green, fungal-wood is dusk-purple.
        // v0.5.70 — per-stone subtype tints so Granite / Limestone /
        // Marble / Obsidian / Quartz walls read distinctly. Picked to
        // match real-world stone palettes (granite=mottled grey,
        // limestone=cream-tan, marble=white with cool cast, obsidian=
        // near-black volcanic glass, quartz=pale blue-white).
        public static (float r, float g, float b) Tint(StructureMat m) => m switch
        {
            StructureMat.Stone      => (0.78f, 0.78f, 0.80f),   // near-white grey (generic fallback)
            StructureMat.DeadWood   => (0.85f, 0.65f, 0.40f),   // warm tan
            StructureMat.LivingWood => (0.55f, 0.85f, 0.55f),   // leafy green
            StructureMat.FungalWood => (0.85f, 0.78f, 0.72f),   // cream cap; mushroom sprite handles purple spots
            StructureMat.Wood       => (0.80f, 0.60f, 0.35f),   // legacy generic wood ≈ deadwood
            StructureMat.Granite    => (0.70f, 0.68f, 0.66f),   // mottled mid-grey
            StructureMat.Limestone  => (0.92f, 0.88f, 0.74f),   // cream-tan
            StructureMat.Marble     => (0.96f, 0.96f, 0.98f),   // bright white, cool cast
            StructureMat.Obsidian   => (0.22f, 0.22f, 0.30f),   // near-black volcanic glass
            StructureMat.Quartz     => (0.86f, 0.92f, 0.98f),   // pale blue-white
            // v0.5.84t — cobblestone medium-grey with a faint warm cast
            // to read distinct from generic Stone (which is cooler grey).
            StructureMat.Pebblestone => (0.66f, 0.62f, 0.58f),
            _                       => (1.00f, 1.00f, 1.00f),
        };

        // v0.5.84i — material Comfort multiplier. Sam: "fungalwood beds
        // and wood beds should be comfortable while stone beds are less
        // so." Applied multiplicatively to per-material gameplay effects
        // (currently: bed Rest restoration rate; future: GossipBench
        // social gain, Workbench cooking quality bonus, etc.). 1.0 is
        // baseline; > 1 is more comfortable, < 1 less.
        //   FungalWood = 1.05 — slightly cozier than deadwood (the
        //     spongy cap material has a soft give to it).
        //   DeadWood / LivingWood = 1.0 — baseline comfortable wood.
        //   Limestone = 0.78 — porous, slightly warmer than dense stones.
        //   Marble    = 0.85 — polished, the most comfortable stone.
        //   Granite / Quartz = 0.70 — hard, cold.
        //   Obsidian  = 0.65 — sharp + cold, the least comfortable.
        // Legacy generic Stone/Wood fall through to a wood / stone
        // baseline so old saves with those Material values still apply
        // sensible comfort even though they can't be picked any more.
        public static float Comfort(StructureMat m) => m switch
        {
            StructureMat.FungalWood => 1.05f,
            StructureMat.DeadWood   => 1.00f,
            StructureMat.LivingWood => 1.00f,
            StructureMat.Wood       => 1.00f,   // legacy
            StructureMat.Limestone  => 0.78f,
            StructureMat.Marble     => 0.85f,
            StructureMat.Granite    => 0.70f,
            StructureMat.Quartz     => 0.70f,
            StructureMat.Obsidian   => 0.65f,
            StructureMat.Stone      => 0.72f,   // legacy
            // v0.5.84t — pebblestone sits a touch above raw stone; the
            // refined, rounded surface is slightly less harsh than
            // chiselled blocks but still cold.
            StructureMat.Pebblestone => 0.78f,
            _                        => 1.00f,
        };

        // v0.5.84i — material Beauty bonus. Marble + Obsidian + Quartz
        // are the high-end visual materials (RimWorld parity — those
        // are the "premium" stones); Granite + Limestone are workhorses;
        // Wood family is neutral; FungalWood reads as ornamental. Used
        // by the (Phase 5/6) Beauty score on built structures — added
        // to per-tile RoomDetector beauty contribution. Stub-safe for
        // now: code paths can read this without a Beauty system shipped.
        //   Obsidian = 1.4 — striking volcanic black, prestige stone.
        //   Marble   = 1.5 — polished elegance.
        //   Quartz   = 1.3 — crystalline highlights.
        //   FungalWood = 1.2 — distinctive mushroom motif.
        //   LivingWood = 1.1 — vital green.
        //   Granite  = 1.0 — neutral baseline.
        //   Limestone = 0.95 — earthy plain.
        //   DeadWood  = 0.9 — the most utilitarian.
        public static float BeautyBonus(StructureMat m) => m switch
        {
            StructureMat.Marble     => 1.50f,
            StructureMat.Obsidian   => 1.40f,
            StructureMat.Quartz     => 1.30f,
            StructureMat.FungalWood => 1.20f,
            StructureMat.LivingWood => 1.10f,
            StructureMat.Granite    => 1.00f,
            StructureMat.Limestone  => 0.95f,
            StructureMat.DeadWood   => 0.90f,
            StructureMat.Stone      => 1.00f,   // legacy
            StructureMat.Wood       => 0.95f,   // legacy
            // v0.5.84t — pebblestone reads as cleaner than raw stone (the
            // polish/rounding step lifts beauty) but well below the
            // premium polished marbles + obsidians.
            StructureMat.Pebblestone => 1.10f,
            _                        => 1.00f,
        };
    }
}
