using System;
using System.Collections.Generic;
using Godot;
using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

namespace SmurfulationC.Simulation.Systems
{
    // v0.4.2 — Haul task implementation (replaces the v0.4.0 stub).
    //
    // Without Phase 5 stockpile zones, items are dropped on the tile
    // where they were gathered / excavated / chopped / cut. A smurf with
    // any Haul priority > 0 in their Jobs grid will:
    //   1. Scan for the nearest on-tile item that isn't already
    //      reserved by another hauler.
    //   2. Walk to it, mark the smurf as the haul target.
    //   3. On arrival, pick the item up into `Smurf.CarriedItem`
    //      (RimWorld single-carry-slot model).
    //   4. Walk to the colony delivery point (defaults to the spawn
    //      cluster centre; Phase 5 will replace this with the nearest
    //      stockpile zone accepting the item's category).
    //   5. On arrival at the delivery point, drop the item into
    //      `ColonyResources.Inventory` so the player's HUD totals
    //      reflect the new supply.
    //
    // The two-step path (pickup → deliver) is tracked via the smurf's
    // CurrentTask.TargetTileX/Y (pickup target) and a second-stage
    // toggle: when `CarriedItem != null` the task target is the
    // delivery point, not the original tile.
    public static class HaulSystem
    {
        // Reservation set so multiple haulers don't converge on the same
        // pickup. Items are reserved by the smurf's Guid when the task
        // is created, released on completion or task abandonment.
        private static readonly object _reserveLock = new();
        private static readonly Dictionary<Guid, Guid> _reservations = new();

        // v0.4.12 — priority set populated by the Haul order. Items in
        // this set bypass `SelectHaulTarget`'s 32-tile radius cap and
        // get picked up by the first available hauler regardless of
        // distance. Cleared per-item when the haul completes
        // (HaulSystem.Apply phase 1) so the dict stays bounded.
        private static readonly HashSet<Guid> _priorityHauls = new();

        // v0.4.27 — fires whenever the priority-haul set changes (Mark or
        // Clear). `DesignationOverlay` subscribes so the on-tile Haul
        // glyph appears the moment the player commits a Haul drag, and
        // disappears when the priority item is picked up. Raised from
        // both sim thread (Apply Phase 1's ClearPriority) and main
        // thread (player Haul drag's MarkPriority); subscribers must
        // be thread-safe (the overlay just sets a `volatile bool`).
        public static event System.Action? PriorityHaulsChanged;

        public static void MarkPriority(Guid itemId)
        {
            bool added;
            lock (_reserveLock) added = _priorityHauls.Add(itemId);
            if (added) PriorityHaulsChanged?.Invoke();
        }

        public static void ClearPriority(Guid itemId)
        {
            bool removed;
            lock (_reserveLock) removed = _priorityHauls.Remove(itemId);
            if (removed) PriorityHaulsChanged?.Invoke();
        }

        public static bool IsPriority(Guid itemId)
        {
            lock (_reserveLock) return _priorityHauls.Contains(itemId);
        }

        // v0.4.50 — cheap "is any priority item flagged" probe used by
        // SelectHaulTarget to skip the priority-pass map walk when the
        // set is empty (the steady-state case). One lock + one Count
        // read; avoids dropping a per-tile EnumerateDroppedItems
        // snapshot just to discover zero hits.
        public static bool HasAnyPriorityHaul()
        {
            lock (_reserveLock) return _priorityHauls.Count > 0;
        }

        // v0.4.27 — one-shot snapshot of the priority set, taken under
        // the reservation lock then iterated lock-free by the overlay's
        // RebuildInstances walk. Per-priority-item `IsPriority` calls
        // would take 200+ lock acquires per rebuild on a busy haul site;
        // a single snapshot collapses that to one.
        public static HashSet<Guid> SnapshotPriorityHauls()
        {
            lock (_reserveLock) return new HashSet<Guid>(_priorityHauls);
        }

        public static void Reserve(Item item, Guid haulerId)
        {
            lock (_reserveLock) _reservations[item.Id] = haulerId;
        }

        public static void Release(Item item)
        {
            lock (_reserveLock) _reservations.Remove(item.Id);
        }

