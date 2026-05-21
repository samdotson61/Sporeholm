using Godot;

namespace Sporeholm.UI
{
    // v0.5.19 (Phase 5B — rimport.md N3 + Roadmap §5.2 Build Mode).
    // Build tab content. Hosts the tile-based construction tools that
    // share `DesignationTool` enum with the Orders / Zones tab tools but
    // live in their own conceptual surface (Orders = one-shot work,
    // Zones = persistent territory designations, Build = planted-now-
    // realised-later structures).
    //
    // v0.5.19 ships three tools — Wall / Floor / Demolish. v0.5.20+ will
    // extend with Door (after pathfinding cost-tier), Furniture (after
    // ItemRegistry furniture-subtype block lands), Workbench / Bonfire
    // (after Bills system in v0.5.21).
    //
    // Design: same tool-routing pattern as ZonesPanel (v0.5.1) — buttons
    // call DesignationToolbar.SetActiveTool so there is exactly ONE
    // active tool across the whole player input surface. Subscribes to
    // ToolChanged so its button-pressed states stay in sync when the
    // player picks an Orders / Zones tab tool.
    public partial class BuildPanel : Control
    {
        private DesignationToolbar? _toolbar;
        private Button              _wallBtn     = null!;
        private Button              _floorBtn    = null!;
        private Button              _doorBtn     = null!;   // v0.5.20
        private Button              _shelfBtn    = null!;   // v0.5.21
        private Button              _workbenchBtn = null!;  // v0.5.32 (was stub)
        private Button              _bonfireBtn   = null!;   // v0.5.32 (was stub)
        private Button              _bedBtn      = null!;   // v0.5.35
        private Button              _shrineBtn   = null!;   // v0.5.36
        private Button              _boardBtn    = null!;   // v0.5.36
        private Button              _benchBtn    = null!;   // v0.5.36
        private Button              _tableBtn    = null!;   // v0.5.37
        private Button              _torchBtn    = null!;   // v0.5.84t
        private Button              _cookingTableBtn = null!; // v0.6.2 (Phase 5.6)
        private Button              _demolishBtn = null!;
        // v0.5.32 — material picker chips. One per StructureMat option.
        // Visibility + enabled state filtered per-tool by RefreshMaterialChips.
        private HBoxContainer       _matRow      = null!;
        private System.Collections.Generic.Dictionary<Sporeholm.World.StructureMat, Button> _matChips = new();

        // v0.5.42 — RimWorld-Architect-style sub-category panel. The Build
        // tab now opens with a sub-category selector chip row at the top;
        // the tool row below filters to show only the tools belonging to
        // the active sub-category. Default: Structure (walls / floors /
        // doors). Sam: "We should have a 'Build' tab like Rimworld's
        // 'Architect' tab that opens up with sub-categories... like
        // 'Orders', 'Zones', 'Structure', 'Furniture', etc."
        // v0.6.2 (Phase 5.6 ship) — Production sub-cat split off Furniture.
        // Production hosts the workstations that DRIVE production loops:
        // Workbench (Crafting recipes) + Cooking Table (Cooking recipes).
        // Phase 11 tier additions (Forge / Brewery / Loom / Magic Altar)
        // will land here too. Furniture keeps the passive interior pieces
        // (Bed / Table / Shelf / Bonfire / Torch). Bonfire stays in Furniture
        // because its primary purpose is heat — its 50%-speed cooking
        // fallback is a Step-5 CookSystem feature, not a UI categorisation.
        private enum SubCat { Structure, Production, Furniture, Joy }
        private SubCat _subCat = SubCat.Structure;
        private HBoxContainer _subCatRow  = null!;
        private HBoxContainer _toolsRow   = null!;
        private System.Collections.Generic.Dictionary<SubCat, Button> _subCatChips = new();

