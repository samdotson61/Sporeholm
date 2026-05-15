using System;
using System.Collections.Generic;
using Godot;
using SmurfulationC.World;

namespace SmurfulationC.Simulation
{
    // v0.3.47 (Phase 4 sub-B) — A* pathfinder over LocalMap passability.
    // v0.4.18 — rewritten for the 250-smurf perf target. Key changes vs
    // the v0.4.13 version:
    //
    //   • Generation-counter arrays. The old code reset gScore / parent /
    //     closed across the full W×H span on every call (~108 000 writes
    //     per call at 240×150). Now each call increments a per-thread
    //     generation int; entries are "fresh" iff their stored generation
    //     != current. No reset, O(1) startup, regardless of map size.
    //   • Cached PriorityQueue. The old `new PriorityQueue<int, int>()`
    //     per call allocated a fresh heap backing array; the queue is now
    //     a thread-local field cleared at the start of each search.
    //   • Fill-into-buffer result. Callers pass `Smurf.PathWaypoints` and
    //     it gets cleared + filled in place — no `new List<Vector2>(32)`
    //     per call.
    //   • Parallel `Dx` / `Dy` / `Cost` arrays. The old `foreach (var (dx,
    //     dy, cost) in Dirs)` allocated a tuple deconstruction per step;
    //     the new layout is index-by-`int i` over three flat arrays.
    //   • Raw `bool[]` passability access. Inner expansion bypasses
    //     `LocalMap.IsPassable`'s virtual + double-bounds path and reads
    //     `_passable` directly through `LocalMap.PassableUnsafe`.
    //
    // 8-connected grid (cardinal + diagonal) with diagonal cut-corner
    // enforcement. Manhattan-with-diagonal-credit heuristic. Caps the
    // closed set at MaxNodes (defaults to 1024) so a single pathfind on a
    // 320×200 map can't lock up the sim thread.
    public static class Pathfinder
    {
        // Square pixel distance beyond which BehaviorSystem prefers A*.
        // 8 tiles = 128 px. Squared = 16384. (v0.4.16 BehaviorSystem also
        // forces A* unconditionally for designation tasks regardless of
        // this threshold; the constant stays for non-designation callers.)
        public const float PreferAStarDistSqPx = 128f * 128f;

        public const int MaxNodes = 1024;

        // Diagonal cost ≈ √2; cardinal = 1.0. Multiplied by 100 and stored
        // as int so the priority queue can use cheap integer comparison.
        private const int CardinalCost = 100;
        private const int DiagonalCost = 141;   // round(100 * √2)

        // v0.4.58 — RimWorld-style soft-cost crowd avoidance. Adds 175 per
        // OTHER smurf currently occupying the candidate tile. Cardinal
        // step is 100, so one blocker bumps a tile's effective cost
        // from 100 → 275 (1 cardinal vs ~2 cardinals' worth of detour).
        // Calibrated to match RimWorld's `Cost_PawnCollision = 175`
        // (decompiled `Verse.AI.PathFinder`): the pawn will detour 1-2
        // tiles around a single blocker, but plow through if the
        // alternative is much longer (e.g. all alternatives are also
        // crowded). Replaces the steering-layer "crowdedFallback"
        // oscillation at saturated work faces — the path itself now
        // routes around the cluster.
        //
        // Admissibility: the heuristic doesn't see collision cost, so it
        // remains a lower bound on true path cost. A* still finds the
        // optimal path; just expands a few more nodes in crowded scenes.
        private const int PawnCollisionCost = 175;

        // 8 movement directions, parallel arrays to avoid tuple
        // deconstruction in the hot inner loop. Order: cardinals first
        // (so straight motion is preferred when paths are equal-cost),
        // then diagonals.
        private static readonly sbyte[] DX   = { 1, -1,  0,  0,  1,  1, -1, -1 };
        private static readonly sbyte[] DY   = { 0,  0,  1, -1,  1, -1,  1, -1 };
        private static readonly int[]   Cost = { CardinalCost, CardinalCost, CardinalCost, CardinalCost,
                                                  DiagonalCost, DiagonalCost, DiagonalCost, DiagonalCost };

        // ── Fill-into-buffer entry point (preferred — no GC pressure) ─────

