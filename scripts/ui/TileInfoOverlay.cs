using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.UI;
using Sporeholm.World;

// Top-right tile-hover info: terrain + temperature/classification + vegetation
// for the tile currently under the cursor. Updated every frame by
// GameController. Reverted in v0.3.31 to bare drop-shadow text matching the
// message-log style — the v0.3.30 paneled version was too heavy for what is
// minor hover-only information.
public partial class TileInfoOverlay : Control
{
    private Label _label = null!;

    public override void _Ready()
    {
        // Top-right band, just beneath the HUD speed capsule. The width is
        // wide enough for the longest practical hover line; horizontal
        // alignment of the label keeps the text right-aligned to the edge.
        AnchorLeft   = 1f;
        AnchorRight  = 1f;
        AnchorTop    = 0f;
        AnchorBottom = 0f;

        OffsetLeft   = -360f;
        OffsetRight  = -UITheme.EdgeInset;
        // v0.5.81 — derive vertical clearance from the actual HUD-capsule
        // height at the current UI scale instead of a hard-coded
        // Scaled(50). The v0.4.11 constant assumed scale 1.0 dimensions
        // and overlapped the speed/menu capsule at the 33–66 % scales the
        // Settings UI Size slider can reach. Sam: "Fix UI scaling so
        // settings slider does not cause overlap in top right no matter
        // what setting it's set to."
        //
        // HUD capsule height = Scaled(ToolbarButtonSize) (scales with UI)
        //                    + 2 × ContentPadY (fixed-px stylebox margins
        //                      — FloatingPanelStyle's ContentMarginTop/Bottom
        //                      doesn't multiply through Scaled, so these
        //                      stay constant across slider settings).
        // Total top offset = EdgeInset above + capsule height + 8 px gap.
        OffsetTop    = ComputeOffsetTop();
        OffsetBottom = OffsetTop + UITheme.Scaled(55);   // room for three 13-pt lines

        MouseFilter = MouseFilterEnum.Ignore;

        BuildLabel();

        Visible = false;
        UITheme.UIScaleChanged += OnUIScaleChanged;
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    // v0.3.31 — live re-scale. Just rebuild the label so the new font size
    // and offset re-apply. Label state is rewritten by the next ShowTile.
    private void OnUIScaleChanged()
    {
        OffsetTop    = ComputeOffsetTop();           // v0.5.81 — derived from current HUD-capsule height
        OffsetBottom = OffsetTop + UITheme.Scaled(55);
        foreach (Node c in GetChildren()) c.QueueFree();
        BuildLabel();
        Visible = false;
    }

    // v0.5.81 — single source for the "below the HUD speed capsule"
    // vertical placement. EdgeInset above the band + Scaled toolbar
    // button height + the FloatingPanelStyle's fixed top+bottom content
    // margins + an 8 px breathing gap. Stays clear of the HUD capsule
    // at every UI-scale slider position (33 → 100 %).
    private static float ComputeOffsetTop() =>
        UITheme.EdgeInset
        + UITheme.Scaled(UITheme.ToolbarButtonSize)
        + 2 * UITheme.ContentPadY
        + 8;

    private void BuildLabel()
    {
        _label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            AutowrapMode        = TextServer.AutowrapMode.Off,
            MouseFilter         = MouseFilterEnum.Ignore,
        };
        _label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _label.AddThemeColorOverride("font_color",        new Color(1f, 1f, 1f, 0.92f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.75f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        _label.AddThemeFontSizeOverride("font_size", UITheme.Scaled(13));
        AddChild(_label);
    }

    // Called by GameController each frame with the tile and vegetation under the cursor.
    // v0.4.11 — `stone` carries the per-Boulder stone subtype so the hover
    // can show "Granite · Yields Granite Stone" instead of the generic
    // "Boulder · Yields Stone". Null for non-Boulder tiles (or Boulder
    // tiles on legacy saves that pre-date the stone-variation system).
    // v0.4.23 — write-elide. `GameController.UpdateTileInfo` calls ShowTile
    // every frame from `_Process` (mouse-hover tooltip). The previous
    // path re-formatted the string and assigned `_label.Text = …` even
    // when the player wasn't moving the mouse — at 60 FPS that's 60
    // label-text writes per second plus the corresponding canvas redraws
    // of the tooltip for no visual change.
    public void ShowTile(LocalTile tile, VegetationSlot veg, BiomeType worldBiome,
        MaterialKey? stone = null,
        System.Collections.Generic.IReadOnlyList<Item>? droppedItems = null)
    {
        string txt = Format(tile, veg, worldBiome, stone, droppedItems);
        if (_label.Text != txt) _label.Text = txt;
        if (!Visible) Visible = true;
    }

    public void Clear()
    {
        if (Visible) Visible = false;
    }

    // ── Format ─────────────────────────────────────────────────────────────────

    private static string Format(LocalTile tile, VegetationSlot veg, BiomeType worldBiome,
        MaterialKey? stone,
        System.Collections.Generic.IReadOnlyList<Item>? droppedItems)
    {
        string terrain = TerrainName(tile.Terrain, stone);
        string detail  = TerrainDetail(tile, stone);
        string line1   = detail.Length > 0 ? $"{terrain}  ·  {detail}" : terrain;

        // Roadmap §3.x.5: surface tile temperature + Indoor/Outdoor classification.
        // Temperature is a biome-baseline placeholder until Phase 5.x adds the
        // real LocalTile.Temperature field; layout is set up now so it doesn't
        // shift when the real value lands.
        int placeholderTemp = BiomeBaselineTemperature(worldBiome);
        string classification = ClassifyTile(tile);
        string line2 = $"{placeholderTemp,3} °C  ·  {classification}";

        // v0.4.30 — items on the hovered tile. RimWorld/DF show the full
        // item label on hover; we list each stack as "{display} ({material}) ×{qty}"
        // with one line per stack. Equipment-tier items also include the
        // Quality so the player can spot a Masterwork sword in the pile.
        // v0.4.33 — Corpse items get a dedicated multi-field obituary
        // line ("Sloppy — Adult Male Forager, died of Starvation; body
        // Stale") so the player gets a proper RimWorld-style death
        // record by hovering the body.
        // Empty list / null skips the items section.
        string itemsBlock = "";
        if (droppedItems != null && droppedItems.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < droppedItems.Count; i++)
            {
                var it = droppedItems[i];
                sb.Append('\n');
                // v0.5.0 (Phase 5A) — forbid prefix tag.
                if (it.IsForbidden) sb.Append("[forbidden] ");
                if (it.Kind == ItemKind.Corpse && it.CorpseInfo != null)
                {
                    sb.Append(FormatCorpseLine(it));
                }
                else
                {
                    var def = ItemRegistry.Get(it.Kind, it.SubType);
                    string display = def?.DisplayName ?? it.SubType;
                    string mat = it.Material.Family;
                    string qty = it.Quantity > 1 ? $" ×{it.Quantity}" : "";
                    string qual = ItemKindMeta.IsStackable(it.Kind)
                        ? ""
                        : $" [{it.Quality}]";
                    sb.Append(display).Append(" (").Append(mat).Append(')').Append(qual).Append(qty);
                }
            }
            itemsBlock = sb.ToString();
        }

        if (!veg.IsPresent) return $"{line1}\n{line2}{itemsBlock}";

        string vegName = VegetationName(veg.Type);
        string yieldHint = VegetationYield(veg.Type);
        string depleted  = veg.IsDepleted ? " [depleted]" : "";
        string line3 = yieldHint.Length > 0
            ? $"{vegName} ({yieldHint}){depleted}"
            : vegName;

        return $"{line1}\n{line2}\n{line3}{itemsBlock}";
    }

    // v0.4.33 — RimWorld-style corpse obituary line. Format:
    //   "{Name} — {age-stage} {sex} {role}, died of {cause}; body {state}"
    // e.g. "Sloppy — Adult Male Forager, died of Starvation; body Stale"
    // Falls back gracefully when older saves provide a corpse Item with
    // a missing CorpseInfo sidecar (treated as a generic "Shroomp corpse").
    private static string FormatCorpseLine(Item it)
    {
        if (it.CorpseInfo == null) return "Shroomp corpse";
        var c = it.CorpseInfo;
        string stage = c.AgeYears switch
        {
            < 20  => "Sprout",
            < 50  => "Juvenile",
            < 400 => "Adult",
            < 545 => "Elder",
            _     => "LastSeason",
        };
        string state = it.State switch
        {
            ItemState.Fresh   => "Fresh",
            ItemState.Stale   => "Stale",
            ItemState.Spoiled => "Decayed",
            _                 => "—",
        };
        return $"{c.Name} — {stage} {c.Sex} {c.Role}, died of {c.Cause}; body {state}";
    }

    // Phase-5.x placeholder. Until LocalTile.Temperature lands, derive a
    // reasonable per-biome baseline so the tooltip line isn't a dead zero.
    private static int BiomeBaselineTemperature(BiomeType b) => b switch
    {
        BiomeType.Desert     => 32,
        BiomeType.Plains     => 18,
        BiomeType.Forest     => 16,
        BiomeType.Hills      => 14,
        BiomeType.Mountains  => 8,
        BiomeType.Peaks      => -4,
        BiomeType.Swamp      => 20,
        BiomeType.Coast      => 16,
        BiomeType.Island     => 22,
        BiomeType.MagicGrove => 17,
        _                    => 15,
    };

    private static string ClassifyTile(LocalTile tile) => tile.Terrain switch
    {
        TerrainType.Boulder    => "Sheltered (rock)",
        TerrainType.DeadLog    => "Sheltered (timber)",
        TerrainType.LivingWood => "Sheltered (timber)",
        TerrainType.Skeleton   => "Sheltered (bone)",
        TerrainType.Water      => "Surface water",
        TerrainType.Shallows   => "Wadeable water",
        _                      => "Outdoors",
    };

    // v0.4.11 — Boulder tiles report their specific stone subtype
    // (Granite / Limestone / Marble / Obsidian / Quartz / Magicstone /
    // MagicCrystal) instead of the generic "Boulder". Other terrain
    // types ignore the stone param.
    private static string TerrainName(TerrainType t, MaterialKey? stone) => t switch
    {
        TerrainType.Water       => "Water",
        TerrainType.Mud         => "Mud",
        TerrainType.Sand        => "Sand",
        TerrainType.Grass       => "Grass",
        TerrainType.ForestFloor => "Forest Floor",
        TerrainType.Boulder     => StoneDisplayName(stone),
        TerrainType.MagicGrove  => "Magic Grove",
        TerrainType.DeadLog     => "Dead Log",
        TerrainType.LivingWood  => "Living Wood",
        TerrainType.Shallows    => "Shallows",
        TerrainType.Skeleton    => "Skeleton",
        _                       => "Unknown",
    };

    private static string StoneDisplayName(MaterialKey? stone)
    {
        if (stone == null) return "Boulder";
        var def = MaterialRegistry.Get(stone.Value);
        if (def != null) return def.DisplayName;
        return stone.Value.SubType;
    }

    // v0.4.11 — Boulder yield line lists the actual drops. MagicCrystal
    // tiles drop a Stone Block (Magic Crystal) PLUS Crystal Shards
    // (1-3 per excavation, per BehaviorSystem.cs). Other Boulder
    // subtypes drop a single Stone Block of their family.
    private static string TerrainDetail(LocalTile tile, MaterialKey? stone) => tile.Terrain switch
    {
        TerrainType.Water       => "Impassable",
        TerrainType.Boulder     => "Impassable  ·  " + StoneYieldText(stone),
        TerrainType.DeadLog     => "Impassable  ·  Yields Dead Wood",
        TerrainType.LivingWood  => "Impassable  ·  Yields Living Wood",
        TerrainType.Sand        => $"Dry  ·  Fertility {tile.Fertility:P0}",
        TerrainType.Mud         => $"Boggy  ·  Fertility {tile.Fertility:P0}",
        TerrainType.Grass       => $"Fertility {tile.Fertility:P0}",
        TerrainType.ForestFloor => $"Fertility {tile.Fertility:P0}",
        TerrainType.MagicGrove  => "✦ Resonant",
        TerrainType.Shallows    => "Wadeable  ·  0.30× movement",
        TerrainType.Skeleton    => "Impassable  ·  Yields Bone (×3)",
        _                       => "",
    };

    private static string StoneYieldText(MaterialKey? stone)
    {
        if (stone == null) return "Yields Stone";
        string mat = StoneDisplayName(stone);
        if (stone.Value.SubType == "MagicCrystal")
            return $"Yields {mat} Block + Crystal Shards";
        return $"Yields {mat} Block";
    }

    private static string VegetationName(VegetationType v) => v switch
    {
        VegetationType.Underbrush      => "Grass Tuft",
        VegetationType.CapberryBush  => "Capberry Bush",
        VegetationType.SmallMushroom   => "Small Mushroom",
        VegetationType.LargeMushroom   => "Large Mushroom",
        VegetationType.HerbCluster     => "Herb Cluster",
        VegetationType.MagicFlower     => "Magic Flower",
        VegetationType.MossPatch       => "Moss Patch",
        VegetationType.SmallSandshroom => "Small Sandshroom",
        VegetationType.LargeSandshroom => "Large Sandshroom",
        VegetationType.PalmShroom      => "Palm Shroom",
        VegetationType.PineShroom      => "Pine Shroom",
        _                              => "",
    };

    private static string VegetationYield(VegetationType v) => v switch
    {
        VegetationType.CapberryBush  => "Food",
        VegetationType.SmallMushroom   => "Food",
        VegetationType.LargeMushroom   => "Fungal Wood",
        VegetationType.HerbCluster     => "Food + Magic Essence",
        VegetationType.MagicFlower     => "Magic Essence",
        VegetationType.SmallSandshroom => "Food",
        VegetationType.LargeSandshroom => "Fungal Wood",
        VegetationType.PalmShroom      => "Fungal Wood",
        VegetationType.PineShroom      => "Food",
        _                              => "",
    };
}
