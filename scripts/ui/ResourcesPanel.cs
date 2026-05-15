using Godot;
using System.Collections.Generic;
using SmurfulationC;
using SmurfulationC.Simulation.Items;
using SmurfulationC.UI;

// v0.3.41 — granular resource ledger for the bottom-bar "📦 Resources" tab.
// v0.3.46 (Phase 4 core) — populates from the live colony Inventory snapshot.
// Each row corresponds to one stack-key (SubType / Material / Quality / State);
// expanding a category drops the per-stack list beneath it.
//
// Layout matches Phase 4 roadmap §4 Resources Tab spec:
//   Category   Sub-type   Material   Quality   Condition   Count
//
// Sort + per-column header click is queued for the next pass; v0.3.46 keeps
// stacks in their inventory-insertion order which is good-enough for the
// initial UI.
public partial class ResourcesPanel : Control
{
    private static readonly Color Parchment = new(0.95f, 0.89f, 0.70f);
    private static readonly Color Muted     = new(0.60f, 0.50f, 0.32f);
    private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Spoiling  = new(0.85f, 0.55f, 0.20f);
    private static readonly Color Spoiled   = new(0.85f, 0.30f, 0.30f);

    public SmurfulationC.SimulationManager Sim { get; set; } = null!;

    private VBoxContainer _categoriesVbox = null!;

    private sealed class CategoryRow
    {
        public ItemKind       Kind;
        public string         Name           = "";
        public string         Icon           = "";
        public Button         CaretBtn       = null!;
        public Label          TotalLbl       = null!;
        public VBoxContainer  ExpansionBox   = null!;
        public VBoxContainer  StackList      = null!;   // populated each tick
        public Label          EmptyLbl       = null!;   // shown when no stacks
        public bool           Expanded;
    }

    private readonly List<CategoryRow> _rows = new();

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
        margin.AddThemeConstantOverride("margin_left",   UITheme.Scaled(16));
        margin.AddThemeConstantOverride("margin_right",  UITheme.Scaled(16));
        margin.AddThemeConstantOverride("margin_top",    UITheme.Scaled(12));
        margin.AddThemeConstantOverride("margin_bottom", UITheme.Scaled(12));
        AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.CustomMinimumSize = new Vector2(UITheme.Scaled(680), UITheme.Scaled(240));
        margin.AddChild(vbox);

        // ── Title ──────────────────────────────────────────────────────────
        var title = Lbl("📦 Colony Resources", UITheme.Scaled(16), Gold);
        vbox.AddChild(title);

        var subtitle = Lbl(
            "Live inventory — expand a category to see stacks by sub-type, material, and quality.",
            UITheme.Scaled(10), Muted);
        vbox.AddChild(subtitle);

        // ── Header row ─────────────────────────────────────────────────────
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(header);

