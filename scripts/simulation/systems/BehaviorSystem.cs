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
    public static partial class BehaviorSystem
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

                case TaskType.Train:
                    // v0.7.2 review fix — the training building must still exist.
                    // If the Sparring Yard / Training Dummy is demolished while a
                    // shroomp walks to it or drills on it, drop the task so it
                    // re-selects instead of "training" an empty tile (which would
                    // grant free Melee XP at thin air).
                    if (t.TargetTileX < 0 || t.TargetTileY < 0) return true;
                    var trainType = map.GetStructure(t.TargetTileX, t.TargetTileY).Type;
                    return trainType == StructureType.SparringYard
                        || trainType == StructureType.TrainingDummy;

                case TaskType.Patrol:
                    // v0.7.3 — patrol validity is managed by the patrol pass
                    // (which owns its own movement + waypoint cycling).
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
        // v0.7.0 (Phase 7) — entity roster for this tick, for combat target
        // search + pursuit. Sim-thread-only scratch, set at the top of Tick
        // (mirrors _currentTick / _currentHourOfDay).
        private static IReadOnlyList<Entities.Entity> _entities =
            System.Array.Empty<Entities.Entity>();
        // v0.7.0 — true when any entity this tick is Hostile or actively Hunting,
        // so the per-shroomp auto-engage scan is skipped entirely in peacetime.
        private static bool _combatHasHostiles = false;
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
            // v0.7.1 — pain can also knock a colonist unconscious (Pain > 90),
            // with hysteresis (must drop below 80 to come round) so they don't
            // flicker awake right at the threshold.
            float pain = s.ComputePain();
            if (!s.IsDowned && (h < downAt || pain > 90f))
            {
                s.IsDowned = true;
                ThoughtRegistry.Add(s, "Downed");
            }
            else if (s.IsDowned && h >= standAt && pain < 80f)
            {
                s.IsDowned = false;
                ThoughtRegistry.Add(s, "StoodBackUp");
            }
        }

        public static void Tick(IReadOnlyList<Shroomp> shroomps,
            IReadOnlyList<Entities.Entity> entities, LocalMap? map,
            ColonyResources resources, Queue<PlayerOrder>? pendingOrders,
            Random rng, float dtSeconds, long currentTick = 0, int hourOfDay = 12)
        {
            _currentHourOfDay = hourOfDay;
            _currentTick      = currentTick;   // v0.5.82 — pawn-blocked repath cooldown
            _entities         = entities ?? System.Array.Empty<Entities.Entity>();   // v0.7.0
            _combatHasHostiles = AnyCombatHostile(_entities);                        // v0.7.0
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
                    if (s.CombatTargetName == "enemy") s.CombatTargetName = null;   // v0.7.0 — drop ⚔ while downed
                    // v0.7.2 — a downed colonist can't carry anyone: release its
                    // rescue link so the would-be rescuee isn't left frozen.
                    if (s.CarriedShroompId.HasValue)
                        ReleaseCarry(s, shroomps, s.SimPos);
                    continue;   // skip the rest of the per-shroomp tick
                }

                // v0.7.2 review fix — a shroomp currently being CARRIED (rescue)
                // must not run its own movement / task pipeline; the carrier
                // drives its SimPos. Guard regardless of downed state: a victim
                // that recovers (stands up) mid-carry would otherwise become a
                // second writer of its own position, fighting the carrier's drag.
                // (The carrier releases the link in TryHandleRescue once the
                // victim is no longer downed.) UpdateDownedState already ran above
                // so the victim's IsDowned stays current for that release check.
                if (s.IsBeingCarried)
                {
                    s.PrevSimPos = s.SimPos;
                    continue;
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
                // v0.8.1 — per-prey hunt give-up cooldown (see FindNearestHuntTarget).
                if (s.RecentHuntGiveUpTicks > 0)
                {
                    s.RecentHuntGiveUpTicks -= tickInterval;
                    if (s.RecentHuntGiveUpTicks <= 0) s.RecentHuntGiveUp = null;
                }

                // v0.8.1 (Phase 8) — Hunt acquisition. A colonist with the Hunt
                // job (armed, non-pacifist) and no current combat target locks
                // onto the nearest reachable player-marked huntable creature. The
                // combat pass below then pursues + kills it through the Phase 7
                // engine — no separate hunt mover needed. (Skipped when a player
                // attack order or auto-defense already set CombatTargetId.)
                if (s.CombatTargetId == null && !s.IsPacifist && JobPriorityOn(s, "Hunt")
                    && s.EquippedWeapon is { } hw
                    && hw.State != Sporeholm.Simulation.Items.ItemState.Broken)
                {
                    var prey = FindNearestHuntTarget(s, map);
                    if (prey != null) { s.CombatTargetId = prey.Id; s.CombatTargetName = "enemy"; }
                }

                // v0.7.0 (Phase 7) — combat pass. Tick down the attack cooldown,
                // then (if a hostile is ordered-targeted or auto-acquired in
                // range) pursue + strike, or flee if pacifist / badly wounded.
                // When engaged this owns the shroomp's tick — skip the normal
                // need/work/idle pipeline below.
                if (s.AttackCooldownTicks > 0)
                    s.AttackCooldownTicks -= tickInterval;
                if (TryHandleCombat(s, map, effectiveDt, rng))
                {
                    // v0.7.2 review fix (critical) — a carrier pulled into combat
                    // must drop its rescue carry, else the downed victim is left
                    // frozen, untreatable, and un-rescuable for the whole fight.
                    // Set them down where they were being dragged so another
                    // rescuer / doctor can take over.
                    if (s.CarriedShroompId.HasValue)
                        ReleaseCarry(s, shroomps, s.SimPos);
                    continue;
                }

                // v0.7.2 (Phase 7) — rescue pass: carry a downed colonist to a
                // bed. After combat (a rescuer under attack defends first),
                // before the medical pass (so the wounded get moved to safety
                // before being tended in place).
                if (TryHandleRescue(s, map, shroomps, effectiveDt))
                    continue;

                // v0.7.1 (Phase 7) — medical pass: a Doctor / Caretaker tends the
                // wounded in place (consuming medicine if available). Runs after
                // combat (a doctor under attack defends first), before the normal
                // work / idle pipeline.
                if (TryHandleMedical(s, map, shroomps, resources, effectiveDt))
                    continue;

                // v0.7.3 (N8) — mental break. A shroomp whose mood has collapsed
                // may lose player control for a spell — it overrides draft /
                // patrol / work / idle, but runs AFTER combat/rescue/medical so a
                // breaking colonist still defends itself and the wounded are
                // handled first. Tick an active break; otherwise roll to start
                // one while mood is in the Breaking/Collapse band and no life
                // threat is pulling at them (Eat/Sleep take priority).
                if (s.MentalBreakCooldown > 0) s.MentalBreakCooldown -= tickInterval;
                if (s.MentalBreakTicks > 0)
                {
                    TickMentalBreak(s, map, effectiveDt, rng, tickInterval);
                    continue;
                }
                if (s.MentalBreakCooldown <= 0
                    && (s.MoodState == MoodState.Breaking || s.MoodState == MoodState.Collapse)
                    && !IsLifeThreatening(s)
                    && rng.NextDouble() < MentalBreakChancePerTick * tickInterval)
                {
                    StartMentalBreak(s, map, rng);
                    continue;
                }

                // v0.7.1 (Phase 7) — drafted hold. A drafted colonist that isn't
                // fighting or treating, has no active player order, and isn't in a
                // critical state just stands its post: set a one-time idle "hold"
                // and skip the work/equip pipeline (no per-tick churn or tool-drop).
                if (s.IsDrafted
                    && !(s.CurrentTask is { Type: TaskType.PlayerOrder })
                    && s.Nutrition >= 20f && s.Rest >= 15f && s.Safety >= 20f)
                {
                    if (!(s.CurrentTask is { Type: TaskType.None }))
                    {
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = new BehaviorTask(TaskType.None, s.SimPos, 0f);
                        s.PathWaypoints.Clear();
                    }
                    s.PrevSimPos = s.SimPos;
                    continue;
                }

                // v0.7.3 (N20) — patrol pass. A standing player order: loop
                // between PatrolWaypoints. Runs after combat/rescue/medical/draft
                // and BEFORE the normal work/idle pipeline, so an explicit patrol
                // suppresses autonomous work + idle (like draft) yet still yields
                // to critical needs (Nutrition<20 / Rest<15 / Safety<20 → fall
                // through so Eat/Sleep/SeekSafety run) and to an explicit move
                // order. Uses the normal MoveOneTick A* follower so long routes
                // path across walls (unlike the combat pass's direct steering).
                if (s.PatrolWaypoints.Count >= 2
                    && !(s.CurrentTask is { Type: TaskType.PlayerOrder })
                    && s.Nutrition >= 20f && s.Rest >= 15f && s.Safety >= 20f)
                {
                    if (s.PatrolIndex < 0 || s.PatrolIndex >= s.PatrolWaypoints.Count)
                        s.PatrolIndex = 0;
                    if (!(s.CurrentTask is { Type: TaskType.Patrol }))
                    {
                        if (s.CurrentTask != null) ReleaseTaskClaim(s, map);
                        AssignReachablePatrolHop(s, map);
                    }
                    bool patrolArrived = MoveOneTick(s, map, effectiveDt, rng, tickInterval);
                    if (patrolArrived)
                    {
                        s.PatrolIndex = (s.PatrolIndex + 1) % s.PatrolWaypoints.Count;
                        AssignReachablePatrolHop(s, map);
                    }
                    continue;
                }

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

                    // v0.6.2 — reset the per-task progress accumulator on
                    // every fresh assignment. Currently consumed by
                    // CookSystem auto-cook (Phase 5.6) for Bonfire-speed
                    // scaling; any future tick-accumulating per-task system
                    // (e.g. demolish, scholar study) reads + resets the
                    // same field, so the reset belongs here at the central
                    // assignment point not in every system.
                    s.TaskProgressTicks = 0;

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

    }
}
