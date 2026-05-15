using Godot;
using System.Collections.Generic;
using SmurfulationC.Simulation;
using SmurfulationC.UI;

// v0.3.47 (Phase 4 sub-B) — RimWorld-style work priority grid.
//
// Replaces the v0.3.x "Phase 3.10 stub" label in BottomTabPanel's Jobs tab.
// Columns are WorkPriorityDefaults.Categories (Patient, BedRest, Doctor,
// Construct, Mine, PlantCut, Grow, Cook, Hunt, Forage, Chop, Haul, Clean,
// Research, Attune); rows are alive smurfs in roster order. Cell values
// are 0 (off) through 4 (priority); clicking a cell cycles 1 → 2 → 3 → 4 →
// off → 1. Header click cycles every cell in that column. Right-click on
// a cell or header sets to off.
//
// BehaviorSystem.SelectTask consults `s.WorkPriorities[category]` to gate
// Tier 2 evaluation: 0 = blacklist, 1-4 = consider in priority order.
public partial class JobsPanel : Control
{
    private static readonly Color Parchment = new(0.95f, 0.89f, 0.70f);
    private static readonly Color Muted     = new(0.60f, 0.50f, 0.32f);
    private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Prio1     = new(0.45f, 0.85f, 0.45f);   // green
    private static readonly Color Prio2     = new(0.75f, 0.85f, 0.30f);
    private static readonly Color Prio3     = new(0.90f, 0.75f, 0.30f);
    private static readonly Color Prio4     = new(0.85f, 0.55f, 0.30f);
    private static readonly Color OffColor  = new(0.45f, 0.40f, 0.32f);

    public SmurfulationC.SimulationManager Sim { get; set; } = null!;

    private ScrollContainer _scroll = null!;
    private GridContainer   _grid   = null!;

    // Per-cell button captured so Refresh can update labels in place.
    private readonly Dictionary<(string Name, string Cat), Button> _cells = new();

