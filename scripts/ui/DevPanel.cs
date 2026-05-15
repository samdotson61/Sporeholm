using Godot;
using System.Collections.Generic;
using SmurfulationC.Simulation.Items;
using SmurfulationC.UI;
using SmurfulationC.World;

namespace SmurfulationC.UI
{
    // v0.4.32 — RimWorld-style Developer Mode panel. Floating right-side
    // panel rendered above the HUD when DevMode.IsEnabled. F12 toggles
    // visibility; closing the panel doesn't disable dev mode (settings
    // toggle does). All actions route through SimulationManager.Dev*
    // methods — no direct sim-thread state mutation here.
    //
    // Categories:
    //   • Simulation   — pause / tick-once / speed bursts
    //   • Selected     — manipulate currently-selected smurf (fill, drain,
    //                    kill, mood spawns, yield)
    //   • Spawn        — drop items / spawn smurfs at the cursor
    //   • Map          — region rebuild + stubs for future overlays
    //   • Visualize    — disabled placeholder toggles (pathing, regions,
    //                    occupancy, claims)
    //   • Future       — disabled placeholders for Phase 7+ systems
    //                    (combat, weather, raids, traders, fire, disease)
    public partial class DevPanel : Control
    {
        // GameController plugs both refs in after construction; the panel
        // pulls cursor pos + selected smurf names through them as needed.
        public SimulationManager?   Sim   { get; set; }
        public Godot.Node2D?        MapNode { get; set; }   // for cursor → tile
        public System.Func<IReadOnlyCollection<string>>? GetSelectedSmurfs { get; set; }

        // Live status row at the top — "Dev Mode active · F12 to hide".
        private Label _statusLabel = null!;
        private Label _logLabel    = null!;  // tail of last action's result
        private VBoxContainer _content = null!;

        private static readonly Color PanelBg     = new(0.05f, 0.05f, 0.08f, 0.94f);
        private static readonly Color HeaderCol   = new(0.95f, 0.78f, 0.30f);
        private static readonly Color BtnFg       = new(0.92f, 0.92f, 0.92f);
        private static readonly Color BtnFgDim    = new(0.55f, 0.55f, 0.58f);
        private static readonly Color StubTooltipCol = new(0.70f, 0.70f, 0.30f);

        public override void _Ready()
        {
            // v0.4.52b — LEFT edge, top → bottom. Width fixed at 240 px.
            // Was right-anchored (v0.4.32 → v0.4.52); Sam reported the
            // dev panel obscured the SmurfCardPanel + TileInfoOverlay +
            // TilePropertiesPanel (all top-right) so when he clicked
            // dev-mode actions like "Fill needs" or "+Mood" he couldn't
            // see the smurf-card values changing in real time — hence
            // the "buttons don't do anything" impression. The buttons
            // were working; the visual feedback was hidden under this
            // panel. Moving to the left puts the dev panel below the
            // top-left HUD capsule and well clear of the bottom-left
            // MessageLog (which starts ~170 px from viewport bottom;
            // dev panel ends at ~268 px from viewport bottom). The
            // SmurfCardPanel + the rest of the right-side UI now stay
            // fully visible while dev actions fire.
            AnchorLeft   = 0f; AnchorRight  = 0f;
            AnchorTop    = 0f; AnchorBottom = 1f;
            OffsetLeft   = UITheme.EdgeInset;
            OffsetRight  = UITheme.EdgeInset + 240f;
            OffsetTop    = UITheme.EdgeInset + UITheme.Scaled(110);
            OffsetBottom = -UITheme.EdgeInset - UITheme.Scaled(260);
            MouseFilter  = MouseFilterEnum.Stop;

            var panel = new PanelContainer();
            panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var style = new StyleBoxFlat { BgColor = PanelBg };
            style.SetBorderWidthAll(2);
            style.BorderColor = HeaderCol;
            style.SetCornerRadiusAll(8);
            panel.AddThemeStyleboxOverride("panel", style);
            AddChild(panel);

            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical   = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            panel.AddChild(scroll);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   10);
            margin.AddThemeConstantOverride("margin_right",  10);
            margin.AddThemeConstantOverride("margin_top",    10);
            margin.AddThemeConstantOverride("margin_bottom", 10);
            margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(margin);

