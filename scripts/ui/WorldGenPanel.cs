using Godot;
using System;
using System.Collections.Generic;
using SmurfulationC.World;

// Full-screen overlay shown when the player clicks New Game.
// Settings phase: name, seed, world size, level size, generation bias sliders, saved worlds.
// Map phase: generated world map; player clicks a tile to select landing zone.
public partial class WorldGenPanel : Control
{
    [Signal] public delegate void BeginColonyRequestedEventHandler();
    [Signal] public delegate void BackRequestedEventHandler();

    private static readonly Color ParchBg   = new(0.07f, 0.05f, 0.02f, 1.00f);
    private static readonly Color Gold       = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Parchment  = new(0.96f, 0.91f, 0.72f);
    private static readonly Color DarkWood   = new(0.20f, 0.12f, 0.04f);
    private static readonly Color Muted      = new(0.60f, 0.50f, 0.32f);

    // World size options (grid size N → N×N world tiles)
    // v0.4.41 — dropped the 32 / 64 entries (too coarse to give meaningful
    // landing-zone variety) and added 192 (128 × 1.5) at the top. 128 is
    // now the dropdown default since 96 became the new smallest option.
    private static readonly int[]          WorldSizeOptions = { 96, 128, 192 };
    private static readonly string[]       WorldSizeLabels  = { "96 × 96  (Small)", "128 × 128  (Default)", "192 × 192  (Max)" };

    // Level size options (local map width × height)
    // v0.4.41 — dropped the 80 × 50 default (postage-stamp-sized for a
    // colony) and added 720 × 450 (480 × 1.5) at the top. 240 × 150 is
    // now the dropdown default; 480 × 300 keeps the "Large" tag and the
    // new max is for players with the perf budget to enjoy a sprawling
    // cross-map expedition (~324k cells; ~2× the 480 generation time).
    private static readonly (int W, int H)[] LevelSizeOptions = { (160, 100), (240, 150), (320, 200), (480, 300), (720, 450) };
    private static readonly string[]         LevelSizeLabels  = { "160 × 100  (Small)", "240 × 150  (Default)", "320 × 200  (Recommended)", "480 × 300  (Large)", "720 × 450  (Max)" };

    // ── Phase containers ──────────────────────────────────────────────────────

    private Control   _settingsView = null!;
    private Control   _mapView      = null!;

    // Settings widgets
    private LineEdit     _nameEdit       = null!;
    private SpinBox      _seedSpin       = null!;
    private OptionButton _worldSizeDrop  = null!;
    private OptionButton _levelSizeDrop  = null!;
    private HSlider      _elevSlider     = null!;
    private HSlider      _rainSlider     = null!;
    private HSlider      _tempSlider     = null!;
    private HSlider      _magicSlider    = null!;
    private Label        _elevVal        = null!;
    private Label        _rainVal        = null!;
    private Label        _tempVal        = null!;
    private Label        _magicVal       = null!;
    private VBoxContainer _savedWorldsList = null!;

    // Map-phase state
    private WorldTile[,]? _worldMap;
    private int           _selX = -1, _selY = -1;
    private WorldMapControl        _mapControl    = null!;
    private LocalMapPreviewControl _preview       = null!;
    private Label                  _tileInfoLabel = null!;
    private Label                  _resourceInfoLabel = null!;   // v0.4.40 — resource breakdown under level preview
    private Label                  _levelTypeLabel = null!;      // v0.4.44 — biome + subtype label directly under preview thumbnail
    private AnimatedButton         _beginBtn      = null!;

    // Loading overlay shown during worldgen / level-preview generation. The
    // overlay is added directly to this panel as the last child so it sits
    // above all interactive controls. Generation work is `CallDeferred`-ed
    // one frame later so the overlay has a chance to render first.
    private Control _loadingOverlay = null!;
    private Label   _loadingLabel   = null!;

