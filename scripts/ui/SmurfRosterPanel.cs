using Godot;
using System.Collections.Generic;
using SmurfulationC.UI;
using SmurfulationC.Simulation;

// v0.3.27 — RimWorld-style roster table for the Smurfs tab. Each row shows
// one alive smurf with columns for the data the player needs to scan a
// colony at a glance:
//
//   📷  Name           Role        Mood       🍓 💤 👥 ✨ 🛡    Activity   ⚔
//
// Columns covered (mapping to RimWorld's Schedule + Assign + Work tabs):
//   • Focus button      — single click zooms camera (Schedule's "select" verb).
//   • Name              — clickable label, selects smurf + opens unit card.
//   • Role              — present feature.
//   • Mood              — coloured pip + state name (present).
//   • Needs (5)         — coloured percentage bars (Nutrition / Rest / Social /
//                         Magic / Safety — present features).
//   • Activity          — verb form of CurrentTask (present).
//   • Combat            — ⚔ glyph when CombatTargetName != null (Phase-8 stub).
//
// RimWorld's Apparel/Food/Drug/Reading policy columns are intentionally
// omitted because none of those systems exist in SmurfulationC yet — they'd
// add empty columns the player couldn't interact with. Per-role Work
// priorities live in their own ⚙ Jobs tab (stubbed).
public partial class SmurfRosterPanel : Control
{
    [Signal] public delegate void SmurfZoomRequestedEventHandler(string name);
    [Signal] public delegate void SmurfSelectRequestedEventHandler(string name);

    private ScrollContainer _scroll = null!;
    private VBoxContainer   _rowsVbox = null!;

    // Per-row controls captured so Refresh can update in place rather than
    // tearing down + rebuilding the VBox each tick (which would flicker and
    // lose the player's scroll position).
    private readonly Dictionary<string, RosterRowCtrls> _rows = new();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;
        BuildContent();
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    private void OnUIScaleChanged()
    {
        _rows.Clear();
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildContent();
    }