    // Row count from last refresh — re-build the grid only when the roster
    // shape changes, not every Process tick.
    private int _lastRowCount = -1;
    private readonly List<string> _lastRowNames = new();

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
    }

    private void OnUIScaleChanged()
    {
        _cells.Clear();
        _lastRowCount = -1;
        _lastRowNames.Clear();
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildShell();
    }

    // v0.4.10 — compact grid metrics. Previous values left ~50 % of the
    // panel as whitespace. Cells went from 52×22 → 38×18; name column
    // from 96 → 76; grid separations 4×3 → 2×1; outer margins / vbox
    // separation halved. Result: same 15 columns × N rows in roughly
    // 60 % of the previous footprint.
    private const int CellW   = 38;
    private const int CellH   = 18;
    private const int NameW   = 76;
    private const int FontSm  = 10;
    private const int FontHdr = 10;

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
        vbox.AddThemeConstantOverride("separation", 3);
        // Wide enough for 15 work categories + name column at the new
        // compact metrics: 76 (name) + 15 × 38 (cells) + 14 × 2 (h_sep)
        // ≈ 674 px at 1× scale; round up for breathing room.
        vbox.CustomMinimumSize = new Vector2(UITheme.Scaled(700), UITheme.Scaled(200));
        margin.AddChild(vbox);

        var title = MakeLabel("⚙ Jobs — Work Priorities", UITheme.Scaled(13), Gold);
        vbox.AddChild(title);
        var subtitle = MakeLabel(
            "1 = highest, 4 = lowest, dash = forbidden. Click cycles · right-click sets off.",
            UITheme.Scaled(9), Muted);
        vbox.AddChild(subtitle);

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            CustomMinimumSize    = new Vector2(0, UITheme.Scaled(160)),
        };
        vbox.AddChild(_scroll);

        // GridContainer columns = 1 (name) + Categories.Length
        _grid = new GridContainer { Columns = 1 + WorkPriorityDefaults.Categories.Length };
        _grid.AddThemeConstantOverride("h_separation", 2);
        _grid.AddThemeConstantOverride("v_separation", 1);
        _scroll.AddChild(_grid);

        BuildHeaderRow();
    }

    private void BuildHeaderRow()
    {
        // Top-left corner — empty.
        _grid.AddChild(MakeHeaderLabel(""));

        foreach (var cat in WorkPriorityDefaults.Categories)
        {
            var btn = new Button
            {
                Text              = cat,
                Flat              = true,
                FocusMode         = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(UITheme.Scaled(CellW), UITheme.Scaled(CellH)),
                TooltipText       = $"Click to cycle every smurf's '{cat}' priority. Right-click to set all to off.",
            };
            btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(FontHdr));
            btn.AddThemeColorOverride("font_color", Gold);
            string captured = cat;
            btn.Pressed += () => CycleColumn(captured);
            btn.GuiInput += (e) =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed
                    && mb.ButtonIndex == MouseButton.Right)
                {
                    SetColumnAll(captured, 0);
                }
            };
            _grid.AddChild(btn);
        }
    }

    public override void _Process(double delta)
    {
        if (Sim == null) return;
        if (!Visible) return;
        RefreshIfRosterChanged();
        RefreshCellValues();
    }

    private void RefreshIfRosterChanged()
    {
        var snap = Sim.GetWorkPrioritiesSnapshot();
        // Compare keys against the last shape.
        bool sameShape = snap.Count == _lastRowCount;
        if (sameShape)
        {
            int i = 0;
            foreach (var name in snap.Keys)
            {
                if (i >= _lastRowNames.Count || _lastRowNames[i] != name)
                {
                    sameShape = false;
                    break;
                }
                i++;
            }
        }

        if (sameShape) return;

        // Rebuild rows (keep header — it lives in the first row).
        var keep = new List<Node>();
        for (int j = 0; j < 1 + WorkPriorityDefaults.Categories.Length; j++)
            keep.Add(_grid.GetChild(j));
        foreach (Node c in _grid.GetChildren())
            if (!keep.Contains(c)) c.QueueFree();
        _cells.Clear();
        _lastRowNames.Clear();

        foreach (var (name, prios) in snap)
        {
            _lastRowNames.Add(name);

            _grid.AddChild(MakeNameCell(name));
            foreach (var cat in WorkPriorityDefaults.Categories)
                _grid.AddChild(MakeCell(name, cat));
        }
        _lastRowCount = snap.Count;
    }

    private void RefreshCellValues()
    {
        var snap = Sim.GetWorkPrioritiesSnapshot();
        foreach (var (name, prios) in snap)
        {
            foreach (var cat in WorkPriorityDefaults.Categories)
            {
                if (!_cells.TryGetValue((name, cat), out var btn)) continue;
                byte val = prios.TryGetValue(cat, out var v) ? v : (byte)0;
                btn.Text = val switch
                {
                    0 => "—",
                    _ => val.ToString(),
                };
                btn.AddThemeColorOverride("font_color", PrioColor(val));
            }
        }
    }

    private Color PrioColor(byte v) => v switch
    {
        1 => Prio1, 2 => Prio2, 3 => Prio3, 4 => Prio4, _ => OffColor,
    };

    private Label MakeHeaderLabel(string text)
    {
        var l = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(UITheme.Scaled(NameW), UITheme.Scaled(CellH)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.AddThemeColorOverride("font_color", Gold);
        l.AddThemeFontSizeOverride("font_size", UITheme.Scaled(FontHdr));
        return l;
    }

    private Label MakeNameCell(string name)
    {
        var l = new Label
        {
            Text = name,
            CustomMinimumSize = new Vector2(UITheme.Scaled(NameW), UITheme.Scaled(CellH)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        l.AddThemeColorOverride("font_color", Parchment);
        l.AddThemeFontSizeOverride("font_size", UITheme.Scaled(FontSm));
        return l;
    }

    private Button MakeCell(string name, string cat)
    {
        var btn = new Button
        {
            Text              = "—",
            Flat              = true,
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(UITheme.Scaled(CellW), UITheme.Scaled(CellH)),
        };
        btn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(FontSm));
        btn.AddThemeColorOverride("font_color", OffColor);
        string capturedName = name;
        string capturedCat  = cat;
        btn.Pressed += () => CycleCell(capturedName, capturedCat);
        btn.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed
                && mb.ButtonIndex == MouseButton.Right)
            {
                Sim.SetWorkPriority(capturedName, capturedCat, 0);
            }
        };
        _cells[(name, cat)] = btn;
        return btn;
    }

    // Cycles a single cell 1 → 2 → 3 → 4 → off → 1.
    private void CycleCell(string name, string cat)
    {
        var snap = Sim.GetWorkPrioritiesSnapshot();
        byte cur = snap.TryGetValue(name, out var prios)
            && prios.TryGetValue(cat, out var v) ? v : (byte)0;
        byte next = cur switch
        {
            0 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 0,
        };
        Sim.SetWorkPriority(name, cat, next);
    }

    // Cycles every cell in a column to a common next value.
    private void CycleColumn(string cat)
    {
        var snap = Sim.GetWorkPrioritiesSnapshot();
        // Use the first smurf's value to decide the cycle direction.
        byte cur = 0;
        foreach (var (_, prios) in snap)
        {
            cur = prios.TryGetValue(cat, out var v) ? v : (byte)0;
            break;
        }
        byte next = cur switch
        {
            0 => 1, 1 => 2, 2 => 3, 3 => 4, _ => 0,
        };
        SetColumnAll(cat, next);
    }

    private void SetColumnAll(string cat, byte v)
    {
        var snap = Sim.GetWorkPrioritiesSnapshot();
        foreach (var (name, _) in snap)
            Sim.SetWorkPriority(name, cat, v);
    }

    private Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
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