        // v0.4.7 (bugreport B-3) — release by Guid string. Used by
        // BehaviorSystem.ReleaseTaskClaim to clear haul reservations when
        // a Haul task is interrupted before pickup (critical need fires,
        // stuck-detector gives up, player issues a new order). Without
        // this, reservations leaked into the dict and accumulated until
        // every dropped item was "reserved" by long-departed haulers
        // and the colony stopped hauling entirely.
        public static void ReleaseByIdString(string? guidString)
        {
            if (string.IsNullOrEmpty(guidString)) return;
            if (!System.Guid.TryParse(guidString, out var id)) return;
            lock (_reserveLock) _reservations.Remove(id);
        }

        public static bool IsReservedByOther(Item item, Guid askingId)
        {
            lock (_reserveLock)
            {
                if (!_reservations.TryGetValue(item.Id, out var owner)) return false;
                return owner != askingId;
            }
        }

        // Selects the nearest on-tile item not reserved by another smurf
        // and not already in this smurf's carry slot. Returns a Haul
        // task targeting the pickup tile.
        //
        // v0.4.6 — bounded search radius. The previous version
        // iterated *every* dropped-items tile on the map. At 1000
        // smurfs with say 200 dropped piles that's 200k iterations
        // per sim tick just to find a haul target. Cap at 32 tiles
        // (squared = 1024) which is half the visible viewport at 1×
        // zoom — far enough that haulers don't fixate on the nearest
        // berry while a pile rots across the map, close enough that
        // we don't pay the full-map walk every tick.
        private const int HaulSearchRadiusSq = 32 * 32;

        public static BehaviorTask? SelectHaulTarget(Smurf s, LocalMap? map, ColonyResources r)
        {
            // v0.4.30 — only fires for fully-empty smurfs. Mid-haul retargeting
            // (capacity not yet full → grab another nearby item) is now handled
            // INSIDE Apply Phase 1, so a smurf carrying anything is already
            // committed to either continuing the trip or delivering.
            if (s.Inventory.Count > 0) return null;
            if (map == null) return null;
            // Capacity gate: if a smurf is so weak (e.g. Sprout w/ Miniaturization)
            // that their cap is at the floor of 5, that's still ≥ 1 — but we
            // skip if somehow zero (defensive; shouldn't happen with the
            // [5,75] clamp).
            if (s.CarryingCapacity <= 0) return null;

            (int X, int Y)? bestTile = null;
            Item?           bestItem = null;
            int             bestD2   = HaulSearchRadiusSq;

            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);

