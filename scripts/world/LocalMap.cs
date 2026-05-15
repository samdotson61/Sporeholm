using System;
using System.Collections.Generic;
using Godot;  // v0.3.33 — Vector2 for indexed designation lookups
using SmurfulationC.Simulation.Items;

namespace SmurfulationC.World
{
    // Dense playable tile grid. Default 80×50 (16×16 px tiles = 1280×800 total).
    // Width and Height are instance properties so maps can be scaled up at world-gen time.
    // A parallel VegetationSlot[,] array occupies the same grid; Phase 3 reads it for task targets.
    public class LocalMap
    {
        public const int TileSize      = 16;
        public const int DefaultWidth  = 80;
        public const int DefaultHeight = 50;

        public int Width  { get; }
        public int Height { get; }
        public int Seed   { get; init; }

        private readonly LocalTile[,]       _tiles;
        private readonly VegetationSlot[,]  _vegetation;

        // v0.3.39 (O-M.2) — flat-array passability cache. Indexed by
        // `y * Width + x`. Maintained alongside `_tiles[x, y].Passable` so
        // BehaviorSystem's tight steering checks (8 candidate steps per
        // smurf per tick × 1000 smurfs × 60 Hz = 480 k IsPassable calls/sec
        // at target scale) avoid the LocalTile struct copy + bounds check.
        // Always read through the public `IsPassable` method, which still
        // walls off out-of-bounds via the array length check below.
        private readonly bool[] _passable;

        // v0.4.13 — DF-style connected-component IDs per tile. 0 = impassable.
        // Non-zero values group passable tiles into 8-connected reachability
        // regions, sharing the same diagonal cut-corner rule as
        // `Pathfinder.FindPath`. `AreReachable` and `IsWorkReachable` do an
        // O(1) region-id compare so `BehaviorSystem` can filter unreachable
        // designations and `Pathfinder` can fail-fast before A* explores its
        // 1024-node budget. Rebuilt lazily via `EnsureRegions`; any code path
        // that mutates passability sets `_regionsDirty = true`. Full rebuild
        // is a single BFS pass over W×H tiles (microseconds at 64k tiles).
        private readonly ushort[] _regionId;
        private bool _regionsDirty = true;
        private ushort _regionCount;            // number of regions in the latest rebuild
        private readonly object _regionLock = new();

        // v0.4.14 — sim-tick batching. `BeginTick` rebuilds once at the
        // top of `BehaviorSystem.Tick` and then suppresses any further
        // rebuild for the duration of the tick. Without this gate,
        // `ApplyTaskEffect` mutations (every dig completion) flip
        // `_regionsDirty` mid-loop and the next smurf's
        // `FindNearestExcavate` re-runs the full W×H BFS — at 240×150 +
        // 17 simultaneous diggers that was ~50 ms / tick of pure rebuild
        // cost. Within a tick the stale region data is acceptable: the
        // worst case is a smurf picking a target that was reachable a
        // tick ago and is still reachable (excavation only adds
        // connectivity), so no correctness loss.
        private volatile bool _freezeRegionRebuilds;

        // v0.4.14 — reused BFS scratch queue. The previous version
        // allocated a fresh Queue<(int, int)> on every rebuild and the
        // queue grew its internal buffer up to 4 KB during the flood,
        // which accumulated gen-0 pressure under heavy excavation.
        private Queue<(int x, int y)>? _bfsScratch;

        // v0.3.36 (B.10) — mutation logs are dictionaries keyed by tile coord
        // for O(1) update + bounded memory. The previous List-based version
        // did `RemoveAll(...)` on every write — O(N) per mutation, O(N²)
        // amortised on a long-running game with active excavation. At the
        // planned 1000-smurf colony size with sustained work, the lists
        // could grow into the tens of thousands of entries and dominate
        // mutation cost. Dict gives constant-time last-write-wins.
        private readonly Dictionary<(int X, int Y), TerrainType>                  _terrainMutations = new();
        private readonly Dictionary<(int X, int Y), (byte Yield, ushort Regrow)>  _vegMutations     = new();

        // Fired when a terrain tile's color changes (renderer updates the pixel).
        public event Action<int, int, TerrainType>? TerrainChanged;

        // Fired when a vegetation slot's visual state changes (renderer calls QueueRedraw).
        public event Action<int, int>? VegetationChanged;

        // Phase 3.21 — fired when a tile's DesignatedForExcavation or
        // DesignatedForGather flag changes. The DesignationOverlay subscribes
        // and calls QueueRedraw so the player gets immediate visual feedback
        // when a box-drag commits.
        public event Action<int, int>? DesignationChanged;

        // v0.3.33 (B.1 / B.7) — denormalised designation indexes. The per-tile
        // flags stay the source of truth (the overlay still reads them via
        // `Get`), but BehaviorSystem iterates these sets instead of doing a
        // 51×51 radial scan — turns nearest-designation lookup from O(R²)
        // into O(N) where N is the live designation count. With ~1000 active
        // designations and 20 smurfs at 60 Hz that's a ~60× reduction in
        // tile-reads per second.
        //
        // `_claims` (B.7) tracks which smurf is currently routing toward each
        // designated tile so a second smurf scanning for work skips already-
        // taken tiles. Prevents the "every smurf piles onto the same berry"
        // failure mode. The Guid is the claimer's `Smurf.Id`.
        private readonly HashSet<(int X, int Y)> _excavateDesignations = new();
        private readonly HashSet<(int X, int Y)> _gatherDesignations   = new();
        // v0.3.38 — indexes for the new Chop Wood / Cut Plants orders. Same
        // pattern as Gather / Excavate: kept in lock-step with the per-tile
        // flags so BehaviorSystem.FindNearest* lookups stay O(N) over live
        // designations instead of O(R²) radial scans.
        private readonly HashSet<(int X, int Y)> _chopWoodDesignations = new();
        private readonly HashSet<(int X, int Y)> _cutDesignations      = new();
        private readonly Dictionary<(int X, int Y), Guid> _claims = new();
        private readonly object _designationsLock = new();

        // v0.5.0 (Phase 5A — rimport N1) — Stockpile zones. Player-painted
        // cell sets for haul destinations. `_stockpileZones` keyed by
        // monotonic int ID; `_cellZoneId[ty * Width + tx]` gives the zone
        // ID owning a given cell (0 = no zone). Both protected by
        // `_designationsLock` since stockpile paint flows through the same
        // designation infrastructure as the v0.3.21 Gather/Excavate paint.
        //
        // Per-cell lookup is the RimWorld `SlotGroupGrid[,]` pattern — gives
        // O(1) "which zone owns this tile?" for the inspector + haul-cell
        // collision check, vs the O(N zones × M cells/zone) walk we'd need
        // without it.
        private readonly Dictionary<int, StockpileZone> _stockpileZones = new();
        private int[]                                   _cellZoneId      = System.Array.Empty<int>();
        private int                                     _nextStockpileId = 1;
        public event Action<int, int>?                  StockpileChanged;

        // v0.5.14 (Phase 5C — rimport.md N18) — buried-treasure markers.
        // Tiles flagged here are impassable Boulder-class terrain that drop
        // an additional Trinket/Magic item beyond the standard StoneBlock
        // when excavated. Sam: "what will I find under there?" Excavation
        // hook in BehaviorSystem reads HasBuriedTreasure / RemoveBuriedTreasure.
        private readonly System.Collections.Generic.HashSet<(int X, int Y)> _buriedTreasure = new();

        // v0.5.14 (Phase 5C — rimport.md N19) — wildlife spawn-point stubs.
        // Generation places these per-biome; the full Animal system in
        // Phase 9 (rimport.md N12) will consume the list to populate
        // creatures. Until then the list is just generation output.
        private readonly System.Collections.Generic.List<AnimalSpawnPoint> _animalSpawns = new();

        // v0.4.2 — per-Boulder stone subtype, assigned at generation time.
        // Maps (x,y) → MaterialKey so excavation knows exactly which
        // stone family the player is mining (Granite / Limestone / Marble /
        // Obsidian / Quartz / Magicstone / MagicCrystal). The renderer
        // also reads this to draw a stone-typed pixel tint. Tiles
        // without an entry fall back to Granite by default.
        private readonly Dictionary<(int X, int Y), MaterialKey> _tileStone = new();

        // v0.4.2 — on-tile dropped items. Replaces the v0.3.46 "items
        // teleport straight to colony pool" path. Every Gather / Excavate /
        // Chop / Cut now drops a physical item on the work tile; a Haul
        // task picks it up later and carries it to the stockpile (Phase 5
        // zones — until then we use the colony spawn cluster centre as
        // the destination so the colony pool fills as items get hauled).
        // List<Item> per tile because multiple items can pile on the
        // same square (e.g. MagicCrystal excavation drops both a
        // StoneBlock and a CrystalShard).
        private readonly Dictionary<(int X, int Y), List<Item>> _droppedItems = new();
        public event Action<int, int>? ItemsChanged;

        // v0.4.15 — dedicated lock for `_droppedItems`. Previously items
        // shared `_designationsLock` with designations + claims, so the
        // renderer's `RebuildDrawCache` call to `EnumerateDroppedItems`
        // blocked every concurrent `FindNearestExcavate` on the sim
        // thread — and with heavy excavation that produced a microstutter
        // on every item drop. The two collections never participate in
        // the same atomic operation, so the split is safe.
        private readonly object _itemsLock = new();

