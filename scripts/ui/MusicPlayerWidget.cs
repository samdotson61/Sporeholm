using Godot;

namespace Sporeholm.UI
{
    // v0.5.75 — Small parchment-card widget anchored to the top-left of
    // the main menu. Exposes play/pause + skip controls for the menu
    // music. Listens to MusicManager.TrackChanged so the "Now Playing"
    // label auto-refreshes when shuffle picks a new track or the user
    // taps Skip.
    //
    // v0.5.77 — shrunk + made less visually prominent per Sam: "Make the
    // music player a little smaller and less noticeable in the top left."
    // Pre-v0.5.77 was a 260×80 px card with a header label, title row,
    // and 40×26 buttons at ~0.93 alpha. Now ~190×30 single-row layout
    // (track title + ⏯ + ⏭), default alpha 0.45 with a hover bump to
    // 0.90 so the widget recedes when you're reading the menu but
    // brightens when you reach for it.
    public partial class MusicPlayerWidget : Control
    {
        private static readonly Color ParchBg     = new(0.84f, 0.76f, 0.56f, 1f);  // alpha applied via Modulate
        private static readonly Color Gold        = new(0.82f, 0.63f, 0.18f);
        private static readonly Color DarkWood    = new(0.20f, 0.12f, 0.04f);
        private static readonly Color Brown       = new(0.45f, 0.28f, 0.08f);
        private static readonly Color BrownMuted  = new(0.55f, 0.40f, 0.18f);

        // v0.5.77 — modulate alpha so the whole widget fades together
        // (bg + text + buttons) on hover transitions.
        // v0.5.81 — idle alpha bumped 0.45 → 0.70 so the title text reads
        // without leaning in (Sam: "Also make the text on the music player
        // in the main menu a little bit easier to see"). Hover alpha
        // unchanged at 0.90; the widget still recedes a little vs full
        // opacity, just no longer down at the "barely legible" tier.
        private const float IdleAlpha  = 0.70f;
        private const float HoverAlpha = 0.95f;

        private Label  _titleLabel = null!;
        private Button _playBtn    = null!;
        private Button _skipBtn    = null!;
        private PanelContainer _card = null!;

        public override void _Ready()
        {
            // v0.5.77 — anchored to top-left, smaller footprint than v0.5.75.
            AnchorLeft = 0f; AnchorRight = 0f;
            AnchorTop  = 0f; AnchorBottom = 0f;
            OffsetLeft = 12f; OffsetTop = 12f;
            OffsetRight = 12f + 190f; OffsetBottom = 12f + 30f;
            MouseFilter = MouseFilterEnum.Pass;
            Modulate = new Color(1f, 1f, 1f, IdleAlpha);

            _card = new PanelContainer();
            _card.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _card.MouseFilter = MouseFilterEnum.Pass;
            var style = new StyleBoxFlat { BgColor = ParchBg };
            style.SetBorderWidthAll(1);
            style.BorderColor = Gold;
            style.SetCornerRadiusAll(5);
            style.ShadowColor  = new Color(0f, 0f, 0f, 0.25f);
            style.ShadowSize   = 3;
            style.ShadowOffset = new Vector2(0, 1);
            style.ContentMarginLeft = 6;
            style.ContentMarginRight = 4;
            style.ContentMarginTop = style.ContentMarginBottom = 2;
            _card.AddThemeStyleboxOverride("panel", style);
            AddChild(_card);

            // Single-row layout: ♪ title | ⏯ | ⏭
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 3);
            _card.AddChild(row);

