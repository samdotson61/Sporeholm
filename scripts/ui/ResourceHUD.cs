using Godot;
using SmurfulationC;
using SmurfulationC.UI;

// Roadmap §3.x.4 — floating capsule at top-centre showing the colony's
// stored-resource totals. Wired to SimulationManager.GetResourcesSnapshot()
// per the Phase 3 data layer. Until Phase 5 storage-building tagging lands
// (Roadmap §3.x.4 / §5.11 gating), the readout shows the v0.3.0 ledger
// totals with an `(unstored)` qualifier so the player understands the number
// reflects the placeholder pool.
public partial class ResourceHUD : Control
{
    public SimulationManager Sim { get; set; } = null!;

    private Label _food  = null!;
    private Label _stone = null!;
    private Label _wood  = null!;
    private Label _magic = null!;
    private Label _stamp = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        GrowHorizontal = GrowDirection.Both;
        // Sits at the top-centre with the same EdgeInset clearance as the HUD
        // left/right capsules — they no longer occupy full-width since the
        // Phase 3.x HUD refactor (v0.3.17), so this row can share the top
        // band horizontally without overlapping.
        OffsetTop      = UITheme.EdgeInset;
        OffsetBottom   = UITheme.EdgeInset + UITheme.HudHeight;
        MouseFilter    = MouseFilterEnum.Pass;

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        AddChild(panel);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        panel.AddChild(row);

        _food  = MakeReadout(row, "🍓 Food",   "—");
        _stone = MakeReadout(row, "🪨 Stone",  "—");
        _wood  = MakeReadout(row, "🪵 Wood",   "—");
        _magic = MakeReadout(row, "✨ Magic",   "—");

        _stamp = new Label { Text = "(unstored — Phase 5 will gate)" };
        _stamp.AddThemeColorOverride("font_color", UITheme.TextMuted);
        _stamp.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(_stamp);
    }

    // v0.4.27 — throttle removed (was 200 ms in v0.4.23). Gameplay
    // requirement: the totals must reflect inventory changes as soon as
    // they happen. The `SetTextIfChanged` write-elide guards keep the
    // per-frame cost to four string comparisons when nothing has
    // changed — no Label.Text writes, no canvas redraws.
    public override void _Process(double _)
    {
        if (Sim == null) return;

        var r = Sim.GetResourcesSnapshot();
        SetTextIfChanged(_food,  $"{r.Food:0}");
        SetTextIfChanged(_stone, $"{r.Stone:0}");
        SetTextIfChanged(_wood,  $"{r.Wood:0}");
        SetTextIfChanged(_magic, $"{r.MagicEssence:0}");
    }

    private static void SetTextIfChanged(Label lbl, string newText)
    {
        if (lbl.Text != newText) lbl.Text = newText;
    }

    private static Label MakeReadout(HBoxContainer parent, string title, string initial)
    {
        var sub = new HBoxContainer();
        sub.AddThemeConstantOverride("separation", 4);
        parent.AddChild(sub);

        var titleLbl = new Label { Text = title };
        titleLbl.AddThemeColorOverride("font_color", UITheme.TextMuted);
        titleLbl.AddThemeFontSizeOverride("font_size", 12);
        sub.AddChild(titleLbl);

        var valueLbl = new Label { Text = initial };
        valueLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        valueLbl.AddThemeFontSizeOverride("font_size", 13);
        sub.AddChild(valueLbl);

        return valueLbl;
    }
}
