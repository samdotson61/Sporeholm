using System.Collections.Generic;
using Sporeholm.Simulation.Items;

namespace Sporeholm.World
{
    // v0.5.23 (Phase 5F — Roadmap §5.4-5.6) — flood-fill room detection.
    // Walks the passable-tile graph to identify enclosed regions (rooms),
    // assigns each interior tile a RoomId, and registers per-room
    // metadata in LocalMap.RoomRegistry.
    //
    // RimWorld parity: rooms are connected components of passable tiles
    // bounded by impassable walls / map edge. A region that touches the
    // map edge is "outdoors" and gets RoomId = 1 (the implicit outdoor
    // room). Genuinely-enclosed regions get RoomId 2, 3, 4, ... assigned
    // in flood-fill order.
    //
    // Walls and Doors are BOTH treated as room boundaries (v0.6.0 —
    // changed from prior behaviour where doors connected rooms). Walls are
    // impassable to movement and to room flood-fill alike; Doors stay
    // passable to movement but are skipped by the room flood-fill so a
    // walled enclosure with a door reads as a closed room (not bleeding
    // out to outdoors through the door tile). Doors themselves carry no
    // RoomId — they belong to neither side. This matches player intuition
    // ("if I close my walls with a door, that's a room") and gives the
    // room-type / beauty / temperature systems the right answer for the
    // common "small room with one doorway" build.
    //
    // Performance: full rebuild is O(W*H) — at 80×50 default that's 4000
    // tiles, ~0.1ms. Triggered when a Wall or Door is built / demolished
    // (StructureChanged event already in place since v0.5.19). Per-frame
    // is overkill; rebuild only on actual passability change.
    public static class RoomDetector
    {
        public const ushort OutdoorRoomId = 1;

        // Rebuilds the entire room map. Walks every passable tile, BFS-
        // floods to find connected components, assigns RoomIds, and
        // populates LocalMap.RoomRegistry with per-room metadata.
        // Stamps StructureSlot.RoomId for each tile.
        public static void Rebuild(LocalMap map)
        {
            int W = map.Width, H = map.Height;
            // Reset every tile's RoomId by walking the structure array.
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var s = map.GetStructure(x, y);
                if (s.RoomId != 0)
                {
                    s.RoomId = 0;
                    map.SetStructureRoomId(x, y, 0);
                }
            }

            map.ClearRoomRegistry();
            ushort nextRoomId = OutdoorRoomId + 1;   // outdoor reserved
            var queue = new Queue<(int X, int Y)>();
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!map.IsPassable(x, y)) continue;
                // v0.6.0 — doors are treated as room boundaries even though
                // they're passable to shroomp movement. Skip seeding a flood
                // from a door tile so doors don't carry RoomId and don't
                // bridge interior + exterior into one merged region.
                if (IsDoorTile(map, x, y)) continue;
                if (map.GetStructure(x, y).RoomId != 0) continue;

                // BFS flood-fill from this seed.
                bool touchesEdge = false;
                int tileCount = 0;
                int floorCount = 0;
                int furnitureCount = 0;
                int hearthCount = 0;
                int corpseCount = 0;   // beauty negative — only if items registered
                // v0.5.84t — per-furniture counts for RoomType inference.
                int bedCount       = 0;
                int workbenchCount = 0;
                int shelfCount     = 0;
                int torchCount     = 0;   // v0.5.84t

                // First pass: assign a temporary marker (use the next id),
                // we'll downgrade to OutdoorRoomId at the end if it touched the edge.
                ushort tempId = nextRoomId;
                queue.Enqueue((x, y));
                map.SetStructureRoomId(x, y, tempId);

