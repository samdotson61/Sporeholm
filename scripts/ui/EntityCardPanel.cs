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
    //   • Header row — close button (×) at top-right.
    //   • Name — species DisplayName, gold, 16pt.
    //   • Description — italic two-line blurb from EntityDef.Description.
    //   • Health   — labelled ProgressBar (current/max).
    //   • Mood     — single-word label from EntitySnapshot.MoodLabel.
    //   • Needs    — Nutrition + Rest as labelled ProgressBars.
    //
    // The panel is mutually exclusive with ShroompCardPanel + TilePropertiesPanel
    // (they share top-right screen space). GameController toggles `.Visible`
    // and calls Show / Refresh on snapshot ticks.
    public partial class EntityCardPanel : Control
    {
        private static readonly Color DarkWood = UITheme.TextPrimary;
        private static readonly Color Gold     = UITheme.TextAccent;
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

        private Guid? _selectedId;

        public override void _Ready()
        {
            // v0.6.2 — anchor top-right (same slot as ShroompCardPanel /
            // TilePropertiesPanel so they share visibility space).
            AnchorLeft   = 1; AnchorRight  = 1;
            AnchorTop    = 0; AnchorBottom = 0;
            OffsetLeft   = -260; OffsetRight  = -16;
            OffsetTop    = 64;   OffsetBottom = 64 + 360;
            Visible      = false;

            var bg = new Panel { AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1 };
            bg.AddThemeStyleboxOverride("panel", MakeCardStylebox());
            bg.MouseFilter = MouseFilterEnum.Pass;
            AddChild(bg);

            var vbox = new VBoxContainer
            {
                AnchorLeft   = 0, AnchorRight  = 1,
                AnchorTop    = 0, AnchorBottom = 1,
                OffsetLeft   = 12, OffsetRight  = -12,
                OffsetTop    = 10, OffsetBottom = -12,
            };
            AddChild(vbox);

            // Header row: title slot is filled by _nameLabel; close at right.
            var headerRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            _closeBtn = new Button
            {
                Text = "×",
                CustomMinimumSize = new Vector2(24, 24),
                FocusMode = FocusModeEnum.None,
                TooltipText = "Close",
            };
            _closeBtn.Pressed += () => { Visible = false; _selectedId = null; };
            headerRow.AddChild(_closeBtn);
            vbox.AddChild(headerRow);

            _nameLabel = NewTitleLabel("Unknown Creature");
            vbox.AddChild(_nameLabel);

            _descLabel = NewBodyLabel("");
            _descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _descLabel.AddThemeColorOverride("font_color", TextDim);
            vbox.AddChild(_descLabel);

            // State row (e.g. "Wandering • Friendly")
            _stateLabel = NewBodyLabel("");
            _stateLabel.AddThemeColorOverride("font_color", TextDim);
            vbox.AddChild(_stateLabel);

            vbox.AddChild(NewDivider());

            // ── Health ───────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Health"));
            var (healthRow, healthBar, healthLabel) = NewLabelledBar(0, 100);
            _healthBar = healthBar;
            _healthBarLabel = healthLabel;
            vbox.AddChild(healthRow);

            vbox.AddChild(NewDivider());

            // ── Mood ─────────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Mood"));
            _moodLabel = NewBodyLabel("Calm");
            vbox.AddChild(_moodLabel);

            vbox.AddChild(NewDivider());

            // ── Needs ────────────────────────────────────────────────
            vbox.AddChild(NewSectionLabel("Needs"));
            var (nutritionRow, nutritionBar, nutritionLabel) = NewLabelledBar(0, 100, "Nutrition");
            _nutritionBar   = nutritionBar;
            _nutritionLabel = nutritionLabel;
            vbox.AddChild(nutritionRow);
            var (restRow, restBar, restLabel) = NewLabelledBar(0, 100, "Rest");
            _restBar   = restBar;
            _restLabel = restLabel;
            vbox.AddChild(restRow);
        }

        // Open + lock-onto the given snapshot entity. Subsequent snapshot
        // ticks call Refresh(snap) which keeps the panel synced to live
        // health/needs/mood while the player has the card open.
        public void Show(EntitySnapshot e)
        {
            _selectedId = e.Id;
            Visible = true;
            ApplySnapshot(e);
        }

        public void Refresh(SimulationSnapshot snap)
        {
            if (!Visible || _selectedId == null) return;
            for (int i = 0; i < snap.Entities.Count; i++)
            {
                var e = snap.Entities[i];
                if (e.Id != _selectedId.Value) continue;
                ApplySnapshot(e);
                return;
            }
            // Entity is gone (died / despawned) — close the card.
            Visible = false;
            _selectedId = null;
        }

        public void Close()
        {
            Visible = false;
            _selectedId = null;
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

        private static StyleBoxFlat MakeCardStylebox()
        {
            return new StyleBoxFlat
            {
                BgColor               = UITheme.PanelBg,
                BorderColor           = UITheme.PanelBorderColour,
                BorderWidthLeft       = 2,
                BorderWidthRight      = 2,
                BorderWidthTop        = 2,
                BorderWidthBottom     = 2,
                CornerRadiusTopLeft   = 6,
                CornerRadiusTopRight  = 6,
                CornerRadiusBottomLeft  = 6,
                CornerRadiusBottomRight = 6,
                ShadowColor           = new Color(0, 0, 0, 0.35f),
                ShadowSize            = 6,
            };
        }

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
            l.AddThemeColorOverride("font_color", DarkWood);
            l.AddThemeFontSizeOverride("font_size", 11);
            return l;
        }

        private static HSeparator NewDivider()
        {
            return new HSeparator { CustomMinimumSize = new Vector2(0, 4) };
        }

        // Returns (rowContainer, bar, label). The label sits to the right
        // of the bar so the player gets a numeric readout alongside the
        // visual fill. `nameOpt` is shown to the LEFT of the bar when
        // supplied (used for Nutrition / Rest); the Health row passes
        // null since the section header above the bar already names it.
        private static (HBoxContainer Row, ProgressBar Bar, Label NumLabel)
            NewLabelledBar(float min, float max, string? nameOpt = null)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 18) };
            if (nameOpt != null)
            {
                var n = NewBodyLabel(nameOpt);
                n.CustomMinimumSize = new Vector2(60, 0);
                row.AddChild(n);
            }
            var bar = new ProgressBar
            {
                MinValue = min,
                MaxValue = max,
                Value    = max,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(120, 12),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.AddChild(bar);
            var num = NewBodyLabel("0");
            num.CustomMinimumSize = new Vector2(48, 0);
            num.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(num);
            return (row, bar, num);
        }
    }
}
