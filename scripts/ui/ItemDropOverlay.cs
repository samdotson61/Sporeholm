using Godot;
using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

// v0.4.2 — renders items that have been dropped on the map by gather /
// excavate / chop / cut tasks. Each visible item shows a small icon at
// its tile centre; multi-unit stacks show a count to the right, and
// multi-stack tiles get a small yellow pile-badge.
//
// v0.4.25 — `MultiMeshInstance2D`-based rendering for the icon pass.
// The pre-v0.4.25 path emitted ~4 procedural canvas commands per
// dropped tile (DrawCircle + DrawArc + optional DrawRect or
// DrawColoredPolygon for the kind shape) plus a many-DrawRect-per-digit
// stack-count number. At 200+ stone piles on a busy dig site that was
// ~1 000+ canvas commands per redraw. Every `ItemsChanged` event
// triggered another redraw — the renderer churned even when paused.
//
// Now: one pre-baked 16×16 RGBA sprite per item-variant (food sub-type,
// material family × sub-type, magic sub-type — 23 total), one
// `MultiMeshInstance2D` child per variant, and per-item transforms
// pushed into the multimesh buffer when the dropped-items set changes.
// The icon pass becomes exactly 23 instanced GPU draw calls regardless
// of how many items are on the ground.
//
// Stack-count numbers + multi-stack pile-badges remain procedural for
// now: they're sparse (most piles are single-item) and depend on the
// live count value, which would require a baked-digit cache to remove
// the per-pixel DrawRect work. Held back as a follow-up; current
// frame-time wins come from the icon pass.
public partial class ItemDropOverlay : Node2D
{
    private LocalMap? _map;

    // Cached summary snapshot. Refreshed on `ItemsChanged`; consumed by
    // both `RebuildInstances` (icon MMI) and `_Draw` (stack numbers +
    // pile badges) so the renderer walks the dropped-items dictionary
    // exactly once per change.
    private System.Collections.Generic.List<LocalMap.DroppedTileSummary> _summaries
        = new();

    // v0.4.56 — per-tile dirty tracking + 200 ms refresh throttle. The
    // previous `volatile bool _dirty` flag flipped to true on every
    // `ItemsChanged` event and triggered a full SnapshotDroppedItemSummaries
    // + RebuildInstances + QueueRedraw on the next `_Process` — at 60 Hz
    // with 50 smurfs continuously dropping/picking-up items on a heavy
    // dig site, this saturated the main thread (DrawAllBadges costs ~3-6 ms
    // per redraw at 300 dropped tiles; 60 Hz × 3-6 ms = 180-360 ms/sec
    // burned on badges alone).
    //
    // Replaced by a HashSet<(int,int)> that records exactly which tiles
    // changed since the last refresh, gated by a 200 ms minimum interval.
    // Sam: terrain / item updates can lag up to 200 ms without the player
    // noticing (designations stay instant — separate overlay), and the sim
    // thread reads tile state directly from LocalMap so smurfs re-evaluate
    // paths/tasks the moment the data changes, independent of render
    // cadence. The dirty set also lets us skip rebuilds when an event
    // fires without an actual tile change (rare, but free correctness).
    private readonly System.Collections.Generic.HashSet<(int X, int Y)> _dirtyTiles = new();
    private readonly object _dirtyLock = new();
    private const double MinRefreshIntervalSec = 0.20;
    private double _timeSinceRefresh = 1.0;   // start "ready" so the first dirty event refreshes immediately

    private const int TS         = LocalMap.TileSize;
    private const int MaxInstancesPerVariant = 5000;
    // VariantCount is declared near VariantIndexFor (the canonical source —
    // bumped to 28 in v0.4.30 when equipment-tier variants were added).

    private MultiMeshInstance2D[] _mmi = System.Array.Empty<MultiMeshInstance2D>();
    private int[]                 _counts = System.Array.Empty<int>();

    // v0.4.28b — count-badge canvas. Children render after the parent's
    // _Draw, so without a separate node the icon MMI children would
    // render ON TOP of any DrawString called from this Node2D's _Draw —
    // exactly what made the badge digits unreadable in the screenshot.
    // Added as the LAST child so its _Draw fires after every icon MMI.
    private NumberBadgeNode? _badgeNode;