                // v0.5.30 — Quality-weighted beauty accumulator. RimWorld
                // pattern: per-furniture quality (Awful/Normal/Excellent
                // /Masterwork) drives the room's BeautyScore. We sum
                // QualityMeta.ValueMul (Crude 0.5 / Normal 1.0 / Fine 1.4
                // / Superior 2.0 / Masterwork 3.5 / Legendary 6.0) per
                // furniture tile, then fold into Room.BeautyScore.
                float furnitureBeauty = 0f;
                float floorBeauty     = 0f;

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    tileCount++;
                    if (cx == 0 || cy == 0 || cx == W - 1 || cy == H - 1)
                        touchesEdge = true;
                    var slot = map.GetStructure(cx, cy);
                    float qMul = QualityMeta.ValueMul(slot.Quality);
                    switch (slot.Type)
                    {
                        case StructureType.Floor:        floorCount++;     floorBeauty += qMul;     break;
                        case StructureType.Shelf:        furnitureCount++; shelfCount++;     furnitureBeauty += qMul; break;
                        case StructureType.Workbench:    furnitureCount++; workbenchCount++; furnitureBeauty += qMul; break;
                        case StructureType.Hearth:       furnitureCount++; hearthCount++; furnitureBeauty += qMul; break;
                        // v0.6.0 — Doors are room boundaries (treated like
                        // walls for flood-fill), so a door tile never carries
                        // a RoomId and this case is unreachable. Beauty
                        // contribution from doors is folded in at room build
                        // time via the adjacent-door audit pass below.
                        // v0.5.35-37 — new furniture types contribute to
                        // FurnitureCount + FurnitureBeauty. Joy structures
                        // and Tables tend to look nice (player paint them);
                        // beds are quality-keyed for the Phase 6 bedroom
                        // auto-inference.
                        case StructureType.Bed:              furnitureCount++; bedCount++;       furnitureBeauty += qMul; break;
                        case StructureType.MeditationShrine: furnitureCount++; furnitureBeauty += qMul * 1.3f; break;
                        case StructureType.ShroomBoard:      furnitureCount++; furnitureBeauty += qMul; break;
                        case StructureType.GossipBench:      furnitureCount++; furnitureBeauty += qMul; break;
                        case StructureType.Table:            furnitureCount++; furnitureBeauty += qMul; break;
                        // v0.5.84t — Torch contributes to FurnitureCount + a
                        // small beauty add. TorchCount tracked separately so
                        // Room.TemperatureOffsetC adds +2°C per torch.
                        case StructureType.Torch:            furnitureCount++; torchCount++; furnitureBeauty += qMul; break;
                    }