            // Title — uses the ♪ glyph as a prefix so we don't need a
            // separate header label.
            _titleLabel = new Label
            {
                Text                = "♪ —",
                ClipText            = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            _titleLabel.AddThemeColorOverride("font_color", DarkWood);
            _titleLabel.AddThemeFontSizeOverride("font_size", 11);    // v0.5.81 — 10→11 for legibility
            // v0.5.81 — soft cream-stroke shadow behind the title so the
            // text reads on either the parchment background (at idle alpha)
            // OR on whatever village-background colour sits behind the
            // widget when alpha drops below 0.7. Half-pixel offset =
            // anti-aliased halo, not a chunky drop shadow.
            _titleLabel.AddThemeColorOverride("font_shadow_color", new Color(0.96f, 0.91f, 0.78f, 0.85f));
            _titleLabel.AddThemeConstantOverride("shadow_offset_x", 1);
            _titleLabel.AddThemeConstantOverride("shadow_offset_y", 1);
            row.AddChild(_titleLabel);

            _playBtn = MakeBtn("⏸",
                "Pause / resume the menu music.",
                onPressed: TogglePlayPause);
            row.AddChild(_playBtn);

            _skipBtn = MakeBtn("⏭",
                "Skip to the next track in the menu playlist.",
                onPressed: () => MusicManager.Instance?.Skip());
            row.AddChild(_skipBtn);

            if (MusicManager.Instance != null)
                MusicManager.Instance.TrackChanged += OnTrackChanged;

            MouseEntered += OnHoverIn;
            MouseExited  += OnHoverOut;

            RefreshLabels();
        }

        public override void _ExitTree()
        {
            if (MusicManager.Instance != null)
                MusicManager.Instance.TrackChanged -= OnTrackChanged;
        }

        private void OnHoverIn()
        {
            var t = CreateTween();
            t.TweenProperty(this, "modulate:a", HoverAlpha, 0.18f);
        }

        private void OnHoverOut()
        {
            var t = CreateTween();
            t.TweenProperty(this, "modulate:a", IdleAlpha, 0.35f);
        }

        private Button MakeBtn(string label, string tooltip, System.Action onPressed)
        {
            var b = new Button
            {
                Text              = label,
                TooltipText       = tooltip,
                FocusMode         = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(22, 20),
            };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeColorOverride("font_color",         DarkWood);
            b.AddThemeColorOverride("font_hover_color",   Brown);
            b.AddThemeColorOverride("font_pressed_color", Brown);
            var normal = new StyleBoxFlat { BgColor = new Color(0.92f, 0.84f, 0.62f, 1f) };
            normal.SetBorderWidthAll(1);
            normal.BorderColor = BrownMuted;
            normal.SetCornerRadiusAll(3);
            normal.ContentMarginLeft = normal.ContentMarginRight = 3;
            normal.ContentMarginTop  = normal.ContentMarginBottom = 1;
            var hover = (StyleBoxFlat)normal.Duplicate();
            hover.BgColor = new Color(0.96f, 0.88f, 0.68f, 1f);
            hover.BorderColor = Gold;
            var pressed = (StyleBoxFlat)normal.Duplicate();
            pressed.BgColor = new Color(0.74f, 0.62f, 0.40f, 1f);
            pressed.BorderColor = Gold;
            b.AddThemeStyleboxOverride("normal",  normal);
            b.AddThemeStyleboxOverride("hover",   hover);
            b.AddThemeStyleboxOverride("pressed", pressed);
            b.Pressed += () => onPressed();
            return b;
        }

        private void TogglePlayPause()
        {
            var mgr = MusicManager.Instance;
            if (mgr == null) return;
            if (mgr.IsPlaying) mgr.Pause();
            else               mgr.Resume();
            RefreshLabels();
        }

        private void OnTrackChanged() => RefreshLabels();

        private void RefreshLabels()
        {
            var mgr = MusicManager.Instance;
            if (mgr == null)
            {
                _titleLabel.Text = "♪ —";
                _titleLabel.TooltipText = "";
                _playBtn.Text = "▶";
                return;
            }
            var track = mgr.CurrentTrack;
            if (track == null)
            {
                _titleLabel.Text = "♪ —";
                _titleLabel.TooltipText = "";
            }
            else
            {
                _titleLabel.Text        = "♪ " + track.Title;
                _titleLabel.TooltipText = MusicManager.DefaultAttributionLine(track);
            }
            _playBtn.Text = mgr.IsPlaying ? "⏸" : "▶";
        }
    }
}
