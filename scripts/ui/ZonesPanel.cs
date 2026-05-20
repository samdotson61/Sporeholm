using Godot;

namespace Sporeholm.UI
{
    // v0.5.1 (Phase 5A) — Zones tab content. Hosts zone-painting tools that
    // share `DesignationTool` enum with the Orders tab's tools but live in
    // the conceptually-separate "zones" surface (per the rimport.md tab
    // separation: Orders are one-shot work commands, Zones are persistent
    // territory designations).
    //
    // Today: one button (Stockpile painter, v0.5.0). The Allowed-area
    // bitmap (rimport N6) and per-zone-config UI (Phase 5C) will land
    // here as additional buttons.
    //
    // Design: button activation routes through the existing
    // `DesignationToolbar.SetActiveTool` so there is exactly ONE active
    // tool across the whole player input surface. ZonesPanel subscribes
    // to `ToolChanged` so its button's pressed-state stays in sync when
    // the player picks an Orders-tab tool (Stockpile button releases) or
    // clicks the Stockpile button itself again (deselect, matches the
    // toolbar's click-twice-to-deselect convention).
    public partial class ZonesPanel : Control
    {
        private DesignationToolbar? _toolbar;
        private Button              _stockpileBtn   = null!;
        private Button              _allowedAreaBtn = null!;   // v0.5.25

        public override void _Ready()
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Pass;
            BuildContent();
            UITheme.UIScaleChanged += OnUIScaleChanged;   // v0.5.44
        }

        public override void _ExitTree()
        {
            UITheme.UIScaleChanged -= OnUIScaleChanged;
            if (_toolbar != null) _toolbar.ToolChanged -= OnToolbarChanged;
        }

        // v0.5.44 — rebuild on UI Size change so the panel's UITheme.Scaled
        // sizes pick up the new slider value. Same idiom as BuildPanel
        // (v0.5.43) and DesignationToolbar. Preserves the toolbar binding.
        private void OnUIScaleChanged()
        {
            var preservedToolbar = _toolbar;
            _toolbar = null;
            foreach (Node c in GetChildren()) c.QueueFree();
            BuildContent();
            if (preservedToolbar != null) BindToolbar(preservedToolbar);
        }

        // Called by GameController after both ZonesPanel and DesignationToolbar
        // are constructed — same pattern as `BottomTabPanel.Attach`.
        public void BindToolbar(DesignationToolbar toolbar)
        {
            _toolbar = toolbar;
            _toolbar.ToolChanged += OnToolbarChanged;
            Refresh();
        }

        private void OnToolbarChanged(int newTool)
        {
            Refresh();
        }

        private void BuildContent()
        {
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(8));
            margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(8));
            margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(4));
            margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(4));
            margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(margin);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            margin.AddChild(row);

            // Tooltip respects the gameplay setting, same as DesignationToolbar.
            var cfg = new ConfigFile();
            bool tips = cfg.Load("user://settings.cfg") != Error.Ok
                     || (bool)cfg.GetValue("gameplay", "show_tooltips", true);

            _stockpileBtn = MakeButton(
                "▦ Stockpile",
                tips ? "Paint cells as a stockpile zone (haul destination)." : "");
            _stockpileBtn.Pressed += () =>
            {
                _toolbar?.SetActiveTool(DesignationToolbar.Tool.Stockpile);
            };
            row.AddChild(_stockpileBtn);

            // v0.5.25 (Phase 5C — rimport N6) — Allowed Area painter live.
            // Per-shroomp bitmap. Paint with this tool active applies to the
            // currently-selected shroomp via GameController. If no shroomp is
            // selected, the paint is a no-op. Right-click while the tool
            // is active erases (clears the painted tile from the bitmap).
            _allowedAreaBtn = MakeButton(
                "▥ Allowed Area",
                tips ? "Per-shroomp allowed-area painter. Select one shroomp, then drag to paint where they're allowed to work. Erase the bitmap to remove all restrictions (shroomp can work anywhere)." : "");
            _allowedAreaBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.AllowedArea);
            row.AddChild(_allowedAreaBtn);
        }

        private Button MakeButton(string text, string tooltip)
        {
            var btn = new Button
            {
                Text              = text,
                ToggleMode        = true,
                ButtonPressed     = false,
                TooltipText       = tooltip,
                CustomMinimumSize = new Vector2(UITheme.Scaled(140), UITheme.Scaled(UITheme.ToolbarButtonSize)),
                FocusMode         = FocusModeEnum.None,
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(14));
            btn.AddThemeColorOverride("font_color",         UITheme.TextPrimary);
            btn.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
            btn.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
            btn.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
            btn.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
            btn.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
            btn.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
            return btn;
        }

        private void Refresh()
        {
            if (_toolbar == null) return;
            _stockpileBtn  .SetPressedNoSignal(
                _toolbar.ActiveTool == DesignationToolbar.Tool.Stockpile);
            _allowedAreaBtn.SetPressedNoSignal(
                _toolbar.ActiveTool == DesignationToolbar.Tool.AllowedArea);
        }

        // (v0.5.44 — _ExitTree merged with the UIScaleChanged-aware
        // override above so we don't double-define the method.)

        // v0.5.4 — propagate the inner MarginContainer's minimum size up
        // to the hosting PanelContainer in BottomTabPanel. Without this,
        // ZonesPanel reports zero min size (Control's default), the host
        // PanelContainer collapses to its style-margins (~12 px), and the
        // Zones tab visibly shows just the tab bar with NO content panel
        // above it. Same `_GetMinimumSize` pattern DesignationToolbar
        // uses (DesignationToolbar.cs:202).
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