    private partial class NumberBadgeNode : Node2D
    {
        // v0.4.42 — `new` to acknowledge the Node.Owner hide. We're using
        // this field as our own typed back-reference to the parent overlay,
        // not the Godot editor-owner concept. CS0108 silenced.
        public new ItemDropOverlay Owner = null!;
        public override void _Draw() => Owner?.DrawAllBadges();
    }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        // Above the designation overlay (z=0) but below the smurf colony
        // (z=1) so a smurf walking over an item visually obscures it.
        ZIndex = 0;

        var quad = new QuadMesh { Size = new Vector2(TS, TS) };
        _mmi    = new MultiMeshInstance2D[VariantCount];
        _counts = new int[VariantCount];
        for (int v = 0; v < VariantCount; v++)
        {
            var tex = BakeVariantSprite(v);
            _mmi[v] = CreateMmi(quad, tex);
        }

        // Add LAST so its _Draw fires after the icon MMIs above.
        _badgeNode = new NumberBadgeNode { Owner = this };
        AddChild(_badgeNode);
    }

    public void SetMap(LocalMap map)
    {
        if (_map != null) _map.ItemsChanged -= OnItemsChanged;
        _map = map;
        _map.ItemsChanged += OnItemsChanged;
        // v0.4.56 — on first map bind, force an immediate refresh on
        // the next _Process by clearing the timer and dropping a sentinel
        // into the dirty set. The sentinel uses (-1,-1) which is filtered
        // by the `InBounds` guard inside the rebuild path (it never
        // matches a real tile).
        lock (_dirtyLock) _dirtyTiles.Add((-1, -1));
        _timeSinceRefresh = MinRefreshIntervalSec;
    }

    public override void _ExitTree()
    {
        if (_map != null) _map.ItemsChanged -= OnItemsChanged;
    }

    private void OnItemsChanged(int x, int y)
    {
        lock (_dirtyLock) _dirtyTiles.Add((x, y));
    }

    public override void _Process(double delta)
    {
        // v0.4.56 — accumulate the per-frame delta regardless of dirty
        // state so a dormant dirty set that wakes up after >200 ms can
        // refresh on the very next frame instead of waiting another
        // throttle window.
        _timeSinceRefresh += delta;
        if (_map == null || _mmi.Length == 0) return;
        bool hasDirty;
        lock (_dirtyLock) hasDirty = _dirtyTiles.Count > 0;
        if (!hasDirty) return;
        if (_timeSinceRefresh < MinRefreshIntervalSec) return;

        // Snapshot + clear the dirty set under the lock so events that
        // arrive during the rebuild are caught by the next cycle.
        lock (_dirtyLock) _dirtyTiles.Clear();
        _timeSinceRefresh = 0;

        _summaries = _map.SnapshotDroppedItemSummaries();
        RebuildInstances();
        _badgeNode?.QueueRedraw();   // stack numbers + pile badges
    }

    private void RebuildInstances()
    {
        for (int i = 0; i < VariantCount; i++) _counts[i] = 0;

        int n = _summaries.Count;
        for (int i = 0; i < n; i++)
        {
            var s = _summaries[i];
            int v = VariantIndexFor(s.Primary);
            if ((uint)v >= (uint)VariantCount) v = VariantCount - 1;
            int idx = _counts[v];
            if (idx >= MaxInstancesPerVariant) continue;

            // QuadMesh of size (TS, TS) is centred at its local origin, so
            // translating by tile-centre lands the quad over exactly the
            // (X, Y) tile.
            var origin = new Vector2(s.X * TS + TS * 0.5f, s.Y * TS + TS * 0.5f);
            _mmi[v].Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
            _counts[v] = idx + 1;
        }

        for (int i = 0; i < VariantCount; i++)
            _mmi[i].Multimesh.VisibleInstanceCount = _counts[i];
    }

    // v0.4.28b — invoked by NumberBadgeNode._Draw so the badges render
    // AFTER the icon MMI children (children render after parent _Draw,
    // and the badge node is the last child — see _Ready). Without this
    // split the icon MultiMeshes drew on top of the digits.
    internal void DrawAllBadges()
    {
        if (_map == null || _badgeNode == null) return;
        int n = _summaries.Count;
        for (int i = 0; i < n; i++)
        {
            var s = _summaries[i];
            float cx = s.X * TS + TS * 0.5f;
            float cy = s.Y * TS + TS * 0.5f;

            // v0.4.46 — count badge anchored at upper-right tile corner via
            // DrawSmallNumberTopRight. Old version centred horizontally
            // on the icon body, which drowned the sprite under the badge
            // background. The right-anchored variant keeps the badge
            // tight to the corner regardless of digit count.
            if (s.TotalCount > 1)
                DrawSmallNumberTopRight(_badgeNode, s.TotalCount,
                    cx + TS * 0.5f, cy - TS * 0.5f);

            // Multi-stack pile-badge: tiny yellow square at the bottom-LEFT
            // (was top-right; moved v0.4.46 to free the upper-right for the
            // count badge).
            if (s.StackCount > 1)
                _badgeNode.DrawRect(new Rect2(cx - TS * 0.5f + 1f, cy + TS * 0.5f - 4f, 3f, 3f),
                    new Color(1.0f, 0.95f, 0.4f, 1f));

            // v0.4.30 — equipment-tier ground label. RimWorld/DF show a
            // shorthand (e.g. "good ddwd. swrd.") under each dropped tool
            // / weapon so the player can spot what's on the ground at a
            // glance. Stackable-tier items (food / material / magic) skip
            // this — the variant icon already conveys the type and the
            // count badge handles quantity.
            if (IsEquipmentKind(s.Primary.Kind))
                DrawShorthandLabelOn(_badgeNode, s.Primary, cx, cy + TS * 0.5f + 7f);
            // v0.4.33 — corpse-tier label: use the dead smurf's name + a
            // state suffix (Fresh / Stale / Spoiled) so the player can
            // tell rotted bodies from fresh ones at a glance.
            else if (s.Primary.Kind == ItemKind.Corpse)
                DrawCorpseLabelOn(_badgeNode, s.Primary, cx, cy + TS * 0.5f + 7f);
        }
    }

    private static bool IsEquipmentKind(ItemKind k) =>
        k == ItemKind.Tool || k == ItemKind.Weapon || k == ItemKind.Apparel
        || k == ItemKind.Furniture || k == ItemKind.Trinket;

    // v0.4.33 — corpse label drawn under the body sprite. Format:
    // "{Name} ({state-short})" so a fresh body says "Sloppy (fr.)" and
    // a fully-rotted one "Sloppy (sp.)". Falls back to "corpse" when the
    // CorpseInfo sidecar is missing (older saves loaded onto a fresh
    // model — defensive). Colour matches the equipment-label parchment.
    private static void DrawCorpseLabelOn(Node2D canvas, Item item, float cx, float y)
    {
        var font = ThemeDB.FallbackFont;
        const int fontSize = 7;
        string name = item.CorpseInfo?.Name ?? "corpse";
        string state = item.State switch
        {
            ItemState.Fresh   => "fr.",
            ItemState.Stale   => "st.",
            ItemState.Spoiled => "sp.",
            _                 => "",
        };
        string label = state.Length > 0 ? $"{name} ({state})" : name;
        var ts = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
        canvas.DrawRect(new Rect2(
            cx - ts.X * 0.5f - 1f, y - ts.Y + 1f, ts.X + 2f, ts.Y + 1f),
            new Color(0f, 0f, 0f, 0.70f));
        canvas.DrawString(font, new Vector2(cx - ts.X * 0.5f, y), label,
            HorizontalAlignment.Left, -1, fontSize,
            new Color(0.85f, 0.55f, 0.55f, 1f));
    }

    // v0.4.30 — shorthand format Sam asked for: "{quality} {mat-short} {sub-short}"
    // e.g. Quality.Fine + Material.SubType "DeadWood" + Item.SubType "Sword"
    // → "good ddwd. swrd."
    private static string ShorthandLabel(Item it)
    {
        string q   = QualityPrefix(it.Quality);
        string mat = ShortenIdent(it.Material.SubType);
        string sub = ShortenIdent(it.SubType);
        return $"{q}{mat} {sub}";
    }

    private static string QualityPrefix(Quality q) => q switch
    {
        Quality.Crude       => "crude ",
        Quality.Fine        => "good ",
        Quality.Superior    => "great ",
        Quality.Masterwork  => "mw ",
        Quality.Legendary   => "leg ",
        _                   => "",   // Normal = no prefix
    };

    // Consonant compression: keep first char, then up to 3 consonants,
    // lowercase, suffix with a period. "DeadWood" → "ddwd.",
    // "LivingWood" → "lvwd.", "Sword" → "swrd.", "Hammer" → "hmmr.".
    private static string ShortenIdent(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(5);
        sb.Append(char.ToLower(s[0]));
        for (int i = 1; i < s.Length && sb.Length < 4; i++)
        {
            char c = s[i];
            if ("aeiouAEIOU".IndexOf(c) >= 0) continue;
            sb.Append(char.ToLower(c));
        }
        sb.Append('.');
        return sb.ToString();
    }

    // Centre the label horizontally on cx with its baseline at y.
    private static void DrawShorthandLabelOn(Node2D canvas, Item item, float cx, float y)
    {
        var font = ThemeDB.FallbackFont;
        const int fontSize = 7;
        string label = ShorthandLabel(item);
        var ts = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
        // Tight black background for legibility on grass / mud.
        canvas.DrawRect(new Rect2(
            cx - ts.X * 0.5f - 1f, y - ts.Y + 1f, ts.X + 2f, ts.Y + 1f),
            new Color(0f, 0f, 0f, 0.65f));
        canvas.DrawString(font, new Vector2(cx - ts.X * 0.5f, y), label,
            HorizontalAlignment.Left, -1, fontSize,
            new Color(0.95f, 0.92f, 0.72f, 1f));
    }

    private MultiMeshInstance2D CreateMmi(Mesh mesh, Texture2D tex)
    {
        var mm = new MultiMesh
        {
            Mesh                 = mesh,
            TransformFormat      = MultiMesh.TransformFormatEnum.Transform2D,
            InstanceCount        = MaxInstancesPerVariant,
            VisibleInstanceCount = 0,
        };
        var mmi = new MultiMeshInstance2D
        {
            Multimesh     = mm,
            Texture       = tex,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        AddChild(mmi);
        return mmi;
    }

    // ── Variant resolution + bake ──────────────────────────────────────────

    // 28 distinct visual variants. Indices match `VariantTable` below.
    //   [0..4]   Food: Smurfberry / SmallMushroom / HerbCluster / MagicBerry / default
    //   [5..8]   Material/Wood: DeadWood / LivingWood / Fungal / default
    //   [9..16]  Material/Stone: Granite / Limestone / Marble / Obsidian /
    //                              Quartz / Magicstone / MagicCrystal / default
    //   [17]     Material/Plant
    //   [18]     Material default
    //   [19..21] Magic: RawEssence / CrystalShard / default
    //   [22]     Fallback
    //   [23..27] v0.4.30 — equipment-tier ground icons:
    //              23 Tool / 24 Weapon / 25 Apparel / 26 Furniture / 27 Trinket
    //   [28]     v0.4.33 — Corpse (smurf body); shorthand label uses the
    //              CorpseInfo.Name rather than the generic mat+sub form.
    private const int VariantCount = 29;
    private static int VariantIndexFor(Item item)
    {
        if (item.Kind == ItemKind.Food)
        {
            return item.SubType switch
            {
                "Smurfberry"    => 0,
                "SmallMushroom" => 1,
                "HerbCluster"   => 2,
                "MagicBerry"    => 3,
                _               => 4,
            };
        }
        if (item.Kind == ItemKind.Material)
        {
            return item.Material.Family switch
            {
                "Wood" => item.Material.SubType switch
                {
                    "DeadWood"   => 5,
                    "LivingWood" => 6,
                    "Fungal"     => 7,
                    _            => 8,
                },
                "Stone" => item.Material.SubType switch
                {
                    "Granite"      => 9,
                    "Limestone"    => 10,
                    "Marble"       => 11,
                    "Obsidian"     => 12,
                    "Quartz"       => 13,
                    "Magicstone"   => 14,
                    "MagicCrystal" => 15,
                    _              => 16,
                },
                "Plant" => 17,
                _       => 18,
            };
        }
        if (item.Kind == ItemKind.Magic)
        {
            return item.SubType switch
            {
                "RawEssence"   => 19,
                "CrystalShard" => 20,
                _              => 21,
            };
        }
        // v0.4.30 — equipment-tier kinds. One variant per Kind (rather
        // than per SubType) keeps the variant count bounded; the
        // shorthand label rendered under the icon disambiguates Sword
        // vs Pick vs Helmet at a glance.
        if (item.Kind == ItemKind.Tool)      return 23;
        if (item.Kind == ItemKind.Weapon)    return 24;
        if (item.Kind == ItemKind.Apparel)   return 25;
        if (item.Kind == ItemKind.Furniture) return 26;
        if (item.Kind == ItemKind.Trinket)   return 27;
        if (item.Kind == ItemKind.Corpse)    return 28;
        return 22;
    }

    private enum IconShape { Round, Square, Diamond, Triangle }

    // (Shape, Fill, Edge) per variant. Index aligns with VariantIndexFor.
    private static readonly (IconShape Shape, Color Fill, Color Edge)[] VariantTable =
    {
        (IconShape.Round,   new(0.55f, 0.30f, 0.85f), new(0.20f, 0.10f, 0.40f)),  // Smurfberry
        (IconShape.Round,   new(0.85f, 0.45f, 0.35f), new(0.40f, 0.18f, 0.15f)),  // SmallMushroom
        (IconShape.Round,   new(0.60f, 0.85f, 0.40f), new(0.20f, 0.40f, 0.15f)),  // HerbCluster
        (IconShape.Round,   new(0.85f, 0.50f, 0.95f), new(0.40f, 0.18f, 0.50f)),  // MagicBerry
        (IconShape.Round,   new(0.85f, 0.65f, 0.30f), new(0.40f, 0.30f, 0.10f)),  // Food default
        // v0.4.36 — wood palette lightened so the yellow count-badge digits
        // read clearly on top. Old saturated browns blended into the
        // black-bg badge.
        (IconShape.Square,  new(0.78f, 0.58f, 0.36f), new(0.40f, 0.26f, 0.12f)),  // DeadWood
        (IconShape.Square,  new(0.58f, 0.75f, 0.32f), new(0.26f, 0.40f, 0.12f)),  // LivingWood
        (IconShape.Square,  new(0.92f, 0.78f, 0.55f), new(0.46f, 0.34f, 0.18f)),  // Fungal
        (IconShape.Square,  new(0.72f, 0.55f, 0.32f), new(0.36f, 0.22f, 0.10f)),  // Wood default
        (IconShape.Square,  new(0.62f, 0.62f, 0.68f), new(0.30f, 0.30f, 0.34f)),  // Granite
        (IconShape.Square,  new(0.88f, 0.82f, 0.66f), new(0.46f, 0.40f, 0.28f)),  // Limestone
        (IconShape.Square,  new(0.92f, 0.90f, 0.88f), new(0.46f, 0.44f, 0.42f)),  // Marble
        (IconShape.Square,  new(0.35f, 0.32f, 0.45f), new(0.12f, 0.10f, 0.18f)),  // Obsidian (lightened v0.4.46)
        (IconShape.Square,  new(0.86f, 0.90f, 0.96f), new(0.46f, 0.50f, 0.58f)),  // Quartz
        (IconShape.Square,  new(0.62f, 0.50f, 0.78f), new(0.28f, 0.20f, 0.42f)),  // Magicstone
        (IconShape.Square,  new(0.65f, 0.95f, 1.00f), new(0.30f, 0.45f, 0.60f)),  // MagicCrystal
        (IconShape.Square,  new(0.55f, 0.55f, 0.62f), new(0.30f, 0.30f, 0.34f)),  // Stone default
        (IconShape.Square,  new(0.42f, 0.58f, 0.30f), new(0.18f, 0.26f, 0.12f)),  // Plant (Cuttings)
        (IconShape.Square,  new(0.55f, 0.45f, 0.30f), new(0.25f, 0.20f, 0.10f)),  // Material default
        (IconShape.Diamond, new(0.55f, 0.85f, 1.00f), new(0.20f, 0.40f, 0.60f)),  // RawEssence
        (IconShape.Diamond, new(0.85f, 0.55f, 1.00f), new(0.40f, 0.20f, 0.50f)),  // CrystalShard
        (IconShape.Diamond, new(0.65f, 0.85f, 1.00f), new(0.25f, 0.40f, 0.60f)),  // Magic default
        (IconShape.Round,   new(0.80f, 0.80f, 0.80f), new(0.30f, 0.30f, 0.30f)),  // Fallback
        // v0.4.30 — equipment ground-icon variants. Triangles point up
        // for Tool / Weapon / Trinket; Apparel uses a soft square (cloth);
        // Furniture uses a chunky dark square (built object). Quality is
        // surfaced via the shorthand label drawn below the icon, not the
        // icon colour, so Masterwork still uses the same brown.
        (IconShape.Triangle, new(0.62f, 0.46f, 0.28f), new(0.28f, 0.20f, 0.10f)),  // Tool — brown
        (IconShape.Triangle, new(0.78f, 0.30f, 0.30f), new(0.36f, 0.12f, 0.12f)),  // Weapon — red
        (IconShape.Square,   new(0.45f, 0.55f, 0.78f), new(0.20f, 0.25f, 0.36f)),  // Apparel — blue cloth
        (IconShape.Square,   new(0.60f, 0.42f, 0.22f), new(0.30f, 0.20f, 0.08f)),  // Furniture — wood (lightened v0.4.46)
        (IconShape.Diamond,  new(0.95f, 0.82f, 0.30f), new(0.55f, 0.42f, 0.10f)),  // Trinket — gold
        // v0.4.33 — Corpse icon. Dull dusky-red square with near-black
        // outline; the shorthand label drawn below names the smurf so the
        // player can pick out "Sloppy" vs "Grumbly" without hovering.
        // As the body decays the State (Fresh / Stale / Spoiled) shifts
        // the hover line; the icon colour stays constant.
        (IconShape.Square,   new(0.60f, 0.22f, 0.22f), new(0.25f, 0.08f, 0.08f)),  // Corpse (lightened v0.4.46)
    };

    private static ImageTexture BakeVariantSprite(int variantIndex)
    {
        var (shape, fill, edge) = VariantTable[variantIndex];
        var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
        // Transparent background — items only paint their icon, not a
        // full-tile tint.
        int cx = TS / 2;
        int cy = TS / 2;
        // v0.4.46 — icon size bumped from r = 0.22 × TS (≈3.5 px / 7 px
        // diameter on a 16 px tile) to r = 0.40 × TS (≈6.5 px / 13 px
        // diameter). The old icons were so small they registered as
        // tiny coloured dots, and the v0.4.28b count-badge background
        // sitting on top of them drowned the colour entirely. Now the
        // icon fills most of the tile and the badge (moved to the
        // upper-right corner) sits outside the icon body. Also: shapes
        // are now the icon proper — no underlying circle for non-Round
        // variants — so Square / Diamond / Triangle items read as
        // their actual shape instead of "circle with a tiny overlay".
        float r = TS * 0.40f;

        if (shape == IconShape.Round)
        {
            FillCircleOnImage(img, cx, cy, r, fill);
            CircleOutlineOnImage(img, cx, cy, r, edge);
        }
        else if (shape == IconShape.Square)
        {
            float s = r * 1.6f;  // square side ≈ 1.6× the radius so it visually matches the Round footprint
            int x0 = (int)System.Math.Floor(cx - s * 0.5f);
            int y0 = (int)System.Math.Floor(cy - s * 0.5f);
            int sw = (int)System.Math.Round(s);
            FillRectOnImage(img, x0, y0, sw, sw, fill);
            RectOutlineOnImage(img, x0, y0, sw, sw, edge);
        }
        else if (shape == IconShape.Diamond)
        {
            FillDiamondOnImage(img, cx, cy, r, fill);
            DiamondOutlineOnImage(img, cx, cy, r, edge);
        }
        else if (shape == IconShape.Triangle)
        {
            // v0.4.30 — equipment triangle (Tool / Weapon). Apex at top,
            // base across the bottom. Filled then outlined.
            float rr = r * 1.0f;
            var tip   = new Vector2(cx,        cy - rr);
            var left  = new Vector2(cx - rr,   cy + rr * 0.6f);
            var right = new Vector2(cx + rr,   cy + rr * 0.6f);
            FillTriangleOnImage(img, tip, left, right, fill);
            // Outline: just paint the three edges via Bresenham lines.
            DrawLineOnImage(img, tip, left,  edge);
            DrawLineOnImage(img, left, right, edge);
            DrawLineOnImage(img, right, tip,  edge);
        }

        return ImageTexture.CreateFromImage(img);
    }

    // v0.4.30 — barycentric scan-fill triangle. Same approach as
    // SmurfColonyView.FillTriangleOnImage but inlined here so the
    // overlay doesn't take an outside dependency.
    private static void FillTriangleOnImage(Image img, Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        float minX = System.Math.Max(0, System.Math.Min(a.X, System.Math.Min(b.X, c.X)));
        float minY = System.Math.Max(0, System.Math.Min(a.Y, System.Math.Min(b.Y, c.Y)));
        float maxX = System.Math.Min(w - 1, System.Math.Max(a.X, System.Math.Max(b.X, c.X)));
        float maxY = System.Math.Min(h - 1, System.Math.Max(a.Y, System.Math.Max(b.Y, c.Y)));
        float denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (System.Math.Abs(denom) < 1e-5f) return;
        for (int py = (int)minY; py <= (int)maxY; py++)
        for (int px = (int)minX; px <= (int)maxX; px++)
        {
            float u = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denom;
            float v = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denom;
            float wt = 1f - u - v;
            if (u >= 0f && v >= 0f && wt >= 0f) img.SetPixel(px, py, col);
        }
    }

    // Bresenham line (cheap; runs once per equipment variant at first
    // bake, then never again — the variant texture is cached).
    private static void DrawLineOnImage(Image img, Vector2 a, Vector2 b, Color col)
    {
        int x0 = (int)System.Math.Round(a.X), y0 = (int)System.Math.Round(a.Y);
        int x1 = (int)System.Math.Round(b.X), y1 = (int)System.Math.Round(b.Y);
        int w = img.GetWidth(), h = img.GetHeight();
        int dx = System.Math.Abs(x1 - x0), dy = -System.Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h) img.SetPixel(x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ── Image painting primitives ──────────────────────────────────────────

    private static void FillCircleOnImage(Image img, int cx, int cy, float r, Color col)
    {
        int ir = (int)System.Math.Ceiling(r);
        float r2 = r * r;
        int w = img.GetWidth(), h = img.GetHeight();
        for (int dy = -ir; dy <= ir; dy++)
        {
            int py = cy + dy;
            if ((uint)py >= (uint)h) continue;
            float dy2 = dy * dy;
            if (dy2 > r2) continue;
            int halfW = (int)System.Math.Floor(System.Math.Sqrt(r2 - dy2));
            int xLo = System.Math.Max(0, cx - halfW);
            int xHi = System.Math.Min(w - 1, cx + halfW);
            for (int x = xLo; x <= xHi; x++) img.SetPixel(x, py, col);
        }
    }

    private static void CircleOutlineOnImage(Image img, int cx, int cy, float r, Color col)
    {
        int ir = (int)System.Math.Ceiling(r);
        float rOut2 = r * r;
        float rIn  = r - 1.4f;
        float rIn2 = rIn * rIn;
        int w = img.GetWidth(), h = img.GetHeight();
        for (int dy = -ir; dy <= ir; dy++)
        for (int dx = -ir; dx <= ir; dx++)
        {
            float d2 = dx * dx + dy * dy;
            if (d2 > rOut2 || d2 < rIn2) continue;
            int px = cx + dx, py = cy + dy;
            if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
            img.SetPixel(px, py, col);
        }
    }

    private static void FillRectOnImage(Image img, int x, int y, int w, int h, Color col)
    {
        int iw = img.GetWidth(), ih = img.GetHeight();
        int xLo = System.Math.Max(0, x);
        int yLo = System.Math.Max(0, y);
        int xHi = System.Math.Min(iw, x + w);
        int yHi = System.Math.Min(ih, y + h);
        for (int py = yLo; py < yHi; py++)
            for (int px = xLo; px < xHi; px++)
                img.SetPixel(px, py, col);
    }

    private static void RectOutlineOnImage(Image img, int x, int y, int w, int h, Color col)
    {
        int iw = img.GetWidth(), ih = img.GetHeight();
        for (int i = 0; i < w; i++)
        {
            if ((uint)(x + i) < (uint)iw)
            {
                if ((uint)y           < (uint)ih) img.SetPixel(x + i, y,           col);
                if ((uint)(y + h - 1) < (uint)ih) img.SetPixel(x + i, y + h - 1,   col);
            }
        }
        for (int i = 0; i < h; i++)
        {
            if ((uint)(y + i) < (uint)ih)
            {
                if ((uint)x           < (uint)iw) img.SetPixel(x,           y + i, col);
                if ((uint)(x + w - 1) < (uint)iw) img.SetPixel(x + w - 1,   y + i, col);
            }
        }
    }

    private static void FillDiamondOnImage(Image img, int cx, int cy, float r, Color col)
    {
        int ir = (int)System.Math.Ceiling(r);
        int w = img.GetWidth(), h = img.GetHeight();
        for (int dy = -ir; dy <= ir; dy++)
        {
            int py = cy + dy;
            if ((uint)py >= (uint)h) continue;
            // |dx| + |dy| <= r  ⇒  |dx| <= r - |dy|
            float halfW = r - System.Math.Abs(dy);
            if (halfW < 0) continue;
            int xLo = System.Math.Max(0, (int)System.Math.Ceiling(cx - halfW));
            int xHi = System.Math.Min(w - 1, (int)System.Math.Floor(cx + halfW));
            for (int x = xLo; x <= xHi; x++) img.SetPixel(x, py, col);
        }
    }

    private static void DiamondOutlineOnImage(Image img, int cx, int cy, float r, Color col)
    {
        // 4 Bresenham lines forming the diamond.
        int[] xs = { cx, cx + (int)r, cx, cx - (int)r };
        int[] ys = { cy - (int)r, cy, cy + (int)r, cy };
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) & 3;
            DrawLineOnImage(img, xs[i], ys[i], xs[j], ys[j], col);
        }
    }

    private static void DrawLineOnImage(Image img, int x0, int y0, int x1, int y1, Color col)
    {
        int dx = System.Math.Abs(x1 - x0), dy = -System.Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int w = img.GetWidth(), h = img.GetHeight();
        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h) img.SetPixel(x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ── Number badge ───────────────────────────────────────────────────────

    // v0.4.46 — top-right-anchored badge. The icon-overlay redesign moved
    // the count badge from "centred above the icon" to "tucked into the
    // tile's upper-right corner" so the (now-larger v0.4.46) icon sprite
    // isn't drowned by the badge rectangle. Right-anchored so digit
    // width never pushes the rect past the tile boundary.
    private static void DrawSmallNumberTopRight(Node2D canvas, int n, float rightX, float topY)
    {
        var font = ThemeDB.FallbackFont;
        const int fontSize = 8;   // one notch smaller than the v0.4.28 size 9; v0.4.46 icons need the corner space
        string s = n > 99 ? "99+" : n.ToString();
        var ts = font.GetStringSize(s, HorizontalAlignment.Left, -1, fontSize);

        float padX = 1.5f, padY = 0.5f;
        canvas.DrawRect(new Rect2(
            rightX - ts.X - padX * 2f, topY,
            ts.X + padX * 2f, ts.Y + padY),
            new Color(0f, 0f, 0f, 0.80f));
        canvas.DrawString(font, new Vector2(rightX - ts.X - padX, topY + ts.Y - 2f), s,
            HorizontalAlignment.Left, -1, fontSize,
            new Color(1.0f, 0.95f, 0.50f, 1f));
    }

    // v0.4.28 — replaced the v0.3.31 3×5 hand-drawn pixel font with a
    // ThemeDB.FallbackFont DrawString call. The hand-drawn glyphs were
    // illegible at the default zoom (Sam's report); the fallback font
    // hints cleanly at small sizes and matches the smurf-name label
    // style.
    //
    // v0.4.28b — draws onto the supplied Node2D's canvas (the badge node)
    // so the digits render after the icon MMI children. Centres the badge
    // horizontally on cx; bottom edge sits at bottomY. Godot's DrawString
    // silently ignores HorizontalAlignment.Center when width = -1, so we
    // manually offset by -textWidth/2 instead.
    private static void DrawSmallNumberOn(Node2D canvas, int n, float cx, float bottomY)
    {
        var font = ThemeDB.FallbackFont;
        const int fontSize = 9;
        string s = n > 99 ? "99+" : n.ToString();

        var ts = font.GetStringSize(s, HorizontalAlignment.Left, -1, fontSize);

        float padX = 1.5f, padY = 1f;
        canvas.DrawRect(new Rect2(
            cx - ts.X * 0.5f - padX, bottomY - ts.Y - padY * 0.5f,
            ts.X + padX * 2f, ts.Y + padY),
            new Color(0f, 0f, 0f, 0.75f));

        canvas.DrawString(font, new Vector2(cx - ts.X * 0.5f, bottomY - 1f), s,
            HorizontalAlignment.Left, -1, fontSize,
            new Color(1.0f, 0.95f, 0.50f, 1f));
    }
}