        // v0.4.50 — running tallies of dropped-item Quantity per
        // ItemKind and per (Kind, Material.Family). Maintained alongside
        // _droppedItems under _itemsLock so reads stay consistent. The
        // HUD's ColonyResources getters (Food/Stone/Wood/MagicEssence)
        // used to call EnumerateDroppedItems() per access — which
        // allocates a (int,int,Item[])[] of size N plus an Item[] per
        // tile, four times per Snapshot call, twice per frame (ResourceHUD
        // + HUDController), so ~8 full snapshot allocations per frame
        // were burned just to compute totals. With 50 smurfs running
        // gather/excavate the dropped-tile count routinely hits 200+,
        // turning the per-frame allocation storm into a measurable
        // main-thread stutter. Caching the totals reduces those getters
        // to a single dictionary lookup under the lock.
        private readonly Dictionary<ItemKind, int> _droppedKindTotals = new();
        private readonly Dictionary<(ItemKind Kind, string Family), int> _droppedFamilyTotals = new();

        // Caller MUST hold _itemsLock. Sign is +1 on add, -1 on remove.
        // Quantity zero-bookkeeping is fine: keys can stay in the dict
        // with a zero value (rare in steady state since gather drops are
        // bursty, not balanced).
        private void AdjustDroppedTotals(Item item, int sign)
        {
            int qty = item.Quantity * sign;
            if (qty == 0) return;
            _droppedKindTotals.TryGetValue(item.Kind, out int kTotal);
            _droppedKindTotals[item.Kind] = kTotal + qty;
            string family = item.Material.Family;
            if (!string.IsNullOrEmpty(family))
            {
                var fkey = (item.Kind, family);
                _droppedFamilyTotals.TryGetValue(fkey, out int fTotal);
                _droppedFamilyTotals[fkey] = fTotal + qty;
            }
        }

        // O(1) totals read for the HUD aggregator. Lock acquisition is
        // the only real cost — no allocations, no scans.
        public int SumDroppedByKind(ItemKind kind)
        {
            lock (_itemsLock)
            {
                _droppedKindTotals.TryGetValue(kind, out int t);
                return t;
            }
        }
        public int SumDroppedByFamily(ItemKind kind, string family)
        {
            if (string.IsNullOrEmpty(family)) return 0;
            lock (_itemsLock)
            {
                _droppedFamilyTotals.TryGetValue((kind, family), out int t);
                return t;
            }
        }

        // v0.4.30 — DF/RimWorld-style stockpile rules. A tile is type-locked
        // by the (Kind, SubType, Material) of its first occupant; subsequent
        // drops of the same triple stack/coexist on it. Different types
        // overflow to the nearest compatible tile in a 5-tile spiral.
        // Total Quantity per tile is capped at MaxStackPerTile = 250
        // (planned storage furniture later raises this cap per chest /
        // shelf — see roadmap §Storage). Quality / State can vary within
        // a tile because CanStackWith already discriminates them, so
        // Fine and Normal Granite live as separate Item entries on the
        // same Granite tile.
        public const int MaxStackPerTile = 250;

        public void DropItem(Item item)
        {
            if (item == null || item.TilePos == null) return;
            int tx = (int)(item.TilePos.Value.X / TileSize);
            int ty = (int)(item.TilePos.Value.Y / TileSize);
            if (!InBounds(tx, ty)) return;

            // Try the requested tile first; if it's incompatible (different
            // type) or at the cap, spiral outward for the nearest suitable
            // passable tile and re-target the item there. Updates
            // item.TilePos so the renderer + save see the actual landing
            // tile, not the requested one.
            if (TryDropOnTile(item, tx, ty)) return;

            var alt = FindStockpileTileNear(item, tx, ty, maxRadius: 5);
            if (alt.HasValue)
            {
                item.TilePos = new Vector2(
                    alt.Value.X * TileSize + TileSize * 0.5f,
                    alt.Value.Y * TileSize + TileSize * 0.5f);
                if (TryDropOnTile(item, alt.Value.X, alt.Value.Y)) return;
            }

            // Last-ditch fallback: cap-bust on the requested tile rather
            // than lose the item. Logged as a warning so we can spot
            // saturation hot spots when the player extends the dig site.
            ForceDropOnTile(item, tx, ty);
            GD.PushWarning($"DropItem fallback: no compatible tile within 5 of ({tx},{ty}) for {item.Kind}/{item.SubType} — cap-busted");
        }

        // True iff (tx, ty) is currently allowed to receive `item` under
        // the single-type + 250-cap rules. Stacking happens here when
        // possible; same-type-different-quality items are appended as
        // separate stacks on the same tile.
        private bool TryDropOnTile(Item item, int tx, int ty)
        {
            lock (_itemsLock)
            {
                if (!_droppedItems.TryGetValue((tx, ty), out var list))
                {
                    _droppedItems[(tx, ty)] = list = new List<Item> { item };
                    AdjustDroppedTotals(item, +1);
                    ItemsChanged?.Invoke(tx, ty);
                    return true;
                }
                if (!IsCompatibleType(list, item)) return false;
                if (SumQuantity(list) + item.Quantity > MaxStackPerTile) return false;
                if (ItemKindMeta.IsStackable(item.Kind))
                {
                    foreach (var existing in list)
                    {
                        if (existing.CanStackWith(item))
                        {
                            // Absorb mutates existing.Quantity += item.Quantity.
                            // Bump totals by item.Quantity (the delta), not by
                            // the post-absorb existing.Quantity.
                            AdjustDroppedTotals(item, +1);
                            existing.Absorb(item);
                            ItemsChanged?.Invoke(tx, ty);
                            return true;
                        }
                    }
                }
                list.Add(item);
                AdjustDroppedTotals(item, +1);
                ItemsChanged?.Invoke(tx, ty);
                return true;
            }
        }

        // Tile is type-locked by the FIRST item's (Kind, SubType, Material).
        // Quality / State are allowed to differ — they sit as separate
        // stacks. Empty tiles are always compatible.
        private static bool IsCompatibleType(List<Item> list, Item item)
        {
            if (list.Count == 0) return true;
            var first = list[0];
            return first.Kind == item.Kind
                && first.SubType == item.SubType
                && first.Material == item.Material;
        }

        private static int SumQuantity(List<Item> list)
        {
            int total = 0;
            for (int i = 0; i < list.Count; i++) total += list[i].Quantity;
            return total;
        }