            _content = new VBoxContainer();
            _content.AddThemeConstantOverride("separation", 6);
            margin.AddChild(_content);

            // Title + status
            _statusLabel = MakeText("Developer Mode  ·  F12 to hide", 12, HeaderCol);
            _content.AddChild(_statusLabel);

            // v0.4.51 — log moved to the top (was at the bottom past several
            // sections). User report: clicking dev-panel buttons "did
            // nothing" — actually the buttons fired and the log label
            // showed the result, but the label was scrolled off-screen
            // beneath the Map / Visualize / Future stub sections. Pinning
            // the log directly under the header makes every action's
            // outcome immediately visible without scrolling, and surfaces
            // the "No smurf selected" / "Cursor not on map" guards so
            // the player understands the action was a no-op.
            _logLabel = MakeText("(idle)", 10, BtnFg);
            _logLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _content.AddChild(_logLabel);
            _content.AddChild(MakeRule());

            BuildSimSection();
            _content.AddChild(MakeRule());
            BuildSelectedSection();
            _content.AddChild(MakeRule());
            BuildSpawnSection();
            _content.AddChild(MakeRule());
            BuildMapSection();
            _content.AddChild(MakeRule());
            BuildVisualizeSection();
            _content.AddChild(MakeRule());
            BuildFutureSection();

            DevMode.Changed += OnDevModeChanged;
            OnDevModeChanged();
        }

        public override void _ExitTree()
        {
            DevMode.Changed -= OnDevModeChanged;
        }

        private void OnDevModeChanged()
        {
            Visible = DevMode.IsEnabled;
        }

        // F12 hotkey handler — GameController.InputHandler calls this.
        public void HandleHotkey()
        {
            if (!DevMode.IsEnabled) return;
            Visible = !Visible;
        }

        // ── Section builders ────────────────────────────────────────────────────

        private void BuildSimSection()
        {
            _content.AddChild(MakeText("Simulation", 11, HeaderCol));
            var row1 = MakeRow();
            row1.AddChild(MakeBtn("Pause/Resume", () =>
            {
                if (Sim == null) return;
                bool paused = Sim.DevTogglePause();
                Log(paused ? "Sim paused" : "Sim resumed");
            }));
            row1.AddChild(MakeBtn("Tick 1×", () =>
            {
                if (Sim == null) return;
                Sim.SetSpeed(1f);
                Log("Speed → 1×");
            }));
            _content.AddChild(row1);

            var row2 = MakeRow();
            row2.AddChild(MakeBtn("5×",   () => { Sim?.SetSpeed(5f);   Log("Speed → 5×"); }));
            row2.AddChild(MakeBtn("25×",  () => { Sim?.SetSpeed(25f);  Log("Speed → 25×"); }));
            row2.AddChild(MakeBtn("100×", () => { Sim?.SetSpeed(100f); Log("Speed → 100× burst"); }));
            _content.AddChild(row2);
        }

