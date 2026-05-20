using Godot;
using System.Collections.Generic;
using Sporeholm.UI;

// v0.5.44 (Phase 5 polish — RimWorld-parity Areas system).
//
// Replaces the v0.5.25 per-shroomp allowed-area painter with a colony-shared
// named-area model. The "Home" area exists from world creation; the player
// paints tiles into it via the 🖌 Paint button (which routes through the
// AllowedArea designation tool — same painter chrome as v0.5.25, different
// target). Each shroomp then gets a dropdown that sets `AssignedAreaName`:
//   • "Unrestricted" → shroomp works anywhere (no spatial gate)
//   • "Home"         → shroomp only works on tiles flagged in the Home bitmap
//
// BehaviorSystem.IsTileInAllowedArea reads the assigned area's cells at
// designation-task selection time, so a Forager assigned to Home will
// ignore a Gather designation 30 tiles outside the painted area.
//
// Future v0.5.45+ scope: create/rename/delete custom areas beyond Home.
// For now the tab ships with a single default area + the per-shroomp
// dropdown so the player can already restrict their colony spatially.
public partial class AreasPanel : Control
{
    private static readonly Color Parchment = new(0.95f, 0.89f, 0.70f);
    private static readonly Color Muted     = new(0.60f, 0.50f, 0.32f);
    private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Active    = new(0.65f, 0.95f, 0.50f);

    public Sporeholm.SimulationManager Sim { get; set; } = null!;
    public DesignationToolbar?              Toolbar { get; set; }