        // Spiral search outward from (sx, sy) for the nearest passable
        // tile that can accept `item` under the type + cap rules. Returns
        // null if none found within maxRadius. Walks rings in order so
        // the closest qualifying tile wins (Manhattan-style ring; not
        // strictly Euclidean closest but within 1-2 tiles of optimal,
        // and avoids a per-call sort).
        private (int X, int Y)? FindStockpileTileNear(Item item, int sx, int sy, int maxRadius)
        {
            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int nx = sx + dx, ny = sy + dy;
                    if (!InBounds(nx, ny)) continue;
                    if (!IsPassable(nx, ny)) continue;
                    lock (_itemsLock)
                    {
                        if (!_droppedItems.TryGetValue((nx, ny), out var list))
                            return (nx, ny);
                        if (!IsCompatibleType(list, item)) continue;
                        if (SumQuantity(list) + item.Quantity > MaxStackPerTile) continue;
                        return (nx, ny);
                    }
                }
            }
            return null;
        }

        // Last-resort drop that skips the type + cap checks. Called only
        // when the spiral search fails to find a suitable tile within
        // maxRadius — better to bust the cap than to lose the item.
        private void ForceDropOnTile(Item item, int tx, int ty)
        {
            lock (_itemsLock)
            {
                if (!_droppedItems.TryGetValue((tx, ty), out var list))
                    _droppedItems[(tx, ty)] = list = new List<Item>();
                list.Add(item);
                AdjustDroppedTotals(item, +1);
            }
            ItemsChanged?.Invoke(tx, ty);
        }

        // v0.5.0 (Phase 5A) — public hook for Forbid/Allow flag toggles
        // and similar item-state edits that don't add or remove items.
        // Lets ItemDropOverlay re-snapshot so the visual state catches up
        // (badge variant, future forbid-X icon).
        public void NotifyItemsChanged(int x, int y)
        {
            ItemsChanged?.Invoke(x, y);
        }

        public bool RemoveItem(Item item)
        {
            if (item == null || item.TilePos == null) return false;
            int tx = (int)(item.TilePos.Value.X / TileSize);
            int ty = (int)(item.TilePos.Value.Y / TileSize);
            lock (_itemsLock)
            {
                if (!_droppedItems.TryGetValue((tx, ty), out var list)) return false;
                bool removed = list.Remove(item);
                if (removed) AdjustDroppedTotals(item, -1);
                if (list.Count == 0) _droppedItems.Remove((tx, ty));
                if (removed) ItemsChanged?.Invoke(tx, ty);
                return removed;
            }
        }

        // v0.4.33 — corpse rot. Called from SimulationCore on the day
        // boundary so corpses don't sit on the map forever. ~7 in-game
        // days from a fresh body to a fully-decayed empty tile, matching
        // RimWorld's rough timeline. Drops the AvgCondition by ~14 per
        // day (so 100 → 0 over ~7.1 days), updates State as it crosses
        // the Fresh/Stale/Spoiled thresholds for the hover label, and
        // removes corpses that hit 0 condition entirely. Equipment dropped
        // alongside (separate Item entries on the same tile, see
        // SimulationCore.DropCorpseGear) is unaffected — those decay on
        // their own material schedule via the colony Inventory path when
        // they're hauled later.
        //
        // Map item decay is intentionally a separate walk from the colony
        // pool's TickDeterioration. The two stores never share Item
        // references (HaulSystem's deposit either uses one or the other);
        // mixing them through a single loop would either double-decay or
        // leak per-tile bookkeeping into the colony Inventory class.
        public void TickCorpseDecay(long globalTick, float daysElapsed)
        {
            if (daysElapsed <= 0f) return;
            const float CorpseDecayPerDay = 14f;
            var dirty = new List<(int, int)>();
            // v0.4.35 — accumulate tile coords where corpses just hit 0
            // condition so bones can be spawned OUTSIDE the lock. Spawning
            // inside the lock would call DropItem → TryDropOnTile → would
            // mutate _droppedItems while we're still iterating it (foreach
            // over the dict).
            var bonesToSpawn = new List<(int X, int Y)>();
            lock (_itemsLock)
            {
                foreach (var kv in _droppedItems)
                {
                    var list = kv.Value;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var it = list[i];
                        if (it.Kind != ItemKind.Corpse) continue;
                        it.AvgCondition -= CorpseDecayPerDay * daysElapsed;
                        if (it.AvgCondition < 0f) it.AvgCondition = 0f;
                        // Fresh > 70, Stale 40-70, Spoiled <= 40 — matches
                        // ItemStateMeta's food thresholds so the hover line
                        // shows "Fresh / Stale / Spoiled" through the same
                        // state enum.
                        if      (it.AvgCondition >= 70f) it.State = ItemState.Fresh;
                        else if (it.AvgCondition >= 40f) it.State = ItemState.Stale;
                        else                              it.State = ItemState.Spoiled;
                        if (it.AvgCondition <= 0f)
                        {
                            AdjustDroppedTotals(it, -1);
                            list.RemoveAt(i);
                            dirty.Add(kv.Key);
                            bonesToSpawn.Add(kv.Key);
                        }
                    }
                }
                // Clean empty lists from the dict.
                for (int i = 0; i < dirty.Count; i++)
                {
                    var key = dirty[i];
                    if (_droppedItems.TryGetValue(key, out var l) && l.Count == 0)
                        _droppedItems.Remove(key);
                }
            }
            // v0.4.35 — leave bones behind. One Material/Bone item per
            // fully-decayed corpse so the death site has a lasting trace
            // (and so the player can haul / trade bones once Phase 9
            // husbandry / Phase 11 trade open the loop). Drops route
            // through the standard DropItem pipeline so v0.4.30 stack
            // rules apply: the bone may overflow to an adjacent tile if
            // the corpse site is now occupied by a different type.
            for (int i = 0; i < bonesToSpawn.Count; i++)
            {
                var (bx, by) = bonesToSpawn[i];
                var bones = new Item
                {
                    Kind          = ItemKind.Material,
                    SubType       = "Bones",
                    Material      = new MaterialKey("Bone", "Generic"),
                    Quality       = Quality.Normal,
                    State         = ItemState.Fresh,
                    Quantity      = 1,
                    AvgCondition  = 100f,
                    DurabilityCap = 100f,
                    AvgBirthTick  = globalTick,
                    TilePos       = new Vector2(
                        bx * TileSize + TileSize * 0.5f,
                        by * TileSize + TileSize * 0.5f),
                };
                DropItem(bones);
            }
            for (int i = 0; i < dirty.Count; i++)
                ItemsChanged?.Invoke(dirty[i].Item1, dirty[i].Item2);
        }

        // Returns a snapshot of items on a given tile (or empty). Called
        // by Haul lookups + the renderer; both call paths tolerate stale
        // reads — the renderer redraws on ItemsChanged, the lookup
        // tolerates returning a list that's about to be picked up by
        // another smurf (Haul does an existence-and-claim re-check
        // before deciding to pick up).
        public IReadOnlyList<Item> GetItemsOnTile(int x, int y)
        {
            lock (_itemsLock)
            {
                if (!_droppedItems.TryGetValue((x, y), out var list))
                    return Array.Empty<Item>();
                return list.ToArray();
            }
        }

        // v0.4.51 — non-allocating, radius-filtered walk for sim-thread
        // callers (haul target selection). The previous path forced every
        // hauler to call `EnumerateDroppedItems` which snapshotted the
        // entire dict into a `(int,int,Item[])[]` and a per-tile
        // `Item[]` — at 500 dropped tiles × 50 smurfs × 1+ haul scan per
        // task selection, that was the dominant main-thread + sim-thread
        // allocation source under heavy gather/excavate. This method holds
        // the items lock for the whole walk (typically <100 μs at 500
        // tiles) and passes the live `List<Item>` to the callback — safe
        // because sim thread is the sole writer of `_droppedItems` and
        // the callback is bounded to read-only access. The `radiusSq`
        // gate cuts the inner work by ~10× since most dropped tiles are
        // outside any given smurf's 32-tile radius.
        public void ForEachDroppedInRadius(int sx, int sy, int radiusSq,
            System.Action<int, int, IReadOnlyList<Item>> visit)
        {
            lock (_itemsLock)
            {
                foreach (var kv in _droppedItems)
                {
                    int dx = kv.Key.X - sx, dy = kv.Key.Y - sy;
                    if (dx * dx + dy * dy > radiusSq) continue;
                    visit(kv.Key.X, kv.Key.Y, kv.Value);
                }
            }
        }

        // Walks every tile-with-items in the map. Used by Haul to find
        // the nearest pickup target. Caller can break early once it has
        // a satisfactory match.
        public IEnumerable<(int X, int Y, IReadOnlyList<Item> Items)> EnumerateDroppedItems()
        {
            // Snapshot the keys + lists under the lock, then yield outside
            // so the caller can take its time iterating without blocking
            // sim-thread writes.
            (int X, int Y, Item[] Items)[] snap;
            lock (_itemsLock)
            {
                snap = new (int, int, Item[])[_droppedItems.Count];
                int i = 0;
                foreach (var kv in _droppedItems)
                {
                    snap[i++] = (kv.Key.X, kv.Key.Y, kv.Value.ToArray());
                }
            }
            for (int i = 0; i < snap.Length; i++)
                yield return (snap[i].X, snap[i].Y, snap[i].Items);
        }

        public int DroppedTileCount
        {
            get { lock (_itemsLock) return _droppedItems.Count; }
        }

        // v0.4.15 — lightweight snapshot for the `ItemDropOverlay`
        // renderer. The previous `EnumerateDroppedItems` path allocated
        // an `Item[]` per dropped tile inside the lock — with ~200 tiles
        // of dropped stone after a big excavation that was 200 small
        // arrays per redraw, and every drop event scheduled a redraw.
        // This summary returns only the data the overlay actually
        // draws: primary item, total quantity, stack count. One List
        // allocation per snapshot regardless of tile count.
        public readonly struct DroppedTileSummary
        {
            public readonly int X;
            public readonly int Y;
            public readonly Item Primary;
            public readonly int TotalCount;
            public readonly int StackCount;

            public DroppedTileSummary(int x, int y, Item primary, int totalCount, int stackCount)
            {
                X = x; Y = y; Primary = primary;
                TotalCount = totalCount; StackCount = stackCount;
            }
        }

        public List<DroppedTileSummary> SnapshotDroppedItemSummaries()
        {
            var result = new List<DroppedTileSummary>();
            lock (_itemsLock)
            {
                result.Capacity = _droppedItems.Count;
                foreach (var kv in _droppedItems)
                {
                    var items = kv.Value;
                    int n = items.Count;
                    if (n == 0) continue;
                    Item primary = items[0];
                    int totalCount = primary.Quantity;
                    int maxQ = primary.Quantity;
                    for (int i = 1; i < n; i++)
                    {
                        int q = items[i].Quantity;
                        if (q > maxQ) { primary = items[i]; maxQ = q; }
                        totalCount += q;
                    }
                    result.Add(new DroppedTileSummary(kv.Key.X, kv.Key.Y, primary, totalCount, n));
                }
            }
            return result;
        }

        // v0.4.7 (bugreport B-1) — main-thread snapshot of every item on
        // every tile, for save. Returns a flat list of (X, Y, Item)
        // tuples. Locks once; safe to call at save time from the
        // sim-driving thread or via the registered SimulationManager.
        public List<(int X, int Y, Item Item)> SnapshotDroppedItems()
        {
            var result = new List<(int, int, Item)>();
            lock (_itemsLock)
            {
                foreach (var kv in _droppedItems)
                {
                    foreach (var it in kv.Value)
                        result.Add((kv.Key.X, kv.Key.Y, it));
                }
            }
            return result;
        }

        // v0.4.2 — set / query the stone subtype assigned to a Boulder
        // tile at generation time. Renderer + excavate handler both read
        // GetTileStone; the generator calls SetTileStone once per Boulder
        // tile after the initial terrain pass.
        public void SetTileStone(int x, int y, MaterialKey key)
        {
            if (!InBounds(x, y)) return;
            _tileStone[(x, y)] = key;
        }

        public MaterialKey? GetTileStone(int x, int y) =>
            _tileStone.TryGetValue((x, y), out var k) ? k : null;

        // v0.4.7 (bugreport B-7) — snapshot every (x, y) → stone-subtype
        // entry for save. Only tiles whose terrain is still Boulder
        // matter; excavated tiles are mud and the entry is moot.
        public List<(int X, int Y, string MaterialSubType)> SnapshotStoneVariation()
        {
            var result = new List<(int, int, string)>(_tileStone.Count);
            foreach (var kv in _tileStone)
            {
                if (!InBounds(kv.Key.X, kv.Key.Y)) continue;
                if (_tiles[kv.Key.X, kv.Key.Y].Terrain != TerrainType.Boulder) continue;
                result.Add((kv.Key.X, kv.Key.Y, kv.Value.SubType));
            }
            return result;
        }

        public LocalMap(int width = DefaultWidth, int height = DefaultHeight)
        {
            Width       = width;
            Height      = height;
            _tiles      = new LocalTile[width, height];
            _vegetation = new VegetationSlot[width, height];
            _passable   = new bool[width * height];
            _regionId   = new ushort[width * height];
            _cellZoneId = new int[width * height];   // v0.5.0 Phase 5A — stockpile zone ownership grid
            // _tiles starts default-zeroed (Terrain=Water, Passable=false).
            // LocalMapGenerator will populate via Set() which routes through
            // the public mutators; the cache stays in sync from there.
        }

        // ── DF-style region connectivity (v0.4.13) ────────────────────────────────

        // Flood-fills `_regionId` from every passable tile using the same
        // 8-connected + diagonal-cut-corner rule as `Pathfinder.FindPath`,
        // so a region-id match is a true "smurfs can walk between these"
        // guarantee. Impassable tiles stay at 0. Region ids start at 1 and
        // are reused on every rebuild.
        private void RebuildRegions()
        {
            int W = Width, H = Height;
            Array.Clear(_regionId, 0, _regionId.Length);
            ushort nextId = 0;
            // v0.4.14 — reused scratch queue. First call allocates;
            // subsequent rebuilds reuse the same Queue (Clear is O(1)
            // and the internal buffer holds onto its grown capacity).
            _bfsScratch ??= new Queue<(int x, int y)>(Math.Min(W * H, 4096));
            var queue = _bfsScratch;
            queue.Clear();
            for (int sy = 0; sy < H; sy++)
            for (int sx = 0; sx < W; sx++)
            {
                int sIdx = sy * W + sx;
                if (!_passable[sIdx] || _regionId[sIdx] != 0) continue;
                nextId++;
                if (nextId == 0)        // ushort overflow guard — should never trigger at 64k tiles
                    nextId = ushort.MaxValue;
                _regionId[sIdx] = nextId;
                queue.Enqueue((sx, sy));
                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    // 8-neighbour expansion with cut-corner check.
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cx + dx, ny = cy + dy;
                        if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                        int nIdx = ny * W + nx;
                        if (_regionId[nIdx] != 0) continue;
                        if (!_passable[nIdx]) continue;
                        if (dx != 0 && dy != 0)
                        {
                            // Diagonal step requires both orthogonals passable
                            // — must match Pathfinder's rule exactly or A*
                            // would refuse a step the region check just promised.
                            if (!_passable[cy * W + (cx + dx)]) continue;
                            if (!_passable[(cy + dy) * W + cx]) continue;
                        }
                        _regionId[nIdx] = nextId;
                        queue.Enqueue((nx, ny));
                    }
                }
            }
            _regionCount = nextId;
            _regionsDirty = false;
        }

        private void EnsureRegions()
        {
            if (!_regionsDirty) return;
            // v0.4.14 — during a sim tick the rebuild is suppressed; the
            // tick uses the data BeginTick produced. Mutations within the
            // tick still set `_regionsDirty = true`, so the next
            // BeginTick (or any non-tick caller) sees a fresh rebuild.
            if (_freezeRegionRebuilds) return;
            lock (_regionLock)
            {
                if (_regionsDirty && !_freezeRegionRebuilds) RebuildRegions();
            }
        }

        // v0.4.14 — explicit tick boundaries for the sim thread.
        // `BehaviorSystem.Tick` calls `BeginTick()` once before iterating
        // smurfs and `EndTick()` once after. Between them the region
        // graph is frozen against further rebuilds even if excavation
        // mutations dirty it. The full rebuild happens at most once per
        // tick regardless of how many tiles flipped. On a 240×150 map
        // with 17 active diggers this turned a 17× rebuild storm into a
        // single ~750 µs pass.
        public void BeginTick()
        {
            _freezeRegionRebuilds = false;
            EnsureRegions();
            _freezeRegionRebuilds = true;
        }

        public void EndTick()
        {
            _freezeRegionRebuilds = false;
        }

        // Region id for a tile (0 = impassable or OOB). Forces a rebuild
        // when the dirty flag is set. Cheap when clean: single bool read +
        // array index.
        public ushort GetRegion(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            EnsureRegions();
            return _regionId[y * Width + x];
        }

        // O(1) DF-style reachability: two passable tiles in the same region
        // are walk-connected; mismatch (or either being impassable / OOB)
        // means no path exists.
        public bool AreReachable(int x0, int y0, int x1, int y1)
        {
            ushort a = GetRegion(x0, y0);
            return a != 0 && a == GetRegion(x1, y1);
        }

        // "Can the smurf at (sx, sy) actually reach a tile of work at
        // (tx, ty)?" Handles both passable work (gather, cut, chop on a
        // passable tile) and impassable work (excavate Boulder; chop
        // LargeMushroom whose cap is still up). For the impassable case
        // any 8-neighbour passable tile in the same region as the smurf
        // counts as reachable — the smurf can stand there and act on the
        // wall. Uses cached region data, so it's an O(1) test plus at most
        // 8 neighbour lookups.
        public bool IsWorkReachable(int sx, int sy, int tx, int ty)
        {
            if ((uint)tx >= (uint)Width || (uint)ty >= (uint)Height) return false;
            EnsureRegions();

            // v0.4.16 — robust wall-stuck fallback. Collect the smurf's
            // *set* of candidate region ids: their own tile if passable,
            // otherwise every distinct non-zero region adjacent to them.
            // The previous version used a single `startRid` variable and
            // an inner-only `break` in the fallback, so if the smurf had
            // SimPos-drifted into a wall whose 8-neighbours straddled
            // two caverns the last-checked neighbour's region won and
            // the smurf wrongly blacklisted reachable targets in the
            // other region. With the multi-region set, *any* neighbour
            // region the smurf could step onto satisfies reachability.
            ushort r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            int regionCount = 0;

            void AddRegion(ushort r)
            {
                if (r == 0 || regionCount >= 4) return;
                if (regionCount > 0 && r0 == r) return;
                if (regionCount > 1 && r1 == r) return;
                if (regionCount > 2 && r2 == r) return;
                switch (regionCount)
                {
                    case 0: r0 = r; break;
                    case 1: r1 = r; break;
                    case 2: r2 = r; break;
                    case 3: r3 = r; break;
                }
                regionCount++;
            }

            ushort selfRid = GetRegion(sx, sy);
            if (selfRid != 0)
            {
                AddRegion(selfRid);
            }
            else
            {
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    AddRegion(GetRegion(sx + dx, sy + dy));
                }
                if (regionCount == 0) return false;
            }

            int tIdx = ty * Width + tx;
            if (_passable[tIdx])
            {
                ushort tRid = _regionId[tIdx];
                return Matches(tRid);
            }
            // Impassable target → check 8 neighbours for a passable cell
            // in any of the smurf's candidate regions.
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                int nIdx = ny * Width + nx;
                if (!_passable[nIdx]) continue;
                if (Matches(_regionId[nIdx])) return true;
            }
            return false;

            bool Matches(ushort r) =>
                (regionCount > 0 && r0 == r) ||
                (regionCount > 1 && r1 == r) ||
                (regionCount > 2 && r2 == r) ||
                (regionCount > 3 && r3 == r);
        }

        // ── Tile accessors ─────────────────────────────────────────────────────────

        public LocalTile Get(int x, int y)               => _tiles[x, y];

        // v0.3.39 (O-M.2) — Set keeps the flat passability cache in sync.
        // Generator code path lands here on initial map construction.
        public void Set(int x, int y, LocalTile t)
        {
            _tiles[x, y] = t;
            int idx = y * Width + x;
            if (_passable[idx] != t.Passable) _regionsDirty = true;
            _passable[idx] = t.Passable;
        }

        public bool InBounds(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height;

        // v0.3.39 — uses the flat cache instead of `_tiles[x, y].Passable`
        // (which copied the entire 24-byte LocalTile struct just to read
        // one bool). Bounds-check stays explicit.
        public bool IsPassable(int x, int y) =>
            (uint)x < (uint)Width && (uint)y < (uint)Height && _passable[y * Width + x];

        // v0.4.18 — direct passability-array accessor for the Pathfinder
        // inner expansion loop, which queries up to 24 cells per node
        // expansion (8 neighbours × cardinal + cut-corner). Going through
        // `IsPassable` per call adds a virtual-call + double bounds check
        // on every probe; at 250 smurfs × frequent A* runs the overhead
        // showed up as the task-reassignment spikes the player reported.
        // Callers must respect bounds themselves (`Width`/`Height` properties).
        // Stable across the LocalMap's lifetime — same array reference for
        // every call, no resize.
        public bool[] PassableUnsafe => _passable;

        // v0.3.43 — cheap "is there any work the player has designated
        // anywhere on the map?" probe used by BehaviorSystem to decide
        // whether an idle-lingering smurf should re-evaluate. O(1) — just
        // counts across the four designation HashSets.
        public bool HasAnyDesignation() =>
               _excavateDesignations.Count > 0
            || _gatherDesignations  .Count > 0
            || _chopWoodDesignations.Count > 0
            || _cutDesignations     .Count > 0;

        // v0.5.9 — per-tile designation existence probes. Used by the
        // BehaviorSystem.IsTaskStillValid check (RimWorld JobDriver
        // FailOn-pattern equivalent) to bail out of a task whose
        // designation was cleared by another smurf, the player's Remove
        // tool, or task completion elsewhere. O(1) HashSet.Contains under
        // the designation lock for thread-safety with the painter +
        // ClearDesignationsAt mutators.
        public bool HasExcavateDesignation(int x, int y)
        {
            lock (_designationsLock) return _excavateDesignations.Contains((x, y));
        }
        public bool HasGatherDesignation(int x, int y)
        {
            lock (_designationsLock) return _gatherDesignations.Contains((x, y));
        }
        public bool HasChopWoodDesignation(int x, int y)
        {
            lock (_designationsLock) return _chopWoodDesignations.Contains((x, y));
        }
        public bool HasCutDesignation(int x, int y)
        {
            lock (_designationsLock) return _cutDesignations.Contains((x, y));
        }

        // v0.5.14 — buried-treasure marker accessors. Used by BehaviorSystem
        // GatherMaterial excavation case to drop a bonus Trinket alongside
        // the standard StoneBlock. SetBuriedTreasure called by
        // LocalMapGenerator.ScatterBuriedTreasure at gen time.
        public void SetBuriedTreasure(int x, int y)
        {
            if (!InBounds(x, y)) return;
            lock (_designationsLock) _buriedTreasure.Add((x, y));
        }
        public bool HasBuriedTreasure(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            lock (_designationsLock) return _buriedTreasure.Contains((x, y));
        }
        public void RemoveBuriedTreasure(int x, int y)
        {
            lock (_designationsLock) _buriedTreasure.Remove((x, y));
        }
        public int BuriedTreasureCount
        {
            get { lock (_designationsLock) return _buriedTreasure.Count; }
        }

        // v0.5.14 — wildlife spawn-point accessors. AddAnimalSpawn called by
        // LocalMapGenerator.ScatterAnimalSpawnPoints at gen time. Phase 9's
        // animal system will read SnapshotAnimalSpawns to populate creatures.
        public void AddAnimalSpawn(AnimalSpawnPoint p)
        {
            lock (_designationsLock) _animalSpawns.Add(p);
        }
        public System.Collections.Generic.List<AnimalSpawnPoint> SnapshotAnimalSpawns()
        {
            lock (_designationsLock)
            {
                return new System.Collections.Generic.List<AnimalSpawnPoint>(_animalSpawns);
            }
        }
        public int AnimalSpawnCount
        {
            get { lock (_designationsLock) return _animalSpawns.Count; }
        }

        // ── Vegetation accessors ───────────────────────────────────────────────────

        public VegetationSlot GetVegetation(int x, int y)                       => _vegetation[x, y];
        public void           SetVegetation(int x, int y, VegetationSlot slot)  => _vegetation[x, y] = slot;

        // ── Passability helper ─────────────────────────────────────────────────────

        public static bool IsPassableTerrain(TerrainType t) =>
            t != TerrainType.Water && t != TerrainType.Boulder &&
            t != TerrainType.DeadLog && t != TerrainType.LivingWood;
        // Shallows IS passable — smurfs wade through (slower; Phase 5
        // movement-cost work will dial in the actual speed multiplier).
        // The default IsPassableTerrain treats Shallows as passable
        // because it's NOT in the impassable list above.

        // Finds a cluster of passable tiles closest to the map's geometric centre,
        // used for initial smurf spawning so every level reliably has them visible
        // and grouped together rather than scattered (or hidden inside rock walls).
        // BFS spreads outward from centre; the first N passable tiles encountered
        // form the cluster. Returns fewer than N only if the map is so dense that
        // BFS exhausts all reachable tiles, which should never happen for any
        // landable biome but is handled defensively.
        public List<(int X, int Y)> FindSpawnCluster(int count)
        {
            var result  = new List<(int X, int Y)>(count);
            var visited = new bool[Width, Height];
            var queue   = new Queue<(int x, int y)>();
            int cx = Width / 2, cy = Height / 2;
            queue.Enqueue((cx, cy));
            visited[cx, cy] = true;

            while (queue.Count > 0 && result.Count < count)
            {
                var (x, y) = queue.Dequeue();
                if (IsPassable(x, y)) result.Add((x, y));
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!InBounds(nx, ny) || visited[nx, ny]) continue;
                    visited[nx, ny] = true;
                    queue.Enqueue((nx, ny));
                }
            }
            return result;
        }

        // ── Designation API (Phase 3.21 — player-issued tile flags) ───────────────

        // Mark/unmark a tile for excavation. Only Boulder / DeadLog / LivingWood
        // are valid targets — non-matching tiles are silently ignored so the
        // box-drag handler doesn't need to pre-filter.
        public void SetExcavationDesignation(int x, int y, bool on)
        {
            if (!InBounds(x, y)) return;
            var t = _tiles[x, y];
            if (on && t.Terrain != TerrainType.Boulder
                   && t.Terrain != TerrainType.DeadLog
                   && t.Terrain != TerrainType.LivingWood) return;
            bool changed = t.DesignatedForExcavation != on;
            if (!changed) return;
            // Setting Excavate clears any prior other-designation flags.
            bool clearedGather   = on && t.DesignatedForGather;
            bool clearedChop     = on && t.DesignatedForChopWood;
            bool clearedCut      = on && t.DesignatedForCut;
            if (clearedGather) t.DesignatedForGather   = false;
            if (clearedChop)   t.DesignatedForChopWood = false;
            if (clearedCut)    t.DesignatedForCut      = false;
            t.DesignatedForExcavation = on;
            _tiles[x, y] = t;
            lock (_designationsLock)
            {
                if (on) _excavateDesignations.Add((x, y));
                else
                {
                    _excavateDesignations.Remove((x, y));
                    _claims.Remove((x, y));
                }
                if (clearedGather) _gatherDesignations  .Remove((x, y));
                if (clearedChop)   _chopWoodDesignations.Remove((x, y));
                if (clearedCut)    _cutDesignations     .Remove((x, y));
            }
            DesignationChanged?.Invoke(x, y);
        }

        // Mark/unmark a tile for food-gathering. Valid targets: any tile whose
        // vegetation slot is present, not depleted, and yields food.
        public void SetGatherDesignation(int x, int y, bool on)
        {
            if (!InBounds(x, y)) return;
            var t = _tiles[x, y];
            if (on)
            {
                var veg = _vegetation[x, y];
                if (!veg.IsPresent || veg.IsDepleted) return;
                if (!IsFoodYielding(veg.Type)) return;
            }
            bool changed = t.DesignatedForGather != on;
            if (!changed) return;
            bool clearedExcavate = on && t.DesignatedForExcavation;
            bool clearedChop     = on && t.DesignatedForChopWood;
            bool clearedCut      = on && t.DesignatedForCut;
            if (clearedExcavate) t.DesignatedForExcavation = false;
            if (clearedChop)     t.DesignatedForChopWood   = false;
            if (clearedCut)      t.DesignatedForCut        = false;
            t.DesignatedForGather = on;
            _tiles[x, y] = t;
            lock (_designationsLock)
            {
                if (on) _gatherDesignations.Add((x, y));
                else
                {
                    _gatherDesignations.Remove((x, y));
                    _claims.Remove((x, y));
                }
                if (clearedExcavate) _excavateDesignations.Remove((x, y));
                if (clearedChop)     _chopWoodDesignations.Remove((x, y));
                if (clearedCut)      _cutDesignations     .Remove((x, y));
            }
            DesignationChanged?.Invoke(x, y);
        }

        // v0.3.38 — Chop Wood designation. Valid on vegetation slots that
        // yield Fungal Wood (LargeMushroom / LargeSandshroom / PalmShroom).
        // Behaviour mirrors SetGatherDesignation: mutual-exclusion with the
        // other designation types, index maintained, event fired.
        public void SetChopWoodDesignation(int x, int y, bool on)
        {
            if (!InBounds(x, y)) return;
            var t = _tiles[x, y];
            if (on)
            {
                var veg = _vegetation[x, y];
                if (!veg.IsPresent || veg.IsDepleted) return;
                if (!IsWoodYielding(veg.Type)) return;
            }
            bool changed = t.DesignatedForChopWood != on;
            if (!changed) return;
            bool clearedExcavate = on && t.DesignatedForExcavation;
            bool clearedGather   = on && t.DesignatedForGather;
            bool clearedCut      = on && t.DesignatedForCut;
            if (clearedExcavate) t.DesignatedForExcavation = false;
            if (clearedGather)   t.DesignatedForGather     = false;
            if (clearedCut)      t.DesignatedForCut        = false;
            t.DesignatedForChopWood = on;
            _tiles[x, y] = t;
            lock (_designationsLock)
            {
                if (on) _chopWoodDesignations.Add((x, y));
                else
                {
                    _chopWoodDesignations.Remove((x, y));
                    _claims.Remove((x, y));
                }
                if (clearedExcavate) _excavateDesignations.Remove((x, y));
                if (clearedGather)   _gatherDesignations  .Remove((x, y));
                if (clearedCut)      _cutDesignations     .Remove((x, y));
            }
            DesignationChanged?.Invoke(x, y);
        }

        // v0.3.38 — Cut Plants designation. Valid on ANY non-depleted
        // vegetation. No resource drop — purely clears the vegetation slot.
        public void SetCutDesignation(int x, int y, bool on)
        {
            if (!InBounds(x, y)) return;
            var t = _tiles[x, y];
            if (on)
            {
                var veg = _vegetation[x, y];
                if (!veg.IsPresent || veg.IsDepleted) return;
            }
            bool changed = t.DesignatedForCut != on;
            if (!changed) return;
            bool clearedExcavate = on && t.DesignatedForExcavation;
            bool clearedGather   = on && t.DesignatedForGather;
            bool clearedChop     = on && t.DesignatedForChopWood;
            if (clearedExcavate) t.DesignatedForExcavation = false;
            if (clearedGather)   t.DesignatedForGather     = false;
            if (clearedChop)     t.DesignatedForChopWood   = false;
            t.DesignatedForCut = on;
            _tiles[x, y] = t;
            lock (_designationsLock)
            {
                if (on) _cutDesignations.Add((x, y));
                else
                {
                    _cutDesignations.Remove((x, y));
                    _claims.Remove((x, y));
                }
                if (clearedExcavate) _excavateDesignations.Remove((x, y));
                if (clearedGather)   _gatherDesignations  .Remove((x, y));
                if (clearedChop)     _chopWoodDesignations.Remove((x, y));
            }
            DesignationChanged?.Invoke(x, y);
        }

        // Yields-Fungal-Wood filter, mirroring IsFoodYielding's role for
        // Gather. Used as the validity check for Chop Wood designations and
        // for the visual overlay's glyph selection.
        public static bool IsWoodYielding(VegetationType v) => v switch
        {
            VegetationType.LargeMushroom    => true,
            VegetationType.LargeSandshroom  => true,
            VegetationType.PalmShroom       => true,
            _                               => false,
        };

        // Wipes every designation flag on a tile. Used by the Remove brush
        // and by ApplyTaskEffect after a successful harvest / excavation.
        public void ClearDesignationsAt(int x, int y)
        {
            if (!InBounds(x, y)) return;
            var t = _tiles[x, y];
            if (!t.DesignatedForExcavation && !t.DesignatedForGather
                && !t.DesignatedForChopWood && !t.DesignatedForCut) return;
            t.DesignatedForExcavation = false;
            t.DesignatedForGather     = false;
            t.DesignatedForChopWood   = false;
            t.DesignatedForCut        = false;
            _tiles[x, y] = t;
            lock (_designationsLock)
            {
                _excavateDesignations.Remove((x, y));
                _gatherDesignations  .Remove((x, y));
                _chopWoodDesignations.Remove((x, y));
                _cutDesignations     .Remove((x, y));
                _claims.Remove((x, y));
            }
            DesignationChanged?.Invoke(x, y);
        }

        // ── v0.5.0 (Phase 5A) Stockpile zone API ────────────────────────────────

        // Adds (x, y) to a stockpile zone. If `extendZoneId` is provided AND
        // exists, the cell joins that zone; otherwise a new zone is created
        // with default settings (Normal priority, accept-all kinds).
        // Idempotent: cells already in any zone are silently skipped.
        // Returns the resulting zone ID (existing or newly-created).
        // Refuses to paint on impassable tiles (water, boulders) — items
        // can't be hauled to a wall.
        public int SetStockpileCell(int x, int y, int extendZoneId = 0)
        {
            if (!InBounds(x, y)) return 0;
            if (!IsPassable(x, y)) return 0;
            int idx = y * Width + x;
            int existing = _cellZoneId[idx];
            if (existing != 0) return existing;   // already part of a zone

            lock (_designationsLock)
            {
                StockpileZone zone;
                if (extendZoneId > 0 && _stockpileZones.TryGetValue(extendZoneId, out var ez))
                {
                    zone = ez;
                }
                else
                {
                    int id = _nextStockpileId++;
                    zone = new StockpileZone(id);
                    _stockpileZones[id] = zone;
                }
                zone.Cells.Add((x, y));
                _cellZoneId[idx] = zone.Id;
                StockpileChanged?.Invoke(x, y);
                return zone.Id;
            }
        }

        // Removes (x, y) from any stockpile zone it belongs to. Empty zones
        // are deleted. No-op for cells not in any zone.
        public void ClearStockpileCell(int x, int y)
        {
            if (!InBounds(x, y)) return;
            int idx = y * Width + x;
            int existing = _cellZoneId[idx];
            if (existing == 0) return;

            lock (_designationsLock)
            {
                if (_stockpileZones.TryGetValue(existing, out var zone))
                {
                    zone.Cells.Remove((x, y));
                    if (zone.Cells.Count == 0) _stockpileZones.Remove(existing);
                }
                _cellZoneId[idx] = 0;
            }
            StockpileChanged?.Invoke(x, y);
        }

        public int GetStockpileIdAt(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return _cellZoneId[y * Width + x];
        }

        public StockpileZone? GetStockpileAt(int x, int y)
        {
            int id = GetStockpileIdAt(x, y);
            if (id == 0) return null;
            lock (_designationsLock)
            {
                return _stockpileZones.TryGetValue(id, out var z) ? z : null;
            }
        }

        // Snapshot of all zones, ordered by Priority descending (haul-target
        // selection walks this list). Caller must not mutate the returned
        // zones' cell lists or settings outside the lock.
        public List<StockpileZone> SnapshotStockpileZones()
        {
            lock (_designationsLock)
            {
                var list = new List<StockpileZone>(_stockpileZones.Count);
                list.AddRange(_stockpileZones.Values);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                return list;
            }
        }

        // Find the closest cell in any zone whose filter accepts `item` and
        // whose tile has spare capacity under v0.4.30 stack rules. Returns
        // null if no zone accepts the item or every accepting cell is full.
        // Walked by HaulSystem to pick the deliver target after pickup.
        //
        // Cell capacity check is delegated to the same per-tile rules
        // `TryDropOnTile` uses internally — type-locked + 250-cap.
        public (int X, int Y)? FindStockpileCellFor(Item item, int fromTileX, int fromTileY)
        {
            if (item == null) return null;
            var zones = SnapshotStockpileZones();
            (int X, int Y)? best = null;
            int bestPri = -1, bestD2 = int.MaxValue;
            foreach (var z in zones)
            {
                if (!z.Accepts(item)) continue;
                int zPri = (int)z.Priority;
                if (zPri < bestPri) break;   // priority-descending sort + best already at higher tier
                foreach (var (cx, cy) in z.Cells)
                {
                    if (!CellCanAcceptItem(cx, cy, item)) continue;
                    int dx = cx - fromTileX, dy = cy - fromTileY;
                    int d2 = dx * dx + dy * dy;
                    if (zPri > bestPri || d2 < bestD2)
                    {
                        bestPri = zPri;
                        bestD2  = d2;
                        best    = (cx, cy);
                    }
                }
            }
            return best;
        }

        // True iff (cx, cy) is currently allowed to receive `item` under the
        // v0.4.30 single-type + 250-cap per-tile rules. Used by the stockpile
        // cell picker to skip full or type-incompatible cells.
        private bool CellCanAcceptItem(int cx, int cy, Item item)
        {
            if (!InBounds(cx, cy) || !IsPassable(cx, cy)) return false;
            lock (_itemsLock)
            {
                if (!_droppedItems.TryGetValue((cx, cy), out var list)) return true;
                if (!IsCompatibleType(list, item)) return false;
                if (SumQuantity(list) + item.Quantity > MaxStackPerTile) return false;
                return true;
            }
        }

        // ── Indexed designation lookups (v0.3.33 — replaces 51×51 scans) ───────────

        // Returns the unclaimed designation tile closest to `fromPixel` (or
        // claimed by `claimerId` itself — letting a smurf re-find its own
        // task target). v0.3.40 — `avoid` is now a small FIFO of recently-
        // given-up tiles (with per-entry TTL) so a smurf that's bounced off
        // several unreachable targets in a row blacklists all of them
        // simultaneously. Entries with TicksLeft == 0 are inactive slots.
        public (int X, int Y)? FindNearestExcavate(Vector2 fromPixel, Guid claimerId,
            (int X, int Y, int TicksLeft)[]? avoid = null,
            System.Func<int, int, bool>? approachBlocked = null)
        {
            int cx = (int)(fromPixel.X / TileSize);
            int cy = (int)(fromPixel.Y / TileSize);
            int best = int.MaxValue;
            (int X, int Y)? winner = null;
            // v0.4.13 — region-aware filter. Ensure the smurf can actually
            // reach an excavate target before claiming it; the previous
            // version returned the closest-by-distance designation even
            // when sealed behind rock, producing the "smurfs cluster at
            // the edge of the dig site and jitter" bug.
            //
            // v0.4.29 — approachBlocked callback also lets the BehaviorSystem
            // veto targets whose only passable approaches are currently
            // occupied by other smurfs. Without this, multiple smurfs
            // cascade into a single-tile tunnel: each picks the nearest
            // open boulder, all converge on the one entry tile, and jam
            // the tunnel for everyone (no progress, no exit). The check
            // is per-candidate (cheap O(8) array lookups against the
            // occupancy grid the callback closes over).
            EnsureRegions();
            lock (_designationsLock)
            {
                foreach (var pos in _excavateDesignations)
                {
                    if (IsInAvoidList(avoid, pos.X, pos.Y)) continue;
                    if (_claims.TryGetValue(pos, out var owner) && owner != claimerId) continue;
                    if (!IsWorkReachable(cx, cy, pos.X, pos.Y)) continue;
                    if (approachBlocked != null && approachBlocked(pos.X, pos.Y)) continue;
                    int dx = pos.X - cx, dy = pos.Y - cy;
                    int d  = dx * dx + dy * dy;
                    if (d < best) { best = d; winner = pos; }
                }
            }
            return winner;
        }

        // Shared by all four FindNearest* methods. Walks the small fixed-size
        // FIFO and returns true if `(x, y)` matches any slot whose TTL is
        // still active. With 4 slots this is unrolled by the JIT.
        private static bool IsInAvoidList((int X, int Y, int TicksLeft)[]? avoid, int x, int y)
        {
            if (avoid == null) return false;
            for (int i = 0; i < avoid.Length; i++)
            {
                ref readonly var a = ref avoid[i];
                if (a.TicksLeft > 0 && a.X == x && a.Y == y) return true;
            }
            return false;
        }

        // Same for Gather. Also filters out tiles whose vegetation has gone
        // depleted in the meantime (autonomous harvest by some other smurf,
        // regrowth race condition, etc.) so the caller can't be routed to a
        // dead designation.
        public (int X, int Y)? FindNearestGather(Vector2 fromPixel, Guid claimerId,
            (int X, int Y, int TicksLeft)[]? avoid = null,
            System.Func<int, int, bool>? approachBlocked = null)
        {
            int cx = (int)(fromPixel.X / TileSize);
            int cy = (int)(fromPixel.Y / TileSize);
            int best = int.MaxValue;
            (int X, int Y)? winner = null;
            EnsureRegions();        // v0.4.13 — reachability filter
            lock (_designationsLock)
            {
                foreach (var pos in _gatherDesignations)
                {
                    if (IsInAvoidList(avoid, pos.X, pos.Y)) continue;
                    if (_claims.TryGetValue(pos, out var owner) && owner != claimerId) continue;
                    var veg = _vegetation[pos.X, pos.Y];
                    if (!veg.IsPresent || veg.IsDepleted) continue;
                    if (!IsWorkReachable(cx, cy, pos.X, pos.Y)) continue;
                    // v0.5.7 — approach-occupancy filter, ported from
                    // FindNearestExcavate (v0.4.29). Without this, Gather
                    // smurfs claim berry bushes whose only passable
                    // adjacent tiles are already occupied by other smurfs;
                    // they walk over, jam against the blocked perimeter,
                    // jitter until StuckThreshold (~1.5 s), give up,
                    // re-pick the SAME tile next idle cycle (per-smurf
                    // AvoidTiles only blacklists for one smurf), repeat.
                    // Sam: "Smurfs keep getting stuck on tasks like
                    // excavating and gather when there are lots of smurfs
                    // on-screen." RimWorld parity — JobGiver_Work skips
                    // targets whose reservation surface is currently
                    // blocked.
                    if (approachBlocked != null && approachBlocked(pos.X, pos.Y)) continue;
                    int dx = pos.X - cx, dy = pos.Y - cy;
                    int d  = dx * dx + dy * dy;
                    if (d < best) { best = d; winner = pos; }
                }
            }
            return winner;
        }

        // v0.3.38 — same shape as FindNearestGather but iterates the
        // Chop-Wood-designations set. Skips depleted vegetation so a smurf
        // can't be routed to a tile whose shroom is already cleared.
        public (int X, int Y)? FindNearestChopWood(Vector2 fromPixel, Guid claimerId,
            (int X, int Y, int TicksLeft)[]? avoid = null,
            System.Func<int, int, bool>? approachBlocked = null)
        {
            int cx = (int)(fromPixel.X / TileSize);
            int cy = (int)(fromPixel.Y / TileSize);
            int best = int.MaxValue;
            (int X, int Y)? winner = null;
            EnsureRegions();        // v0.4.13 — reachability filter
            lock (_designationsLock)
            {
                foreach (var pos in _chopWoodDesignations)
                {
                    if (IsInAvoidList(avoid, pos.X, pos.Y)) continue;
                    if (_claims.TryGetValue(pos, out var owner) && owner != claimerId) continue;
                    var veg = _vegetation[pos.X, pos.Y];
                    if (!veg.IsPresent || veg.IsDepleted) continue;
                    if (!IsWorkReachable(cx, cy, pos.X, pos.Y)) continue;
                    // v0.5.7 — approach-occupancy filter, ported from
                    // FindNearestExcavate (v0.4.29). See FindNearestGather
                    // above for the full rationale.
                    if (approachBlocked != null && approachBlocked(pos.X, pos.Y)) continue;
                    int dx = pos.X - cx, dy = pos.Y - cy;
                    int d  = dx * dx + dy * dy;
                    if (d < best) { best = d; winner = pos; }
                }
            }
            return winner;
        }

        // v0.3.38 — Cut Plants find. Any vegetation slot that's still present.
        public (int X, int Y)? FindNearestCut(Vector2 fromPixel, Guid claimerId,
            (int X, int Y, int TicksLeft)[]? avoid = null,
            System.Func<int, int, bool>? approachBlocked = null)
        {
            int cx = (int)(fromPixel.X / TileSize);
            int cy = (int)(fromPixel.Y / TileSize);
            int best = int.MaxValue;
            (int X, int Y)? winner = null;
            EnsureRegions();        // v0.4.13 — reachability filter
            lock (_designationsLock)
            {
                foreach (var pos in _cutDesignations)
                {
                    if (IsInAvoidList(avoid, pos.X, pos.Y)) continue;
                    if (_claims.TryGetValue(pos, out var owner) && owner != claimerId) continue;
                    var veg = _vegetation[pos.X, pos.Y];
                    if (!veg.IsPresent || veg.IsDepleted) continue;
                    if (!IsWorkReachable(cx, cy, pos.X, pos.Y)) continue;
                    // v0.5.7 — approach-occupancy filter, ported from
                    // FindNearestExcavate (v0.4.29). See FindNearestGather
                    // above for the full rationale.
                    if (approachBlocked != null && approachBlocked(pos.X, pos.Y)) continue;
                    int dx = pos.X - cx, dy = pos.Y - cy;
                    int d  = dx * dx + dy * dy;
                    if (d < best) { best = d; winner = pos; }
                }
            }
            return winner;
        }

        // ── Designation claims (v0.3.33 — B.7 soft-claim) ──────────────────────────
        //
        // A smurf calls TryClaim when it picks up a designation as its task
        // target. The claim is stored in `_claims` keyed by tile coord, with
        // the smurf's Id as the value. Other smurfs scanning for designations
        // skip claimed tiles via the `claimerId != owner` filter above. On
        // task completion or stuck-give-up the smurf calls ReleaseClaim. The
        // claim is also auto-released whenever the underlying designation
        // flag is cleared (in SetX / ClearDesignationsAt above).

        public bool TryClaim(int x, int y, Guid claimerId)
        {
            lock (_designationsLock)
            {
                if (_claims.TryGetValue((x, y), out var owner)) return owner == claimerId;
                _claims[(x, y)] = claimerId;
                return true;
            }
        }

        public void ReleaseClaim(int x, int y, Guid claimerId)
        {
            lock (_designationsLock)
            {
                if (_claims.TryGetValue((x, y), out var owner) && owner == claimerId)
                    _claims.Remove((x, y));
            }
        }

        // Snapshot of every flagged tile for the visual overlay. Returned as
        // a freshly allocated list — callers shouldn't mutate it. v0.3.38
        // — `Kind` distinguishes Excavate / Gather / ChopWood / Cut so the
        // overlay can draw a distinct glyph per type, and the save record
        // can round-trip the original designation kind.
        public enum DesignationKind { Excavate, Gather, ChopWood, Cut }

        public IReadOnlyList<(int X, int Y, DesignationKind Kind)> SnapshotDesignations()
        {
            lock (_designationsLock)
            {
                var result = new List<(int, int, DesignationKind)>(
                    _excavateDesignations.Count + _gatherDesignations.Count
                    + _chopWoodDesignations.Count + _cutDesignations.Count);
                foreach (var p in _excavateDesignations) result.Add((p.X, p.Y, DesignationKind.Excavate));
                foreach (var p in _gatherDesignations)   result.Add((p.X, p.Y, DesignationKind.Gather));
                foreach (var p in _chopWoodDesignations) result.Add((p.X, p.Y, DesignationKind.ChopWood));
                foreach (var p in _cutDesignations)      result.Add((p.X, p.Y, DesignationKind.Cut));
                return result;
            }
        }

        // Yield filter shared by Gather designation validity and the renderer's
        // glyph selection. Mirrors VegetationSlot.BaseYield > 0 minus the
        // non-food yielders (LargeMushroom yields Fungal Wood, MagicFlower
        // yields Magic Essence — those are not Gather targets).
        public static bool IsFoodYielding(VegetationType v) => v switch
        {
            VegetationType.SmurfberryBush  => true,
            VegetationType.SmallMushroom   => true,
            VegetationType.HerbCluster     => true,
            VegetationType.SmallSandshroom => true,
            VegetationType.PineShroom      => true,
            _                              => false,
        };

        // ── Runtime mutation API (called by Phase 3 behavior system) ───────────────

        // Permanently converts a tile's terrain (e.g., Boulder → Mud after excavation).
        // Logs the change and notifies the renderer so its image pixel updates immediately.
        public void MutateTerrain(int x, int y, TerrainType terrain)
        {
            if (!InBounds(x, y)) return;
            var tile      = _tiles[x, y];
            tile.Terrain  = terrain;
            tile.Passable = IsPassableTerrain(terrain);
            _tiles[x, y]  = tile;
            int idx = y * Width + x;
            if (_passable[idx] != tile.Passable) _regionsDirty = true;
            _passable[idx] = tile.Passable;       // v0.3.39 (O-M.2)
            _terrainMutations[(x, y)] = terrain;
            TerrainChanged?.Invoke(x, y, terrain);
        }

        // Updates a vegetation slot's yield and regrowth state.
        // Logs the change and notifies the renderer so the overlay redraws.
        public void SetVegetationYield(int x, int y, byte yieldRemaining, ushort regrowthTimer)
        {
            if (!InBounds(x, y)) return;
            var slot            = _vegetation[x, y];
            slot.YieldRemaining = yieldRemaining;
            slot.RegrowthTimer  = regrowthTimer;
            _vegetation[x, y]   = slot;
            _vegMutations[(x, y)] = (yieldRemaining, regrowthTimer);
            VegetationChanged?.Invoke(x, y);
        }

        // Restores a depleted slot after its regrowth timer expires.
        // LargeMushroom / LargeSandshroom: regrown cap becomes impassable again — notifies renderer for both layers.
        public void RestoreVegetationYield(int x, int y)
        {
            if (!InBounds(x, y)) return;
            var slot = _vegetation[x, y];
            if (!slot.IsPresent) return;

            byte restored       = VegetationSlot.BaseYield(slot.Type);
            slot.YieldRemaining = restored;
            slot.RegrowthTimer  = 0;
            slot.Health         = 100;
            _vegetation[x, y]   = slot;

            _vegMutations[(x, y)] = (restored, (ushort)0);

            if (slot.Type == VegetationType.LargeMushroom ||
                slot.Type == VegetationType.LargeSandshroom ||
                slot.Type == VegetationType.PalmShroom)
            {
                var tile      = _tiles[x, y];
                tile.Passable = false;
                _tiles[x, y]  = tile;
                _passable[y * Width + x] = false;       // v0.3.39
                _regionsDirty = true;
                TerrainChanged?.Invoke(x, y, tile.Terrain);
            }

            VegetationChanged?.Invoke(x, y);
        }

        // v0.4.15 — fell-the-whole-tree variant of `HarvestVegetation`.
        // Used by `ChopWood` so a single arrival depletes the shroom
        // entirely (RimWorld-style: one task = one tree felled). For
        // LargeMushroom / LargeSandshroom / PalmShroom the impassable
        // cap clears in the same call, flipping `_regionsDirty` so the
        // batched region rebuild on the next tick incorporates the
        // newly-passable tile. Returns the amount of yield that was
        // remaining (zero if the slot was already depleted), so the
        // caller can multiply by per-yield drop quantity.
        public byte FullyDepleteVegetation(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            var slot = _vegetation[x, y];
            if (!slot.IsPresent || slot.IsDepleted) return 0;

            byte taken = slot.YieldRemaining;
            ushort regrowthDays = VegetationSlot.RegrowthDays(slot.Type);
            SetVegetationYield(x, y, 0, regrowthDays);

            if (slot.Type == VegetationType.LargeMushroom ||
                slot.Type == VegetationType.LargeSandshroom ||
                slot.Type == VegetationType.PalmShroom)
            {
                var tile = _tiles[x, y];
                if (!tile.Passable)
                {
                    tile.Passable = true;
                    _tiles[x, y]  = tile;
                    int idx = y * Width + x;
                    _passable[idx] = true;
                    _regionsDirty = true;
                    TerrainChanged?.Invoke(x, y, tile.Terrain);
                }
            }
            return taken;
        }

        // v0.4.17 — fully removes a vegetation slot (Type → None). Used by
        // the Cut command on decoration vegetation (Underbrush, MossPatch)
        // which has BaseYield = 0 and would therefore never enter the
        // "depleted" visual state via FullyDepleteVegetation — cutting
        // them is meant to clear the tile entirely, not leave an invisible
        // depleted slot in place. Also flips passability back to the
        // underlying terrain's default if the cleared vegetation had been
        // making the tile impassable (defensive; only Large* / Palm shrooms
        // do that, and they're never sent here).
        public void ClearVegetation(int x, int y)
        {
            if (!InBounds(x, y)) return;
            var slot = _vegetation[x, y];
            if (!slot.IsPresent) return;
            _vegetation[x, y] = VegetationSlot.Empty;
            VegetationChanged?.Invoke(x, y);

            var tile = _tiles[x, y];
            bool defaultPassable = IsPassableTerrain(tile.Terrain);
            if (!tile.Passable && defaultPassable)
            {
                tile.Passable = true;
                _tiles[x, y]  = tile;
                int idx = y * Width + x;
                _passable[idx] = true;
                _regionsDirty = true;
                TerrainChanged?.Invoke(x, y, tile.Terrain);
            }
        }

        // Phase 3 stub: harvests one unit from a vegetation slot.
        //
        // Food plants (SmurfberryBush, SmallMushroom, etc.) dim to half opacity when
        // depleted, then silently regrow after their RegrowthDays timer.
        //
        // LargeMushroom: the tree disappears on full depletion (tile becomes passable,
        // half-opacity stump drawn) and will regrow after RegrowthDays.
        public void HarvestVegetation(int x, int y)
        {
            if (!InBounds(x, y)) return;
            var slot = _vegetation[x, y];
            if (!slot.IsPresent || slot.IsDepleted) return;

            if (slot.YieldRemaining > 1)
            {
                SetVegetationYield(x, y, (byte)(slot.YieldRemaining - 1), slot.RegrowthTimer);
            }
            else
            {
                // Fully depleted — enter regrowth state.
                ushort regrowthDays = VegetationSlot.RegrowthDays(slot.Type);
                SetVegetationYield(x, y, 0, regrowthDays);

                // LargeMushroom / LargeSandshroom: cleared cap opens passage.
                if (slot.Type == VegetationType.LargeMushroom ||
                    slot.Type == VegetationType.LargeSandshroom ||
                    slot.Type == VegetationType.PalmShroom)
                {
                    var tile      = _tiles[x, y];
                    tile.Passable = true;
                    _tiles[x, y]  = tile;
                    _passable[y * Width + x] = true;       // v0.3.39
                    _regionsDirty = true;
                    TerrainChanged?.Invoke(x, y, tile.Terrain);
                }
            }
        }

        // ── Delta apply (called during save load — does NOT log as mutations) ──────

        public void ApplyTerrainDelta(int x, int y, TerrainType terrain)
        {
            if (!InBounds(x, y)) return;
            var tile      = _tiles[x, y];
            tile.Terrain  = terrain;
            tile.Passable = IsPassableTerrain(terrain);
            _tiles[x, y]  = tile;
            int idx = y * Width + x;
            if (_passable[idx] != tile.Passable) _regionsDirty = true;
            _passable[idx] = tile.Passable;       // v0.3.39
        }

        // Restores yield/regrowth from a save delta.
        // Depleted LargeMushroom → tile must be passable (tree was cut before the save).
        public void ApplyVegetationDelta(int x, int y, byte yieldRemaining, ushort regrowthTimer)
        {
            if (!InBounds(x, y)) return;
            var slot            = _vegetation[x, y];
            slot.YieldRemaining = yieldRemaining;
            slot.RegrowthTimer  = regrowthTimer;
            _vegetation[x, y]   = slot;

            if ((slot.Type == VegetationType.LargeMushroom ||
                 slot.Type == VegetationType.LargeSandshroom ||
                 slot.Type == VegetationType.PalmShroom) && yieldRemaining == 0)
            {
                var tile      = _tiles[x, y];
                tile.Passable = true;
                _tiles[x, y]  = tile;
                int idx = y * Width + x;
                if (!_passable[idx]) _regionsDirty = true;
                _passable[idx] = true;       // v0.3.39
            }
        }

        // ── Save-delta export ──────────────────────────────────────────────────────

        // v0.3.36 — flatten the dict into a list for the save-delta consumer.
        // SaveManager calls this once per save, so allocation cost is
        // bounded; the dict's O(1) per-write inserts are the value.
        public IReadOnlyList<(int X, int Y, TerrainType Terrain)> GetTerrainMutations()
        {
            var list = new List<(int X, int Y, TerrainType)>(_terrainMutations.Count);
            foreach (var (key, terrain) in _terrainMutations)
                list.Add((key.X, key.Y, terrain));
            return list;
        }

        public IReadOnlyList<(int X, int Y, byte YieldRemaining, ushort RegrowthTimer)> GetVegetationMutations()
        {
            var list = new List<(int X, int Y, byte, ushort)>(_vegMutations.Count);
            foreach (var (key, val) in _vegMutations)
                list.Add((key.X, key.Y, val.Yield, val.Regrow));
            return list;
        }
    }
}
