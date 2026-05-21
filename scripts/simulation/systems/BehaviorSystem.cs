using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // Roadmap §3.3 / §3.6 / §3.7 / §3.8 — Shroomp behavior driver.
    //
    // Architecture overview:
    //   • Runs on the simulation thread once per sim-system interval (60 ticks).
    //   • For each living shroomp: select highest-priority valid task across the
    //     three tiers (critical needs → role → idle), advance SimPos toward the
    //     task's target by SimSpeed × dt, and apply task effects on arrival.
    //   • Player orders dequeue from a thread-safe queue and override the
    //     evaluation when present and not preempted by a critical-need emergency.
    //   • Designations on LocalTile gate Tier-2 role tasks (excavation, gather).
    //
    // The system reads only from LocalMap (terrain + vegetation) — it never
    // *writes* terrain except through LocalMap.HarvestVegetation, which logs
    // mutations and notifies the renderer.
    public static class BehaviorSystem
    {
        // Per-tick effect rates (units per system-tick = once per second at 1×).
        private const float EatRate         = 18f;
        private const float SleepRate       = 22f;
        private const float SocializeRate   = 14f;
        private const float AttuneRate      = 12f;
        // v0.4.63 (G4) — Joy restored per second by idle activities. Calibrated
        // so a 5-second loiter restores ~25 Joy (about 1/4 the bar). Joy
        // decay is 0.005/call × ~16.7 calls/day = 0.084/day baseline, so a
        // few minutes of idle a day comfortably tops up.
        private const float JoyRate         = 5f;
        private const float SeekSafetyRate  = 16f;
        private const float HealRate        =  8f;

        // Distance (in pixels) at which a shroomp is considered "at" their target.
        // Slightly larger than half a tile so movement converges cleanly.
        // v0.3.38 — bumped from 10 → 14 px. The previous radius was tight
        // enough that a shroomp could be physically *inside* its target
        // adjacent tile (an 8-px-radius square) yet not register as
        // arrived, especially when steering deflections landed it at the
        // tile edge rather than centre. 14 px (≈ √2 × 10) covers the full
        // diagonal of the adjacent tile, so anywhere inside it counts as
        // arrival and ApplyTaskEffect fires. Player reported shroomps
        // "standing at their task for multiple seconds without executing";
        // most cases were this off-by-radius issue.
        private const float ArrivalRadius   = 14f;

        // ── LOD tick groups (v0.3.39 / O-H.2) ───────────────────────────────
        //
        // Off-screen shroomps don't need 60 Hz behaviour updates — the player
        // can't see micro-deltas in their position. This is the same trick
        // Songs of Syx and RimWorld use to scale to thousands of pawns.
        //
        // Three LOD bands assigned per shroomp based on camera-distance:
        //   Phase 0 (Hot)  → shroomps within ~20 tiles of camera. Ticked every
        //                    sim tick. Per-step distance = SimSpeed × dt.
        //   Phase 1 (Warm) → shroomps within ~50 tiles. Ticked every 3 sim
        //                    ticks. Per-step distance = SimSpeed × 3 × dt so
        //                    the shroomp covers the same total distance per
        //                    unit time as a hot shroomp. Slot 0–2 distributes
        //                    fairness across the three ticks (so a 30-shroomp
        //                    Warm band fires ~10 each tick, not 30 once).
        //   Phase 2 (Cold) → everything else. Ticked every 6 sim ticks.
        //                    Per-step distance = SimSpeed × 6 × dt.
        //
        // The per-step compensation matters: without it, cold shroomps would
        // visibly walk 6× slower than hot ones, which is a UX bug. Because
        // we're using the existing local-steering loop, a 6× larger step
        // would clear small obstacles in one hop and increase the "walks
        // through a wall" risk — so MoveOneTick subdivides the step into
        // 1-px-equivalent sub-steps when the tick interval is > 1.
        private const int WarmInterval = 3;
        private const int ColdInterval = 6;
        // v0.3.40 — Hot range expanded 20 → 40 tiles. The previous 20-tile
        // ring (320 px) was smaller than a default-zoom viewport (~40×25
        // tiles visible at the standard zoom level), so shroomps at the edges
        // of the visible area were ending up in the Warm band and showing
        // the LOD stutter through the camera. 40 tiles covers viewport
        // corners at zoom 1× and 2×; at lower zoom the lerp in
        // ShroompColonyView smooths what stutter remains.
        private const float HotRangePx  = 40f * 16f;     // 40 tiles = 640 px
        private const float WarmRangePx = 100f * 16f;    // 100 tiles = 1600 px

        // Called from SimulationCore.Run every ~32 ticks. Walks every alive
        // shroomp, classifies by distance to `cameraFollow`, assigns phase + slot.
        // Keeping this off the per-tick hot path is fine — shroomps barely move
        // across 32 ticks at any speed multiplier, so band membership is
        // stable on that timescale.
        public static void AssignTickPhases(IReadOnlyList<Shroomp> shroomps, Godot.Vector2 cameraFollow)
        {
            int colorWarm = 0, colorCold = 0;
            foreach (var s in shroomps)
            {
                if (!s.IsAlive) continue;
                float dx = s.SimPos.X - cameraFollow.X;
                float dy = s.SimPos.Y - cameraFollow.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < HotRangePx)       { s.TickPhase = 0; s.TickSlot = 0; }
                else if (dist < WarmRangePx) { s.TickPhase = 1; s.TickSlot = (byte)(colorWarm++ % WarmInterval); }
                else                          { s.TickPhase = 2; s.TickSlot = (byte)(colorCold++ % ColdInterval); }
            }
        }

        // True when this shroomp should tick on the current sim tick. Hot
        // shroomps always tick; warm/cold shroomps tick when their slot matches
        // the current tick modulo their group interval.
        private static bool ShouldTick(Shroomp s, long currentTick)
        {
            if (s.TickPhase == 0) return true;
            int interval = s.TickPhase == 1 ? WarmInterval : ColdInterval;
            return (currentTick % interval) == s.TickSlot;
        }

        // v0.4.19 — per-tick shroomp-occupancy grid. `_shroompPerTile[idx]` is
        // the number of alive shroomps whose SimPos rounds to tile `idx`.
        // Rebuilt once at the top of every Tick from the shroomp list, then
        // read-only for the rest of the tick. Used by the soft-collision
        // local steering (`MoveOneTick`) and the claim-aware adjacent-
        // tile picker (`NearestAdjacentPassableTile`) so multiple shroomps
        // converging on a work face spread across distinct approach
        // tiles instead of all stacking onto the same one.
        private static int[] _shroompPerTile = System.Array.Empty<int>();
        // v0.4.29 — first non-yielding shroomp at each tile index. Lets the
        // yield-trigger find WHO to ask to lie down without re-walking the
        // shroomp list. Cleared & repopulated alongside _shroompPerTile.
        private static Shroomp?[] _firstShroompPerTile = System.Array.Empty<Shroomp?>();
        // v0.5.76 — per-tick claim counter. Incremented by MoveOneTick when
        // a shroomp commits to stepping onto a tile other than its current
        // one (see ClaimTileForMove). Read by TileHasOtherShroomp alongside
        // the persistent _shroompPerTile so a SECOND shroomp deciding in
        // the same tick sees the first shroomp's claim and steers around
        // it. Pre-v0.5.76 the occupancy grid was a snapshot rebuilt once
        // per tick, so N shroomps converging on the same chokepoint all
        // saw the destination as empty and all stepped onto it, producing
        // the doorway / corner pileups Sam screenshotted ("Pawns seem to
        // move well for a time then get stuck on each other and on/in
        // corners"). The claim counter is cleared at the start of every
        // batch tick by PopulateOccupancyGrid.
        private static int[] _claimedThisTick = System.Array.Empty<int>();
        private static int   _occGridWidth;       // captured at populate-time so helpers don't need `map`

        private static void PopulateOccupancyGrid(LocalMap? map, IReadOnlyList<Shroomp> shroomps)
        {
            if (map == null) { _occGridWidth = 0; return; }
            int W = map.Width, H = map.Height;
            int need = W * H;
            if (_shroompPerTile.Length < need)
            {
                _shroompPerTile      = new int[need];
                _firstShroompPerTile = new Shroomp?[need];
                _claimedThisTick     = new int[need];          // v0.5.76
            }
            else
            {
                System.Array.Clear(_shroompPerTile,      0, need);
                System.Array.Clear(_firstShroompPerTile, 0, need);
                System.Array.Clear(_claimedThisTick,     0, need);   // v0.5.76
            }
            _occGridWidth = W;
            int count = shroomps.Count;
            for (int i = 0; i < count; i++)
            {
                var s = shroomps[i];
                if (!s.IsAlive) continue;
                // v0.4.29 — yielding (lying-down) shroomps are skipped so the
                // soft-collision steering and the FindNearestExcavate
                // approach-blocked check both treat their tile as "free".
                // Lets the shroomp they're yielding for step over them.
                if (s.YieldingTicks > 0) continue;
                int tx = (int)(s.SimPos.X / LocalMap.TileSize);
                int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
                if ((uint)tx < (uint)W && (uint)ty < (uint)H)
                {
                    int idx = ty * W + tx;
                    _shroompPerTile[idx]++;
                    if (_firstShroompPerTile[idx] == null)
                        _firstShroompPerTile[idx] = s;
                }
            }
        }

        // v0.4.29 — DF-style yield trigger. When a shroomp has been stuck for
        // YieldTriggerTicks behind another (non-yielding) shroomp in the
        // direction it wants to walk, the BLOCKER lies down for
        // YieldDurationTicks. The stuck shroomp's StuckTicks resets so its
        // re-path / give-up window starts fresh while the path is open.
        // Idempotent: a blocker already yielding stays as-is.
        // Sits between StuckRePathTicks (8) and StuckThreshold (18): the
        // re-pathfind gets first crack at corner-stuck oscillation, then if
        // the shroomp is *still* stuck (almost always meaning a real shroomp is
        // in the way) we ask the blocker to lie down. Give-up at 18 stays
        // as the final fallback if even the yield doesn't unblock us.
        //
        // v0.4.59 — every retry / yield / give-up timing roughly halved
        // from the prior values. Sam: "Decrease amount of time to retry
        // actions." With v0.4.58's A* crowd avoidance handling strategic
        // routing, the steering layer's dwell time on stuck states adds
        // less value (paths re-route around clusters from the start, so
        // by the time stuck-detect fires the cluster is already
        // dissolving). Faster reactions feel more responsive without
        // losing the recovery window.
        // v0.5.56 — YieldTriggerTicks 12 → 6. Pairs with the cardinal-neighbour
        // fallback below — earlier trigger + broader blocker search means
        // single-tile-hallway jams resolve before the StuckThreshold (18)
        // give-up window fires. Sam screenshot: "Shroomps getting stuck in
        // single-tile hallways again. Not using lay-down mechanic?"
        private const int YieldTriggerTicks  = 6;
        private const int YieldDurationTicks = 30;
        private static void TryTriggerBlockerYield(Shroomp s, LocalMap map)
        {
            if (_occGridWidth == 0) return;
            // Pass 1 — directional ahead-tile check (the original behaviour).
            // Works for the common case where the shroomp's walk direction is
            // cardinal and the blocker sits in that direction.
            Vector2 walkTo = ResolveWalkTarget(s, map);
            Vector2 diff = walkTo - s.SimPos;
            if (diff.LengthSquared() >= 0.0001f)
            {
                Vector2 ahead = s.SimPos + diff.Normalized() * (LocalMap.TileSize * 0.75f);
                if (TryYieldBlockerAt(s, map, (int)(ahead.X / LocalMap.TileSize),
                                              (int)(ahead.Y / LocalMap.TileSize)))
                    return;
            }
            // v0.5.56 — Pass 2: cardinal-neighbour fallback. The directional
            // check fails when (a) the shroomp's walk target is very close
            // (diff < ε), (b) the diff is non-cardinal so "ahead" lands on a
            // wall instead of the blocker's tile, or (c) the blocker is on
            // a non-primary cardinal (e.g., two shroomps on the SAME tile after
            // a climb-over — both are at curTile and the "ahead" tile is
            // empty). Without this fallback the yield silently never fires
            // and the shroomp rides out the full StuckThreshold to give-up,
            // looping endlessly. Walks N/S/E/W around the shroomp's current
            // tile and asks any non-yielding blocker there to lie down.
            int sTx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sTy = (int)(s.SimPos.Y / LocalMap.TileSize);
            // Also check the shroomp's own tile — handles the same-tile-after-
            // climb-over case where the blocker is right under the shroomp.
            if (TryYieldBlockerAt(s, map, sTx,     sTy    )) return;
            if (TryYieldBlockerAt(s, map, sTx + 1, sTy    )) return;
            if (TryYieldBlockerAt(s, map, sTx - 1, sTy    )) return;
            if (TryYieldBlockerAt(s, map, sTx,     sTy + 1)) return;
            if (TryYieldBlockerAt(s, map, sTx,     sTy - 1)) return;
        }

        // v0.5.56 — yield-trigger helper for one specific tile. Returns true
        // if a non-yielding blocker was found there and asked to lie down
        // (with the asker's StuckTicks reset). Returns false (no-op) if the
        // tile is OOB, empty in the occupancy grid, occupied by the asker
        // itself, or occupied by an already-yielding shroomp.
        private static bool TryYieldBlockerAt(Shroomp s, LocalMap map, int tx, int ty)
        {
            if ((uint)tx >= (uint)map.Width || (uint)ty >= (uint)map.Height) return false;
            int idx = ty * _occGridWidth + tx;
            if ((uint)idx >= (uint)_firstShroompPerTile.Length) return false;
            var blocker = _firstShroompPerTile[idx];
            if (blocker == null || blocker == s || blocker.YieldingTicks > 0) return false;
            blocker.YieldingTicks = YieldDurationTicks;
            // Give the now-unblocked shroomp a clean stuck window so the
            // existing re-path / give-up timers don't immediately fire on
            // top of the resolution.
            s.StuckTicks  = 0;
            s.RePathTried = false;
            // v0.5.82 — also reset the BLOCKER's stuck state so when they
            // stand up after YieldDurationTicks they get a fresh budget.
            // Pre-v0.5.82 a symmetric corridor deadlock (A blocked by B,
            // B blocked by A, both at StuckTicks≈5) would have A yield B,
            // B lie down for 30 ticks, then stand up still at StuckTicks=5
            // — one more frame of failed motion fires B's own yield-or-
            // give-up window immediately. Now the lie-down resets both
            // sides' counters, mirroring RimWorld's "fresh start after
            // pawn cooldown" semantics.
            blocker.StuckTicks  = 0;
            blocker.RePathTried = false;
            return true;
        }

        // v0.4.58 — compute the shroomp's current tile index in the per-tick
        // occupancy grid. Returns -1 when the grid hasn't been populated
        // yet (very first tick after map bind) or when the shroomp is OOB.
        // Used as the `askerTileIdx` self-exemption arg to Pathfinder so
        // the asker doesn't pay the crowd-cost penalty on its own tile.
        private static int OccTileIdx(Shroomp s)
        {
            if (_occGridWidth <= 0) return -1;
            int tx = (int)(s.SimPos.X / LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
            return ty * _occGridWidth + tx;
        }

        // True iff some *other* shroomp occupies the candidate tile. The
        // shroomp about to move is exempt — they're currently contributing
        // to the count of their own tile, and "I'm in the way of myself"
        // would be a meaningless veto. Caller passes the shroomp's current
        // tile index so the exemption can be done by index compare.
        // v0.5.76 — also factor in same-tick claims (see _claimedThisTick).
        // Without this, N shroomps deciding in the same batch tick all see
        // a destination tile as empty and all step onto it, producing the
        // doorway / corner pileups Sam screenshotted. The claim counter
        // makes second-and-later shroomps see the first shroomp's pending
        // commit and steer around.
        private static bool TileHasOtherShroomp(int candidateIdx, int currentIdx)
        {
            if (_occGridWidth == 0 || (uint)candidateIdx >= (uint)_shroompPerTile.Length)
                return false;
            int n = _shroompPerTile[candidateIdx];
            if (candidateIdx == currentIdx) n--;   // subtract self
            if ((uint)candidateIdx < (uint)_claimedThisTick.Length)
                n += _claimedThisTick[candidateIdx];   // v0.5.76 — pending commits this tick
            return n > 0;
        }

        // v0.5.76 — register that this shroomp is about to step onto a new
        // tile this tick. Called by MoveOneTick AFTER bestChosen is picked
        // and BEFORE the next shroomp in the batch evaluates its own
        // candidates. The +1 propagates through TileHasOtherShroomp so
        // later shroomps see the tile as occupied even though the snapshot
        // _shroompPerTile (from PopulateOccupancyGrid) hasn't been rebuilt
        // yet. Idempotent for "stayed on own tile" — only fires when the
        // destination tile index differs from the start tile.
        private static void ClaimTileForMove(int prevIdx, int newIdx)
        {
            if (newIdx == prevIdx) return;
            if (_occGridWidth == 0) return;
            if ((uint)newIdx >= (uint)_claimedThisTick.Length) return;
            _claimedThisTick[newIdx]++;
        }

        // v0.5.9 — task viability check. RimWorld's JobDriver fail-condition
        // pattern: each Toil registers fail conditions (FailOnDestroyedOrNull,
        // FailOnForbidden, FailOnSomeoneElseHaulingIt, etc.) that are
        // evaluated every tick during the job. If any fail condition is
        // true, the JobDriver ends with JobCondition.Incompletable and the
        // pawn drops the job + asks JobGiver for a new one.
        //
        // Sporeholm equivalent: structural sanity check on the current
        // task's target. Designation still painted? Vegetation still
        // present? Haul item still on the source tile? Player-order
        // destination still reachable? If any of these are now false, the
        // task can't be completed and the shroomp should release the claim
        // and re-evaluate, instead of walking + jittering against a goal
        // that no longer exists.
        //
        // Sam: "I see pawns getting stuck on haul orders, excavate orders,
        // and right-click move orders when they find themselves unable to
        // complete the task, then never reassigning causing a lock and
        // visible jitter."
        //
        // Crucially the check is *structural*, not *progress*. A crafting
        // task that will take 30 seconds is still valid as long as the
        // workbench exists + ingredients are reachable — the shroomp holds
        // the task and accumulates progress. Same for slow mining (Phase
        // 6 tool durability). Sam: "a shroomp that is completing a task,
        // like crafting, mining, or hauling, should know to reassign if
        // that task is impossible while still holding onto the task if it
        // takes 15-30s."
        //
        // Idle tasks (Wander/Loiter/Observe/Converse/Meditate/VisitFavorite),
        // critical-need tasks (Eat/Sleep/Attune/Socialize), and any task
        // without resolved tile coords return true unconditionally —
        // those are either self-contained (idle effects) or handled by
        // their own system (e.g., HaulSystem manages Phase 1 → Phase 2).
        private static bool IsTaskStillValid(Shroomp s, BehaviorTask t, LocalMap map)
        {
            switch (t.Type)
            {
                case TaskType.GatherMaterial:   // Excavate-driven (boulders/dead logs/living wood)
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    return map.HasExcavateDesignation(t.TargetTileX, t.TargetTileY);

                case TaskType.GatherFood:
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    if (!map.HasGatherDesignation(t.TargetTileX, t.TargetTileY)) return false;
                    {
                        var veg = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (!veg.IsPresent || veg.IsDepleted) return false;
                    }
                    return true;

                case TaskType.ChopWood:
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    if (!map.HasChopWoodDesignation(t.TargetTileX, t.TargetTileY)) return false;
                    {
                        var veg = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (!veg.IsPresent || veg.IsDepleted) return false;
                    }
                    return true;

                case TaskType.CutVegetation:
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    if (!map.HasCutDesignation(t.TargetTileX, t.TargetTileY)) return false;
                    {
                        var veg = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (!veg.IsPresent || veg.IsDepleted) return false;
                    }
                    return true;

                case TaskType.Haul:
                {
                    // Phase 1 (pickup) — TargetId != null. Item must still
                    // be on the source tile and not forbidden.
                    if (t.TargetId != null)
                    {
                        if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                        var items = map.GetItemsOnTile(t.TargetTileX, t.TargetTileY);
                        for (int i = 0; i < items.Count; i++)
                        {
                            var it = items[i];
                            if (it.Id.ToString() == t.TargetId)
                                return !it.IsForbidden;
                        }
                        return false;   // item gone (consumed / hauled / despawned)
                    }
                    // Phase 2 (deliver) — TargetId == null, shroomp is
                    // carrying items toward the delivery tile. The
                    // delivery tile is just a destination; HaulSystem.Apply
                    // handles drop-on-arrival even if the target isn't a
                    // stockpile anymore (player un-painted). Only abort
                    // if the destination became physically unreachable;
                    // defer that check when SimPos is in a wall (passability
                    // flip race) so IsWorkReachable's region query stays
                    // meaningful.
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    if (!IsPixelPassable(map, s.SimPos)) return true;
                    int hTx = (int)(s.SimPos.X / LocalMap.TileSize);
                    int hTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                    return map.IsWorkReachable(hTx, hTy, t.TargetTileX, t.TargetTileY);
                }

                case TaskType.PlayerOrder:
                    // The move destination must still be a passable tile
                    // the shroomp can reach. Player-issued orders are
                    // important — only abort on hard impossibility (tile
                    // became impassable, region cut off).
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    if (!map.IsPassable(t.TargetTileX, t.TargetTileY)) return false;
                    if (!IsPixelPassable(map, s.SimPos)) return true;   // defer
                    {
                        int pTx = (int)(s.SimPos.X / LocalMap.TileSize);
                        int pTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                        return map.IsWorkReachable(pTx, pTy, t.TargetTileX, t.TargetTileY);
                    }

                case TaskType.Build:
                case TaskType.BuildHaul:
                    // v0.5.19 — blueprint must still be present (not demolished,
                    // not already built by another shroomp). Reachability deferred
                    // when SimPos is in a wall.
                    // v0.5.57 / v0.5.60 — when a Build/BuildHaul task is
                    // routing through a source tile, s.BuildSiteTileX/Y holds
                    // the blueprint coords and t.TargetTileX/Y is the SOURCE.
                    // Validate the blueprint at its real coordinates, not the
                    // source. Mid-haul if the blueprint is canceled the task
                    // drops cleanly and the shroomp goes back to SelectTask.
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    int valBpTx = s.BuildSiteTileX >= 0 ? s.BuildSiteTileX : t.TargetTileX;
                    int valBpTy = s.BuildSiteTileY >= 0 ? s.BuildSiteTileY : t.TargetTileY;
                    var bpSlot = map.GetStructure(valBpTx, valBpTy);
                    if (!bpSlot.IsBlueprint) return false;
                    return true;

                default:
                    return true;
            }
        }

        // v0.5.8 — climb-over usefulness check. The shroomp's local steering
        // may fall back to stepping onto an occupied tile (a "climb over"
        // a blocker) when every uncrowded side-step is terrain-blocked.
        // That step is *useful* only in one of two cases:
        //
        //   1. The candidate tile is Chebyshev-≤-1 from the current task's
        //      target tile — stepping onto it puts the shroomp at touch-
        //      arrival distance to the work tile, so the next tick fires
        //      IsAtTouchArrival → ApplyTaskEffect. This is the common case
        //      for Excavate (target impassable, shroomp must stand adjacent)
        //      and the boundary case for Gather/Chop/Cut where the
        //      candidate IS the target.
        //
        //   2. The tile *beyond* the candidate, in the direction of motion,
        //      is terrain-passable — the shroomp has somewhere to continue
        //      after the climb, so the climb is a useful step on a path.
        //
        // Neither case → the climb is a dead-end: the shroomp steps onto a
        // blocker, can't continue forward (impassable beyond), and the
        // next tick's primary direction pulls back into the same blocker
        // → oscillation. Returning false here makes the steering leave
        // the shroomp in place, letting the YieldTrigger (12 ticks of
        // StuckTicks) ask the blocker to lie down. Once the blocker
        // yields, its tile drops out of the occupancy grid and the
        // shroomp's primary step becomes uncrowded.
        //
        // `motion` is the candidate's direction vector (baseStep for the
        // primary, rotated for side-steps). Math.Sign gives one of -1/0/+1
        // per axis so the tile beyond is the next tile in the 8-direction
        // sense. Step magnitude doesn't matter — only direction does.
        private static bool IsClimbOverUseful(LocalMap map, Shroomp s, Vector2 candidate, Vector2 motion)
        {
            int candTx = (int)(candidate.X / LocalMap.TileSize);
            int candTy = (int)(candidate.Y / LocalMap.TileSize);

            // v0.5.84t — hard passability guard. The candidate tile itself must
            // be passable; otherwise "useful" is meaningless (we'd be marking
            // a wall step as worth taking). Pre-v0.5.84t this returned true
            // when the candidate was Chebyshev-1 of the task target — including
            // the wall the task target sat behind — so the crowdedFallback
            // path could nudge the shroomp 1-2 px into the wall every tick
            // (micro-jitter against the wall, never tripping the >0.5 px
            // stuck detector). Sam playtest: pawns bunched on a 2-tile-thick
            // wall despite a 3-tile-wide doorway behind them.
            if (!map.IsPassable(candTx, candTy)) return false;

            // Case 1 — candidate at touch-arrival to task target.
            if (s.CurrentTask is { } t && t.TargetTileX >= 0 && t.TargetTileY >= 0)
            {
                int dx = candTx - t.TargetTileX;
                int dy = candTy - t.TargetTileY;
                if (dx < 0) dx = -dx;
                if (dy < 0) dy = -dy;
                if (dx <= 1 && dy <= 1) return true;
            }

            // Case 2 — tile beyond candidate is terrain-passable.
            int signX = motion.X > 0 ? 1 : motion.X < 0 ? -1 : 0;
            int signY = motion.Y > 0 ? 1 : motion.Y < 0 ? -1 : 0;
            // No motion (signX=signY=0) means we aren't crossing tiles at
            // all; treat as "no climb required" = allow. Defensive — local
            // steering shouldn't call this with a zero-vector motion.
            if (signX == 0 && signY == 0) return true;
            int beyondTx = candTx + signX;
            int beyondTy = candTy + signY;
            return map.IsPassable(beyondTx, beyondTy);
        }

        // v0.5.84t — tool bonus multiplier. When the shroomp's EquippedTool's
        // PreferredForTasks list contains the current task type, apply
        // ToolBaseBonus (1.30×) scaled by the tool's Quality
        // (SkillCurve.ToolQualityFactor). Otherwise return 1.0 — bare-handed.
        // Sam: "pickaxes make mining faster" — this is the lever. Wired into
        // mining per-tick, construction speed, cut/chop yields, cook/craft
        // speed (Phase 5.5 bills) so the right tool for the job actually
        // matters.
        private const float ToolBaseBonus = 1.30f;
        private static float GetToolBonusFor(Shroomp s, TaskType taskType)
        {
            var tool = s.EquippedTool;
            if (tool == null) return 1.0f;
            var def = Items.ItemRegistry.Get(Items.ItemKind.Tool, tool.SubType);
            if (def == null || def.PreferredForTasks == null) return 1.0f;
            bool preferred = false;
            for (int i = 0; i < def.PreferredForTasks.Length; i++)
            {
                if (def.PreferredForTasks[i] == taskType) { preferred = true; break; }
            }
            if (!preferred) return 1.0f;
            return ToolBaseBonus * SkillCurve.ToolQualityFactor(tool.Quality);
        }

        // v0.5.84t — true iff the step from `from` to `to` crosses a tile
        // boundary (the two pixels live in different tiles). Used by the
        // climb-over-primary gate so partial-pixel nudges within the same
        // tile don't bypass the stuck detector. RimWorld parity: cell-based
        // movement either commits a full cell step or waits.
        private static bool CrossesTileBoundary(Vector2 from, Vector2 to)
        {
            int fx = (int)(from.X / LocalMap.TileSize);
            int fy = (int)(from.Y / LocalMap.TileSize);
            int tx = (int)(to.X / LocalMap.TileSize);
            int ty = (int)(to.Y / LocalMap.TileSize);
            return fx != tx || fy != ty;
        }

        // ── Main tick ───────────────────────────────────────────────────────
        // Called from SimulationCore.Tick once per sim-system interval (1× = 1 s).
        // v0.5.79 — current-hour cache so ApplyTaskEffect can consult the
        // night-sleep window without changing the ApplyTaskEffect signature.
        // Set at the top of Tick from the hourOfDay arg the sim core
        // passes in (SimulationDate.Hour).
        private static int _currentHourOfDay = 12;

        // v0.5.82 — current-tick cache + pawn-blocked-path cooldown gate.
        // Mirrors RimWorld's Pawn_PathFollower.BestPathHadPawnsInTheWayRecently:
        // when a fresh A* path includes pawn-occupied tiles we mark the
        // shroomp's LastPawnBlockedPathTick. Subsequent stuck-detection
        // re-path attempts within PawnBlockedRepathCooldown ticks are
        // suppressed — the shroomp sits and waits for the cluster to
        // disperse instead of looping a new (still-blocked) plan every
        // StuckRePathTicks. 240 ticks @ 60 Hz = 4 in-game seconds at 1×.
        private static long _currentTick = 0;
        private const  long PawnBlockedRepathCooldown = 240;

        // Scan the freshly-computed path waypoints for any tile that's
        // currently occupied by another shroomp. If found, stamp the
        // shroomp's LastPawnBlockedPathTick so the cooldown gate below
        // suppresses re-pathing for the next PawnBlockedRepathCooldown
        // ticks. Cheap O(N) walk over the path — typically < 30 waypoints.
        private static void RecordPathPawnBlockage(Shroomp s)
        {
            if (_occGridWidth <= 0) return;
            int W = _occGridWidth;
            for (int i = 0; i < s.PathWaypoints.Count; i++)
            {
                Vector2 wp = s.PathWaypoints[i];
                int wx = (int)(wp.X / LocalMap.TileSize);
                int wy = (int)(wp.Y / LocalMap.TileSize);
                int idx = wy * W + wx;
                if ((uint)idx >= (uint)_shroompPerTile.Length) continue;
                if (_shroompPerTile[idx] > 0)
                {
                    s.LastPawnBlockedPathTick = _currentTick;
                    return;
                }
            }
        }

        // v0.5.79 — RimWorld-parity Downed-state config.
        // v0.5.80 — flipped from a mutable dev-panel slider to a code-side
        // constant + trait modifier per Sam: "The thresholds should only
        // be affected by traits and code changes." Base threshold = downed
        // when weighted health drops below (100 − BaseDamageToDown) = 30 %.
        // Per-shroomp trait modifiers shift the threshold up (tougher) or
        // down (more fragile). See DownThresholdFor.
        private const int   BaseDamageToDown      = 70;
        private const float StandBackUpHysteresis = 10f;

        // v0.5.80 — per-shroomp down threshold. Returns the health % below
        // which the shroomp collapses. Higher threshold = collapses
        // sooner (more fragile); lower = stays upright longer (tougher).
        // Brawny: +5 damage tolerance (down at 25 % vs default 30 %).
        // Accident-Prone: -5 damage tolerance (down at 35 %).
        // Stoic: +3 damage tolerance (pain resistance shaves the gap).
        public static float DownThresholdFor(Shroomp s)
        {
            int dmg = BaseDamageToDown;
            if (s.Personality != null)
            {
                if (s.Personality.Contains("Brawny"))         dmg += 5;
                if (s.Personality.Contains("Stoic"))          dmg += 3;
                if (s.Personality.Contains("Accident-Prone")) dmg -= 5;
            }
            return 100f - dmg;
        }

        private static void UpdateDownedState(Shroomp s)
        {
            float h = s.ComputeHealthPercent();
            float downAt   = DownThresholdFor(s);
            float standAt  = downAt + StandBackUpHysteresis;
            if (!s.IsDowned && h < downAt)
            {
                s.IsDowned = true;
                ThoughtRegistry.Add(s, "Downed");
            }
            else if (s.IsDowned && h >= standAt)
            {
                s.IsDowned = false;
                ThoughtRegistry.Add(s, "StoodBackUp");
            }
        }

        public static void Tick(IReadOnlyList<Shroomp> shroomps, LocalMap? map,
            ColonyResources resources, Queue<PlayerOrder>? pendingOrders,
            Random rng, float dtSeconds, long currentTick = 0, int hourOfDay = 12)
        {
            _currentHourOfDay = hourOfDay;
            _currentTick      = currentTick;   // v0.5.82 — pawn-blocked repath cooldown
            // v0.4.14 — batch the region-graph rebuild to once per sim
            // tick. Without this gate every excavation's `MutateTerrain`
            // flipped `_regionsDirty`, and the next shroomp's SelectTask
            // re-ran the full W×H BFS. At 240×150 with 17 active diggers
            // that was ~50 ms / tick of pure rebuild work — the cause of
            // the sim-thread stall reported as "shroomps stuck + visual
            // warping at the edges". Inside the tick, the data may go
            // stale by one tick (a tile that just became passable still
            // reads region 0); excavation only ADDS connectivity so
            // shroomps still pick valid targets, and the worst case is one
            // extra tick of latency before a newly-opened pocket is
            // assigned work.
            map?.BeginTick();
            try
            {

            // v0.4.19 — populate the per-tick shroomp-occupancy grid. Local
            // steering + the adjacent-tile picker read this to avoid
            // routing shroomps through each other's current tile (soft
            // RimWorld-style collision; not a hard block, just a
            // tie-breaker so the colony spreads out at work faces
            // instead of stacking). Rebuilt once per tick from the
            // shroomp list — cost is O(N) once, vs O(N²) per-shroomp
            // scans.
            PopulateOccupancyGrid(map, shroomps);

            // 1. Drain any pending player orders and stage them on their target shroomp.
            if (pendingOrders != null)
            {
                while (pendingOrders.Count > 0)
                {
                    var order = pendingOrders.Dequeue();
                    foreach (var s in shroomps)
                    {
                        if (s.Name != order.ShroompName) continue;

                        // v0.4.51b — release the OLD task's reservations /
                        // designation claims before clobbering CurrentTask.
                        // Without this, right-clicking a shroomp mid-Haul or
                        // mid-Gather left the haul reservation / designation
                        // claim dangling so other shroomps couldn't pick up
                        // the dropped work. v0.4.7 already had the right
                        // helper (`ReleaseTaskClaim`) — we just weren't
                        // calling it on the player-order path. Also clears
                        // PathWaypoints and resets stuck/repath state so
                        // the new order paths fresh from current position
                        // rather than re-using the prior task's stale
                        // route — the explicit "break idle freezing"
                        // gesture Sam asked for.
                        ReleaseTaskClaim(s, map);
                        s.PathWaypoints.Clear();
                        s.StuckTicks    = 0;
                        s.RePathTried   = false;
                        s.IdleLingerTicks = 0;

                        // v0.4.3 — if the order target is a tile that
                        // currently holds a dropped item, convert this
                        // into a Haul pick-up cycle: walk to the tile,
                        // pick the first item up, then deliver to the
                        // colony pool. Other player orders (empty tile,
                        // workbench, …) still flow through the generic
                        // PlayerOrder move path.
                        //
                        // v0.5.3 — both branches now assign tileX/tileY
                        // and immediately invoke Pathfinder.FindPath. Pre-
                        // v0.5.3 the PlayerOrder branch (no tile coords)
                        // relied on greedy steering — shroomps would walk
                        // straight at the destination and dead-end against
                        // walls / concave terrain since needNewTask was
                        // false for the freshly-assigned task and the
                        // section-2a pathfinding block at line 543 never
                        // fired in the same tick. Sam: "Currently, shroomps
                        // will path in a straight line towards their
                        // destination when using right-click orders
                        // especially." Mirrors RimWorld: every player
                        // Goto issues a full pathfind at command time.
                        if (map != null)
                        {
                            int tx = (int)(order.Target.X / LocalMap.TileSize);
                            int ty = (int)(order.Target.Y / LocalMap.TileSize);
                            var items = map.GetItemsOnTile(tx, ty);
                            if (items.Count > 0)
                            {
                                var pick = items[0];
                                HaulSystem.Reserve(pick, s.Id);
                                s.CurrentTask = new BehaviorTask(
                                    TaskType.Haul, order.Target, 100f,
                                    isPlayerOrder: true, interruptible: false,
                                    tileX: tx, tileY: ty,
                                    targetId: pick.Id.ToString());
                                s.SimTarget = order.Target;
                                Pathfinder.FindPath(map, s.SimPos, (tx, ty),
                                    s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
                                break;
                            }

                            s.CurrentTask = new BehaviorTask(
                                TaskType.PlayerOrder, order.Target, 100f,
                                isPlayerOrder: true, interruptible: false,
                                tileX: tx, tileY: ty);
                            s.SimTarget = order.Target;
                            Pathfinder.FindPath(map, s.SimPos, (tx, ty),
                                s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
                            break;
                        }

                        // map == null fallback (shouldn't reach during normal play
                        // — kept for safety so the task still gets assigned).
                        s.CurrentTask = new BehaviorTask(
                            TaskType.PlayerOrder, order.Target, 100f,
                            isPlayerOrder: true, interruptible: false);
                        s.SimTarget = order.Target;
                        break;
                    }
                }
            }

            // 2. Per-shroomp evaluation + movement + effects.
            // v0.4.18 — indexed loop. `foreach` on `IReadOnlyList<Shroomp>`
            // boxes to a heap-allocated enumerator on every Tick; at 60 Hz
            // that was 60 enumerator allocations per second of pure GC
            // pressure. Indexed access takes the same `IList<T>.this[int]`
            // path the JIT already devirtualises for `List<T>`.
            int shroompCount = shroomps.Count;
            for (int si = 0; si < shroompCount; si++)
            {
                var s = shroomps[si];
                if (!s.IsAlive) continue;

                // v0.3.39 (O-H.2) — LOD skip. Off-screen shroomps tick less
                // often. When they DO tick, MoveOneTick scales the per-step
                // distance up by the interval so total motion-per-real-time
                // is preserved.
                if (!ShouldTick(s, currentTick)) continue;
                int tickInterval = s.TickPhase switch
                {
                    0 => 1,
                    1 => WarmInterval,
                    _ => ColdInterval,
                };
                float effectiveDt = dtSeconds * tickInterval;

                // v0.5.79 — RimWorld-parity "Downed" state. When weighted
                // health drops below (100 - DamageToDown) the shroomp is
                // incapacitated: drops the current task, doesn't pick a
                // new one, and doesn't move. Stands back up once health
                // recovers above (100 - DamageToDown) + 10 (hysteresis
                // prevents flicker at the threshold). Renderer lays the
                // sprite horizontal (similar to sleep) with a darker
                // tint. Sam: "We should also implement a 'down before
                // dead' state like rimworld or dwarf fortress."
                UpdateDownedState(s);
                if (s.IsDowned)
                {
                    if (s.CurrentTask != null) ReleaseTaskClaim(s, map);
                    s.CurrentTask = null;
                    s.PathWaypoints.Clear();
                    s.PrevSimPos = s.SimPos;
                    continue;   // skip the rest of the per-shroomp tick
                }

                // v0.3.35 / v0.3.40 — tick down each per-shroomp "recently
                // gave up on this tile" blacklist slot. Entries are
                // implicitly empty when TicksLeft == 0; the FindNearest*
                // path uses that to skip. No allocation per tick; the FIFO
                // is a fixed-size struct array.
                for (int i = 0; i < s.AvoidTiles.Length; i++)
                {
                    if (s.AvoidTiles[i].TicksLeft > 0)
                        s.AvoidTiles[i].TicksLeft--;
                }
                // v0.4.57 — post-abandon designation-task cooldown.
                if (s.DesignationCooldownTicks > 0)
                    s.DesignationCooldownTicks -= tickInterval;

                // v0.5.60 — RimWorld-parity per-tick interaction roll.
                // Fires Chitchat / KindWords / Slight / DeepTalk as ONE-TICK
                // EVENTS independent of TaskType.Converse. Pawns interact
                // while eating, walking, working — not just during dedicated
                // chat tasks. ~1 % per-tick probability gated by proximity
                // and pair cooldown means actual interaction rate is much
                // lower (only fires when partner is nearby AND off-cooldown).
                InteractionTracker.Tick(s, shroomps, rng, currentTick);

                // v0.5.60 — JoyTolerance decay (RimWorld parity). Shroomp's
                // tolerance for each idle activity slowly drops while doing
                // OTHER activities, so a shroomp that just did Meditate gets
                // fresh again after a few minutes of other work. Tolerance
                // scales joy gain (in ApplyTaskEffect) and idle-activity
                // weight (in SelectIdleActivity). Decay tied to tickInterval
                // so cold-LOD shroomps decay in real time at the same rate.
                if (s.JoyTolerance.Count > 0)
                {
                    DecayJoyTolerance(s, tickInterval);
                }
                // v0.5.23 (Phase 5F G5) — periodic Beauty check. Every
                // ~300 ticks (~5 sec at hot LOD) the shroomp samples the
                // room they're standing in and fires BeautyPretty /
                // BeautyUgly thoughts based on the room's BeautyScore.
                // Hash-spread by shroomp id so all shroomps don't sample on
                // the same tick. Outdoor room intentionally has
                // BeautyScore=0 and emits no thought (the wilderness is
                // baseline; only built rooms move the needle).
                if (map != null && (currentTick + (s.Id.GetHashCode() & 0xFF)) % 300 == 0)
                {
                    map.EnsureRooms();
                    int sx = (int)(s.SimPos.X / LocalMap.TileSize);
                    int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
                    if (map.InBounds(sx, sy))
                    {
                        var slot = map.GetStructure(sx, sy);
                        if (slot.RoomId != 0 && slot.RoomId != RoomDetector.OutdoorRoomId)
                        {
                            var room = map.GetRoom(slot.RoomId);
                            if (room != null)
                            {
                                if (room.BeautyScore >= 10f) ThoughtRegistry.Add(s, "BeautyPretty");
                                else if (room.BeautyScore < -3f) ThoughtRegistry.Add(s, "BeautyUgly");
                            }
                        }
                    }
                }
                // v0.5.4 — work-search debounce decrement. See Shroomp.cs
                // comment + the workAvailable gate below.
                if (s.WorkSearchCooldownTicks > 0)
                    s.WorkSearchCooldownTicks -= tickInterval;
                // v0.5.84g — path-fail debounce decrement.
                if (s.PathFailCooldownTicks > 0)
                    s.PathFailCooldownTicks -= tickInterval;

                // v0.5.9 — task viability gate (RimWorld JobDriver FailOn
                // pattern). If the current task can no longer be completed
                // — designation cleared by another shroomp or the player,
                // haul item missing/forbidden, player-order destination
                // walled off, etc. — release the claim + clear the task
                // here so the section-2a needNewTask block immediately
                // routes the shroomp to SelectTask. Without this gate the
                // shroomp walks the full path to a defunct target, jitters
                // on arrival when nothing happens, eventually times out
                // via StuckThreshold (~90 ticks ~ 1.5 s of visible jitter).
                // The check itself is O(1) per task type (HashSet.Contains
                // or single tile lookup) so the per-tick cost is trivial.
                if (map != null && s.CurrentTask is { } valTask
                    && !IsTaskStillValid(s, valTask, map))
                {
                    ReleaseTaskClaim(s, map);
                    s.PathWaypoints.Clear();
                    s.StuckTicks = 0;
                    s.RePathTried = false;
                    s.CurrentTask = null;
                    // Don't blacklist the tile — the task became invalid
                    // through external state change (other shroomp finished
                    // it, player removed designation), not through this
                    // shroomp's path choices. A future re-evaluation should
                    // be free to pick a fresh target nearby.
                }

                // 2a. Re-evaluate task selection unless a non-interruptible player order
                //     is in progress or the current task is still valid and current.
                //
                // v0.3.43 — idle tasks (Wander / Loiter / Observe / Converse /
                // Meditate / VisitFavorite) now respect IdleLingerTicks. After
                // arriving at an idle target the shroomp "stays" for the
                // task's ArrivalLinger duration — that's what makes the
                // colony feel alive instead of jittering. During linger we
                // skip re-evaluation EXCEPT when a critical need fires OR
                // when a new designation has appeared anywhere on the map
                // (cheap check via LocalMap.HasAnyDesignation). The latter
                // preserves the v0.3.23 "wanderers pick up new designations"
                // behaviour without forcing the entire idle pool to re-
                // evaluate every tick.
                bool needNewTask;
                if (s.CurrentTask is { } ct)
                {
                    bool idle = IsIdleType(ct.Type);
                    // v0.4.61 (E6) — life-threatening needs override the
                    // Interruptible gate. Without this, a shroomp carrying
                    // out a non-interruptible PlayerOrder could starve to
                    // death walking to the order target.
                    bool lifeThreat = IsLifeThreatening(s);
                    bool critical = lifeThreat
                        || (ct.Interruptible && CriticalNeedsOverride(s, ct.Priority));
                    // v0.5.1 — lingerExpired requires arrival AND tick-down.
                    // Pre-v0.5.1 the check fired during walks for
                    // long-distance idle tasks (Wander 8-28 tiles, etc.)
                    // because IdleLingerTicks counted from task creation,
                    // not arrival. Now MoveOneTick sets IdleArrived=true
                    // and resets the linger only when the shroomp actually
                    // reaches the destination, so this check fires once
                    // the post-arrival dwell is over.
                    bool lingerExpired = idle && s.IdleArrived && s.IdleLingerTicks <= 0;
                    // v0.4.65 — gate `workAvailable` on the post-abandon
                    // cooldown. Without this, a shroomp in cooldown that's
                    // doing an idle task triggers a re-eval EVERY TICK
                    // because designations exist on the map; SelectTask
                    // then blocks every designation branch (per v0.4.57's
                    // DesignationCooldownTicks gate) and falls through to
                    // the idle tier, where it picks a NEW personality-
                    // weighted random idle task. Net effect: visible
                    // cycling between Wander → Loiter → Observe → ... for
                    // the full ~1s cooldown duration. Sam's report of
                    // "stuck cycling between idle behaviors." With the
                    // gate: a cooldown shroomp finishes their current idle
                    // task's linger naturally and only re-evaluates when
                    // either the cooldown expires (designations become
                    // available again) or the linger does (normal idle
                    // rotation).
                    // v0.5.4 — also gate on WorkSearchCooldownTicks. The
                    // v0.4.65 DesignationCooldownTicks gate stops cycling
                    // for shroomps who *just abandoned* a task (~1s cooldown).
                    // But Sam's persistent idle-cycling report: a shroomp
                    // who *successfully completes* one task and then can't
                    // find new reachable work (all designations claimed by
                    // others / blacklisted / unreachable) hits
                    // workAvailable=true every tick because designations
                    // exist somewhere globally. SelectTask falls through to
                    // SelectIdleActivity, which RNG-rolls a NEW idle each
                    // call — visible cycling Wander → Loiter → Observe →
                    // Meditate → Converse, ad infinitum. The new
                    // WorkSearchCooldownTicks (set after every idle-only
                    // SelectTask return, lines ~545) debounces re-eval to
                    // ~1s, matching RimWorld's JobSearchSuppressUntilTick.
                    // Critical needs + chained player orders bypass via
                    // their own clauses, so urgent overrides still work.
                    bool workAvailable = idle && map != null && map.HasAnyDesignation()
                        && s.DesignationCooldownTicks <= 0
                        && s.WorkSearchCooldownTicks <= 0;
                    // v0.5.2 — chain orders interrupt idle activity. RTS
                    // standard: shift-click on an idle unit starts the
                    // first chained order immediately (vs. waiting for
                    // some current job to finish — which they don't have).
                    // Working shroomps (Excavating / Hauling / Eating /
                    // PlayerOrder etc.) let the current task finish first;
                    // the queue head pops on the natural CurrentTask=null
                    // transition.
                    bool chainPending = idle && s.MoveOrderQueue.Count > 0;
                    needNewTask = ct.Type == TaskType.None
                        || critical
                        || lingerExpired
                        || workAvailable
                        || chainPending;
                }
                else
                {
                    // v0.5.84g — path-fail cooldown gate. After A* fails for
                    // a task, BehaviorSystem releases the task and sets
                    // PathFailCooldownTicks. If the next tick's needNewTask
                    // fires unconditionally (which it would here without
                    // this gate), SelectTask would re-roll, hit the same
                    // chokepoint A* failure, and grind. The cooldown caps
                    // re-pick rate to ~2/sec under failure conditions.
                    // Life-threatening needs always override the gate so
                    // a starving shroomp still re-evaluates immediately.
                    needNewTask = s.PathFailCooldownTicks <= 0 || IsLifeThreatening(s);
                }

                if (needNewTask)
                {
                    // v0.3.33 (B.7) — release any prior designation claim
                    // before re-selecting so the tile becomes available to
                    // other shroomps. Stale claims would block work assignment
                    // until SetXDesignation re-set the tile.
                    ReleaseTaskClaim(s, map);

                    // v0.5.2 — chain order queue. If the shroomp has any
                    // shift+right-click queued Move orders pending, pop
                    // the head and create a fresh PlayerOrder for it.
                    // Bypasses the v0.4.19 failure-recovery short-circuit
                    // and the regular SelectTask roll because the player
                    // explicitly queued these. Life-threat critical needs
                    // (`IsLifeThreatening` above) still override via the
                    // `critical` branch — a starving shroomp interrupts a
                    // chained order to eat, then the queue resumes once
                    // the eat task completes. Standard RTS semantics:
                    // shift-click queues, the queue plays out as each
                    // task completes.
                    if (s.MoveOrderQueue.Count > 0 && !IsLifeThreatening(s))
                    {
                        var queuedTarget = s.MoveOrderQueue[0];
                        s.MoveOrderQueue.RemoveAt(0);
                        // v0.5.3 — pass tile coords so the section-2a A*
                        // pathfinding block (lines ~543) computes a real
                        // route instead of leaving PathWaypoints empty
                        // (greedy-steering fallback that dead-ends on walls).
                        int qtx = (int)(queuedTarget.X / LocalMap.TileSize);
                        int qty = (int)(queuedTarget.Y / LocalMap.TileSize);
                        s.CurrentTask = new BehaviorTask(
                            TaskType.PlayerOrder, queuedTarget, 100f,
                            isPlayerOrder: true, interruptible: false,
                            tileX: qtx, tileY: qty);
                        s.SimTarget = queuedTarget;
                        s.PathWaypoints.Clear();
                        s.StuckTicks = 0;
                        s.RePathTried = false;
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                    }
                    // v0.4.19 — failure-recovery short-circuit. When a
                    // shroomp has just completed three work tasks in a row
                    // without producing any output (haul item missing on
                    // arrival, designation cleared by another shroomp, slot
                    // depleted upstream) we force a Wander to break the
                    // cycle. Without this, shroomps at the delivery point
                    // would keep being handed nearby Haul tasks that
                    // already-finished by the time they reached the
                    // pickup tile, visibly bunching around the spawn
                    // cluster making no progress. The double-linger
                    // gives the colony state time to settle before the
                    // shroomp re-engages with the priority queue.
                    else if (s.ConsecutiveTaskFailures >= TaskFailureForceWander)
                    {
                        s.CurrentTask = NewWanderTask(s.SimPos, map, rng);
                        s.ConsecutiveTaskFailures = 0;
                    }
                    else
                    {
                        s.CurrentTask = SelectTask(s, map, resources, rng, shroomps, hourOfDay);
                    }

                    // v0.4.4 — auto-equip the dominant-hand tool that
                    // matches this task's preferred-tools list. Magic
                    // grab from the colony pool until Phase 5 stockpile
                    // zones land; off-hand stays free for shields +
                    // dual-wield.
                    if (s.CurrentTask is { } autoEquipTask)
                        EquipmentSystem.AutoEquipForTask(s, autoEquipTask, resources);
                    // v0.5.84t — opportunistic weapon upgrade. Pacifists
                    // skipped inside. Fires on every task transition so a
                    // shroomp who just finished a job sees the latest
                    // weapon catalogue + swaps in if it's a clear upgrade.
                    EquipmentSystem.AutoEquipBetterWeapon(s, resources);
                    // v0.5.84t — drop tools that aren't needed by the new
                    // task or the shroomp's role. Sam: "they should drop
                    // them unless they're forced." Drops to current tile
                    // unforbidden so HaulSystem moves the tool to a
                    // stockpile. Role-canonical exceptions (Sage's Sage
                    // Staff, Crafter's Hammer, Forager's Basket) are
                    // skipped inside. Weapons are never dropped here.
                    EquipmentSystem.DropUnsuitableTool(s, map);
                    s.PathWaypoints.Clear();   // invalidate any stale Phase-4 path
                    s.StuckTicks = 0;
                    s.RePathTried = false;     // v0.4.17 — new task gets a fresh re-path budget
                    // v0.5.11 — fresh no-progress window for the new task.
                    s.MinSqrDistanceToWalkTarget = float.MaxValue;
                    s.NoProgressTicks = 0;
                    s.LastWalkTargetTileX = -1;
                    s.LastWalkTargetTileY = -1;
                    s.ProgressRePathTried = false;
                    // v0.3.45 — initialise the idle linger to the task's
                    // ArrivalLinger at *creation*, not at arrival.
                    // v0.5.1 — the v0.3.45 "total time-budget" model is
                    // wrong for tasks whose walk takes longer than the
                    // linger value. Wander walks 8-28 tiles (~4-14 sec at
                    // base speed) but LingerWander = 120 ticks (~2 sec),
                    // so lingerExpired triggered DURING the walk and
                    // re-rolled the idle task — visible cycling that
                    // pegged FPS via per-tick SelectTask. Fix: linger now
                    // starts at ARRIVAL (`IdleArrived` flag set in
                    // MoveOneTick); the value here is just the initial
                    // "still walking" sentinel. The lingerExpired check
                    // requires both arrival AND tick-down to 0.
                    s.IdleArrived     = false;
                    if (s.CurrentTask is { } newTask && IsIdleType(newTask.Type))
                    {
                        s.IdleLingerTicks = newTask.ArrivalLinger;
                        // v0.5.4 — RimWorld JobSearchSuppressUntilTick.
                        // SelectTask returned an idle task, which means
                        // no reachable / claimable work exists for this
                        // shroomp right now. Suppress the workAvailable
                        // re-eval clause for ~1 second so the shroomp
                        // commits to their chosen leisure activity
                        // instead of re-rolling on every tick because
                        // designations exist somewhere globally. Re-
                        // checked when the cooldown expires; the shroomp
                        // notices new player designations within ~1s.
                        s.WorkSearchCooldownTicks = 60;
                    }
                    else
                    {
                        s.IdleLingerTicks = 0;
                        // Got actual work — clear any leftover suppression
                        // so the next idle pick (after this work ends)
                        // gets a fresh window.
                        s.WorkSearchCooldownTicks = 0;
                    }

                    // v0.3.47 (Phase 4 sub-B) — for non-trivial routes,
                    // request a full A* path now. The path lands in
                    // PathWaypoints; ResolveWalkTarget consumes the head
                    // each tick, falling through to greedy steering only
                    // for adjacent destinations. This dramatically
                    // improves long-route reliability — shroomps no longer
                    // dead-end against concave wall pockets.
                    if (s.CurrentTask is { } pt && map != null
                        && pt.TargetTileX >= 0 && pt.TargetTileY >= 0)
                    {
                        // v0.5.84f — A* every target-tile task, regardless
                        // of distance. Pre-fix `distSq > PreferAStarDistSqPx`
                        // (8 tiles) gated A* for non-designation, non-player-
                        // order tasks; Loiter (2-5 tiles), Observe (3-7),
                        // close Wander hops, and Converse partners all
                        // fell through to greedy local steering with NO
                        // waypoint path. Local steering's fan-out can
                        // sometimes route around small obstacles but
                        // dead-ends against walls (the visible "stuck on
                        // walls when attempting to wander" Sam reported).
                        // RimWorld pathfinds every move regardless of
                        // distance — no local-steering shortcut. We adopt
                        // the same: A* fail-fast on the region check (line
                        // 127 in Pathfinder.cs) makes a short reachable
                        // search dirt-cheap (<20 expansions), and the
                        // explicit waypoint list means steering can never
                        // try to walk straight at an impassable tile.
                        // Designation + player-order paths kept on the
                        // existing call site so the historic comment trail
                        // (v0.4.16 / v0.5.3) still applies. The gate is
                        // gone — the body fires unconditionally now.
                        if (true)
                        {
                            // v0.4.18 — fill-into-buffer API. The Pathfinder
                            // clears + populates s.PathWaypoints directly,
                            // skipping the per-call List<Vector2> allocation
                            // that previously fired on every task selection.
                            // v0.4.58 — pass the per-tick occupancy grid +
                            // asker's tile index so A* applies the RimWorld
                            // soft-collision cost (175 per other shroomp on
                            // a candidate tile). Path naturally routes
                            // around clusters at saturated work faces.
                            bool found = Pathfinder.FindPath(map, s.SimPos,
                                (pt.TargetTileX, pt.TargetTileY), s.PathWaypoints,
                                _shroompPerTile, OccTileIdx(s));
                            if (found) RecordPathPawnBlockage(s);   // v0.5.82
                            // v0.4.13 — fail-fast unreachable. The DF-region
                            // check inside FindPath now returns false in
                            // O(1) when start and goal sit in different
                            // regions. Blacklisting the tile here means
                            // the shroomp reprioritises on the very next
                            // tick instead of wasting StuckThreshold (~1.5s)
                            // jittering at the edge of an interior pocket.
                            // v0.5.84a — extended to ALL task types. Pre-fix
                            // only designation tasks dropped CurrentTask on
                            // path-fail; wander/loiter/observe/visit-fav/
                            // converse/meditate/haul/player-orders left the
                            // task alive with empty PathWaypoints, which
                            // caused ResolveWalkTarget to fall through to
                            // the raw `task.Target` pixel and steering to
                            // walk straight at whatever wall was blocking
                            // the path. Sam screenshot: wander-through-walls
                            // on pre-patch save plus 46% A* success rate
                            // under chokepoint crowd cost. The picker-side
                            // region gate (v0.5.83) covered the "destination
                            // is in another DF region" case; this covers the
                            // "destination is technically in-region but A*
                            // exhausted MaxNodes budget (e.g. cluster crowd
                            // cost made every path too expensive)" case.
                            // Blacklisting still designation-only (idle/move
                            // orders can legitimately be retried — only
                            // dropping the current task without poisoning
                            // the tile for future tasks).
                            if (!found)
                            {
                                if (IsDesignationTaskType(pt.Type))
                                {
                                    int oldestIdx = 0;
                                    int oldestTtl = int.MaxValue;
                                    for (int i = 0; i < s.AvoidTiles.Length; i++)
                                        if (s.AvoidTiles[i].TicksLeft < oldestTtl)
                                        { oldestTtl = s.AvoidTiles[i].TicksLeft; oldestIdx = i; }
                                    s.AvoidTiles[oldestIdx] = (pt.TargetTileX, pt.TargetTileY, 360);
                                }
                                ReleaseTaskClaim(s, map);
                                s.CurrentTask = null;
                                s.PathWaypoints.Clear();
                                // v0.5.84g — throttle the next task pick.
                                // Without this the CurrentTask=null branch
                                // of needNewTask fires every tick, calling
                                // SelectTask → A* → fail → drop → repeat at
                                // 60 Hz on the same chokepoint. At 50 pop
                                // with the v0.5.84f MaxNodes=4096 bump this
                                // ground the sim thread. The cooldown caps
                                // re-pick rate to ~2/sec per pawn under
                                // failure conditions.
                                s.PathFailCooldownTicks = 30;
                            }
                        }
                    }
                }

                if (s.CurrentTask == null) continue;

                // v0.4.14 — pre-movement reachability gate for short routes.
                // v0.4.17 — gated on shroomp-pixel passability. If SimPos has
                // briefly drifted into a wall (passability flip mid-tick,
                // save-load race, vegetation regrowth), the check would
                // hit `IsWorkReachable`'s multi-region wall fallback and
                // could blacklist a valid target by picking a neighbour
                // in the wrong region. MoveOneTick's SimPos rescue runs a
                // few lines below; defer the reachability check until the
                // shroomp is back on a passable tile.
                if (map != null && IsPixelPassable(map, s.SimPos)
                    && s.CurrentTask is { } reachCheck
                    && IsDesignationTaskType(reachCheck.Type)
                    && reachCheck.TargetTileX >= 0 && reachCheck.TargetTileY >= 0)
                {
                    int sxTile = (int)(s.SimPos.X / LocalMap.TileSize);
                    int syTile = (int)(s.SimPos.Y / LocalMap.TileSize);
                    if (!map.IsWorkReachable(sxTile, syTile,
                            reachCheck.TargetTileX, reachCheck.TargetTileY))
                    {
                        int oldestIdx = 0, oldestTtl = int.MaxValue;
                        for (int i = 0; i < s.AvoidTiles.Length; i++)
                            if (s.AvoidTiles[i].TicksLeft < oldestTtl)
                            { oldestTtl = s.AvoidTiles[i].TicksLeft; oldestIdx = i; }
                        s.AvoidTiles[oldestIdx] =
                            (reachCheck.TargetTileX, reachCheck.TargetTileY, 360);
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = null;
                        s.PathWaypoints.Clear();
                        continue;
                    }
                }

                // v0.3.43 — tick down idle linger so the shroomp "stays" at
                // their target a moment after arriving. Stops the rapid
                // re-pick cycle that produced jittering. Scaled by the LOD
                // interval so cold shroomps (which only tick every 6 sim
                // ticks) accumulate linger at the same real-time rate as
                // hot shroomps.
                if (s.IdleLingerTicks > 0)
                    s.IdleLingerTicks -= tickInterval;

                // 2b. Movement (v0.3.22 — rewritten, see MoveOneTick).
                //     SimPos validation, target-tile interaction routing,
                //     multi-direction local steering, stuck detection.
                // v0.3.39 — pass the LOD-scaled effective dt so warm/cold
                // shroomps cover the same distance per second as hot ones.
                // Pass tickInterval so MoveOneTick can scale the
                // stuck-detector threshold proportionally.
                bool arrived = MoveOneTick(s, map, effectiveDt, rng, tickInterval);

                if (arrived && s.CurrentTask is { } arrivedTask)
                {
                    // v0.4.19 — observe the task outcome to drive the
                    // failure-recovery loop. We reset `TaskDidWork`
                    // before the effect fires; each `ApplyTaskEffect`
                    // case that produces actual output (item drop,
                    // terrain mutation, inventory deposit, haul
                    // pickup) sets it true. Tasks that finished as a
                    // no-op (haul item missing, designation cleared
                    // before the shroomp arrived, slot depleted
                    // upstream) leave it false.
                    bool wasWorkTask = IsDesignationTaskType(arrivedTask.Type)
                        || arrivedTask.Type == TaskType.Haul;
                    s.TaskDidWork = false;

                    // 2c. On arrival, execute task effect.
                    ApplyTaskEffect(s, arrivedTask, map, resources, dtSeconds, shroomps, rng, currentTick);

                    // v0.4.19 — failure accounting. Only work-typed
                    // tasks (designation work + Haul) count toward the
                    // failure counter; idle / critical-need tasks
                    // (Eat, Sleep, Loiter, …) always reset it since
                    // they're cosmetic to the failure recovery flow.
                    // The completion-without-output case fires when
                    // CurrentTask was cleared during ApplyTaskEffect
                    // — that's the signal the task ran to completion
                    // (instead of mid-haul transitioning to phase 2).
                    if (wasWorkTask && s.CurrentTask == null)
                    {
                        if (s.TaskDidWork) s.ConsecutiveTaskFailures = 0;
                        else               s.ConsecutiveTaskFailures++;
                    }
                    else if (!wasWorkTask)
                    {
                        s.ConsecutiveTaskFailures = 0;
                    }

                    // v0.3.45 — linger countdown already started at task
                    // assignment (see the needNewTask block above), so
                    // there's nothing to do here. The previous v0.3.43
                    // code reset linger to full ArrivalLinger on arrival,
                    // but combined with the bug there it never fired —
                    // and is now redundant anyway because the shroomp has
                    // already been "engaged in the activity" since the
                    // task was assigned.
                }
            }

            }
            finally
            {
                map?.EndTick();   // v0.4.14 — release the region-rebuild freeze
            }
        }

        // ── Movement (v0.3.22 — replaces inline ±45 ° steering) ──────────────
        //
        // Per-tick movement breakdown:
        //   1. Rescue SimPos: if the shroomp is somehow standing inside an
        //      impassable tile (vegetation regrowth, save/load race, etc.),
        //      BFS to the nearest passable tile and snap there immediately —
        //      otherwise the rest of the tick reasons about a position that
        //      shouldn't exist and the shroomp will look like it's inside a wall.
        //   2. Normalise the movement target. Tasks like GatherMaterial target
        //      an impassable Boulder/DeadLog/LivingWood tile — the shroomp needs
        //      to walk to an *adjacent* passable tile to interact, not to the
        //      tile centre (which is inside the rock). The task target itself
        //      stays unchanged so ApplyTaskEffect still mutates the right tile.
        //   3. Try straight, ±45 °, ±90 °, ±135 ° (six fan-out angles) before
        //      giving up — concave geometry (L-walls, inside corners) defeats
        //      the previous two-angle check.
        //   4. If everything is blocked, stay put. Never teleport.
        //   5. Stuck detection: if SimPos barely moved for `StuckThreshold`
        //      ticks, clear the task so the shroomp re-evaluates. Without this
        //      an unreachable designation traps the shroomp forever.
        //
        // Phase-4 hook: when `PathWaypoints` is non-empty, treat the head of
        // the list as the per-step target instead of `CurrentTask.Target`.
        // The A* planner that lands in Phase 4 will populate the list; until
        // then it stays empty and this code falls through to greedy steering.
        private const float ArrivalEpsilon  = 0.5f;    // px progress to count as "moved"
        // v0.4.17 — recovery cadence tightened again from 90 → 30 ticks (~0.5
        // sec at 1×). At the v0.4.16 always-A*-for-designations setting,
        // genuine 1.5-second stalls were almost always either a stale path
        // (other shroomps nearby) or a corner-stuck oscillation — both
        // recoverable by an early re-pathfind + blacklist. The shorter
        // threshold makes wall-corner stuck cases visibly snap out within
        // half a second instead of feeling frozen. We also try one
        // re-pathfind at StuckRePathTicks (8) before the final give-up.
        //
        // v0.4.59 — halved from v0.4.36's 30 / 15. At 60 Hz sim tick rate
        // that's 300 ms / 133 ms (was 500 ms / 250 ms). Faster recovery
        // window pairs with v0.4.58's A* crowd cost so shroomps spend
        // less time dwelling at jammed work-faces.
        // v0.5.82 — StuckThreshold 18 → 36 so it covers the v0.4.29
        // YieldDurationTicks=30 yield window with budget to spare. Pre-
        // v0.5.82 race: a yielded shroomp's asker rode the 30-tick lie-
        // down but its own StuckThreshold fired at 18 → it abandoned the
        // task before the blocker even stood back up. Aligning the two
        // means the asker waits out the full yield + has a 6-tick grace
        // before give-up. 600 ms at 1× — still well inside the player-
        // perceptible "wait, are they stuck?" window.
        private const int   StuckThreshold    = 36;
        private const int   StuckRePathTicks  = 8;

        // v0.5.11 — distance-not-decreasing stuck thresholds. RimWorld
        // pawns re-path when not progressing toward the goal. Our existing
        // StuckTicks/StuckRePathTicks fire only on immobility (progressed
        // < ArrivalEpsilon). The corner-stuck pattern Sam still sees has
        // shroomps sideways-oscillating at concave terrain corners — they
        // ARE moving, so StuckTicks doesn't accumulate, so the immobility
        // re-path never fires. This pair of thresholds catches "moving
        // but not getting closer to the next walk target." Slightly more
        // lenient than the immobility thresholds (legit detours can have
        // brief no-progress windows when local steering navigates around
        // an obstacle).
        private const int   NoProgressRePathTicks = 30;   // ≈ 0.5 s at 60 Hz
        private const int   NoProgressGiveUpTicks = 60;   // ≈ 1.0 s post-re-path

        // v0.4.19 — force-wander trip count. When a shroomp's last N work
        // tasks (Haul + designation types) all completed as no-ops
        // — the haul item was already gone, the designation was
        // cleared by someone else, the vegetation depleted upstream —
        // the next `needNewTask` block hands them a Wander instead of
        // re-rolling the priority queue. 3 is empirically generous:
        // legitimate "two shroomps racing for the same item, lost the
        // race" cases reset the counter on the next successful
        // completion, but a shroomp stuck in a no-op feedback loop
        // breaks out within ~3 ticks instead of indefinitely.
        private const int   TaskFailureForceWander = 3;
        // v0.3.36 (B.17) — precomputed (cos, sin) rotation pairs. Each
        // steering attempt previously called `step.Rotated(angle)` which
        // computes Cos+Sin on every call; with 8 angles × 1000 shroomps × 60 Hz
        // (target colony size) that would be ~480k trig pairs per second.
        // Now the per-tick steering loop multiplies by a precomputed unit
        // vector. Algebraically identical to Rotated; just no trig.
        // Order matches the original SteerAngles order: 0, ±45, ±90, ±135,
        // 180. Last entry (180°) is the v0.3.35 "back out of dead end"
        // fallback that only fires when every forward direction is blocked.
        private static readonly (float Cos, float Sin)[] SteerVectors =
        {
            ( 1.000000f,  0.000000f),  //   0°
            ( 0.707107f,  0.707107f),  //  +45°
            ( 0.707107f, -0.707107f),  //  -45°
            ( 0.000000f,  1.000000f),  //  +90°
            ( 0.000000f, -1.000000f),  //  -90°
            (-0.707107f,  0.707107f),  // +135°
            (-0.707107f, -0.707107f),  // -135°
            (-1.000000f,  0.000000f),  // 180°
        };

        private static bool MoveOneTick(Shroomp s, LocalMap? map, float dtSeconds, Random rng,
            int tickInterval = 1)
        {
            if (s.CurrentTask == null) return false;

            // v0.4.29 — DF-style yield: lying-down shroomps hold position so
            // the shroomp they're blocking can climb over them. Decrement
            // the timer and skip movement entirely while it's active.
            // Doesn't reset StuckTicks — if this shroomp had its OWN stuck
            // counter rising before being asked to yield, it picks up
            // where it left off after standing back up.
            //
            // v0.4.42 BUGFIX: returns FALSE, not true. The caller reads the
            // return value as "shroomp arrived at task target" and fires
            // ApplyTaskEffect on true — which would mutate the designated
            // tile (boulder → mud, vegetation → cleared, etc.) without
            // the shroomp actually being there. Sam's report: tiles
            // destroyed after designation despite no shroomp walking over.
            // A yielding shroomp is NOT arriving anywhere; they're just
            // holding position. Returning false keeps the tick a no-op
            // for the post-arrival accounting block.
            if (s.YieldingTicks > 0)
            {
                s.YieldingTicks -= tickInterval;
                if (s.YieldingTicks < 0) s.YieldingTicks = 0;
                s.PrevSimPos = s.SimPos;
                return false;
            }

            // 1. SimPos rescue — never reason from a position inside a rock.
            if (map != null && !IsPixelPassable(map, s.SimPos))
            {
                var rescued = NearestPassableTileCentre(s.SimPos, map);
                if (rescued.HasValue) s.SimPos = rescued.Value;
            }

            // v0.4.19 — path-invalidation gate. The cached A* path is a
            // snapshot from task-selection time; if a downstream
            // waypoint's tile has since become impassable (vegetation
            // regrowth, another shroomp's excavation invalidating a
            // corridor, a player-issued wall placement) we'd waste the
            // stuck-detector window walking into it. Check the head
            // waypoint (the only one we're about to step onto this tick);
            // if it's now in a wall, drop the path and request a fresh
            // A* immediately so the shroomp reroutes within the same tick
            // instead of bashing at the new obstacle for 15 ticks.
            // v0.5.82 — full-path re-validation (was: head-waypoint only).
            // Pre-v0.5.82 only PathWaypoints[0] was checked here; a tile
            // turning impassable DEEPER in the path (another shroomp's
            // wall placement, vegetation regrowth, save-load race) went
            // undetected until the shroomp physically walked into it and
            // the stuck-detector eventually fired ~8 ticks later. Now we
            // scan every queued waypoint; the first impassable one
            // triggers the re-path. Cheap O(N) over ≤ ~30 waypoints.
            if (map != null && s.PathWaypoints.Count > 0)
            {
                bool anyInvalid = false;
                for (int wi = 0; wi < s.PathWaypoints.Count; wi++)
                {
                    Vector2 wp = s.PathWaypoints[wi];
                    int wpx = (int)(wp.X / LocalMap.TileSize);
                    int wpy = (int)(wp.Y / LocalMap.TileSize);
                    if (!map.IsPassable(wpx, wpy)) { anyInvalid = true; break; }
                }
                if (anyInvalid)
                {
                    s.PathWaypoints.Clear();
                    if (s.CurrentTask is BehaviorTask invalTask
                        && IsDesignationTaskType(invalTask.Type)
                        && invalTask.TargetTileX >= 0 && invalTask.TargetTileY >= 0)
                    {
                        // v0.4.58 — crowd-aware re-path. Same crowd cost
                        // applies on the in-flight path-invalidation
                        // recompute as on initial task assignment.
                        bool found = Pathfinder.FindPath(map, s.SimPos,
                            (invalTask.TargetTileX, invalTask.TargetTileY),
                            s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
                        if (found) RecordPathPawnBlockage(s);   // v0.5.82
                    }
                }
            }

            // 2. Decide what to walk toward this tick.
            Vector2 walkTo = ResolveWalkTarget(s, map);
            s.SimTarget = walkTo;

            // v0.4.57 — RimWorld-style PathEndMode.Touch arrival for
            // impassable-target work tasks. Previously the arrival check
            // was `dist(SimPos, walkTo) <= ArrivalRadius`, where walkTo
            // is the specific adjacent tile NearestAdjacentPassableTile
            // picked at task assignment. At saturated work sites (50
            // shroomps converging on a 10×10 boulder cluster, only ~36
            // perimeter tiles reachable), every adjacent tile is
            // crowded — soft-collision steering vetoes stepping onto
            // them, so the shroomp orbits at `ArrivalRadius + ε` and
            // never crosses the threshold. ApplyTaskEffect never fires.
            // Result: cascading stuck-out → abandonment → re-pick same
            // task → cycle.
            //
            // RimWorld's PathFinder uses `PathEndMode.Touch`: the goal
            // is reached when ANY of the 8 cells adjacent to the
            // target is reached, whichever the search hits first.
            // Mirror that here: if the task target tile is impassable
            // (Boulder / DeadLog / LivingWood / LargeMushroom etc.)
            // and the shroomp is Chebyshev-distance ≤ 1 from it, fire
            // arrival regardless of which adjacent the path-resolver
            // picked. The cluster-jam dissolves because any shroomp in
            // any of the 8 neighbours can complete the work effect.
            if (IsAtTouchArrival(s, map))
            {
                s.PrevSimPos = s.SimPos;
                s.StuckTicks = 0;
                MarkIdleArrivalIfNeeded(s);   // v0.5.1
                return true;
            }

            Vector2 diff = walkTo - s.SimPos;
            float dist = diff.Length();
            if (dist <= ArrivalRadius)
            {
                // v0.5.79 — snap SimPos to the walk target on Sleep arrival
                // so the shroomp lands ON the bed tile (not adjacent at
                // ArrivalRadius-1 px away). Without the snap, ArrivalRadius=14
                // can fire when SimPos is in a neighbouring tile, the
                // ApplyTaskEffect Sleep skips its `atBed` branch, and the
                // ComputeIsSleeping check (tile-equality with target) returns
                // false so the renderer keeps the shroomp upright. Sam:
                // "Ensure pawns move to and sleep on the same tile as the
                // bed they're sleeping on."
                if (s.CurrentTask is { Type: TaskType.Sleep } st
                    && st.TargetTileX >= 0 && st.TargetTileY >= 0)
                {
                    s.SimPos = walkTo;
                }
                s.PrevSimPos = s.SimPos;
                s.StuckTicks = 0;
                MarkIdleArrivalIfNeeded(s);   // v0.5.1
                return true;
            }

            // 3. Local steering — try fan-out angles in increasing deviation.
            // v0.3.36 (B.17) — multiply by precomputed (cos, sin) instead of
            // calling Vector2.Rotated(angle), which would re-evaluate
            // Cos+Sin per call. Algebraically identical: rotating (x, y) by
            // θ is (x·cos − y·sin, x·sin + y·cos).
            // v0.4.20 — primary-direction-wins rule. v0.4.19's two-tier
            // scoring evaluated every angle for crowdedness, which caused
            // an orbital failure mode: when the shroomp's destination tile
            // happened to be crowded, the fan-out preferred any uncrowded
            // side-step over the (crowded but correct) primary. The
            // shroomp orbited at `ArrivalRadius + ε` and never crossed the
            // arrival threshold, so ApplyTaskEffect never fired —
            // exactly the dig-cluster jitter the player reported.
            //
            // RimWorld's local steering handles this by always taking
            // the primary direction when it's *terrain*-passable and
            // only consulting the soft-collision fallback when the
            // primary is blocked by a wall. v0.4.36 — softened that rule so
            // shroomps prefer walking AROUND each other when there's room to
            // do so.
            //
            // v0.5.10 — softening v0.4.36 was too aggressive. In dense
            // clusters (10+ shroomps converging on 4 work targets, see Sam
            // screenshot) the "first uncrowded side-step wins" rule sent
            // every shroomp perpendicular to its path, abandoning the A*
            // route that crowd-cost-soft-cost-A* (v0.4.58) carefully
            // computed. Shroomps oscillated sideways without forward
            // progress, paths invalidated, repaths fired, more chaos.
            // RimWorld's actual behaviour: pawns commit to their planned
            // path even through other pawns (they visually overlap; the
            // crowd cost was applied at planning time, not at walking
            // time). Lifting that idea: when the primary direction is
            // path-useful (next path waypoint or touch-arrival to target)
            // we prefer climbing over the blocker to side-stepping away.
            //
            // Resolution order (post-v0.5.10):
            //   1. Primary terrain-passable AND uncrowded → take (best).
            //   2. Primary passable + crowded + climb is *useful* (path-
            //      follow with passable beyond OR touch-arrival via
            //      v0.5.8 IsClimbOverUseful) → take primary, climb the
            //      blocker. Keeps the shroomp on its planned path through
            //      a crowd. The blocker isn't yielded here; the visual
            //      overlap is acceptable (RimWorld parity).
            //   3. Primary impassable OR climb-not-useful → fan rotated
            //      angles ±45° … 180° for an uncrowded passable side-step.
            //      First match wins. This is the genuine "route around
            //      an obstacle" branch.
            //   4. No uncrowded side-step → take a useful crowded
            //      candidate as fallback (preserves v0.5.8 dead-end
            //      guard).
            //   5. Nothing useful at all → stay put. StuckTicks builds,
            //      v0.4.29 YieldTrigger fires at 12 ticks asking the
            //      blocker to lie down, then the shroomp walks through
            //      naturally because the yielding shroomp drops out of
            //      the occupancy grid.
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0
                ? curTileY * _occGridWidth + curTileX
                : -1;

            // v0.4.38 — terrain-based movement-speed multiplier. Shallows
            // (v0.4.37 passable shallow water) slows shroomps to 30 % walk
            // speed while wading, matching RimWorld's shallow-water value.
            // Applied to baseStep BEFORE the direction search so the same
            // multiplier governs whatever direction the steering picks.
            // Shroombridges (§5.11.d, Phase 5) will sit on Shallows tiles
            // and lift the penalty when they land.
            float terrainSpeedMul = 1f;
            if (map != null && map.InBounds(curTileX, curTileY)
                && map.Get(curTileX, curTileY).Terrain == TerrainType.Shallows)
            {
                terrainSpeedMul = 0.30f;
            }

            // v0.5.81 — Moving capacity from leg/foot damage. Phase 7 prep:
            // an injured shroomp limps proportionally to their leg
            // condition (RimWorld parity). Both legs intact = 1.0×; one
            // shredded = ~0.5×; both shredded ≈ 0 (and the shroomp is
            // already Downed at that point per v0.5.79 thresholds). The
            // multiplier folds into baseStep alongside terrain so the
            // existing steering / pathfind layers don't need to know
            // about injury — they just see a slower shroomp.
            float movingMul = s.ComputeMovingCapacity();
            // v0.5.84r — Athletics-level move-speed bonus. Sam: "[Athletics
            // gives] a tiny increase in carry capacity/movement speed for
            // each level." 0.5 % per level → lvl 0 = 1.0×, lvl 20 = 1.10×.
            // Stacks multiplicatively with terrain + injury-mediated moving
            // capacity. Per-tick lookup is one dict read; negligible cost.
            float athleticsMul = 1.0f + 0.005f * SkillLevel(s, "Athletics");
            Vector2 baseStep = diff.Normalized() * s.SimSpeed * terrainSpeedMul * movingMul * athleticsMul * dtSeconds;
            Vector2 bestChosen = Vector2.Zero;
            bool moved = false;

            Vector2 primary = s.SimPos + baseStep;
            // v0.5.77 — step-level passability (refuses diagonal corner-cuts).
            bool primaryPassable = map == null || IsStepPassable(map, s.SimPos, primary);
            bool primaryCrowded  = false;
            if (primaryPassable && map != null && _occGridWidth > 0)
            {
                int pTx = (int)(primary.X / LocalMap.TileSize);
                int pTy = (int)(primary.Y / LocalMap.TileSize);
                int pIdx = pTy * _occGridWidth + pTx;
                primaryCrowded = TileHasOtherShroomp(pIdx, curTileIdx);
            }

            // Best case: primary terrain-passable AND uncrowded.
            if (primaryPassable && !primaryCrowded)
            {
                bestChosen = primary;
                moved = true;
            }
            // v0.5.84t — REMOVED priority-2 climb-over-primary fast path.
            // Pre-v0.5.84t (this patch) the steering would commit to
            // "stepping onto a crowded blocker tile" BEFORE trying the
            // fan-out side-steps. That made BuildHaul haulers walking
            // through stockpiles step over every shroomp in their way
            // instead of going around — Sam: "Shroomps need to stop
            // stepping over each other so much. They should first try to
            // avoid, then only step over if needed as it looks like it's
            // interrupting their buildhaul tasks."
            //
            // New priority order (RimWorld parity — avoid before climb):
            //   1. Primary uncrowded → take it (best case, handled above).
            //   2. Fan-out for an uncrowded side-step.
            //   3. crowdedFallback (primary if useful + crosses a tile,
            //      or first useful crowded side-step) — only when NO
            //      uncrowded option exists.
            //   4. Wait in place — stuck detector + yield + replan fire
            //      after StuckRePathTicks.
            //
            // The fan-out's crowdedFallback already handles "everything
            // crowded → take primary"; removing the fast path just lets
            // step 2 run first.
            else if (map != null)
            {
                // Look for an uncrowded side-step before settling for a
                // crowded primary (climb-over). Loops through ±45° … 180°
                // rotations; first uncrowded passable tile wins, otherwise
                // we remember the best crowded fallback to use only if no
                // uncrowded alternative existed. `map` is guaranteed
                // non-null here — the primary-passable branch above
                // covered the map-null case (treats primary as passable).
                Vector2 crowdedFallback = primary;
                // v0.5.8 — climb-over (stepping onto an occupied tile) is
                // only accepted as a fallback if the climb is *useful* —
                // either the candidate tile is touch-arrival distance to
                // the task target (so stopping there completes the work
                // via IsAtTouchArrival next tick), OR the tile beyond the
                // candidate in the direction of motion is passable (so the
                // shroomp has somewhere to continue after climbing). Without
                // this guard, shroomps would climb onto a blocker B whose
                // far side is an impassable wall / their excavate target,
                // then oscillate because they can't continue forward and
                // their primary direction keeps pulling them back into B.
                // Sam: "shroomps can't path through another shroomp and get
                // stuck when they can't pass over them into an unpassable
                // tile."
                // v0.5.84t — primary-as-fallback also requires CrossesTileBoundary
                // so partial-pixel nudges don't bypass the stuck detector.
                // Same guard the removed priority-2 fast path carried.
                bool    crowdedFallbackHas = primaryPassable
                    && IsClimbOverUseful(map, s, primary, baseStep)
                    && CrossesTileBoundary(s.SimPos, primary);
                for (int i = 1; i < SteerVectors.Length; i++)
                {
                    var (c, sn) = SteerVectors[i];
                    Vector2 rotated = new Vector2(
                        baseStep.X * c  - baseStep.Y * sn,
                        baseStep.X * sn + baseStep.Y * c);
                    Vector2 candidate = s.SimPos + rotated;
                    // v0.5.77 — step-level passability (refuses diagonal
                    // corner-cuts through wall corners). The ±45° / ±135°
                    // entries in SteerVectors are the cases that previously
                    // sneaked through IsPixelPassable when only the
                    // destination tile was passable.
                    if (!IsStepPassable(map, s.SimPos, candidate)) continue;
                    bool crowded;
                    if (_occGridWidth > 0)
                    {
                        int cTx = (int)(candidate.X / LocalMap.TileSize);
                        int cTy = (int)(candidate.Y / LocalMap.TileSize);
                        int cIdx = cTy * _occGridWidth + cTx;
                        crowded = TileHasOtherShroomp(cIdx, curTileIdx);
                    }
                    else { crowded = false; }
                    if (!crowded)
                    {
                        // Found a clear side-step — take it and stop searching.
                        bestChosen = candidate;
                        moved = true;
                        break;
                    }
                    // First crowded candidate becomes the fallback if no
                    // uncrowded one is found by the end of the loop. v0.5.8
                    // — only accept as fallback if the climb-over is
                    // useful (see crowdedFallbackHas init above).
                    if (!crowdedFallbackHas && IsClimbOverUseful(map, s, candidate, rotated))
                    {
                        crowdedFallback = candidate;
                        crowdedFallbackHas = true;
                    }
                }
                if (!moved && crowdedFallbackHas)
                {
                    // Last resort: every option is crowded, including the
                    // primary if it's terrain-passable. Take the primary
                    // (or first crowded side-step if primary is blocked).
                    // Paired with the v0.4.29 yield trigger this still
                    // unblocks single-tile-tunnel jams via lie-down.
                    bestChosen = crowdedFallback;
                    moved = true;
                }
                // v0.5.8 — if NO climb-over candidate was useful, the
                // shroomp stays put this tick. StuckTicks builds → the
                // v0.4.29 YieldTrigger fires at 12 ticks, asking the
                // blocker to lie down. Once the blocker yields, its tile
                // drops out of the occupancy grid (PopulateOccupancyGrid
                // line 157) so the shroomp's next primary step becomes
                // uncrowded and resolves naturally.
            }

            if (moved)
            {
                s.SimPos = bestChosen;
                // v0.5.76 — register the destination tile so later shroomps
                // in this same batch tick see it as occupied. Prevents
                // multi-shroomp same-tick pileups at doorways / corners.
                if (_occGridWidth > 0)
                {
                    int newTileX = (int)(bestChosen.X / LocalMap.TileSize);
                    int newTileY = (int)(bestChosen.Y / LocalMap.TileSize);
                    int newIdx   = newTileY * _occGridWidth + newTileX;
                    ClaimTileForMove(curTileIdx, newIdx);
                }
                // v0.5.84r — walking-trickle Athletics XP. Sam: "Walking
                // should also provide a very small trickle of Athletics
                // XP." 0.04 XP per move tick × hot LOD ~60 ticks/sec at
                // 1× = ~2.4 XP/sec while walking. Over an in-game day
                // (~10 sec real-time at default speed) that's ~24 XP per
                // active walking-day — slow but persistent. A pawn that
                // hauls heavily levels Athletics primarily via the haul
                // completion grant (40 XP/drop); a pawn that wanders /
                // walks errands still levels slowly via this trickle.
                SkillRegistry.GainXp(s, "Athletics", 0.04f);
            }
            // else: fully blocked — stay put (never teleport).

            // 4. Stuck detection — increment when net progress is below epsilon.
            // v0.3.39 (O-H.2) — scale the increment by the LOD tick interval
            // so cold shroomps (which only tick every 6 sim ticks) accumulate
            // stuck-ness at the same real-time rate as hot shroomps. Without
            // this, a cold shroomp would take 6× longer to give up than a hot
            // shroomp in the same physical configuration.
            // v0.5.84t — supplement the pixel-progress check with a tile-
            // boundary tracker. Pre-v0.5.84t a pawn micro-jittering 0.6 px/tick
            // into a wall passed the 0.5 px threshold every tick (StuckTicks
            // never accumulated). Now we also count as stuck when the
            // shroomp HAS moved pixels but hasn't entered a new tile —
            // that's the wall-grind pattern. Slow legitimate walks still
            // pop their tile every few ticks (full speed ~6 px/tick on a
            // 16 px tile = 3 ticks; encumbered ~2 px/tick = 8 ticks), well
            // under the StuckRePathTicks (~30) repath threshold.
            float progressed = (s.SimPos - s.PrevSimPos).Length();
            int curTileIdxForStuck = _occGridWidth > 0
                ? (int)(s.SimPos.Y / LocalMap.TileSize) * _occGridWidth
                  + (int)(s.SimPos.X / LocalMap.TileSize)
                : -1;
            bool tileChanged = curTileIdxForStuck != s.LastProgressTileIdx;
            if (tileChanged) s.LastProgressTileIdx = curTileIdxForStuck;
            bool pixelStuck = progressed < ArrivalEpsilon;
            bool tileStuck  = !tileChanged && !pixelStuck;   // moving but not crossing tiles
            if (pixelStuck || tileStuck)
            {
                s.StuckTicks += tickInterval;

                // v0.4.17 — one re-pathfind attempt at the halfway mark.
                // Corner-stuck oscillation usually clears with a fresh A*
                // path from the shroomp's current pixel (the v0.4.16 always-
                // A* path was computed at task selection from an earlier
                // SimPos; the shroomp may have drifted into a tile from
                // which a different route is needed). Cheap (one A* per
                // shroomp per stuck window) and lets the shroomp recover
                // without triggering the give-up + blacklist path. Only
                // fires for designation tasks since those are the ones
                // routed through A* in the first place.
                // v0.5.3 — PlayerOrder joins the re-path tier. Pre-v0.5.3
                // a stuck player order rode out the full ~90-tick stuck
                // window before give-up; with re-path enabled the shroomp
                // tries an alternative route at ~30 ticks. Same one-shot
                // budget (RePathTried) so a genuinely-blocked order still
                // gives up at StuckThreshold and doesn't loop forever.
                // v0.5.82 — RimWorld-parity pawn-blocked-path cooldown.
                // If the previous A* path was pawn-blocked, suppress
                // re-pathing for PawnBlockedRepathCooldown ticks; the
                // shroomp sits and waits for the cluster to disperse.
                // Pre-v0.5.82 the one-shot RePathTried gate let a shroomp
                // re-path once per task, which re-shuffled the waypoint
                // list but generally landed on a similarly-crowded route
                // — the visible "jittering after a few minutes" Sam
                // reported. The cooldown closes that loop.
                bool pawnBlockedRecently =
                    _currentTick - s.LastPawnBlockedPathTick < PawnBlockedRepathCooldown;
                if (map != null && !s.RePathTried && !pawnBlockedRecently
                    && s.StuckTicks >= StuckRePathTicks
                    && s.CurrentTask is BehaviorTask rpt
                    && (IsDesignationTaskType(rpt.Type) || rpt.IsPlayerOrder)
                    && rpt.TargetTileX >= 0 && rpt.TargetTileY >= 0)
                {
                    s.RePathTried = true;
                    // v0.4.18 — fill-into-buffer API. Reuses s.PathWaypoints,
                    // zero per-call alloc.
                    // v0.4.58 — crowd-aware stuck re-path. The most likely
                    // cause of the stuck is that the prior path's waypoint
                    // is now fully occupied; the crowd cost steers the
                    // recompute through neighbouring tiles instead of
                    // routing back through the same jam.
                    bool found = Pathfinder.FindPath(map, s.SimPos,
                        (rpt.TargetTileX, rpt.TargetTileY), s.PathWaypoints,
                        _shroompPerTile, OccTileIdx(s));
                    if (found && s.PathWaypoints.Count > 0)
                    {
                        s.StuckTicks = 0;   // give the new path a clean budget
                        RecordPathPawnBlockage(s);   // v0.5.82 — arm the cooldown if still crowded
                    }
                }

                // v0.4.29 — DF-style yield. Re-pathfind didn't help
                // (StuckTicks kept climbing past the trigger), so the
                // obstruction is almost certainly another shroomp, not bad
                // routing. Ask the blocker in the primary direction to lie
                // down; on success this resets StuckTicks so the give-up
                // window starts fresh while we walk over them.
                if (map != null && s.StuckTicks >= YieldTriggerTicks)
                    TryTriggerBlockerYield(s, map);

                if (s.StuckTicks > StuckThreshold)
                {
                    // Give up on this task. v0.3.23 routes to a fresh Wander.
                    // v0.3.33 (B.7) — also release the designation claim so
                    // another shroomp can pick the tile up.
                    // v0.3.35 — record the abandoned tile in the shroomp's
                    // short-term avoid list so SelectTask doesn't immediately
                    // re-pick it. 300 ticks ≈ 5 sec at 1×, enough for the
                    // shroomp to wander far enough that a different target
                    // becomes closer.
                    if (s.CurrentTask is BehaviorTask gtask
                        && (gtask.Type == TaskType.GatherFood || gtask.Type == TaskType.GatherMaterial
                            || gtask.Type == TaskType.ChopWood || gtask.Type == TaskType.CutVegetation)
                        && gtask.TargetTileX >= 0 && gtask.TargetTileY >= 0)
                    {
                        // v0.3.40 — push this tile into the FIFO blacklist.
                        // Find the slot with the smallest TicksLeft (the
                        // oldest entry, or any empty slot) and overwrite it.
                        // Other slots keep their TTL — consecutive give-ups
                        // accumulate up to 4 distinct blacklisted tiles.
                        // v0.4.59 — TTL halved 360 → 180 (~6 s → 3 s at 1×).
                        // With v0.4.58 A* crowd cost handling cluster
                        // routing strategically, the per-tile blacklist
                        // matters less; shorter TTL lets the shroomp retry
                        // a tile that's since cleared.
                        int oldestIdx = 0;
                        int oldestTtl = int.MaxValue;
                        for (int i = 0; i < s.AvoidTiles.Length; i++)
                            if (s.AvoidTiles[i].TicksLeft < oldestTtl)
                            { oldestTtl = s.AvoidTiles[i].TicksLeft; oldestIdx = i; }
                        s.AvoidTiles[oldestIdx] = (gtask.TargetTileX, gtask.TargetTileY, 180);
                    }
                    // v0.3.43 — give-up emits a frustration thought so
                    // repeated failures show in mood as well as in the
                    // shroomp's behaviour. Single thought slot (RimWorld
                    // pattern), so multiple stucks just refresh its TTL
                    // rather than stacking.
                    ThoughtRegistry.Add(s, "TaskAbandoned");
                    ReleaseTaskClaim(s, map);
                    // v0.4.57 — post-abandon cooldown. Forces this shroomp
                    // into idle/wander tier so they don't immediately
                    // re-pick the same designation from the work cluster
                    // they just gave up on. RimWorld-equivalent of the
                    // 10-jobs-in-10-ticks spam-guard force-idle.
                    // v0.4.59 — halved 120 → 60 ticks (~2 s → ~1 s at 1×).
                    // With v0.4.58's A* crowd cost dispersing paths from
                    // the start, the cooldown's main job (give the
                    // cluster time to breathe) needs less wall-clock
                    // because the cluster forms less tightly to begin
                    // with — 1 s of forced wander is enough to physically
                    // displace the shroomp to a position where a different
                    // designation is closer.
                    s.DesignationCooldownTicks = 60;
                    s.CurrentTask = NewWanderTask(s.SimPos, map, rng);
                    s.StuckTicks = 0;
                    s.PathWaypoints.Clear();
                }
            }
            else
            {
                s.StuckTicks = 0;
                s.RePathTried = false;   // v0.4.17 — fresh budget once we're moving again
            }

            // v0.5.11 — distance-not-decreasing detector. Fires regardless
            // of whether the shroomp is moving, because the failure mode is
            // "moving sideways at a corner forever, never getting closer
            // to the next waypoint." MinSqrDistanceToWalkTarget tracks the
            // smallest distance² ever achieved to the current walk target
            // (the head of PathWaypoints, or task.Target if no waypoints).
            // Reset when the walk target changes (waypoint pops, path
            // refresh). Two thresholds:
            //
            //   • NoProgressRePathTicks (30): if we haven't beaten our
            //     best distance for ~0.5 s, request a fresh A*. The local
            //     geometry might be navigable with a different path. One-
            //     shot via ProgressRePathTried so genuinely-blocked cases
            //     still escalate.
            //
            //   • NoProgressGiveUpTicks (60 post-re-path): if the new path
            //     also doesn't help, abandon the task — same flow as the
            //     immobility-based StuckThreshold give-up.
            //
            // Skipped for idle tasks (Wander/Loiter/etc.) because their
            // arrived-and-lingering state has distance ≈ 0 indefinitely
            // by design — false-positive territory.
            if (map != null && s.CurrentTask is { } progressTask
                && !IsIdleType(progressTask.Type))
            {
                Vector2 walkTarget = s.PathWaypoints.Count > 0
                    ? s.PathWaypoints[0]
                    : progressTask.Target;
                int wpTx = (int)(walkTarget.X / LocalMap.TileSize);
                int wpTy = (int)(walkTarget.Y / LocalMap.TileSize);

                // Walk target changed (waypoint popped, task replaced) →
                // reset tracking. New target gets a fresh window. Also
                // reset ProgressRePathTried so each new walk segment
                // gets its own re-path budget — Section 1 player orders,
                // v0.5.5 Wander chain hops, and Haul phase transitions
                // mutate CurrentTask without going through section 2a's
                // reset, so the budget needs to refresh here too.
                if (wpTx != s.LastWalkTargetTileX || wpTy != s.LastWalkTargetTileY)
                {
                    s.LastWalkTargetTileX = wpTx;
                    s.LastWalkTargetTileY = wpTy;
                    s.MinSqrDistanceToWalkTarget = float.MaxValue;
                    s.NoProgressTicks = 0;
                    s.ProgressRePathTried = false;
                }

                Vector2 toWalk = walkTarget - s.SimPos;
                float currentSqrDist = toWalk.X * toWalk.X + toWalk.Y * toWalk.Y;

                if (currentSqrDist < s.MinSqrDistanceToWalkTarget)
                {
                    // Closer than ever — real progress. Update best,
                    // reset counter.
                    s.MinSqrDistanceToWalkTarget = currentSqrDist;
                    s.NoProgressTicks = 0;
                }
                else
                {
                    // No progress this tick. Accumulate scaled by LOD
                    // tickInterval so cold shroomps hit the threshold at
                    // the same wall-clock rate as hot shroomps.
                    s.NoProgressTicks += tickInterval;

                    if (!s.ProgressRePathTried
                        && s.NoProgressTicks >= NoProgressRePathTicks
                        && progressTask.TargetTileX >= 0 && progressTask.TargetTileY >= 0)
                    {
                        // Stage 1 — try a fresh A* from the current
                        // SimPos. Different geometry, different path
                        // sequence. Same crowd-cost-aware Pathfinder API
                        // as the immobility re-path at line ~1413.
                        s.ProgressRePathTried = true;
                        Pathfinder.FindPath(map, s.SimPos,
                            (progressTask.TargetTileX, progressTask.TargetTileY),
                            s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
                        // Reset tracking so the new path gets its own
                        // measurement window.
                        s.MinSqrDistanceToWalkTarget = float.MaxValue;
                        s.NoProgressTicks = 0;
                        s.LastWalkTargetTileX = -1;
                        s.LastWalkTargetTileY = -1;
                    }
                    else if (s.ProgressRePathTried
                        && s.NoProgressTicks >= NoProgressGiveUpTicks)
                    {
                        // Stage 2 — re-path also failed. Abandon the task.
                        // Mirrors the StuckThreshold abandon block at
                        // line ~1431. Designation tasks get tile blacklist
                        // so the shroomp doesn't immediately re-pick the
                        // same target. Forced wander + cooldown gives the
                        // cluster physical breathing room.
                        if (s.CurrentTask is BehaviorTask gt
                            && IsDesignationTaskType(gt.Type)
                            && gt.TargetTileX >= 0 && gt.TargetTileY >= 0)
                        {
                            int oldestIdx = 0, oldestTtl = int.MaxValue;
                            for (int i = 0; i < s.AvoidTiles.Length; i++)
                                if (s.AvoidTiles[i].TicksLeft < oldestTtl)
                                { oldestTtl = s.AvoidTiles[i].TicksLeft; oldestIdx = i; }
                            s.AvoidTiles[oldestIdx] = (gt.TargetTileX, gt.TargetTileY, 180);
                        }
                        ThoughtRegistry.Add(s, "TaskAbandoned");
                        ReleaseTaskClaim(s, map);
                        s.DesignationCooldownTicks = 60;
                        s.CurrentTask = NewWanderTask(s.SimPos, map, rng);
                        s.StuckTicks = 0;
                        s.PathWaypoints.Clear();
                        s.MinSqrDistanceToWalkTarget = float.MaxValue;
                        s.NoProgressTicks = 0;
                        s.LastWalkTargetTileX = -1;
                        s.LastWalkTargetTileY = -1;
                        s.ProgressRePathTried = false;
                    }
                }
            }

            s.PrevSimPos = s.SimPos;

            return false;
        }

        // Picks the actual pixel the shroomp should walk toward this tick.
        //
        //   • If Phase 4's planner has populated PathWaypoints, the head of
        //     the list is the next waypoint. When the shroomp is within
        //     ArrivalRadius of that waypoint we pop it and the next tick
        //     advances to the one after.
        //   • If the *task* target is an impassable tile (GatherMaterial),
        //     we route to the nearest passable neighbour of that tile rather
        //     than into the rock itself. The shroomp will arrive at the
        //     neighbour, ApplyTaskEffect will fire while the shroomp stands
        //     adjacent to the boulder, and the task's tile coordinates still
        //     drive the harvest/excavation effect.
        //   • Otherwise the task target is used directly.
        private static Vector2 ResolveWalkTarget(Shroomp s, LocalMap? map)
        {
            // Phase-4 path consumption: walk to the next waypoint until close.
            while (s.PathWaypoints.Count > 0)
            {
                Vector2 wp = s.PathWaypoints[0];
                if ((wp - s.SimPos).Length() <= ArrivalRadius)
                {
                    s.PathWaypoints.RemoveAt(0);
                    continue;
                }
                return wp;
            }

            // v0.3.36 — `task` is unwrapped from Nullable<BehaviorTask>. The
            // caller checked CurrentTask != null before invoking us.
            var task = s.CurrentTask!.Value;
            if (map != null && task.TargetTileX >= 0 && task.TargetTileY >= 0)
            {
                // v0.5.84t — UNIVERSAL impassable-target redirect. Pre-v0.5.84t
                // only Gather/Chop/Cut redirected to an adjacent passable tile
                // when their target was impassable. Build / Sleep / Cook /
                // BuildHaul / DoBill / etc. let `walkTo` point at the wall
                // tile centre — and once PathWaypoints emptied, local steering
                // grinded the shroomp into the wall (Sam playtest: pawns
                // bunched on a 2-tile-thick wall with a 3-tile doorway behind
                // them). New invariant: `walkTo` NEVER points at an impassable
                // tile centre. If the task target itself is impassable, pick
                // the nearest adjacent passable tile in the shroomp's DF
                // region — same picker used by the legacy Gather/Chop/Cut
                // path. Falls back to raw task target only when no passable
                // neighbour exists (extreme edge case; the shroomp won't
                // make progress but at least the steering won't grind a
                // wall).
                if (!map.IsPassable(task.TargetTileX, task.TargetTileY))
                {
                    int ssx = (int)(s.SimPos.X / LocalMap.TileSize);
                    int ssy = (int)(s.SimPos.Y / LocalMap.TileSize);
                    var adj = NearestAdjacentPassableTile(task.TargetTileX, task.TargetTileY, map, ssx, ssy);
                    if (adj.HasValue) return TileToPixel(adj.Value);
                }
            }
            return task.Target;
        }

        // Task types whose target tile is itself impassable and that interact
        // *with* the tile (chop / mine / dig) rather than stand on it. Eating
        // happens on the shroomp's own tile, so it isn't in this list.
        private static bool RequiresAdjacentApproach(TaskType t) =>
            // v0.3.38 — ChopWood and CutVegetation join the adjacent-approach
            // list because LargeMushroom variants are impassable until
            // their cap clears, and a shroomp trying to walk to the tile
            // centre would get blocked by the same wall it's trying to
            // harvest. ResolveWalkTarget routes them to a neighbour.
            t == TaskType.GatherMaterial || t == TaskType.ChopWood || t == TaskType.CutVegetation;

        // v0.4.57 — RimWorld PathEndMode.Touch semantics. True iff the
        // shroomp's current tile is Chebyshev-distance ≤ 1 from an
        // impassable-target work task's target tile. Used to fire
        // ApplyTaskEffect arrival as soon as the shroomp is at ANY of
        // the 8 neighbours of the work target, not specifically the
        // adjacent tile NearestAdjacentPassableTile picked at
        // task-assignment time. Critical at saturated work sites
        // where the picker's chosen adjacent is occupied by another
        // shroomp and soft-collision steering blocks entry.
        private static bool IsAtTouchArrival(Shroomp s, LocalMap? map)
        {
            if (map == null) return false;
            if (s.CurrentTask is not { } ct) return false;
            if (!RequiresAdjacentApproach(ct.Type)) return false;
            if (ct.TargetTileX < 0 || ct.TargetTileY < 0) return false;
            if (map.IsPassable(ct.TargetTileX, ct.TargetTileY)) return false;
            // Shroomp must also be on the same DF region (no leaping
            // through a wall to "touch" a boulder on the far side).
            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
            int dx = ct.TargetTileX - sx; if (dx < 0) dx = -dx;
            int dy = ct.TargetTileY - sy; if (dy < 0) dy = -dy;
            int cheb = dx > dy ? dx : dy;
            return cheb <= 1;
        }

        // v0.4.13 — designation-backed task types. Used by the fail-fast
        // unreachable handler to decide whether a path-null result should
        // blacklist the target tile (so SelectTask doesn't immediately
        // re-pick it). Haul / combat / move orders are excluded —
        // player-issued moves can legitimately retry across map state
        // changes, and haul reservations have their own retry loop.
        private static bool IsDesignationTaskType(TaskType t) =>
            t == TaskType.GatherFood || t == TaskType.GatherMaterial
            || t == TaskType.ChopWood || t == TaskType.CutVegetation
            || t == TaskType.Build                         // v0.5.19 Phase 5B
            || t == TaskType.BuildHaul;                    // v0.5.60

        // v0.5.19 (Phase 5B) — consume materials for a Build task. Returns
        // true if the full cost was taken from the colony Inventory; false
        // if the inventory didn't have enough (in which case the build
        // aborts and the blueprint stays for later). Tries the requested
        // family first (Stone or Wood from the blueprint's Material), then
        // falls back to the other family if the first is insufficient —
        // shroomps are pragmatic builders, they'll use whatever's on hand.
        // Future Phase 5C polish: explicit material-tier preferences (a
        // Wall blueprint with Material=Stone will refuse Wood substitution
        // if the player set a "stone-only" preference).
        // v0.5.57 — does the shroomp's inventory contain at least one unit of
        // the material requested by the given blueprint? Used by the Build
        // SelectTask branch (route to source if false) and the Build
        // ApplyTaskEffect (deposit if true and at blueprint).
        private static bool ShroompCarriesMatchingBuildMaterial(Shroomp s, Sporeholm.World.StructureMat mat)
        {
            string family = Sporeholm.World.StructureMatMeta.ConsumeFamily(mat);
            string? subType = Sporeholm.World.StructureMatMeta.ConsumeSubType(mat);
            // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
            string? itemSubType = Sporeholm.World.StructureMatMeta.ConsumeItemSubType(mat);
            foreach (var it in s.Inventory)
            {
                if (it.Quantity <= 0) continue;
                if (it.Kind != Items.ItemKind.Material) continue;
                if (it.Material.Family != family) continue;
                if (subType != null && it.Material.SubType != subType) continue;
                if (itemSubType != null && it.SubType != itemSubType) continue;
                return true;
            }
            return false;
        }

        // v0.5.57 — pull one unit of matching build material out of the
        // shroomp's inventory. Returns true when a unit was consumed; false
        // when nothing matched. Called at the blueprint tile to advance
        // MaterialsDelivered without going through the colony pool /
        // map-drop fallback path.
        private static bool ConsumeOneFromShroompInventory(Shroomp s, Sporeholm.World.StructureMat mat)
        {
            string family = Sporeholm.World.StructureMatMeta.ConsumeFamily(mat);
            string? subType = Sporeholm.World.StructureMatMeta.ConsumeSubType(mat);
            // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
            string? itemSubType = Sporeholm.World.StructureMatMeta.ConsumeItemSubType(mat);
            for (int i = 0; i < s.Inventory.Count; i++)
            {
                var it = s.Inventory[i];
                if (it.Quantity <= 0) continue;
                if (it.Kind != Items.ItemKind.Material) continue;
                if (it.Material.Family != family) continue;
                if (subType != null && it.Material.SubType != subType) continue;
                if (itemSubType != null && it.SubType != itemSubType) continue;
                it.Quantity--;
                if (it.Quantity <= 0) s.Inventory.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static bool TryConsumeBuildMaterials(ColonyResources r, string preferredFamily, int cost)
            => TryConsumeBuildMaterials(r, preferredFamily, subType: null, cost);

        // v0.5.43 — material-strict overload. When `subType` is non-null
        // (e.g. "FungalWood" for a FungalWood blueprint) the consume
        // ONLY matches that subtype — no fallback to other subtypes in
        // the same family, no fallback to the other family. This makes
        // the player's material picker physically meaningful: a FungalWood
        // wall requires FungalWood logs in the colony pool. Sam: "nothing
        // using the correct materials can be built." Result: blueprint
        // stalls in delivery phase until the right material is supplied,
        // matching RimWorld's per-stuff strict-consume.
        //
        // When subType is null, falls back to the old "preferred family
        // then other family" behaviour for callers (none today) that
        // don't care about the specific material.
        //
        // v0.5.55 — RimWorld parity: the build consume now ALSO draws from
        // on-map stockpiles + ground drops (`map.ConsumeDroppedItemsByMaterial`),
        // not just the colony Inventory pool. Pre-v0.5.55 a colony with
        // 47 FungalWood logs sitting in a stockpile would still stall every
        // build tick because Inventory.ConsumeByMaterial only walked the
        // pool — and hauled wood lands on the MAP, not in the pool.
        // Sam: "Shroomps don't properly build buildings from materials that
        // are in the stockpile." Order of operations: inventory first
        // (fast, lock-light), map second (the actual stockpile contents).
        private static bool TryConsumeBuildMaterials(ColonyResources r, string preferredFamily, string? subType, int cost)
        {
            if (subType != null)
            {
                int taken = r.Inventory.ConsumeByMaterial(ItemKind.Material, preferredFamily, subType, cost);
                if (taken < cost && r.Map != null)
                    taken += r.Map.ConsumeDroppedItemsByMaterial(
                        ItemKind.Material, preferredFamily, subType, cost - taken);
                return taken >= cost;
            }
            string fallback = preferredFamily == "Stone" ? "Wood" : "Stone";
            int total = r.Inventory.ConsumeByFamily(ItemKind.Material, preferredFamily, cost);
            if (total < cost && r.Map != null)
                total += r.Map.ConsumeDroppedItemsByMaterial(
                    ItemKind.Material, preferredFamily, null, cost - total);
            if (total < cost)
            {
                total += r.Inventory.ConsumeByFamily(ItemKind.Material, fallback, cost - total);
                if (total < cost && r.Map != null)
                    total += r.Map.ConsumeDroppedItemsByMaterial(
                        ItemKind.Material, fallback, null, cost - total);
            }
            return total >= cost;
        }

        // Returns the 8-neighbour passable tile coordinate closest to (sx, sy).
        // v0.4.14 — picks the neighbour closest to the SHROOMP, not the target,
        // and prefers neighbours in the shroomp's own DF region. The old "first
        // cardinal" rule routed every shroomp to the west-side neighbour of
        // every impassable tile, which was the wrong side for diggers
        // approaching from the east / south and produced the diagonal pile-up
        // the player reported. Falls back to a target-relative pick if the
        // shroomp coordinate is unknown (sx < 0) or if no neighbour matches
        // the shroomp's region — at worst we keep the v0.4.13 behaviour.
        private static (int x, int y)? NearestAdjacentPassableTile(
            int tx, int ty, LocalMap map, int sx = -1, int sy = -1)
        {
            // v0.4.19 — claim-aware approach picker. The previous version
            // (v0.4.14) preferred the in-region neighbour closest to the
            // shroomp; ties were broken by iteration order, so multiple
            // diggers converging on adjacent Boulders would route to the
            // same approach tile. The four-bucket scan below ranks
            // candidates as:
            //   1. in-shroomp-region AND unoccupied   (best)
            //   2. in-shroomp-region (occupied)
            //   3. any region AND unoccupied
            //   4. any region (occupied)            (last-resort)
            // Distance from the shroomp still breaks ties within each
            // bucket. Shroomps heading to the same work face now naturally
            // spread across distinct approach tiles instead of all
            // pointing at the same one.
            (int x, int y)? regionUnocc = null; int bestRegionUnocc = int.MaxValue;
            (int x, int y)? regionAny   = null; int bestRegionAny   = int.MaxValue;
            (int x, int y)? anyUnocc    = null; int bestAnyUnocc    = int.MaxValue;
            (int x, int y)? anyAny      = null; int bestAnyAny      = int.MaxValue;

            ushort shroompRegion = (sx >= 0 && sy >= 0) ? map.GetRegion(sx, sy) : (ushort)0;
            bool haveShroomp = (sx >= 0 && sy >= 0);
            int curIdx = haveShroomp && _occGridWidth > 0 ? sy * _occGridWidth + sx : -1;

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if (!map.IsPassable(nx, ny)) continue;

                int d;
                if (haveShroomp)
                {
                    int ddx = nx - sx, ddy = ny - sy;
                    d = ddx * ddx + ddy * ddy;
                }
                else
                {
                    d = dx * dx + dy * dy;
                }

                bool inRegion = shroompRegion != 0 && map.GetRegion(nx, ny) == shroompRegion;
                bool unocc = _occGridWidth > 0
                    && !TileHasOtherShroomp(ny * _occGridWidth + nx, curIdx);

                if (inRegion && unocc) { if (d < bestRegionUnocc) { bestRegionUnocc = d; regionUnocc = (nx, ny); } }
                if (inRegion)          { if (d < bestRegionAny)   { bestRegionAny   = d; regionAny   = (nx, ny); } }
                if (unocc)             { if (d < bestAnyUnocc)    { bestAnyUnocc    = d; anyUnocc    = (nx, ny); } }
                if (d < bestAnyAny)    { bestAnyAny = d; anyAny = (nx, ny); }
            }
            return regionUnocc ?? regionAny ?? anyUnocc ?? anyAny;
        }

        // BFS outward from the shroomp's current tile to find the nearest
        // passable tile centre. Bounded to keep the worst case cheap on
        // dense maps (radius 8 = 64 tiles inspected). Returns null only on
        // pathologically enclosed maps, in which case the caller leaves
        // SimPos where it is — the shroomp will be effectively frozen, but
        // that's strictly better than wandering through walls.
        private static Vector2? NearestPassableTileCentre(Vector2 from, LocalMap map)
        {
            int cx = (int)(from.X / LocalMap.TileSize);
            int cy = (int)(from.Y / LocalMap.TileSize);
            const int radius = 8;
            for (int r = 1; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r) continue;
                    int tx = cx + dx, ty = cy + dy;
                    if (map.IsPassable(tx, ty)) return TileToPixel((tx, ty));
                }
            }
            return null;
        }

        // ── Task selection ──────────────────────────────────────────────────
        // Roadmap §3.3 / §3.4 / §3.5. Walks tiers in priority order and returns
        // the highest-priority valid task.
        private static BehaviorTask SelectTask(Shroomp s, LocalMap? map,
            ColonyResources resources, Random rng, IReadOnlyList<Shroomp> shroomps,
            int hourOfDay = 12)
        {
            // ── Tier 1: critical needs ──────────────────────────────────────
            float eatThreshold   = HasTrait(s, "Glutton")   ? 70f : 50f;
            float sleepThreshold = HasTrait(s, "Sleepyhead")? 60f : 40f;
            float safetyBonus    = HasTrait(s, "Worrywart") ? 20f : 0f;
            float socialThres    = HasPersonality(s, "Introvert") ? 10f : 20f;

            // v0.5.61 — nighttime sleep gating. Shroomps sleep through the
            // night even at moderate Rest levels (matches RimWorld's
            // schedule and the actual circadian biology of "go to bed
            // when it's bedtime"). Night Owl trait flips the sleep window
            // from night to day.
            //   Default sleep window: 22:00 – 06:00 (8 hours)
            //   Night Owl flip:       10:00 – 18:00 (8 hours)
            // Inside the window, Rest below 80 triggers sleep at high
            // priority. Outside the window the existing thresholds apply
            // (40-60 depending on Sleepyhead).
            bool nightOwl = HasPersonality(s, "Night Owl");
            bool inSleepWindow = nightOwl
                ? (hourOfDay >= 10 && hourOfDay < 18)
                : (hourOfDay >= 22 || hourOfDay < 6);
            if (inSleepWindow && s.Rest < 80f) return MakeSleep(s, priority: 75f, map: map);

            if (s.Nutrition < 20f) return MakeEat(s, resources, map: map);
            if (s.Rest      < 15f) return MakeSleep(s, map: map);
            if (s.Safety    < 20f) return MakeSeekSafety(s, safetyBonus);
            if (s.Social    < socialThres) return MakeSocialize(s, MoodAdjust(s));
            if (s.MagicResonance < 15f) return MakeAttune(s, MoodAdjust(s));
            // v0.5.61 — Joy critical threshold. Below 20, shroomps prioritize
            // recreation over role work (matches RimWorld where low-Joy
            // pawns get the "I need joy" alert and chase recreation
            // activities). The Tier-3 idle picker then weights toward the
            // shroomp's preferred activity (Meditate / Loiter / etc.) — the
            // critical clause just elevates the idle tier above role work.
            if (s.Joy < 20f) return SelectIdleActivity(s, map, rng, shroomps);

            if (s.Nutrition < eatThreshold)   return MakeEat(s, resources, priority: 70f, map: map);
            if (s.Rest      < sleepThreshold) return MakeSleep(s, priority: 65f, map: map);

            // ── Tier 2: role tasks ──────────────────────────────────────────
            // v0.3.21 — player-issued designations take precedence over the
            // autonomous role behaviour. Designated targets ignore the colony
            // food/material thresholds entirely: if the player drew a Gather
            // box, the Forager should go pick from it whether or not pantry
            // is full. Anyone else (any role) will pick up designations as a
            // fallback when no role-matching shroomp is available — this is how
            // "nearest available shroomp with the highest job priority carries
            // out the order" emerges from each shroomp independently scanning
            // each tick.

            if (map != null)
            {
                bool isForager = s.Role == "Forager";
                bool isCrafter = s.Role == "Crafter";

                // v0.3.43 — preference-based priority bumps. Liked
                // activities resist interruption (higher priority); disliked
                // activities are picked but more easily overridden by needs
                // / other work. Range ±10 keeps preferences as colour, not
                // a hard veto on role behaviour — a Forager who hates
                // Foraging still does it, just less enthusiastically.
                float pForage = PreferenceTilt(s, "Foraging");
                float pDig    = PreferenceTilt(s, "Excavating");
                float pChop   = PreferenceTilt(s, "ChoppingWood");
                float pCut    = PreferenceTilt(s, "CuttingPlants");

                // v0.3.47 — Jobs-tab work priority gate. byte 0 means the
                // shroomp is forbidden from this work (player set them to
                // off in the Jobs tab); 1-4 means "consider, with the
                // shown priority shifting the in-engine priority value".
                // v0.4.36 — widened the tilts from ±4 / ±12 to ±10 / ±25 so
                // the Jobs-panel pri makes a visibly bigger gameplay
                // difference. Original spread was small relative to the
                // 40-60 base priority of most task categories, so a
                // Priority-1 Forager only barely outranked a Priority-4
                // one. New table:
                //   1 → +25  (high — strongly wins over default)
                //   2 → +10
                //   3 → -10
                //   4 → -25  (last-resort, only when nothing else available)
                // v0.5.40 — RimWorld-parity gate. Pre-v0.5.40 this returned
                // true (allow) when the shroomp had NO entry for `category`,
                // so any role whose `ByRole` defaults didn't list a column
                // (e.g. Forager + "Cook", Scholar + "Mine") happily picked
                // up that work anyway. Sam: "shroomps seem to do jobs they
                // are not assigned to." RimWorld defaults missing entries
                // to OFF; we now match by returning false when the key is
                // absent. Every role's ByRole dict was audited at this
                // version — Patient / BedRest / Haul / Clean are present
                // for every role, so the basics keep working; only the
                // role-inappropriate work (Forager cooking, Mage mining,
                // etc.) gets correctly gated out.
                bool jobOk(string category)
                {
                    if (s.WorkPriorities == null) return false;
                    return s.WorkPriorities.TryGetValue(category, out var v) && v != 0;
                }
                float jobTilt(string category)
                {
                    if (s.WorkPriorities == null) return 0f;
                    if (!s.WorkPriorities.TryGetValue(category, out var v)) return 0f;
                    return v switch
                    {
                        1 => +25f, 2 => +10f, 3 => -10f, 4 => -25f, _ => 0f,
                    };
                }

                // v0.4.57 — gate every designation pick on the post-
                // abandon cooldown. A shroomp that gave up on an excavate /
                // gather / chop / cut in the last ~2 sec falls through
                // every designation branch and ends up at the idle tier
                // below (Wander / Loiter / etc.), giving the work-face
                // cluster time to dissolve before they try again.
                bool designationsOk = s.DesignationCooldownTicks <= 0;

                // Forager priority — designated gather first.
                if (designationsOk && isForager && jobOk("Forage"))
                {
                    var g = FindDesignatedGather(s, map);
                    if (g.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.GatherFood, g.Value,
                            60f + pForage + jobTilt("Forage"));
                }

                // Crafter priority — designated excavation first.
                if (designationsOk && isCrafter && jobOk("Mine"))
                {
                    var dig = FindDesignatedExcavation(s, map);
                    if (dig.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.GatherMaterial, dig.Value,
                            60f + pDig + jobTilt("Mine"));
                }

                if (designationsOk && !isForager && jobOk("Forage"))
                {
                    var g = FindDesignatedGather(s, map);
                    if (g.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.GatherFood, g.Value,
                            40f + pForage + jobTilt("Forage"));
                }
                if (designationsOk && !isCrafter && jobOk("Mine"))
                {
                    var dig = FindDesignatedExcavation(s, map);
                    if (dig.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.GatherMaterial, dig.Value,
                            40f + pDig + jobTilt("Mine"));
                }

                if (designationsOk && jobOk("Chop"))
                {
                    var chop = FindDesignatedChopWood(s, map);
                    if (chop.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.ChopWood,
                            chop.Value, (isCrafter ? 55f : 40f) + pChop + jobTilt("Chop"));
                }
                if (designationsOk && jobOk("PlantCut"))
                {
                    var cut = FindDesignatedCut(s, map);
                    if (cut.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.CutVegetation,
                            cut.Value, 40f + pCut + jobTilt("PlantCut"));
                }

                // v0.4.2 — Haul task. After Gather / Excavate / Chop /
                // Cut drop items on the world, any shroomp with a Haul
                // priority > 0 will pick them up and deliver to the
                // colony pile. Tier 2 priority intentionally lower
                // than primary work so a Forager finishes their
                // designated gather before pivoting to hauling, but
                // higher than idle so dropped items don't pile up
                // forever.
                if (jobOk("Haul"))
                {
                    var haul = HaulSystem.SelectHaulTarget(s, map, resources);
                    if (haul.HasValue)
                    {
                        var h = haul.Value;
                        return new BehaviorTask(h.Type, h.Target,
                            h.Priority + jobTilt("Haul"),
                            interruptible: h.Interruptible,
                            tileX: h.TargetTileX, tileY: h.TargetTileY,
                            targetId: h.TargetId);
                    }
                }

                // v0.5.19 (Phase 5B) — Build task. Crafters get a strong
                // priority boost; other roles fill in as fallback.
                // v0.5.60 — RimWorld-parity split: BuildHaul (Haul priority,
                // any role) delivers materials; Build (Construct priority,
                // Crafter preferred) does the framing. Mirrors RimWorld's
                // WorkGiver_ConstructDeliverResources +
                // WorkGiver_ConstructFinishFrames split. Multiple haulers
                // can deliver to one blueprint while a Crafter waits to
                // finish it — colony parallelism that pre-v0.5.60 didn't
                // exist (one Crafter solo'd every build).
                bool canHaulForBuild   = designationsOk && map.HasAnyBlueprint() && jobOk("Haul");
                bool canFrameForBuild  = designationsOk && map.HasAnyBlueprint() && jobOk("Construct");
                if (canHaulForBuild || canFrameForBuild)
                {
                    var bp = FindDesignatedBuild(s, map);
                    if (bp.HasValue && IsTileInAllowedArea(s, bp.Value, map))   // v0.5.20 N6
                    {
                        // v0.5.31 — blueprint can be placed on impassable
                        // terrain (Boulder / DeadLog / LivingWood / Skeleton)
                        // or on non-depleted vegetation. If so, the
                        // constructor handles the clearing themselves before
                        // building, **bypassing the normal Mine / Cut
                        // priority gates** (the work is a sub-step of
                        // construction, not standalone mining/cutting).
                        // RimWorld pattern: "deliver materials, cut plants,
                        // mine boulder" are all baked into the construction
                        // job. We approximate that by redirecting the task
                        // type for one tick — the shroomp will run the
                        // existing Excavate / Cut Apply path against the
                        // blueprint tile, then on the next SelectTask the
                        // now-cleared tile will fall into the normal Build
                        // branch.
                        // Clearing prep (Construct-priority work — it's a
                        // sub-step of construction). If the shroomp doesn't
                        // have Construct, skip clearing; they may still be
                        // able to haul (BuildHaul below).
                        if (canFrameForBuild
                            && !SimulationManager.IsBlueprintBuildReady(map, bp.Value.x, bp.Value.y))
                        {
                            var clearKind = ResolveBlueprintClearTask(map, bp.Value.x, bp.Value.y);
                            if (clearKind.HasValue)
                                return ClaimAndMakeDesignationTask(s, map, clearKind.Value, bp.Value,
                                    (isCrafter ? 55f : 38f) + jobTilt("Construct"));
                            // No clearing makes sense → fall through; treat
                            // as standard build (rare: only if the obstruction
                            // is something we haven't mapped, e.g. Water,
                            // which CanPlaceBlueprint already rejected).
                        }
                        // v0.5.57 — RimWorld-parity haul-to-site routing.
                        // Inspect the blueprint's material requirement and the
                        // shroomp's inventory. Three states:
                        //
                        //   A. Blueprint is fully supplied (MaterialsDelivered
                        //      >= cost) → straight Build task at the blueprint
                        //      (framing only).
                        //   B. Blueprint needs more materials AND the shroomp
                        //      is already carrying the right material → Build
                        //      task at the blueprint (deposit + frame).
                        //   C. Blueprint needs more materials AND the shroomp
                        //      isn't carrying any → find the nearest source
                        //      stack on the map, route the Build task target
                        //      to that source tile, stash the blueprint
                        //      coordinates on s.BuildSiteTileX/Y. The
                        //      ApplyTaskEffect handler picks up the material
                        //      on arrival at the source, then re-routes back
                        //      to the blueprint.
                        //
                        // Pre-v0.5.57 every Build task went straight to the
                        // blueprint and consumed materials in-place from the
                        // colony pool (v0.5.34) + map drops (v0.5.55). The
                        // shroomp never physically walked to fetch wood; this
                        // ships that missing leg for RimWorld parity.
                        var bpSlot = map.GetStructure(bp.Value.x, bp.Value.y);
                        byte buildCost = Sporeholm.World.StructureSlot.BuildMaterialCost(bpSlot.Type);
                        int remaining = buildCost - bpSlot.MaterialsDelivered;
                        bool carriesMatching = ShroompCarriesMatchingBuildMaterial(s, bpSlot.Material);

                        // BRANCH 1 — Framing. Blueprint fully supplied AND
                        // shroomp has Construct priority. Crafter goes to the
                        // blueprint and ticks BuildProgress.
                        if (remaining <= 0 && canFrameForBuild)
                        {
                            s.BuildSiteTileX = -1;
                            s.BuildSiteTileY = -1;
                            // Use ReservationManager (v0.5.60) for the framing
                            // claim. Single-claim layer — only one Crafter
                            // finishes a given frame at a time.
                            return ClaimAndMakeDesignationTask(s, map, TaskType.Build, bp.Value,
                                (isCrafter ? 55f : 38f) + jobTilt("Construct"));
                        }

                        // BRANCH 2 — Haul materials. Blueprint needs more
                        // materials AND shroomp has Haul priority. ANY role
                        // can deliver (matches RimWorld's WorkGiver_Construct
                        // DeliverResources gated on Hauling work type, not
                        // Construction).
                        //
                        // v0.5.84t — single-hauler-per-blueprint reservation.
                        // Pre-v0.5.84t we let multiple haulers commit to the
                        // same blueprint, which produced over-supply (N
                        // haulers each fetched a carry-load for a 1-cost
                        // Floor, dumping forbidden surplus on built tiles)
                        // and a conga line (N-1 haulers arrived to find
                        // `needed<=0` and abandoned with material stuck in
                        // their inventory, then re-acquired the same task
                        // each tick until the cycle ate sim ticks). Now the
                        // first hauler to ReserveTile(LayerBuildHaul) gets
                        // exclusive delivery rights to the blueprint; other
                        // haulers FindDesignatedBuildForHaul to find the
                        // next-nearest unreserved blueprint instead.
                        if (remaining > 0 && canHaulForBuild)
                        {
                            // Pick a haul target. If the framing-pick is
                            // already haul-reserved by another shroomp,
                            // search for the next-nearest blueprint not
                            // haul-reserved.
                            (int x, int y) haulBp = bp.Value;
                            var bpSlotForHaul = bpSlot;
                            var rezMgr = Sporeholm.Simulation.ReservationManager.Active;
                            if (rezMgr != null && rezMgr.IsTileReservedByOther(
                                    haulBp.x, haulBp.y,
                                    Sporeholm.Simulation.ReservationManager.LayerBuildHaul, s.Id))
                            {
                                var altBp = FindDesignatedBuildForHaul(s, map);
                                if (!altBp.HasValue) goto skipBuildHaul;
                                haulBp = altBp.Value;
                                bpSlotForHaul = map.GetStructure(haulBp.x, haulBp.y);
                                byte altCost = StructureSlot.BuildMaterialCost(bpSlotForHaul.Type);
                                int altRemaining = altCost - bpSlotForHaul.MaterialsDelivered;
                                if (altRemaining <= 0) goto skipBuildHaul;
                            }
                            // Claim the haul reservation atomically. If the
                            // reserve loses the race (another shroomp grabbed
                            // it in the same tick), skip BuildHaul this tick;
                            // SelectTask will re-evaluate next tick.
                            if (rezMgr != null && !rezMgr.ReserveTile(
                                    haulBp.x, haulBp.y,
                                    Sporeholm.Simulation.ReservationManager.LayerBuildHaul, s.Id))
                            {
                                goto skipBuildHaul;
                            }

                            // 2a. Already carrying matching material → go
                            // straight to the blueprint to deposit.
                            if (ShroompCarriesMatchingBuildMaterial(s, bpSlotForHaul.Material))
                            {
                                s.BuildSiteTileX = -1;
                                s.BuildSiteTileY = -1;
                                float pri = (isCrafter ? 50f : 45f) + jobTilt("Haul");
                                return new BehaviorTask(TaskType.BuildHaul,
                                    new Vector2(haulBp.x * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                                haulBp.y * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                                    pri,
                                    tileX: haulBp.x, tileY: haulBp.y);
                            }
                            // 2b. Not carrying → find nearest matching stack
                            // on the map, route the BuildHaul task to that
                            // source tile, stash blueprint coords in
                            // BuildSiteTileX/Y for the return leg.
                            string family = Sporeholm.World.StructureMatMeta.ConsumeFamily(bpSlotForHaul.Material);
                            string? subType = Sporeholm.World.StructureMatMeta.ConsumeSubType(bpSlotForHaul.Material);
                            // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
                            string? itemSubType = Sporeholm.World.StructureMatMeta.ConsumeItemSubType(bpSlotForHaul.Material);
                            var source = map.FindNearestMaterial(
                                (int)(s.SimPos.X / LocalMap.TileSize),
                                (int)(s.SimPos.Y / LocalMap.TileSize),
                                Items.ItemKind.Material, family, subType, itemSubType);
                            if (source.HasValue)
                            {
                                s.BuildSiteTileX = haulBp.x;
                                s.BuildSiteTileY = haulBp.y;
                                float pri = (isCrafter ? 50f : 45f) + jobTilt("Haul");
                                return new BehaviorTask(TaskType.BuildHaul,
                                    new Vector2(source.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                                source.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                                    pri,
                                    tileX: source.Value.X, tileY: source.Value.Y);
                            }
                            // No source material on the map — release the
                            // reservation we just claimed (we're not actually
                            // going to deliver) and fall through.
                            rezMgr?.ReleaseTile(haulBp.x, haulBp.y,
                                Sporeholm.Simulation.ReservationManager.LayerBuildHaul, s.Id);
                            skipBuildHaul:;
                        }
                    }
                }

                // v0.5.22 (Phase 5E) — Cook task. Crafters get the priority
                // boost (matches Build), other roles cook as fallback when
                // no Crafter is around. Fires when a Workbench exists +
                // raw Food is in colony inventory. CookSystem.SelectCookTarget
                // returns null when conditions aren't met (cheap O(map)
                // workbench scan; can be optimised with a workbench HashSet
                // in v0.5.23+ if needed).
                if (jobOk("Cook"))
                {
                    var cookT = CookSystem.SelectCookTarget(s, map, resources);
                    if (cookT.HasValue)
                    {
                        var c = cookT.Value;
                        return new BehaviorTask(c.Type, c.Target,
                            (isCrafter ? 52f : 35f) + jobTilt("Cook"),
                            interruptible: c.Interruptible,
                            tileX: c.TargetTileX, tileY: c.TargetTileY);
                    }
                }

                // v0.5.84s — Phase 5.5 Crafting Bills. Try DoBill BEFORE
                // auto-cook so player-queued recipes take priority over
                // the auto-cook fallback. BillSystem.SelectTarget returns
                // null if no workbench has an active satisfiable bill;
                // the existing Cook fallback above keeps PreparedMeal
                // production flowing for colonies that never queue a bill.
                if (jobOk("Craft"))
                {
                    var billT = BillSystem.SelectTarget(s, map, resources);
                    if (billT.HasValue)
                    {
                        var c = billT.Value;
                        return new BehaviorTask(c.Type, c.Target,
                            (isCrafter ? 58f : 38f) + jobTilt("Craft"),
                            interruptible: c.Interruptible,
                            tileX: c.TargetTileX, tileY: c.TargetTileY,
                            targetId: c.TargetId);
                    }
                }

                // Forager autonomous fallback — only when pantry is low and no
                // explicit designations remain.
                if (isForager && resources.Food < 30f)
                {
                    var target = FindNearestVegetation(s.SimPos, map);
                    if (target.HasValue)
                    {
                        var px = TileToPixel(target.Value);
                        return new BehaviorTask(TaskType.GatherFood, px, 55f,
                            tileX: target.Value.x, tileY: target.Value.y);
                    }
                }
            }

            // ── Tier 3: idle activity (v0.3.43) ─────────────────────────────
            // Personality- and preference-weighted picker. Six variants:
            // Wander / Loiter / Observe / Converse / Meditate / VisitFavorite.
            // Each has a different movement footprint and arrival-linger
            // duration — collectively they replace the single Wander loop
            // that produced the jittering-in-place feel.
            return SelectIdleActivity(s, map, rng, shroomps);
        }

        // v0.3.43 — Tier-3 idle picker. Builds a weight table per activity,
        // weighted by personality + preferences, then samples one. The
        // picker is the load-bearing part of "shroomps feel alive": every
        // tick a shroomp without work selects from a varied pool instead of
        // always wandering, and the picked activity carries its own
        // ArrivalLinger so the shroomp actually stays where they ended up.
        private static BehaviorTask SelectIdleActivity(Shroomp s, LocalMap? map, Random rng,
            IReadOnlyList<Shroomp> shroomps)
        {
            // Base weights.
            int wWander    = 18;
            int wLoiter    = 16;
            int wObserve   = 10;
            int wConverse  = 10;
            int wMeditate  = 4;
            int wVisitFav  = 5;

            // Personality nudges.
            if (HasPersonality(s, "Introvert"))     { wConverse  =  3; wObserve   += 6; }
            if (HasPersonality(s, "Gossip"))        { wConverse += 14; }
            if (HasPersonality(s, "Daydreamer"))    { wObserve  += 10; wLoiter   += 6; }
            if (HasPersonality(s, "Brawny"))        { wWander   +=  8; wLoiter   -= 4; }
            if (HasPersonality(s, "Sleepyhead"))    { wLoiter   +=  8; wWander   -= 4; }
            if (HasPersonality(s, "Thrill-Seeker")) { wWander   += 10; }
            if (HasPersonality(s, "Mushroom Whisperer")) { wMeditate += 6; }

            // Role nudges — Mages and Scholars meditate more often; Foragers wander.
            if (s.Role == "Sage")    wMeditate += 12;
            if (s.Role == "Scholar") wObserve  +=  6;
            if (s.Role == "Forager") wWander   +=  6;

            // Preference nudges.
            var prefs = s.Preferences;
            if (prefs != null)
            {
                if (prefs.LikesActivity("Socializing"))  wConverse += 10;
                if (prefs.DislikesActivity("Socializing")) wConverse = Math.Max(0, wConverse - 8);
                if (prefs.LikesActivity("Observing"))    wObserve  += 8;
                if (prefs.LikesActivity("Meditating"))   wMeditate += 8;
                if (prefs.LikesActivity("Wandering"))    wWander   += 6;
            }

            // v0.5.59 — RimWorld-parity need-aware idle weighting. A pawn
            // whose Social need is already at/near 100 shouldn't keep
            // re-picking Converse; the marginal Social gain is zero, and the
            // pawn looks idle-spammy if they chain back-to-back chats while
            // others do productive work. RimWorld's JoyGiver_SocialRelax
            // returns null when the pawn's Joy is full; equivalent fix here
            // is to scale the idle weight by remaining need.
            wConverse = (int)System.MathF.Round(wConverse * System.MathF.Max(0f, 100f - s.Social) / 100f);
            wMeditate = (int)System.MathF.Round(wMeditate * System.MathF.Max(0f, 100f - s.MagicResonance) / 100f);

            // v0.5.60 — JoyTolerance scaling. Bored shroomps naturally cycle.
            // RimWorld pattern: a pawn that just played 3 games of billiards
            // gets low weight for a 4th — JoyKindTolerance scales the
            // giver weight. Apply per-TaskType so each activity tapers
            // independently.
            wWander    = (int)System.MathF.Round(wWander    * JoyToleranceMul(s, TaskType.Wander));
            wLoiter    = (int)System.MathF.Round(wLoiter    * JoyToleranceMul(s, TaskType.Loiter));
            wObserve   = (int)System.MathF.Round(wObserve   * JoyToleranceMul(s, TaskType.Observe));
            wConverse  = (int)System.MathF.Round(wConverse  * JoyToleranceMul(s, TaskType.Converse));
            wMeditate  = (int)System.MathF.Round(wMeditate  * JoyToleranceMul(s, TaskType.Meditate));
            wVisitFav  = (int)System.MathF.Round(wVisitFav  * JoyToleranceMul(s, TaskType.VisitFavorite));

            // Clamp negatives.
            if (wWander   < 0) wWander   = 0;
            if (wLoiter   < 0) wLoiter   = 0;
            if (wObserve  < 0) wObserve  = 0;
            if (wConverse < 0) wConverse = 0;
            if (wMeditate < 0) wMeditate = 0;
            if (wVisitFav < 0) wVisitFav = 0;

            int total = wWander + wLoiter + wObserve + wConverse + wMeditate + wVisitFav;
            if (total <= 0) return NewWanderTask(s.SimPos, map, rng);

            int roll = rng.Next(total);
            // v0.5.5 — Wander chosen as the *idle pick* uses the multi-hop
            // overload (2-4 destinations). Forced-wander sites (failure
            // recovery, abandoned-task displacement) keep the single-hop
            // overload — they're "go elsewhere then re-evaluate", not
            // "commit to a real walk."
            if ((roll -= wWander)   < 0) return NewWanderTask  (s, map, rng);
            if ((roll -= wLoiter)   < 0) return NewLoiterTask  (s.SimPos, map, rng);
            if ((roll -= wObserve)  < 0) return NewObserveTask (s.SimPos, map, rng);
            if ((roll -= wConverse) < 0) return NewConverseTask(s, map, rng, shroomps);
            if ((roll -= wMeditate) < 0) return NewMeditateTask(s.SimPos, map, rng);
            return NewVisitFavoriteTask(s, map, rng);
        }

        private static bool IsIdleType(TaskType t) =>
               t == TaskType.Wander
            || t == TaskType.Loiter
            || t == TaskType.Observe
            || t == TaskType.Converse
            || t == TaskType.Meditate
            || t == TaskType.VisitFavorite;

        // v0.5.60 — allocation-free JoyTolerance decay. Uses a small static
        // buffer instead of allocating a new key list per shroomp per tick.
        // Cap at 8 — there are only 6 idle TaskTypes so the dict never
        // grows that large in practice.
        [System.ThreadStatic] private static TaskType[]? _tolKeyBuf;
        private static void DecayJoyTolerance(Shroomp s, int tickInterval)
        {
            int n = s.JoyTolerance.Count;
            if (_tolKeyBuf == null || _tolKeyBuf.Length < n)
                _tolKeyBuf = new TaskType[System.Math.Max(8, n)];
            int i = 0;
            foreach (var k in s.JoyTolerance.Keys) _tolKeyBuf[i++] = k;
            float decay = 0.0001f * tickInterval;   // ~10 sim min full decay at 1× LOD
            for (int j = 0; j < n; j++)
            {
                float v = s.JoyTolerance[_tolKeyBuf[j]] - decay;
                if (v <= 0f) s.JoyTolerance.Remove(_tolKeyBuf[j]);
                else s.JoyTolerance[_tolKeyBuf[j]] = v;
            }
        }

        // v0.5.60 — bump tolerance for the active idle activity. Called
        // from ApplyTaskEffect's idle cases. Saturates near 1.0 quickly
        // (~3-5 sim sec of continuous activity) so chained-recreation
        // cycles taper joy gain fast.
        private static void BumpJoyTolerance(Shroomp s, TaskType t, float amount = 0.003f)
        {
            s.JoyTolerance.TryGetValue(t, out float current);
            float next = current + amount;
            if (next > 1f) next = 1f;
            s.JoyTolerance[t] = next;
        }

        // v0.5.60 — return the joy-gain multiplier for this shroomp and task,
        // clamped 0-1. Boredom mechanic: fully-tolerant shroomps get zero
        // joy from the over-done activity.
        private static float JoyToleranceMul(Shroomp s, TaskType t)
        {
            if (!s.JoyTolerance.TryGetValue(t, out float v)) return 1f;
            return System.MathF.Max(0f, 1f - v);
        }

        // v0.5.60 — swap a BuildHaul task's target from source to blueprint
        // (called after pickup completes). Clears BuildSiteTileX/Y so the
        // next tick's routingFromSource check goes false, kicks off an
        // explicit A* so the shroomp walks back without StuckRePathTicks
        // delay, resets stuck-counter state.
        private static void RetargetBuildHaulToBlueprint(Shroomp s, BehaviorTask t, LocalMap map, int bpTx, int bpTy)
        {
            s.BuildSiteTileX = -1;
            s.BuildSiteTileY = -1;
            s.CurrentTask = new BehaviorTask(
                TaskType.BuildHaul,
                new Vector2(bpTx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                            bpTy * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                t.Priority,
                interruptible: t.Interruptible,
                tileX: bpTx, tileY: bpTy);
            s.PathWaypoints.Clear();
            Pathfinder.FindPath(map, s.SimPos, (bpTx, bpTy),
                s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
            s.StuckTicks = 0;
            s.RePathTried = false;
            s.MinSqrDistanceToWalkTarget = float.MaxValue;
            s.NoProgressTicks = 0;
            s.LastWalkTargetTileX = -1;
            s.LastWalkTargetTileY = -1;
            s.ProgressRePathTried = false;
        }

        // v0.5.1 — call from MoveOneTick's arrival branches. First arrival
        // for an idle task flips IdleArrived=true and resets the linger
        // counter to ArrivalLinger so the post-arrival dwell starts from
        // full. Subsequent arrival ticks (shroomp still at target) no-op
        // because IdleArrived is already true. Non-idle tasks are
        // unaffected — their CurrentTask gets cleared by ApplyTaskEffect
        // anyway, so the arrival flag stays unused.
        private static void MarkIdleArrivalIfNeeded(Shroomp s)
        {
            if (s.IdleArrived) return;
            if (s.CurrentTask is not { } ct) return;
            if (!IsIdleType(ct.Type)) return;
            s.IdleArrived = true;
            s.IdleLingerTicks = ct.ArrivalLinger;
        }

        // v0.5.5 — two-way Converse lock. Called by ApplyTaskEffect's
        // Converse case every tick the initiator (s) is at their target
        // and the partner is found alive. Idempotent — bails if the
        // partner is already locked into a Converse pointing back at us
        // OR if the partner is doing something we shouldn't interrupt.
        //
        // Lockable conditions for partner:
        //   • Within ConverseLockRangePx (~3 tiles). If they wandered
        //     out of arm's reach, the chat doesn't catch them.
        //   • Not in life-threatening need (starving / suffocating /
        //     bleeding out — those tasks must finish first).
        //   • Current task is None, an idle activity, OR already a
        //     Converse pointing back at us. We never interrupt
        //     player orders, designation work, hauls, or chained
        //     orders — RimWorld pattern, "social interactions can't
        //     pull a pawn off a job."
        //   • Not already locked with a third party (already
        //     Converse-targeting someone else).
        //
        // On lock: partner's CurrentTask becomes a Converse pointing
        // back at s, IdleArrived=true (they've "arrived" at the chat),
        // IdleLingerTicks set to the same window so they expire
        // together. PathWaypoints cleared because they're at the chat
        // location now. WorkSearchCooldownTicks bumped so the
        // workAvailable gate doesn't pull them out mid-chat.
        private const float ConverseLockRangePx = 3f * LocalMap.TileSize;
        private static void TryLockConversePartner(Shroomp s, Shroomp partner, int lingerTicks)
        {
            if (!partner.IsAlive) return;

            float dx = partner.SimPos.X - s.SimPos.X;
            float dy = partner.SimPos.Y - s.SimPos.Y;
            if (dx * dx + dy * dy > ConverseLockRangePx * ConverseLockRangePx) return;

            if (IsLifeThreatening(partner)) return;

            // Already locked back at us → idempotent no-op (don't
            // refresh the linger every tick, that would be infinite chat).
            if (partner.CurrentTask is { } pt
                && pt.Type == TaskType.Converse
                && pt.TargetId == s.Name)
                return;

            // Locked with someone else → can't poach. They'll finish
            // their existing chat and become available afterward.
            if (partner.CurrentTask is { } pt2
                && pt2.Type == TaskType.Converse
                && pt2.TargetId != null
                && pt2.TargetId != s.Name)
                return;

            // Don't interrupt non-idle tasks (work / haul / player order /
            // critical need). RimWorld-equivalent: social interactions
            // are weakest priority, never preempt productive work.
            if (partner.CurrentTask is { } pt3
                && pt3.Type != TaskType.None
                && !IsIdleType(pt3.Type))
                return;

            // Lock partner into a reciprocal Converse pointing at s.
            // Same linger window so they expire together (one second of
            // sim time difference at most, depending on tick alignment).
            partner.CurrentTask = new BehaviorTask(
                TaskType.Converse, s.SimPos, 6f,
                interruptible: true,
                arrivalLinger: lingerTicks,
                targetId: s.Name);
            partner.SimTarget = s.SimPos;
            partner.PathWaypoints.Clear();
            partner.IdleArrived = true;
            partner.IdleLingerTicks = lingerTicks;
            partner.StuckTicks = 0;
            partner.RePathTried = false;
            // Suppress the workAvailable re-eval gate for the duration
            // of the chat so the partner doesn't get yanked out by a
            // designation appearing somewhere else on the map.
            partner.WorkSearchCooldownTicks = lingerTicks + 60;
            // Wander chain (if any) is dropped — the chat takes
            // precedence. The partner can choose Wander again next idle.
            partner.WanderHopsRemaining = 0;
        }

        // Roadmap §3.4: distressed-or-worse shroomps gain +10 priority on
        // comfort tasks (Socialize / Attune).
        private static float MoodAdjust(Shroomp s) =>
            s.MoodState <= MoodState.Distressed ? 10f : 0f;

        // If a task's current priority is below the would-be critical-need
        // priority for this shroomp right now, allow override.
        private static bool CriticalNeedsOverride(Shroomp s, float currentPriority)
        {
            if (s.Nutrition < 20f && currentPriority < 100f) return true;
            if (s.Rest      < 15f && currentPriority <  95f) return true;
            if (s.Safety    < 20f && currentPriority <  85f) return true;
            return false;
        }

        // v0.4.61 (E6 from rimport.md) — life-threatening needs that MUST
        // override even non-interruptible tasks (e.g. an in-flight player
        // PlayerOrder/Haul). A starving shroomp walking on a "Move here"
        // order should still drop the order to eat — otherwise they
        // starve to death obeying. RimWorld parallel: `JobGiver_Work`
        // emergency tier always overrides drafted-state movement when
        // the pawn is below health-critical thresholds. Hard floor at
        // 5f so the bypass is reserved for genuine emergencies — a
        // shroomp at Nutrition=18 still respects the player order.
        private static bool IsLifeThreatening(Shroomp s)
        {
            return s.Nutrition < 5f || s.Rest < 5f;
        }

        // ── Task constructors ───────────────────────────────────────────────
        // v0.5.68 — Eat now routes through three checks in priority order:
        //   1. Colony Inventory has food → walk to nearest Table, eat there
        //      (RimWorld preferred path — meals + tasty produce + table mood
        //      bonus). Falls through to (3) if no Table is built.
        //   2. Map drops have food → walk to nearest food tile, eat at the
        //      drop (RimWorld JobGiver_GetFood: walk to storage cell).
        //   3. Nothing edible found → fall back to eating in place (will
        //      fail in ApplyTaskEffect, which then clears the task so other
        //      behaviours can run instead of looping on a dead Eat).
        // When starving (Nutrition < 25) the inventory + map scans both
        // widen to include Spoiled food and Corpses (RimWorld FoodUtility
        // urgent-food fallback). The mood debt lands at consume time via
        // AteSpoiled / AteCorpse thoughts.
        private static BehaviorTask MakeEat(Shroomp s, ColonyResources r, float priority = 100f, LocalMap? map = null)
        {
            bool starving = s.Nutrition < 25f;
            bool inventoryHasFood = r.Inventory.FindBestFood(s, allowSpoiled: starving) != null;

            if (map != null)
            {
                int tx = (int)(s.SimPos.X / LocalMap.TileSize);
                int ty = (int)(s.SimPos.Y / LocalMap.TileSize);

                // Path 1: inventory food + table → route to table.
                if (inventoryHasFood)
                {
                    var table = map.FindNearestTable(tx, ty);
                    if (table.HasValue)
                    {
                        var pos = new Vector2(
                            table.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                            table.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                        return new BehaviorTask(TaskType.Eat, pos, priority,
                            tileX: table.Value.X, tileY: table.Value.Y,
                            interruptible: priority < 95f);
                    }
                    // No table — eat in place from inventory.
                    return new BehaviorTask(TaskType.Eat, s.SimPos, priority,
                        interruptible: priority < 95f);
                }

                // Path 2: map drops (foraged/hauled food, optionally corpses
                // when starving). Routes the shroomp directly to the food
                // tile and ApplyTaskEffect Eat consumes from the map.
                var foodTile = map.FindNearestFoodTile(tx, ty,
                    allowSpoiled: starving, allowCorpse: starving);
                if (foodTile.HasValue)
                {
                    var pos = new Vector2(
                        foodTile.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        foodTile.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    return new BehaviorTask(TaskType.Eat, pos, priority,
                        tileX: foodTile.Value.X, tileY: foodTile.Value.Y,
                        interruptible: priority < 95f);
                }
            }

            // Path 3: nothing edible anywhere. Eat in place — ApplyTaskEffect
            // will fail to consume and clear the task. SelectTask re-evaluates
            // next tick so the shroomp can chase other needs / work instead of
            // standing still over a dead Eat.
            return new BehaviorTask(TaskType.Eat, s.SimPos, priority,
                interruptible: priority < 95f);
        }
        // v0.5.35 — Sleep task now routes to the nearest built Bed if one
        // exists. Bed tile becomes Target; shroomp paths there before
        // sleeping. ApplyTaskEffect Sleep detects "at a bed" via tile
        // proximity and applies the 1.0× RestEffectiveness + WellRested
        // thought. No bed → fall back to floor-sleep at current SimPos
        // (0.8× effectiveness + SleptOnGround thought).
        private static BehaviorTask MakeSleep(Shroomp s, float priority = 95f, LocalMap? map = null)
        {
            if (map != null)
            {
                int tx = (int)(s.SimPos.X / LocalMap.TileSize);
                int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
                var bed = map.FindNearestBed(tx, ty);
                if (bed.HasValue)
                {
                    var pos = new Vector2(
                        bed.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        bed.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    return new BehaviorTask(TaskType.Sleep, pos, priority,
                        tileX: bed.Value.X, tileY: bed.Value.Y,
                        interruptible: priority < 90f);
                }
            }
            return new BehaviorTask(TaskType.Sleep, s.SimPos, priority, interruptible: priority < 90f);
        }
        private static BehaviorTask MakeSocialize(Shroomp s, float bonus) =>
            new(TaskType.Socialize, s.SimPos, 80f + bonus);
        private static BehaviorTask MakeAttune(Shroomp s, float bonus) =>
            new(TaskType.Attune, s.SimPos, 75f + bonus);
        private static BehaviorTask MakeSeekSafety(Shroomp s, float bonus) =>
            new(TaskType.SeekSafety, s.SimPos, 85f + bonus);

        // v0.3.43 — per-activity arrival linger (in sim ticks at 60/sec).
        // The higher the linger, the longer the shroomp stands at the
        // destination before re-evaluating. These bracket "feels alive"
        // pacing: Observe (a shroomp gazing) lingers longest; Wander
        // (an active stretch-of-legs) lingers least.
        private const int LingerWander    = 120;   // ≈ 2 sec
        private const int LingerLoiter    = 240;   // ≈ 4 sec
        private const int LingerObserve   = 360;   // ≈ 6 sec
        private const int LingerConverse  = 300;   // ≈ 5 sec
        private const int LingerMeditate  = 540;   // ≈ 9 sec
        private const int LingerVisitFav  = 300;   // ≈ 5 sec

        private static BehaviorTask NewWanderTask(Vector2 from, LocalMap? map, Random rng)
        {
            // v0.5.5 — single-hop wander. The multi-hop chain is set up by
            // the per-shroomp overload below which also seeds WanderHops.
            return PickIdleDestination(from, map, rng, TaskType.Wander, LingerWander, 8, 28);
        }

        // v0.5.5 — multi-hop wander factory. "Taking a walk" should be
        // a real walk: 2-4 destinations chained, walking between each,
        // then a final linger. Sam: "a shroomp should actually take a short
        // walk and finish it." NewWanderTask(Shroomp) seeds the hop counter;
        // ApplyTaskEffect's Wander case consumes it on each arrival,
        // chaining a fresh destination + bumping WorkSearchCooldownTicks
        // so the chained legs don't trigger a re-eval.
        private static BehaviorTask NewWanderTask(Shroomp s, LocalMap? map, Random rng)
        {
            // 1-3 additional hops after the first arrival → 2-4 legs total.
            s.WanderHopsRemaining = rng.Next(1, 4);
            return PickIdleDestination(s.SimPos, map, rng, TaskType.Wander, LingerWander, 8, 28);
        }

        // Short-distance idle: stays close to where the shroomp already is.
        // Produces the "shuffling near the campfire" feel.
        private static BehaviorTask NewLoiterTask(Vector2 from, LocalMap? map, Random rng)
        {
            return PickIdleDestination(from, map, rng, TaskType.Loiter, LingerLoiter, 2, 5);
        }

        // Observe: pick a nearby visible tile, walk to a tile *near* it (not
        // onto it), and stand looking. For now this is functionally a
        // short-radius wander with a much longer linger; once Phase 4
        // introduces points-of-interest (workshops, hearths, item piles),
        // Observe can prefer those tiles.
        // v0.5.36 — if a ShroomBoard exists, the shroomp routes to it
        // (Cerebral recreation). Falls back to the random observe-tile
        // sample otherwise. At-board Joy gain is multiplied 1.5×.
        private static BehaviorTask NewObserveTask(Vector2 from, LocalMap? map, Random rng)
        {
            if (map != null)
            {
                int tx = (int)(from.X / LocalMap.TileSize);
                int ty = (int)(from.Y / LocalMap.TileSize);
                var furn = map.FindNearestJoyFurniture(tx, ty,
                    new[] { StructureType.ShroomBoard });
                if (furn.HasValue)
                {
                    var pos = new Vector2(
                        furn.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        furn.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    return new BehaviorTask(TaskType.Observe, pos, 6f,
                        tileX: furn.Value.X, tileY: furn.Value.Y,
                        interruptible: true, arrivalLinger: LingerObserve);
                }
            }
            return PickIdleDestination(from, map, rng, TaskType.Observe, LingerObserve, 3, 7);
        }

        // Converse: head toward another nearby alive shroomp and stop a tile
        // short. Boosts both shroomps' Social on arrival. Falls back to a
        // loiter if no partner is found within range — solo shroomps don't
        // wander pointlessly looking for a chat.
        private const float ConversePartnerRangePx = 20f * 16f;     // 20 tiles
        private static BehaviorTask NewConverseTask(Shroomp s, LocalMap? map, Random rng,
            IReadOnlyList<Shroomp> shroomps)
        {
            // Find the nearest other alive shroomp within range. Prefer
            // liked shroomps (LikedShroomps list) when one is in range — the
            // existing social-affinity makes the choice feel intentional.
            // v0.5.83 — region gate. A partner inside a walled structure the
            // converser can't reach would have them path-fail every tick.
            int sxTile = (int)(s.SimPos.X / LocalMap.TileSize);
            int syTile = (int)(s.SimPos.Y / LocalMap.TileSize);
            ushort srcRid = map != null ? map.GetRegion(sxTile, syTile) : (ushort)0;

            Shroomp? best = null;
            float bestDist = ConversePartnerRangePx * ConversePartnerRangePx;
            bool foundLiked = false;
            foreach (var other in shroomps)
            {
                if (other == s || !other.IsAlive) continue;
                float dx = other.SimPos.X - s.SimPos.X;
                float dy = other.SimPos.Y - s.SimPos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > bestDist) continue;
                if (map != null && srcRid != 0)
                {
                    int oxTile = (int)(other.SimPos.X / LocalMap.TileSize);
                    int oyTile = (int)(other.SimPos.Y / LocalMap.TileSize);
                    if (map.GetRegion(oxTile, oyTile) != srcRid) continue;
                }
                bool liked = s.Preferences != null && s.Preferences.LikesShroomp(other.Name);
                if (foundLiked && !liked) continue;
                if (liked && !foundLiked) { best = other; bestDist = d2; foundLiked = true; continue; }
                best = other; bestDist = d2;
            }

            if (best == null)
            {
                // No partner in range — fall back to a loiter so the shroomp
                // doesn't just stand still ticking re-evaluation.
                return NewLoiterTask(s.SimPos, map, rng);
            }

            return new BehaviorTask(TaskType.Converse, best.SimPos, 6f,
                interruptible: true, arrivalLinger: LingerConverse,
                targetId: best.Name);
        }

        // Meditate: stand and boost MagicResonance. No movement at all —
        // target is the current position. Mage / Scholar / Mushroom
        // Whisperer types weight into this heavily.
        // v0.5.36 — if a MeditationShrine exists on the map, the shroomp
        // routes to the nearest one instead of meditating in place. At-
        // shrine Joy gain is multiplied 1.5× in ApplyTaskEffect.
        private static BehaviorTask NewMeditateTask(Vector2 from, LocalMap? map, Random rng)
        {
            if (map != null)
            {
                int tx = (int)(from.X / LocalMap.TileSize);
                int ty = (int)(from.Y / LocalMap.TileSize);
                var furn = map.FindNearestJoyFurniture(tx, ty,
                    new[] { StructureType.MeditationShrine });
                // v0.5.83 — reachability gate. If the only/nearest shrine sits
                // in a different DF region (across a wall), fall through to
                // in-place meditate rather than queueing a doomed walk.
                if (furn.HasValue && map.AreReachable(tx, ty, furn.Value.X, furn.Value.Y))
                {
                    var pos = new Vector2(
                        furn.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                        furn.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                    return new BehaviorTask(TaskType.Meditate, pos, 6f,
                        tileX: furn.Value.X, tileY: furn.Value.Y,
                        interruptible: true, arrivalLinger: LingerMeditate);
                }
            }
            return new BehaviorTask(TaskType.Meditate, from, 6f,
                interruptible: true, arrivalLinger: LingerMeditate);
        }

        // VisitFavorite: today this is "walk to a slightly farther random
        // tile" — once shroomps remember their favourite spots (Phase 4
        // workshops, last-good-meal location), this picks one of them. The
        // long-tail wander variant.
        private static BehaviorTask NewVisitFavoriteTask(Shroomp s, LocalMap? map, Random rng)
        {
            return PickIdleDestination(s.SimPos, map, rng, TaskType.VisitFavorite,
                LingerVisitFav, 10, 22);
        }

        // Shared destination sampler — replaces v0.3.35's NewWanderTask
        // inner logic. Tries progressively wider radii within the [minR,
        // maxR] bracket so a shroomp in a pocket still finds somewhere to go.
        // v0.3.43 — parameterised by activity to keep the picker DRY.
        private static BehaviorTask PickIdleDestination(Vector2 from, LocalMap? map,
            Random rng, TaskType activity, int linger, int minRadius, int maxRadius)
        {
            if (map == null)
                return new BehaviorTask(activity, from, 5f,
                    interruptible: true, arrivalLinger: linger);

            int cx = (int)(from.X / LocalMap.TileSize);
            int cy = (int)(from.Y / LocalMap.TileSize);

            // v0.5.83 — region gate. A passable tile in a different DF region
            // is provably unreachable (separated by walls or terrain). Without
            // this check, a shroomp outside a walled structure could pick a
            // passable interior tile and loop on it forever — the visible
            // "Wandering" pawns trying to walk through walls in playtest.
            // GetRegion is O(1) on cached region data.
            ushort srcRid = map.GetRegion(cx, cy);

            // Stretched radii sample similar to v0.3.35 — small first, widen
            // on failure. The activity's bracket bounds the search so a
            // Loiter doesn't wander 28 tiles by accident.
            int[] radii = { minRadius, (minRadius + maxRadius) / 2, maxRadius };
            foreach (int r in radii)
            {
                for (int i = 0; i < 10; i++)
                {
                    int dx = rng.Next(-r, r + 1);
                    int dy = rng.Next(-r, r + 1);
                    int tx = cx + dx;
                    int ty = cy + dy;
                    if (!map.IsPassable(tx, ty)) continue;
                    if (srcRid != 0 && map.GetRegion(tx, ty) != srcRid) continue;
                    return new BehaviorTask(activity,
                        TileToPixel((tx, ty)), 5f,
                        tileX: tx, tileY: ty,
                        interruptible: true, arrivalLinger: linger);
                }
            }

            // v0.5.84b — deterministic ring-scan fallback. The 30 random
            // samples above can all miss in a constrained scene (e.g. small
            // room where most tiles in the maxRadius square fall outside
            // the shroomp's region, or dense pawn-cluster where every
            // sampled tile happens to be the asker's own tile).
            // v0.5.84f — perimeter walk instead of full-square scan. The
            // v0.5.84b first cut iterated (2r+1)² tiles per ring and
            // filtered to perimeter — ~50k operations per call at r=28,
            // ~2.5M ops/tick when many pawns fell through. Direct
            // perimeter walk is 8r per ring; sum r=1..28 = 3248 ops per
            // call. Same result, ~16× less work.
            if (srcRid != 0)
            {
                for (int r = 1; r <= maxRadius; r++)
                {
                    // Top + bottom edges (full width).
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var hit = TryRingPoint(map, cx + dx, cy - r, srcRid);
                        if (hit.HasValue) return MakeIdle(activity, hit.Value, linger);
                        hit = TryRingPoint(map, cx + dx, cy + r, srcRid);
                        if (hit.HasValue) return MakeIdle(activity, hit.Value, linger);
                    }
                    // Left + right edges (excluding corners already done above).
                    for (int dy = -r + 1; dy <= r - 1; dy++)
                    {
                        var hit = TryRingPoint(map, cx - r, cy + dy, srcRid);
                        if (hit.HasValue) return MakeIdle(activity, hit.Value, linger);
                        hit = TryRingPoint(map, cx + r, cy + dy, srcRid);
                        if (hit.HasValue) return MakeIdle(activity, hit.Value, linger);
                    }
                }
            }

            // Last-ditch: completely sealed in (no reachable tile within
            // maxRadius). Return the shroomp's current position but with
            // arrivalLinger=1 so SelectTask re-rolls next tick instead of
            // freezing for the full linger window. SelectTask's next
            // roll might land on a different activity (Loiter, Converse,
            // Meditate) which has different radius brackets.
            return new BehaviorTask(activity, from, 5f,
                interruptible: true, arrivalLinger: 1);
        }

        // v0.5.84f — ring-scan helper. Returns the tile if it's passable
        // and in the requested region, else null.
        private static (int X, int Y)? TryRingPoint(LocalMap map, int tx, int ty, ushort srcRid)
        {
            if (!map.IsPassable(tx, ty)) return null;
            if (map.GetRegion(tx, ty) != srcRid) return null;
            return (tx, ty);
        }

        private static BehaviorTask MakeIdle(TaskType activity, (int X, int Y) tile, int linger) =>
            new BehaviorTask(activity, TileToPixel(tile), 5f,
                tileX: tile.X, tileY: tile.Y,
                interruptible: true, arrivalLinger: linger);

        // ── Task effects (Roadmap §3.8) ─────────────────────────────────────
        private static void ApplyTaskEffect(Shroomp s, BehaviorTask t, LocalMap? map,
            ColonyResources r, float dt, IReadOnlyList<Shroomp> shroomps,
            Random rng, long globalTick)
        {
            switch (t.Type)
            {
                case TaskType.Eat:
                {
                    // v0.3.46 (Phase 4) — Eat resolves a specific item from
                    // the colony Inventory instead of decrementing a float.
                    // v0.5.68 — also tries map-drop food at the destination
                    // tile (food foraged onto stockpiles never reached the
                    // inventory under the old code path, so a colony with
                    // 40+ Food on the HUD could still starve to death).
                    // Eating order:
                    //   1. Colony Inventory FindBestFood (meals, raw produce).
                    //   2. Map drop at TargetTileX/Y (where MakeEat routed us).
                    // When starving (Nutrition < 25) both paths widen to
                    // include Spoiled food + Corpses (RimWorld urgent-food
                    // fallback). After the ingest the task is cleared so
                    // SelectTask re-evaluates next tick — keeps the shroomp
                    // from looping on a stale Eat task when food has been
                    // exhausted between SelectTask and ApplyTaskEffect.
                    bool wasFamished = s.Nutrition < 15f;
                    bool starving    = s.Nutrition < 25f;

                    Items.Item? consumed = null;
                    bool fromMap = false;
                    bool fromInventory = false;

                    var stack = r.Inventory.FindBestFood(s, allowSpoiled: starving);
                    if (stack != null)
                    {
                        var def = ItemRegistry.Get(stack.Kind, stack.SubType);
                        float perUnit = (def?.BaseNutrition ?? 5f)
                                      * QualityMeta.NutritionMul(stack.Quality);
                        if (stack.State == ItemState.Stale)        perUnit *= 0.6f;
                        else if (stack.State == ItemState.Spoiled) perUnit *= 0.3f;
                        s.Nutrition = MathF.Min(100f, s.Nutrition + perUnit);
                        r.Inventory.Consume(stack, 1);
                        consumed = stack;
                        fromInventory = true;
                    }
                    else if (map != null)
                    {
                        // Map fallback — look on the tile the shroomp is
                        // standing on (MakeEat parked them here). Also accept
                        // food on adjacent tiles to handle the case where the
                        // shroomp arrives close-enough via the climb/yield
                        // path but not exactly on the food tile.
                        int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                        int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                        Items.Item? pick = map.PickupBestFoodAt(curTx, curTy, s,
                            allowSpoiled: starving, allowCorpse: starving);
                        if (pick == null && t.TargetTileX >= 0 && t.TargetTileY >= 0
                            && (t.TargetTileX != curTx || t.TargetTileY != curTy))
                        {
                            pick = map.PickupBestFoodAt(t.TargetTileX, t.TargetTileY, s,
                                allowSpoiled: starving, allowCorpse: starving);
                        }
                        if (pick != null)
                        {
                            float baseNutrition = pick.Kind == Items.ItemKind.Corpse
                                ? 15f
                                : (ItemRegistry.Get(pick.Kind, pick.SubType)?.BaseNutrition ?? 5f);
                            float perUnit = baseNutrition * QualityMeta.NutritionMul(pick.Quality);
                            if (pick.State == Items.ItemState.Stale)        perUnit *= 0.6f;
                            else if (pick.State == Items.ItemState.Spoiled) perUnit *= 0.3f;
                            else if (pick.Kind == Items.ItemKind.Corpse)    perUnit *= 0.5f;
                            s.Nutrition = MathF.Min(100f, s.Nutrition + perUnit);
                            consumed = pick;
                            fromMap = true;
                        }
                    }

                    if (consumed != null)
                    {
                        // Pick the thought: corpse + spoiled take precedence
                        // over normal quality / preference thoughts. AteHungry
                        // overrides quality thoughts (RimWorld "Finally ate"
                        // long-tail mood from going below the Urgent
                        // threshold) but NOT the AteCorpse / AteSpoiled
                        // trauma — eating a body is bad even when starving.
                        string key;
                        if (consumed.Kind == Items.ItemKind.Corpse)
                            key = "AteCorpse";
                        else if (consumed.State == Items.ItemState.Spoiled)
                            key = "AteSpoiled";
                        else if (s.Preferences != null && s.Preferences.LikesItem(consumed.SubType))
                            key = "AteFavorite";
                        else if (s.Preferences != null && s.Preferences.DislikesItem(consumed.SubType))
                            key = "AteDisliked";
                        else if (wasFamished)
                            key = "AteHungry";
                        else
                            key = QualityMeta.MealThoughtKey(consumed.Quality);
                        ThoughtRegistry.Add(s, key, consumed.SubType);

                        // v0.5.37 — AteWithoutTable mood penalty. If the
                        // shroomp is NOT adjacent to a built Table, emit
                        // AteWithoutTable (-3). AteHungry / AteSpoiled /
                        // AteCorpse all suppress the penalty since the
                        // shroomp had no choice in the matter.
                        bool suppressTablePenalty = key == "AteHungry"
                            || key == "AteSpoiled" || key == "AteCorpse";
                        if (map != null && !suppressTablePenalty)
                        {
                            int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            bool nearTable = false;
                            for (int dy = -1; dy <= 1 && !nearTable; dy++)
                            for (int dx = -1; dx <= 1 && !nearTable; dx++)
                            {
                                int nx = curTx + dx, ny = curTy + dy;
                                if (!map.InBounds(nx, ny)) continue;
                                if (map.GetStructure(nx, ny).Type == StructureType.Table)
                                    nearTable = true;
                            }
                            if (!nearTable)
                                ThoughtRegistry.Add(s, "AteWithoutTable");
                            // v0.5.60 B1 — eating at a table satisfies
                            // Social mildly when another shroomp is also
                            // adjacent to a table within 3 tiles. Only the
                            // inventory + table path qualifies; eating off
                            // the floor from a map drop doesn't.
                            else if (fromInventory)
                            {
                                int partnersAtTable = 0;
                                for (int oi = 0; oi < shroomps.Count && partnersAtTable < 1; oi++)
                                {
                                    var o = shroomps[oi];
                                    if (o == s || !o.IsAlive) continue;
                                    int oTx = (int)(o.SimPos.X / LocalMap.TileSize);
                                    int oTy = (int)(o.SimPos.Y / LocalMap.TileSize);
                                    int ddx = oTx - curTx, ddy = oTy - curTy;
                                    if (ddx * ddx + ddy * ddy <= 9) partnersAtTable++;
                                }
                                if (partnersAtTable > 0)
                                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.25f);
                            }
                        }
                    }

                    // v0.5.68 — always clear the Eat task at the end. Whether
                    // we ate or not (food gone, starving with nothing to
                    // find), the task is done. Next tick SelectTask picks
                    // again — re-Eat if still hungry and food exists, or
                    // move on to something else if no food remains. Without
                    // this clear a shroomp could loop forever standing at a
                    // table with an empty inventory.
                    _ = fromMap;   // reserved for future map-eaten hediff hooks
                    s.CurrentTask = null;
                    break;
                }
                case TaskType.Sleep:
                {
                    // v0.5.35 — RestEffectiveness depends on whether the shroomp
                    // is sleeping in a Bed (1.0×) or on the floor (0.8×).
                    // Mirrors RimWorld's bed effectiveness table — sleeping
                    // spot 0.80, vanilla bed 1.00, royal 1.05. Quality
                    // multiplier (Crude 0.86 / Normal 1.0 / Masterwork 1.25)
                    // is applied via Items.QualityMeta.ValueMul/2+0.5
                    // approximation so a masterwork bed grants noticeably
                    // faster rest restoration.
                    float effectiveness = 0.80f;
                    bool atBed = false;
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var bedSlot = map.GetStructure(t.TargetTileX, t.TargetTileY);
                        if (bedSlot.Type == StructureType.Bed)
                        {
                            atBed = true;
                            effectiveness = 1.0f;
                            // Quality bonus (mirrors RimWorld bed quality multipliers
                            // 0.86 → 1.60 across Awful → Legendary; we map our
                            // Crude → Masterwork to a milder 0.86 → 1.25 range).
                            effectiveness *= bedSlot.Quality switch
                            {
                                Items.Quality.Crude      => 0.86f,
                                Items.Quality.Normal     => 1.00f,
                                Items.Quality.Fine       => 1.08f,
                                Items.Quality.Superior   => 1.14f,
                                Items.Quality.Masterwork => 1.25f,
                                Items.Quality.Legendary  => 1.40f,
                                _                        => 1.00f,
                            };
                            // v0.5.84i — material comfort bonus on top
                            // of bed-vs-floor + quality. Sam: "fungalwood
                            // beds and wood beds should be comfortable
                            // while stone beds are less so." Granite bed
                            // (Comfort=0.70) vs fungalwood bed (Comfort=
                            // 1.05) → ~50 % slower rest restoration on
                            // stone. Stack multiplicatively with quality
                            // so a Granite Masterwork bed (0.70 × 1.25 =
                            // 0.875) still beats a FungalWood Crude bed
                            // (1.05 × 0.86 = 0.903) only marginally —
                            // material matters but masterwork crafting
                            // still rewards the player.
                            effectiveness *= StructureMatMeta.Comfort(bedSlot.Material);
                        }
                    }
                    s.Rest = MathF.Min(100f, s.Rest + SleepRate * dt * effectiveness);
                    // v0.5.35 — Wake-time mood thought. WellRested for bed
                    // sleepers, SleptOnGround for floor-sleepers. Fires once
                    // per sleep arc when Rest crosses 80 %.
                    if (s.Rest > 80f)
                    {
                        ThoughtRegistry.Add(s, atBed ? "WellRested" : "SleptOnGround");
                        // v0.5.84t — extra mood boost when the bed is inside
                        // a Bedroom-typed room (Room.Type == Bedroom). RimWorld
                        // parity: "Slept in bedroom" comfort thought.
                        if (atBed && map != null)
                        {
                            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            map.EnsureRooms();
                            var sleepSlot = map.GetStructure(sx, sy);
                            if (sleepSlot.RoomId != 0 && sleepSlot.RoomId != RoomDetector.OutdoorRoomId)
                            {
                                var sleepRoom = map.GetRoom(sleepSlot.RoomId);
                                if (sleepRoom != null && sleepRoom.Type == RoomType.Bedroom)
                                    ThoughtRegistry.Add(s, "SleptInBedroom");
                            }
                        }
                    }
                    // v0.5.68 — wake up when fully rested. RimWorld parity:
                    // Toils_LayDown ends when need_rest >= 1.0. Without this
                    // clause a shroomp who hit Rest=100 during the night-sleep
                    // window (priority 75) keeps sleeping until Nutrition
                    // drops below 20 — wasting the rest of the night and
                    // letting Nutrition degrade far below the comfort floor.
                    // Sam's screenshot: shroomps slept the entire night at
                    // Rest=100 then died of starvation. Clearing the task
                    // forces SelectTask to re-evaluate next tick; if it's
                    // still night they'd re-pick Sleep only when Rest drops
                    // below 80 again (the in-window threshold).
                    if (s.Rest >= 100f)
                    {
                        s.CurrentTask = null;
                        break;
                    }
                    // v0.5.60 B1 — multi-need activity location. Sleeping
                    // near a partner (within 2 tiles) ticks Social mildly.
                    // DF pattern: dwarves sharing bedrooms gain social
                    // need fulfilment passively from proximity. Mild effect
                    // so it doesn't replace Converse / interactions — just
                    // makes shared sleeping quarters feel cohesive.
                    if (atBed && map != null)
                    {
                        int sleepTx = (int)(s.SimPos.X / LocalMap.TileSize);
                        int sleepTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                        for (int oi = 0; oi < shroomps.Count; oi++)
                        {
                            var o = shroomps[oi];
                            if (o == s || !o.IsAlive) continue;
                            if (o.CurrentTask is not { Type: TaskType.Sleep }) continue;
                            int oTx = (int)(o.SimPos.X / LocalMap.TileSize);
                            int oTy = (int)(o.SimPos.Y / LocalMap.TileSize);
                            int dxs = oTx - sleepTx, dys = oTy - sleepTy;
                            if (dxs * dxs + dys * dys <= 4)
                            {
                                s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.15f);
                                break;
                            }
                        }
                    }
                    break;
                }
                case TaskType.Socialize:
                    s.Social    = MathF.Min(100f, s.Social    + SocializeRate * dt);
                    SkillRegistry.GainXp(s, "Social", 0.06f);   // sustained — per-tick
                    break;
                case TaskType.Attune:
                    {
                        // v0.5.84t — apply Focus tool bonus to Attune rate.
                        float attuneToolBonus = GetToolBonusFor(s, TaskType.Attune);
                        s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt * attuneToolBonus);
                        if (s.MagicResonance > 85f) ThoughtRegistry.Add(s, "Attuned");
                        SkillRegistry.GainXp(s, "Magic", 0.08f);   // sustained — per-tick (v0.5.84r: Arcane → Magic)
                    }
                    break;
                case TaskType.SeekSafety:
                    s.Safety    = MathF.Min(100f, s.Safety    + SeekSafetyRate * dt);
                    if (s.Safety > 80f) ThoughtRegistry.Add(s, "FoundSafety");
                    break;
                case TaskType.Heal:
                    // Heal own most-damaged body part as a placeholder; Phase 7
                    // Healer system wires the proper tend-at-bed loop (rescue
                    // downed pawn → carry to bed → Healer treats wounds with
                    // medicine items).
                    // v0.5.84r — flat HealRate replaced by natural biological
                    // healing in NeedsSystem (runs passively on every pawn).
                    // This Heal task now layers an active-tending boost on
                    // top of natural healing using the Healing skill — the
                    // self-tend stub stays in place so the Heal TaskType
                    // remains exercised until Phase 7 ships the real Healer
                    // mechanics. Healer skill scales the bonus 1.0× (lvl 0)
                    // → 2.0× (lvl 20). Awards Healing XP per completion.
                    {
                        string? worst = null;
                        float   low   = 100f;
                        foreach (var (part, cond) in s.BodyParts)
                            if (cond < low && cond > 0f) { worst = part; low = cond; }
                        if (worst != null)
                        {
                            int healSkill = SkillLevel(s, "Healing");
                            float skillMul = 1.0f + 0.05f * healSkill;   // lvl 0 = 1.0, lvl 20 = 2.0
                            float tend = 1.0f * dt * skillMul;          // tend-quality on top of natural heal
                            s.BodyParts[worst] = MathF.Min(100f, low + tend);
                            SkillRegistry.GainXp(s, "Healing", 30f * dt);   // ~30 XP per second of tending
                        }
                    }
                    break;
                case TaskType.GatherFood:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var slot = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (slot.IsPresent && !slot.IsDepleted)
                        {
                            var vegType = slot.Type;
                            map.HarvestVegetation(t.TargetTileX, t.TargetTileY);
                            var mapping = ItemFactory.FoodFromVegetation(vegType);
                            int baseFoodYield = (int)MathF.Round(FoodYield(vegType));
                            // v0.5.30 — Foraging skill scales the food yield.
                            // Lvl 0 = 50 %, lvl 8 = 100 %, lvl 20 = 130 %.
                            // Botched harvest at low skill drops to 50 %.
                            int forageSkillFood = SkillLevel(s, "Botany");
                            // v0.5.84t — apply tool bonus (Basket with GatherFood).
                            float gatherToolBonus = GetToolBonusFor(s, TaskType.GatherFood);
                            int yield = baseFoodYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseFoodYield,
                                SkillCurve.PlantYieldFactor(forageSkillFood) * gatherToolBonus,
                                rng);
                            if (yield > 0 && SkillCurve.HarvestBotch(forageSkillFood, rng))
                                yield = Mathf.Max(1, yield / 2);
                            // v0.4.2 — drop the food item *on the tile*
                            // (Phase 4 sub-A pushed directly to colony
                            // pool; sub-B introduces the on-tile drop
                            // pipeline + Haul task). The tile position is
                            // the work tile's pixel centre.
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            if (mapping.HasValue && yield > 0)
                            {
                                var item = ItemFactory.Create(
                                    ItemKind.Food, mapping.Value.SubType,
                                    mapping.Value.Material, rng, globalTick,
                                    skillLevel: SkillLevel(s, "Botany"),
                                    quantity: yield);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            // v0.4.2 — magic vegetation also drops Raw
                            // Essence per the player brief: "magic plants
                            // give both essence and food".
                            if (ItemFactory.VegetationYieldsMagicEssence(vegType))
                            {
                                var essence = ItemFactory.Create(
                                    ItemKind.Magic, "RawEssence",
                                    new MaterialKey("Magic","RawEssence"),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Botany"),
                                    quantity: 1);
                                essence.TilePos = dropPos;
                                map.DropItem(essence);
                            }
                            EmitWorkThought(s, TaskType.GatherFood,
                                ItemNameFor(vegType));
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Foraging XP per harvest. RimWorld
                            // gives ~80 XP per completed work-step; we tune
                            // forage lower since vegetation is the
                            // most-frequent work type.
                            SkillRegistry.GainXp(s, "Botany", 40f);   // v0.5.84r: Foraging merged into Botany
                        }
                        // v0.3.21 — once harvested, the Gather designation is
                        // fulfilled. Clear it so the overlay glyph disappears
                        // and the next idle shroomp doesn't reroute here.
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.GatherMaterial:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var tile = map.Get(t.TargetTileX, t.TargetTileY);
                        if (tile.DesignatedForExcavation)
                        {
                            // v0.5.84t — per-tick mining. Pre-v0.5.84t mining
                            // was instant-on-arrival, which made the dormant
                            // SkillCurve.MiningSpeedFactor curve invisible.
                            // Now we accumulate work per tick:
                            //   delta = 10 × MiningSpeedFactor(skill) × ToolBonus
                            //   target = WorkAmount(terrain)
                            // Reset progress when the shroomp targets a
                            // different tile or abandons the task. Stays in
                            // this branch until progress reaches the target,
                            // then the original yield/terrain-mutate logic
                            // fires at the bottom.
                            int workTarget = tile.Terrain switch
                            {
                                TerrainType.Boulder    => 200,
                                TerrainType.DeadLog    => 150,
                                TerrainType.LivingWood => 200,
                                TerrainType.Skeleton   => 100,
                                _                      => 0,
                            };
                            if (workTarget > 0)
                            {
                                // Reset progress if we switched targets.
                                if (s.GatherTargetTileX != t.TargetTileX || s.GatherTargetTileY != t.TargetTileY)
                                {
                                    s.GatherProgress    = 0;
                                    s.GatherTargetTileX = t.TargetTileX;
                                    s.GatherTargetTileY = t.TargetTileY;
                                }
                                int miningSkill = SkillLevel(s, "Mining");
                                float toolBonus = GetToolBonusFor(s, TaskType.GatherMaterial);
                                int delta = Mathf.Max(1, (int)(10f * SkillCurve.MiningSpeedFactor(miningSkill) * toolBonus));
                                s.GatherProgress += delta;
                                s.TaskDidWork = true;
                                // Trickle Mining XP per work tick so cold
                                // shroomps still level up while mining (the
                                // 80 XP/boulder grant only fires on completion).
                                SkillRegistry.GainXp(s, "Mining", 0.4f);
                                if (s.GatherProgress < workTarget)
                                {
                                    // Not yet complete — keep the task alive
                                    // for next tick. Don't fall through to
                                    // the yield block.
                                    break;
                                }
                                // Progress reached target — reset and fall
                                // through to the existing yield logic below.
                                s.GatherProgress    = 0;
                                s.GatherTargetTileX = -1;
                                s.GatherTargetTileY = -1;
                            }
                            // v0.4.2 — Boulder material drawn from the
                            // per-tile stone subtype stored at generation
                            // (LocalMap.GetTileStone), falling back to a
                            // weighted roll when the tile has no
                            // pre-assigned material. DeadLog → DeadWood
                            // log, LivingWood → LivingWood log.
                            var mapping = ItemFactory.MaterialFromTerrain(tile.Terrain, rng);
                            if (tile.Terrain == TerrainType.Boulder)
                            {
                                var perTile = map.GetTileStone(t.TargetTileX, t.TargetTileY);
                                if (perTile.HasValue) mapping = (ItemKind.Material, "StoneBlock", perTile.Value);
                            }
                            // v0.5.16 — Skeleton terrain drops Bone material
                            // instead of Stone/Wood. Uses the existing
                            // mapping pipeline via ItemFactory.MaterialFromTerrain
                            // (no special branch needed once that helper
                            // recognises Skeleton → Bone). Yield 3 = small
                            // pile per skeleton fragment (rib bone, partial
                            // skull). Sam: "imitate the look of a rib bone
                            // or partial animal skull poking out of the
                            // ground." Provides early-game Bone material
                            // before Phase 8 animal butchery lands.
                            if (tile.Terrain == TerrainType.Skeleton)
                            {
                                mapping = (ItemKind.Material, "BoneFragment",
                                    new MaterialKey("Bone","Generic"));
                            }
                            int baseYield = tile.Terrain switch
                            {
                                TerrainType.Boulder    => 4,
                                TerrainType.DeadLog    => 4,
                                TerrainType.LivingWood => 6,
                                TerrainType.Skeleton   => 3,   // v0.5.16
                                _                      => 0,
                            };
                            // v0.5.30 — Mining yield scaled by skill (RimWorld
                            // pattern). Lvl 0 = 60 %, lvl 8 = 80 %, lvl 16 = 100 %,
                            // lvl 20 = 110 %. A level-0 novice still extracts
                            // some material (never zero); a master gets the
                            // full yield + a small bonus. DeadLog/LivingWood
                            // share the Mining skill since the player drives
                            // both via the Excavate designation.
                            int yield = baseYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseYield,
                                SkillCurve.MiningYieldFactor(SkillLevel(s, "Mining")),
                                rng);
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            if (yield > 0)
                            {
                                var item = ItemFactory.Create(
                                    mapping.Kind, mapping.SubType,
                                    mapping.Material, rng, globalTick,
                                    skillLevel: SkillLevel(s, "Mining"),
                                    quantity: yield);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            // v0.4.2 — MagicCrystal stone is the rare
                            // ore-vein variant. Excavating it produces a
                            // separate Magic/CrystalShard item alongside
                            // the StoneBlock, matching the DF / RimWorld
                            // "mining gems drops shards" pattern.
                            if (tile.Terrain == TerrainType.Boulder
                                && mapping.Material.SubType == "MagicCrystal")
                            {
                                var shard = ItemFactory.Create(
                                    ItemKind.Magic, "CrystalShard",
                                    new MaterialKey("Magic","CrystalShard"),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Mining"),
                                    quantity: rng.Next(1, 4));   // 1-3 shards per vein
                                shard.TilePos = dropPos;
                                map.DropItem(shard);
                            }
                            // v0.5.14 (Phase 5C — rimport.md N18) — buried
                            // treasure quest hook. Tile flagged at gen time
                            // by ScatterBuriedTreasure drops a bonus Trinket
                            // alongside the standard StoneBlock. Same
                            // mechanism the future "sleeping creatures"
                            // hook (Phase 8) will use — different on-excavate
                            // effect. Sam: "what will I find under there?"
                            if (tile.Terrain == TerrainType.Boulder
                                && map.HasBuriedTreasure(t.TargetTileX, t.TargetTileY))
                            {
                                var trinket = ItemFactory.Create(
                                    ItemKind.Trinket, "AncientRelic",
                                    new MaterialKey("Magic","CrystalShard"),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Mining"),
                                    quantity: 1);
                                trinket.TilePos = dropPos;
                                map.DropItem(trinket);
                                map.RemoveBuriedTreasure(t.TargetTileX, t.TargetTileY);
                                ThoughtRegistry.Add(s, "FoundTreasure");
                            }
                            map.MutateTerrain(t.TargetTileX, t.TargetTileY, TerrainType.Mud);
                            map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                            EmitWorkThought(s, TaskType.GatherMaterial, null);
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Mining XP per boulder mined.
                            // 80 XP matches RimWorld's per-completion grant
                            // for a "real" work step. ~12 boulders gets
                            // a mid-skill miner from level 4 → 5.
                            SkillRegistry.GainXp(s, "Mining", 80f);
                        }
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.38 — Chop Wood: harvest a wood-yielding shroom. Same
                // harvest mechanic as GatherFood but yields Wood. The
                // vegetation slot's `HarvestVegetation` already flips tile
                // passability when LargeMushroom variants are fully depleted.
                case TaskType.ChopWood:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var slot = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (slot.IsPresent && !slot.IsDepleted)
                        {
                            // v0.4.15 — single-shot felling (RimWorld
                            // semantics). The previous version called
                            // `HarvestVegetation` once (decrement yield
                            // by 1) and then `ClearDesignationsAt`, so a
                            // LargeMushroom (BaseYield = 3) shed its chop
                            // designation after producing 1/3 of its
                            // wood. Shroomps would then walk away leaving
                            // a half-chopped tree standing, the player
                            // would re-designate, and in dense chop
                            // clusters the colony jittered between
                            // adjacent partial trees. Now: one arrival
                            // fells the whole tree, drops total wood,
                            // tile flips passable in the same call.
                            var vegType = slot.Type;
                            int basePerYield = (int)MathF.Round(WoodYield(vegType));
                            byte taken = map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            // v0.5.30 — Plant yield scaled by Foraging skill.
                            // Lvl 0 = 50 %, lvl 8 = 100 %, lvl 20 = 130 %.
                            // Per-stalk botch chance at low skill can drop
                            // total to 50 % of baseline (HarvestBotch roll).
                            int forageSkill = SkillLevel(s, "Botany");
                            int baseTotal = basePerYield * taken;
                            // v0.5.84t — apply tool bonus (Sickle/Knife with ChopWood).
                            float chopToolBonus = GetToolBonusFor(s, TaskType.ChopWood);
                            int totalYield = baseTotal == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseTotal,
                                SkillCurve.PlantYieldFactor(forageSkill) * chopToolBonus,
                                rng);
                            if (totalYield > 0 && SkillCurve.HarvestBotch(forageSkill, rng))
                                totalYield = Mathf.Max(1, totalYield / 2);
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            if (totalYield > 0)
                            {
                                var mat = ItemFactory.WoodFromVegetation(vegType);
                                var item = ItemFactory.Create(
                                    ItemKind.Material, "WoodLog", mat,
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Construction"),
                                    quantity: totalYield);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            EmitWorkThought(s, TaskType.ChopWood, null);
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Foraging XP per chopped tree.
                            // (Could split to a dedicated Plants skill in
                            // a future skill audit; for now Foraging
                            // covers all wild-resource gathering.)
                            SkillRegistry.GainXp(s, "Botany", 60f);   // v0.5.84r: ChopWood now awards Botany (chopping LargeMushroom is plant work)
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.38 — Cut Plants: clear any vegetation tile and drop
                // the relevant resource for that plant.
                // v0.5.69 — yield split by vegetation kind (Sam):
                //   • Undergrowth / MossPatch → Cuttings (compost biomass;
                //     reserved for decoration plants — repurposed later)
                //   • Wood-yielding shrooms (LargeMushroom, LargeSandshroom,
                //     PalmShroom) → Fungal Wood (matches ChopWood yield path
                //     so cutting a large shroom is functionally equivalent
                //     to chopping it)
                //   • Food-yielding plants (CapberryBush, SmallMushroom,
                //     HerbCluster, MagicFlower, etc.) → their food drop
                //     (matches GatherFood yield path)
                // Pre-v0.5.69 every Cut dropped Cuttings regardless of plant
                // type — a large shroom cut yielded biomass instead of wood,
                // which Sam called out as wrong.
                case TaskType.CutVegetation:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var slot = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (slot.IsPresent && !slot.IsDepleted)
                        {
                            var vegType = slot.Type;
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            bool isDecoration =
                                vegType == VegetationType.Underbrush
                                || vegType == VegetationType.MossPatch;
                            bool isWoodYielding = LocalMap.IsWoodYielding(vegType);

                            // Deplete the slot. Decoration veg (BaseYield = 0)
                            // can't be HarvestVegetation'd, so ClearVegetation
                            // removes it outright. Harvestable veg goes
                            // through FullyDeplete so it leaves a stump and
                            // regrows on its normal schedule.
                            byte taken;
                            if (isDecoration)
                            {
                                map.ClearVegetation(t.TargetTileX, t.TargetTileY);
                                taken = 1;
                            }
                            else
                            {
                                taken = map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            }

                            if (isDecoration)
                            {
                                // Decoration → biomass cuttings. v0.5.70 splits:
                                //   Underbrush → Cuttings (Plant/Cuttings)
                                //   MossPatch  → Mosslet  (Plant/Mosslet),
                                //               reserved for a future system
                                //               (Sam: "we'll use later").
                                string sub = vegType == VegetationType.MossPatch
                                    ? "Mosslet"
                                    : "Cuttings";
                                int qty = vegType == VegetationType.MossPatch ? 2 : 1;
                                var item = ItemFactory.Create(
                                    ItemKind.Material, sub, new MaterialKey("Plant", sub),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Botany"),
                                    quantity: qty);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            else if (isWoodYielding)
                            {
                                // Wood-yielder → Fungal Wood (matches
                                // ChopWood yield curve so Cut/Chop are
                                // interchangeable on large shrooms).
                                int basePerYield = (int)MathF.Round(WoodYield(vegType));
                                int forageSkill = SkillLevel(s, "Botany");
                                int baseTotal = basePerYield * (taken == 0 ? 1 : taken);
                                // v0.5.84t — apply tool bonus (Sickle/Knife with CutVegetation).
                                float cutToolBonusWood = GetToolBonusFor(s, TaskType.CutVegetation);
                                int totalYield = baseTotal == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                    baseTotal,
                                    SkillCurve.PlantYieldFactor(forageSkill) * cutToolBonusWood,
                                    rng);
                                if (totalYield > 0 && SkillCurve.HarvestBotch(forageSkill, rng))
                                    totalYield = Mathf.Max(1, totalYield / 2);
                                if (totalYield > 0)
                                {
                                    var mat = ItemFactory.WoodFromVegetation(vegType);
                                    var item = ItemFactory.Create(
                                        ItemKind.Material, "WoodLog", mat,
                                        rng, globalTick,
                                        skillLevel: SkillLevel(s, "Construction"),
                                        quantity: totalYield);
                                    item.TilePos = dropPos;
                                    map.DropItem(item);
                                }
                            }
                            else
                            {
                                // Food-yielder → drop the relevant food
                                // (matches GatherFood yield curve). Magic
                                // vegetation also drops Raw Essence as in
                                // GatherFood.
                                var mapping = ItemFactory.FoodFromVegetation(vegType);
                                int baseFoodYield = (int)MathF.Round(FoodYield(vegType));
                                int forageSkillFood = SkillLevel(s, "Botany");
                                // v0.5.84t — apply tool bonus (Sickle/Knife with CutVegetation).
                                float cutToolBonusFood = GetToolBonusFor(s, TaskType.CutVegetation);
                                int yield = baseFoodYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                    baseFoodYield,
                                    SkillCurve.PlantYieldFactor(forageSkillFood) * cutToolBonusFood,
                                    rng);
                                if (yield > 0 && SkillCurve.HarvestBotch(forageSkillFood, rng))
                                    yield = Mathf.Max(1, yield / 2);
                                if (mapping.HasValue && yield > 0)
                                {
                                    var item = ItemFactory.Create(
                                        ItemKind.Food, mapping.Value.SubType,
                                        mapping.Value.Material, rng, globalTick,
                                        skillLevel: SkillLevel(s, "Botany"),
                                        quantity: yield);
                                    item.TilePos = dropPos;
                                    map.DropItem(item);
                                }
                                if (ItemFactory.VegetationYieldsMagicEssence(vegType))
                                {
                                    var essence = ItemFactory.Create(
                                        ItemKind.Magic, "RawEssence",
                                        new MaterialKey("Magic","RawEssence"),
                                        rng, globalTick,
                                        skillLevel: SkillLevel(s, "Botany"),
                                        quantity: 1);
                                    essence.TilePos = dropPos;
                                    map.DropItem(essence);
                                }
                            }
                            EmitWorkThought(s, TaskType.CutVegetation, null);
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Botany XP per cut. Lower than
                            // chop because cut covers a wider mix including
                            // small decoration plants; less skill-relevant.
                            SkillRegistry.GainXp(s, "Botany", 30f);
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.43 — idle effects. The shroomp has arrived at their
                // chosen idle destination; ApplyTaskEffect handles the
                // "what does this activity actually do" side-effects, and
                // the main tick loop sets IdleLingerTicks so the shroomp
                // holds at the destination for ArrivalLinger ticks instead
                // of immediately re-picking.
                case TaskType.Loiter:
                    // Loiter is the "doing nothing in particular" activity.
                    // Tiny idle thought, no need changes. Emit only on
                    // ~10 % of arrivals so the thoughts pane doesn't get
                    // spammed with the same headline.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    if (r != null /* keep r warning-free */ && (s.Id.GetHashCode() & 7) == 0)
                        ThoughtRegistry.Add(s, "Wandered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.6f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Loiter));
                    BumpJoyTolerance(s, TaskType.Loiter);
                    break;

                case TaskType.Observe:
                    // Standing and watching. Boosts Social slightly (people-
                    // watching is social!) and emits a Daydreamed thought.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.3f);
                    ThoughtRegistry.Add(s, "Daydreamed");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.8f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Observe));
                    BumpJoyTolerance(s, TaskType.Observe);
                    break;

                case TaskType.Converse:
                {
                    // v0.5.59 — RimWorld-parity recreation exit. RimWorld's
                    // JoyGiver / SocialRelax aborts the job the moment the
                    // pawn's Joy / Social need crosses 90 % — recreation
                    // stops being chosen, and any in-progress recreation
                    // ends. Sam: "pawns chat for far too long at 100 social
                    // while others deliver resources infinitely to
                    // blueprints that never get built." Pre-v0.5.59 the
                    // Converse case unconditionally clamped Social to 100
                    // but never cleared CurrentTask — so once a shroomp
                    // started chatting, they rode out the full
                    // LingerConverse window (300 ticks ≈ 5 sec at 1×)
                    // regardless of need state, then re-picked Converse
                    // again next idle roll because the idle weight didn't
                    // account for Social either (separate fix in
                    // SelectIdleActivity below). Net effect: paired shroomps
                    // chained 5-second chats indefinitely. Now: if Social
                    // is already at/near full, exit immediately — and free
                    // the locked partner too so they don't sit chatting
                    // at thin air for another 5 seconds.
                    if (s.Social >= 95f)
                    {
                        if (t.TargetId != null)
                        {
                            foreach (var o in shroomps)
                            {
                                if (!o.IsAlive || o.Name != t.TargetId) continue;
                                if (o.CurrentTask is { } ot
                                    && ot.Type == TaskType.Converse
                                    && ot.TargetId == s.Name)
                                {
                                    o.CurrentTask = null;
                                    o.IdleArrived = false;
                                    o.IdleLingerTicks = 0;
                                }
                                break;
                            }
                        }
                        s.CurrentTask = null;
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                        break;
                    }
                    // Boost both this shroomp and the partner's Social, build
                    // friendship over repeated chats, and emit a thought
                    // that depends on whether they like each other.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt);
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Converse));
                    BumpJoyTolerance(s, TaskType.Converse);
                    Shroomp? partner = null;
                    if (t.TargetId != null)
                    {
                        foreach (var o in shroomps)
                            if (o.IsAlive && o.Name == t.TargetId) { partner = o; break; }
                    }
                    if (partner != null)
                    {
                        // v0.5.5 — two-way conversation lock. RimWorld's
                        // InteractionWorker pattern: when the initiator
                        // arrives at their target, the target is locked
                        // into a reciprocal interaction so the social
                        // exchange is genuinely two-way (both pawns face
                        // each other, both gain joy/social, both produce
                        // thoughts referencing the other). Without this,
                        // the partner doesn't know they're being talked
                        // to — they continue their own task and may
                        // wander off mid-conversation, leaving the
                        // initiator chatting at thin air.
                        //
                        // Lock idempotently — only if the partner is
                        // close enough, idle (not mid-work / mid-critical
                        // / mid-PlayerOrder / locked with a third party),
                        // and not already pointing back at us. Sam:
                        // "a shroomp should actually have a two way
                        // conversation with another shroomp that engages
                        // both and lasts until they're done speaking."
                        TryLockConversePartner(s, partner, t.ArrivalLinger);

                        partner.Social = MathF.Min(100f, partner.Social + SocializeRate * dt);
                        // Build affinity. Three positive chats = friend.
                        bool weDislike   = s.Preferences?.DislikesShroomp(partner.Name) ?? false;
                        bool theyDislike = partner.Preferences?.DislikesShroomp(s.Name) ?? false;
                        if (weDislike || theyDislike)
                        {
                            ThoughtRegistry.Add(s,       "ChatWithEnemy", partner.Name);
                            ThoughtRegistry.Add(partner, "ChatWithEnemy", s.Name);
                        }
                        else
                        {
                            bool weLike   = s.Preferences?.LikesShroomp(partner.Name) ?? false;
                            ThoughtRegistry.Add(s,       weLike ? "ChatWithFriend" : "NiceChat", partner.Name);
                            ThoughtRegistry.Add(partner, weLike ? "ChatWithFriend" : "NiceChat", s.Name);
                            // Tiny chance of forming a friendship on a positive chat.
                            if ((s.Id.GetHashCode() ^ partner.Id.GetHashCode() & 0xF) == 0)
                            {
                                s.Preferences?.Befriend(partner.Name);
                                partner.Preferences?.Befriend(s.Name);
                            }
                        }
                    }
                    break;
                }

                case TaskType.Meditate:
                    // Mage-style idle: standing meditation lifts MagicResonance
                    // at a fraction of the dedicated Attune task rate so the
                    // need can keep up without making Attune redundant.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt * 0.5f);
                    ThoughtRegistry.Add(s, "Pondered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.7f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Meditate));
                    BumpJoyTolerance(s, TaskType.Meditate);
                    break;

                case TaskType.VisitFavorite:
                    // Phase 4 will route this to a remembered location; for
                    // now the activity is just a longer-distance wander
                    // with a positive memory thought on arrival.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    ThoughtRegistry.Add(s, "VisitedSpot");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 1.2f
                        * JoyToleranceMul(s, TaskType.VisitFavorite));
                    BumpJoyTolerance(s, TaskType.VisitFavorite);
                    break;

                // v0.4.0 — Phase-5-deferred task stubs. Both are reachable
                // through the Jobs tab today (the player can set their
                // Haul / Cook priorities) but neither has a workplace yet:
                //   Haul → needs Phase 5 stockpile zones to know where to
                //          carry the item. Without a destination tile the
                //          stub clears the task and the shroomp re-evaluates.
                //   Cook → needs Phase 5 Kitchen building plus the
                //          Raw → Prepared food taxonomy. Same no-op
                //          behaviour for now.
                // HaulSystem.cs / CookSystem.cs hold the actual work-flow
                // skeletons + the data structures Phase 5 will plug into.
                case TaskType.Haul:
                    // v0.4.2 — HaulSystem.Apply manages CurrentTask
                    // itself (sets the deliver task after pickup; nulls
                    // on completion / failure). Don't clobber it here.
                    HaulSystem.Apply(s, t, map, r);
                    break;
                case TaskType.Cook:
                    CookSystem.Apply(s, t, map, r);
                    s.CurrentTask = null;
                    break;
                // v0.5.84s — Phase 5.5 bills dispatch.
                case TaskType.DoBill:
                    BillSystem.Apply(s, t, map, r);
                    // BillSystem.Apply manages CurrentTask itself: keeps
                    // the task across multiple Apply ticks until ProgressTicks
                    // hits the recipe's WorkTicks, then nulls.
                    break;

                case TaskType.BuildHaul:
                    // v0.5.60 — RimWorld-parity "WorkGiver_ConstructDeliver
                    // Resources" equivalent. Gated by Haul priority (any
                    // role can deliver materials). Stages:
                    //   A. AT SOURCE, BuildSiteTileX/Y set → pickup matching
                    //      material into Inventory; re-route to blueprint
                    //   B. AT BLUEPRINT, carrying matching material → deposit
                    //      ONE UNIT per tick. v0.5.60 S2 drops a VISIBLE
                    //      Item on the blueprint tile (IsForbidden=true so
                    //      HaulSystem doesn't try to haul it away). Player
                    //      sees materials pile up at the build site.
                    //      MaterialsDelivered counter increments alongside.
                    //   C. AT BLUEPRINT, no matching carry, blueprint
                    //      under-supplied → abandon (SelectTask re-routes)
                    //   D. AT BLUEPRINT, supplied → done. Abandon, let a
                    //      Crafter pick up the framing via TaskType.Build.
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        bool routingFromSource = s.BuildSiteTileX >= 0 && s.BuildSiteTileY >= 0;
                        int bpTx = routingFromSource ? s.BuildSiteTileX : t.TargetTileX;
                        int bpTy = routingFromSource ? s.BuildSiteTileY : t.TargetTileY;
                        var bpSlot = map.GetStructure(bpTx, bpTy);
                        if (!bpSlot.IsBlueprint)
                        {
                            // v0.5.84t — go through ReleaseTaskClaim so the
                            // LayerBuildHaul reservation is freed + any picked-
                            // up surplus is dropped on the current tile.
                            ReleaseTaskClaim(s, map);
                            s.CurrentTask = null;
                            break;
                        }
                        byte cost = StructureSlot.BuildMaterialCost(bpSlot.Type);
                        int needed = cost - bpSlot.MaterialsDelivered;
                        if (needed <= 0)
                        {
                            // v0.5.84t — supplied by another hauler in between
                            // our pickup and arrival (pre-v0.5.84t the single-
                            // hauler reservation prevents this entirely, but
                            // keep the guard for race-condition safety).
                            // ReleaseTaskClaim drops the carried surplus so
                            // HaulSystem cleans it up rather than the shroomp
                            // riding around with it.
                            ReleaseTaskClaim(s, map);
                            s.CurrentTask = null;
                            break;
                        }
                        string family  = StructureMatMeta.ConsumeFamily(bpSlot.Material);
                        string? subType = StructureMatMeta.ConsumeSubType(bpSlot.Material);
                        // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
                        string? itemSubType = StructureMatMeta.ConsumeItemSubType(bpSlot.Material);

                        // Stage A — pickup at source
                        if (routingFromSource)
                        {
                            int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            int pickupCap = System.Math.Min(needed,
                                System.Math.Max(0, s.CarryingCapacity - s.CurrentCarriedCount));
                            if (pickupCap <= 0)
                            {
                                // Carry cap exceeded — walk to blueprint anyway
                                // to dump what (matching) material we have.
                                RetargetBuildHaulToBlueprint(s, t, map, bpTx, bpTy);
                                break;
                            }
                            int taken = map.PickupDroppedAt(curTx, curTy,
                                Items.ItemKind.Material, family, subType, pickupCap, itemSubType);
                            if (taken > 0)
                            {
                                var matKey = new Items.MaterialKey(family, subType ?? "");
                                Items.Item? topUp = null;
                                foreach (var inv in s.Inventory)
                                {
                                    if (inv.Kind != Items.ItemKind.Material) continue;
                                    if (inv.Material.Family != family) continue;
                                    if (subType != null && inv.Material.SubType != subType) continue;
                                    if (itemSubType != null && inv.SubType != itemSubType) continue;
                                    topUp = inv;
                                    break;
                                }
                                if (topUp != null)
                                {
                                    topUp.Quantity += taken;
                                }
                                else
                                {
                                    s.Inventory.Add(new Items.Item
                                    {
                                        Kind     = Items.ItemKind.Material,
                                        SubType  = itemSubType ?? subType ?? "Generic",
                                        Material = matKey,
                                        Quality  = Items.Quality.Normal,
                                        State    = Items.ItemState.Fresh,
                                        Quantity = taken,
                                        OwnerShroompId = s.Id,
                                    });
                                }
                                SkillRegistry.GainXp(s, "Construction", 4f);
                            }
                            RetargetBuildHaulToBlueprint(s, t, map, bpTx, bpTy);
                            break;
                        }

                        // Stage B — deposit at blueprint
                        if (ConsumeOneFromShroompInventory(s, bpSlot.Material))
                        {
                            bpSlot.MaterialsDelivered++;
                            map.SetStructure(bpTx, bpTy, bpSlot);
                            // v0.5.60 S2 — drop visible material item ON the
                            // blueprint tile. Player sees the deposit pile up.
                            // IsForbidden=true so HaulSystem doesn't try to
                            // haul these back to a stockpile (matches RimWorld
                            // Frame.resourceContainer behaviour — materials
                            // belong to the frame, not the haul pool).
                            var depositItem = new Items.Item
                            {
                                Kind     = Items.ItemKind.Material,
                                SubType  = subType ?? "Generic",
                                Material = new Items.MaterialKey(family, subType ?? ""),
                                Quality  = Items.Quality.Normal,
                                State    = Items.ItemState.Fresh,
                                Quantity = 1,
                                TilePos  = new Vector2(
                                    bpTx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                    bpTy * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                                IsForbidden = true,
                            };
                            map.DropItem(depositItem);
                            s.TaskDidWork = true;
                            // If still under-supplied AND still carrying → keep
                            // depositing next tick (don't clear task).
                            // If carry depleted AND still under-supplied → fall
                            // through to clear task; SelectTask will re-route
                            // to a fresh source.
                            if (bpSlot.MaterialsDelivered >= cost
                                || !ShroompCarriesMatchingBuildMaterial(s, bpSlot.Material))
                            {
                                // v0.5.84t — release LayerBuildHaul + drop
                                // any leftover carried surplus to current tile.
                                ReleaseTaskClaim(s, map);
                                s.CurrentTask = null;
                            }
                            break;
                        }
                        // No matching carry — abandon, SelectTask routes again.
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = null;
                    }
                    else
                    {
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = null;
                    }
                    break;

                case TaskType.Build:
                    // v0.5.60 — Build is now FRAMING ONLY. Hauling moved to
                    // TaskType.BuildHaul (gated by Haul priority, any role).
                    // Build is gated by Construct priority (Crafter preferred).
                    // Stages:
                    //   D. Tick BuildProgress per tick by SkillCurve factor
                    //   E. On BuildProgress >= target: complete, consume all
                    //      deposited material items on the tile, flip
                    //      blueprint → built, roll Quality, release claim
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        int bpTx = t.TargetTileX;
                        int bpTy = t.TargetTileY;
                        var bpSlot = map.GetStructure(bpTx, bpTy);
                        if (!bpSlot.IsBlueprint && !bpSlot.IsBuilt)
                        {
                            s.BuildSiteTileX = -1;
                            s.BuildSiteTileY = -1;
                            s.CurrentTask = null;
                            break;
                        }

                        // Defensive: if blueprint isn't fully supplied (race —
                        // a hauler abandoned with partial delivery), abandon
                        // framing so a BuildHaul task gets re-issued.
                        if (bpSlot.IsBlueprint)
                        {
                            byte cost = StructureSlot.BuildMaterialCost(bpSlot.Type);
                            if (bpSlot.MaterialsDelivered < cost)
                            {
                                s.CurrentTask = null;
                                break;
                            }
                            // Stage D framing.
                            // v0.5.30 — RimWorld-parity Construction curve.
                            // delta = 10 × ConstructionSpeedFactor(skill)
                            // — lvl 0 → 3/tick (~3.3s to 600), lvl 8 →
                            // 10/tick (~1s), lvl 20 → 20/tick (~0.5s).
                            // The ~6.7× spread matches RimWorld's published
                            // Construction Speed table (0.30 → 2.05).
                            int builderSkill = SkillLevel(s, "Construction");
                            // v0.5.84t — apply tool bonus (Hammer with Build).
                            float buildToolBonus = GetToolBonusFor(s, TaskType.Build);
                            int delta = Mathf.Max(1, (int)(10f * SkillCurve.ConstructionSpeedFactor(builderSkill) * buildToolBonus));
                            int newProg = bpSlot.BuildProgress + delta;
                            if (newProg < StructureSlot.BuildProgressTarget)
                            {
                                bpSlot.BuildProgress = (ushort)newProg;
                                map.SetStructure(t.TargetTileX, t.TargetTileY, bpSlot);
                                // Don't clear CurrentTask — stay on the build
                                // for more ticks. Shroomp already at the tile;
                                // ApplyTaskEffect re-fires next tick.
                            }
                            else
                            {
                                // Stage 3: complete.
                                var built = bpSlot;
                                built.Type = bpSlot.Type switch
                                {
                                    StructureType.WallPlanned       => StructureType.Wall,
                                    StructureType.DoorPlanned       => StructureType.Door,
                                    StructureType.ShelfPlanned      => StructureType.Shelf,       // v0.5.21
                                    StructureType.WorkbenchPlanned  => StructureType.Workbench,   // v0.5.22
                                    StructureType.HearthPlanned     => StructureType.Hearth,      // v0.5.24
                                    StructureType.BedPlanned        => StructureType.Bed,         // v0.5.35
                                    StructureType.MeditationShrinePlanned => StructureType.MeditationShrine,   // v0.5.36
                                    StructureType.ShroomBoardPlanned      => StructureType.ShroomBoard,        // v0.5.36
                                    StructureType.GossipBenchPlanned      => StructureType.GossipBench,        // v0.5.36
                                    StructureType.TablePlanned      => StructureType.Table,       // v0.5.37
                                    _                               => StructureType.Floor,       // FloorPlanned + safety default
                                };
                                built.BuildProgress = StructureSlot.BuildProgressTarget;
                                // v0.5.30 — roll Quality from Construction
                                // skill at completion. SkillCurve.Roll
                                // StructureQuality returns Crude/Normal/
                                // Fine/Superior/Masterwork (Legendary
                                // reserved for inspired-creativity events).
                                // Quality drives BeautyScore + future
                                // bed RestEffectiveness + tooltip display.
                                built.Quality = SkillCurve.RollStructureQuality(builderSkill, rng);
                                map.SetStructure(t.TargetTileX, t.TargetTileY, built);
                                // v0.5.60 S2 — consume the visible deposited
                                // material items off the blueprint tile.
                                // Pre-v0.5.60 materials disappeared into the
                                // MaterialsDelivered counter on first delivery;
                                // now they sit visibly on the tile until
                                // completion, then get consumed (fold into
                                // the structure). RimWorld pattern: frame.
                                // resourceContainer is emptied on
                                // CompleteConstruction.
                                string famConsume = StructureMatMeta.ConsumeFamily(built.Material);
                                string? subConsume = StructureMatMeta.ConsumeSubType(built.Material);
                                // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
                                string? itemSubConsume = StructureMatMeta.ConsumeItemSubType(built.Material);
                                byte consumeCount = StructureSlot.BuildMaterialCost(built.Type);
                                map.PickupDroppedAt(t.TargetTileX, t.TargetTileY,
                                    Items.ItemKind.Material, famConsume, subConsume, consumeCount, itemSubConsume);
                                // v0.5.84t — unforbid any leftover dropped
                                // material on the just-built tile so HaulSystem
                                // can move it to a stockpile. Without this,
                                // over-deposit from pre-v0.5.84t multi-hauler
                                // races (or from legacy saves) stays forbidden
                                // forever on the built tile.
                                map.UnforbidDroppedAt(t.TargetTileX, t.TargetTileY);
                                s.TaskDidWork = true;
                                SkillRegistry.GainXp(s, "Construction", 80f);
                                // v0.5.59 — release the blueprint claim on completion.
                                map.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
                                s.BuildSiteTileX = -1;
                                s.BuildSiteTileY = -1;
                                s.CurrentTask = null;
                            }
                        }
                        else
                        {
                            // Blueprint vanished (demolished mid-build) — abandon.
                            s.CurrentTask = null;
                        }
                    }
                    else
                    {
                        s.CurrentTask = null;
                    }
                    break;

                case TaskType.Wander:
                    // v0.4.63 (G4) — basic idle wander gives modest Joy.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.5f
                        * JoyToleranceMul(s, TaskType.Wander));
                    BumpJoyTolerance(s, TaskType.Wander);
                    // v0.5.5 — multi-hop chain. If WanderHopsRemaining > 0,
                    // pick a fresh destination, swap it into CurrentTask,
                    // reset arrival state so the shroomp walks again, and
                    // bump WorkSearchCooldownTicks high enough to cover
                    // the next leg's walk + linger (so the chain isn't
                    // interrupted by a workAvailable re-eval mid-leg).
                    // Pathfind the new destination immediately so the
                    // section-2a A* gate doesn't need to re-fire.
                    //
                    // Sam: "a shroomp should actually take a short walk and
                    // finish it when 'taking a walk'." A 2-4 leg chain at
                    // 8-28 tiles per leg gives ~10-25 sec of visible
                    // wandering, matching the RimWorld feel of pawns
                    // "going for a walk" between work shifts.
                    // v0.5.79 — break the auto-chain when the shroomp has
                    // a more pressing need or a pending player order.
                    // Pre-v0.5.79 the chain ran unconditionally → night-
                    // sleep-window shroomps with Rest=19 kept wandering
                    // through the night (Sam screenshot: Ethan, Elder,
                    // Rest=19 at hour 23, Wandering); right-click move
                    // orders queued in MoveOrderQueue stayed queued until
                    // the chain naturally exhausted because the section-
                    // 2a re-eval skipped the chain-pop block whenever
                    // s.CurrentTask was non-null (Wander auto-chain kept
                    // it non-null indefinitely).
                    //
                    // Three break conditions:
                    //   1. Life-threatening need (Nutrition<5 / Rest<5)
                    //   2. Pending player move order in MoveOrderQueue
                    //   3. Night-sleep gate fires (in-window + Rest<80)
                    //      — same condition SelectTask Tier-1 line 2118
                    //      uses to enqueue a high-priority Sleep, so the
                    //      auto-chain ducking out here lets that branch
                    //      take over on the next SelectTask iteration.
                    bool breakChain = IsLifeThreatening(s)
                        || s.MoveOrderQueue.Count > 0;
                    if (!breakChain && s.WanderHopsRemaining > 0)
                    {
                        // Night-sleep check (matches SelectTask line 2118)
                        bool nightOwlW = HasPersonality(s, "Night Owl");
                        int hr = _currentHourOfDay;
                        bool sleepWin = nightOwlW
                            ? (hr >= 10 && hr < 18)
                            : (hr >= 22 || hr <  6);
                        if (sleepWin && s.Rest < 80f) breakChain = true;
                    }
                    if (breakChain)
                    {
                        // Drop the current Wander task + remaining chain.
                        // Section-2a's needNewTask gate fires on the next
                        // tick (CurrentTask == None) and SelectTask picks
                        // the appropriate high-priority response.
                        s.CurrentTask = null;
                        s.PathWaypoints.Clear();
                        s.WanderHopsRemaining = 0;
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                        s.WorkSearchCooldownTicks = 0;
                        break;
                    }

                    if (s.WanderHopsRemaining > 0)
                    {
                        s.WanderHopsRemaining--;
                        var nextHop = PickIdleDestination(s.SimPos, map, rng,
                            TaskType.Wander, LingerWander, 8, 28);
                        s.CurrentTask = nextHop;
                        s.SimTarget = nextHop.Target;
                        s.PathWaypoints.Clear();
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                        s.StuckTicks = 0;
                        s.RePathTried = false;
                        // ~6 sec — enough for a max-radius 28-tile leg
                        // (≈ 4-5 sec walk) plus the post-arrival linger
                        // before the next chain decision. Keeps
                        // workAvailable suppressed so the leg isn't
                        // interrupted, but doesn't prevent re-eval forever.
                        s.WorkSearchCooldownTicks = 360;
                        if (map != null && nextHop.TargetTileX >= 0 && nextHop.TargetTileY >= 0)
                        {
                            Pathfinder.FindPath(map, s.SimPos,
                                (nextHop.TargetTileX, nextHop.TargetTileY),
                                s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
                        }
                    }
                    break;
                case TaskType.None:
                case TaskType.PlayerOrder:
                default:
                    // Player order: clear after arrival so next tick re-evaluates.
                    if (t.Type == TaskType.PlayerOrder) s.CurrentTask = null;
                    break;
            }
        }

        // v0.3.43 — emit a thought matching how a shroomp feels about a work
        // task they just completed. The mapping uses Preferences.LikesActivity
        // for the activity itself (Foraging / Excavating / …) and an
        // optional item-axis lookup for the specific yield (Capberry vs
        // Pineshroom). Cheap — called once per completion, not per tick.
        private static void EmitWorkThought(Shroomp s, TaskType type, string? itemName)
        {
            var prefs = s.Preferences;
            string? activity = PreferenceRegistry.ActivityNameFor(type);

            // Activity preference: liked/disliked/neither.
            if (prefs != null && activity != null)
            {
                if (prefs.LikesActivity(activity))
                    ThoughtRegistry.Add(s, "WorkedFavorite");
                else if (prefs.DislikesActivity(activity))
                    ThoughtRegistry.Add(s, "WorkedDisliked");
                else
                    ThoughtRegistry.Add(s, "Accomplished");
            }

            // Item preference, on top.
            if (prefs != null && itemName != null)
            {
                if (prefs.LikesItem(itemName))    ThoughtRegistry.Add(s, "AteFavorite", itemName);
                if (prefs.DislikesItem(itemName)) ThoughtRegistry.Add(s, "AteDisliked", itemName);
            }
        }

        // Maps the harvested vegetation type to the canonical item-pool name
        // that Preferences stores. Mirrors the strings in
        // PreferenceRegistry.ItemPool so liked-food preferences line up.
        // v0.4.2 — mapping kept for preference-aware thought emission.
        // SmallMushroom and all its variants resolve to "SmallMushroom"
        // because they're the same food sub-type with a different
        // material tag; preferences stored against "SmallMushroom" cover
        // the entire variant set.
        private static string? ItemNameFor(Sporeholm.World.VegetationType v) => v switch
        {
            Sporeholm.World.VegetationType.CapberryBush  => "Capberry",
            Sporeholm.World.VegetationType.SmallMushroom   => "SmallMushroom",
            Sporeholm.World.VegetationType.LargeMushroom   => "SmallMushroom",
            Sporeholm.World.VegetationType.HerbCluster     => "HerbCluster",
            Sporeholm.World.VegetationType.MagicFlower     => "MagicBerry",
            Sporeholm.World.VegetationType.PineShroom      => "SmallMushroom",
            Sporeholm.World.VegetationType.PalmShroom      => "SmallMushroom",
            Sporeholm.World.VegetationType.SmallSandshroom => "SmallMushroom",
            Sporeholm.World.VegetationType.LargeSandshroom => "SmallMushroom",
            _ => null,
        };

        // ── Map helpers ─────────────────────────────────────────────────────
        private static (int x, int y)? FindNearestVegetation(Vector2 from, LocalMap map)
        {
            int cx = (int)(from.X / LocalMap.TileSize);
            int cy = (int)(from.Y / LocalMap.TileSize);
            int best = int.MaxValue;
            (int x, int y)? winner = null;
            int radius = 20;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (!map.InBounds(tx, ty) || !map.IsPassable(tx, ty)) continue;
                var slot = map.GetVegetation(tx, ty);
                if (!slot.IsPresent || slot.IsDepleted) continue;
                if (VegetationSlot.BaseYield(slot.Type) == 0) continue;
                int d = dx * dx + dy * dy;
                if (d < best) { best = d; winner = (tx, ty); }
            }
            return winner;
        }

        // v0.3.33 (B.1) — replaces the 51×51 radial scan with an O(N)
        // iteration over the indexed designation set in LocalMap. Combined
        // with B.7 soft-claims (passes `s.Id` as the claimer filter), other
        // shroomps' targets are skipped so the colony spreads work across
        // available tiles instead of all rushing the same one.
        // v0.3.40 — pass the FIFO avoid array directly to the find methods.
        // LocalMap iterates the (4-slot) array per candidate designation;
        // entries with TicksLeft == 0 are inactive and skipped.
        //
        // v0.4.29 — passes an `approachBlocked` callback that consults the
        // per-tick occupancy grid. A candidate excavate target is rejected
        // when *every* passable 8-neighbour is already occupied by a
        // *different* shroomp — i.e. the only ways into the dig face are
        // currently blocked by colleagues. This prevents the cascade where
        // 5+ shroomps claim adjacent boulders in a single-tile tunnel and
        // immediately jam each other up. A target with no passable
        // neighbours at all is left to IsWorkReachable to filter (already
        // handled). A target with at least one open passable approach
        // passes through unchanged.
        private static (int x, int y)? FindDesignatedExcavation(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;

            var pos = map.FindNearestExcavate(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.4.29 — true iff every passable 8-neighbour of (tx, ty) is
        // currently occupied by some shroomp other than the caller. If there
        // are no passable neighbours at all, returns false (let
        // IsWorkReachable veto that case — the answer here is "doesn't
        // apply", not "approach is blocked"). Cheap: at most 8 array reads
        // against the per-tick occupancy grid.
        private static bool IsApproachFullyOccupied(LocalMap map, int tx, int ty, int currentShroompTileIdx)
        {
            if (_occGridWidth == 0) return false;
            int W = map.Width, H = map.Height;
            bool anyPassable = false;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
                if (!map.IsPassable(nx, ny)) continue;
                anyPassable = true;
                int nIdx = ny * W + nx;
                if (!TileHasOtherShroomp(nIdx, currentShroompTileIdx))
                    return false;   // at least one open approach → not blocked
            }
            return anyPassable;   // had passable neighbours, all occupied
        }

        // v0.5.7 — Gather / ChopWood / Cut now pass the same approach-
        // occupancy callback as FindDesignatedExcavation (v0.4.29). The
        // omission meant shroomps would claim approach-blocked Gather /
        // Chop / Cut targets even when 50 shroomps were converging on a
        // small cluster, get stuck against the occupied perimeter for the
        // full StuckThreshold window, then re-pick the same tile next
        // idle cycle (per-shroomp AvoidTiles only blacklists for one
        // shroomp, not globally). RimWorld parity — JobGiver_Work
        // surface vetoes targets whose reservation surface is fully
        // occupied.
        private static (int x, int y)? FindDesignatedGather(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestGather(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        private static (int x, int y)? FindDesignatedChopWood(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestChopWood(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        private static (int x, int y)? FindDesignatedCut(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestCut(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.5.20 (Phase 5C — rimport.md N6) — allowed-area check.
        // Returns true when (a) shroomp has no allowed-area set (default —
        // can work anywhere) OR (b) the tile coord is within the painted
        // area. Map-size match required so a saved shroomp with a
        // different-size bitmap (impossible in practice — bitmap is
        // map-bound) safely returns true rather than blocking everything.
        private static bool IsTileInAllowedArea(Shroomp s, (int x, int y) tile, LocalMap map)
        {
            // v0.5.44 — RimWorld-parity area gate. Reads the colony-shared
            // NamedAreas bitmap rather than the deprecated per-shroomp
            // AllowedArea (kept on Shroomp for save-load back-compat only).
            // Null AssignedAreaName = unrestricted (no spatial restriction).
            if (s.AssignedAreaName != null)
            {
                if ((uint)tile.x >= (uint)map.Width || (uint)tile.y >= (uint)map.Height) return false;
                return map.AreaContains(s.AssignedAreaName, tile.x, tile.y);
            }
            // Legacy per-shroomp bitmap path. v0.5.25 → v0.5.43; ignored
            // after v0.5.44 unless save-loaded with a populated bitmap and
            // the player hasn't assigned a named area yet.
            if (s.AllowedArea == null) return true;
            if (s.AllowedAreaWidth != map.Width) return true;   // mismatch → bail to default
            if ((uint)tile.x >= (uint)map.Width || (uint)tile.y >= (uint)map.Height) return false;
            int idx = tile.y * map.Width + tile.x;
            if ((uint)idx >= (uint)s.AllowedArea.Length) return false;
            return s.AllowedArea[idx];
        }

        // v0.5.36 — Joy-multiplier helper used by all idle ApplyTaskEffect
        // cases. Returns 1.5× when the shroomp stands ON or ADJACENT to any
        // built Joy furniture (MeditationShrine / ShroomBoard /
        // GossipBench); else 1.0×. RimWorld pattern: recreation furniture
        // grants higher per-tick Joy than freelance idle. The 8-tile
        // neighbourhood lookup is cheap (max 9 GetStructure calls) and
        // only runs while a shroomp is mid-idle.
        private static float JoyFurnitureMul(Shroomp s, LocalMap? map)
        {
            if (map == null) return 1f;
            int tx = (int)(s.SimPos.X / LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = tx + dx, ny = ty + dy;
                if (!map.InBounds(nx, ny)) continue;
                var st = map.GetStructure(nx, ny).Type;
                if (st == StructureType.MeditationShrine ||
                    st == StructureType.ShroomBoard ||
                    st == StructureType.GossipBench)
                    return 1.5f;
            }
            return 1f;
        }

        // v0.5.31 — picks the right clearing TaskType for an obstructed
        // blueprint tile. Returns null when the tile is already build-ready
        // (caller should fall through to normal Build dispatch). The
        // Build branch in SelectTask uses this to redirect a Crafter to
        // do the prep work themselves, bypassing the normal Mine / Cut
        // priority gates — clearing is part of the construction job.
        //
        //   Impassable terrain → GatherMaterial (Excavate apply path).
        //                       The Excavate ApplyTaskEffect already
        //                       handles Boulder / DeadLog / LivingWood
        //                       / Skeleton drops + terrain mutation.
        //   Non-depleted veg   → CutVegetation (clears the tile + drops
        //                       Cuttings; FullyDepleteVegetation handles
        //                       tree-class passability flip).
        //   Otherwise           → null (tile is build-ready).
        private static TaskType? ResolveBlueprintClearTask(LocalMap map, int x, int y)
        {
            var terrain = map.Get(x, y).Terrain;
            if (terrain == TerrainType.Boulder ||
                terrain == TerrainType.DeadLog ||
                terrain == TerrainType.LivingWood ||
                terrain == TerrainType.Skeleton)
                return TaskType.GatherMaterial;
            var veg = map.GetVegetation(x, y);
            if (veg.IsPresent && !veg.IsDepleted)
                return TaskType.CutVegetation;
            return null;
        }

        // v0.5.19 (Phase 5B) — blueprint find for the Build task. Same
        // approach-blocked filter parity as the v0.5.7 Gather/Chop/Cut
        // pass so Crafters don't claim blueprints whose only adjacent
        // tiles are occupied by other shroomps.
        private static (int x, int y)? FindDesignatedBuild(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestBlueprint(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.5.84t — haul-side blueprint pick. Skips blueprints already
        // reserved by another shroomp on the BuildHaul layer so we don't
        // get the v0.5.84 over-supply + conga-line bug. RimWorld parity:
        // each blueprint accepts one delivery convoy at a time (the
        // hauler may make multiple trips if the cost exceeds carry cap,
        // but other shroomps wait for the reservation to clear).
        private static (int x, int y)? FindDesignatedBuildForHaul(Shroomp s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestBlueprint(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx),
                extraLayer: Sporeholm.Simulation.ReservationManager.LayerBuildHaul);
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.3.33 (B.7) — builds a designation task AND records the soft-
        // claim on the tile so other shroomps scanning won't try to pick the
        // same target. Released when:
        //   • The shroomp completes the task (ApplyTaskEffect → ClearDesignationsAt
        //     auto-releases via the lock in LocalMap).
        //   • The shroomp is forced into re-evaluation (Wander, critical need).
        //   • The shroomp gives up after StuckThreshold ticks of zero progress.
        //   • The player removes the designation via the Remove tool.
        private static BehaviorTask ClaimAndMakeDesignationTask(
            Shroomp s, LocalMap map, TaskType type, (int x, int y) tile, float priority)
        {
            map.TryClaim(tile.x, tile.y, s.Id);
            return new BehaviorTask(type, TileToPixel(tile), priority,
                tileX: tile.x, tileY: tile.y);
        }

        // Releases the shroomp's claim on the current task's target tile, if
        // the task is a designation type. Called whenever the shroomp abandons
        // or replaces its current task. Safe to call when CurrentTask is
        // null or not a designation type — just no-ops.
        private static void ReleaseTaskClaim(Shroomp s, LocalMap? map)
        {
            if (s.CurrentTask is not { } t) return;

            // v0.4.7 (bugreport B-3) — release haul reservations on
            // task abandonment. Without this, the per-item reservation
            // dict in HaulSystem leaked an entry every time a shroomp
            // got pulled off a Haul (critical need, stuck-detector,
            // player re-order) — eventually every dropped item was
            // "reserved" by long-departed haulers and the colony
            // stopped hauling.
            if (t.Type == TaskType.Haul && t.TargetId != null)
            {
                HaulSystem.ReleaseByIdString(t.TargetId);
            }
            // v0.5.82 — release the haul-destination cell reservation
            // claimed by PickDeliveryTileFor. The check `TargetId == null`
            // distinguishes Phase 2 deliveries (no item id, target is the
            // drop tile) from Phase 1 pickups (item id set, target is the
            // pickup tile — that one releases via the line above).
            if (t.Type == TaskType.Haul && t.TargetId == null
                && t.TargetTileX >= 0 && t.TargetTileY >= 0)
            {
                Sporeholm.Simulation.ReservationManager.Active?.ReleaseTile(
                    t.TargetTileX, t.TargetTileY,
                    Sporeholm.Simulation.ReservationManager.LayerHaul, s.Id);
            }

            if (map == null) return;
            // v0.3.38 — extended to release claims on the new Chop Wood /
            // Cut Plants task types too.
            // v0.5.59 — Build claim release. Pre-v0.5.59 Build was missing
            // from this list — when a shroomp abandoned a Build task (Stage C
            // material-not-available fallback, stuck-give-up, critical-need
            // preemption), the blueprint claim leaked. Other Crafters then
            // saw the blueprint as "claimed by ghost shroomp" via
            // FindNearestBlueprint's `owner != claimerId` filter and
            // skipped it forever. The leaking shroomp could re-pick the
            // blueprint (owner == own Id), but no one else could help,
            // and if the leaker shifted to other work the blueprint sat
            // unbuilt indefinitely. Sam: "deliver resources infinitely
            // to blueprints that never get built." Build tasks claim the
            // BLUEPRINT, but in the v0.5.57 haul-from-source flow
            // `t.TargetTileX/Y` is the SOURCE tile while
            // `s.BuildSiteTileX/Y` is the actual blueprint — release the
            // BuildSite tile when set, otherwise the task target.
            if (t.Type == TaskType.Build)
            {
                int relX = s.BuildSiteTileX >= 0 ? s.BuildSiteTileX : t.TargetTileX;
                int relY = s.BuildSiteTileY >= 0 ? s.BuildSiteTileY : t.TargetTileY;
                if (relX >= 0 && relY >= 0) map.ReleaseClaim(relX, relY, s.Id);
                s.BuildSiteTileX = -1;
                s.BuildSiteTileY = -1;
                return;
            }
            // v0.5.84t — BuildHaul claims the blueprint on the
            // LayerBuildHaul reservation (single-hauler per blueprint).
            // Release on every abandon path. The blueprint tile is in
            // BuildSiteTileX/Y during Stage A (routing to source) and in
            // t.TargetTileX/Y during Stage B (at blueprint). v0.5.60 used
            // BuildSiteTileX/Y as the "return to blueprint after pickup"
            // pointer; the same field tells us which tile to release.
            // Also drop surplus carried material on the current tile as
            // unforbidden so HaulSystem hauls it to a stockpile instead
            // of the shroomp riding around with the surplus forever.
            if (t.Type == TaskType.BuildHaul)
            {
                int relX = s.BuildSiteTileX >= 0 ? s.BuildSiteTileX : t.TargetTileX;
                int relY = s.BuildSiteTileY >= 0 ? s.BuildSiteTileY : t.TargetTileY;
                if (relX >= 0 && relY >= 0)
                {
                    Sporeholm.Simulation.ReservationManager.Active?.ReleaseTile(
                        relX, relY,
                        Sporeholm.Simulation.ReservationManager.LayerBuildHaul, s.Id);
                }
                // Drop any carried Material the shroomp picked up for this
                // BuildHaul. Without this, the surplus stays in inventory
                // and the shroomp tries to re-deliver it on the next
                // BuildHaul (the conga line). Drop unforbidden so HaulSystem
                // picks it up to a stockpile.
                if (map != null && s.Inventory != null && s.Inventory.Count > 0)
                {
                    int dropTx = (int)(s.SimPos.X / LocalMap.TileSize);
                    int dropTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                    for (int i = s.Inventory.Count - 1; i >= 0; i--)
                    {
                        var it = s.Inventory[i];
                        if (it.Quantity <= 0) { s.Inventory.RemoveAt(i); continue; }
                        // Only drop Material items — keep tools/apparel/etc.
                        if (it.Kind != Items.ItemKind.Material) continue;
                        var drop = new Items.Item
                        {
                            Kind     = it.Kind,
                            SubType  = it.SubType,
                            Material = it.Material,
                            Quality  = it.Quality,
                            State    = it.State,
                            Quantity = it.Quantity,
                            TilePos  = new Vector2(
                                dropTx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                dropTy * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                            IsForbidden = false,
                        };
                        map.DropItem(drop);
                        s.Inventory.RemoveAt(i);
                    }
                }
                s.BuildSiteTileX = -1;
                s.BuildSiteTileY = -1;
                return;
            }
            if (t.Type != TaskType.GatherFood && t.Type != TaskType.GatherMaterial
                && t.Type != TaskType.ChopWood && t.Type != TaskType.CutVegetation) return;
            if (t.TargetTileX < 0 || t.TargetTileY < 0) return;
            map.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
        }

        private static Vector2 TileToPixel((int x, int y) t) =>
            new(t.x * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                t.y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);

        private static bool IsPixelPassable(LocalMap map, Vector2 px)
        {
            int tx = (int)(px.X / LocalMap.TileSize);
            int ty = (int)(px.Y / LocalMap.TileSize);
            return map.IsPassable(tx, ty);
        }

        // v0.5.77 — step-level passability check that enforces the same
        // no-corner-cutting rule the A* Pathfinder applies on its
        // 8-connected graph (Pathfinder.cs line 187-191). Pre-v0.5.77 local
        // steering's IsPixelPassable alone missed this case: a diagonal
        // step from tile (x,y) → (x+1,y+1) returned true when only the
        // destination was passable, even if both orthogonals (x+1,y) AND
        // (x,y+1) were walls. The shroomp visually cut through the wall
        // corner. Sam: "Ensure smurfs never attempt to path through
        // impassable tiles and instead always path around them."
        //
        // Rule: a diagonal step requires BOTH orthogonal tiles to be
        // passable (matching the A* graph so steering can't take a step
        // the planner refused to plan). Cardinal steps and same-tile
        // movement skip the orthogonal check.
        private static bool IsStepPassable(LocalMap map, Vector2 fromPx, Vector2 toPx)
        {
            int toTx = (int)(toPx.X / LocalMap.TileSize);
            int toTy = (int)(toPx.Y / LocalMap.TileSize);
            if (!map.IsPassable(toTx, toTy)) return false;

            int fromTx = (int)(fromPx.X / LocalMap.TileSize);
            int fromTy = (int)(fromPx.Y / LocalMap.TileSize);
            if (toTx == fromTx || toTy == fromTy) return true;   // cardinal or same-tile

            // Diagonal — both orthogonals must be passable.
            if (!map.IsPassable(toTx,   fromTy)) return false;
            if (!map.IsPassable(fromTx, toTy  )) return false;
            return true;
        }

        // NudgeToPassable removed v0.3.22 — its only call site was the
        // "everything is blocked" branch in the inline movement code, which
        // teleported the shroomp up to one tile away. The new MoveOneTick stays
        // put when fully blocked and rescues bad SimPos via
        // NearestPassableTileCentre (BFS, not random sampling).

        private static float FoodYield(VegetationType type) => type switch
        {
            VegetationType.CapberryBush  => 3f,
            VegetationType.SmallMushroom   => 2f,
            VegetationType.LargeMushroom   => 6f,
            VegetationType.HerbCluster     => 2f,
            VegetationType.MagicFlower     => 1f,
            VegetationType.SmallSandshroom => 1.5f,
            VegetationType.LargeSandshroom => 5f,
            VegetationType.PalmShroom      => 4f,
            VegetationType.PineShroom      => 3f,
            _                              => 0f,
        };

        // v0.3.38 — Fungal-Wood yield per harvest for wood-yielding shrooms.
        // The Chop Wood task type calls this; values mirror the BaseYield
        // table in VegetationSlot scaled to match GatherMaterial wood yields
        // (Boulder = 4 Stone, DeadLog = 4 Wood — wood shrooms drop between
        // 3–5 to align with their per-cap output before depletion).
        private static float WoodYield(VegetationType type) => type switch
        {
            VegetationType.LargeMushroom    => 5f,
            VegetationType.LargeSandshroom  => 4f,
            VegetationType.PalmShroom       => 3f,
            _                               => 0f,
        };

        // v0.3.43 — small signed bump applied on top of role priority based
        // on the shroomp's activity preference. +8 for liked, -6 for disliked,
        // 0 neutral. The asymmetric magnitudes mirror RimWorld's "liked
        // activities resist interruption more strongly than disliked ones
        // are avoided" pattern — work still has to get done.
        private static float PreferenceTilt(Shroomp s, string activity)
        {
            var prefs = s.Preferences;
            if (prefs == null) return 0f;
            if (prefs.LikesActivity   (activity)) return +8f;
            if (prefs.DislikesActivity(activity)) return -6f;
            return 0f;
        }

        // v0.3.46 (Phase 4) — skill lookup used by ItemFactory.Create to
        // shift the quality bell upward for a shroomp with relevant
        // training. Returns 0 if the shroomp hasn't learned this skill.
        private static int SkillLevel(Shroomp s, string skill) =>
            s.Skills != null && s.Skills.TryGetValue(skill, out int lvl) ? lvl : 0;

        private static bool HasTrait(Shroomp s, string trait) =>
            s.Traits.TryGetValue(trait, out float v) && v >= 0.5f;

        private static bool HasPersonality(Shroomp s, string trait) =>
            s.Personality.Contains(trait);
    }

    // Roadmap §3.9 — player order envelope queued from the main thread.
    public readonly struct PlayerOrder
    {
        public readonly string  ShroompName;
        public readonly Vector2 Target;
        public PlayerOrder(string shroompName, Vector2 target)
        { ShroompName = shroompName; Target = target; }
    }
}