    private VBoxContainer _shroompList = null!;
    private Label         _areaLabel = null!;
    private Button        _paintBtn  = null!;
    private OptionButton  _areaSelector = null!;   // v0.5.45 — multi-area picker
    private LineEdit      _renameEdit   = null!;   // v0.5.45 — rename input
    private readonly Dictionary<string, OptionButton> _shroompDropdowns = new();
    private readonly List<string> _knownNames = new();
    private readonly List<string> _knownAreaNames = new();   // v0.5.45 — track for refresh

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;
        BuildShell();
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
        if (Toolbar != null) Toolbar.ToolChanged -= OnToolChanged;
    }

    private void OnUIScaleChanged()
    {
        _shroompDropdowns.Clear();
        _knownNames.Clear();
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildShell();
    }

    public void BindToolbar(DesignationToolbar toolbar)
    {
        if (Toolbar != null) Toolbar.ToolChanged -= OnToolChanged;
        Toolbar = toolbar;
        Toolbar.ToolChanged += OnToolChanged;
        RefreshPaintButton();
    }

    private void OnToolChanged(int newTool) => RefreshPaintButton();

    private void BuildShell()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(8));
        margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(8));
        margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(6));
        margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(6));
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.SizeFlagsVertical   = SizeFlags.ExpandFill;
        vbox.CustomMinimumSize   = new Vector2(UITheme.Scaled(380), UITheme.Scaled(180));
        margin.AddChild(vbox);

        vbox.AddChild(MakeLabel("🗺 Areas — Restrict where shroomps work", UITheme.Scaled(13), Gold));
        vbox.AddChild(MakeLabel(
            "Create named areas, paint tiles into them, then assign shroomps. Unrestricted = work anywhere.",
            UITheme.Scaled(9), Muted));

        // v0.5.45 — area-selector row. Replaces the v0.5.44 single-area
        // label. Player picks which area to edit; the painter writes to
        // that area's bitmap via DesignationToolbar.ActiveAreaName.
        var selectorRow = new HBoxContainer();
        selectorRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(selectorRow);

        selectorRow.AddChild(MakeLabel("Editing:", UITheme.Scaled(11), Parchment));

        _areaSelector = new OptionButton
        {
            CustomMinimumSize = new Vector2(UITheme.Scaled(110), UITheme.Scaled(22)),
            FocusMode         = FocusModeEnum.None,
        };
        _areaSelector.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
        _areaSelector.ItemSelected += idx =>
        {
            string name = _areaSelector.GetItemText((int)idx);
            Toolbar?.SetActiveAreaName(name);
            UpdatePaintButtonLabel();
        };
        selectorRow.AddChild(_areaSelector);

        // Kept as a status label too — shows which area the toolbar
        // currently considers "active" (mirrors selector but readable
        // when the dropdown is collapsed).
        _areaLabel = MakeLabel("Home", UITheme.Scaled(10), Active);
        selectorRow.AddChild(_areaLabel);

        _paintBtn = new Button
        {
            Text          = "🖌 Paint",
            ToggleMode    = true,
            FocusMode     = FocusModeEnum.None,
            TooltipText   = "Click then drag-paint tiles into the active area. Click again to deselect.",
            CustomMinimumSize = new Vector2(UITheme.Scaled(80), UITheme.Scaled(22)),
        };
        _paintBtn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(11));
        _paintBtn.Pressed += () =>
            Toolbar?.SetActiveTool(DesignationToolbar.Tool.AllowedArea);
        selectorRow.AddChild(_paintBtn);

        // v0.5.45 — Create / Rename / Delete row.
        var lifecycleRow = new HBoxContainer();
        lifecycleRow.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(lifecycleRow);

        _renameEdit = new LineEdit
        {
            PlaceholderText   = "New area name",
            CustomMinimumSize = new Vector2(UITheme.Scaled(140), UITheme.Scaled(22)),
        };
        _renameEdit.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
        lifecycleRow.AddChild(_renameEdit);

        var createBtn = MakeSmallActionButton("➕ Create",
            "Create a new named area with the name typed in the field.");
        createBtn.Pressed += () =>
        {
            if (Sim == null) return;   // v0.5.53 — guard against pre-StartSim clicks
            string name = _renameEdit.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (Sim.CreateArea(name))
            {
                _renameEdit.Text = "";
                Toolbar?.SetActiveAreaName(name);
            }
        };
        lifecycleRow.AddChild(createBtn);

        var renameBtn = MakeSmallActionButton("✎ Rename",
            "Rename the currently-edited area to the name typed in the field.");
        renameBtn.Pressed += () =>
        {
            if (Sim == null) return;   // v0.5.53 — guard against pre-StartSim clicks
            string newName = _renameEdit.Text.Trim();
            string oldName = Toolbar?.ActiveAreaName ?? "";
            if (string.IsNullOrEmpty(newName) || string.IsNullOrEmpty(oldName)) return;
            if (Sim.RenameArea(oldName, newName))
            {
                _renameEdit.Text = "";
                Toolbar?.SetActiveAreaName(newName);
            }
        };
        lifecycleRow.AddChild(renameBtn);

        var deleteBtn = MakeSmallActionButton("🗑 Delete",
            "Delete the currently-edited area. Shroomps assigned to it revert to Unrestricted.");
        deleteBtn.Pressed += () =>
        {
            if (Sim == null) return;   // v0.5.53 — guard against pre-StartSim clicks
            string name = Toolbar?.ActiveAreaName ?? "";
            if (string.IsNullOrEmpty(name)) return;
            if (Sim.DeleteArea(name))
            {
                // After delete, fall back to the first remaining area
                // (or "Home" — auto-recreated by next snapshot if it's
                // the one we deleted).
                var remaining = Sim.SnapshotAreaNames();
                Toolbar?.SetActiveAreaName(remaining.Count > 0 ? remaining[0] : "Home");
            }
        };
        lifecycleRow.AddChild(deleteBtn);

        vbox.AddChild(MakeLabel("Shroomp assignment", UITheme.Scaled(11), Gold));

        // Per-shroomp scrollable list.
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            CustomMinimumSize    = new Vector2(0, UITheme.Scaled(120)),
        };
        vbox.AddChild(scroll);

        _shroompList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _shroompList.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_shroompList);

        RefreshPaintButton();
    }

    public override void _Process(double delta)
    {
        if (Sim == null) return;
        if (!Visible) return;
        RefreshAreaSelectorIfChanged();   // v0.5.45
        RefreshIfRosterChanged();
        RefreshAssignmentsFromSnapshot();
        RefreshPaintButton();
    }

    private void RefreshPaintButton()
    {
        if (_paintBtn == null) return;
        bool active = Toolbar != null &&
            Toolbar.ActiveTool == DesignationToolbar.Tool.AllowedArea;
        _paintBtn.SetPressedNoSignal(active);
        UpdatePaintButtonLabel();
    }

    // v0.5.45 — keeps the paint button + status label in sync with the
    // toolbar's active area name. Called whenever the area selector
    // changes or the active area shifts via Toolbar.SetActiveAreaName.
    private void UpdatePaintButtonLabel()
    {
        if (Toolbar == null) return;
        _areaLabel.Text = Toolbar.ActiveAreaName;
        _paintBtn.Text  = $"🖌 Paint {Toolbar.ActiveAreaName}";
    }

    // v0.5.45 — repopulates the area selector dropdown when the map's
    // named-area list changes (Create/Rename/Delete). Cheap O(N) compare
    // before mutating so we don't churn on every Process tick.
    private void RefreshAreaSelectorIfChanged()
    {
        var names = Sim.SnapshotAreaNames();
        bool changed = names.Count != _knownAreaNames.Count;
        if (!changed)
            for (int i = 0; i < names.Count; i++)
                if (names[i] != _knownAreaNames[i]) { changed = true; break; }
        if (!changed) return;

        _knownAreaNames.Clear();
        _areaSelector.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            _knownAreaNames.Add(names[i]);
            _areaSelector.AddItem(names[i]);
        }
        // Keep the dropdown selection synced with the toolbar's active
        // area name (in case Create / Delete shifted things underfoot).
        string activeName = Toolbar?.ActiveAreaName ?? "";
        int activeIdx = _knownAreaNames.IndexOf(activeName);
        if (activeIdx >= 0) _areaSelector.Selected = activeIdx;
        else if (_knownAreaNames.Count > 0)
        {
            // Toolbar's active area was deleted; snap to first.
            _areaSelector.Selected = 0;
            Toolbar?.SetActiveAreaName(_knownAreaNames[0]);
        }
        // Force per-shroomp dropdown rebuild to pick up the new area list.
        _knownNames.Clear();
        foreach (Node c in _shroompList.GetChildren()) c.QueueFree();
        _shroompDropdowns.Clear();
    }

    private void RefreshIfRosterChanged()
    {
        var assignments = Sim.SnapshotShroompAreaAssignments();
        bool sameShape = assignments.Count == _knownNames.Count;
        if (sameShape)
            for (int i = 0; i < assignments.Count; i++)
                if (assignments[i].Name != _knownNames[i]) { sameShape = false; break; }
        if (sameShape) return;

        foreach (Node c in _shroompList.GetChildren()) c.QueueFree();
        _shroompDropdowns.Clear();
        _knownNames.Clear();

        var areaNames = Sim.SnapshotAreaNames();
        foreach (var (name, _) in assignments)
        {
            _knownNames.Add(name);
            _shroompList.AddChild(MakeAssignmentRow(name, areaNames));
        }
    }

    private void RefreshAssignmentsFromSnapshot()
    {
        var assignments = Sim.SnapshotShroompAreaAssignments();
        foreach (var (name, areaName) in assignments)
        {
            if (!_shroompDropdowns.TryGetValue(name, out var drop)) continue;
            // Index 0 = Unrestricted; index 1..N = area names sorted.
            int target = 0;
            if (areaName != null)
            {
                for (int i = 1; i < drop.ItemCount; i++)
                    if (drop.GetItemText(i) == areaName) { target = i; break; }
            }
            if (drop.Selected != target) drop.Selected = target;
        }
    }

    private Control MakeAssignmentRow(string shroompName, IReadOnlyList<string> areaNames)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);

        var nameLbl = MakeLabel(shroompName, UITheme.Scaled(10), Parchment);
        nameLbl.CustomMinimumSize = new Vector2(UITheme.Scaled(120), UITheme.Scaled(20));
        nameLbl.ClipText = true;
        row.AddChild(nameLbl);

        var drop = new OptionButton
        {
            CustomMinimumSize = new Vector2(UITheme.Scaled(120), UITheme.Scaled(22)),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
        };
        drop.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
        drop.AddItem("Unrestricted");
        foreach (var a in areaNames) drop.AddItem(a);
        drop.Selected = 0;
        drop.ItemSelected += idx =>
        {
            string? target = idx == 0 ? null : drop.GetItemText((int)idx);
            Sim.SetShroompAssignedArea(shroompName, target);
        };
        row.AddChild(drop);
        _shroompDropdowns[shroompName] = drop;
        return row;
    }

    private Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    // v0.5.45 — compact action button for Create / Rename / Delete.
    // Matches the AreasPanel's general 22-logical-px row height.
    private Button MakeSmallActionButton(string label, string tooltip)
    {
        var btn = new Button
        {
            Text              = label,
            FocusMode         = FocusModeEnum.None,
            TooltipText       = tooltip,
            CustomMinimumSize = new Vector2(UITheme.Scaled(78), UITheme.Scaled(22)),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
        return btn;
    }

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
