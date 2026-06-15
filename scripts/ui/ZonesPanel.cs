using Godot;
using System.Collections.Generic;
using Sporeholm.World;
using Sporeholm.Simulation.Items;

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
        private Button              _farmBtn        = null!;   // v0.8.0 (Phase 8)
        private readonly Dictionary<CropType, Button> _cropChips = new();   // v0.8.0

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
            if (_toolbar != null)
            {
                _toolbar.ToolChanged -= OnToolbarChanged;
                _toolbar.ActiveCropChanged -= OnActiveCropChanged;   // v0.8.0
            }
        }

        // v0.5.44 — rebuild on UI Size change so the panel's UITheme.Scaled
        // sizes pick up the new slider value. Same idiom as BuildPanel
        // (v0.5.43) and DesignationToolbar. Preserves the toolbar binding.
        private void OnUIScaleChanged()
        {
            var preservedToolbar = _toolbar;
            // v0.8.0 — unsubscribe BEFORE nulling so BindToolbar's re-subscribe
            // doesn't leak a duplicate ToolChanged/ActiveCropChanged handler on
            // the long-lived toolbar every time the UI scale changes.
            if (_toolbar != null)
            {
                _toolbar.ToolChanged -= OnToolbarChanged;
                _toolbar.ActiveCropChanged -= OnActiveCropChanged;
            }
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
            _toolbar.ActiveCropChanged += OnActiveCropChanged;   // v0.8.0
            Refresh();
        }

        private void OnToolbarChanged(int newTool)
        {
            Refresh();
        }

        // v0.8.0 — keep the crop-chip pressed-states in sync when the active
        // crop changes (e.g. another panel sets it, or the rebuild restores it).
        private void OnActiveCropChanged(int newCrop)
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

            // v0.8.0 — two stacked rows: zone tools on top, the Farm crop
            // picker beneath. A VBox lets the crop-chip row wrap under the
            // tools without widening the whole bottom panel.
            _cropChips.Clear();
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 6);
            margin.AddChild(col);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            col.AddChild(row);

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

            // v0.8.0 (Phase 8) — Farm grow-zone painter. Activating it paints
            // the currently-selected crop (defaults to Small Mushroom).
            _farmBtn = MakeButton(
                "🌱 Farm",
                tips ? "Paint a farm grow-zone for the selected crop below. Growers sow + harvest it (Grow work priority). Remove clears grow-zone cells." : "");
            _farmBtn.Pressed += () => _toolbar?.SetActiveTool(DesignationToolbar.Tool.Farm);
            row.AddChild(_farmBtn);

            // ── Crop picker row ──────────────────────────────────────────────
            // One chip per registered crop. Clicking a chip selects that crop
            // AND switches to the Farm tool, so picking a crop is the one-click
            // way to start farming it. Botany requirement is in the tooltip;
            // crops above the colony's skill simply wait for a skilled grower.
            var cropRow = new HBoxContainer();
            cropRow.AddThemeConstantOverride("separation", 4);
            col.AddChild(cropRow);

            foreach (var def in CropRegistry.All)
            {
                var crop = def.Type;
                // Show the Botany requirement right on the chip ("(B6)") so the
                // player sees at paint time that a high-tier crop needs a skilled
                // grower — otherwise the painted field sits idle with no feedback.
                string botanyReq = def.BotanyMin > 0 ? $" (B{def.BotanyMin})" : "";
                string botanyTip = def.BotanyMin > 0 ? $"Needs Botany {def.BotanyMin}+ · " : "";
                // Resolve the real harvest item name (CaveMoss→Mosslet,
                // LargeMushroom→Wood Log, etc.) rather than the crop's own name.
                var yieldDef = ItemRegistry.Get(def.YieldItemKind, def.YieldItemSubType);
                string yieldName = yieldDef?.DisplayName ?? def.DisplayName;
                var chip = MakeCropChip(
                    $"{def.Icon} {def.DisplayName}{botanyReq}",
                    tips ? $"{botanyTip}yields {yieldName}." : "");
                chip.Pressed += () =>
                {
                    _toolbar?.SetActiveCrop(crop);
                    if (_toolbar != null && _toolbar.ActiveTool != DesignationToolbar.Tool.Farm)
                        _toolbar.SetActiveTool(DesignationToolbar.Tool.Farm);
                    Refresh();
                };
                _cropChips[crop] = chip;
                cropRow.AddChild(chip);
            }
        }

        // v0.8.0 — compact toggle chip for the crop picker (narrower than the
        // zone-tool buttons so the full crop list fits one row).
        private Button MakeCropChip(string text, string tooltip)
        {
            var btn = new Button
            {
                Text              = text,
                ToggleMode        = true,
                ButtonPressed     = false,
                TooltipText       = tooltip,
                CustomMinimumSize = new Vector2(UITheme.Scaled(104), UITheme.Scaled(UITheme.ToolbarButtonSize)),
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
            // v0.8.0 — Farm button + crop chips. The active crop chip lights up
            // only while the Farm tool itself is active, so the player can read
            // both "am I farming?" and "which crop?" from the pressed-states.
            bool farming = _toolbar.ActiveTool == DesignationToolbar.Tool.Farm;
            _farmBtn.SetPressedNoSignal(farming);
            foreach (var (crop, chip) in _cropChips)
                chip.SetPressedNoSignal(farming && _toolbar.ActiveCrop == crop);
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
