using Godot;
using System.Collections.Generic;
using Sporeholm.Simulation.Items;
using Sporeholm.UI;
using Sporeholm.World;

namespace Sporeholm.UI
{
    // v0.4.32 — RimWorld-style Developer Mode panel. Floating right-side
    // panel rendered above the HUD when DevMode.IsEnabled. F12 toggles
    // visibility; closing the panel doesn't disable dev mode (settings
    // toggle does). All actions route through SimulationManager.Dev*
    // methods — no direct sim-thread state mutation here.
    //
    // Categories:
    //   • Simulation   — pause / tick-once / speed bursts
    //   • Selected     — manipulate currently-selected shroomp (fill, drain,
    //                    kill, mood spawns, yield)
    //   • Spawn        — drop items / spawn shroomps at the cursor
    //   • Map          — region rebuild + stubs for future overlays
    //   • Visualize    — disabled placeholder toggles (pathing, regions,
    //                    occupancy, claims)
    //   • Future       — disabled placeholders for Phase 7+ systems
    //                    (combat, weather, raids, traders, fire, disease)
    public partial class DevPanel : Control
    {
        // GameController plugs both refs in after construction; the panel
        // pulls cursor pos + selected shroomp names through them as needed.
        public SimulationManager?   Sim   { get; set; }
        public Godot.Node2D?        MapNode { get; set; }   // for cursor → tile
        public System.Func<IReadOnlyCollection<string>>? GetSelectedShroomps { get; set; }

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
            // dev panel obscured the ShroompCardPanel + TileInfoOverlay +
            // TilePropertiesPanel (all top-right) so when he clicked
            // dev-mode actions like "Fill needs" or "+Mood" he couldn't
            // see the shroomp-card values changing in real time — hence
            // the "buttons don't do anything" impression. The buttons
            // were working; the visual feedback was hidden under this
            // panel. Moving to the left puts the dev panel below the
            // top-left HUD capsule and well clear of the bottom-left
            // MessageLog (which starts ~170 px from viewport bottom;
            // dev panel ends at ~268 px from viewport bottom). The
            // ShroompCardPanel + the rest of the right-side UI now stay
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
            // the "No shroomp selected" / "Cursor not on map" guards so
            // the player understands the action was a no-op.
            _logLabel = MakeText("(idle)", 10, BtnFg);
            _logLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _content.AddChild(_logLabel);
            _content.AddChild(MakeRule());

            BuildSimSection();
            _content.AddChild(MakeRule());
            BuildPerfSection();
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

        // v0.5.84 — Perf section. Polls SimulationManager.GetPerfCounters
        // every UI frame and computes deltas between polls so a number that
        // grows over a polling window is shown as an instantaneous rate (e.g.
        // "A* calls/tick" = calls-since-last-poll / ticks-since-last-poll).
        // Sam: "There is also noticeable pawn movement/sim slowdown with 50
        // pawns attempting to move around the screen at once." Diagnostic
        // first — surface where tick time actually goes before guessing.
        private Label? _perfTickLabel;
        private Label? _perfBehaviorLabel;
        private Label? _perfNeedsLabel;
        private Label? _perfPathLabel;
        private Label? _perfPathRateLabel;
        private SimulationManager.PerfCounters _perfLast;
        private bool _perfLastValid;
        private double _perfPollAccumSec;

        private void BuildPerfSection()
        {
            _content.AddChild(MakeText("Performance", 11, HeaderCol));
            _perfTickLabel     = MakeText("(no data)", 10, BtnFg);
            _perfBehaviorLabel = MakeText("(no data)", 10, BtnFg);
            _perfNeedsLabel    = MakeText("(no data)", 10, BtnFg);
            _perfPathLabel     = MakeText("(no data)", 10, BtnFg);
            _perfPathRateLabel = MakeText("(no data)", 10, BtnFg);
            _content.AddChild(_perfTickLabel);
            _content.AddChild(_perfBehaviorLabel);
            _content.AddChild(_perfNeedsLabel);
            _content.AddChild(_perfPathLabel);
            _content.AddChild(_perfPathRateLabel);
        }

