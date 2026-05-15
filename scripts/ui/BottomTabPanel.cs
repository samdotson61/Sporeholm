using Godot;
using SmurfulationC.UI;

// v0.3.27 — bottom-right tab shell with per-host panels.
//
// Previous attempt (v0.3.26) stacked all hosts inside a single shared
// PanelContainer via a plain `Control` wrapper. Plain Control doesn't
// propagate its children's minimum sizes upward, so the PanelContainer
// shrank to its content margins (~12 px) and nothing visible came out
// of any tab.
//
// New structure puts each host as its OWN styled PanelContainer, all
// siblings in the contentRow HBox with the ExpandFill spacer to their
// left. Only one host is `Visible` at a time; hidden Controls collapse
// out of HBox layout in Godot 4, so the visible host hugs the right edge
// just like the tab bar capsule below it. Each host's PanelContainer
// sizes to its own child's minimum (toolbar / roster / stub label), so
// the player sees an actual panel with actual content.
public partial class BottomTabPanel : Control
{
    // v0.3.41 — Resources and Animals tabs added.
    // Resources sits between Jobs and Smurfs (granular ledger view).
    // Animals is a Phase 9 stub anchored at the far right.
    public enum Tab { None, Orders, Build, Zones, Jobs, Resources, Smurfs, Animals }

    private Tab _active = Tab.None;

    private VBoxContainer _band  = null!;
    private PanelContainer _tabBar = null!;

    private PanelContainer _ordersHost    = null!;
    private PanelContainer _buildHost     = null!;
    private PanelContainer _zonesHost     = null!;
    private PanelContainer _jobsHost      = null!;
    private PanelContainer _resourcesHost = null!;
    private PanelContainer _smurfsHost    = null!;
    private PanelContainer _animalsHost   = null!;

    private Button _ordersBtn    = null!;
    private Button _buildBtn     = null!;
    private Button _zonesBtn     = null!;
    private Button _jobsBtn      = null!;
    private Button _resourcesBtn = null!;
    private Button _smurfsBtn    = null!;
    private Button _animalsBtn   = null!;

    private DesignationToolbar? _orders;
    private SmurfRosterPanel?   _roster;
    private ResourcesPanel?     _resources;
    private JobsPanel?          _jobs;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // v0.3.28 — Ignore on the FullRect outer so clicks over the empty
        // transparent area (everything except the bottom-right capsule and
        // its content panel) fall through to the controls beneath. Without
        // this, the HUD's top-right speed buttons were unreachable: with
        // MouseFilter.Pass, BottomTabPanel received the event and only
        // propagated to its *parent* (CanvasLayer), never to siblings.
        // The band itself (and its panel children) keeps Stop semantics so
        // their own hit-testing still works.
        MouseFilter = MouseFilterEnum.Ignore;
        BuildShell();
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    private void OnUIScaleChanged()
    {
        // Detach embedded panels before nuking the shell so they survive.
        if (_orders    != null) _ordersHost   .RemoveChild(_orders);
        if (_roster    != null) _smurfsHost   .RemoveChild(_roster);
        if (_resources != null) _resourcesHost.RemoveChild(_resources);
        if (_jobs      != null) _jobsHost     .RemoveChild(_jobs);
        // v0.5.4 — also detach _zones across UI-scale rebuilds. Pre-v0.5.4
        // the panel was QueueFree'd along with the shell (since the field
        // wasn't detached) and never re-attached, so any UI-scale change
        // would leave the Zones tab permanently empty.
        if (_zones     != null) _zonesHost    .RemoveChild(_zones);
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildShell();
        if (_orders    != null) _ordersHost   .AddChild(_orders);
        if (_roster    != null) _smurfsHost   .AddChild(_roster);
        if (_resources != null) _resourcesHost.AddChild(_resources);
        if (_jobs      != null) _jobsHost     .AddChild(_jobs);
        if (_zones     != null) _zonesHost    .AddChild(_zones);
        SetActiveTab(_active);
    }