    private void BuildContent()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(10));
        margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(10));
        margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(8));
        margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(8));
        AddChild(margin);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 4);
        margin.AddChild(outer);

        // Header row — column titles aligned with the per-smurf rows.
        outer.AddChild(BuildHeader());

        // Separator under the header.
        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(UITheme.PanelBorderColour.R,
            UITheme.PanelBorderColour.G, UITheme.PanelBorderColour.B, 0.45f));
        outer.AddChild(sep);

        // Scrollable list of smurf rows.
        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize    = new Vector2(UITheme.Scaled(640), UITheme.Scaled(160)),
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
        };
        outer.AddChild(_scroll);

        _rowsVbox = new VBoxContainer();
        _rowsVbox.AddThemeConstantOverride("separation", 2);
        _rowsVbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(_rowsVbox);
    }

    private Control BuildHeader()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        row.AddChild(MakeColLabel("",         UITheme.Scaled(28), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("Name",     UITheme.Scaled(110), HorizontalAlignment.Left));
        row.AddChild(MakeColLabel("Role",     UITheme.Scaled(80), HorizontalAlignment.Left));
        row.AddChild(MakeColLabel("Mood",     UITheme.Scaled(110), HorizontalAlignment.Left));
        row.AddChild(MakeColLabel("🍓",       UITheme.Scaled(36), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("💤",       UITheme.Scaled(36), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("👥",       UITheme.Scaled(36), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("✨",       UITheme.Scaled(36), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("🛡",        UITheme.Scaled(36), HorizontalAlignment.Center));
        row.AddChild(MakeColLabel("Activity", UITheme.Scaled(110), HorizontalAlignment.Left));
        row.AddChild(MakeColLabel("⚔",        UITheme.Scaled(24), HorizontalAlignment.Center));
        return row;
    }

    private Label MakeColLabel(string text, int width, HorizontalAlignment align)
    {
        var l = new Label
        {
            Text                = text,
            CustomMinimumSize   = new Vector2(width, 0),
            HorizontalAlignment = align,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.Scaled(11));
        l.AddThemeColorOverride("font_color", UITheme.TextMuted);
        return l;
    }

    // ── Refresh from snapshot ─────────────────────────────────────────────────

    public void Refresh(IReadOnlyList<SimulationC_Roster_Row> rows)
    {
        if (_rowsVbox == null) return;

        // Drop rows for smurfs that no longer exist in the snapshot.
        var alive = new HashSet<string>();
        foreach (var r in rows) alive.Add(r.Name);
        var toRemove = new List<string>();
        foreach (var kv in _rows)
            if (!alive.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var name in toRemove)
        {
            _rowsVbox.RemoveChild(_rows[name].Root);
            _rows[name].Root.QueueFree();
            _rows.Remove(name);
        }

        // Add new rows; update existing ones in place.
        foreach (var r in rows)
        {
            if (!_rows.TryGetValue(r.Name, out var ctrls))
            {
                ctrls = BuildRow(r);
                _rows[r.Name] = ctrls;
                _rowsVbox.AddChild(ctrls.Root);
            }
            UpdateRow(ctrls, r);
        }
    }

    private RosterRowCtrls BuildRow(SimulationC_Roster_Row r)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        // Focus button — small camera icon; double-click on row also zooms.
        var focus = new Button
        {
            Text              = "📷",
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(UITheme.Scaled(28), UITheme.Scaled(22)),
            TooltipText       = "Centre the camera on this smurf",
            Flat              = true,
        };
        focus.AddThemeFontSizeOverride("font_size", UITheme.Scaled(12));
        focus.Pressed += () => EmitSignal(SignalName.SmurfZoomRequested, r.Name);
        row.AddChild(focus);

        // Name button — text-only button so single-click selects.
        var nameBtn = new Button
        {
            Text              = r.Name,
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(UITheme.Scaled(110), UITheme.Scaled(22)),
            Flat              = true,
            Alignment         = HorizontalAlignment.Left,
            TooltipText       = "Single-click to select & open unit card · Double-click to focus camera",
        };
        nameBtn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(12));
        nameBtn.AddThemeColorOverride("font_color",       UITheme.TextPrimary);
        nameBtn.AddThemeColorOverride("font_hover_color", UITheme.TextAccent);
        nameBtn.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.DoubleClick)
                    EmitSignal(SignalName.SmurfZoomRequested, r.Name);
                else
                    EmitSignal(SignalName.SmurfSelectRequested, r.Name);
            }
        };
        row.AddChild(nameBtn);

        var roleLbl = MakeCellLabel(r.Role, UITheme.Scaled(80), HorizontalAlignment.Left);
        row.AddChild(roleLbl);

        var moodLbl = MakeCellLabel(FormatMood(r.MoodState), UITheme.Scaled(110), HorizontalAlignment.Left);
        moodLbl.AddThemeColorOverride("font_color", MoodColor(r.MoodState));
        row.AddChild(moodLbl);

        var nut = MakeNeedBar();
        var rst = MakeNeedBar();
        var soc = MakeNeedBar();
        var mag = MakeNeedBar();
        var saf = MakeNeedBar();
        row.AddChild(nut);
        row.AddChild(rst);
        row.AddChild(soc);
        row.AddChild(mag);
        row.AddChild(saf);

        var activityLbl = MakeCellLabel("—", UITheme.Scaled(110), HorizontalAlignment.Left);
        row.AddChild(activityLbl);

        var combatLbl = MakeCellLabel("", UITheme.Scaled(24), HorizontalAlignment.Center);
        combatLbl.AddThemeColorOverride("font_color", new Color(0.95f, 0.30f, 0.30f, 0.95f));
        row.AddChild(combatLbl);

        return new RosterRowCtrls
        {
            Root        = row,
            NameBtn     = nameBtn,
            RoleLbl     = roleLbl,
            MoodLbl     = moodLbl,
            Nutrition   = nut,
            Rest        = rst,
            Social      = soc,
            Magic       = mag,
            Safety      = saf,
            ActivityLbl = activityLbl,
            CombatLbl   = combatLbl,
        };
    }

    private void UpdateRow(RosterRowCtrls c, SimulationC_Roster_Row r)
    {
        // Don't rewrite text if unchanged (avoids spurious GUI redraws).
        if (c.NameBtn.Text != r.Name) c.NameBtn.Text = r.Name;
        if (c.RoleLbl.Text != r.Role) c.RoleLbl.Text = r.Role;

        string moodText = FormatMood(r.MoodState);
        if (c.MoodLbl.Text != moodText)
        {
            c.MoodLbl.Text = moodText;
            c.MoodLbl.AddThemeColorOverride("font_color", MoodColor(r.MoodState));
        }

        SetNeedBar(c.Nutrition, r.Nutrition);
        SetNeedBar(c.Rest,      r.Rest);
        SetNeedBar(c.Social,    r.Social);
        SetNeedBar(c.Magic,     r.Magic);
        SetNeedBar(c.Safety,    r.Safety);

        string verb = ActivityVerb(r.CurrentTask);
        if (c.ActivityLbl.Text != verb) c.ActivityLbl.Text = verb;

        bool inCombat = r.CombatTargetName != null;
        string sword = inCombat ? "⚔" : "";
        if (c.CombatLbl.Text != sword) c.CombatLbl.Text = sword;
    }

    private static ProgressBar MakeNeedBar()
    {
        var bar = new ProgressBar
        {
            MinValue          = 0,
            MaxValue          = 100,
            Value             = 100,
            ShowPercentage    = false,
            CustomMinimumSize = new Vector2(UITheme.Scaled(36), UITheme.Scaled(14)),
            MouseFilter       = MouseFilterEnum.Ignore,
        };

        var track = new StyleBoxFlat { BgColor = new Color(0.18f, 0.13f, 0.06f, 0.65f) };
        track.SetCornerRadiusAll(3);
        bar.AddThemeStyleboxOverride("background", track);

        var fill = new StyleBoxFlat { BgColor = new Color(0.55f, 0.80f, 0.30f) };
        fill.SetCornerRadiusAll(3);
        bar.AddThemeStyleboxOverride("fill", fill);
        return bar;
    }

    private static void SetNeedBar(ProgressBar bar, float v)
    {
        v = Mathf.Clamp(v, 0f, 100f);
        if (!Mathf.IsEqualApprox((float)bar.Value, v)) bar.Value = v;

        // Recolour: green ≥ 60, amber 30–60, red < 30. Player scans the row
        // for any red/amber pip and knows that smurf needs attention.
        Color c;
        if      (v >= 60f) c = new Color(0.55f, 0.80f, 0.30f);
        else if (v >= 30f) c = new Color(0.90f, 0.70f, 0.25f);
        else               c = new Color(0.95f, 0.30f, 0.25f);
        var fill = new StyleBoxFlat { BgColor = c };
        fill.SetCornerRadiusAll(3);
        bar.AddThemeStyleboxOverride("fill", fill);
    }

    private static Label MakeCellLabel(string text, int width, HorizontalAlignment align)
    {
        var l = new Label
        {
            Text                = text,
            CustomMinimumSize   = new Vector2(width, UITheme.Scaled(14)),
            HorizontalAlignment = align,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        l.AddThemeFontSizeOverride("font_size", UITheme.Scaled(12));
        l.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        return l;
    }

    private static string FormatMood(MoodState m)
    {
        string emoji = m switch
        {
            MoodState.Inspired   => "✨",
            MoodState.Content    => "🙂",
            MoodState.Stressed   => "😟",
            MoodState.Distressed => "😣",
            MoodState.Breaking   => "💢",
            MoodState.Collapse   => "💀",
            _                    => "·",
        };
        return $"{emoji} {m}";
    }

    private static Color MoodColor(MoodState m) => m switch
    {
        MoodState.Inspired   => new Color(0.45f, 0.95f, 0.55f),
        MoodState.Content    => new Color(0.85f, 0.85f, 0.70f),
        MoodState.Stressed   => new Color(0.95f, 0.80f, 0.30f),
        MoodState.Distressed => new Color(0.95f, 0.55f, 0.30f),
        MoodState.Breaking   => new Color(0.95f, 0.30f, 0.30f),
        MoodState.Collapse   => new Color(0.65f, 0.20f, 0.65f),
        _                    => UITheme.TextPrimary,
    };

    // v0.3.46 — verb mapping moved to `TaskVerb.Of` so the roster and
    // the unit card stay in sync on a single switch statement.
    private static string ActivityVerb(TaskType t) => TaskVerb.Of(t);

    // ── Row controls struct ──────────────────────────────────────────────────

    private sealed class RosterRowCtrls
    {
        public HBoxContainer Root    = null!;
        public Button       NameBtn = null!;
        public Label        RoleLbl = null!;
        public Label        MoodLbl = null!;
        public ProgressBar  Nutrition = null!;
        public ProgressBar  Rest      = null!;
        public ProgressBar  Social    = null!;
        public ProgressBar  Magic     = null!;
        public ProgressBar  Safety    = null!;
        public Label        ActivityLbl = null!;
        public Label        CombatLbl   = null!;
    }

    // Roster-panel size propagation so the host PanelContainer in
    // BottomTabPanel sizes correctly.
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

// v0.3.27 — expanded row record so the roster table can show needs + activity
// + combat status alongside name/role/mood. Populated from SmurfSnapshot in
// GameController.OnTick.
public readonly record struct SimulationC_Roster_Row(
    string                              Name,
    string                              Role,
    SmurfulationC.Simulation.MoodState  MoodState,
    float                               Nutrition,
    float                               Rest,
    float                               Social,
    float                               Magic,
    float                               Safety,
    SmurfulationC.Simulation.TaskType   CurrentTask,
    string?                             CombatTargetName);