        // Performs the A* search and fills `resultBuffer` with the
        // tile-centre pixel waypoints. The buffer is cleared at entry, so
        // a previously-populated buffer can be reused freely. Returns
        // false when no path exists (unreachable, OOB, or expansion
        // budget exceeded); in that case `resultBuffer` is left empty.
        //
        // v0.4.58 — `smurfPerTile` + `askerTileIdx` are an optional
        // RimWorld-style crowd-avoidance input. When provided, each
        // candidate tile's expansion cost is increased by
        // `PawnCollisionCost * (other_smurfs_on_tile)`. The asker's own
        // tile is exempt (the asker's count is subtracted). Pass `null`
        // to disable (legacy behaviour). The array must be sized to at
        // least `map.Width * map.Height`; smaller buffers are ignored.
        public static bool FindPath(LocalMap map, Vector2 fromPixel,
            (int X, int Y) toTile, List<Vector2> resultBuffer,
            int[]? smurfPerTile = null, int askerTileIdx = -1)
        {
            resultBuffer.Clear();
            if (map == null) return false;

            int sx = (int)(fromPixel.X / LocalMap.TileSize);
            int sy = (int)(fromPixel.Y / LocalMap.TileSize);
            int tx = toTile.X;
            int ty = toTile.Y;

            if (!map.InBounds(sx, sy) || !map.InBounds(tx, ty)) return false;

            int W = map.Width;
            int H = map.Height;
            bool[] passable = map.PassableUnsafe;

            // If the destination is impassable, route to the passable
            // neighbour that shares the smurf's region when one exists
            // (v0.4.13). Falls back to the first 8-neighbour passable
            // tile for the SimPos-in-wall edge case.
            if (!passable[ty * W + tx])
            {
                var adj = FindReachableAdjacent(map, sx, sy, tx, ty, passable, W, H)
                       ?? FindAdjacentPassable(passable, tx, ty, W, H);
                if (!adj.HasValue) return false;
                tx = adj.Value.X; ty = adj.Value.Y;
            }

            if (sx == tx && sy == ty) return true;   // already at goal

            // O(1) DF-region reachability gate. If start and goal sit in
            // distinct connected components there's provably no
            // 8-connected path, so fail fast before opening the heap.
            ushort sRid = map.GetRegion(sx, sy);
            ushort tRid = map.GetRegion(tx, ty);
            if (sRid != 0 && tRid != 0 && sRid != tRid) return false;

            int cap = W * H;
            int[]    gScore = RentGScore(cap);
            int[]    parent = RentParent(cap);
            int[]    genArr = RentGenArr(cap);
            int      gen    = NextGeneration();
            var      open   = RentOpen();
            open.Clear();

            // v0.4.58 — crowd-cost guard. Only enable if the caller passed
            // an occupancy array sized for this map. Sized mismatch would
            // be an out-of-band write into wrong cells; degrade silently.
            bool useCrowdCost = smurfPerTile != null && smurfPerTile.Length >= cap;

            int startIdx = sy * W + sx;
            int goalIdx  = ty * W + tx;

            gScore[startIdx] = 0;
            parent[startIdx] = -1;
            genArr[startIdx] = gen;
            open.Enqueue(startIdx, Heuristic(sx, sy, tx, ty));

            // `closed` is encoded as `genArr[idx] == -gen` (any negative
            // sentinel paired with the current generation). Saves the
            // third array. Reuses `genArr` slot to mean both "touched"
            // and "expanded" depending on sign.
            int expanded = 0;

            while (open.Count > 0)
            {
                int idx = open.Dequeue();
                int g = genArr[idx];
                if (g == -gen) continue;       // already closed in this generation
                if (g != gen) continue;        // stale entry from a previous search
                genArr[idx] = -gen;            // mark closed

                if (idx == goalIdx)
                {
                    ReconstructInto(parent, idx, W, resultBuffer);
                    return true;
                }
                if (++expanded > MaxNodes) return false;

                int cx = idx % W;
                int cy = idx / W;
                int curG = gScore[idx];

                for (int i = 0; i < 8; i++)
                {
                    int dx = DX[i];
                    int dy = DY[i];
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if ((uint)nx >= (uint)W) continue;
                    if ((uint)ny >= (uint)H) continue;
                    int nIdx = ny * W + nx;
                    if (!passable[nIdx]) continue;
                    if (dx != 0 && dy != 0)
                    {
                        // Diagonal step: both orthogonals must be passable
                        // or the smurf would cut through a wall corner.
                        if (!passable[cy * W + nx]) continue;
                        if (!passable[ny * W + cx]) continue;
                    }
                    int nGen = genArr[nIdx];
                    if (nGen == -gen) continue;   // closed
                    int stepCost = Cost[i];
                    // v0.4.58 — soft-cost crowd avoidance. Each smurf
                    // occupying the candidate tile adds PawnCollisionCost
                    // to the step. Self-exemption: if the candidate is
                    // the asker's own tile, subtract 1 from the count
                    // (the asker is included in PopulateOccupancyGrid's
                    // tally). Path naturally routes around clusters
                    // instead of running into them and relying on
                    // local-steering side-step fallbacks.
                    if (useCrowdCost)
                    {
                        int crowd = smurfPerTile![nIdx];
                        if (nIdx == askerTileIdx && crowd > 0) crowd--;
                        if (crowd > 0) stepCost += PawnCollisionCost * crowd;
                    }
                    int tentative = curG + stepCost;
                    if (nGen != gen || tentative < gScore[nIdx])
                    {
                        gScore[nIdx] = tentative;
                        parent[nIdx] = idx;
                        genArr[nIdx] = gen;
                        open.Enqueue(nIdx, tentative + Heuristic(nx, ny, tx, ty));
                    }
                }
            }

            return false;
        }