                    // 4-neighbour expansion. Diagonal not used (keeps room
                    // topology consistent with how walls separate spaces).
                    TryEnqueue(map, cx + 1, cy, tempId, queue);
                    TryEnqueue(map, cx - 1, cy, tempId, queue);
                    TryEnqueue(map, cx, cy + 1, tempId, queue);
                    TryEnqueue(map, cx, cy - 1, tempId, queue);
                }

                // v0.6.0 — doors are skipped by the flood-fill but still
                // contribute to FurnitureCount + FurnitureBeauty of every
                // room they border. Walk the perimeter of the just-flooded
                // region and credit each cardinally-adjacent Door tile once.
                // We use a tiny visited-set to avoid double-counting a door
                // that touches the same room on two sides (e.g. an L-shape).
                int doorBorderCount = 0;
                float doorBorderBeauty = 0f;
                if (!touchesEdge)
                {
                    var doorSeen = new HashSet<int>();
                    for (int yy = 0; yy < H; yy++)
                    for (int xx = 0; xx < W; xx++)
                    {
                        if (map.GetStructure(xx, yy).RoomId != tempId) continue;
                        TryCountAdjacentDoor(map, xx + 1, yy, doorSeen, ref doorBorderCount, ref doorBorderBeauty);
                        TryCountAdjacentDoor(map, xx - 1, yy, doorSeen, ref doorBorderCount, ref doorBorderBeauty);
                        TryCountAdjacentDoor(map, xx, yy + 1, doorSeen, ref doorBorderCount, ref doorBorderBeauty);
                        TryCountAdjacentDoor(map, xx, yy - 1, doorSeen, ref doorBorderCount, ref doorBorderBeauty);
                    }
                }

                if (touchesEdge)
                {
                    // Downgrade this region to the outdoor room.
                    OverwriteRoomId(map, tempId, OutdoorRoomId);
                }
                else
                {
                    furnitureCount  += doorBorderCount;
                    furnitureBeauty += doorBorderBeauty;
                    // v0.5.84t — infer RoomType from furniture mix.
                    // Priority: Bedroom > Kitchen > Workshop > Storage > Generic.
                    RoomType type;
                    if      (bedCount > 0)       type = RoomType.Bedroom;
                    else if (hearthCount > 0)    type = RoomType.Kitchen;
                    else if (workbenchCount > 0) type = RoomType.Workshop;
                    else if (shelfCount > 0)     type = RoomType.Storage;
                    else                         type = RoomType.Generic;

                    map.RegisterRoom(new Room
                    {
                        Id              = tempId,
                        TileCount       = tileCount,
                        FloorCount      = floorCount,
                        FurnitureCount  = furnitureCount,
                        HearthCount     = hearthCount,
                        CorpseCount     = corpseCount,
                        FurnitureBeauty = furnitureBeauty,
                        FloorBeauty     = floorBeauty,
                        BedCount        = bedCount,
                        WorkbenchCount  = workbenchCount,
                        ShelfCount      = shelfCount,
                        TorchCount      = torchCount,
                        Type            = type,
                    });
                    nextRoomId++;
                }
            }
            // Always register the outdoor room (Id=1) as a fallback so
            // shroomps outside have a valid lookup target.
            if (!map.HasRoom(OutdoorRoomId))
            {
                map.RegisterRoom(new Room
                {
                    Id              = OutdoorRoomId,
                    TileCount       = 0,   // not metric'd; outdoors is always "outdoors"
                    Type            = RoomType.Outdoor,
                });
            }
        }

        private static void TryEnqueue(LocalMap map, int x, int y, ushort id, Queue<(int X, int Y)> queue)
        {
            if (!map.InBounds(x, y)) return;
            if (!map.IsPassable(x, y)) return;
            // v0.6.0 — doors don't propagate the flood fill, so a closed
            // doorway between two enclosures keeps each side as its own room.
            if (IsDoorTile(map, x, y)) return;
            if (map.GetStructure(x, y).RoomId != 0) return;
            map.SetStructureRoomId(x, y, id);
            queue.Enqueue((x, y));
        }

        // v0.6.0 — Door / DoorPlanned both count as boundaries. Only the
        // built Door is a real obstacle to room flood-fill; the blueprint
        // ghost (DoorPlanned) is treated the same so the player can see how
        // the room will resolve once the door is built without waiting for
        // construction completion.
        private static bool IsDoorTile(LocalMap map, int x, int y)
        {
            var t = map.GetStructure(x, y).Type;
            return t == StructureType.Door || t == StructureType.DoorPlanned;
        }

        // v0.6.0 — count a built Door adjacent to a room toward the room's
        // furniture/beauty totals. Only built Doors (not blueprints) count.
        // The HashSet keys on tile-linear-index so a door touching the same
        // room from two sides only contributes once.
        private static void TryCountAdjacentDoor(LocalMap map, int x, int y, HashSet<int> seen,
            ref int doorCount, ref float doorBeauty)
        {
            if (!map.InBounds(x, y)) return;
            var slot = map.GetStructure(x, y);
            if (slot.Type != StructureType.Door) return;
            int key = y * map.Width + x;
            if (!seen.Add(key)) return;
            doorCount++;
            doorBeauty += QualityMeta.ValueMul(slot.Quality);
        }

        private static void OverwriteRoomId(LocalMap map, ushort fromId, ushort toId)
        {
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width;  x++)
                if (map.GetStructure(x, y).RoomId == fromId)
                    map.SetStructureRoomId(x, y, toId);
        }
    }

    // Per-room metadata. Beauty score is derived from contents — RimWorld's
    // pattern: positive contributions from furniture / floor / decoration;
    // negative from corpses / debris. v0.5.23 ships a simple model:
    //   • +1 per furniture piece (Shelf, Workbench, Hearth, Door)
    //   • +0.5 per Floor tile (laid floor better than dirt)
    //   • +5 per Hearth (warm, cosy)
    //   • -3 per CorpseCount (placeholder; corpses-in-room detection
    //     deferred until v0.6 thought integration)
    // Beauty thresholds for the player-facing thought tier:
    //   ≥ 10 — "Beauty: Pretty" (+3 mood)
    //   < -3 — "Beauty: Ugly"   (-3 mood)
    //   else — no thought
    // v0.5.84t (Phase 5H — Roadmap §5.5) — room type inference. Derived from
    // the furniture inside the room at detection time. Player override (a
    // "Designate Room Type" UI) is deferred to v0.6+; the auto-infer covers
    // the common case. Priority: Bedroom > Kitchen > Workshop > Storage > Generic.
    public enum RoomType : byte
    {
        Outdoor   = 0,
        Generic   = 1,
        Bedroom   = 2,   // has at least one Bed
        Kitchen   = 3,   // has Hearth(s), no Bed
        Workshop  = 4,   // has Workbench(s), no Bed/Hearth
        Storage   = 5,   // only Shelves (or only floors + Shelves)
    }

    public class Room
    {
        public ushort Id;
        public int    TileCount;
        public int    FloorCount;
        public int    FurnitureCount;
        public int    HearthCount;
        public int    CorpseCount;
        // v0.5.84t — additional per-furniture counts for room-type inference.
        public int    BedCount;
        public int    WorkbenchCount;
        public int    ShelfCount;
        // v0.5.84t — torch count per room. Heat: +2°C per torch (folded into
        // TemperatureOffsetC). Light: tracked for future glow-grid system
        // (Phase 10) — no visual effect today.
        public int    TorchCount;
        // v0.5.84t — derived room type (Bedroom / Kitchen / Workshop / Storage /
        // Generic). Outdoor room (Id == OutdoorRoomId) always reports Outdoor.
        // Computed at Rebuild from per-furniture counts.
        public RoomType Type { get; set; } = RoomType.Generic;

        // v0.5.30 — quality-weighted beauty sums for furniture + floor.
        // Populated by RoomDetector.Rebuild from each tile's
        // StructureSlot.Quality via QualityMeta.ValueMul. A masterwork
        // shelf (×3.5) contributes much more to BeautyScore than a
        // crude shelf (×0.5) — encourages the player to use skilled
        // builders on furniture, RimWorld-style.
        public float FurnitureBeauty;
        public float FloorBeauty;

        // BeautyScore folds quality-weighted furniture + floor + hearths
        // — minus corpses. Hearths get a flat +5 each on top of their
        // quality contribution because a hearth's beauty is in its
        // function ("warm, cosy") not its build polish.
        public float BeautyScore =>
            FurnitureBeauty + FloorBeauty * 0.5f + HearthCount * 5f - CorpseCount * 3f;

        public bool IsOutdoor => Id == RoomDetector.OutdoorRoomId;

        // v0.5.24 (Phase 5G — Roadmap §5.10) — Roofing. A room is "roofed"
        // when it's enclosed (any non-outdoor room). RimWorld's player-
        // painted roof zones are a v0.6+ refinement; for v0.5.24 the
        // detector auto-roofs every enclosed room. Shroomps inside a roofed
        // room don't get rained on (Phase 10 weather wire-in) and the
        // room's temperature is sheltered from outdoor ambient.
        public bool HasRoof => !IsOutdoor;

        // v0.5.24 (Phase 5G — Roadmap §5.12) — Temperature. Simple model:
        // outdoor rooms inherit biome ambient (15 °C default). Indoor
        // rooms get a baseline +2 °C insulation, plus +10 °C per Hearth.
        // A room with one Hearth at 15 °C ambient sits at 27 °C — warm
        // and comfortable. Per-tile diffusion (RimWorld parity) is
        // deferred to v0.6 since per-room is enough for the meaningful
        // gameplay coupling (item decay, shroomp comfort).
        //
        // The actual ambient is set by ItemDeteriorationSystem /
        // shroomp-comfort checks based on biome + weather (Phase 10).
        // For v0.5.24 we just expose the room-aware offset; callers
        // combine with ambient.
        public float TemperatureOffsetC =>
            IsOutdoor ? 0f
                      // v0.5.84t — torches add +2°C each on top of Hearth contribution.
                      : 2f + (HearthCount * 10f) + (TorchCount * 2f);
    }
}
