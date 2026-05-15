using Godot;
using System.Collections.Generic;
using SmurfulationC.UI;

// Roadmap §3.x.6 — alerts pane. Reworked in v0.3.18 from an always-visible
// top-right column into a centred dismissible popup that only appears when
// critical alerts are pushed (smurf death, mental-break thresholds, raid
// inbound, starvation, plague onset, etc.). Idle gameplay shows no alert
// surface at all — the UI gets out of the way until something demands the
// player's attention.
public partial class AlertsPane : Control
{
    private PanelContainer _popup = null!;
    private VBoxContainer  _list  = null!;
    private Label          _title = null!;

    // Persisted across UI-scale rebuilds (v0.3.20).
    private readonly List<(string Text, AlertLevel Level)> _alerts = new();

    public override void _Ready()
    {
        // Full-rect transparent host. Stays invisible until AddAlert is called.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;
        Visible     = false;

        BuildContent();
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    private void OnUIScaleChanged()
    {
        // Preserve visibility + the alert list, rebuild the popup chrome at
        // the new scale, then re-emit the alerts into the new list container.
        bool wasVisible = Visible;
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildContent();
        foreach (var (text, level) in _alerts) AppendAlertRow(text, level);
        Visible = wasVisible;
    }

    private void BuildContent()
    {

        // Optional dim backdrop so the popup reads as modal. Click-through is
        // disabled while the popup is open (player must dismiss to keep playing).
        var backdrop = new ColorRect
        {
            Color       = new Color(0f, 0f, 0f, 0.45f),
            MouseFilter = MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        // Centred popup panel
        _popup = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _popup.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        _popup.AnchorLeft = _popup.AnchorRight = _popup.AnchorTop = _popup.AnchorBottom = 0.5f;
        float halfW = UITheme.ScaledF(260);
        float halfH = UITheme.ScaledF(160);
        _popup.OffsetLeft   = -halfW;
        _popup.OffsetRight  = halfW;
        _popup.OffsetTop    = -halfH;
        _popup.OffsetBottom = halfH;
        _popup.CustomMinimumSize = UITheme.ScaledVec(420, 200);
        AddChild(_popup);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        _popup.AddChild(vbox);

        _title = new Label
        {
            Text                = "Alerts",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _title.AddThemeColorOverride("font_color", UITheme.TextAccent);
        _title.AddThemeFontSizeOverride("font_size", UITheme.Scaled(20));
        vbox.AddChild(_title);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
            CustomMinimumSize   = new Vector2(0, UITheme.Scaled(180)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vbox.AddChild(scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        // Footer with Dismiss button.
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(footer);

        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var dismiss = new AnimatedButton
        {
            Text              = "Dismiss",
            Compact           = true,
            CustomMinimumSize = UITheme.ScaledVec(140, 36),
        };
        dismiss.Pressed += Hide;
        footer.AddChild(dismiss);
    }

    // Public API. Pushes a critical alert and shows the popup. Non-critical
    // calls (Info-level) accumulate silently; only the first Warning/Critical
    // push opens the popup. Players can also opt out by hitting Dismiss at any
    // time — the alert stays in the list but the popup hides until the next
    // critical push.
    public void AddAlert(string text, AlertLevel level = AlertLevel.Info)
    {
        _alerts.Add((text, level));
        AppendAlertRow(text, level);

        // Show the popup for Warning / Critical pushes. Info-level alerts
        // accumulate silently in the list until the player opens the popup
        // explicitly (future: tray-icon hook for opening at will).
        if (level == AlertLevel.Critical || level == AlertLevel.Warning)
            Visible = true;
    }

    public void ClearAlerts()
    {
        _alerts.Clear();
        foreach (Node c in _list.GetChildren()) c.QueueFree();
    }

    // Builds a single row into `_list`. Used by both AddAlert (live) and
    // OnUIScaleChanged (replay-on-rebuild) — keeps rendering consistent.
    private void AppendAlertRow(string text, AlertLevel level)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        _list.AddChild(row);

        // Severity glyph.
        var glyph = new Label
        {
            Text              = level switch
            {
                AlertLevel.Critical => "⚠",
                AlertLevel.Warning  => "!",
                _                   => "·",
            },
            CustomMinimumSize = new Vector2(UITheme.Scaled(18), 0),
        };
        glyph.AddThemeColorOverride("font_color", level switch
        {
            AlertLevel.Critical => UITheme.TextDanger,
            AlertLevel.Warning  => UITheme.TextWarn,
            _                   => UITheme.TextMuted,
        });
        glyph.AddThemeFontSizeOverride("font_size", UITheme.Scaled(16));
        row.AddChild(glyph);

        var lbl = new Label
        {
            Text                = text,
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        lbl.AddThemeColorOverride("font_color", level switch
        {
            AlertLevel.Critical => UITheme.TextDanger,
            AlertLevel.Warning  => UITheme.TextWarn,
            _                   => UITheme.TextPrimary,
        });
        lbl.AddThemeFontSizeOverride("font_size", UITheme.Scaled(13));
        row.AddChild(lbl);
    }

    public enum AlertLevel { Info, Warning, Critical }
}