        // ── Legacy API shim (allocates a List; new callers should use the
        // fill-into-buffer overload above) ────────────────────────────────

        // Kept so any out-of-tree caller still compiles. Internal callers
        // should pass `Smurf.PathWaypoints` to the buffer overload to
        // eliminate the per-call list allocation.
        public static List<Vector2>? FindPath(LocalMap map, Vector2 fromPixel, (int X, int Y) toTile)
        {
            var buf = new List<Vector2>(32);
            return FindPath(map, fromPixel, toTile, buf) ? buf : null;
        }

        // ── Heuristic + reconstruction ────────────────────────────────────

        // Manhattan with diagonal credit: cost is min(dx,dy)·diag +
        // (|dx|-|dy|)·cardinal. Admissible and consistent on 8-grid.
        private static int Heuristic(int sx, int sy, int tx, int ty)
        {
            int dx = tx - sx; if (dx < 0) dx = -dx;
            int dy = ty - sy; if (dy < 0) dy = -dy;
            int diag = dx < dy ? dx : dy;
            int straight = (dx > dy ? dx : dy) - diag;
            return diag * DiagonalCost + straight * CardinalCost;
        }

        private static void ReconstructInto(int[] parent, int goalIdx, int W, List<Vector2> outList)
        {
            // Walk parents from goal back to start, then reverse. The
            // start tile (parent == -1) is excluded since the smurf is
            // already there.
            int idx = goalIdx;
            float half = LocalMap.TileSize * 0.5f;
            while (idx >= 0 && parent[idx] >= 0)
            {
                int x = idx % W;
                int y = idx / W;
                outList.Add(new Vector2(x * LocalMap.TileSize + half,
                                        y * LocalMap.TileSize + half));
                idx = parent[idx];
            }
            outList.Reverse();
        }

        // ── Per-thread scratch ────────────────────────────────────────────
        //
        // The sim thread is currently the only caller of FindPath. The
        // ThreadLocal wrapper costs ~30 ns per Value access but keeps
        // future off-thread callers honest. Each buffer is grown lazily
        // to the largest map seen so far and then reused.

        private static readonly System.Threading.ThreadLocal<int[]>             _gScoreBuf = new(() => Array.Empty<int>());
        private static readonly System.Threading.ThreadLocal<int[]>             _parentBuf = new(() => Array.Empty<int>());
        private static readonly System.Threading.ThreadLocal<int[]>             _genArrBuf = new(() => Array.Empty<int>());
        private static readonly System.Threading.ThreadLocal<int>               _genCount  = new(() => 0);
        private static readonly System.Threading.ThreadLocal<PriorityQueue<int, int>> _openBuf = new(() => new PriorityQueue<int, int>(256));

        private static int[] RentGScore(int n)
        {
            if (_gScoreBuf.Value!.Length < n) _gScoreBuf.Value = new int[n];
            return _gScoreBuf.Value!;
        }
        private static int[] RentParent(int n)
        {
            if (_parentBuf.Value!.Length < n) _parentBuf.Value = new int[n];
            return _parentBuf.Value!;
        }
        private static int[] RentGenArr(int n)
        {
            if (_genArrBuf.Value!.Length < n)
            {
                _genArrBuf.Value = new int[n];   // fresh array starts at 0, never matches any gen
                _genCount.Value  = 0;            // align generation with the fresh buffer
            }
            return _genArrBuf.Value!;
        }
        private static PriorityQueue<int, int> RentOpen() => _openBuf.Value!;

        private static int NextGeneration()
        {
            int g = _genCount.Value + 1;
            if (g <= 0)
            {
                // Overflow: very-very rare (2 billion searches). Wipe the
                // gen array so we can safely restart at gen = 1.
                Array.Clear(_genArrBuf.Value!, 0, _genArrBuf.Value!.Length);
                g = 1;
            }
            _genCount.Value = g;
            return g;
        }

        // ── Adjacent-tile pickers ─────────────────────────────────────────

        private static (int X, int Y)? FindAdjacentPassable(bool[] passable, int tx, int ty, int W, int H)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                if (passable[ny * W + nx]) return (nx, ny);
            }
            return null;
        }

        private static (int X, int Y)? FindReachableAdjacent(LocalMap map, int sx, int sy,
            int tx, int ty, bool[] passable, int W, int H)
        {
            ushort sRid = map.GetRegion(sx, sy);
            if (sRid == 0) return null;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                if (!passable[ny * W + nx]) continue;
                if (map.GetRegion(nx, ny) == sRid) return (nx, ny);
            }
            return null;
        }
    }
}
