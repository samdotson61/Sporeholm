using Godot;
using System;
using Sporeholm.Simulation;
using Sporeholm.Simulation.Entities;

namespace Sporeholm.UI
{
    // v0.6.2 — wildlife inspector card. Compact single-tab panel shown when
    // the player clicks on an entity in the world. Mirrors the dark-brown
    // floating-panel theme + tab styling used by ShroompCardPanel /
    // CreditsPanel / TilePropertiesPanel; uses the same UITheme palette so
    // any global theme tweak propagates here automatically.
    //
    // Layout (top to bottom):
    //   • Header row — species name + close button (×) at top-right.
    //   • Description — italic two-line blurb from EntityDef.Description.
    //   • State + Disposition row.
    //   • Health   — labelled ProgressBar (current/max).
    //   • Mood     — single-word label from EntitySnapshot.MoodLabel.
    //   • Needs    — Nutrition + Rest as labelled ProgressBars.
    //
    // The panel is mutually exclusive with ShroompCardPanel + TilePropertiesPanel
    // (they share top-right screen space). GameController toggles `.Visible`
    // and calls Show / Refresh on snapshot ticks.
    //
    // v0.6.2u — rewrite. Previously used raw Panel + manual stylebox that
    // didn't render against the world (transparent background, invisible
    // ProgressBars). New version uses PanelContainer + FloatingPanelStyle.Make
    // matching ShroompCardPanel's pattern, plus explicit fill + track
    // styleboxes on each ProgressBar so the bars actually fill visibly.
    public partial class EntityCardPanel : Control
    {
        // v0.6.2u — emitted when the card closes (× button, Esc, auto-close
        // on selected-entity death). GameController catches this to clear
        // the world-space selection brackets on EntityColonyView.
        [Signal] public delegate void ClosedEventHandler();

        private static readonly Color Gold     = UITheme.TextAccent;
        private static readonly Color Body     = UITheme.TextPrimary;
        private static readonly Color TextDim  = UITheme.TextMuted;

        // ── UI handles ────────────────────────────────────────────────
        private Label       _nameLabel        = null!;
        private Label       _descLabel        = null!;
        private Label       _stateLabel       = null!;
        private Label       _moodLabel        = null!;
        private ProgressBar _healthBar        = null!;
        private Label       _healthBarLabel   = null!;
        private ProgressBar _nutritionBar     = null!;
        private Label       _nutritionLabel   = null!;
        private ProgressBar _restBar          = null!;
        private Label       _restLabel        = null!;
        private Button      _closeBtn         = null!;

        // v0.6.2u — entity id currently displayed; null when closed. Public so
        // EntityColonyView's selection-bracket render can query "is this entity
        // selected?" without going through a signal-back round-trip.
        public Guid? SelectedId { get; private set; }

        public override void _Ready()
        {
            // v0.6.2w — anchor matches ShroompCardPanel: bottom-right slot,
            // 320 wide × 320 tall, 240 px above the bottom edge. Sharing
            // these exact coordinates means the entity card and shroomp
            // card occupy the same screen space (mutually exclusive — only
            // one is ever open at a time per the click-handler rules) and
            // neither overlaps the top-right TileInfoOverlay hover readout.
            AnchorLeft   = 1f; AnchorTop    = 1f;
            AnchorRight  = 1f; AnchorBottom = 1f;
            OffsetLeft   = -320f;
            OffsetRight  = -UITheme.EdgeInset;
            OffsetBottom = -240f;
            OffsetTop    = OffsetBottom - 320f;
            Visible      = false;
            MouseFilter  = MouseFilterEnum.Pass;

            // ── Background ────────────────────────────────────────────
            // v0.6.2u — PanelContainer with FloatingPanelStyle so the card
            // gets the same dark-brown look as ShroompCardPanel + HUD +
            // BottomTabPanel. Raw Panel + manual stylebox (the v0.6.2
            // initial version) rendered transparently in-game; switching to
            // PanelContainer fixes the visibility.
            var bg = new PanelContainer();
            bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            bg.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
            AddChild(bg);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   8);
            margin.AddThemeConstantOverride("margin_right",  8);
            margin.AddThemeConstantOverride("margin_top",    8);
            margin.AddThemeConstantOverride("margin_bottom", 8);
            bg.AddChild(margin);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 4);
            margin.AddChild(vbox);

            // ── Header: name + close button on one row ────────────────
            var headerRow = new HBoxContainer();
            _nameLabel = NewTitleLabel("Unknown Creature");
            _nameLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            headerRow.AddChild(_nameLabel);

            _closeBtn = new Button
            {
                Text = "×",
                CustomMinimumSize = new Vector2(24, 24),
                FocusMode = FocusModeEnum.None,
                TooltipText = "Close",
            };
            _closeBtn.Pressed += () =>
            {
                Visible = false;
                SelectedId = null;
                EmitSignal(SignalName.Closed);
            };
            headerRow.AddChild(_closeBtn);
            vbox.AddChild(headerRow);

            // Description (auto-wrap) — sits below the title.
            _descLabel = NewBodyLabel("");
            _descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _descLabel.AddThemeColorOverride("font_color", TextDim);
            vbox.AddChild(_descLabel);

            // State row (e.g. "Wandering · Friendly").
            _stateLabel = NewBodyLabel("");
            _stateLabel.AddThemeColorOverride("font_color", TextDim);
            vbox.AddChild(_stateLabel);

            vbox.AddChild(NewDivider());

            // ── Health ────────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Health"));
            var (healthRow, healthBar, healthLabel) = NewLabelledBar(
                0, 100, null,
                fill:  new Color(0.70f, 0.20f, 0.20f),    // red fill (health)
                track: new Color(0.25f, 0.10f, 0.10f, 0.55f));
            _healthBar = healthBar;
            _healthBarLabel = healthLabel;
            vbox.AddChild(healthRow);