        public override void _Ready()
        {
            // v0.5.55 — FullRect preset so the panel fills its host
            // PanelContainer. Without it the panel defaulted to LayoutPreset.None
            // (top-left, size 0) and the host's content overflow had no
            // upper bound, contributing to the "panel extends off the right
            // edge of the screen" symptom Sam screenshotted.
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Pass;
            // v0.5.69 — pin a stable min-size so the host PanelContainer
            // doesn't jitter in width when the player switches between
            // sub-cats with different tool counts (Structure = 4 tools,
            // Furniture = 6 tools, Joy = 4 tools). v0.5.70 widened from
            // 660 → 960 to accommodate the expanded stone-subtype material
            // chip set (Stone + Granite + Limestone + Marble + Obsidian +
            // Quartz + DeadWood + FungalWood + LivingWood = 9 chips × 96
            // + 8 × sep 4 = ~896 logical px chip area + chrome). Height
            // is sized for the 3-row cascade-up with proper breathing
            // room so the bottom sub-cat chips don't crowd the panel edge
            // (Sam: "Fix UI to the bottom right" — v0.5.69).
            // v0.5.84n — height bumped 118 → 130 to fit the v0.5.84m bottom-
            // anchor spacer. Pre-v0.5.84m the VBox had 3 children (mat + tools
            // + subcat = 2 separations × 6 = 12, plus 28+38+28+12 pads = 118
            // exactly). v0.5.84m's spacer added a 4th child → 3 separations ×
            // 6 = 18, plus 0+28+38+28+12 pads = 124, overflowing the 118 cap
            // by 6 px and clipping the material chips above the panel border.
            // Bumped to 130 for a 6-px buffer.
            CustomMinimumSize = new Vector2(UITheme.Scaled(960), UITheme.Scaled(130));
            BuildContent();
            UITheme.UIScaleChanged += OnUIScaleChanged;
        }

        public override void _ExitTree()
        {
            UITheme.UIScaleChanged -= OnUIScaleChanged;
            if (_toolbar != null)
            {
                _toolbar.ToolChanged -= OnToolbarChanged;
                _toolbar.BuildMaterialChanged -= OnMaterialChanged;
            }
        }

        // v0.5.43 — rebuild the panel when the UI Size slider changes.
        // Pre-v0.5.43 the BuildPanel never rebuilt, so font sizes / chip
        // widths / styleboxes baked at original UI scale persisted; that
        // caused the "Build panel UI breaks after a little while" stale
        // visual state Sam called out. Same rebuild idiom as DesignationToolbar
        // (preserve _subCat + _toolbar binding, recreate children).
        private void OnUIScaleChanged()
        {
            var preservedSubCat = _subCat;
            var preservedToolbar = _toolbar;
            _toolbar = null;
            _subCatChips.Clear();
            _matChips.Clear();
            foreach (Node c in GetChildren()) c.QueueFree();
            BuildContent();
            _subCat = preservedSubCat;
            RefreshSubCatChips();
            RefreshToolVisibility();
            if (preservedToolbar != null) BindToolbar(preservedToolbar);
        }

        public void BindToolbar(DesignationToolbar toolbar)
        {
            _toolbar = toolbar;
            _toolbar.ToolChanged += OnToolbarChanged;
            _toolbar.BuildMaterialChanged += OnMaterialChanged;
            Refresh();
        }

        private void OnToolbarChanged(int newTool) => Refresh();
        private void OnMaterialChanged(int newMat) => RefreshMaterialChips();

