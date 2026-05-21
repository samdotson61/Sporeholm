using Godot;
using System;
using Sporeholm.UI;

// Roadmap §3.x.2 — floating designation toolbar anchored to the bottom-centre
// of the play area. Hosts the player's high-level designation categories:
// Gather / Excavate / Storage / Wall / Priorities / Remove. Storage / Wall /
// Priorities are stubbed at v0.3.21 (Phase 5 + later); the active tools work
// via drag-box selection — pick a tool, then left-click-drag a rectangle over
// the map. Every valid tile in the box is designated; the renderer overlays a
// coloured glyph on each one. The nearest available shroomp with the highest
// role priority for that designation picks it up via BehaviorSystem.SelectTask.
//
// Move-orders for an individually-selected shroomp are issued via right-click
// (see GameController.TryHandleMouseButton) and are independent of the
// toolbar's active tool — the old Tool.Move button was removed in v0.3.21
// because the Gather designation supersedes its general-purpose use case.
public partial class DesignationToolbar : Control
{
    // v0.3.21 — Tool is an alias for the shared DesignationTool enum so the
    // sim-side DesignateRect / SetXDesignation calls and the UI dispatcher
    // agree on identifiers. Build / Zone / Priority remain UI-only stubs.
    public enum Tool
    {
        None     = DesignationTool.None,
        Gather   = DesignationTool.Gather,
        Excavate = DesignationTool.Excavate,
        Remove   = DesignationTool.Remove,
        // v0.3.38 — new orders.
        ChopWood = DesignationTool.ChopWood,
        Cut      = DesignationTool.Cut,
        // v0.4.12 — force-haul order.
        Haul     = DesignationTool.Haul,
        // v0.5.0 (Phase 5A) — stockpile zone painter (no longer a stub).
        Stockpile = DesignationTool.Stockpile,
        // v0.5.19 (Phase 5B) — construction blueprints (no longer a stub).
        BuildWall  = DesignationTool.BuildWall,
        BuildFloor = DesignationTool.BuildFloor,
        // v0.5.20 (Phase 5C) — Door tool.
        BuildDoor  = DesignationTool.BuildDoor,
        // v0.5.21 (Phase 5D) — Shelf storage furniture.
        BuildShelf = DesignationTool.BuildShelf,
        // v0.5.22 (Phase 5E) — Workbench.
        BuildWorkbench = DesignationTool.BuildWorkbench,
        // v0.5.24 (Phase 5G) — Bonfire.
        BuildBonfire    = DesignationTool.BuildBonfire,
        // v0.5.35 (Phase 5 arc) — Bed.
        BuildBed       = DesignationTool.BuildBed,
        // v0.5.36 (Phase 5 arc) — Joy furniture (recreation).
        BuildMeditationShrine = DesignationTool.BuildMeditationShrine,
        BuildShroomBoard      = DesignationTool.BuildShroomBoard,
        BuildGossipBench      = DesignationTool.BuildGossipBench,
        // v0.5.37 (Phase 5 arc) — Table.
        BuildTable     = DesignationTool.BuildTable,
        // v0.5.84t — Torch. Cheap floor-tile decoration; +2°C per torch.
        BuildTorch     = DesignationTool.BuildTorch,
        // v0.6.2 (Phase 5.6 ship) — Cooking Table painter.
        BuildCookingTable = DesignationTool.BuildCookingTable,
        // v0.5.25 (Phase 5C polish) — Allowed-area painter (per-shroomp).
        AllowedArea    = DesignationTool.AllowedArea,
        Demolish   = DesignationTool.Demolish,
        Priority     = 102,   // Phase 3.10 stub
    }

    [Signal] public delegate void ToolChangedEventHandler(int newTool);

    // v0.5.32 — fires when the player picks a Build material from the
    // BuildPanel chip row. DesignateRect reads ActiveBuildMaterial when
    // placing blueprints. Defaults to Stone (matches pre-v0.5.32 hardcoded
    // wall/floor/bonfire default); BuildPanel switches to a wood variant
    // automatically when the player picks a wood-only tool (Door/Shelf/
    // Workbench), so the picker never has to handle "no material chosen".
    [Signal] public delegate void BuildMaterialChangedEventHandler(int newMat);

    // v0.5.44 — which named area the AllowedArea painter targets. Set by
    // AreasPanel when the player picks an area to edit. Default "Home"
    // because LocalMap auto-creates that area at world construction.
    [Signal] public delegate void ActiveAreaNameChangedEventHandler(string newAreaName);

    public Tool ActiveTool { get; private set; } = Tool.None;
    // v0.5.84i — default changed from generic Stone to Granite. Sam:
    // "remove generic wood/stone entirely from the game and replace
    // with the appropriate subtypes." Granite is the only stone subtype
    // that appears in every biome's weight table, so it's the safest
    // default — guaranteed present on every map.
    public Sporeholm.World.StructureMat ActiveBuildMaterial { get; private set; }
        = Sporeholm.World.StructureMat.Granite;
    public string ActiveAreaName { get; private set; } = "Home";

