using Godot;

namespace Sporeholm.UI
{
    // v0.5.74 — Full-screen overlay shown when the player clicks Credits.
    // Pulls MusicManager.Instance.GetCredits() at Open() time so a future
    // playlist edit shows up here without touching this file.
    //
    // Visual style mirrors SettingsPanel: parchment-bg card centred on a
    // dimmed overlay, gold border, Grobold title, scrollable content + a
    // pinned Back button at the bottom.
    public partial class CreditsPanel : Control
    {
        [Signal] public delegate void ClosedEventHandler();

        private static readonly Color ParchBg  = new(0.84f, 0.76f, 0.56f, 0.97f);
        private static readonly Color DarkWood = new(0.20f, 0.12f, 0.04f);
        private static readonly Color Gold     = new(0.82f, 0.63f, 0.18f);
        private static readonly Color Brown    = new(0.45f, 0.28f, 0.08f);
        private static readonly Color Muted    = new(0.40f, 0.28f, 0.12f);

        private VBoxContainer _musicSection = null!;

        public override void _Ready()
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Stop;

            var overlay = new ColorRect { Color = new Color(0f, 0f, 0f, 0.60f) };
            overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(overlay);

            var card = new PanelContainer();
            card.AnchorLeft = 0.5f; card.AnchorRight = 0.5f;
            card.AnchorTop  = 0f;   card.AnchorBottom = 1f;
            card.OffsetLeft = -320f; card.OffsetRight  = 320f;
            card.OffsetTop  = 28f;   card.OffsetBottom = -28f;
            card.GrowHorizontal = GrowDirection.Both;
            var style = new StyleBoxFlat { BgColor = ParchBg };
            style.SetBorderWidthAll(4);
            style.BorderColor = Gold;
            style.SetCornerRadiusAll(10);
            style.ShadowColor  = new Color(0f, 0f, 0f, 0.5f);
            style.ShadowSize   = 12;
            style.ShadowOffset = new Vector2(0, 4);
            card.AddThemeStyleboxOverride("panel", style);
            AddChild(card);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   22);
            margin.AddThemeConstantOverride("margin_right",  28);
            margin.AddThemeConstantOverride("margin_top",    22);
            margin.AddThemeConstantOverride("margin_bottom", 22);
            card.AddChild(margin);

            var outer = new VBoxContainer();
            outer.AddThemeConstantOverride("separation", 10);
            margin.AddChild(outer);

            outer.AddChild(BigLabel("Credits"));
            outer.AddChild(MakeSep());

            // Scrollable content area
            var scroll = new ScrollContainer();
            scroll.SizeFlagsVertical    = SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            outer.AddChild(scroll);

            var vbox = new VBoxContainer();
            vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            vbox.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(vbox);

            // ── Game ───────────────────────────────────────────────────────────
            vbox.AddChild(SectionLabel("Sporeholm"));
            vbox.AddChild(BodyLine("Designed and developed by Sam Dotson."));
            vbox.AddChild(BodyLine("Engine: Godot 4 (C# / .NET 8) — godotengine.org"));

            vbox.AddChild(MakeSep());

            // ── Music ──────────────────────────────────────────────────────────
            vbox.AddChild(SectionLabel("Music"));
            _musicSection = new VBoxContainer();
            _musicSection.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(_musicSection);

            vbox.AddChild(MakeSep());

            // ── Fonts / SFX ────────────────────────────────────────────────────
            vbox.AddChild(SectionLabel("Other Assets"));
            vbox.AddChild(BodyLine("Grobold font — used for titles and headings"));
            vbox.AddChild(BodyLine("UI sound effects — Freesound.org (CC0 licence)"));

            // ── Back button (pinned) ───────────────────────────────────────────
            outer.AddChild(MakeSep());
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            outer.AddChild(btnRow);
            var back = new AnimatedButton { Text = "Back" };
            back.Pressed += OnBack;
            btnRow.AddChild(back);

            Visible = false;
        }

        public void Open()
        {
            RebuildMusicSection();
            Visible = true;
        }

        public void Close()
        {
            Visible = false;
            EmitSignal(SignalName.Closed);
        }

        private void OnBack() => Close();

        // Pulls the live MusicManager playlist at open time so the credits
        // always reflect whatever is currently wired. One row per unique
        // track across all contexts, formatted via DefaultAttributionLine
        // with the source URL on a second line for click-through reference.
        private void RebuildMusicSection()
        {
            foreach (Node c in _musicSection.GetChildren()) c.QueueFree();

            var mgr = MusicManager.Instance;
            if (mgr == null)
            {
                _musicSection.AddChild(BodyLineMuted("(music manager not yet initialised)"));
                return;
            }
            var credits = mgr.GetCredits();
            if (credits.Count == 0)
            {
                _musicSection.AddChild(BodyLineMuted("(no tracks currently registered)"));
                return;
            }
            foreach (var t in credits)
            {
                _musicSection.AddChild(BodyLine(MusicManager.DefaultAttributionLine(t)));
                if (!string.IsNullOrEmpty(t.SourceUrl))
                    _musicSection.AddChild(BodyLineMuted("    " + t.SourceUrl));
            }
        }

        // ── Style helpers (mirror SettingsPanel) ───────────────────────────────

        private Label BigLabel(string text)
        {
            var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
            l.AddThemeColorOverride("font_color", DarkWood);
            l.AddThemeFontSizeOverride("font_size", 28);
            ApplyGrobold(l);
            return l;
        }

        private Label SectionLabel(string text)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", Brown);
            l.AddThemeFontSizeOverride("font_size", 18);
            ApplyGrobold(l);
            return l;
        }

        private Label BodyLine(string text)
        {
            var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
            l.AddThemeColorOverride("font_color", DarkWood);
            l.AddThemeFontSizeOverride("font_size", 14);
            return l;
        }

        private Label BodyLineMuted(string text)
        {
            var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
            l.AddThemeColorOverride("font_color", Muted);
            l.AddThemeFontSizeOverride("font_size", 13);
            return l;
        }

        private static void ApplyGrobold(Label l)
        {
            const string font = "res://assets/fonts/Grobold.ttf";
            if (ResourceLoader.Exists(font))
                l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        }

        private static HSeparator MakeSep()
        {
            var h = new HSeparator();
            h.AddThemeColorOverride("color", new Color(0.55f, 0.38f, 0.12f, 0.6f));
            return h;
        }
    }
}
