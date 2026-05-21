using Godot;

namespace Sporeholm.UI
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
        // v0.5.19 (Phase 5B — rimport N3) — Construction blueprints.
        // Player drags a rectangle to plant Wall / Floor blueprints on
        // every passable tile in the box. Blueprints sit as ghost-state
        // StructureSlots until the Crafter task picks them up, delivers
        // materials (Stone or Wood from colony inventory), and advances
        // BuildProgress to 100. Wall blueprints become impassable at
        // completion; Floor blueprints stay passable but get a distinct
        // visual + cosmetic boost.
        BuildWall,
        BuildFloor,
        // v0.5.20 (Phase 5C) — Door painter. Same Build pipeline as
        // Wall / Floor but the built result is passable (cosmetic for
        // v0.5.20; future Phase 7 combat will use it for line-of-sight).
        BuildDoor,
        // v0.5.21 (Phase 5D) — Shelf painter. First storage-furniture
        // structure; built shelf adds +1 stack capacity to its tile via
        // the IHaulDestination interface.
        BuildShelf,
        // v0.5.22 (Phase 5E) — Workbench painter. Crafters use workbenches
        // to run Cook recipes (raw food → prepared meal).
        BuildWorkbench,
        // v0.5.24 (Phase 5G) — Bonfire painter. Heat source for room
        // temperature simulation; cooking-quality bonus when adjacent
        // to a workbench.
        BuildBonfire,
        // v0.5.35 (Phase 5 arc) — Bed painter. Shroomps sleep on built beds
        // for full rest effectiveness (1.0×) + a positive "WellRested"
        // mood thought. Without a bed, shroomps sleep on the ground (0.8×
        // effectiveness + "SleptOnGround" mood penalty).
        BuildBed,
        // v0.5.36 (Phase 5 arc) — Joy furniture. Three starter recreation
        // structures: MeditationShrine (Solitary), ShroomBoard (Cerebral),
        // GossipBench (Social). Shroomps route to these during idle to
        // restore Joy faster than the default 6-activity table.
        BuildMeditationShrine,
        BuildShroomBoard,
        BuildGossipBench,
        // v0.5.37 (Phase 5 arc) — Table painter. Shroomps prefer to eat at
        // tables; eating without a table triggers the AteWithoutTable
        // mood penalty (RimWorld pattern).
        BuildTable,
        // v0.5.84t — Torch painter. Cheap floor-tile light source +
        // small room temperature offset (+2°C per torch).
        BuildTorch,
        // v0.6.2 (Phase 5.6 ship) — Cooking Table painter. Dedicated cook
        // station for the new Cooking skill: Cooks prepare meals at full
        // speed here. Bonfire is a half-speed fallback so a bare colony can
        // still cook before a Cooking Table is built.
        BuildCookingTable,
        // Demolish removes built structures (Wall / Floor / Door / Shelf
        // / Workbench / Bonfire / Bed / Joy furniture / Table) AND cancels
        // pending blueprints. v0.5.20 added partial material refund (50%
        // of original cost) for completed structures.
        Demolish,
        // v0.5.25 (Phase 5C polish — rimport.md N6) — Allowed-area
        // painter. Per-shroomp bitmap that restricts where work tasks can
        // be assigned. Paint = mark tile as ALLOWED for the currently
        // selected shroomp; right-click / use a future "ForbidArea"
        // erase mode to flip to disallowed. Operates on the currently
        // selected single shroomp via GameController.SelectedShroompNames.
        AllowedArea,
    }

    // Roadmap §3.x.7 — single source of truth for floating-panel theme constants.
    // Every new Phase-3.x UI element references these so the visual identity stays
    // consistent across HUD / designation toolbar / alerts pane / shroomp card /
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
