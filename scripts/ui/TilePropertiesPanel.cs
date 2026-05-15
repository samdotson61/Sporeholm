using Godot;
using System.Collections.Generic;
using System.Text;
using SmurfulationC.Simulation.Items;
using SmurfulationC.UI;
using SmurfulationC.World;

// v0.4.34 — RimWorld-style "stationary inspector". Left-click an empty
// tile (no smurf, no active designation tool) that has items or
// vegetation on it and this card opens with the relevant properties
// laid out in friendly fields. Modelled after `SmurfCardPanel`'s
// floating-panel + scroll-content shape but simpler: one column of
// sections, no tabs.
//
// IMPORTANT — the card is a SNAPSHOT, not a live view. `Open(tx, ty,
// map)` captures everything once into local fields and rebuilds the
// content; closing and re-opening on the same tile picks up any state
// changes. There is NO per-tick refresh subscription — Sam called this
// out explicitly so the inspector doesn't pay the cost of re-reading
// dropped-items / vegetation every frame for a card the player may not
// even be looking at.
public partial class TilePropertiesPanel : Control
{
    private static readonly Color DarkWood = UITheme.TextPrimary;
    private static readonly Color Gold     = UITheme.TextAccent;
    private static readonly Color TextDim  = UITheme.TextMuted;
    private static readonly Color PosCol   = new(0.20f, 0.65f, 0.25f);
    private static readonly Color NegCol   = new(0.80f, 0.25f, 0.20f);

    private Label         _titleLabel = null!;
    private Label         _subLabel   = null!;
    private VBoxContainer _content    = null!;
    private AnimatedButton _closeBtn  = null!;

    public override void _Ready()
    {
        // Anchored top-right, same band as SmurfCardPanel. GameController
        // hides the smurf card on Open and vice versa so the two never
        // share screen space.
        AnchorLeft = 1f; AnchorTop = 1f; AnchorRight = 1f; AnchorBottom = 1f;
        OffsetLeft     = -320f;
        OffsetRight    = -UITheme.EdgeInset;
        OffsetBottom   = -240f;
        OffsetTop      = OffsetBottom - 320f;
        GrowHorizontal = GrowDirection.Begin;

        var bg = new PanelContainer();
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        bg.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        AddChild(bg);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   6);
        margin.AddThemeConstantOverride("margin_right",  6);
        margin.AddThemeConstantOverride("margin_top",    6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        bg.AddChild(margin);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 4);
        margin.AddChild(outer);

        // Header — title + close button
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        outer.AddChild(header);

        header.AddChild(MakeLabel("◆", 14, Gold));
        _titleLabel = MakeLabel("", 12, DarkWood);
        _titleLabel.SizeFlagsHorizontal = SizeFlags.Expand;
        header.AddChild(_titleLabel);

        _closeBtn = new AnimatedButton
        {
            Text              = "✕",
            PlayHoverSound    = false,
            Compact           = true,
            CustomMinimumSize = new Vector2(22, 22),
        };
        _closeBtn.Pressed += Close;
        header.AddChild(_closeBtn);

        _subLabel = MakeLabel("", 9, TextDim);
        _subLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        outer.AddChild(_subLabel);

        outer.AddChild(MakeRule());