    private void BuildShell()
    {
        // ── Outer band: bottom-anchored, grows upward ───────────────────────
        _band = new VBoxContainer();
        _band.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        _band.OffsetLeft   = UITheme.EdgeInset;
        _band.OffsetRight  = -UITheme.EdgeInset;
        _band.OffsetBottom = -UITheme.EdgeInset;
        _band.GrowVertical = GrowDirection.Begin;
        _band.AddThemeConstantOverride("separation", 6);
        // v0.3.28 — Ignore on every transparent layout container in the band
        // so clicks on the bottom-left / bottom-centre of the play area
        // (outside the bottom-right capsule and content panel) fall through
        // to the map. Only the actual PanelContainers below use Stop.
        _band.MouseFilter  = MouseFilterEnum.Ignore;
        AddChild(_band);

        // ── Row 1: content — spacer + one visible host ──────────────────────
        var contentRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        contentRow.AddThemeConstantOverride("separation", 0);
        _band.AddChild(contentRow);

        contentRow.AddChild(new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter         = MouseFilterEnum.Ignore,
        });

        _ordersHost    = MakeHostPanel();
        _buildHost     = MakeHostPanel();
        _zonesHost     = MakeHostPanel();
        _jobsHost      = MakeHostPanel();
        _resourcesHost = MakeHostPanel();
        _smurfsHost    = MakeHostPanel();
        _animalsHost   = MakeHostPanel();
        contentRow.AddChild(_ordersHost);
        contentRow.AddChild(_buildHost);
        contentRow.AddChild(_zonesHost);
        contentRow.AddChild(_jobsHost);
        contentRow.AddChild(_resourcesHost);
        contentRow.AddChild(_smurfsHost);
        contentRow.AddChild(_animalsHost);

        // Stub content for the not-yet-built tabs. Orders, Smurfs,
        // Resources, Jobs, and Zones are filled by Attach() from
        // GameController.
        _buildHost  .AddChild(MakeStubContent("🔨 Build",   "Walls, floors, doors — Phase 5 stub"));
        _animalsHost.AddChild(MakeStubContent("🐾 Animals", "Husbandry / taming / pens — Phase 9 stub"));

        // ── Row 2: tab bar capsule, right-aligned ───────────────────────────
        var tabRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        tabRow.AddThemeConstantOverride("separation", 0);
        _band.AddChild(tabRow);

