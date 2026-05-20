using Godot;
using System.Collections.Generic;
using Sporeholm.Simulation;
using Sporeholm.UI;

// v0.3.47 (Phase 4 sub-B) — RimWorld-style work priority grid.
//
// Replaces the v0.3.x "Phase 3.10 stub" label in BottomTabPanel's Jobs tab.
// Columns are WorkPriorityDefaults.Categories (Patient, BedRest, Doctor,
// Construct, Mine, PlantCut, Grow, Cook, Hunt, Forage, Chop, Haul, Clean,
// Research, Attune); rows are alive shroomps in roster order. Cell values
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

    public Sporeholm.SimulationManager Sim { get; set; } = null!;

    private ScrollContainer _scroll     = null!;
    private GridContainer   _grid       = null!;
    // v0.5.48 — sticky header lives OUTSIDE the scroll so it stays
    // visible while the player scrolls through the shroomp list at
    // high-population counts.
    private GridContainer   _headerGrid = null!;

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
    //
    // v0.5.38 — `CellW` is now the *minimum* width per column instead of
    // a fixed width. Every cell sets `SizeFlagsHorizontal = ExpandFill`
    // so the GridContainer distributes any extra width across the 15
    // columns equally. The vbox no longer forces a 700-px minimum, so a
    // narrow host (small viewport + large UI Size + many other tabs) lets
    // cells compress to `CellMinW` instead of clipping the rightmost
    // columns. `NameW` similarly is a minimum that grows with available
    // width via the same Expand flag.
    private const int CellW    = 70;   // preferred width when room allows
    // v0.5.43 → v0.5.48 — CellMinW bumped 48 → 64 so all 15 in-game-verb
    // headers fit at default UI Size without the panel needing to grow
    // to its absolute minimum. Each column still ExpandFills extra space.
    private const int CellMinW = 64;
    private const int CellH    = 18;
    private const int NameW    = 96;
    private const int NameMinW = 78;   // shroomp-name column — fits long names like "Architect"
    private const int FontSm   = 10;
    private const int FontHdr  = 10;

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
        vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.SizeFlagsVertical   = SizeFlags.ExpandFill;
        // v0.5.38 — minimum width reads off CellMinW + NameMinW so the
        // panel fits into a constrained host (e.g. UI Size at 100 % +
        // narrow BottomTabPanel slot). At default UI Size: 50 (name) +
        // 15 × 24 (cells) + 14 × 2 (h_sep) ≈ 438 px (vs the previous
        // rigid 700 px). Cells use ExpandFill to share any extra
        // horizontal space — see MakeCell / MakeNameCell.
        int minW = NameMinW + WorkPriorityDefaults.Categories.Length * CellMinW
                 + (WorkPriorityDefaults.Categories.Length - 1) * 2;
        vbox.CustomMinimumSize = new Vector2(UITheme.Scaled(minW), UITheme.Scaled(180));
        margin.AddChild(vbox);

        var title = MakeLabel("⚙ Jobs — Work Priorities", UITheme.Scaled(13), Gold);
        vbox.AddChild(title);
        var subtitle = MakeLabel(
            "1 = highest, 4 = lowest, dash = forbidden. Click cycles · right-click sets off.",
            UITheme.Scaled(9), Muted);
        vbox.AddChild(subtitle);

        // v0.5.48 — sticky header grid lives OUTSIDE the scroll container,
        // so column titles stay pinned at the top while the player scrolls
        // through the shroomp list (RimWorld convention). Both grids share
        // the same Columns count + ExpandFill cells so column widths
        // align automatically across the two grids.
        _headerGrid = new GridContainer { Columns = 1 + WorkPriorityDefaults.Categories.Length };
        _headerGrid.AddThemeConstantOverride("h_separation", 2);
        _headerGrid.AddThemeConstantOverride("v_separation", 1);
        _headerGrid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(_headerGrid);

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,   // v0.5.48 — header isn't scrolled horizontally; columns auto-fit
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            CustomMinimumSize    = new Vector2(0, UITheme.Scaled(160)),
        };
        vbox.AddChild(_scroll);

        // Data GridContainer — same Columns count as _headerGrid; rows
        // are shroomps, columns are work categories. Cells use ExpandFill
        // so columns align with the header above.
        _grid = new GridContainer { Columns = 1 + WorkPriorityDefaults.Categories.Length };
        _grid.AddThemeConstantOverride("h_separation", 2);
        _grid.AddThemeConstantOverride("v_separation", 1);
        _grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(_grid);

        BuildHeaderRow();
    }

    private void BuildHeaderRow()
    {
        // v0.5.48 — header cells now go into the dedicated _headerGrid
        // OUTSIDE the scroll container so they stay pinned while the
        // player scrolls the data grid.
        _headerGrid.AddChild(MakeHeaderLabel(""));

        foreach (var cat in WorkPriorityDefaults.Categories)
        {
            // v0.5.40 — header text uses the in-game verb (Excavate / Cut /
            // Gather / Build / Chop) while the column key stays at the
            // save-compat-stable internal name (Mine / PlantCut / Forage /
            // Construct / Chop). Tooltip explains what the column gates.
            string label   = WorkPriorityDefaults.DisplayLabel(cat);
            string tooltip = WorkPriorityDefaults.DisplayTooltip(cat) +
                "\n\nClick the header to cycle every shroomp in this column. Right-click clears the column to off.";
            var btn = new Button
            {
                Text              = label,
                Flat              = true,
                FocusMode         = FocusModeEnum.None,
                // v0.5.38 — minimum width only; ExpandFill makes the
                // header share width with the cell columns below it
                // so the grid stays aligned at any host width / UI scale.
                CustomMinimumSize = new Vector2(UITheme.Scaled(CellMinW), UITheme.Scaled(CellH)),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText       = tooltip,
                ClipText          = true,   // overflow names (e.g. "Construct") clip rather than expand
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
            _headerGrid.AddChild(btn);   // v0.5.48 — into sticky header grid
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

        // v0.5.48 — header lives in the separate _headerGrid outside the
        // scroll, so the data _grid only contains shroomp rows now. Clear
        // everything and rebuild from the snapshot.
        foreach (Node c in _grid.GetChildren()) c.QueueFree();
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
        // The top-left corner cell. Mirrors the name column's min width
        // (ExpandFill so the spacer holds the shroomp-name column width
        // even when the header text is empty).
        var l = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(UITheme.Scaled(NameMinW), UITheme.Scaled(CellH)),
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
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
            CustomMinimumSize = new Vector2(UITheme.Scaled(NameMinW), UITheme.Scaled(CellH)),
            VerticalAlignment = VerticalAlignment.Center,
            // v0.5.38 — share width with the corner cell + cells below.
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText            = true,
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
            // v0.5.38 — minimum cell width only. The GridContainer
            // distributes any extra horizontal space equally across
            // the 15 priority columns via the Expand flag.
            CustomMinimumSize = new Vector2(UITheme.Scaled(CellMinW), UITheme.Scaled(CellH)),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText          = true,
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
        // Use the first shroomp's value to decide the cycle direction.
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