            // v0.4.12 — first pass: priority haul (Force-haul order).
            // Bypasses the 32-tile radius cap, so haulers will run
            // across the entire map to fetch player-flagged items.
            // Falls through to the default radius-bounded scan if no
            // priority item is found or all are reserved.
            //
            // v0.4.50 — skip the entire walk when no priority items
            // exist. Previously the priority pass enumerated every
            // dropped tile even when `_priorityHauls` was empty (i.e.
            // 99 % of the time in a colony where the player hasn't
            // Force-Hauled anything). Each call allocated a fresh
            // `(int,int,Item[])[]` snapshot of the whole dropped-items
            // dict — 50 smurfs × 2 EnumerateDroppedItems calls per
            // task selection (priority pass + standard pass) became a
            // measurable sim-thread cost under 50-smurf gather/excavate
            // loads. PriorityHaulCount is an O(1) read under the
            // reservation lock.
            (int X, int Y)? priTile = null;
            Item?           priItem = null;
            int             priD2   = int.MaxValue;
            if (HasAnyPriorityHaul())
            {
                foreach (var (tx, ty, items) in map.EnumerateDroppedItems())
                {
                    foreach (var it in items)
                    {
                        if (!IsPriority(it.Id)) continue;
                        if (IsReservedByOther(it, s.Id)) continue;
                        int dx = tx - sx, dy = ty - sy;
                        int d2 = dx * dx + dy * dy;
                        if (d2 < priD2)
                        {
                            priD2 = d2;
                            priTile = (tx, ty);
                            priItem = it;
                        }
                    }
                }
            }
            if (priItem != null && priTile != null)
            {
                Reserve(priItem, s.Id);
                var ppx = new Vector2(
                    priTile.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                    priTile.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                // Bumped priority (60 vs 50) so a force-haul outranks
                // ambient auto-haul if both fire on the same tick.
                return new BehaviorTask(TaskType.Haul, ppx, 60f,
                    interruptible: true,
                    tileX: priTile.Value.X, tileY: priTile.Value.Y,
                    targetId: priItem.Id.ToString());
            }

            // v0.4.51 — radius-filtered, non-allocating walk. Was a full
            // EnumerateDroppedItems snapshot per call; with 50 smurfs
            // and ~500 dropped tiles under heavy gather/excavate that
            // was the dominant sim-thread allocation source.
            // v0.5.6 — items already sitting in a stockpile that accepts
            // them are NOT haulable. Pre-fix the auto-haul scan would
            // re-flag every just-delivered item as a haul source on the
            // very next tick — smurf drops at cell A, SelectTask runs,
            // finds the item still on cell A, picks it up, FindStockpileCellFor
            // returns cell A or B, smurf re-delivers, repeat. Sam's
            // "stand jittering trying to haul forever after dropping off
            // items, almost as if they're in a loop of trying to haul
            // things that are already in a stockpile zone." The
            // priority/force-haul branch above (player explicitly tagged)
            // intentionally skips this guard so the player can move
            // stockpiled items manually.
            map.ForEachDroppedInRadius(sx, sy, HaulSearchRadiusSq, (tx, ty, items) =>
            {
                int dx = tx - sx, dy = ty - sy;
                int d2 = dx * dx + dy * dy;
                if (d2 >= bestD2) return;
                bool tileIsAcceptingStockpile = TileIsAcceptingStockpile(map, tx, ty);
                foreach (var it in items)
                {
                    if (it.IsForbidden) continue;        // v0.5.0 (Phase 5A — rimport N5)
                    if (IsReservedByOther(it, s.Id)) continue;
                    // v0.5.6 — skip if already at home in a stockpile that
                    // accepts this item kind. Per-item check (not just
                    // per-tile) because a future zone with a Kind filter
                    // could accept some items on the tile but not others.
                    if (tileIsAcceptingStockpile && StockpileAcceptsHere(map, tx, ty, it)) continue;
                    bestItem = it;
                    bestTile = (tx, ty);
                    bestD2 = d2;
                    break;
                }
            });

            if (bestItem == null || bestTile == null) return null;

            Reserve(bestItem, s.Id);

            var px = new Vector2(
                bestTile.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                bestTile.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            return new BehaviorTask(TaskType.Haul, px, 50f,
                interruptible: true,
                tileX: bestTile.Value.X, tileY: bestTile.Value.Y,
                targetId: bestItem.Id.ToString());
        }

        // v0.4.30 — multi-trip haul (RimWorld-style). One Haul task can now
        // chain through several pickups before delivering, up to the smurf's
        // CarryingCapacity. Phase discrimination: a non-null TargetId means
        // we're targeting a specific item to pick up; null means we're
        // walking to the delivery tile to drop the lot.
        //
        // DUAL-BOOKKEEPING NOTE: deposit drops items on the stockpile tile
        // via map.DropItem (visible to the player) AND adds to the colony
        // ColonyResources.Inventory pool (HUD totals continue to read). The
        // two will drift over time as consumption decrements only the pool;
        // a unifying refactor is queued for the Phase-5 stockpile-zone work.
        // Acceptable for v0.4.30 because the visible-stockpile feature is
        // the headline ask and the HUD divergence stays bounded by the
        // consumption rate (~tens of items per minute, not per tick).
        public static void Apply(Smurf s, BehaviorTask t, LocalMap? map, ColonyResources r)
        {
            if (map == null) { s.CurrentTask = null; return; }

            // ── Phase 1: pickup at TargetId tile ─────────────────────
            if (!string.IsNullOrEmpty(t.TargetId))
            {
                if (t.TargetTileX < 0 || t.TargetTileY < 0) { s.CurrentTask = null; return; }
                var items = map.GetItemsOnTile(t.TargetTileX, t.TargetTileY);
                Item? pickup = null;
                foreach (var it in items)
                    if (it.Id.ToString() == t.TargetId) { pickup = it; break; }
                if (pickup == null) { s.CurrentTask = null; return; }
                if (!map.RemoveItem(pickup)) { s.CurrentTask = null; return; }
                Release(pickup);
                ClearPriority(pickup.Id);
                pickup.TilePos = null;
                pickup.OwnerSmurfId = s.Id;
                s.Inventory.Add(pickup);
                s.TaskDidWork = true;

                // v0.4.30 — chain to the next nearby pickup if there's still
                // capacity remaining. Skips items that wouldn't fit. The
                // 32-tile radius from SelectHaulTarget applies here too:
                // a smurf chasing one last unreserved berry across the map
                // would tank perf and look pathological.
                if (s.CurrentCarriedCount < s.CarryingCapacity)
                {
                    var next = FindNextHaulNearby(s, map);
                    if (next.HasValue)
                    {
                        Reserve(next.Value.Item, s.Id);
                        s.CurrentTask = new BehaviorTask(TaskType.Haul, next.Value.Pixel, 50f,
                            interruptible: true,
                            tileX: next.Value.X, tileY: next.Value.Y,
                            targetId: next.Value.Item.Id.ToString());
                        return;
                    }
                }

                // Either full or no more nearby items — go deliver everything.
                var delivery = PickDeliveryTileFor(s, map);
                s.CurrentTask = new BehaviorTask(TaskType.Haul, delivery, 50f,
                    interruptible: true,
                    tileX: (int)(delivery.X / LocalMap.TileSize),
                    tileY: (int)(delivery.Y / LocalMap.TileSize));
                return;
            }

            // ── Phase 2: deliver all carried items at the stockpile tile ─
            // v0.4.36 — drop ONLY on the map. The v0.4.30 dual-write
            // (also calling r.Inventory.Add(item)) caused a compounding
            // overflow: smurfs would re-pick up their own deliveries
            // from the stockpile tile in their next multi-trip cycle,
            // and every re-deposit added the item's Quantity to the
            // colony pool again without consuming the on-map stack.
            // After enough cycles the pool overflowed int → negative
            // HUD totals. The HUD now reads on-map stockpiles directly
            // through ColonyResources.Map (see ColonyResources.cs).
            int dx = t.TargetTileX, dy = t.TargetTileY;
            var dropPos = new Vector2(
                dx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                dy * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            for (int i = 0; i < s.Inventory.Count; i++)
            {
                var item = s.Inventory[i];
                item.OwnerSmurfId = null;
                item.TilePos = dropPos;
                map.DropItem(item);   // visible stockpile drop — cap/type rules in DropItem
            }
            s.Inventory.Clear();
            s.TaskDidWork = true;
            s.CurrentTask = null;
        }

        // v0.4.30 — multi-trip helper. After a pickup, scan the same
        // 32-tile radius for the next nearest unreserved haulable that
        // would still fit. Skips items whose Quantity would push us over
        // the capacity (so a smurf with 5 slots remaining doesn't try to
        // grab a 50-stack of berries — they'd rather walk home empty than
        // claim something they can't take).
        private static (Item Item, int X, int Y, Vector2 Pixel)? FindNextHaulNearby(Smurf s, LocalMap map)
        {
            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
            int bestD2 = HaulSearchRadiusSq;
            Item? bestItem = null;
            (int X, int Y)? bestTile = null;
            int remaining = s.CarryingCapacity - s.CurrentCarriedCount;
            // v0.4.51 — radius-filtered, non-allocating walk. Multi-trip
            // pickups fire this on every chained pickup, so haul-heavy
            // ticks used to trigger as many full snapshots as
            // SelectHaulTarget did.
            map.ForEachDroppedInRadius(sx, sy, HaulSearchRadiusSq, (tx, ty, items) =>
            {
                int dx = tx - sx, dy = ty - sy;
                int d2 = dx * dx + dy * dy;
                if (d2 >= bestD2) return;
                // v0.5.6 — same stockpile-skip guard as SelectHaulTarget.
                // Without this, the multi-trip chain would also re-pick
                // just-delivered items as it walked back through stockpile
                // tiles on the way to the next legitimate haul source.
                bool tileIsAcceptingStockpile = TileIsAcceptingStockpile(map, tx, ty);
                foreach (var it in items)
                {
                    if (it.IsForbidden) continue;        // v0.5.0 (Phase 5A — rimport N5)
                    if (IsReservedByOther(it, s.Id)) continue;
                    if (it.Quantity > remaining) continue;
                    if (tileIsAcceptingStockpile && StockpileAcceptsHere(map, tx, ty, it)) continue;
                    bestItem = it;
                    bestTile = (tx, ty);
                    bestD2 = d2;
                    break;
                }
            });
            if (bestItem == null || bestTile == null) return null;
            var px = new Vector2(
                bestTile.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                bestTile.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            return (bestItem, bestTile.Value.X, bestTile.Value.Y, px);
        }

        // v0.5.6 — fast tile-level pre-check. True if the tile sits in any
        // stockpile zone. Cheap (one int array lookup); the per-item
        // `Accepts` check below only runs when this is true. Splits the
        // common case (most haul-source candidates are NOT in a stockpile)
        // from the expensive accepts check.
        private static bool TileIsAcceptingStockpile(LocalMap map, int tx, int ty)
        {
            return map.GetStockpileIdAt(tx, ty) != 0;
        }

        // v0.5.6 — full per-item check: this tile's stockpile zone exists
        // AND accepts items of this Kind. Today every zone with empty
        // AcceptedKinds accepts everything; once Phase 5C adds the per-zone
        // filter UI, items in a zone that doesn't accept their Kind will
        // remain haul-source-eligible (the smurf will move them to a zone
        // that does accept them). RimWorld's "store at higher priority"
        // pattern can layer on top of this without changing the call site.
        private static bool StockpileAcceptsHere(LocalMap map, int tx, int ty, Item item)
        {
            var zone = map.GetStockpileAt(tx, ty);
            return zone != null && zone.Accepts(item);
        }

        // v0.4.19 — cached spawn-cluster tile list. The previous
        // `FindDeliveryPoint` called `LocalMap.FindSpawnCluster(8)` on
        // every Phase-1 pickup; that method allocates a fresh
        // `bool[Width, Height]` (36 KB at 240×150, 144 KB at 480×300)
        // and runs a BFS from the map centre, every single call. With
        // 250 active haulers at the perf target that was a 36 MB / sec
        // gen-0 storm + a small BFS-per-second hammer on the sim
        // thread. The cluster only shifts on map regeneration, so we
        // cache it (keyed by map reference) and just look up the
        // smurf's stable index modulo cluster size.
        private static LocalMap?         _cachedMap;
        private static (int X, int Y)[]? _cachedCluster;

        private static (int X, int Y)[] GetCluster(LocalMap map)
        {
            if (!ReferenceEquals(map, _cachedMap) || _cachedCluster == null)
            {
                var list = map.FindSpawnCluster(12);
                if (list == null || list.Count == 0)
                {
                    // Fallback: a single tile at map centre.
                    _cachedCluster = new[] { (map.Width / 2, map.Height / 2) };
                }
                else
                {
                    _cachedCluster = new (int, int)[list.Count];
                    for (int i = 0; i < list.Count; i++) _cachedCluster[i] = list[i];
                }
                _cachedMap = map;
            }
            return _cachedCluster!;
        }

        // Picks a delivery tile.
        //
        // v0.5.0 (Phase 5A — rimport N1) — if any stockpile zone exists that
        // accepts the carried item AND has spare capacity, deliver to the
        // closest such cell. Walks zones in StoragePriority-descending
        // order via `LocalMap.FindStockpileCellFor`. Picks the FIRST item
        // in the smurf's inventory as the routing key — multi-type
        // inventories will route to whichever zone matches that one item;
        // mismatched items in the same haul fall back to the spawn-cluster
        // delivery point (gracefully, since v0.4.30 stockpile rules will
        // overflow them to compatible adjacent tiles).
        //
        // Falls back to the spawn-cluster cell when no zone accepts the
        // item (no stockpile painted yet, or the player's painted zones
        // don't include this item kind). Keeps the v0.4.19 spawn-cluster
        // hash-spread for the fallback.
        private static Vector2 PickDeliveryTileFor(Smurf s, LocalMap map)
        {
            // v0.5.0 — try stockpile first.
            if (s.Inventory != null && s.Inventory.Count > 0)
            {
                var first = s.Inventory[0];
                int sx = (int)(s.SimPos.X / LocalMap.TileSize);
                int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
                var dest = map.FindStockpileCellFor(first, sx, sy);
                if (dest.HasValue)
                {
                    return new Vector2(
                        dest.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        dest.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                }
            }

            // Fallback: spawn-cluster (pre-v0.5.0 behaviour).
            var cluster = GetCluster(map);
            int idx = (s.Id.GetHashCode() & 0x7FFFFFFF) % cluster.Length;
            var (tx, ty) = cluster[idx];
            return new Vector2(tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                               ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
        }
    }
}
