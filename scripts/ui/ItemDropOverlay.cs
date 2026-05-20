using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

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
    // with 50 shroomps continuously dropping/picking-up items on a heavy
    // dig site, this saturated the main thread (DrawAllBadges costs ~3-6 ms
    // per redraw at 300 dropped tiles; 60 Hz × 3-6 ms = 180-360 ms/sec
    // burned on badges alone).
    //
    // Replaced by a HashSet<(int,int)> that records exactly which tiles
    // changed since the last refresh, gated by a 200 ms minimum interval.
    // Sam: terrain / item updates can lag up to 200 ms without the player
    // noticing (designations stay instant — separate overlay), and the sim
    // thread reads tile state directly from LocalMap so shroomps re-evaluate
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
        // Above the designation overlay (z=0) but below the shroomp colony
        // (z=1) so a shroomp walking over an item visually obscures it.
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
            // v0.4.33 — corpse-tier label: use the dead shroomp's name + a
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

    // v0.5.84t — variant catalogue. Each index has a dedicated PaintVariant
    // function that draws a recognizable bundle/pile/silhouette in 16 px so
    // the player can identify what's on the ground at a glance (Sam: "we
    // want everything easily identifiable at a glance"). The pre-v0.5.84t
    // system rendered every drop as a tinted Round/Square/Diamond/Triangle,
    // which read as "coloured dots."
    //
    //   [0..6]   Food: Capberry / SmallMushroom / HerbCluster / MagicBerry /
    //                  PreparedMeal / BerryJuice / Food default (basket)
    //   [7..10]  Wood: DeadWood / LivingWood / Fungal / Wood default (stick bundles)
    //   [11..16] Stone: Granite / Limestone / Marble / Obsidian / Quartz / Stone default
    //   [17..18] Magic mineral: Magicstone / MagicCrystal
    //   [19]     RefinedPlank — stacked planks
    //   [20]     Pebblestone — rounded cobblestones
    //   [21]     Cuttings (Grass) — angled green tufts
    //   [22]     Mosslet — moss clump
    //   [23]     MossCloth — folded green cloth
    //   [24]     GrassLinen — folded tan cloth
    //   [25]     Cloth default — folded blue cloth
    //   [26]     BoneFragment — bone shapes
    //   [27]     Material default — generic brown bundle
    //   [28..30] Magic items: RawEssence / CrystalShard / Magic default (vials/shards)
    //   [31]     MagicHerbPoultice — clay jar
    //   [32]     Fallback (unknown)
    //   [33..38] Equipment: Tool / Knife / Weapon / Apparel / Furniture / Trinket
    //   [39]     Corpse
    //   [40..48] v0.5.84t — extended weapon catalogue: Spear / Club / Sling /
    //              Bow / Crossbow / Atlatl / Sword / Axe / Shield
    private const int VariantCount = 49;
    private static int VariantIndexFor(Item item)
    {
        if (item.Kind == ItemKind.Food)
        {
            return item.SubType switch
            {
                "Capberry"     => 0,
                "SmallMushroom"=> 1,
                "HerbCluster"  => 2,
                "MagicBerry"   => 3,
                "PreparedMeal" => 4,
                "BerryJuice"   => 5,
                _              => 6,
            };
        }
        if (item.Kind == ItemKind.Material)
        {
            // v0.5.84t — Pebblestone (cobblestone-style refined stone)
            // gets a dedicated variant regardless of stone subtype.
            if (item.SubType == "Pebblestone") return 20;
            // v0.5.84t — RefinedPlank gets its own variant regardless of
            // wood subtype (stacked-planks icon).
            if (item.SubType == "RefinedPlank") return 19;
            // v0.5.84t — BoneFragment is materially distinct.
            if (item.SubType == "BoneFragment") return 26;
            return item.Material.Family switch
            {
                "Wood" => item.Material.SubType switch
                {
                    "DeadWood"   => 7,
                    "LivingWood" => 8,
                    "Fungal"     => 9,
                    _            => 10,
                },
                "Stone" => item.Material.SubType switch
                {
                    "Granite"      => 11,
                    "Limestone"    => 12,
                    "Marble"       => 13,
                    "Obsidian"     => 14,
                    "Quartz"       => 15,
                    "Magicstone"   => 17,
                    "MagicCrystal" => 18,
                    _              => 16,
                },
                "Plant" => item.Material.SubType switch
                {
                    "Cuttings" => 21,
                    "Mosslet"  => 22,
                    _          => 21,
                },
                // v0.5.84t — cloth family (MossCloth + GrassLinen).
                "Cloth" => item.SubType switch
                {
                    "MossCloth"  => 23,
                    "GrassLinen" => 24,
                    _            => 25,
                },
                "Bone"  => 26,
                _       => 27,
            };
        }
        if (item.Kind == ItemKind.Magic)
        {
            return item.SubType switch
            {
                "RawEssence"        => 28,
                "CrystalShard"      => 29,
                "MagicHerbPoultice" => 31,
                _                   => 30,
            };
        }
        if (item.Kind == ItemKind.Tool)
        {
            // v0.5.84t — Knife gets a dedicated blade-silhouette variant.
            if (item.SubType == "Knife") return 34;
            return 33;   // Tool default
        }
        if (item.Kind == ItemKind.Weapon)
        {
            // v0.5.84t — per-weapon icons so the player can identify
            // dropped weapons at a glance (was all-sword silhouette).
            return item.SubType switch
            {
                "Spear"    => 40,
                "Club"     => 41,
                "Sling"    => 42,
                "Bow"      => 43,
                "Crossbow" => 44,
                "Atlatl"   => 45,
                "Sword"    => 46,
                "Axe"      => 47,
                _          => 35,   // Weapon default (generic blade)
            };
        }
        if (item.Kind == ItemKind.Apparel)
        {
            // v0.5.84t — Shield is technically Apparel (off-hand slot) but
            // visually distinct enough to deserve its own icon.
            if (item.SubType == "Shield") return 48;
            return 36;   // Apparel default (folded cloth)
        }
        if (item.Kind == ItemKind.Furniture) return 37;
        if (item.Kind == ItemKind.Trinket)   return 38;
        if (item.Kind == ItemKind.Corpse)    return 39;
        return 32;
    }

    // v0.5.84t — per-variant pixel-art painters. Each function draws a
    // recognizable bundle/pile/silhouette in 16 px. Replaces the pre-v0.5.84t
    // colored Round/Square/Diamond/Triangle dots that read as "coloured
    // markers" not "stick bundle / berry / sword". Sam: "redesign dropped
    // item/raw material textures to actually look like a bundle of whatever
    // it's called... easily identifiable at a glance."
    private static ImageTexture BakeVariantSprite(int variantIndex)
    {
        var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
        switch (variantIndex)
        {
            case 0:  PaintCapberry(img);        break;
            case 1:  PaintSmallMushroom(img);   break;
            case 2:  PaintHerbCluster(img);     break;
            case 3:  PaintMagicBerry(img);      break;
            case 4:  PaintPreparedMeal(img);    break;
            case 5:  PaintBerryJuice(img);      break;
            case 6:  PaintFoodDefault(img);     break;
            case 7:  PaintStickBundle(img, new Color(0.78f, 0.58f, 0.36f), new Color(0.40f, 0.26f, 0.12f));         break;  // DeadWood
            case 8:  PaintStickBundle(img, new Color(0.58f, 0.75f, 0.32f), new Color(0.26f, 0.40f, 0.12f), leaf: true); break; // LivingWood
            case 9:  PaintStickBundle(img, new Color(0.85f, 0.74f, 0.86f), new Color(0.42f, 0.30f, 0.46f));         break;  // Fungal (purple-cream)
            case 10: PaintStickBundle(img, new Color(0.72f, 0.55f, 0.32f), new Color(0.36f, 0.22f, 0.10f));         break;  // Wood default
            case 11: PaintStonePile(img, new Color(0.62f, 0.62f, 0.68f), new Color(0.30f, 0.30f, 0.34f));           break;  // Granite
            case 12: PaintStonePile(img, new Color(0.88f, 0.82f, 0.66f), new Color(0.46f, 0.40f, 0.28f));           break;  // Limestone
            case 13: PaintStonePile(img, new Color(0.92f, 0.90f, 0.88f), new Color(0.46f, 0.44f, 0.42f));           break;  // Marble
            case 14: PaintStonePile(img, new Color(0.35f, 0.32f, 0.45f), new Color(0.12f, 0.10f, 0.18f));           break;  // Obsidian
            case 15: PaintStonePile(img, new Color(0.86f, 0.90f, 0.96f), new Color(0.46f, 0.50f, 0.58f));           break;  // Quartz
            case 16: PaintStonePile(img, new Color(0.55f, 0.55f, 0.62f), new Color(0.30f, 0.30f, 0.34f));           break;  // Stone default
            case 17: PaintStonePile(img, new Color(0.62f, 0.50f, 0.78f), new Color(0.28f, 0.20f, 0.42f), sparkle: true); break;  // Magicstone
            case 18: PaintCrystalCluster(img, new Color(0.65f, 0.95f, 1.00f), new Color(0.30f, 0.45f, 0.60f));      break;  // MagicCrystal
            case 19: PaintRefinedPlank(img);    break;
            case 20: PaintPebblestone(img);     break;
            case 21: PaintCuttings(img);        break;
            case 22: PaintMosslet(img);         break;
            case 23: PaintFoldedCloth(img, new Color(0.45f, 0.70f, 0.42f), new Color(0.20f, 0.36f, 0.18f));         break;  // MossCloth
            case 24: PaintFoldedCloth(img, new Color(0.85f, 0.76f, 0.52f), new Color(0.46f, 0.38f, 0.22f));         break;  // GrassLinen
            case 25: PaintFoldedCloth(img, new Color(0.45f, 0.55f, 0.78f), new Color(0.20f, 0.25f, 0.36f));         break;  // Cloth default
            case 26: PaintBone(img);            break;
            case 27: PaintMaterialDefault(img); break;
            case 28: PaintCrystalCluster(img, new Color(0.55f, 0.92f, 1.00f), new Color(0.20f, 0.42f, 0.60f));      break;  // RawEssence
            case 29: PaintCrystalShard(img);    break;
            case 30: PaintCrystalCluster(img, new Color(0.65f, 0.85f, 1.00f), new Color(0.25f, 0.40f, 0.60f));      break;  // Magic default
            case 31: PaintJar(img);             break;  // MagicHerbPoultice
            case 32: PaintFallback(img);        break;
            case 33: PaintToolDefault(img);     break;
            case 34: PaintKnife(img);           break;
            case 35: PaintSword(img);           break;
            case 36: PaintFoldedCloth(img, new Color(0.45f, 0.55f, 0.78f), new Color(0.20f, 0.25f, 0.36f));         break;  // Apparel
            case 37: PaintFurniture(img);       break;
            case 38: PaintTrinket(img);         break;
            case 39: PaintCorpse(img);          break;
            case 40: PaintSpear(img);           break;
            case 41: PaintClub(img);            break;
            case 42: PaintSling(img);           break;
            case 43: PaintBow(img);             break;
            case 44: PaintCrossbow(img);        break;
            case 45: PaintAtlatl(img);          break;
            case 46: PaintSword(img);           break;   // dedicated sword (was Weapon=35 fallback)
            case 47: PaintAxe(img);             break;
            case 48: PaintShield(img);          break;
            default: PaintFallback(img);        break;
        }
        return ImageTexture.CreateFromImage(img);
    }

    // ── Per-variant painters ──────────────────────────────────────────────
    // 16 × 16 canvas. Origin (0,0) is top-left; centre ≈ (8, 8).

    // Cluster of 3 purple berries with highlight pixels (top-left).
    private static void PaintCapberry(Image img)
    {
        Color body = new(0.65f, 0.32f, 0.88f), edge = new(0.28f, 0.12f, 0.42f), hi = new(0.92f, 0.78f, 1.00f);
        // Bottom-left, bottom-right, top.
        FillCircleOnImage(img, 5,  10, 3.2f, body); CircleOutlineOnImage(img, 5,  10, 3.2f, edge);
        FillCircleOnImage(img, 11, 10, 3.2f, body); CircleOutlineOnImage(img, 11, 10, 3.2f, edge);
        FillCircleOnImage(img, 8,  5,  3.2f, body); CircleOutlineOnImage(img, 8,  5,  3.2f, edge);
        img.SetPixel(4, 9,  hi); img.SetPixel(10, 9, hi); img.SetPixel(7, 4, hi);
    }

    // Cap on top, narrow stem below. Tan cap with darker spots.
    private static void PaintSmallMushroom(Image img)
    {
        Color cap = new(0.78f, 0.42f, 0.30f), spot = new(0.95f, 0.92f, 0.80f), edge = new(0.36f, 0.16f, 0.12f), stem = new(0.94f, 0.86f, 0.72f);
        // Cap: half-disc top, base rectangle.
        FillCircleOnImage(img, 8, 7, 5f, cap);
        FillRectOnImage(img, 3, 7, 11, 2, cap);
        // Spots on cap.
        img.SetPixel(6, 5, spot); img.SetPixel(10, 5, spot); img.SetPixel(8, 7, spot);
        // Stem.
        FillRectOnImage(img, 6, 9, 4, 4, stem);
        RectOutlineOnImage(img, 6, 9, 4, 4, edge);
        // Cap outline.
        CircleOutlineOnImage(img, 8, 7, 5f, edge);
    }

    // Vertical stem + 3 angled leaves.
    private static void PaintHerbCluster(Image img)
    {
        Color leaf = new(0.40f, 0.78f, 0.30f), stem = new(0.32f, 0.50f, 0.18f), edge = new(0.18f, 0.30f, 0.10f);
        // Stem.
        FillRectOnImage(img, 7, 5, 2, 9, stem);
        // 3 leaves (each is a small angled cluster of pixels).
        // Top leaf.
        FillCircleOnImage(img, 8, 4, 2.5f, leaf);  CircleOutlineOnImage(img, 8, 4, 2.5f, edge);
        // Mid-left.
        FillCircleOnImage(img, 4, 7, 2.2f, leaf);  CircleOutlineOnImage(img, 4, 7, 2.2f, edge);
        // Mid-right.
        FillCircleOnImage(img, 12, 8, 2.2f, leaf); CircleOutlineOnImage(img, 12, 8, 2.2f, edge);
    }

    // Single glowing magenta berry with sparkle pixels.
    private static void PaintMagicBerry(Image img)
    {
        Color body = new(0.88f, 0.40f, 0.95f), edge = new(0.42f, 0.14f, 0.50f), hi = new(1.00f, 0.85f, 1.00f), spark = new(1.00f, 1.00f, 0.85f);
        FillCircleOnImage(img, 8, 8, 5f, body);
        CircleOutlineOnImage(img, 8, 8, 5f, edge);
        // Highlight + sparkle.
        FillCircleOnImage(img, 6, 6, 1.4f, hi);
        img.SetPixel(13, 4,  spark); img.SetPixel(14, 5, spark); img.SetPixel(3, 11, spark);
    }

    // Wooden bowl with food content.
    private static void PaintPreparedMeal(Image img)
    {
        Color bowl = new(0.55f, 0.35f, 0.18f), bowlEdge = new(0.26f, 0.14f, 0.06f);
        Color food = new(0.85f, 0.62f, 0.30f), hi = new(0.96f, 0.82f, 0.55f);
        // Food (top half-disc).
        FillCircleOnImage(img, 8, 8, 5.5f, food);
        // Bowl rim + base under.
        FillRectOnImage(img, 2, 8, 12, 2, bowl);
        // Bowl curve.
        FillCircleOnImage(img, 8, 10, 5.5f, bowl);
        FillRectOnImage(img, 2, 10, 12, 3, bowl);
        // Edges.
        RectOutlineOnImage(img, 2, 8, 12, 6, bowlEdge);
        img.SetPixel(7, 5, hi); img.SetPixel(9, 6, hi);
    }

    // Tapered flask: narrow neck, wider body with purple liquid.
    private static void PaintBerryJuice(Image img)
    {
        Color glass = new(0.95f, 0.95f, 1.00f), edge = new(0.40f, 0.30f, 0.50f), liquid = new(0.55f, 0.28f, 0.78f);
        // Neck.
        FillRectOnImage(img, 6, 2, 4, 2, glass);
        RectOutlineOnImage(img, 6, 2, 4, 2, edge);
        // Shoulder (transition).
        FillRectOnImage(img, 5, 4, 6, 1, glass);
        // Body.
        FillRectOnImage(img, 3, 5, 10, 9, glass);
        RectOutlineOnImage(img, 3, 5, 10, 9, edge);
        // Liquid fill bottom 2/3 of body.
        FillRectOnImage(img, 4, 8, 8, 5, liquid);
        // Bottom edge.
        FillRectOnImage(img, 3, 13, 10, 1, edge);
    }

    // Generic basket: tan rectangle with horizontal weave lines.
    private static void PaintFoodDefault(Image img)
    {
        Color basket = new(0.78f, 0.55f, 0.25f), weave = new(0.50f, 0.32f, 0.10f), edge = new(0.32f, 0.18f, 0.05f);
        FillRectOnImage(img, 2, 5, 12, 9, basket);
        // Weave lines (3 horizontal + 2 vertical).
        for (int x = 2; x < 14; x++) { img.SetPixel(x, 7, weave); img.SetPixel(x, 10, weave); }
        for (int y = 5; y < 14; y++) { img.SetPixel(5, y, weave); img.SetPixel(10, y, weave); }
        RectOutlineOnImage(img, 2, 5, 12, 9, edge);
    }

    // Bundle of horizontal sticks tied together. Optional small leaf for LivingWood.
    private static void PaintStickBundle(Image img, Color fill, Color edge, bool leaf = false)
    {
        // 4 horizontal sticks stacked, each 10 px wide × 2 px tall.
        for (int i = 0; i < 4; i++)
        {
            int y = 3 + i * 3;
            FillRectOnImage(img, 3, y, 10, 2, fill);
            RectOutlineOnImage(img, 3, y, 10, 2, edge);
        }
        // Binding twine (vertical lines).
        Color twine = new(0.38f, 0.22f, 0.10f);
        for (int y = 3; y < 14; y++) { img.SetPixel(5, y, twine); img.SetPixel(11, y, twine); }
        if (leaf)
        {
            Color leafCol = new(0.40f, 0.78f, 0.30f);
            FillCircleOnImage(img, 13, 2, 1.8f, leafCol);
        }
    }

    // 4 small blocks stacked in a pile (2 base + 1 middle + 1 top, with the
    // top one offset for irregular look).
    private static void PaintStonePile(Image img, Color fill, Color edge, bool sparkle = false)
    {
        // Bottom row: two 5×4 blocks side by side.
        FillRectOnImage(img, 1, 9, 6, 5, fill);  RectOutlineOnImage(img, 1, 9, 6, 5, edge);
        FillRectOnImage(img, 8, 9, 7, 5, fill);  RectOutlineOnImage(img, 8, 9, 7, 5, edge);
        // Middle row: one 6×4 block centred.
        FillRectOnImage(img, 4, 4, 7, 5, fill);  RectOutlineOnImage(img, 4, 4, 7, 5, edge);
        // Top row: one small 4×3 block.
        FillRectOnImage(img, 6, 1, 5, 3, fill);  RectOutlineOnImage(img, 6, 1, 5, 3, edge);
        if (sparkle)
        {
            Color spark = new(1.00f, 0.95f, 1.00f);
            img.SetPixel(3, 12, spark); img.SetPixel(9, 6, spark); img.SetPixel(13, 11, spark);
        }
    }

    // 3 angular shards radiating from base.
    private static void PaintCrystalCluster(Image img, Color fill, Color edge)
    {
        // Centre shard (tall diamond).
        var tipC  = new Vector2(8, 1); var lC = new Vector2(6, 7); var rC = new Vector2(10, 7);
        FillTriangleOnImage(img, tipC, lC, rC, fill);
        DrawLineOnImage(img, tipC, lC, edge); DrawLineOnImage(img, tipC, rC, edge);
        // Left shard.
        var tipL = new Vector2(3, 5); var lL = new Vector2(1, 11); var rL = new Vector2(5, 11);
        FillTriangleOnImage(img, tipL, lL, rL, fill);
        DrawLineOnImage(img, tipL, lL, edge); DrawLineOnImage(img, tipL, rL, edge);
        // Right shard.
        var tipR = new Vector2(13, 5); var lR = new Vector2(11, 11); var rR = new Vector2(15, 11);
        FillTriangleOnImage(img, tipR, lR, rR, fill);
        DrawLineOnImage(img, tipR, lR, edge); DrawLineOnImage(img, tipR, rR, edge);
        // Base line.
        for (int x = 1; x < 15; x++) img.SetPixel(x, 12, edge);
        // Highlights.
        Color hi = new(1.00f, 1.00f, 1.00f, 0.9f);
        img.SetPixel(8, 3, hi); img.SetPixel(3, 7, hi); img.SetPixel(13, 7, hi);
    }

    // Sharp single shard (RimWorld-style gem). Magenta.
    private static void PaintCrystalShard(Image img)
    {
        Color fill = new(0.88f, 0.55f, 1.00f), edge = new(0.42f, 0.20f, 0.52f), hi = new(1.00f, 0.92f, 1.00f);
        var tip   = new Vector2(8,  1);
        var midL  = new Vector2(3,  7);
        var midR  = new Vector2(13, 7);
        var bot   = new Vector2(8,  14);
        FillTriangleOnImage(img, tip, midL, bot, fill);
        FillTriangleOnImage(img, tip, midR, bot, fill);
        DrawLineOnImage(img, tip, midL, edge); DrawLineOnImage(img, midL, bot, edge);
        DrawLineOnImage(img, tip, midR, edge); DrawLineOnImage(img, midR, bot, edge);
        DrawLineOnImage(img, tip, bot,  edge);
        img.SetPixel(7, 4, hi); img.SetPixel(7, 8, hi);
    }

    // 3 stacked plank boards with grain lines.
    private static void PaintRefinedPlank(Image img)
    {
        Color fill = new(0.78f, 0.58f, 0.36f), edge = new(0.36f, 0.22f, 0.10f), grain = new(0.55f, 0.36f, 0.18f);
        for (int i = 0; i < 3; i++)
        {
            int y = 3 + i * 4;
            FillRectOnImage(img, 1, y, 14, 3, fill);
            RectOutlineOnImage(img, 1, y, 14, 3, edge);
            // Grain dash near middle.
            for (int x = 3; x < 13; x += 3) img.SetPixel(x, y + 1, grain);
        }
    }

    // 5 rounded cobblestones in a pile (cream-grey).
    private static void PaintPebblestone(Image img)
    {
        Color fill = new(0.72f, 0.68f, 0.62f), edge = new(0.36f, 0.32f, 0.28f), hi = new(0.90f, 0.86f, 0.78f);
        // Bottom row: 3 cobbles.
        FillCircleOnImage(img, 3,  12, 2.4f, fill); CircleOutlineOnImage(img, 3,  12, 2.4f, edge);
        FillCircleOnImage(img, 8,  12, 2.6f, fill); CircleOutlineOnImage(img, 8,  12, 2.6f, edge);
        FillCircleOnImage(img, 13, 12, 2.2f, fill); CircleOutlineOnImage(img, 13, 12, 2.2f, edge);
        // Top row: 2 cobbles.
        FillCircleOnImage(img, 5,  6, 2.4f, fill);  CircleOutlineOnImage(img, 5,  6, 2.4f, edge);
        FillCircleOnImage(img, 11, 6, 2.4f, fill);  CircleOutlineOnImage(img, 11, 6, 2.4f, edge);
        img.SetPixel(2, 11, hi); img.SetPixel(7, 11, hi); img.SetPixel(4, 5, hi); img.SetPixel(10, 5, hi);
    }

    // 3 angled grass blades coming up from a base.
    private static void PaintCuttings(Image img)
    {
        Color leaf = new(0.45f, 0.68f, 0.28f), edge = new(0.20f, 0.32f, 0.10f);
        // Base ground line.
        FillRectOnImage(img, 2, 12, 12, 1, edge);
        // 3 blades — vertical, left-leaning, right-leaning.
        DrawLineOnImage(img, 8, 12, 8,  3, leaf);
        DrawLineOnImage(img, 8, 12, 7,  3, leaf);
        DrawLineOnImage(img, 5, 12, 3,  5, leaf);
        DrawLineOnImage(img, 5, 12, 4,  5, leaf);
        DrawLineOnImage(img, 11, 12, 13, 5, leaf);
        DrawLineOnImage(img, 11, 12, 12, 5, leaf);
        // Tips slightly lighter.
        Color tip = new(0.65f, 0.85f, 0.40f);
        img.SetPixel(8, 3, tip); img.SetPixel(3, 5, tip); img.SetPixel(13, 5, tip);
    }

    // Cluster of 3 soft-green moss circles.
    private static void PaintMosslet(Image img)
    {
        Color fill = new(0.42f, 0.78f, 0.42f), edge = new(0.16f, 0.36f, 0.18f), hi = new(0.78f, 1.00f, 0.78f);
        FillCircleOnImage(img, 5,  10, 3.0f, fill); CircleOutlineOnImage(img, 5,  10, 3.0f, edge);
        FillCircleOnImage(img, 11, 10, 3.0f, fill); CircleOutlineOnImage(img, 11, 10, 3.0f, edge);
        FillCircleOnImage(img, 8,  5,  3.4f, fill); CircleOutlineOnImage(img, 8,  5,  3.4f, edge);
        img.SetPixel(4, 9, hi); img.SetPixel(7, 4, hi); img.SetPixel(10, 9, hi);
    }

    // Folded square cloth with a diagonal fold line.
    private static void PaintFoldedCloth(Image img, Color fill, Color edge)
    {
        // Main square slightly inset.
        FillRectOnImage(img, 2, 3, 12, 10, fill);
        RectOutlineOnImage(img, 2, 3, 12, 10, edge);
        // Diagonal fold line top-left → bottom-right.
        DrawLineOnImage(img, 2, 3, 14, 13, edge);
        // Highlight band along fold.
        Color hi = new(fill.R + 0.10f, fill.G + 0.10f, fill.B + 0.10f, 1f);
        for (int t = 1; t < 11; t++) img.SetPixel(2 + t, 4 + t, hi);
    }

    // Curved white bone with rounded knobs at each end (rib-bone).
    private static void PaintBone(Image img)
    {
        Color fill = new(0.96f, 0.94f, 0.84f), edge = new(0.46f, 0.42f, 0.32f);
        // Shaft (slightly angled).
        FillRectOnImage(img, 3, 7, 10, 2, fill);
        RectOutlineOnImage(img, 3, 7, 10, 2, edge);
        // Left knob.
        FillCircleOnImage(img, 3, 8, 2.4f, fill); CircleOutlineOnImage(img, 3, 8, 2.4f, edge);
        // Right knob.
        FillCircleOnImage(img, 13, 8, 2.4f, fill); CircleOutlineOnImage(img, 13, 8, 2.4f, edge);
    }

    // Generic brown bundle (rectangular wrap + cross binding).
    private static void PaintMaterialDefault(Image img)
    {
        Color fill = new(0.62f, 0.46f, 0.26f), edge = new(0.28f, 0.18f, 0.08f), tie = new(0.18f, 0.10f, 0.04f);
        FillRectOnImage(img, 3, 4, 10, 9, fill);
        RectOutlineOnImage(img, 3, 4, 10, 9, edge);
        // Cross binding.
        for (int x = 3; x < 13; x++) img.SetPixel(x, 8, tie);
        for (int y = 4; y < 13; y++) img.SetPixel(8, y, tie);
    }

    // Small clay jar with cork (magic herb poultice).
    private static void PaintJar(Image img)
    {
        Color body = new(0.68f, 0.52f, 0.34f), edge = new(0.32f, 0.20f, 0.08f), cork = new(0.42f, 0.28f, 0.16f);
        Color contents = new(0.55f, 0.85f, 0.45f);
        // Body.
        FillRectOnImage(img, 3, 6, 10, 8, body);
        RectOutlineOnImage(img, 3, 6, 10, 8, edge);
        // Neck.
        FillRectOnImage(img, 6, 3, 4, 3, body);
        RectOutlineOnImage(img, 6, 3, 4, 3, edge);
        // Cork.
        FillRectOnImage(img, 6, 2, 4, 1, cork);
        // Contents window — small bright-green strip.
        FillRectOnImage(img, 5, 9, 6, 2, contents);
    }

    // Grey circle with "?" overlay.
    private static void PaintFallback(Image img)
    {
        Color fill = new(0.74f, 0.74f, 0.74f), edge = new(0.30f, 0.30f, 0.30f);
        FillCircleOnImage(img, 8, 8, 5f, fill);
        CircleOutlineOnImage(img, 8, 8, 5f, edge);
    }

    // Generic tool: T-shaped (handle + cross-head).
    private static void PaintToolDefault(Image img)
    {
        Color wood = new(0.62f, 0.42f, 0.22f), metal = new(0.75f, 0.75f, 0.78f), edge = new(0.20f, 0.12f, 0.05f);
        // Handle (vertical).
        FillRectOnImage(img, 7, 4, 2, 10, wood);
        RectOutlineOnImage(img, 7, 4, 2, 10, edge);
        // Head (horizontal).
        FillRectOnImage(img, 3, 3, 10, 3, metal);
        RectOutlineOnImage(img, 3, 3, 10, 3, edge);
    }

    // Diagonal blade silhouette with small dark handle.
    private static void PaintKnife(Image img)
    {
        Color blade = new(0.86f, 0.88f, 0.92f), edge = new(0.30f, 0.32f, 0.36f);
        Color handle = new(0.42f, 0.28f, 0.16f), handleEdge = new(0.18f, 0.10f, 0.05f);
        // Blade — diagonal triangle (point top-right, base bottom-left).
        var tip  = new Vector2(14, 2);
        var b1   = new Vector2(11, 8);
        var b2   = new Vector2(6,  10);
        FillTriangleOnImage(img, tip, b1, b2, blade);
        DrawLineOnImage(img, tip, b1, edge); DrawLineOnImage(img, b1, b2, edge); DrawLineOnImage(img, b2, tip, edge);
        // Handle (bottom-left segment).
        FillRectOnImage(img, 2, 10, 6, 3, handle);
        RectOutlineOnImage(img, 2, 10, 6, 3, handleEdge);
    }

    // Long red-tinted blade with cross-guard (generic sword).
    private static void PaintSword(Image img)
    {
        Color blade = new(0.88f, 0.50f, 0.50f), edge = new(0.36f, 0.12f, 0.12f);
        Color guard = new(0.55f, 0.45f, 0.20f), pommel = new(0.78f, 0.62f, 0.30f);
        // Blade — long vertical.
        FillRectOnImage(img, 7, 1, 2, 9, blade);
        // Pointed tip.
        img.SetPixel(8, 0, blade);
        // Cross-guard.
        FillRectOnImage(img, 4, 10, 8, 2, guard);
        // Grip.
        FillRectOnImage(img, 7, 12, 2, 3, edge);
        // Pommel.
        FillRectOnImage(img, 6, 14, 4, 1, pommel);
        // Blade outline.
        RectOutlineOnImage(img, 7, 1, 2, 9, edge);
        RectOutlineOnImage(img, 4, 10, 8, 2, edge);
    }

    // Chunky brown rectangle (chair/cabinet silhouette).
    private static void PaintFurniture(Image img)
    {
        Color fill = new(0.60f, 0.42f, 0.22f), edge = new(0.30f, 0.20f, 0.08f), legShadow = new(0.18f, 0.10f, 0.04f);
        // Seat.
        FillRectOnImage(img, 2, 6, 12, 4, fill);
        RectOutlineOnImage(img, 2, 6, 12, 4, edge);
        // Back.
        FillRectOnImage(img, 2, 2, 3, 4, fill);
        RectOutlineOnImage(img, 2, 2, 3, 4, edge);
        // Legs.
        FillRectOnImage(img, 3, 10, 2, 4, legShadow);
        FillRectOnImage(img, 11, 10, 2, 4, legShadow);
    }

    // Gold disc with 4-point sparkle.
    private static void PaintTrinket(Image img)
    {
        Color fill = new(0.96f, 0.82f, 0.32f), edge = new(0.55f, 0.42f, 0.10f), spark = new(1.00f, 1.00f, 0.92f);
        FillCircleOnImage(img, 8, 8, 5f, fill);
        CircleOutlineOnImage(img, 8, 8, 5f, edge);
        // 4-point star.
        img.SetPixel(8, 4, spark); img.SetPixel(8, 12, spark);
        img.SetPixel(4, 8, spark); img.SetPixel(12, 8, spark);
        img.SetPixel(8, 7, spark); img.SetPixel(8, 9, spark);
        img.SetPixel(7, 8, spark); img.SetPixel(9, 8, spark);
    }

    // Dusky-red body silhouette (top = head circle, bottom = torso rectangle).
    private static void PaintCorpse(Image img)
    {
        Color body = new(0.60f, 0.24f, 0.24f), edge = new(0.26f, 0.08f, 0.08f);
        // Head.
        FillCircleOnImage(img, 8, 4, 2.4f, body);
        CircleOutlineOnImage(img, 8, 4, 2.4f, edge);
        // Torso.
        FillRectOnImage(img, 4, 7, 8, 7, body);
        RectOutlineOnImage(img, 4, 7, 8, 7, edge);
    }

    // ── v0.5.84t extended weapon icons ────────────────────────────────────

    // Long tan haft with a small triangular point at the top.
    private static void PaintSpear(Image img)
    {
        Color haft = new(0.62f, 0.42f, 0.22f), edge = new(0.28f, 0.18f, 0.08f);
        Color tip  = new(0.85f, 0.85f, 0.88f), tipEdge = new(0.35f, 0.35f, 0.40f);
        // Haft — vertical line.
        FillRectOnImage(img, 7, 4, 2, 11, haft);
        RectOutlineOnImage(img, 7, 4, 2, 11, edge);
        // Tip — small upward-pointing triangle.
        var t1 = new Vector2(8, 0); var t2 = new Vector2(5, 5); var t3 = new Vector2(11, 5);
        FillTriangleOnImage(img, t1, t2, t3, tip);
        DrawLineOnImage(img, t1, t2, tipEdge); DrawLineOnImage(img, t2, t3, tipEdge); DrawLineOnImage(img, t3, t1, tipEdge);
        // Binding.
        Color tie = new(0.18f, 0.10f, 0.04f);
        for (int x = 6; x <= 9; x++) img.SetPixel(x, 6, tie);
    }

    // Tan handle + bulbous knob at the bottom (banded with darker rings).
    private static void PaintClub(Image img)
    {
        Color body = new(0.65f, 0.45f, 0.22f), edge = new(0.30f, 0.20f, 0.08f), band = new(0.42f, 0.26f, 0.10f);
        // Handle.
        FillRectOnImage(img, 7, 1, 2, 7, body);
        RectOutlineOnImage(img, 7, 1, 2, 7, edge);
        // Knob — wider tapered rectangle.
        FillRectOnImage(img, 4, 8, 8, 7, body);
        RectOutlineOnImage(img, 4, 8, 8, 7, edge);
        // 2 darker bands across the knob.
        for (int x = 4; x < 12; x++) { img.SetPixel(x, 10, band); img.SetPixel(x, 13, band); }
    }

    // V-shape of two cords meeting at a small pouch.
    private static void PaintSling(Image img)
    {
        Color cord = new(0.85f, 0.78f, 0.55f), pouch = new(0.55f, 0.40f, 0.20f), edge = new(0.30f, 0.20f, 0.08f);
        // Two cords from top corners to a centre pouch.
        DrawLineOnImage(img, 1, 2, 8, 9, cord);
        DrawLineOnImage(img, 14, 2, 8, 9, cord);
        // Pouch — small oval near the centre bottom.
        FillCircleOnImage(img, 8, 11, 2.6f, pouch);
        CircleOutlineOnImage(img, 8, 11, 2.6f, edge);
        // Small stone in the pouch.
        Color stone = new(0.62f, 0.62f, 0.68f);
        FillCircleOnImage(img, 8, 11, 1.0f, stone);
    }

    // Curved bow arc + drawn string + arrow notched.
    private static void PaintBow(Image img)
    {
        Color bowCol = new(0.55f, 0.36f, 0.18f), edge = new(0.26f, 0.14f, 0.05f);
        Color str    = new(0.92f, 0.86f, 0.70f);
        Color arrow  = new(0.80f, 0.80f, 0.82f);
        // Bow arc — approximate with 6 line segments forming a 'C' shape.
        DrawLineOnImage(img, 12, 1,  14, 4,  bowCol);
        DrawLineOnImage(img, 14, 4,  15, 8,  bowCol);
        DrawLineOnImage(img, 15, 8,  14, 12, bowCol);
        DrawLineOnImage(img, 14, 12, 12, 15, bowCol);
        // Inner darker edge.
        DrawLineOnImage(img, 11, 2,  13, 5,  edge);
        DrawLineOnImage(img, 13, 5,  14, 8,  edge);
        DrawLineOnImage(img, 13, 11, 11, 14, edge);
        // String — straight vertical (taut).
        DrawLineOnImage(img, 12, 2, 12, 14, str);
        // Arrow — horizontal through midpoint, fletching at left.
        FillRectOnImage(img, 2, 7, 9, 1, arrow);
        // Arrowhead.
        img.SetPixel(11, 7, edge);
        // Fletching.
        Color fletch = new(0.85f, 0.85f, 0.88f);
        img.SetPixel(2, 6, fletch); img.SetPixel(2, 8, fletch);
    }

    // Horizontal bow on a vertical stock with trigger.
    private static void PaintCrossbow(Image img)
    {
        Color stock = new(0.55f, 0.36f, 0.18f), edge = new(0.26f, 0.14f, 0.05f);
        Color bow   = new(0.70f, 0.52f, 0.28f);
        Color str   = new(0.92f, 0.86f, 0.70f);
        Color trig  = new(0.42f, 0.42f, 0.48f);
        // Stock — vertical rectangle.
        FillRectOnImage(img, 6, 3, 4, 11, stock);
        RectOutlineOnImage(img, 6, 3, 4, 11, edge);
        // Bow — horizontal bar across the top.
        FillRectOnImage(img, 1, 5, 14, 2, bow);
        RectOutlineOnImage(img, 1, 5, 14, 2, edge);
        // String — taut horizontal under the bow.
        for (int x = 2; x < 14; x++) img.SetPixel(x, 4, str);
        // Trigger — small protrusion under the stock.
        FillRectOnImage(img, 7, 14, 2, 2, trig);
    }

    // Slender hooked stick (atlatl lever) with a small spear resting in it.
    private static void PaintAtlatl(Image img)
    {
        Color stick = new(0.62f, 0.42f, 0.22f), edge = new(0.28f, 0.18f, 0.08f);
        Color spear = new(0.78f, 0.62f, 0.30f);
        Color tip   = new(0.85f, 0.85f, 0.88f);
        // Atlatl shaft — diagonal from bottom-left to top-right.
        DrawLineOnImage(img, 2, 14, 13, 4, stick);
        DrawLineOnImage(img, 2, 13, 13, 3, stick);
        DrawLineOnImage(img, 3, 14, 14, 4, edge);
        // Hook at the back end (bottom-left).
        img.SetPixel(2, 12, stick);
        img.SetPixel(3, 12, stick);
        img.SetPixel(2, 11, edge);
        // Spear resting on the lever — thinner diagonal above.
        DrawLineOnImage(img, 5, 11, 14, 2, spear);
        // Spear tip — small triangle at top-right end.
        img.SetPixel(14, 2, tip); img.SetPixel(15, 1, tip); img.SetPixel(13, 3, tip);
    }

    // Curved-edge axe blade on a vertical wooden haft.
    private static void PaintAxe(Image img)
    {
        Color haft  = new(0.55f, 0.36f, 0.18f), edge = new(0.26f, 0.14f, 0.05f);
        Color blade = new(0.78f, 0.78f, 0.82f), bladeEdge = new(0.32f, 0.32f, 0.38f);
        // Haft — vertical.
        FillRectOnImage(img, 7, 1, 2, 14, haft);
        RectOutlineOnImage(img, 7, 1, 2, 14, edge);
        // Blade — wider triangle on the right side of the haft, near the top.
        var bt = new Vector2(9, 1); var bm = new Vector2(15, 5); var bb = new Vector2(9, 7);
        FillTriangleOnImage(img, bt, bm, bb, blade);
        DrawLineOnImage(img, bt, bm, bladeEdge); DrawLineOnImage(img, bm, bb, bladeEdge); DrawLineOnImage(img, bb, bt, bladeEdge);
        // Small balancing back-blade.
        var bt2 = new Vector2(7, 2); var bm2 = new Vector2(4, 4); var bb2 = new Vector2(7, 6);
        FillTriangleOnImage(img, bt2, bm2, bb2, blade);
        DrawLineOnImage(img, bt2, bm2, bladeEdge); DrawLineOnImage(img, bm2, bb2, bladeEdge);
    }

    // Heater-shaped shield with central cross/boss.
    private static void PaintShield(Image img)
    {
        Color body = new(0.55f, 0.42f, 0.24f), edge = new(0.26f, 0.16f, 0.06f);
        Color band = new(0.85f, 0.75f, 0.42f), boss = new(0.92f, 0.86f, 0.40f);
        // Body — rounded shield (rectangle + bottom point).
        FillRectOnImage(img, 3, 2, 10, 9, body);
        // Bottom V-point.
        var p1 = new Vector2(3, 11); var p2 = new Vector2(13, 11); var p3 = new Vector2(8, 15);
        FillTriangleOnImage(img, p1, p2, p3, body);
        // Outline.
        RectOutlineOnImage(img, 3, 2, 10, 9, edge);
        DrawLineOnImage(img, 3, 11, 8, 15, edge);
        DrawLineOnImage(img, 13, 11, 8, 15, edge);
        // Central vertical band.
        FillRectOnImage(img, 7, 2, 2, 12, band);
        // Boss — small circle at centre.
        FillCircleOnImage(img, 8, 7, 1.6f, boss);
    }

    // v0.4.30 — barycentric scan-fill triangle. Same approach as
    // ShroompColonyView.FillTriangleOnImage but inlined here so the
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
    // hints cleanly at small sizes and matches the shroomp-name label
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