        private void BuildSelectedSection()
        {
            _content.AddChild(MakeText("Selected Smurf", 11, HeaderCol));
            var row1 = MakeRow();
            row1.AddChild(MakeBtn("Fill needs",  () => ForEachSelected(n => Sim?.DevFillNeeds(n),  "Filled needs")));
            row1.AddChild(MakeBtn("Drain needs", () => ForEachSelected(n => Sim?.DevDrainNeeds(n), "Drained needs")));
            _content.AddChild(row1);

            var row2 = MakeRow();
            row2.AddChild(MakeBtn("+Mood (TastyMeal)",
                () => ForEachSelected(n => Sim?.DevAddThought(n, "TastyMeal"),  "+Mood thought added")));
            row2.AddChild(MakeBtn("-Mood (TaskAbandoned)",
                () => ForEachSelected(n => Sim?.DevAddThought(n, "TaskAbandoned"), "-Mood thought added")));
            _content.AddChild(row2);

            var row3 = MakeRow();
            row3.AddChild(MakeBtn("Force yield (60t)",
                () => ForEachSelected(n => Sim?.DevForceYield(n, 60), "Yielding for 60 ticks")));
            row3.AddChild(MakeBtn("Kill",
                () => ForEachSelected(n => Sim?.DevKillSmurf(n), "Killed selected")));
            _content.AddChild(row3);
        }

        private void BuildSpawnSection()
        {
            _content.AddChild(MakeText("Spawn at Cursor", 11, HeaderCol));

            var row1 = MakeRow();
            row1.AddChild(MakeBtn("50 Smurfberries", () =>
                SpawnAtCursor(ItemKind.Food, "Smurfberry", "Plant", "Smurfberry", 50)));
            row1.AddChild(MakeBtn("50 Granite", () =>
                SpawnAtCursor(ItemKind.Material, "StoneBlock", "Stone", "Granite", 50)));
            _content.AddChild(row1);

            var row2 = MakeRow();
            row2.AddChild(MakeBtn("50 DeadWood", () =>
                SpawnAtCursor(ItemKind.Material, "WoodBlock", "Wood", "DeadWood", 50)));
            row2.AddChild(MakeBtn("10 Raw Essence", () =>
                SpawnAtCursor(ItemKind.Magic, "RawEssence", "Magic", "Essence", 10)));
            _content.AddChild(row2);

            var row3 = MakeRow();
            row3.AddChild(MakeBtn("Spawn smurf",  SpawnSmurfAtCursor));
            row3.AddChild(MakeBtn("Reveal map",   () => Log("Reveal map: no fog system yet (stub)")));
            _content.AddChild(row3);
        }

        private void BuildMapSection()
        {
            _content.AddChild(MakeText("Map", 11, HeaderCol));
            var row = MakeRow();
            row.AddChild(MakeBtn("Rebuild regions", () =>
            {
                var map = WorldState.Instance?.CurrentLocalMap;
                if (map == null) { Log("No local map bound"); return; }
                // Touch a tile to dirty regions, then re-ensure.
                Log("Regions rebuild requested (no-op stub — regions rebuild lazily on next query)");
            }));
            row.AddChild(MakeBtn("Force redraw", () =>
            {
                Log("Force redraw — call site stub");
            }));
            _content.AddChild(row);
        }

        private void BuildVisualizeSection()
        {
            _content.AddChild(MakeText("Visualize (stubs)", 11, HeaderCol));
            _content.AddChild(MakeStubToggle("Pathfinding overlay"));
            _content.AddChild(MakeStubToggle("Region overlay"));
            _content.AddChild(MakeStubToggle("Occupancy grid"));
            _content.AddChild(MakeStubToggle("Designation claims"));
        }