        tabRow.AddChild(new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter         = MouseFilterEnum.Ignore,
        });

        _tabBar = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _tabBar.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        tabRow.AddChild(_tabBar);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 4);
        _tabBar.AddChild(btnRow);

        _ordersBtn = MakeTabButton("📋 Orders");
        _ordersBtn.Pressed += () => ToggleTab(Tab.Orders);
        btnRow.AddChild(_ordersBtn);

        _buildBtn = MakeTabButton("🔨 Build");
        _buildBtn.Pressed += () => ToggleTab(Tab.Build);
        btnRow.AddChild(_buildBtn);

        _zonesBtn = MakeTabButton("▭ Zones");
        _zonesBtn.Pressed += () => ToggleTab(Tab.Zones);
        btnRow.AddChild(_zonesBtn);

        _jobsBtn = MakeTabButton("⚙ Jobs");
        _jobsBtn.Pressed += () => ToggleTab(Tab.Jobs);
        btnRow.AddChild(_jobsBtn);

        _resourcesBtn = MakeTabButton("📦 Resources");
        _resourcesBtn.Pressed += () => ToggleTab(Tab.Resources);
        btnRow.AddChild(_resourcesBtn);

        _smurfsBtn = MakeTabButton("👥 Smurfs");
        _smurfsBtn.Pressed += () => ToggleTab(Tab.Smurfs);
        btnRow.AddChild(_smurfsBtn);

        _animalsBtn = MakeTabButton("🐾 Animals");
        _animalsBtn.Pressed += () => ToggleTab(Tab.Animals);
        btnRow.AddChild(_animalsBtn);

        // Start closed.
        SetActiveTab(Tab.None);
    }

    private static PanelContainer MakeHostPanel()
    {
        var p = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            Visible     = false,
        };
        p.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        return p;
    }

    // MarginContainer wraps a centred VBox so the stub label sits centred
    // inside the panel with breathing room around it.
    private static Control MakeStubContent(string title, string desc)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(16));
        margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(16));
        margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(10));
        margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(10));

        var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        vbox.AddThemeConstantOverride("separation", 4);
        vbox.CustomMinimumSize = new Vector2(UITheme.Scaled(280), 0);
        margin.AddChild(vbox);

        var t = new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center };
        t.AddThemeFontSizeOverride("font_size", UITheme.Scaled(15));
        t.AddThemeColorOverride("font_color", UITheme.TextAccent);
        vbox.AddChild(t);

        var d = new Label { Text = desc, HorizontalAlignment = HorizontalAlignment.Center };
        d.AddThemeFontSizeOverride("font_size", UITheme.Scaled(12));
        d.AddThemeColorOverride("font_color", UITheme.TextMuted);
        vbox.AddChild(d);
        return margin;
    }

    private Button MakeTabButton(string label)
    {
        var btn = new Button
        {
            Text              = label,
            ToggleMode        = true,
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(UITheme.Scaled(96), UITheme.Scaled(UITheme.ToolbarButtonSize)),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(13));
        btn.AddThemeColorOverride("font_color",          UITheme.TextPrimary);
        btn.AddThemeColorOverride("font_hover_color",    UITheme.TextAccent);
        btn.AddThemeColorOverride("font_pressed_color",  UITheme.TextAccent);
        btn.AddThemeStyleboxOverride("normal",   FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("hover",    FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("pressed",  FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeStyleboxOverride("focus",    FloatingPanelStyle.MakeToolbarButton(true));
        return btn;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Attach(DesignationToolbar toolbar, SmurfRosterPanel roster,
        ResourcesPanel resources, JobsPanel jobs)
    {
        _orders    = toolbar;
        _roster    = roster;
        _resources = resources;
        _jobs      = jobs;
        _ordersHost   .AddChild(toolbar);
        _smurfsHost   .AddChild(roster);
        _resourcesHost.AddChild(resources);
        _jobsHost     .AddChild(jobs);

        // v0.5.1 (Phase 5A) — Zones tab content. Lazily constructed here
        // (after the toolbar exists, so ZonesPanel can bind to it for
        // shared active-tool state).
        _zones = new ZonesPanel { Name = "ZonesPanel" };
        _zonesHost.AddChild(_zones);
        _zones.BindToolbar(toolbar);
    }

    private ZonesPanel? _zones;

    public DesignationToolbar Orders    => _orders!;
    public SmurfRosterPanel   Roster    => _roster!;
    public ResourcesPanel     Resources => _resources!;
    public JobsPanel          Jobs      => _jobs!;

    public void SetActiveTab(Tab tab)
    {
        _active = tab;
        _ordersHost   .Visible = tab == Tab.Orders;
        _buildHost    .Visible = tab == Tab.Build;
        _zonesHost    .Visible = tab == Tab.Zones;
        _jobsHost     .Visible = tab == Tab.Jobs;
        _resourcesHost.Visible = tab == Tab.Resources;
        _smurfsHost   .Visible = tab == Tab.Smurfs;
        _animalsHost  .Visible = tab == Tab.Animals;
        RefreshTabButtons();
    }

    private void ToggleTab(Tab tab) =>
        SetActiveTab(_active == tab ? Tab.None : tab);

    // v0.3.28 — Used by GameController to suppress the mouse-wheel zoom path
    // when the cursor is over any of the bottom shell's actual painted UI.
    // Returns true for the tab bar capsule itself (always visible) and for
    // the currently visible content host (if any).
    public bool IsMouseOverContent()
    {
        var m = GetViewport().GetMousePosition();
        if (_tabBar != null && _tabBar.GetGlobalRect().HasPoint(m)) return true;
        if (_ordersHost   .Visible && _ordersHost   .GetGlobalRect().HasPoint(m)) return true;
        if (_buildHost    .Visible && _buildHost    .GetGlobalRect().HasPoint(m)) return true;
        if (_zonesHost    .Visible && _zonesHost    .GetGlobalRect().HasPoint(m)) return true;
        if (_jobsHost     .Visible && _jobsHost     .GetGlobalRect().HasPoint(m)) return true;
        if (_resourcesHost.Visible && _resourcesHost.GetGlobalRect().HasPoint(m)) return true;
        if (_smurfsHost   .Visible && _smurfsHost   .GetGlobalRect().HasPoint(m)) return true;
        if (_animalsHost  .Visible && _animalsHost  .GetGlobalRect().HasPoint(m)) return true;
        return false;
    }

    private void RefreshTabButtons()
    {
        _ordersBtn   .SetPressedNoSignal(_active == Tab.Orders);
        _buildBtn    .SetPressedNoSignal(_active == Tab.Build);
        _zonesBtn    .SetPressedNoSignal(_active == Tab.Zones);
        _jobsBtn     .SetPressedNoSignal(_active == Tab.Jobs);
        _resourcesBtn.SetPressedNoSignal(_active == Tab.Resources);
        _smurfsBtn   .SetPressedNoSignal(_active == Tab.Smurfs);
        _animalsBtn  .SetPressedNoSignal(_active == Tab.Animals);
    }
}