        // Scrollable body — sections appear here.
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        outer.AddChild(scroll);

        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 4);
        _content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_content);

        Visible = false;
    }

    // Snapshot-and-show. Caller passes the tile coordinates + the live
    // LocalMap; this method copies what it needs into local labels then
    // shows the panel. No reference to the map is retained.
    public void Open(int tx, int ty, LocalMap map)
    {
        Visible = true;
        // Clear previous content.
        foreach (Node c in _content.GetChildren()) c.QueueFree();

        var tile  = map.Get(tx, ty);
        var veg   = map.GetVegetation(tx, ty);
        var items = map.GetItemsOnTile(tx, ty);
        var stone = map.GetTileStone(tx, ty);

        // ── Title row: lead with the most informative thing on the tile.
        if (items != null && items.Count > 0)
        {
            // Items present — title is the primary stack's display name.
            var primary = items[0];
            int totalQty = 0;
            for (int i = 0; i < items.Count; i++) totalQty += items[i].Quantity;
            _titleLabel.Text = FormatItemTitle(primary, totalQty, items.Count);
        }
        else if (veg.IsPresent)
        {
            _titleLabel.Text = FormatVegName(veg.Type);
        }
        else
        {
            // Pure terrain inspector — still useful for boulder material
            // / fertility / passability detail.
            _titleLabel.Text = FormatTileTitle(tile, stone);
        }

        _subLabel.Text = $"Tile ({tx}, {ty})";

        // ── Sections, in order: Items → Vegetation → Tile. Each section
        // is skipped when its data isn't present, so a vegetation-only
        // tile gets one clean section and no empty headers.
        if (items != null && items.Count > 0)
        {
            BuildItemsSection(items);
            _content.AddChild(MakeRule());
        }
        if (veg.IsPresent)
        {
            BuildVegetationSection(veg);
            _content.AddChild(MakeRule());
        }
        BuildTileSection(tile, stone);
    }

    public void Close()
    {
        Visible = false;
        foreach (Node c in _content.GetChildren()) c.QueueFree();
    }

    // ── Section builders ────────────────────────────────────────────────────

    private void BuildItemsSection(IReadOnlyList<Item> items)
    {
        _content.AddChild(MakeHeader("Items"));
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            BuildItemEntry(it);
        }
    }

    private void BuildItemEntry(Item it)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 1);
        _content.AddChild(box);

        // Corpse items get a dedicated obituary block — the v0.4.33 hover
        // line restated in unit-card form so the player gets a proper
        // record without hovering.
        if (it.Kind == ItemKind.Corpse && it.CorpseInfo != null)
        {
            BuildCorpseEntry(box, it);
            return;
        }

        var def     = ItemRegistry.Get(it.Kind, it.SubType);
        string disp = def?.DisplayName ?? it.SubType;
        string qty  = it.Quantity > 1 ? $"  ×{it.Quantity}" : "";
        box.AddChild(MakeLabel($"{disp}{qty}", 11, DarkWood));

        // Material line — "Stone · Granite" / "Wood · DeadWood" / etc.
        if (!string.IsNullOrEmpty(it.Material.Family))
            box.AddChild(MakeLabel($"  Material: {it.Material.Family} · {it.Material.SubType}", 9, TextDim));

        // Equipment-tier shows Quality; stackables omit it (it's almost
        // always Normal and the per-item field is meaningless once stacks
        // average it out).
        if (!ItemKindMeta.IsStackable(it.Kind))
            box.AddChild(MakeLabel($"  Quality: {it.Quality}", 9, TextDim));

        // Condition — always shown (food shows it implicitly via State).
        box.AddChild(MakeLabel($"  Condition: {it.AvgCondition:F0} / {it.DurabilityCap:F0}  ({it.State})", 9, TextDim));
    }

    private void BuildCorpseEntry(VBoxContainer box, Item it)
    {
        var c = it.CorpseInfo!;
        string stage = c.AgeYears switch
        {
            < 20  => "Sprout",
            < 50  => "Juvenile",
            < 400 => "Adult",
            < 545 => "Elder",
            _     => "LastSeason",
        };
        string stateLabel = it.State switch
        {
            ItemState.Fresh   => "Fresh",
            ItemState.Stale   => "Stale",
            ItemState.Spoiled => "Decayed",
            _                 => "—",
        };
        box.AddChild(MakeLabel($"💀  {c.Name}", 11, DarkWood));
        box.AddChild(MakeLabel($"  {stage} · {c.Sex} · {c.Role}", 9, TextDim));
        box.AddChild(MakeLabel($"  Cause of death: {c.Cause}", 9, NegCol));
        box.AddChild(MakeLabel($"  Body state: {stateLabel} ({it.AvgCondition:F0}/100)", 9, TextDim));
        if (c.Personality != null && c.Personality.Count > 0)
        {
            var sb = new StringBuilder("  Personality: ");
            for (int i = 0; i < c.Personality.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(c.Personality[i]);
            }
            var lbl = MakeLabel(sb.ToString(), 9, TextDim);
            lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            box.AddChild(lbl);
        }
        box.AddChild(MakeLabel($"  Handedness: {c.Handedness}", 9, TextDim));
    }

    private void BuildVegetationSection(VegetationSlot veg)
    {
        _content.AddChild(MakeHeader("Vegetation"));
        _content.AddChild(MakeLabel($"  Type: {FormatVegName(veg.Type)}", 10, DarkWood));
        _content.AddChild(MakeLabel($"  Health: {veg.Health} / 100", 9, TextDim));
        if (VegetationSlot.BaseYield(veg.Type) > 0)
        {
            _content.AddChild(MakeLabel(
                $"  Yield remaining: {veg.YieldRemaining} / {VegetationSlot.BaseYield(veg.Type)}",
                9, veg.IsDepleted ? NegCol : TextDim));
        }
        if (veg.IsDepleted)
        {
            int days = VegetationSlot.RegrowthDays(veg.Type);
            _content.AddChild(MakeLabel(
                $"  Regrowth: ~{days}d ({veg.RegrowthTimer} sim days left)",
                9, TextDim));
        }
        _content.AddChild(MakeLabel($"  {VegYieldLine(veg.Type)}", 9, TextDim));
    }

    private void BuildTileSection(LocalTile tile, MaterialKey? stone)
    {
        _content.AddChild(MakeHeader("Tile"));
        _content.AddChild(MakeLabel($"  Terrain: {FormatTerrain(tile.Terrain, stone)}", 10, DarkWood));
        _content.AddChild(MakeLabel($"  Passable: {(tile.Passable ? "Yes" : "No")}", 9,
            tile.Passable ? PosCol : NegCol));
        _content.AddChild(MakeLabel($"  Fertility: {tile.Fertility:P0}", 9, TextDim));
        if (stone.HasValue)
            _content.AddChild(MakeLabel($"  Stone: {stone.Value.Family} · {stone.Value.SubType}", 9, TextDim));
    }

    // ── Format helpers ──────────────────────────────────────────────────────

    private static string FormatItemTitle(Item primary, int totalQty, int stackCount)
    {
        if (primary.Kind == ItemKind.Corpse && primary.CorpseInfo != null)
            return $"Corpse of {primary.CorpseInfo.Name}";
        var def = ItemRegistry.Get(primary.Kind, primary.SubType);
        string disp = def?.DisplayName ?? primary.SubType;
        string qty  = totalQty > 1 ? $"  ×{totalQty}" : "";
        string sc   = stackCount > 1 ? $"  ({stackCount} stacks)" : "";
        return $"{disp}{qty}{sc}";
    }

    private static string FormatTileTitle(LocalTile tile, MaterialKey? stone) =>
        FormatTerrain(tile.Terrain, stone);

    private static string FormatTerrain(TerrainType t, MaterialKey? stone) => t switch
    {
        TerrainType.Water       => "Water",
        TerrainType.Mud         => "Mud",
        TerrainType.Sand        => "Sand",
        TerrainType.Grass       => "Grass",
        TerrainType.ForestFloor => "Forest Floor",
        TerrainType.Boulder     => stone.HasValue
            ? $"{stone.Value.SubType} Boulder"
            : "Boulder",
        TerrainType.MagicGrove  => "Magic Grove",
        TerrainType.DeadLog     => "Dead Log",
        TerrainType.LivingWood  => "Living Wood",
        TerrainType.Shallows    => "Shallows",
        _                       => "Unknown",
    };

    private static string FormatVegName(VegetationType v) => v switch
    {
        VegetationType.SmurfberryBush  => "Smurfberry Bush",
        VegetationType.SmallMushroom   => "Small Mushroom",
        VegetationType.LargeMushroom   => "Large Mushroom",
        VegetationType.HerbCluster     => "Herb Cluster",
        VegetationType.MagicFlower     => "Magic Flower",
        VegetationType.SmallSandshroom => "Small Sandshroom",
        VegetationType.LargeSandshroom => "Large Sandshroom",
        VegetationType.PalmShroom      => "Palm Shroom",
        VegetationType.PineShroom      => "Pine Shroom",
        VegetationType.Underbrush      => "Underbrush",
        VegetationType.MossPatch       => "Moss Patch",
        _                              => v.ToString(),
    };

    private static string VegYieldLine(VegetationType v) => v switch
    {
        VegetationType.SmurfberryBush  => "Yields Smurfberries (food).",
        VegetationType.SmallMushroom   => "Yields Small Mushrooms (food).",
        VegetationType.LargeMushroom   => "Yields Fungal Wood + cuttings.",
        VegetationType.HerbCluster     => "Yields Herbs (food, magic).",
        VegetationType.MagicFlower     => "Yields magic essence.",
        VegetationType.SmallSandshroom => "Yields desert mushrooms.",
        VegetationType.LargeSandshroom => "Yields desert wood.",
        VegetationType.PalmShroom      => "Yields coastal wood.",
        VegetationType.PineShroom      => "Yields hardier mushrooms.",
        VegetationType.Underbrush      => "Cosmetic ground cover.",
        VegetationType.MossPatch       => "Cosmetic moss; passable.",
        _                              => "",
    };

    // ── UI helpers ──────────────────────────────────────────────────────────

    private static Label MakeLabel(string text, int size, Color col)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", col);
        l.AddThemeFontSizeOverride("font_size", size);
        l.MouseFilter = MouseFilterEnum.Pass;
        return l;
    }

    private Label MakeHeader(string text)
    {
        var l = MakeLabel(text, 10, Gold);
        return l;
    }

    private static HSeparator MakeRule()
    {
        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(Gold.R, Gold.G, Gold.B, 0.35f));
        return sep;
    }
}
