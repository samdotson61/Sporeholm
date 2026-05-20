using Godot;
using System.Collections.Generic;
using System.Text;
using Sporeholm.Simulation.Items;
using Sporeholm.UI;
using Sporeholm.World;

// v0.4.34 — RimWorld-style "stationary inspector". Left-click an empty
// tile (no shroomp, no active designation tool) that has items or
// vegetation on it and this card opens with the relevant properties
// laid out in friendly fields. Modelled after `ShroompCardPanel`'s
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
        BuildShell();
        ApplyUniformScale();   // v0.5.46
        UITheme.UIScaleChanged += OnUIScaleChanged;   // v0.5.45
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    // v0.5.45 → v0.5.46 — UI Size change re-applies the uniform Control.Scale
    // transform. No rebuild needed; the panel + every child scales in one
    // transform pass. Pre-v0.5.46 each label / anchor / margin scaled
    // separately via UITheme.Scaled, which left FIXED bar heights and
    // padding values un-scaled — causing the squished cramped layout Sam
    // called out. The uniform transform fixes proportions regardless of
    // any individual control's hardcoded sizes.
    private void OnUIScaleChanged()
    {
        ApplyUniformScale();
    }

    // v0.5.46 — uniform scale via Godot Control.Scale. Pivots from the
    // bottom-right of the layout rect so the visible panel shrinks
    // INWARD from the anchored corner, keeping the panel docked to
    // the screen's bottom-right edge regardless of UI Size.
    private void ApplyUniformScale()
    {
        float s = UITheme.UIScale;
        Scale = new Vector2(s, s);
        // Pivot at the layout rect's bottom-right so shrinking toward
        // bottom-right keeps that corner pinned to viewport anchor.
        PivotOffset = new Vector2(Size.X, Size.Y);
    }

    private void BuildShell()
    {
        // Anchored top-right, same band as ShroompCardPanel. GameController
        // hides the shroomp card on Open and vice versa so the two never
        // share screen space. v0.5.46 — anchor offsets are unscaled
        // logical px; uniform Control.Scale handles the visual shrink.
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
        // v0.5.25 — Structure section (walls / floors / doors / shelves /
        // workbenches / hearths / blueprints). Lands above the Tile
        // section because structures sit on top of the underlying terrain
        // gameplay-wise (a Wall on Grass is "a wall" first).
        var structure = map.GetStructure(tx, ty);
        if (structure.IsPresent)
        {
            BuildStructureSection(structure);
            _content.AddChild(MakeRule());
        }
        // v0.5.84s — Phase 5.5 Bills section. Shown only for Workbench
        // tiles (the structure type that consumes recipes). Sub-section
        // is its own MakeRule-separated block so it reads as distinct
        // from the Structure section above.
        if (structure.Type == Sporeholm.World.StructureType.Workbench)
        {
            BuildBillsSection(map, tx, ty);
            _content.AddChild(MakeRule());
        }
        BuildTileSection(tile, stone);
        // v0.5.25 — Room + Temperature section (Phase 5F + 5G). Always
        // shown — outdoor tiles get the "Outdoors" line for clarity.
        _content.AddChild(MakeRule());
        BuildRoomSection(map, tx, ty);
    }

    // v0.5.25 — Structure section. Shows the structure's type, material,
    // and BuildProgress for blueprints / frames.
    // v0.5.67 — Type now reads "{Material} {Shape}" so a wall built of
    // Living Wood reads "Living Wood Wall" instead of the legacy hard-coded
    // "Stone Wall". The separate Material line is dropped since the material
    // is now part of the type name. Blueprint progress lists material name
    // + remaining count RimWorld/DF-style.
    private void BuildStructureSection(Sporeholm.World.StructureSlot s)
    {
        _content.AddChild(MakeHeader("Structure"));
        string shape = s.Type switch
        {
            Sporeholm.World.StructureType.Wall                    or
            Sporeholm.World.StructureType.WallPlanned             => "Wall",
            Sporeholm.World.StructureType.Floor                   or
            Sporeholm.World.StructureType.FloorPlanned            => "Floor",
            Sporeholm.World.StructureType.Door                    or
            Sporeholm.World.StructureType.DoorPlanned             => "Door",
            Sporeholm.World.StructureType.Shelf                   or
            Sporeholm.World.StructureType.ShelfPlanned            => "Shelf",
            Sporeholm.World.StructureType.Workbench               or
            Sporeholm.World.StructureType.WorkbenchPlanned        => "Workbench",
            Sporeholm.World.StructureType.Hearth                  or
            Sporeholm.World.StructureType.HearthPlanned           => "Hearth",
            Sporeholm.World.StructureType.Bed                     or
            Sporeholm.World.StructureType.BedPlanned              => "Bed",
            Sporeholm.World.StructureType.MeditationShrine        or
            Sporeholm.World.StructureType.MeditationShrinePlanned => "Meditation Shrine",
            Sporeholm.World.StructureType.ShroomBoard             or
            Sporeholm.World.StructureType.ShroomBoardPlanned      => "Shroom Board",
            Sporeholm.World.StructureType.GossipBench             or
            Sporeholm.World.StructureType.GossipBenchPlanned      => "Gossip Bench",
            Sporeholm.World.StructureType.Table                   or
            Sporeholm.World.StructureType.TablePlanned            => "Table",
            _                                                     => s.Type.ToString(),
        };
        // v0.5.55 — show the actual sub-material (FungalWood / DeadWood /
        // LivingWood / Stone) instead of the legacy Wood-vs-Stone collapse,
        // so the player can verify their picker choice carried through to
        // the blueprint. Uses StructureMatMeta.DisplayName.
        string matName = Sporeholm.World.StructureMatMeta.DisplayName(s.Material);
        string plannedSuffix = s.IsBlueprint ? " (planned)" : "";
        _content.AddChild(MakeLabel($"  Type: {matName} {shape}{plannedSuffix}", 10, DarkWood));
        if (s.IsBlueprint)
        {
            // v0.5.30 — BuildProgress widened to ushort; threshold is now
            // BuildProgressTarget (600 work units). v0.5.67 — RimWorld/DF
            // material-counting display: "Materials: 3 / 5 Living Wood
            // (2 more needed)". After all materials are delivered we drop
            // to a Frame % line.
            byte cost = Sporeholm.World.StructureSlot.BuildMaterialCost(s.Type);
            if (s.MaterialsDelivered < cost)
            {
                int remaining = cost - s.MaterialsDelivered;
                _content.AddChild(MakeLabel(
                    $"  Materials: {s.MaterialsDelivered} / {cost} {matName} ({remaining} more needed)",
                    10, DarkWood));
            }
            else
            {
                int pct = 100 * s.BuildProgress / Sporeholm.World.StructureSlot.BuildProgressTarget;
                _content.AddChild(MakeLabel(
                    $"  Materials: {cost} / {cost} {matName} (delivered)",
                    10, DarkWood));
                _content.AddChild(MakeLabel($"  Frame: {pct} % built", 10, DarkWood));
            }
        }
        else if (s.IsBuilt)
        {
            // v0.5.30 — show rolled Quality on built structures. Crude /
            // Normal / Fine / Superior / Masterwork. Hidden for Crude+
            // Normal at the moment to keep the panel quiet for unremarkable
            // builds — only flag the interesting ones (Fine and up + Crude
            // as a "this could be better" tell).
            if (s.Quality != Sporeholm.Simulation.Items.Quality.Normal)
            {
                string qSym = Sporeholm.Simulation.Items.QualityMeta.Symbol(s.Quality);
                _content.AddChild(MakeLabel($"  Quality: {qSym} {s.Quality}", 10, DarkWood));
            }
        }
    }

    // v0.5.84s — Phase 5.5 Bills section. Renders the workbench's bill
    // queue with a recipe-picker dropdown to add a new bill, per-bill
    // Remove button + RepeatMode cycle. The panel re-snapshots on
    // LocalMap.WorkbenchBillsChanged via the existing TilePropertiesPanel
    // refresh path (no in-place mutation — close-and-reopen invalidates
    // the current view; a future polish patch can do partial refresh).
    private void BuildBillsSection(LocalMap map, int tx, int ty)
    {
        _content.AddChild(MakeLabel("Bills", 12, DarkWood));

        var bills = map.GetWorkbenchBills(tx, ty);
        if (bills.Count == 0)
        {
            _content.AddChild(MakeLabel("  (no bills queued)", 10, DarkWood));
        }
        else
        {
            for (int i = 0; i < bills.Count; i++)
            {
                var bill = bills[i];
                var recipe = Sporeholm.Simulation.Crafting.RecipeRegistry.Get(bill.RecipeId);
                string recipeName = recipe?.DisplayName ?? bill.RecipeId;
                string modeLabel = bill.Mode switch
                {
                    Sporeholm.Simulation.Crafting.BillRepeatMode.Forever     => "∞",
                    Sporeholm.Simulation.Crafting.BillRepeatMode.RepeatCount => $"×{bill.RepeatsRemaining}",
                    Sporeholm.Simulation.Crafting.BillRepeatMode.TargetCount => $"≥{bill.TargetCount}",
                    _ => "?",
                };

                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);

                var nameLabel = new Label
                {
                    Text = $"• {recipeName} ({modeLabel})",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                nameLabel.AddThemeColorOverride("font_color", DarkWood);
                nameLabel.AddThemeFontSizeOverride("font_size", 10);
                row.AddChild(nameLabel);

                int billIdx = i;   // capture for closure
                var modeBtn = MakeMiniButton("⇄", "Cycle repeat mode (Forever ↔ Count ↔ Target)");
                modeBtn.Pressed += () =>
                {
                    bill.Mode = bill.Mode switch
                    {
                        Sporeholm.Simulation.Crafting.BillRepeatMode.Forever     => Sporeholm.Simulation.Crafting.BillRepeatMode.RepeatCount,
                        Sporeholm.Simulation.Crafting.BillRepeatMode.RepeatCount => Sporeholm.Simulation.Crafting.BillRepeatMode.TargetCount,
                        _                                                        => Sporeholm.Simulation.Crafting.BillRepeatMode.Forever,
                    };
                    if (bill.Mode == Sporeholm.Simulation.Crafting.BillRepeatMode.RepeatCount && bill.RepeatsRemaining <= 0)
                        bill.RepeatsRemaining = bill.RepeatCount > 0 ? bill.RepeatCount : 5;
                    if (bill.Mode == Sporeholm.Simulation.Crafting.BillRepeatMode.TargetCount && bill.TargetCount <= 0)
                        bill.TargetCount = 10;
                    // Re-open the panel to refresh the display.
                    Open(tx, ty, map);
                };
                row.AddChild(modeBtn);

                var rmBtn = MakeMiniButton("✕", "Remove this bill");
                rmBtn.Pressed += () =>
                {
                    map.RemoveWorkbenchBill(tx, ty, billIdx);
                    Open(tx, ty, map);
                };
                row.AddChild(rmBtn);

                _content.AddChild(row);
            }
        }

        // Add-bill picker. Iterates RecipeRegistry; clicking a recipe
        // appends a new Forever bill to the workbench's bills list.
        var addRow = new HBoxContainer();
        addRow.AddThemeConstantOverride("separation", 4);
        _content.AddChild(addRow);

        var addLabel = new Label
        {
            Text = "Add:",
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        addLabel.AddThemeColorOverride("font_color", DarkWood);
        addLabel.AddThemeFontSizeOverride("font_size", 10);
        addRow.AddChild(addLabel);

        var picker = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        picker.AddThemeFontSizeOverride("font_size", 10);
        picker.AddItem("(pick recipe)", -1);
        for (int i = 0; i < Sporeholm.Simulation.Crafting.RecipeRegistry.All.Length; i++)
        {
            var rec = Sporeholm.Simulation.Crafting.RecipeRegistry.All[i];
            picker.AddItem($"{rec.DisplayName} — {rec.PrimarySkill} ≥{rec.SkillMinimum}", i);
        }
        picker.ItemSelected += (long sel) =>
        {
            if (sel <= 0) return;
            int idx = (int)sel - 1;
            if (idx < 0 || idx >= Sporeholm.Simulation.Crafting.RecipeRegistry.All.Length) return;
            var rec = Sporeholm.Simulation.Crafting.RecipeRegistry.All[idx];
            map.AddWorkbenchBill(tx, ty, new Sporeholm.Simulation.Crafting.Bill
            {
                RecipeId         = rec.Id,
                Mode             = Sporeholm.Simulation.Crafting.BillRepeatMode.Forever,
                RepeatCount      = 5,
                TargetCount      = 10,
                RepeatsRemaining = 0,
            });
            Open(tx, ty, map);
        };
        addRow.AddChild(picker);
    }

    private Button MakeMiniButton(string text, string tooltip)
    {
        var b = new Button
        {
            Text = text,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(22, 18),
            FocusMode = FocusModeEnum.None,
        };
        b.AddThemeFontSizeOverride("font_size", 9);
        b.AddThemeColorOverride("font_color", DarkWood);
        return b;
    }

    // v0.5.25 — Room + Temperature section (Phase 5F + 5G). Triggers
    // EnsureRooms so the player gets a fresh detector pass even just
    // after walling a region in. Outdoor tiles show "Outdoors"; enclosed
    // rooms show TileCount, FurnitureCount, BeautyScore, TemperatureOffsetC.
    private void BuildRoomSection(LocalMap map, int tx, int ty)
    {
        _content.AddChild(MakeHeader("Room"));
        map.EnsureRooms();
        var slot = map.GetStructure(tx, ty);
        if (!map.IsPassable(tx, ty))
        {
            _content.AddChild(MakeLabel("  (impassable — walls aren't in any room)", 10, DarkWood));
            return;
        }
        if (slot.RoomId == 0 || slot.RoomId == Sporeholm.World.RoomDetector.OutdoorRoomId)
        {
            _content.AddChild(MakeLabel("  Outdoors", 10, DarkWood));
            _content.AddChild(MakeLabel("  Temperature: ambient (biome)", 10, DarkWood));
            return;
        }
        var room = map.GetRoom(slot.RoomId);
        if (room == null)
        {
            _content.AddChild(MakeLabel("  (unregistered room — rebuild pending)", 10, DarkWood));
            return;
        }
        // v0.5.84t — surface inferred room type (Bedroom / Kitchen / Workshop /
        // Storage / Generic) alongside id + tile count + roofed status.
        _content.AddChild(MakeLabel($"  Room #{room.Id} · {room.Type} · {room.TileCount} tiles · roofed", 10, DarkWood));
        _content.AddChild(MakeLabel(
            $"  Floor: {room.FloorCount} · Furniture: {room.FurnitureCount} · Hearths: {room.HearthCount}",
            10, DarkWood));
        string beautyTier = room.BeautyScore >= 10 ? " (Pretty +3 mood)"
                         : room.BeautyScore < -3 ? " (Ugly -3 mood)"
                         : "";
        _content.AddChild(MakeLabel(
            $"  Beauty: {room.BeautyScore:0.#}{beautyTier}", 10, DarkWood));
        _content.AddChild(MakeLabel(
            $"  Temperature offset: +{room.TemperatureOffsetC:0.#} °C above ambient",
            10, DarkWood));
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
        // v0.5.84t — surface natural roof status (cavern roofs persist
        // through mining and shelter items from weather).
        if (tile.IsRoofed)
            _content.AddChild(MakeLabel("  Roof: Cavern (natural)", 9, PosCol));
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
        TerrainType.Skeleton    => "Skeleton",
        _                       => "Unknown",
    };

    private static string FormatVegName(VegetationType v) => v switch
    {
        VegetationType.CapberryBush  => "Capberry Bush",
        VegetationType.SmallMushroom   => "Small Mushroom",
        VegetationType.LargeMushroom   => "Large Mushroom",
        VegetationType.HerbCluster     => "Herb Cluster",
        VegetationType.MagicFlower     => "Magic Flower",
        VegetationType.SmallSandshroom => "Small Sandshroom",
        VegetationType.LargeSandshroom => "Large Sandshroom",
        VegetationType.PalmShroom      => "Palm Shroom",
        VegetationType.PineShroom      => "Pine Shroom",
        VegetationType.Underbrush      => "Grass Tuft",
        VegetationType.MossPatch       => "Moss Patch",
        _                              => v.ToString(),
    };

    private static string VegYieldLine(VegetationType v) => v switch
    {
        VegetationType.CapberryBush  => "Yields Capberries (food).",
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
        // v0.5.45 → v0.5.46 — font size is now unscaled here; the parent
        // Control.Scale transform (ApplyUniformScale) shrinks/grows the
        // entire panel + every label proportionally. Pre-v0.5.46 the
        // per-label scaling clashed with hardcoded bar heights and
        // padding values, causing the squished cramped layout Sam called
        // out at small UI Size.
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