        private void BuildFutureSection()
        {
            _content.AddChild(MakeText("Future Systems (stubs)", 11, HeaderCol));
            _content.AddChild(MakeStubBtn("Trigger raid",       "Phase 7 — Combat / raids"));
            _content.AddChild(MakeStubBtn("Force storm",        "Phase 10 — Weather"));
            _content.AddChild(MakeStubBtn("Spawn trader",       "Phase 11 — Trade caravans"));
            _content.AddChild(MakeStubBtn("Start fire",         "Phase 10 — Fire propagation"));
            _content.AddChild(MakeStubBtn("Cause disease",      "Phase 9 — Health / disease"));
            _content.AddChild(MakeStubBtn("Spawn hostile mob",  "Phase 7 — Hostile entities"));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private void ForEachSelected(System.Action<string> act, string okMsg)
        {
            if (GetSelectedSmurfs == null) { Log("No selection callback"); return; }
            var sel = GetSelectedSmurfs();
            if (sel == null || sel.Count == 0) { Log("No smurf selected"); return; }
            int n = 0;
            foreach (var name in sel) { act(name); n++; }
            Log($"{okMsg}  ×{n}");
        }

        private (int tx, int ty)? CursorTile()
        {
            var map = WorldState.Instance?.CurrentLocalMap;
            if (map == null || MapNode == null) return null;
            var world = MapNode.GetGlobalMousePosition();
            int tx = (int)(world.X / LocalMap.TileSize);
            int ty = (int)(world.Y / LocalMap.TileSize);
            if (!map.InBounds(tx, ty)) return null;
            return (tx, ty);
        }

        private void SpawnAtCursor(ItemKind kind, string subType, string matFamily, string matSubType, int qty)
        {
            if (Sim == null) { Log("No sim"); return; }
            var tile = CursorTile();
            if (tile == null) { Log("Cursor not on map"); return; }
            var landed = Sim.DevSpawnItem(tile.Value.tx, tile.Value.ty, kind, subType, matFamily, matSubType, qty);
            if (landed.HasValue)
                Log($"Spawned {qty}× {subType} at ({landed.Value.X},{landed.Value.Y})");
            else
                Log("Spawn rejected");
        }

        private void SpawnSmurfAtCursor()
        {
            if (Sim == null) { Log("No sim"); return; }
            var tile = CursorTile();
            if (tile == null) { Log("Cursor not on map"); return; }
            var pos = new Vector2(
                tile.Value.tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                tile.Value.ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            Sim.DevSpawnSmurf(pos);
            Log($"Spawned smurf at ({tile.Value.tx},{tile.Value.ty})");
        }

        private void Log(string msg)
        {
            if (_logLabel != null) _logLabel.Text = "→ " + msg;
        }

        // ── UI helpers ──────────────────────────────────────────────────────────

        private static Label MakeText(string text, int size, Color col)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", col);
            l.AddThemeFontSizeOverride("font_size", size);
            return l;
        }

        private static HBoxContainer MakeRow()
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 4);
            return h;
        }

        private static HSeparator MakeRule()
        {
            var sep = new HSeparator();
            sep.AddThemeColorOverride("color", new Color(HeaderCol.R, HeaderCol.G, HeaderCol.B, 0.35f));
            return sep;
        }

        private Button MakeBtn(string text, System.Action action)
        {
            var b = new Button
            {
                Text                = text,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(0, 22),
            };
            b.AddThemeFontSizeOverride("font_size", 10);
            b.AddThemeColorOverride("font_color", BtnFg);
            b.Pressed += () => { try { action(); } catch (System.Exception e) { Log($"err: {e.Message}"); } };
            return b;
        }

        private Button MakeStubBtn(string text, string tooltip)
        {
            var b = new Button
            {
                Text                = text + "  (stub)",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Disabled            = true,
                CustomMinimumSize   = new Vector2(0, 20),
                TooltipText         = tooltip + " — wiring lands when the system does.",
            };
            b.AddThemeFontSizeOverride("font_size", 10);
            b.AddThemeColorOverride("font_color_disabled", BtnFgDim);
            return b;
        }

        private Button MakeStubToggle(string text)
        {
            var b = new Button
            {
                Text                = text + "  (stub)",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ToggleMode          = true,
                Disabled            = true,
                CustomMinimumSize   = new Vector2(0, 20),
                TooltipText         = "Overlay rendering hooks aren't in place yet — toggle is a placeholder.",
            };
            b.AddThemeFontSizeOverride("font_size", 10);
            b.AddThemeColorOverride("font_color_disabled", BtnFgDim);
            return b;
        }
    }
}