    // Maps the live-mode tools onto the shared sim enum so GameController
    // can hand the active tool straight to SimulationManager.DesignateRect
    // without a second switch.
    public DesignationTool ActiveDesignation => ActiveTool switch
    {
        Tool.Gather    => DesignationTool.Gather,
        Tool.Excavate  => DesignationTool.Excavate,
        Tool.ChopWood  => DesignationTool.ChopWood,
        Tool.Cut       => DesignationTool.Cut,
        Tool.Haul      => DesignationTool.Haul,
        Tool.Stockpile => DesignationTool.Stockpile,   // v0.5.0
        Tool.BuildWall  => DesignationTool.BuildWall,   // v0.5.19
        Tool.BuildFloor => DesignationTool.BuildFloor,  // v0.5.19
        Tool.BuildDoor  => DesignationTool.BuildDoor,   // v0.5.20
        Tool.BuildShelf => DesignationTool.BuildShelf,  // v0.5.21
        Tool.BuildWorkbench => DesignationTool.BuildWorkbench,   // v0.5.22
        Tool.BuildBonfire    => DesignationTool.BuildBonfire,      // v0.5.24
        Tool.BuildBed       => DesignationTool.BuildBed,         // v0.5.35
        Tool.BuildMeditationShrine => DesignationTool.BuildMeditationShrine,   // v0.5.36
        Tool.BuildShroomBoard      => DesignationTool.BuildShroomBoard,        // v0.5.36
        Tool.BuildGossipBench      => DesignationTool.BuildGossipBench,        // v0.5.36
        Tool.BuildTable     => DesignationTool.BuildTable,       // v0.5.37
        Tool.BuildTorch     => DesignationTool.BuildTorch,       // v0.5.84t
        Tool.BuildCookingTable => DesignationTool.BuildCookingTable, // v0.6.2 (Phase 5.6)
        Tool.AllowedArea    => DesignationTool.AllowedArea,      // v0.5.25
        Tool.Demolish   => DesignationTool.Demolish,    // v0.5.19
        Tool.Remove    => DesignationTool.Remove,
        _              => DesignationTool.None,
    };

    private HBoxContainer _row = null!;