        private void BuildContent()
        {
            var margin = new MarginContainer();
            margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);   // v0.5.55
            margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(8));
            margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(8));
            // v0.5.69 — bumped top/bottom from 4→6 so the cascade-up rows
            // get breathing room from the panel border. The screenshot
            // showed sub-cat chips crowding the bottom edge.
            margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(6));
            margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(6));
            AddChild(margin);

            // v0.5.43 — cascade-up layout. Sam: "All resource selections
            // and further selections should cascade up from the toolbar for
            // a clean look." Bottom row (closest to the tab bar) is the
            // sub-category chips — the FIRST thing the player picks once
            // Build opens. Middle row is the tool buttons that match the
            // active sub-cat. Top row is the material chips that appear
            // when the active tool accepts a material choice. Selections
            // expand UPWARD as the player drills in.
            // v0.5.69 — separation bumped 4→6 so the three rows feel like
            // distinct cascade tiers rather than one squashed block.
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 6);
            // v0.5.84p — bottom alignment via BoxContainer.AlignmentMode.
            // Replaces the v0.5.84m ExpandFill spacer, which competed
            // with the row layouts in some Godot edge cases and left
            // the material chips invisible. The Alignment property
            // does the same job cleanly: when content is shorter than
            // the container (panel min-height 130 vs content ~90 with
            // mat row hidden, ~124 with mat row visible), the slack
            // accumulates at the TOP and the rows pile against the
            // BOTTOM. Sub-cat row stays pinned; mat row appears above
            // tools row when it toggles visible.
            col.Alignment = BoxContainer.AlignmentMode.End;
            margin.AddChild(col);

            // Material chips at TOP — fills in last as player drills down.
            // (HBoxContainer created here; AddMaterialChip later populates it.)
            _matRow = new HBoxContainer();
            _matRow.AddThemeConstantOverride("separation", 4);
            col.AddChild(_matRow);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _toolsRow = row;
            col.AddChild(row);

            // v0.5.42 — sub-cat chip row. Added LAST so it renders at the
            // BOTTOM of the panel (closest to the tab bar) — first interaction.
            _subCatRow = new HBoxContainer();
            _subCatRow.AddThemeConstantOverride("separation", 4);
            col.AddChild(_subCatRow);

            AddSubCatChip(SubCat.Structure, "🧱 Structure",
                "Walls, floors, doors — the basic shell of any room. Click to filter the tool row to structural builds.");
            // v0.6.2 (Phase 5.6 ship) — Production tab. Holds workstations that
            // drive recipe loops. Workbench (Crafting) and Cooking Table
            // (Cooking) for now; Forge / Brewery / Loom / Magic Altar land
            // here in Phase 11 without retrofitting the Build panel.
            AddSubCatChip(SubCat.Production, "⚙ Production",
                "Workbench, Cooking Table — workstations where shroomps run crafting and cooking recipes. Bills queued via the tile-properties panel.");
            AddSubCatChip(SubCat.Furniture, "🪑 Furniture",
                "Beds, tables, shelves, bonfires, torches — interior pieces that make a room functional. Workstations live in the Production tab.");
            AddSubCatChip(SubCat.Joy,       "🎭 Joy",
                "Meditation shrines, shroom boards, gossip benches — recreation furniture that restores Joy faster than freelance idle.");

            var cfg = new ConfigFile();
            bool tips = cfg.Load("user://settings.cfg") != Error.Ok
                     || (bool)cfg.GetValue("gameplay", "show_tooltips", true);

            _wallBtn = MakeButton("🧱 Wall",
                tips ? "Plan walls. Shroomps deliver Stone or Wood and build them — completed walls are impassable. (Crafter role builds fastest.)" : "");
            _wallBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildWall);
            row.AddChild(_wallBtn);

            _floorBtn = MakeButton("🟦 Floor",
                tips ? "Plan floors. Shroomps deliver Stone or Wood and lay them — passable but visually distinct (1 mat per tile)." : "");
            _floorBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildFloor);
            row.AddChild(_floorBtn);

            // v0.5.20 (Phase 5C) — Door tool. Passable to shroomps (cosmetic
            // for v0.5.20; future Phase 7 combat will use line-of-sight).
            _doorBtn = MakeButton("🚪 Door",
                tips ? "Plan doors. Passable for shroomps; blocks line-of-sight (Phase 7 combat). Wood-built." : "");
            _doorBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildDoor);
            row.AddChild(_doorBtn);

            // v0.5.21 (Phase 5D) — Shelf storage furniture. Built shelves
            // add +1 stack capacity to their tile via IHaulDestination.
            _shelfBtn = MakeButton("🗄 Shelf",
                tips ? "Plan shelves. Storage furniture: +1 stack capacity per tile (haul destination)." : "");
            _shelfBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildShelf);
            row.AddChild(_shelfBtn);

            // v0.5.32 — Workbench + Bonfire promoted from stub to live tools
            // (the underlying SimulationManager + StructureOverlay handling
            // shipped in v0.5.22 / v0.5.24; only the BuildPanel button was
            // still a stub).
            // v0.6.2 (Phase 5.6 ship) — Workbench moved from Furniture to
            // Production. Cooking responsibility moved to the new Cooking
            // Table; Workbench now drives Crafting recipes only.
            _workbenchBtn = MakeButton("🔨 Workbench",
                tips ? "Plan a workbench. Crafters run Crafting recipes here (tools, weapons, cloth, knives, planks). Cooking moved to the Cooking Table (Phase 5.6)." : "");
            _workbenchBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildWorkbench);
            row.AddChild(_workbenchBtn);

            // v0.6.2 (Phase 5.6 ship) — Cooking Table. New dedicated cooking
            // workstation for the Cooking skill split. Cooks meals at full
            // speed; Bonfire becomes a half-speed fallback so a bare colony
            // can still cook before building a proper table.
            _cookingTableBtn = MakeButton("🍳 Cook Table",
                tips ? "Plan a Cooking Table. Cooks run Cooking recipes (Cook Meal, Juice Berries, future butchery) here at full speed. Bonfire is a fallback at half speed." : "");
            _cookingTableBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildCookingTable);
            row.AddChild(_cookingTableBtn);

            _bonfireBtn = MakeButton("🔥 Bonfire",
                tips ? "Plan a bonfire. Warms the room (+10°C per bonfire) and serves as a half-speed cooking fallback when no Cooking Table is built. Needed for cold-climate colonies." : "");
            _bonfireBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildBonfire);
            row.AddChild(_bonfireBtn);

            // v0.5.84t — Torch. Cheap floor-tile decoration + light source +
            // small heat (+2°C per torch). Built with 1 wood unit.
            _torchBtn = MakeButton("🕯 Torch",
                tips ? "Plan a torch. Cheap floor-mounted light source + small +2°C room heat per torch. Light emission is a Phase 10 stub today." : "");
            _torchBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildTorch);
            row.AddChild(_torchBtn);

            // v0.5.35 — Bed.
            _bedBtn = MakeButton("🛏 Bed",
                tips ? "Plan a bed. Shroomps sleeping on beds restore Rest faster + get WellRested mood (vs SleptOnGround penalty)." : "");
            _bedBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildBed);
            row.AddChild(_bedBtn);

            // v0.5.36 — Joy furniture (recreation).
            _shrineBtn = MakeButton("🕯 Shrine",
                tips ? "Plan a meditation shrine. Shroomps route here during idle to restore Joy (Solitary recreation)." : "");
            _shrineBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildMeditationShrine);
            row.AddChild(_shrineBtn);

            _boardBtn = MakeButton("🍄 Board",
                tips ? "Plan a shroom-board game. Cerebral recreation; shroomps gain Joy faster than freelance idle." : "");
            _boardBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildShroomBoard);
            row.AddChild(_boardBtn);

            _benchBtn = MakeButton("💬 Bench",
                tips ? "Plan a gossip bench. Social recreation; encourages shroomps to converse and bond." : "");
            _benchBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildGossipBench);
            row.AddChild(_benchBtn);

            // v0.5.37 — Table.
            _tableBtn = MakeButton("🍽 Table",
                tips ? "Plan a table. Shroomps eating adjacent to a table avoid the AteWithoutTable mood penalty (-3)." : "");
            _tableBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.BuildTable);
            row.AddChild(_tableBtn);

            // v0.6.2 — Demolish-as-task. Painting a built structure marks
            // it for tear-down (red X overlay); a Crafter walks to it and
            // performs demolition work over multiple ticks. Refund is
            // 20%-60% based on Construction skill (skilled crafters salvage
            // more). Re-painting the same tile cancels the demolition mark.
            _demolishBtn = MakeButton("⛏ Demolish",
                tips ? "Paint structures for demolition. A Crafter walks to the tile and performs the tear-down (Construction skill drives speed). Refund 20%-60% of the material cost based on Construction skill. Re-paint to cancel. Blueprints cancel instantly + refund any delivered materials." : "");
            _demolishBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.Demolish);
            row.AddChild(_demolishBtn);

            // v0.5.32 — material picker chips. Each chip is a toggle
            // button bound to a StructureMat. Filtered per-tool: walls/
            // floors/bonfires accept Stone + every wood sub-material; doors/
            // shelves/workbenches accept wood sub-materials only.
            // v0.5.43 — _matRow was created at the top of BuildContent
            // (cascade-up layout puts it at the top of the panel). Here
            // we just populate it with chips.
            // v0.5.84i — generic Stone chip removed per Sam: "remove
            // generic wood/stone entirely from the game and replace with
            // the appropriate subtypes." Players pick a concrete stone
            // subtype (Granite/Limestone/Marble/Obsidian/Quartz). Generic
            // Wood was never offered as a chip. Enum values kept for save-
            // compat so existing slots with Material=Stone/Wood still
            // render and consume normally — they just can't be picked for
            // new blueprints.
            // v0.5.70 — per-stone subtype chips. Each consumes its own
            // MaterialKey Stone/<SubType> from the colony pool (strict
            // consume via StructureMatMeta.ConsumeSubType). RimWorld-parity
            // material picker for stone walls / floors / bonfires.
            AddMaterialChip(Sporeholm.World.StructureMat.Granite,     "🪨 Granite",     tips);
            AddMaterialChip(Sporeholm.World.StructureMat.Limestone,   "◻ Limestone",   tips);
            AddMaterialChip(Sporeholm.World.StructureMat.Marble,      "◼ Marble",      tips);
            AddMaterialChip(Sporeholm.World.StructureMat.Obsidian,    "⬛ Obsidian",    tips);
            AddMaterialChip(Sporeholm.World.StructureMat.Quartz,      "◇ Quartz",      tips);
            // v0.5.84t — Pebblestone: cobblestone-like refined-stone, produced by
            // the "Refine *Material* Pebblestone" recipes. Consumes any stone-family
            // Pebblestone stack regardless of source stone (Granite/Marble/etc.).
            AddMaterialChip(Sporeholm.World.StructureMat.Pebblestone, "🪨 Pebblestone", tips);
            AddMaterialChip(Sporeholm.World.StructureMat.DeadWood,    "🪵 Dead Wood",   tips);
            AddMaterialChip(Sporeholm.World.StructureMat.FungalWood,  "🍄 Fungal Wood", tips);
            AddMaterialChip(Sporeholm.World.StructureMat.LivingWood,  "🌳 Living Wood", tips);

            RefreshMaterialChips();
            RefreshToolVisibility();   // v0.5.42 — apply default sub-cat filter
        }

        // v0.5.42 — sub-category chip. Each chip is a toggle button bound
        // to a SubCat value. Clicking switches the active sub-cat which
        // filters the tools row (RefreshSubCatVisibility). The chip's
        // pressed state mirrors the active sub-cat across all chips.
        private void AddSubCatChip(SubCat sub, string label, string tooltip)
        {
            // v0.5.55 — chip min-width 110→92 + height 26→24. Even at 92 the
            // text "🎭 Joy" / "🪑 Furniture" / "🧱 Structure" fits comfortably
            // after stylebox padding (~10-12 px each side).
            // v0.5.69 — chip height 24→28 so the bottom (closest-to-tab-bar)
            // row reads as a real interactive tier instead of a thin sliver
            // crowded against the tab bar. Width unchanged.
            var chip = new Button
            {
                Text              = label,
                ToggleMode        = true,
                ButtonPressed     = sub == _subCat,
                TooltipText       = tooltip,
                CustomMinimumSize = new Vector2(UITheme.Scaled(92), UITheme.Scaled(28)),
                FocusMode         = FocusModeEnum.None,
            };
            chip.AddThemeFontSizeOverride("font_size", UITheme.Scaled(11));
            chip.AddThemeColorOverride("font_color",         UITheme.TextAccent);
            chip.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
            chip.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
            chip.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
            chip.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
            chip.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
            chip.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
            chip.Pressed += () =>
            {
                _subCat = sub;
                RefreshSubCatChips();
                RefreshToolVisibility();
            };
            _subCatRow.AddChild(chip);
            _subCatChips[sub] = chip;
        }

        private void RefreshSubCatChips()
        {
            // v0.5.48 — two-pass clear + set, mirroring the tool-button
            // Refresh fix. Belt-and-suspenders against any stale pressed
            // state on individual chips that SetPressedNoSignal alone
            // hasn't cleared.
            foreach (var (sub, chip) in _subCatChips)
            {
                chip.ButtonPressed = false;
                chip.SetPressedNoSignal(false);
            }
            if (_subCatChips.TryGetValue(_subCat, out var active))
            {
                active.ButtonPressed = true;
                active.SetPressedNoSignal(true);
            }
        }

        // v0.5.42 — filters which build tool buttons are visible based on
        // the active sub-cat. Each tool maps to one sub-cat. The Demolish
        // button is always visible since it's a universal "undo" tool
        // across every sub-cat (matches RimWorld's persistent Deconstruct).
        private void RefreshToolVisibility()
        {
            _wallBtn     .Visible = _subCat == SubCat.Structure;
            _floorBtn    .Visible = _subCat == SubCat.Structure;
            _doorBtn     .Visible = _subCat == SubCat.Structure;
            // v0.6.2 (Phase 5.6 ship) — Workbench + CookingTable in Production.
            _workbenchBtn   .Visible = _subCat == SubCat.Production;
            _cookingTableBtn.Visible = _subCat == SubCat.Production;
            // Furniture: shelf / bonfire / bed / table / torch (interior pieces).
            _shelfBtn    .Visible = _subCat == SubCat.Furniture;
            _bonfireBtn   .Visible = _subCat == SubCat.Furniture;
            _bedBtn      .Visible = _subCat == SubCat.Furniture;
            _tableBtn    .Visible = _subCat == SubCat.Furniture;
            // v0.5.84t — Torch lives in Furniture sub-cat (cheap floor-tile
            // decoration + light + small heat). Joy/Structure don't fit.
            _torchBtn    .Visible = _subCat == SubCat.Furniture;
            _shrineBtn   .Visible = _subCat == SubCat.Joy;
            _boardBtn    .Visible = _subCat == SubCat.Joy;
            _benchBtn    .Visible = _subCat == SubCat.Joy;
            // _demolishBtn always visible — persistent across sub-cats.
        }

        private void AddMaterialChip(Sporeholm.World.StructureMat mat, string label, bool tips)
        {
            // v0.5.55 — chip min-width 120→96 + height 28→24. "🍄 Fungal Wood"
            // is the longest label and fits in 96 after stylebox padding.
            // v0.5.69 — chip height 24→28 to match the sub-cat tier and
            // give the cascade-up a consistent 28/38/28 visual rhythm
            // (material/tool/sub-cat).
            var chip = new Button
            {
                Text              = label,
                ToggleMode        = true,
                ButtonPressed     = false,
                TooltipText       = tips ? $"Build with {Sporeholm.World.StructureMatMeta.DisplayName(mat)}." : "",
                CustomMinimumSize = new Vector2(UITheme.Scaled(96), UITheme.Scaled(28)),
                FocusMode         = FocusModeEnum.None,
            };
            chip.AddThemeFontSizeOverride("font_size", UITheme.Scaled(11));
            chip.AddThemeColorOverride("font_color",         UITheme.TextPrimary);
            chip.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
            chip.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
            chip.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
            chip.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
            chip.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
            chip.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
            chip.Pressed += () => _toolbar?.SetActiveBuildMaterial(mat);
            _matRow.AddChild(chip);
            _matChips[mat] = chip;
        }

        // Filters which material chips are visible/enabled based on the
        // active Build tool. Walls / Floors / Bonfires accept any material;
        // Doors / Shelves / Workbenches / Beds / Joy / Tables are wood-only
        // (cosmetically — the family resolves through StructureMatMeta.
        // ConsumeFamily).
        private void RefreshMaterialChips()
        {
            if (_toolbar == null) return;
            var t = _toolbar.ActiveTool;
            bool isBuildTool =
                t == DesignationToolbar.Tool.BuildWall              ||
                t == DesignationToolbar.Tool.BuildFloor             ||
                t == DesignationToolbar.Tool.BuildDoor              ||
                t == DesignationToolbar.Tool.BuildShelf             ||
                t == DesignationToolbar.Tool.BuildWorkbench         ||
                t == DesignationToolbar.Tool.BuildBonfire            ||
                t == DesignationToolbar.Tool.BuildBed               ||
                t == DesignationToolbar.Tool.BuildMeditationShrine  ||
                t == DesignationToolbar.Tool.BuildShroomBoard       ||
                t == DesignationToolbar.Tool.BuildGossipBench       ||
                t == DesignationToolbar.Tool.BuildTable              ||
                t == DesignationToolbar.Tool.BuildTorch              ||
                t == DesignationToolbar.Tool.BuildCookingTable;       // v0.6.2 (Phase 5.6)
            _matRow.Visible = isBuildTool;
            if (!isBuildTool) return;

            // v0.5.84h — stone-family material chips are now offered for
            // ALL build tools, not just wall/floor/bonfire. Sam: "all
            // furniture, doors, and joy objects should be able to be
            // built with stone subtypes." Doors/Shelves/Workbenches/
            // Beds/MeditationShrine/ShroomBoard/GossipBench/Table all
            // accept any family — RimWorld-style: same sprite, per-
            // instance tint colours the material. ConsumeSubType handles
            // strict-consume for stone subtypes (Granite Door must use
            // Granite blocks), so the build pipeline already supports
            // it; the panel was just hiding the chips.
            //
            // v0.5.84d — per-map material availability. Sam: "Ensure no
            // structures or items can be built with materials that are
            // not generated/dropped. Reflect this in the UI." Query the
            // current map's GetAvailableStructureMats — chips for absent
            // materials get disabled + tooltip explaining "not present
            // on this map" so the player doesn't queue blueprints that
            // can never be fulfilled. Generic Stone/Wood are always in
            // the set (family-consume fallback covers them regardless
            // of subtype presence).
            var availMap = WorldState.Instance?.CurrentLocalMap;
            var avail = availMap?.GetAvailableStructureMats();
            // v0.5.48 — two-pass clear + set on material chip pressed
            // state. Same belt-and-suspenders pattern as tool buttons +
            // sub-cat chips: ButtonPressed direct write + SetPressedNoSignal.
            foreach (var (mat, chip) in _matChips)
            {
                bool present = avail == null || avail.Contains(mat);
                chip.Visible = true;
                chip.Disabled = !present;
                chip.ButtonPressed = false;
                chip.SetPressedNoSignal(false);
                chip.TooltipText = present
                    ? $"Build with {Sporeholm.World.StructureMatMeta.DisplayName(mat)}."
                    : $"{Sporeholm.World.StructureMatMeta.DisplayName(mat)} is not generated on this map.";
            }
            // Press exactly the active material's chip (if visible AND enabled).
            // If the active material became unavailable (e.g. map swap), the
            // chip stays unpressed — the player has to pick another.
            if (_matChips.TryGetValue(_toolbar.ActiveBuildMaterial, out var activeChip)
                && activeChip.Visible && !activeChip.Disabled)
            {
                activeChip.ButtonPressed = true;
                activeChip.SetPressedNoSignal(true);
            }
        }

        private Button MakeButton(string text, string tooltip)
        {
            // v0.5.55 — tool button min-width 140→100 + font 14→12. The
            // Furniture sub-cat shows 6 buttons (Shelf+Workbench+Bonfire+
            // Bed+Table+Demolish) — at 140 the row needed 870 logical px
            // which overflowed the panel's host area on the bottom-right.
            // At 100 the row fits in ~620 logical px including separations.
            var btn = new Button
            {
                Text              = text,
                ToggleMode        = true,
                ButtonPressed     = false,
                TooltipText       = tooltip,
                CustomMinimumSize = new Vector2(UITheme.Scaled(100), UITheme.Scaled(UITheme.ToolbarButtonSize)),
                FocusMode         = FocusModeEnum.None,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(12));
            btn.AddThemeColorOverride("font_color",         UITheme.TextPrimary);
            btn.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
            btn.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
            btn.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
            btn.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
            btn.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
            btn.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
            return btn;
        }

        private Button MakeStubButton(string text, string tooltip)
        {
            var btn = new Button
            {
                Text              = text,
                Disabled          = true,
                CustomMinimumSize = new Vector2(UITheme.Scaled(180), UITheme.Scaled(UITheme.ToolbarButtonSize)),
                FocusMode         = FocusModeEnum.None,
                TooltipText       = tooltip,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(14));
            btn.AddThemeColorOverride("font_color_disabled", UITheme.TextMuted);
            btn.AddThemeStyleboxOverride("disabled", FloatingPanelStyle.MakeToolbarButton(false));
            return btn;
        }

        private void Refresh()
        {
            if (_toolbar == null) return;
            // v0.5.48 — two-pass clear + set. Pre-v0.5.48 each button's
            // pressed state was set independently in one pass via
            // SetPressedNoSignal(active == X). If Godot's auto-toggle
            // had set Wall AND Floor both pressed between Refresh calls
            // (rapid clicks while ToolChanged signal propagation lagged),
            // the SetPressedNoSignal(false) path would NOT actually clear
            // the pressed visual on some builds. Sam screenshot showed
            // Wall + Floor both displayed gold-bordered as "pressed"
            // simultaneously. Belt-and-suspenders: clear every button's
            // ButtonPressed property directly + SetPressedNoSignal, THEN
            // press the one that matches the toolbar's ActiveTool.
            Button[] all = {
                _wallBtn, _floorBtn, _doorBtn, _shelfBtn, _workbenchBtn,
                _bonfireBtn, _bedBtn, _shrineBtn, _boardBtn, _benchBtn,
                _tableBtn, _torchBtn, _cookingTableBtn, _demolishBtn,   // v0.6.2 (Phase 5.6)
            };
            foreach (var b in all)
            {
                b.ButtonPressed = false;
                b.SetPressedNoSignal(false);
            }
            // Second pass: press exactly one (or none) based on active tool.
            Button? activeBtn = _toolbar.ActiveTool switch
            {
                DesignationToolbar.Tool.BuildWall              => _wallBtn,
                DesignationToolbar.Tool.BuildFloor             => _floorBtn,
                DesignationToolbar.Tool.BuildDoor              => _doorBtn,
                DesignationToolbar.Tool.BuildShelf             => _shelfBtn,
                DesignationToolbar.Tool.BuildWorkbench         => _workbenchBtn,
                DesignationToolbar.Tool.BuildBonfire            => _bonfireBtn,
                DesignationToolbar.Tool.BuildBed               => _bedBtn,
                DesignationToolbar.Tool.BuildMeditationShrine  => _shrineBtn,
                DesignationToolbar.Tool.BuildShroomBoard       => _boardBtn,
                DesignationToolbar.Tool.BuildGossipBench       => _benchBtn,
                DesignationToolbar.Tool.BuildTable             => _tableBtn,
                DesignationToolbar.Tool.BuildTorch             => _torchBtn,   // v0.5.84t
                DesignationToolbar.Tool.BuildCookingTable      => _cookingTableBtn, // v0.6.2 (Phase 5.6)
                DesignationToolbar.Tool.Demolish               => _demolishBtn,
                _                                              => null,
            };
            if (activeBtn != null)
            {
                activeBtn.ButtonPressed = true;
                activeBtn.SetPressedNoSignal(true);
            }
            RefreshMaterialChips();   // v0.5.32
        }

        // (v0.5.43 — _ExitTree merged with the earlier UIScaleChanged-aware
        // override above so we don't double-define the method.)

        // Mirror v0.5.4 ZonesPanel fix — the host PanelContainer in
        // BottomTabPanel needs a non-zero minimum size to render.
        public override Vector2 _GetMinimumSize()
        {
            Vector2 max = Vector2.Zero;
            foreach (var child in GetChildren())
                if (child is Control c && c.Visible)
                {
                    var m = c.GetCombinedMinimumSize();
                    if (m.X > max.X) max.X = m.X;
                    if (m.Y > max.Y) max.Y = m.Y;
                }
            return max;
        }
    }
}