        public override void _Process(double delta)
        {
            if (!Visible || Sim == null) return;
            _perfPollAccumSec += delta;
            if (_perfPollAccumSec < 0.25) return;   // poll 4× per second
            _perfPollAccumSec = 0;

            var cur = Sim.GetPerfCounters();
            if (!_perfLastValid)
            {
                _perfLast = cur;
                _perfLastValid = true;
                return;
            }
            long dTicks  = cur.TicksRun     - _perfLast.TicksRun;
            long dTotal  = cur.TotalTickMicros - _perfLast.TotalTickMicros;
            long dBeh    = cur.BehaviorMicros  - _perfLast.BehaviorMicros;
            long dNeeds  = cur.NeedsMicros     - _perfLast.NeedsMicros;
            long dCalls  = cur.PfCalls      - _perfLast.PfCalls;
            long dExp    = cur.PfExpansions - _perfLast.PfExpansions;
            long dSucc   = cur.PfSuccesses  - _perfLast.PfSuccesses;
            long dFail   = cur.PfFailures   - _perfLast.PfFailures;
            _perfLast = cur;

            if (dTicks <= 0)
            {
                _perfTickLabel!.Text     = "Tick:  (paused)";
                _perfBehaviorLabel!.Text = "  Behavior:  —";
                _perfNeedsLabel!.Text    = "  Needs:     —";
                _perfPathLabel!.Text     = $"A*:  {cur.PfCalls} total";
                _perfPathRateLabel!.Text = "  rate: (paused)";
                return;
            }
            double tickMs = (dTotal / 1000.0) / dTicks;
            double behMs  = (dBeh   / 1000.0) / dTicks;
            double needMs = (dNeeds / 1000.0) / dTicks;
            double pCalls = (double)dCalls / dTicks;
            double pExpPerCall = dCalls > 0 ? (double)dExp / dCalls : 0;
            double succPct = (dSucc + dFail) > 0 ? 100.0 * dSucc / (dSucc + dFail) : 0;
            int live = cur.LiveShroomps;
            double behPerShroomp = live > 0 ? behMs / live : 0;

            _perfTickLabel!.Text     = $"Tick:  {tickMs:F2} ms  · {live} alive";
            _perfBehaviorLabel!.Text = $"  Behavior:  {behMs:F2} ms  ({behPerShroomp:F3} /shr)";
            _perfNeedsLabel!.Text    = $"  Needs:     {needMs:F2} ms";
            _perfPathLabel!.Text     = $"A*:  {pCalls:F1}/tick  ·  {pExpPerCall:F0} exp/call";
            _perfPathRateLabel!.Text = $"  success {succPct:F0}%  ·  {cur.PfCalls} lifetime";
        }

        private void BuildSelectedSection()
        {
            _content.AddChild(MakeText("Selected Shroomp", 11, HeaderCol));
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
                () => ForEachSelected(n => Sim?.DevKillShroomp(n), "Killed selected")));
            _content.AddChild(row3);

            // v0.5.80 — Damage-to-Down button. Sam: "Dev panel should have
            // a button, not a slider, that damages the shroomp until they
            // are down. The thresholds should only be affected by traits
            // and code changes." Replaces the v0.5.79 SpinBox slider — the
            // threshold is now a private const in BehaviorSystem with
            // per-shroomp trait modifiers (Brawny / Stoic / Accident-Prone)
            // via DownThresholdFor.
            var row4 = MakeRow();
            row4.AddChild(MakeBtn("Damage to Down",
                () => ForEachSelected(n => Sim?.DevDamageToDown(n), "Damaged to Down")));
            _content.AddChild(row4);
        }

        private void BuildSpawnSection()
        {
            _content.AddChild(MakeText("Spawn at Cursor", 11, HeaderCol));

            var row1 = MakeRow();
            row1.AddChild(MakeBtn("50 Capberries", () =>
                SpawnAtCursor(ItemKind.Food, "Capberry", "Plant", "Capberry", 50)));
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
            row3.AddChild(MakeBtn("Spawn shroomp",  SpawnShroompAtCursor));
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
            _content.AddChild(MakeStubBtn("Cause disease",      "Phase 8 — Health / disease"));
            _content.AddChild(MakeStubBtn("Spawn hostile mob",  "Phase 7 — Hostile entities"));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private void ForEachSelected(System.Action<string> act, string okMsg)
        {
            if (GetSelectedShroomps == null) { Log("No selection callback"); return; }
            var sel = GetSelectedShroomps();
            if (sel == null || sel.Count == 0) { Log("No shroomp selected"); return; }
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

        private void SpawnShroompAtCursor()
        {
            if (Sim == null) { Log("No sim"); return; }
            var tile = CursorTile();
            if (tile == null) { Log("Cursor not on map"); return; }
            var pos = new Vector2(
                tile.Value.tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                tile.Value.ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            Sim.DevSpawnShroomp(pos);
            Log($"Spawned shroomp at ({tile.Value.tx},{tile.Value.ty})");
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