    public override void _Ready()
    {
        // v0.3.24 — toolbar is now embedded inside BottomTabPanel, which
        // handles bottom-anchoring for the whole tabbed shell. Fill the
        // host's Control slot rather than self-anchoring to the viewport.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;

        BuildContent();
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    // v0.3.20 — rebuilds the toolbar's content on UI-scale changes so the new
    // button heights / font sizes take effect immediately. The current
    // `ActiveTool` is preserved across the rebuild — the toolbar is the player's
    // selected-mode state, losing it on a settings tweak would be jarring.
    private void OnUIScaleChanged()
    {
        var preserved = ActiveTool;
        _buttonByTool.Clear();
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildContent();
        // Restore the previously-active tool (Refresh inside BuildContent ran
        // at None; re-apply via the public setter so the matching button is
        // pressed and ToolChanged fires with the original value).
        ActiveTool = preserved;
        Refresh();
    }

    // Builds the toolbar's panel + tool buttons. Extracted from `_Ready` so
    // OnUIScaleChanged can call it again after clearing children.
    private void BuildContent()
    {
        // v0.3.27 — no wrapper Container; the toolbar's _GetMinimumSize
        // override below propagates the HBox's combined size up to the
        // hosting PanelContainer in BottomTabPanel so the panel actually
        // sizes to the buttons. MarginContainer adds breathing room so the
        // buttons don't hug the panel border.
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(8));
        margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(8));
        margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(4));
        margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(4));
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(margin);

        _row = new HBoxContainer();
        _row.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_row);

        // v0.3.38 — tooltips trimmed from multi-sentence descriptions to
        // short one-liners. The player asked for "small boxes near the
        // element" — Godot tooltip width tracks the text, so shorter
        // strings render in compact panels instead of spanning the
        // screen. The Show Tooltips setting in Gameplay still toggles all
        // tooltips on/off; AddToolButton respects it.
        AddToolButton(Tool.Gather,   "🍓 Gather",   "Harvest food vegetation in box.");
        AddToolButton(Tool.ChopWood, "🪓 Chop",     "Fell wood-yielding shrooms.");
        AddToolButton(Tool.Cut,      "✂ Cut",       "Clear vegetation. Drops the relevant resource — Fungal Wood from large shrooms, food from food-yielders, Cuttings from undergrowth/moss.");
        AddToolButton(Tool.Excavate, "⛏ Excavate", "Clear impassable tiles for Stone / Wood.");
        // v0.4.12 — force-haul. Marks every dropped item in the box for
        // priority pick-up; haulers ignore their normal 32-tile search
        // radius for these items.
        AddToolButton(Tool.Haul,     "📦 Haul",    "Force-haul every dropped item in box (bypasses auto-haul radius).");
        // v0.5.0 → v0.5.1 — Stockpile button moved to the Zones tab
        // (ZonesPanel) where it conceptually belongs. The Tool enum
        // entry stays so cross-tab activation still routes through this
        // toolbar's `SetActiveTool` (single source of active-tool truth).
        AddToolButton(Tool.Remove,   "✕ Remove",   "Wipe any designation or stockpile cell in box.");
        // v0.3.26 — Wall / Storage / Priorities moved to their own tabs in
        // BottomTabPanel (Build / Zones / Jobs). Orders tab is now just the
        // active order tools, mirroring RimWorld's tab separation.

        Refresh();
    }

    public void SetActiveTool(Tool t)
    {
        if (t == ActiveTool) t = Tool.None;  // click again to deselect
        ActiveTool = t;
        // v0.5.32 — auto-coerce the active material when the new tool only
        // accepts one family. Doors / Shelves / Workbenches are wood-only;
        // if the player switches into one of those with Stone selected,
        // snap to DeadWood so the next paint produces a valid blueprint.
        // Walls / Floors / Bonfires accept Stone + every wood sub-material,
        // so they leave whatever the player previously chose intact.
        // Wood-only tools snap to DeadWood if Stone is selected. v0.5.35-37
        // joins Beds, Joy furniture, and Tables to the wood-only family.
        bool isWoodOnly =
            t == Tool.BuildDoor              || t == Tool.BuildShelf            ||
            t == Tool.BuildWorkbench         || t == Tool.BuildBed              ||
            t == Tool.BuildMeditationShrine  || t == Tool.BuildShroomBoard      ||
            t == Tool.BuildGossipBench       || t == Tool.BuildTable;
        if (isWoodOnly &&
            !Sporeholm.World.StructureMatMeta.IsWoodFamily(ActiveBuildMaterial))
            SetActiveBuildMaterial(Sporeholm.World.StructureMat.DeadWood);
        Refresh();
        EmitSignal(SignalName.ToolChanged, (int)ActiveTool);
    }

    // v0.5.32 — material picker setter. BuildPanel chip click → here.
    public void SetActiveBuildMaterial(Sporeholm.World.StructureMat mat)
    {
        if (mat == ActiveBuildMaterial) return;
        ActiveBuildMaterial = mat;
        EmitSignal(SignalName.BuildMaterialChanged, (int)mat);
    }

    // v0.5.44 — named-area picker setter. AreasPanel selector → here.
    // The AllowedArea designation tool reads this when drag-paint dispatch
    // runs in GameController; tiles flip in the named area's bitmap.
    public void SetActiveAreaName(string areaName)
    {
        if (string.IsNullOrEmpty(areaName) || areaName == ActiveAreaName) return;
        ActiveAreaName = areaName;
        EmitSignal(SignalName.ActiveAreaNameChanged, areaName);
    }

    private void AddToolButton(Tool tool, string text, string tooltip, bool disabled = false)
    {
        // v0.3.38 — respect the "Show Tooltips" gameplay setting. When
        // off, the tooltip text is left empty so hover never produces a
        // panel. The setting is read once on each toolbar build (a Settings
        // change triggers UIScaleChanged → BuildContent → re-read).
        var cfg = new ConfigFile();
        bool tips = cfg.Load("user://settings.cfg") != Error.Ok
                 || (bool)cfg.GetValue("gameplay", "show_tooltips", true);

        var btn = new Button
        {
            Text              = text,
            ToggleMode        = true,
            ButtonPressed     = false,
            Disabled          = disabled,
            TooltipText       = tips ? tooltip : "",
            // v0.3.41 follow-up — uniform width across all order buttons.
            // Previously each sized to its label, which gave ✂ Cut a tight
            // capsule next to ⛏ Excavate's wider one. 130 px is enough for
            // the longest current label ("⛏ Excavate") at default UI scale.
            CustomMinimumSize = new Vector2(UITheme.Scaled(130), UITheme.Scaled(UITheme.ToolbarButtonSize)),
            FocusMode         = FocusModeEnum.None,  // don't steal arrow-key focus
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(14));
        btn.AddThemeColorOverride("font_color",          UITheme.TextPrimary);
        btn.AddThemeColorOverride("font_hover_color",    UITheme.TextAccent);
        btn.AddThemeColorOverride("font_pressed_color",  UITheme.TextAccent);
        btn.AddThemeColorOverride("font_disabled_color", UITheme.TextMuted);
        btn.AddThemeStyleboxOverride("normal",   FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("hover",    FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("pressed",  FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeStyleboxOverride("focus",    FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeStyleboxOverride("disabled", FloatingPanelStyle.MakeToolbarButton(false));
        btn.Pressed += () => SetActiveTool(tool);
        _row.AddChild(btn);
        _buttonByTool[tool] = btn;
    }

    private readonly System.Collections.Generic.Dictionary<Tool, Button> _buttonByTool = new();

    // Sync every button's pressed-state with ActiveTool so re-clicking the
    // active tool toggles it off without leaving the previous button stuck.
    private void Refresh()
    {
        foreach (var (tool, btn) in _buttonByTool)
            btn.SetPressedNoSignal(tool == ActiveTool);
    }

    // v0.3.27 — propagate the inner MarginContainer's minimum size so the
    // hosting PanelContainer in BottomTabPanel sizes to the actual buttons,
    // not to Control's default zero.
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