            vbox.AddChild(NewDivider());

            // ── Mood ─────────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Mood"));
            _moodLabel = NewBodyLabel("Calm");
            _moodLabel.AddThemeColorOverride("font_color", Body);
            vbox.AddChild(_moodLabel);

            vbox.AddChild(NewDivider());

            // ── Needs ────────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Needs"));
            var (nutritionRow, nutritionBar, nutritionLabel) = NewLabelledBar(
                0, 100, "Nutrition",
                fill:  new Color(0.55f, 0.55f, 0.20f),    // golden fill (food)
                track: new Color(0.20f, 0.20f, 0.08f, 0.55f));
            _nutritionBar   = nutritionBar;
            _nutritionLabel = nutritionLabel;
            vbox.AddChild(nutritionRow);
            var (restRow, restBar, restLabel) = NewLabelledBar(
                0, 100, "Rest",
                fill:  new Color(0.35f, 0.45f, 0.70f),    // muted blue (rest)
                track: new Color(0.12f, 0.16f, 0.28f, 0.55f));
            _restBar   = restBar;
            _restLabel = restLabel;
            vbox.AddChild(restRow);
        }

        // Open + lock-onto the given snapshot entity. Subsequent snapshot
        // ticks call Refresh(snap) which keeps the panel synced to live
        // health/needs/mood while the player has the card open.
        public void Show(EntitySnapshot e)
        {
            SelectedId = e.Id;
            Visible = true;
            ApplySnapshot(e);
        }

        public void Refresh(SimulationSnapshot snap)
        {
            if (!Visible || SelectedId == null) return;
            for (int i = 0; i < snap.Entities.Count; i++)
            {
                var e = snap.Entities[i];
                if (e.Id != SelectedId.Value) continue;
                ApplySnapshot(e);
                return;
            }
            // Entity is gone (died / despawned) — close the card.
            Close();
        }

        public void Close()
        {
            if (!Visible && SelectedId == null) return;
            Visible = false;
            SelectedId = null;
            EmitSignal(SignalName.Closed);
        }

        private void ApplySnapshot(EntitySnapshot e)
        {
            var def = EntityRegistry.Get(e.Kind);
            _nameLabel.Text  = def.DisplayName;
            _descLabel.Text  = def.Description;
            _stateLabel.Text = $"{e.State} · {def.Disposition}{(e.IsTamed ? " · Tamed" : "")}";

            // Health
            _healthBar.MaxValue = Mathf.Max(1, Mathf.Round(e.MaxHealth));
            _healthBar.Value    = Mathf.Clamp(Mathf.Round(e.Health), 0, _healthBar.MaxValue);
            _healthBarLabel.Text = $"{(int)e.Health} / {(int)e.MaxHealth}";

            // Mood
            _moodLabel.Text = e.MoodLabel;

            // Needs
            _nutritionBar.Value   = Mathf.Clamp(Mathf.Round(e.Nutrition), 0, 100);
            _nutritionLabel.Text  = $"{(int)e.Nutrition}";
            _restBar.Value        = Mathf.Clamp(Mathf.Round(e.Rest),      0, 100);
            _restLabel.Text       = $"{(int)e.Rest}";
        }

        // ── small UI helpers ──────────────────────────────────────────

        private static Label NewTitleLabel(string text)
        {
            var l = new Label
            {
                Text = text,
                MouseFilter = MouseFilterEnum.Pass,
            };
            l.AddThemeColorOverride("font_color", Gold);
            l.AddThemeFontSizeOverride("font_size", 16);
            return l;
        }

        private static Label NewSectionLabel(string text)
        {
            var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Pass };
            l.AddThemeColorOverride("font_color", Gold);
            l.AddThemeFontSizeOverride("font_size", 12);
            return l;
        }

        private static Label NewBodyLabel(string text)
        {
            var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Pass };
            l.AddThemeColorOverride("font_color", Body);
            l.AddThemeFontSizeOverride("font_size", 11);
            return l;
        }

        private static HSeparator NewDivider()
        {
            return new HSeparator { CustomMinimumSize = new Vector2(0, 4) };
        }

        // v0.6.2u — labelled ProgressBar row with explicit fill + track
        // styleboxes so the bars render visibly. ShroompCardPanel uses the
        // same pattern (its skill bars + need bars all override fg/bg
        // explicitly because the default Godot ProgressBar theme is near-
        // invisible against the dark-brown panel background).
        private static (HBoxContainer Row, ProgressBar Bar, Label NumLabel)
            NewLabelledBar(float min, float max, string? nameOpt, Color fill, Color track)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 16) };
            row.AddThemeConstantOverride("separation", 6);
            if (nameOpt != null)
            {
                var n = NewBodyLabel(nameOpt);
                n.CustomMinimumSize = new Vector2(58, 0);
                row.AddChild(n);
            }
            var bar = new ProgressBar
            {
                MinValue = min,
                MaxValue = max,
                Value    = max,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(110, 12),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical   = SizeFlags.ShrinkCenter,
            };
            var fillBox = new StyleBoxFlat
            {
                BgColor = fill,
                CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2,
                CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
            };
            var trackBox = new StyleBoxFlat
            {
                BgColor = track,
                CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2,
                CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
            };
            bar.AddThemeStyleboxOverride("fill",       fillBox);
            bar.AddThemeStyleboxOverride("background", trackBox);
            row.AddChild(bar);
            var num = NewBodyLabel("0");
            num.CustomMinimumSize = new Vector2(48, 0);
            num.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(num);
            return (row, bar, num);
        }
    }
}
