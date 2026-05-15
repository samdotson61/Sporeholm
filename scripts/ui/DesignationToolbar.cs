using Godot;
using System;
using SmurfulationC.UI;

// Roadmap §3.x.2 — floating designation toolbar anchored to the bottom-centre
// of the play area. Hosts the player's high-level designation categories:
// Gather / Excavate / Storage / Wall / Priorities / Remove. Storage / Wall /
// Priorities are stubbed at v0.3.21 (Phase 5 + later); the active tools work
// via drag-box selection — pick a tool, then left-click-drag a rectangle over
// the map. Every valid tile in the box is designated; the renderer overlays a
// coloured glyph on each one. The nearest available smurf with the highest
// role priority for that designation picks it up via BehaviorSystem.SelectTask.
//
// Move-orders for an individually-selected smurf are issued via right-click
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
        Build_Wall   = 101,   // Phase 5B stub (construction pipeline)
        Priority     = 102,   // Phase 3.10 stub
    }

    [Signal] public delegate void ToolChangedEventHandler(int newTool);

    public Tool ActiveTool { get; private set; } = Tool.None;

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
        AddToolButton(Tool.Cut,      "✂ Cut",       "Clear any vegetation in box.");
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
        Refresh();
        EmitSignal(SignalName.ToolChanged, (int)ActiveTool);
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
