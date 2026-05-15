using Godot;

namespace SmurfulationC.UI
{
    // v0.3.21 — shared tool identifier used by DesignationToolbar (UI input
    // mode), GameController (drag-box dispatch), SimulationManager.DesignateRect
    // (bulk apply), and DesignationOverlay (per-tile glyph). Living in UITheme
    // alongside the other UI constants keeps both UI and sim layers depending
    // on the same enum without a cross-namespace cycle.
    public enum DesignationTool
    {
        None,
        Gather,
        Excavate,
        Remove,
        // v0.3.38 — new orders matching RimWorld's "Chop wood" and
        // "Cut plants" verbs:
        //   ChopWood — flag wood-yielding shrooms (LargeMushroom /
        //              LargeSandshroom / PalmShroom). Treated like a tree
        //              fell — vegetation removed, fungal-wood resource
        //              credited, tile passability flips when the cap clears.
        //   Cut     — flag ANY vegetation for clearing. No resource drop;
        //              just removes the plant. Used to clear a region.
        ChopWood,
        Cut,
        // v0.4.12 — Haul. Player drags a box over a region with dropped
        // items; every item in the rect gets a priority-haul flag set in
        // HaulSystem. Priority items bypass the local-radius search cap
        // and get picked up by the first available hauler regardless of
        // distance. Useful for cleaning up after a long-distance gather
        // operation that left items scattered past the auto-haul radius.
        Haul,
        // v0.5.0 (Phase 5A — rimport N1) — Stockpile zone painter. Player
        // drags a rectangle to add cells to a stockpile zone; auto-creates
        // a new zone with default settings (StoragePriority.Normal,
        // accepts all stackable kinds). Re-paint over existing zone cells
        // is idempotent. Right-click via the Remove tool clears stockpile
        // membership from a cell.
        Stockpile,
    }

    // Roadmap §3.x.7 — single source of truth for floating-panel theme constants.
    // Every new Phase-3.x UI element references these so the visual identity stays
    // consistent across HUD / designation toolbar / alerts pane / smurf card /
    // tile tooltip / message log without each component re-deriving its own.
    public static class UITheme
    {
        // ── Distance / shape ────────────────────────────────────────────────
        // Roadmap §3.x.1: ~16 px screen-edge insets, ~10 px corner radius.
        public const int EdgeInset    = 16;
        public const int PanelCorner  = 10;
        public const int PanelBorder  = 1;
        public const int ShadowSize   = 6;
        public const int ContentPadX  = 14;
        public const int ContentPadY  = 10;

        // ── Colours ─────────────────────────────────────────────────────────
        // Dark parchment background at ~92 % opacity (play area shows through).
        public static readonly Color PanelBg     = new(0.08f, 0.06f, 0.03f, 0.92f);
        // Slightly lighter on hover / focus.
        public static readonly Color PanelHover  = new(0.12f, 0.09f, 0.05f, 0.94f);
        // Active / pressed tool button background.
        public static readonly Color PanelActive = new(0.28f, 0.18f, 0.06f, 0.96f);
        // Parchment-gold border at 30 % alpha — subtle on dark scenes.
        public static readonly Color PanelBorderColour = new(0.95f, 0.80f, 0.28f, 0.30f);
        // Brighter gold for selected / focused border.
        public static readonly Color PanelBorderFocus  = new(0.95f, 0.80f, 0.28f, 0.85f);
        // Drop shadow.
        public static readonly Color ShadowColour      = new(0.0f, 0.0f, 0.0f, 0.45f);

        // ── Text ────────────────────────────────────────────────────────────
        public static readonly Color TextPrimary   = new(0.95f, 0.89f, 0.70f);     // parchment
        public static readonly Color TextMuted     = new(0.60f, 0.50f, 0.32f);     // muted gold-brown
        public static readonly Color TextAccent    = new(0.95f, 0.80f, 0.28f);     // gold (titles)
        public static readonly Color TextWarn      = new(0.95f, 0.50f, 0.20f);     // amber (mild alerts)
        public static readonly Color TextDanger    = new(0.95f, 0.20f, 0.20f);     // red (critical)

        // ── Tile-tooltip overlay colour ─────────────────────────────────────
        public static readonly Color TooltipBg     = new(0.04f, 0.03f, 0.02f, 0.95f);

        // ── Sizing ──────────────────────────────────────────────────────────
        public const int ToolbarButtonSize = 38;     // designation toolbar
        public const int AlertRowHeight    = 28;     // alerts pane entry
        public const int HudHeight         = 44;     // floating HUD capsule

        // ── UI Size scaling (Settings → Gameplay → UI Size) ─────────────────
        // v0.3.16 implemented UI Size as a CanvasLayer transform scale, which
        // shrank the layout toward the top-left corner instead of just making
        // the content smaller. v0.3.19 replaces that with a per-component
        // font/size multiplier: `UIScale` is read once at scene-ready, and
        // each component multiplies its `font_size` and `CustomMinimumSize`
        // values through `Scaled()` so anchors stay at the viewport edges
        // while inner content (text, buttons, paddings) shrinks.
        public static float UIScale { get; private set; } = 1.0f;

        // Fires whenever `SetUIScale` is called with a different value. Phase-3.x
        // components subscribe in their `_Ready` and tear down + rebuild their
        // content when the scale changes, so a Settings → UI Size adjustment
        // takes effect immediately without requiring a scene reload.
        public static event System.Action? UIScaleChanged;

        public static void SetUIScale(float scale)
        {
            float clamped = scale < 0.05f ? 0.05f : scale;
            if (Mathf.IsEqualApprox(clamped, UIScale)) return;
            UIScale = clamped;
            UIScaleChanged?.Invoke();
        }

        // Scaled integer (floor at 1 so we never produce a zero-size element).
        public static int Scaled(int basePx) => Mathf.Max(1, (int)(basePx * UIScale));

        // Scaled float (for fractional positions).
        public static float ScaledF(float basePx) => Mathf.Max(1f, basePx * UIScale);

        // Scaled Vector2 (for CustomMinimumSize convenience).
        public static Vector2 ScaledVec(float x, float y) => new(ScaledF(x), ScaledF(y));
    }
}