    // Cached args for the deferred preview generation (Godot's CallDeferred
    // can only marshal Variant-compatible primitives — WorldTile is a struct,
    // so we stage it on the panel itself instead of passing through args).
    private WorldTile _pendingPreviewTile;
    private int       _pendingPreviewW;
    private int       _pendingPreviewH;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = ParchBg };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var outer = new VBoxContainer { CustomMinimumSize = new Vector2(1000, 0) };
        outer.AddThemeConstantOverride("separation", 0);
        center.AddChild(outer);

        _settingsView = BuildSettingsView();
        outer.AddChild(_settingsView);

        _mapView = BuildMapView();
        outer.AddChild(_mapView);
        _mapView.Visible = false;

        _loadingOverlay = BuildLoadingOverlay();
        AddChild(_loadingOverlay);

        Visible = false;
    }

    // Full-rect overlay shown during worldgen and level-preview generation.
    // Same visual treatment as GameController's "Generating map…" overlay.
    private Control BuildLoadingOverlay()
    {
        var overlay = new ColorRect
        {
            Name    = "LoadingOverlay",
            Color   = new Color(0.04f, 0.07f, 0.10f, 0.92f),
            Visible = false,
        };
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Block clicks behind the overlay while it's visible.
        overlay.MouseFilter = MouseFilterEnum.Stop;

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        overlay.AddChild(center);

        _loadingLabel = new Label
        {
            Text                = "Generating…",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _loadingLabel.AddThemeColorOverride("font_color", Gold);
        _loadingLabel.AddThemeFontSizeOverride("font_size", 32);
        center.AddChild(_loadingLabel);

        return overlay;
    }

    private void ShowLoading(string text)
    {
        _loadingLabel.Text   = text;
        _loadingOverlay.Visible = true;
    }

    private void HideLoading() => _loadingOverlay.Visible = false;

    // ── Settings phase ────────────────────────────────────────────────────────

    private Control BuildSettingsView()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);

        AddTitle(vbox, "New Colony");

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        vbox.AddChild(sep);

        AddSpacer(vbox, 4);

        // Two-column content row
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 20);
        vbox.AddChild(columns);

        // Left column — settings
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0) };
        left.AddThemeConstantOverride("separation", 10);
        columns.AddChild(left);

        BuildSettingsLeft(left);

        // Vertical divider
        var vsep = new VSeparator();
        vsep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        columns.AddChild(vsep);

        // Right column — saved worlds
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        columns.AddChild(right);

        BuildSettingsRight(right);

        AddSpacer(vbox, 4);

        var sep2 = new HSeparator();
        sep2.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        vbox.AddChild(sep2);

        AddSpacer(vbox, 4);

        // Bottom buttons
        var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btnRow.AddThemeConstantOverride("separation", 16);

        var backBtn = new AnimatedButton { Text = "⬅  Back" };
        backBtn.Pressed += () => EmitSignal(SignalName.BackRequested);
        btnRow.AddChild(backBtn);

        var genBtn = new AnimatedButton { Text = "✦  Generate World" };
        genBtn.Pressed += OnGenerate;
        btnRow.AddChild(genBtn);

        vbox.AddChild(btnRow);
        return vbox;
    }

    private void BuildSettingsLeft(VBoxContainer parent)
    {
        // World name
        AddRow(parent, "World Name:", () =>
        {
            _nameEdit = new LineEdit
            {
                PlaceholderText     = "world-{seed}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            StyleLineEdit(_nameEdit);
            return _nameEdit;
        });

        // Seed row
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        seedRow.AddChild(Lbl("World Seed:", 16, Parchment));

        _seedSpin = new SpinBox
        {
            MinValue = 0, MaxValue = 999999, Step = 1,
            Value    = GD.Randi() % 999999,
            CustomMinimumSize = new Vector2(130, 0),
        };
        StyleSpinBox(_seedSpin);
        seedRow.AddChild(_seedSpin);

        var rndBtn = new AnimatedButton { Text = "↺", Compact = true };
        rndBtn.TooltipText = "Random seed";
        rndBtn.Pressed += () =>
        {
            _seedSpin.Value = GD.Randi() % 999999;
            if (string.IsNullOrWhiteSpace(_nameEdit.Text))
                _nameEdit.PlaceholderText = $"world-{(int)_seedSpin.Value}";
        };
        seedRow.AddChild(rndBtn);
        parent.AddChild(seedRow);

        // World size
        AddRow(parent, "World Size:", () =>
        {
            _worldSizeDrop = MakeOptionButton(WorldSizeLabels, 1);   // v0.4.41 — default = 128 (index 1)
            return _worldSizeDrop;
        });

        // Level size
        AddRow(parent, "Level Size:", () =>
        {
            _levelSizeDrop = MakeOptionButton(LevelSizeLabels, 1);   // v0.4.41 — default = 240×150 (index 1)
            return _levelSizeDrop;
        });

        AddSpacer(parent, 4);

        var attrSep = new HSeparator();
        attrSep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.4f));
        parent.AddChild(attrSep);

        var attrLabel = Lbl("Generation Bias", 14, Muted);
        attrLabel.HorizontalAlignment = HorizontalAlignment.Center;
        parent.AddChild(attrLabel);

        (_elevSlider,  _elevVal)  = AddBiasRow(parent, "Elevation");
        (_rainSlider,  _rainVal)  = AddBiasRow(parent, "Rainfall");
        (_tempSlider,  _tempVal)  = AddBiasRow(parent, "Temperature");
        (_magicSlider, _magicVal) = AddBiasRow(parent, "Magic Density");
    }

    private (HSlider slider, Label val) AddBiasRow(VBoxContainer parent, string name)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var lbl = Lbl(name + ":", 13, Parchment);
        lbl.CustomMinimumSize = new Vector2(110, 0);
        row.AddChild(lbl);

        var slider = new HSlider
        {
            MinValue = -1.0, MaxValue = 1.0, Step = 0.05,
            Value    = 0.0,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize   = new Vector2(0, 20),
        };
        row.AddChild(slider);

        var val = Lbl("  0", 13, Muted);
        val.CustomMinimumSize = new Vector2(32, 0);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(val);

        slider.ValueChanged += v =>
        {
            string sign = v > 0.005 ? "+" : (v < -0.005 ? "" : " ");
            val.Text = $"{sign}{v:F2}";
        };

        parent.AddChild(row);
        return (slider, val);
    }

    private void BuildSettingsRight(VBoxContainer parent)
    {
        var header = Lbl("Saved Worlds", 16, Gold);
        header.HorizontalAlignment = HorizontalAlignment.Center;
        parent.AddChild(header);

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        parent.AddChild(sep);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 220),
        };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        parent.AddChild(scroll);

        _savedWorldsList = new VBoxContainer();
        _savedWorldsList.AddThemeConstantOverride("separation", 6);
        _savedWorldsList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_savedWorldsList);
    }

    // ── Map phase ─────────────────────────────────────────────────────────────

    private Control BuildMapView()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);

        AddTitle(vbox, "Select Landing Zone");

        // Main row: level preview on the left, world map on the right.
        // v0.4.44 — bumped separation 16 → 32 so the Level Preview block
        // doesn't crowd the vertical divider on the right.
        var mainRow = new HBoxContainer();
        mainRow.AddThemeConstantOverride("separation", 32);
        vbox.AddChild(mainRow);

        // ── Left: level preview ──────────────────────────────────────────────

        var leftCol = new VBoxContainer();
        leftCol.AddThemeConstantOverride("separation", 8);
        leftCol.CustomMinimumSize = new Vector2(320, 0);
        mainRow.AddChild(leftCol);

        var previewHeader = Lbl("Level Preview", 16, Gold);
        previewHeader.HorizontalAlignment = HorizontalAlignment.Center;
        leftCol.AddChild(previewHeader);

        _preview = new LocalMapPreviewControl();
        leftCol.AddChild(_preview);

        // v0.4.44 — level-type label directly under the preview thumbnail
        // (biome + subtype variant, e.g. "Coastal Forest · River" or
        // "Caves"). Pre-v0.4.44 this info only appeared under the world
        // map on the right; Sam asked for it under the preview too so
        // it stays next to the visual it describes.
        _levelTypeLabel = Lbl("", 13, Gold);
        _levelTypeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _levelTypeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        leftCol.AddChild(_levelTypeLabel);

        // v0.4.40 — resource breakdown shown below the level preview once a
        // tile is picked. Walks the generated preview map and tallies
        // stone / wood / food / magic / water counts so the player can
        // pick a landing zone with eyes-open about resource scarcity
        // before committing.
        // v0.4.44 — reformatted to one line per resource, with raw
        // counts dropped and zero-quantity sub-types pruned. Wider
        // leftCol (320 px) so the line never wraps.
        var resourceHeader = Lbl("Resources", 14, Gold);
        resourceHeader.HorizontalAlignment = HorizontalAlignment.Center;
        leftCol.AddChild(resourceHeader);

        _resourceInfoLabel = Lbl("— select a tile —", 12, Muted);
        _resourceInfoLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _resourceInfoLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _resourceInfoLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        leftCol.AddChild(_resourceInfoLabel);

        // ── Divider ──────────────────────────────────────────────────────────

        var vsep = new VSeparator();
        vsep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        mainRow.AddChild(vsep);

        // ── Right: world map + tile info ─────────────────────────────────────

        var rightCol = new VBoxContainer();
        rightCol.AddThemeConstantOverride("separation", 10);
        rightCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mainRow.AddChild(rightCol);

        var sub = Lbl("Pondsea and Peaks tiles are impassable - colonies cannot land on water or sheer mountain peaks.", 13, Muted);
        sub.AutowrapMode = TextServer.AutowrapMode.Word;
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        rightCol.AddChild(sub);

        var mapCenter = new CenterContainer();
        _mapControl = new WorldMapControl();
        _mapControl.TileSelected += OnTileSelected;
        mapCenter.AddChild(_mapControl);
        rightCol.AddChild(mapCenter);

        _tileInfoLabel = Lbl("— select a tile —", 15, new Color(0.82f, 0.72f, 0.45f));
        _tileInfoLabel.HorizontalAlignment = HorizontalAlignment.Center;
        rightCol.AddChild(_tileInfoLabel);

        // ── Buttons ──────────────────────────────────────────────────────────

        var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btnRow.AddThemeConstantOverride("separation", 16);

        var backBtn = new AnimatedButton { Text = "⬅  Back" };
        backBtn.Pressed += () => { _mapView.Visible = false; _settingsView.Visible = true; };
        btnRow.AddChild(backBtn);

        _beginBtn = new AnimatedButton { Text = "✦  Begin Colony", Disabled = true };
        _beginBtn.TooltipText = "Select a valid landing tile first";
        _beginBtn.Pressed += OnBeginColony;
        btnRow.AddChild(_beginBtn);

        vbox.AddChild(btnRow);
        return vbox;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnGenerate()
    {
        // Show "Generating world…" overlay, then defer the actual generation by
        // one frame so the overlay renders before WorldMapGenerator.Generate
        // (which can take ~150 ms on a 128×128 world) blocks the main thread.
        ShowLoading("Generating world…");
        CallDeferred(MethodName.GenerateWorldDeferred);
    }

    private void GenerateWorldDeferred()
    {
        int seed = (int)_seedSpin.Value;
        int gridSize = WorldSizeOptions[_worldSizeDrop.Selected];
        var (lw, lh) = LevelSizeOptions[_levelSizeDrop.Selected];
        float elev  = (float)_elevSlider.Value;
        float rain  = (float)_rainSlider.Value;
        float temp  = (float)_tempSlider.Value;
        float magic = (float)_magicSlider.Value;
        string name = _nameEdit.Text.Trim();
        if (name.Length == 0) name = $"world-{seed}";
        _nameEdit.Text = name;

        _worldMap = WorldState.Instance?.GenerateWorld(seed, gridSize, elev, rain, temp, magic, name, lw, lh)
                 ?? WorldMapGenerator.Generate(seed, gridSize, elev, rain, temp, magic);

        WorldState.Instance?.SaveWorldFile();
        RefreshSavedWorlds();

        _selX = _selY = -1;
        _mapControl.SetMap(_worldMap);
        _mapControl.SetSelection(-1, -1);
        _tileInfoLabel.Text   = "— select a tile —";
        _resourceInfoLabel.Text = "— select a tile —";
        _levelTypeLabel.Text  = "";
        _beginBtn.Disabled    = true;
        _settingsView.Visible = false;
        _mapView.Visible      = true;

        HideLoading();
    }

    private void OnTileSelected(int x, int y)
    {
        if (_worldMap == null) return;
        var tile = _worldMap[x, y];
        if (!tile.Passable)
        {
            _tileInfoLabel.Text = tile.Biome == BiomeType.Peaks
                ? "Peaks — too sheer to colonise; choose a lower tile."
                : "Pondsea — choose a land tile.";
            _beginBtn.Disabled  = true;
            _mapControl.SetSelection(-1, -1);
            _preview.Clear();
            _resourceInfoLabel.Text = "— select a tile —";
            _levelTypeLabel.Text = "";
            _selX = _selY = -1;
            return;
        }

        _selX = x; _selY = y;
        _mapControl.SetSelection(x, y);
        // v0.4.45 — mountain biome now resolves through `GetMountainSubtypeName`
        // so the label distinguishes between the six mountain variants
        // (Caves / Rocky Terrain / Mountain Face / Solid Mountain / Canyon /
        // Crags). Non-mountain biomes still read as their plain enum name.
        string biomeName = (tile.Biome == BiomeType.Mountains || tile.Biome == BiomeType.Peaks)
            ? LocalMapGenerator.GetMountainSubtypeName(tile)
            : tile.Biome.ToString();
        string biomeLabel = tile.IsCoastal ? $"Coastal {biomeName}" : biomeName;
        if (tile.HasRiver) biomeLabel = $"{biomeLabel} · River";
        _tileInfoLabel.Text   = $"{biomeLabel} · Elev {tile.Elevation:P0} · Rain {tile.Rainfall:P0} · Magic {tile.MagicDensity:P0}";
        _levelTypeLabel.Text  = biomeLabel;   // v0.4.44 — same label, under the preview
        _beginBtn.Disabled    = false;
        _beginBtn.TooltipText = $"Begin colony in {biomeLabel} biome";

        // Generate the local map for this tile and render it in the preview panel.
        // Large (480×300) maps can take ~250 ms; show a brief overlay so the click
        // never feels like the UI froze. The tile is staged on _pendingPreviewTile
        // because CallDeferred's Variant marshalling can't carry a WorldTile struct.
        var (lw, lh)        = LevelSizeOptions[_levelSizeDrop.Selected];
        _pendingPreviewTile = tile;
        _pendingPreviewW    = lw;
        _pendingPreviewH    = lh;
        ShowLoading("Previewing level…");
        CallDeferred(MethodName.GeneratePreviewDeferred);
    }

    private void GeneratePreviewDeferred()
    {
        var map = LocalMapGenerator.Generate(_pendingPreviewTile, _pendingPreviewW, _pendingPreviewH);
        _preview.ShowMap(map);
        _resourceInfoLabel.Text = SummariseResources(map);
        HideLoading();
    }

    // v0.4.40 — walk the generated preview map and tally headline resource
    // counts. Cheap (single pass over Width × Height = ~12K cells at the
    // default 240×80 level size) and only runs after a tile click, so no
    // per-frame cost. Reports the categories that map directly to actual
    // gameplay extraction:
    //   • Stone — Boulder tile count (and called-out MagicCrystal vein tiles)
    //   • Wood  — DeadLog + LivingWood tile counts + Large Mushroom yield
    //   • Food  — count of food-bearing vegetation slots
    //   • Magic — MagicFlower vegetation + MagicGrove terrain
    //   • Water — Water + Shallows (fishing / wading footprint)
    private static string SummariseResources(SmurfulationC.World.LocalMap map)
    {
        int stone = 0, magicCrystal = 0;
        int deadLog = 0, livingWood = 0;
        int largeMushroom = 0;
        int food = 0, magicVeg = 0;
        int water = 0, shallows = 0;
        int magicGroveTiles = 0;
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width;  x++)
        {
            var t = map.Get(x, y);
            switch (t.Terrain)
            {
                case SmurfulationC.World.TerrainType.Boulder:
                    stone++;
                    var stoneKey = map.GetTileStone(x, y);
                    if (stoneKey.HasValue && stoneKey.Value.SubType == "MagicCrystal") magicCrystal++;
                    break;
                case SmurfulationC.World.TerrainType.DeadLog:    deadLog++; break;
                case SmurfulationC.World.TerrainType.LivingWood: livingWood++; break;
                case SmurfulationC.World.TerrainType.Water:      water++; break;
                case SmurfulationC.World.TerrainType.Shallows:   shallows++; break;
                case SmurfulationC.World.TerrainType.MagicGrove: magicGroveTiles++; break;
            }
            var veg = map.GetVegetation(x, y);
            if (!veg.IsPresent) continue;
            switch (veg.Type)
            {
                case SmurfulationC.World.VegetationType.LargeMushroom:
                case SmurfulationC.World.VegetationType.LargeSandshroom:
                case SmurfulationC.World.VegetationType.PalmShroom:
                    largeMushroom++;
                    break;
                case SmurfulationC.World.VegetationType.SmurfberryBush:
                case SmurfulationC.World.VegetationType.SmallMushroom:
                case SmurfulationC.World.VegetationType.HerbCluster:
                case SmurfulationC.World.VegetationType.SmallSandshroom:
                case SmurfulationC.World.VegetationType.PineShroom:
                    food++;
                    break;
                case SmurfulationC.World.VegetationType.MagicFlower:
                    magicVeg++;
                    food++;   // magic flower also yields food
                    break;
            }
        }

        // Bucket the raw counts into Scarce / Moderate / Abundant labels for
        // glanceability, mirroring the RimWorld vague-but-useful summary
        // style. Thresholds calibrated for a ~12K-cell default map; scale
        // proportionally if level size dial moves the total.
        // v0.4.44 — dropped the raw count numbers; sub-resources with
        // zero quantity are omitted entirely. One line per resource so
        // the leftCol panel doesn't wrap awkwardly.
        string Bucket(int n, int low, int high) =>
            n == 0 ? "None" :
            n < low ? "Scarce" :
            n < high ? "Moderate" :
            "Abundant";

        var sb = new System.Text.StringBuilder();

        sb.Append("  Stone:   ").Append(Bucket(stone, 60, 250));
        if (magicCrystal > 0) sb.Append("  ✦ Magic Crystal");
        sb.Append('\n');

        int woodTotal = deadLog + livingWood + largeMushroom * 3;
        sb.Append("  Wood:    ").Append(Bucket(woodTotal, 30, 120));
        var woodParts = new System.Collections.Generic.List<string>(3);
        if (deadLog       > 0) woodParts.Add("logs");
        if (livingWood    > 0) woodParts.Add("trees");
        if (largeMushroom > 0) woodParts.Add("mushrooms");
        if (woodParts.Count > 0) sb.Append("  (").Append(string.Join(", ", woodParts)).Append(')');
        sb.Append('\n');

        sb.Append("  Food:    ").Append(Bucket(food, 25, 90)).Append('\n');

        int magicTotal = magicVeg + magicGroveTiles + magicCrystal;
        sb.Append("  Magic:   ").Append(Bucket(magicTotal, 5, 30));
        var magicParts = new System.Collections.Generic.List<string>(3);
        if (magicVeg        > 0) magicParts.Add("flowers");
        if (magicGroveTiles > 0) magicParts.Add("grove");
        if (magicCrystal    > 0) magicParts.Add("crystal");
        if (magicParts.Count > 0) sb.Append("  (").Append(string.Join(", ", magicParts)).Append(')');
        sb.Append('\n');

        sb.Append("  Water:   ").Append(Bucket(water + shallows, 30, 200));
        var waterParts = new System.Collections.Generic.List<string>(2);
        if (water    > 0) waterParts.Add("deep");
        if (shallows > 0) waterParts.Add("shallows");
        if (waterParts.Count > 0) sb.Append("  (").Append(string.Join(", ", waterParts)).Append(')');
        return sb.ToString();
    }

    private void OnBeginColony()
    {
        if (_selX < 0 || _selY < 0) return;
        WorldState.Instance?.SelectTile(_selX, _selY);
        EmitSignal(SignalName.BeginColonyRequested);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Open()
    {
        _settingsView.Visible = true;
        _mapView.Visible      = false;
        RefreshSavedWorlds();
        Visible = true;
    }

    public void Close() => Visible = false;

    // ── Saved worlds list ─────────────────────────────────────────────────────

    private void RefreshSavedWorlds()
    {
        foreach (var child in _savedWorldsList.GetChildren())
            child.QueueFree();

        var worlds = WorldState.Instance?.GetSavedWorlds() ?? new List<WorldState.WorldFileInfo>();

        if (worlds.Count == 0)
        {
            var empty = Lbl("No saved worlds.", 13, Muted);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            _savedWorldsList.AddChild(empty);
            return;
        }

        foreach (var w in worlds)
            _savedWorldsList.AddChild(BuildWorldRow(w));
    }

    private Control BuildWorldRow(WorldState.WorldFileInfo info)
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 6);

        var nameLabel = Lbl(info.Name, 14, Parchment);
        nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(nameLabel);

        var loadBtn = new AnimatedButton { Text = "Load", Compact = true };
        loadBtn.TooltipText = "Load these world settings into the left panel";
        loadBtn.Pressed += () => PopulateFromWorldInfo(info);
        top.AddChild(loadBtn);

        var delBtn = new AnimatedButton { Text = "✕", Compact = true };
        delBtn.Modulate = new Color(1f, 0.55f, 0.45f);
        delBtn.TooltipText = "Delete this saved world";
        delBtn.Pressed += () =>
        {
            WorldState.Instance?.DeleteWorldFile(info.FileName);
            RefreshSavedWorlds();
        };
        top.AddChild(delBtn);
        vbox.AddChild(top);

        string szLabel = $"{info.GridSize}×{info.GridSize} world · {info.LocalMapWidth}×{info.LocalMapHeight} level";
        vbox.AddChild(Lbl(szLabel, 11, Muted));

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.35f));
        vbox.AddChild(sep);

        return vbox;
    }

    private void PopulateFromWorldInfo(WorldState.WorldFileInfo info)
    {
        _nameEdit.Text  = info.Name;
        _seedSpin.Value = info.Seed;

        for (int i = 0; i < WorldSizeOptions.Length; i++)
            if (WorldSizeOptions[i] == info.GridSize) { _worldSizeDrop.Select(i); break; }

        for (int i = 0; i < LevelSizeOptions.Length; i++)
            if (LevelSizeOptions[i] == (info.LocalMapWidth, info.LocalMapHeight)) { _levelSizeDrop.Select(i); break; }

        _elevSlider.Value  = info.ElevBias;
        _rainSlider.Value  = info.RainBias;
        _tempSlider.Value  = info.TempBias;
        _magicSlider.Value = info.MagicBias;
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static void AddRow(VBoxContainer parent, string labelText, Func<Control> buildWidget)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var lbl = Lbl(labelText, 16, Parchment);
        lbl.CustomMinimumSize = new Vector2(110, 0);
        row.AddChild(lbl);
        var widget = buildWidget();
        widget.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(widget);
        parent.AddChild(row);
    }

    private void AddTitle(VBoxContainer parent, string text)
    {
        var lbl = new Label
        {
            Text                = text,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        lbl.AddThemeColorOverride("font_color", Gold);
        lbl.AddThemeFontSizeOverride("font_size", 36);
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            lbl.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        parent.AddChild(lbl);
    }

    private static Label Lbl(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        l.MouseFilter = MouseFilterEnum.Ignore;
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        return l;
    }

    private static void AddSpacer(VBoxContainer parent, int h) =>
        parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, h) });

    private static OptionButton MakeOptionButton(string[] items, int selectedIndex)
    {
        var btn = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        foreach (var item in items)
            btn.AddItem(item);
        btn.Select(selectedIndex);
        var style = new StyleBoxFlat { BgColor = new Color(0.96f, 0.91f, 0.72f) };
        style.SetBorderWidthAll(2);
        style.BorderColor = new Color(0.55f, 0.38f, 0.12f);
        style.SetCornerRadiusAll(4);
        style.ContentMarginLeft = 6;
        btn.AddThemeStyleboxOverride("normal",   style);
        btn.AddThemeStyleboxOverride("hover",    style);
        btn.AddThemeStyleboxOverride("pressed",  style);
        btn.AddThemeStyleboxOverride("focus",    style);
        btn.AddThemeColorOverride("font_color", DarkWood);
        btn.AddThemeFontSizeOverride("font_size", 14);
        return btn;
    }

    private static void StyleSpinBox(SpinBox spin)
    {
        var style = new StyleBoxFlat { BgColor = new Color(0.96f, 0.91f, 0.72f) };
        style.SetBorderWidthAll(2);
        style.BorderColor = new Color(0.55f, 0.38f, 0.12f);
        style.SetCornerRadiusAll(5);
        style.ContentMarginLeft  = 8;
        style.ContentMarginRight = 8;
        spin.GetLineEdit().AddThemeStyleboxOverride("normal", style);
        spin.GetLineEdit().AddThemeStyleboxOverride("focus",  style);
        spin.GetLineEdit().AddThemeColorOverride("font_color", DarkWood);
        spin.GetLineEdit().AddThemeFontSizeOverride("font_size", 15);
    }

    private static void StyleLineEdit(LineEdit edit)
    {
        edit.CustomMinimumSize = new Vector2(0, 34);
        var style = new StyleBoxFlat { BgColor = new Color(0.96f, 0.91f, 0.72f) };
        style.SetBorderWidthAll(2);
        style.BorderColor = new Color(0.55f, 0.38f, 0.12f);
        style.SetCornerRadiusAll(5);
        style.ContentMarginLeft  = 8;
        style.ContentMarginRight = 8;
        edit.AddThemeStyleboxOverride("normal", style);
        edit.AddThemeStyleboxOverride("focus",  style);
        edit.AddThemeColorOverride("font_color",             DarkWood);
        edit.AddThemeColorOverride("font_placeholder_color", Muted);
        edit.AddThemeFontSizeOverride("font_size", 15);
    }

    // ── Inner class: local map preview ───────────────────────────────────────

    // Renders a 1-pixel-per-tile minimap of a generated LocalMap using the same colour
    // table as LocalMapRenderer, with a simplified vegetation colour tint per tile.
    // The image is capped at the display dimensions and scaled up with nearest-neighbour
    // filtering so it is always crisp regardless of level size.
    private sealed partial class LocalMapPreviewControl : Control
    {
        private const float DisplayW = 300f;
        private const float DisplayH = 188f;   // 8:5 — matches all level size aspect ratios

        private ImageTexture? _texture;

        public LocalMapPreviewControl()
        {
            CustomMinimumSize = new Vector2(DisplayW, DisplayH);
            TextureFilter     = TextureFilterEnum.Nearest;
        }

        public void ShowMap(LocalMap map)
        {
            // Build the image at 1 px per tile, capped at display dimensions so the
            // texture is never wider/taller than the panel (always upscaled → crisp).
            int imgW = Math.Min(map.Width,  (int)DisplayW);
            int imgH = Math.Min(map.Height, (int)DisplayH);
            float sx = map.Width  / (float)imgW;
            float sy = map.Height / (float)imgH;

            var img = Image.CreateEmpty(imgW, imgH, false, Image.Format.Rgb8);
            for (int py = 0; py < imgH; py++)
            {
                int ty = (int)(py * sy);
                for (int px = 0; px < imgW; px++)
                {
                    int tx  = (int)(px * sx);
                    var col = LocalMapRenderer.TileColor(map.Get(tx, ty).Terrain);
                    var veg = map.GetVegetation(tx, ty);
                    if (veg.IsPresent)
                        col = col.Lerp(VegColor(veg.Type), 0.55f);
                    img.SetPixel(px, py, col);
                }
            }

            _texture = ImageTexture.CreateFromImage(img);
            QueueRedraw();
        }

        public void Clear()
        {
            _texture = null;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var rect = new Rect2(Vector2.Zero, Size);
            DrawRect(rect, new Color(0.05f, 0.04f, 0.03f));
            if (_texture != null)
            {
                DrawTextureRect(_texture, rect, false);
                DrawRect(rect, new Color(0.55f, 0.40f, 0.12f, 0.5f), false, 2f);
            }
            else
            {
                DrawString(ThemeDB.FallbackFont,
                    new Vector2(Size.X * 0.5f, Size.Y * 0.5f),
                    "Select a tile to preview", HorizontalAlignment.Center,
                    (int)Size.X, 13, new Color(0.55f, 0.45f, 0.28f));
            }
        }

        private static Color VegColor(VegetationType v) => v switch
        {
            VegetationType.LargeMushroom  => new Color(0.85f, 0.12f, 0.08f),
            VegetationType.SmurfberryBush => new Color(0.20f, 0.65f, 0.15f),
            VegetationType.Underbrush     => new Color(0.10f, 0.38f, 0.10f),
            VegetationType.SmallMushroom  => new Color(0.80f, 0.68f, 0.40f),
            VegetationType.HerbCluster    => new Color(0.24f, 0.72f, 0.18f),
            VegetationType.MagicFlower    => new Color(0.78f, 0.15f, 0.88f),
            VegetationType.MossPatch      => new Color(0.22f, 0.60f, 0.48f),
            _                             => Colors.Transparent,
        };
    }

    // ── Inner class: world map display control ────────────────────────────────

    // Draws the world grid as colored tiles and reports click coordinates.
    private sealed partial class WorldMapControl : Control
    {
        public event Action<int, int>? TileSelected;

        // Target display size in pixels; cell size computed from grid dimensions.
        // v0.4.40 — bumped 448 → 560 (~1.25×) so the Landing Zone tile grid
        // is comfortable to scan and click. The world map shares the screen
        // with the Level Preview + selection panels, so this size still
        // fits at 1280×720 with the leftCol's 300 px reserved width.
        private const int TargetDisplayPx = 560;

        private WorldTile[,]? _map;
        private int _gridSize = WorldMapGenerator.DefaultGridSize;
        private int _cellSize = TargetDisplayPx / WorldMapGenerator.DefaultGridSize;
        private int _selX = -1, _selY = -1;

        public WorldMapControl()
        {
            UpdateSize();
            MouseFilter = MouseFilterEnum.Stop;
        }

        public void SetMap(WorldTile[,] map)
        {
            _map      = map;
            _gridSize = map.GetLength(0);
            _cellSize = Math.Max(3, TargetDisplayPx / _gridSize);
            UpdateSize();
            QueueRedraw();
        }

        public void SetSelection(int x, int y) { _selX = x; _selY = y; QueueRedraw(); }

        private void UpdateSize() =>
            CustomMinimumSize = new Vector2(_gridSize * _cellSize, _gridSize * _cellSize);

        public override void _Draw()
        {
            if (_map == null) return;

            for (int y = 0; y < _gridSize; y++)
                for (int x = 0; x < _gridSize; x++)
                {
                    var rect = new Rect2(x * _cellSize, y * _cellSize, _cellSize, _cellSize);
                    DrawRect(rect, BiomeColor(_map[x, y].Biome));
                    // River overlay (Phase 2.6): a blue stripe marks tiles
                    // where the local map will carve a river channel. v0.4.31
                    // bumped opacity + thickness; v0.4.38 rotates the stripe
                    // based on which neighbour tiles are also flagged HasRiver
                    // so multi-tile snaking chains (v0.4.37) read as a
                    // connected ribbon across the world preview instead of N
                    // parallel horizontal stripes.
                    //
                    // Direction picker:
                    //   • N or S neighbour HasRiver but no W/E → vertical stripe
                    //   • W or E neighbour HasRiver but no N/S → horizontal stripe
                    //   • Both axes have a river neighbour (junction or bend) →
                    //     draw both stripes overlapping, forms a + or L
                    //   • No river neighbour (orphan single tile) → horizontal
                    //     default so the user still sees something.
                    if (_map[x, y].HasRiver)
                    {
                        bool hasN = y > 0              && _map[x, y - 1].HasRiver;
                        bool hasS = y < _gridSize - 1  && _map[x, y + 1].HasRiver;
                        bool hasW = x > 0              && _map[x - 1, y].HasRiver;
                        bool hasE = x < _gridSize - 1  && _map[x + 1, y].HasRiver;
                        bool vertAxis = hasN || hasS;
                        bool horizAxis = hasW || hasE;
                        // Orphan tile → fallback horizontal.
                        if (!vertAxis && !horizAxis) horizAxis = true;
                        float stripeT = Mathf.Max(2f, _cellSize * 0.35f);
                        var fill = new Color(0.16f, 0.40f, 0.78f, 1.0f);
                        var outlineCol = new Color(0.05f, 0.18f, 0.40f, 1.0f);
                        if (horizAxis)
                        {
                            // Horizontal stripe (W↔E flow).
                            DrawRect(new Rect2(
                                x * _cellSize,
                                y * _cellSize + (_cellSize - stripeT) * 0.5f,
                                _cellSize, stripeT), fill);
                            DrawRect(new Rect2(x * _cellSize,
                                y * _cellSize + (_cellSize - stripeT) * 0.5f - 1f,
                                _cellSize, 1f), outlineCol);
                            DrawRect(new Rect2(x * _cellSize,
                                y * _cellSize + (_cellSize + stripeT) * 0.5f,
                                _cellSize, 1f), outlineCol);
                        }
                        if (vertAxis)
                        {
                            // Vertical stripe (N↔S flow).
                            DrawRect(new Rect2(
                                x * _cellSize + (_cellSize - stripeT) * 0.5f,
                                y * _cellSize,
                                stripeT, _cellSize), fill);
                            DrawRect(new Rect2(
                                x * _cellSize + (_cellSize - stripeT) * 0.5f - 1f,
                                y * _cellSize, 1f, _cellSize), outlineCol);
                            DrawRect(new Rect2(
                                x * _cellSize + (_cellSize + stripeT) * 0.5f,
                                y * _cellSize, 1f, _cellSize), outlineCol);
                        }
                    }
                }

            if (_selX >= 0 && _selY >= 0)
            {
                var rect = new Rect2(_selX * _cellSize, _selY * _cellSize, _cellSize, _cellSize);
                DrawRect(rect, new Color(1f, 0.9f, 0.2f, 0.6f));
                DrawRect(rect, new Color(1f, 0.9f, 0.2f), false, 2f);
            }
        }

        public override void _GuiInput(InputEvent ev)
        {
            if (ev is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Left)
                return;
            int x = (int)(mb.Position.X / _cellSize);
            int y = (int)(mb.Position.Y / _cellSize);
            if (x >= 0 && x < _gridSize && y >= 0 && y < _gridSize)
                TileSelected?.Invoke(x, y);
        }

        private static Color BiomeColor(BiomeType b) => b switch
        {
            BiomeType.Pondsea    => new Color(0.16f, 0.38f, 0.68f),
            BiomeType.Coast      => new Color(0.78f, 0.72f, 0.44f),
            BiomeType.Island     => new Color(0.70f, 0.84f, 0.48f),  // bright sandy-green
            BiomeType.Desert     => new Color(0.85f, 0.72f, 0.36f),
            BiomeType.Plains     => new Color(0.52f, 0.78f, 0.34f),
            BiomeType.Swamp      => new Color(0.30f, 0.40f, 0.18f),
            BiomeType.Forest     => new Color(0.16f, 0.46f, 0.14f),
            BiomeType.Hills      => new Color(0.52f, 0.58f, 0.28f),
            BiomeType.Mountains  => new Color(0.55f, 0.52f, 0.48f),
            BiomeType.Peaks      => new Color(0.88f, 0.88f, 0.90f),
            BiomeType.MagicGrove => new Color(0.50f, 0.20f, 0.75f),
            _                    => Colors.Black,
        };
    }
}