        AddHeaderCell(header, "Category",  UITheme.Scaled(170));
        AddHeaderCell(header, "Sub-type",  UITheme.Scaled(140));
        AddHeaderCell(header, "Material",  UITheme.Scaled(100));
        AddHeaderCell(header, "Quality",   UITheme.Scaled(80));
        AddHeaderCell(header, "Condition", UITheme.Scaled(80));
        AddHeaderCell(header, "Count",     UITheme.Scaled(60));

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.15f, 0.5f));
        vbox.AddChild(sep);

        // ── Category rows ──────────────────────────────────────────────────
        // v0.4.11 — wrap the per-category list in a ScrollContainer so the
        // expanded stack-list (which can grow to hundreds of rows once
        // the colony's a few in-game days old) doesn't push the panel
        // past the top of the viewport. CustomMinimumSize caps the
        // viewport at 280 px; content overflows into a vertical
        // scrollbar instead of pushing the panel taller.
        var rowsScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode   = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize    = new Vector2(0, UITheme.Scaled(280)),
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
        };
        vbox.AddChild(rowsScroll);

        _categoriesVbox = new VBoxContainer();
        _categoriesVbox.AddThemeConstantOverride("separation", 2);
        _categoriesVbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rowsScroll.AddChild(_categoriesVbox);

        // v0.4.2 — Magic re-enabled (MagicBerry plants + MagicCrystal
        // ore-veins now produce items). Weapon / Apparel / Furniture /
        // TradeGood still hidden — no production path until Phase 5+
        // crafting, Phase 7 combat, Phase 11 trade.
        AddCategoryRow(ItemKind.Food,      "🍓", "Food");
        AddCategoryRow(ItemKind.Material,  "🪨", "Material");
        AddCategoryRow(ItemKind.Tool,      "🔨", "Tool");
        AddCategoryRow(ItemKind.Magic,     "✨", "Magic");
        AddCategoryRow(ItemKind.Trinket,   "🌟", "Trinket");
    }

    private static void AddHeaderCell(HBoxContainer parent, string text, int width)
    {
        var l = Lbl(text, UITheme.Scaled(11), Gold);
        l.CustomMinimumSize = new Vector2(width, 0);
        parent.AddChild(l);
    }

    private void AddCategoryRow(ItemKind kind, string icon, string name)
    {
        var row = new CategoryRow { Kind = kind, Name = name, Icon = icon };

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 2);
        _categoriesVbox.AddChild(col);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        col.AddChild(header);

        // ── Category cell (caret + icon + name) ───────────────────────────
        var catCell = new HBoxContainer();
        catCell.AddThemeConstantOverride("separation", 4);
        catCell.CustomMinimumSize = new Vector2(UITheme.Scaled(170), 0);
        header.AddChild(catCell);

        row.CaretBtn = new Button
        {
            Text              = "▶",
            Flat              = true,
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(UITheme.Scaled(16), UITheme.Scaled(16)),
        };
        row.CaretBtn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
        row.CaretBtn.AddThemeColorOverride("font_color",         Muted);
        row.CaretBtn.AddThemeColorOverride("font_hover_color",   Gold);
        row.CaretBtn.AddThemeColorOverride("font_pressed_color", Gold);
        row.CaretBtn.TooltipText = $"Toggle {name} breakdown";
        catCell.AddChild(row.CaretBtn);

        catCell.AddChild(Lbl($"{icon} {name}", UITheme.Scaled(12), Parchment));

        // Placeholder cells filled by the per-stack expansion rows below.
        header.AddChild(MakeCell("",  UITheme.Scaled(140), Muted));
        header.AddChild(MakeCell("",  UITheme.Scaled(100), Muted));
        header.AddChild(MakeCell("",  UITheme.Scaled(80),  Muted));
        header.AddChild(MakeCell("",  UITheme.Scaled(80),  Muted));

        row.TotalLbl = Lbl("0", UITheme.Scaled(12), Parchment);
        row.TotalLbl.CustomMinimumSize = new Vector2(UITheme.Scaled(60), 0);
        header.AddChild(row.TotalLbl);

        row.ExpansionBox = new VBoxContainer { Visible = false };
        row.ExpansionBox.AddThemeConstantOverride("separation", 1);
        col.AddChild(row.ExpansionBox);

        // Indented host for the per-stack rows.
        var indent = new MarginContainer();
        indent.AddThemeConstantOverride("margin_left", UITheme.Scaled(28));
        row.ExpansionBox.AddChild(indent);
        row.StackList = new VBoxContainer();
        row.StackList.AddThemeConstantOverride("separation", 0);
        indent.AddChild(row.StackList);

        row.EmptyLbl = Lbl($"(no {name.ToLower()} in colony)", UITheme.Scaled(10), Muted);
        row.StackList.AddChild(row.EmptyLbl);

        var captured = row;
        row.CaretBtn.Pressed += () =>
        {
            captured.Expanded = !captured.Expanded;
            captured.ExpansionBox.Visible = captured.Expanded;
            captured.CaretBtn.Text = captured.Expanded ? "▼" : "▶";
        };

        _rows.Add(row);
    }

    private static Label MakeCell(string text, int width, Color colour)
    {
        var l = Lbl(text, UITheme.Scaled(11), colour);
        l.CustomMinimumSize = new Vector2(width, 0);
        return l;
    }

    public override void _Process(double delta)
    {
        if (Sim == null) return;
        if (!Visible) return;

        var inv = Sim.GetInventorySnapshot();

        // Pre-bucket rows by ItemKind so the per-category walk is O(items)
        // rather than O(items × categories).
        var byKind = new Dictionary<ItemKind, List<InventoryRow>>();
        foreach (var item in inv)
        {
            if (!byKind.TryGetValue(item.Kind, out var list))
                byKind[item.Kind] = list = new List<InventoryRow>();
            list.Add(item);
        }

        foreach (var cat in _rows)
        {
            byKind.TryGetValue(cat.Kind, out var stacks);
            int total = 0;
            if (stacks != null) foreach (var s in stacks) total += s.Quantity;
            cat.TotalLbl.Text = total.ToString();

            // Repopulate the stack list only if the category is expanded —
            // collapsed categories don't need their rows visible.
            if (!cat.Expanded) continue;
            RebuildStackList(cat, stacks);
        }
    }

    private void RebuildStackList(CategoryRow cat, List<InventoryRow>? stacks)
    {
        // Clear existing per-stack rows. EmptyLbl is re-added below if empty.
        foreach (Node child in cat.StackList.GetChildren())
            child.QueueFree();

        if (stacks == null || stacks.Count == 0)
        {
            cat.EmptyLbl = Lbl($"(no {cat.Name.ToLower()} in colony)", UITheme.Scaled(10), Muted);
            cat.StackList.AddChild(cat.EmptyLbl);
            return;
        }

        foreach (var s in stacks)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            cat.StackList.AddChild(row);

            // Inset under the category header (sub-type column is the
            // first stack-row data; the category label sits to the left).
            row.AddChild(MakeCell("", UITheme.Scaled(40), Muted));   // empty pad
            row.AddChild(MakeCell(SubTypeDisplay(s), UITheme.Scaled(130), Parchment));
            row.AddChild(MakeCell(MaterialDisplay(s), UITheme.Scaled(100), Muted));
            row.AddChild(MakeCell(s.Quality.ToString(), UITheme.Scaled(80), Muted));

            // Condition cell — colour by state.
            float ratio = s.DurabilityCap > 0 ? s.AvgCondition / s.DurabilityCap : 0;
            Color cond = s.State switch
            {
                ItemState.Spoiled => Spoiled,
                ItemState.Stale   => Spoiling,
                ItemState.Broken  => Spoiled,
                _                 => Parchment,
            };
            row.AddChild(MakeCell($"{ratio*100:0}%", UITheme.Scaled(80), cond));

            var count = Lbl(s.Quantity.ToString(), UITheme.Scaled(11), Parchment);
            count.CustomMinimumSize = new Vector2(UITheme.Scaled(60), 0);
            row.AddChild(count);
        }
    }

    private static string SubTypeDisplay(InventoryRow s)
    {
        var def = ItemRegistry.Get(s.Kind, s.SubType);
        return def?.DisplayName ?? s.SubType;
    }

    private static string MaterialDisplay(InventoryRow s)
    {
        if (string.IsNullOrEmpty(s.MaterialFamily)) return "—";
        var def = MaterialRegistry.Get(new MaterialKey(s.MaterialFamily, s.MaterialSubType));
        return def?.DisplayName ?? s.MaterialSubType;
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

    private static Label Lbl(string text, int size, Color color)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        return l;
    }
}
