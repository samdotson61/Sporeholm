using System;
using System.Collections.Generic;
using Godot;
using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

namespace SmurfulationC.Simulation.Systems
{
    // Roadmap §3.3 / §3.6 / §3.7 / §3.8 — Smurf behavior driver.
    //
    // Architecture overview:
    //   • Runs on the simulation thread once per sim-system interval (60 ticks).
    //   • For each living smurf: select highest-priority valid task across the
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

        // Distance (in pixels) at which a smurf is considered "at" their target.
        // Slightly larger than half a tile so movement converges cleanly.
        // v0.3.38 — bumped from 10 → 14 px. The previous radius was tight
        // enough that a smurf could be physically *inside* its target
        // adjacent tile (an 8-px-radius square) yet not register as
        // arrived, especially when steering deflections landed it at the
        // tile edge rather than centre. 14 px (≈ √2 × 10) covers the full
        // diagonal of the adjacent tile, so anywhere inside it counts as
        // arrival and ApplyTaskEffect fires. Player reported smurfs
        // "standing at their task for multiple seconds without executing";
        // most cases were this off-by-radius issue.
        private const float ArrivalRadius   = 14f;

        // ── LOD tick groups (v0.3.39 / O-H.2) ───────────────────────────────
        //
        // Off-screen smurfs don't need 60 Hz behaviour updates — the player
        // can't see micro-deltas in their position. This is the same trick
        // Songs of Syx and RimWorld use to scale to thousands of pawns.
        //
        // Three LOD bands assigned per smurf based on camera-distance:
        //   Phase 0 (Hot)  → smurfs within ~20 tiles of camera. Ticked every
        //                    sim tick. Per-step distance = SimSpeed × dt.
        //   Phase 1 (Warm) → smurfs within ~50 tiles. Ticked every 3 sim
        //                    ticks. Per-step distance = SimSpeed × 3 × dt so
        //                    the smurf covers the same total distance per
        //                    unit time as a hot smurf. Slot 0–2 distributes
        //                    fairness across the three ticks (so a 30-smurf
        //                    Warm band fires ~10 each tick, not 30 once).
        //   Phase 2 (Cold) → everything else. Ticked every 6 sim ticks.
        //                    Per-step distance = SimSpeed × 6 × dt.
        //
        // The per-step compensation matters: without it, cold smurfs would
        // visibly walk 6× slower than hot ones, which is a UX bug. Because
        // we're using the existing local-steering loop, a 6× larger step
        // would clear small obstacles in one hop and increase the "walks
        // through a wall" risk — so MoveOneTick subdivides the step into
        // 1-px-equivalent sub-steps when the tick interval is > 1.
        private const int WarmInterval = 3;
        private const int ColdInterval = 6;
        // v0.3.40 — Hot range expanded 20 → 40 tiles. The previous 20-tile
        // ring (320 px) was smaller than a default-zoom viewport (~40×25
        // tiles visible at the standard zoom level), so smurfs at the edges
        // of the visible area were ending up in the Warm band and showing
        // the LOD stutter through the camera. 40 tiles covers viewport
        // corners at zoom 1× and 2×; at lower zoom the lerp in
        // SmurfColonyView smooths what stutter remains.
        private const float HotRangePx  = 40f * 16f;     // 40 tiles = 640 px
        private const float WarmRangePx = 100f * 16f;    // 100 tiles = 1600 px

        // Called from SimulationCore.Run every ~32 ticks. Walks every alive
        // smurf, classifies by distance to `cameraFollow`, assigns phase + slot.
        // Keeping this off the per-tick hot path is fine — smurfs barely move
        // across 32 ticks at any speed multiplier, so band membership is
        // stable on that timescale.
        public static void AssignTickPhases(IReadOnlyList<Smurf> smurfs, Godot.Vector2 cameraFollow)
        {
            int colorWarm = 0, colorCold = 0;
            foreach (var s in smurfs)
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

        // True when this smurf should tick on the current sim tick. Hot
        // smurfs always tick; warm/cold smurfs tick when their slot matches
        // the current tick modulo their group interval.
        private static bool ShouldTick(Smurf s, long currentTick)
        {
            if (s.TickPhase == 0) return true;
            int interval = s.TickPhase == 1 ? WarmInterval : ColdInterval;
            return (currentTick % interval) == s.TickSlot;
        }

        // v0.4.19 — per-tick smurf-occupancy grid. `_smurfPerTile[idx]` is
        // the number of alive smurfs whose SimPos rounds to tile `idx`.
        // Rebuilt once at the top of every Tick from the smurf list, then
        // read-only for the rest of the tick. Used by the soft-collision
        // local steering (`MoveOneTick`) and the claim-aware adjacent-
        // tile picker (`NearestAdjacentPassableTile`) so multiple smurfs
        // converging on a work face spread across distinct approach
        // tiles instead of all stacking onto the same one.
        private static int[] _smurfPerTile = System.Array.Empty<int>();
        // v0.4.29 — first non-yielding smurf at each tile index. Lets the
        // yield-trigger find WHO to ask to lie down without re-walking the
        // smurf list. Cleared & repopulated alongside _smurfPerTile.
        private static Smurf?[] _firstSmurfPerTile = System.Array.Empty<Smurf?>();
        private static int   _occGridWidth;       // captured at populate-time so helpers don't need `map`

        private static void PopulateOccupancyGrid(LocalMap? map, IReadOnlyList<Smurf> smurfs)
        {
            if (map == null) { _occGridWidth = 0; return; }
            int W = map.Width, H = map.Height;
            int need = W * H;
            if (_smurfPerTile.Length < need)
            {
                _smurfPerTile      = new int[need];
                _firstSmurfPerTile = new Smurf?[need];
            }
            else
            {
                System.Array.Clear(_smurfPerTile,      0, need);
                System.Array.Clear(_firstSmurfPerTile, 0, need);
            }
            _occGridWidth = W;
            int count = smurfs.Count;
            for (int i = 0; i < count; i++)
            {
                var s = smurfs[i];
                if (!s.IsAlive) continue;
                // v0.4.29 — yielding (lying-down) smurfs are skipped so the
                // soft-collision steering and the FindNearestExcavate
                // approach-blocked check both treat their tile as "free".
                // Lets the smurf they're yielding for step over them.
                if (s.YieldingTicks > 0) continue;
                int tx = (int)(s.SimPos.X / LocalMap.TileSize);
                int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
                if ((uint)tx < (uint)W && (uint)ty < (uint)H)
                {
                    int idx = ty * W + tx;
                    _smurfPerTile[idx]++;
                    if (_firstSmurfPerTile[idx] == null)
                        _firstSmurfPerTile[idx] = s;
                }
            }
        }

        // v0.4.29 — DF-style yield trigger. When a smurf has been stuck for
        // YieldTriggerTicks behind another (non-yielding) smurf in the
        // direction it wants to walk, the BLOCKER lies down for
        // YieldDurationTicks. The stuck smurf's StuckTicks resets so its
        // re-path / give-up window starts fresh while the path is open.
        // Idempotent: a blocker already yielding stays as-is.
        // Sits between StuckRePathTicks (8) and StuckThreshold (18): the
        // re-pathfind gets first crack at corner-stuck oscillation, then if
        // the smurf is *still* stuck (almost always meaning a real smurf is
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
        private const int YieldTriggerTicks  = 12;
        private const int YieldDurationTicks = 30;
        private static void TryTriggerBlockerYield(Smurf s, LocalMap map)
        {
            if (_occGridWidth == 0) return;
            // Direction toward the smurf's current walk target. Step half a
            // tile ahead — far enough to land on the next tile, short
            // enough to not skip past adjacent occupants.
            Vector2 walkTo = ResolveWalkTarget(s, map);
            Vector2 diff = walkTo - s.SimPos;
            if (diff.LengthSquared() < 0.0001f) return;
            Vector2 ahead = s.SimPos + diff.Normalized() * (LocalMap.TileSize * 0.75f);
            int aTx = (int)(ahead.X / LocalMap.TileSize);
            int aTy = (int)(ahead.Y / LocalMap.TileSize);
            if ((uint)aTx >= (uint)map.Width || (uint)aTy >= (uint)map.Height) return;
            int aIdx = aTy * _occGridWidth + aTx;
            if ((uint)aIdx >= (uint)_firstSmurfPerTile.Length) return;
            var blocker = _firstSmurfPerTile[aIdx];
            if (blocker == null || blocker == s || blocker.YieldingTicks > 0) return;
            blocker.YieldingTicks = YieldDurationTicks;
            // Give the now-unblocked smurf a clean stuck window so the
            // existing re-path / give-up timers don't immediately fire on
            // top of the resolution.
            s.StuckTicks  = 0;
            s.RePathTried = false;
        }

        // v0.4.58 — compute the smurf's current tile index in the per-tick
        // occupancy grid. Returns -1 when the grid hasn't been populated
        // yet (very first tick after map bind) or when the smurf is OOB.
        // Used as the `askerTileIdx` self-exemption arg to Pathfinder so
        // the asker doesn't pay the crowd-cost penalty on its own tile.
        private static int OccTileIdx(Smurf s)
        {
            if (_occGridWidth <= 0) return -1;
            int tx = (int)(s.SimPos.X / LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
            return ty * _occGridWidth + tx;
        }

        // True iff some *other* smurf occupies the candidate tile. The
        // smurf about to move is exempt — they're currently contributing
        // to the count of their own tile, and "I'm in the way of myself"
        // would be a meaningless veto. Caller passes the smurf's current
        // tile index so the exemption can be done by index compare.
        private static bool TileHasOtherSmurf(int candidateIdx, int currentIdx)
        {
            if (_occGridWidth == 0 || (uint)candidateIdx >= (uint)_smurfPerTile.Length)
                return false;
            int n = _smurfPerTile[candidateIdx];
            if (candidateIdx == currentIdx) n--;   // subtract self
            return n > 0;
        }

        // v0.5.9 — task viability check. RimWorld's JobDriver fail-condition
        // pattern: each Toil registers fail conditions (FailOnDestroyedOrNull,
        // FailOnForbidden, FailOnSomeoneElseHaulingIt, etc.) that are
        // evaluated every tick during the job. If any fail condition is
        // true, the JobDriver ends with JobCondition.Incompletable and the
        // pawn drops the job + asks JobGiver for a new one.
        //
        // SmurfulationC equivalent: structural sanity check on the current
        // task's target. Designation still painted? Vegetation still
        // present? Haul item still on the source tile? Player-order
        // destination still reachable? If any of these are now false, the
        // task can't be completed and the smurf should release the claim
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
        // workbench exists + ingredients are reachable — the smurf holds
        // the task and accumulates progress. Same for slow mining (Phase
        // 6 tool durability). Sam: "a smurf that is completing a task,
        // like crafting, mining, or hauling, should know to reassign if
        // that task is impossible while still holding onto the task if it
        // takes 15-30s."
        //
        // Idle tasks (Wander/Loiter/Observe/Converse/Meditate/VisitFavorite),
        // critical-need tasks (Eat/Sleep/Attune/Socialize), and any task
        // without resolved tile coords return true unconditionally —
        // those are either self-contained (idle effects) or handled by
        // their own system (e.g., HaulSystem manages Phase 1 → Phase 2).
        private static bool IsTaskStillValid(Smurf s, BehaviorTask t, LocalMap map)
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
                    // Phase 2 (deliver) — TargetId == null, smurf is
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
                    // the smurf can reach. Player-issued orders are
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

                default:
                    return true;
            }
        }

        // v0.5.8 — climb-over usefulness check. The smurf's local steering
        // may fall back to stepping onto an occupied tile (a "climb over"
        // a blocker) when every uncrowded side-step is terrain-blocked.
        // That step is *useful* only in one of two cases:
        //
        //   1. The candidate tile is Chebyshev-≤-1 from the current task's
        //      target tile — stepping onto it puts the smurf at touch-
        //      arrival distance to the work tile, so the next tick fires
        //      IsAtTouchArrival → ApplyTaskEffect. This is the common case
        //      for Excavate (target impassable, smurf must stand adjacent)
        //      and the boundary case for Gather/Chop/Cut where the
        //      candidate IS the target.
        //
        //   2. The tile *beyond* the candidate, in the direction of motion,
        //      is terrain-passable — the smurf has somewhere to continue
        //      after the climb, so the climb is a useful step on a path.
        //
        // Neither case → the climb is a dead-end: the smurf steps onto a
        // blocker, can't continue forward (impassable beyond), and the
        // next tick's primary direction pulls back into the same blocker
        // → oscillation. Returning false here makes the steering leave
        // the smurf in place, letting the YieldTrigger (12 ticks of
        // StuckTicks) ask the blocker to lie down. Once the blocker
        // yields, its tile drops out of the occupancy grid and the
        // smurf's primary step becomes uncrowded.
        //
        // `motion` is the candidate's direction vector (baseStep for the
        // primary, rotated for side-steps). Math.Sign gives one of -1/0/+1
        // per axis so the tile beyond is the next tile in the 8-direction
        // sense. Step magnitude doesn't matter — only direction does.
        private static bool IsClimbOverUseful(LocalMap map, Smurf s, Vector2 candidate, Vector2 motion)
        {
            int candTx = (int)(candidate.X / LocalMap.TileSize);
            int candTy = (int)(candidate.Y / LocalMap.TileSize);

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

        // ── Main tick ───────────────────────────────────────────────────────
        // Called from SimulationCore.Tick once per sim-system interval (1× = 1 s).
        public static void Tick(IReadOnlyList<Smurf> smurfs, LocalMap? map,
            ColonyResources resources, Queue<PlayerOrder>? pendingOrders,
            Random rng, float dtSeconds, long currentTick = 0)
        {
            // v0.4.14 — batch the region-graph rebuild to once per sim
            // tick. Without this gate every excavation's `MutateTerrain`
            // flipped `_regionsDirty`, and the next smurf's SelectTask
            // re-ran the full W×H BFS. At 240×150 with 17 active diggers
            // that was ~50 ms / tick of pure rebuild work — the cause of
            // the sim-thread stall reported as "smurfs stuck + visual
            // warping at the edges". Inside the tick, the data may go
            // stale by one tick (a tile that just became passable still
            // reads region 0); excavation only ADDS connectivity so
            // smurfs still pick valid targets, and the worst case is one
            // extra tick of latency before a newly-opened pocket is
            // assigned work.
            map?.BeginTick();
            try
            {

            // v0.4.19 — populate the per-tick smurf-occupancy grid. Local
            // steering + the adjacent-tile picker read this to avoid
            // routing smurfs through each other's current tile (soft
            // RimWorld-style collision; not a hard block, just a
            // tie-breaker so the colony spreads out at work faces
            // instead of stacking). Rebuilt once per tick from the
            // smurf list — cost is O(N) once, vs O(N²) per-smurf
            // scans.
            PopulateOccupancyGrid(map, smurfs);

            // 1. Drain any pending player orders and stage them on their target smurf.
            if (pendingOrders != null)
            {
                while (pendingOrders.Count > 0)
                {
                    var order = pendingOrders.Dequeue();
                    foreach (var s in smurfs)
                    {
                        if (s.Name != order.SmurfName) continue;

                        // v0.4.51b — release the OLD task's reservations /
                        // designation claims before clobbering CurrentTask.
                        // Without this, right-clicking a smurf mid-Haul or
                        // mid-Gather left the haul reservation / designation
                        // claim dangling so other smurfs couldn't pick up
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
                        // relied on greedy steering — smurfs would walk
                        // straight at the destination and dead-end against
                        // walls / concave terrain since needNewTask was
                        // false for the freshly-assigned task and the
                        // section-2a pathfinding block at line 543 never
                        // fired in the same tick. Sam: "Currently, smurfs
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
                                    s.PathWaypoints, _smurfPerTile, OccTileIdx(s));
                                break;
                            }

                            s.CurrentTask = new BehaviorTask(
                                TaskType.PlayerOrder, order.Target, 100f,
                                isPlayerOrder: true, interruptible: false,
                                tileX: tx, tileY: ty);
                            s.SimTarget = order.Target;
                            Pathfinder.FindPath(map, s.SimPos, (tx, ty),
                                s.PathWaypoints, _smurfPerTile, OccTileIdx(s));
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

            // 2. Per-smurf evaluation + movement + effects.
            // v0.4.18 — indexed loop. `foreach` on `IReadOnlyList<Smurf>`
            // boxes to a heap-allocated enumerator on every Tick; at 60 Hz
            // that was 60 enumerator allocations per second of pure GC
            // pressure. Indexed access takes the same `IList<T>.this[int]`
            // path the JIT already devirtualises for `List<T>`.
            int smurfCount = smurfs.Count;
            for (int si = 0; si < smurfCount; si++)
            {
                var s = smurfs[si];
                if (!s.IsAlive) continue;

                // v0.3.39 (O-H.2) — LOD skip. Off-screen smurfs tick less
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

                // v0.3.35 / v0.3.40 — tick down each per-smurf "recently
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
                // v0.5.4 — work-search debounce decrement. See Smurf.cs
                // comment + the workAvailable gate below.
                if (s.WorkSearchCooldownTicks > 0)
                    s.WorkSearchCooldownTicks -= tickInterval;

                // v0.5.9 — task viability gate (RimWorld JobDriver FailOn
                // pattern). If the current task can no longer be completed
                // — designation cleared by another smurf or the player,
                // haul item missing/forbidden, player-order destination
                // walled off, etc. — release the claim + clear the task
                // here so the section-2a needNewTask block immediately
                // routes the smurf to SelectTask. Without this gate the
                // smurf walks the full path to a defunct target, jitters
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
                    // through external state change (other smurf finished
                    // it, player removed designation), not through this
                    // smurf's path choices. A future re-evaluation should
                    // be free to pick a fresh target nearby.
                }

                // 2a. Re-evaluate task selection unless a non-interruptible player order
                //     is in progress or the current task is still valid and current.
                //
                // v0.3.43 — idle tasks (Wander / Loiter / Observe / Converse /
                // Meditate / VisitFavorite) now respect IdleLingerTicks. After
                // arriving at an idle target the smurf "stays" for the
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
                    // Interruptible gate. Without this, a smurf carrying
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
                    // and resets the linger only when the smurf actually
                    // reaches the destination, so this check fires once
                    // the post-arrival dwell is over.
                    bool lingerExpired = idle && s.IdleArrived && s.IdleLingerTicks <= 0;
                    // v0.4.65 — gate `workAvailable` on the post-abandon
                    // cooldown. Without this, a smurf in cooldown that's
                    // doing an idle task triggers a re-eval EVERY TICK
                    // because designations exist on the map; SelectTask
                    // then blocks every designation branch (per v0.4.57's
                    // DesignationCooldownTicks gate) and falls through to
                    // the idle tier, where it picks a NEW personality-
                    // weighted random idle task. Net effect: visible
                    // cycling between Wander → Loiter → Observe → ... for
                    // the full ~1s cooldown duration. Sam's report of
                    // "stuck cycling between idle behaviors." With the
                    // gate: a cooldown smurf finishes their current idle
                    // task's linger naturally and only re-evaluates when
                    // either the cooldown expires (designations become
                    // available again) or the linger does (normal idle
                    // rotation).
                    // v0.5.4 — also gate on WorkSearchCooldownTicks. The
                    // v0.4.65 DesignationCooldownTicks gate stops cycling
                    // for smurfs who *just abandoned* a task (~1s cooldown).
                    // But Sam's persistent idle-cycling report: a smurf
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
                    // Working smurfs (Excavating / Hauling / Eating /
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
                    needNewTask = true;
                }

                if (needNewTask)
                {
                    // v0.3.33 (B.7) — release any prior designation claim
                    // before re-selecting so the tile becomes available to
                    // other smurfs. Stale claims would block work assignment
                    // until SetXDesignation re-set the tile.
                    ReleaseTaskClaim(s, map);

                    // v0.5.2 — chain order queue. If the smurf has any
                    // shift+right-click queued Move orders pending, pop
                    // the head and create a fresh PlayerOrder for it.
                    // Bypasses the v0.4.19 failure-recovery short-circuit
                    // and the regular SelectTask roll because the player
                    // explicitly queued these. Life-threat critical needs
                    // (`IsLifeThreatening` above) still override via the
                    // `critical` branch — a starving smurf interrupts a
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
                    // smurf has just completed three work tasks in a row
                    // without producing any output (haul item missing on
                    // arrival, designation cleared by another smurf, slot
                    // depleted upstream) we force a Wander to break the
                    // cycle. Without this, smurfs at the delivery point
                    // would keep being handed nearby Haul tasks that
                    // already-finished by the time they reached the
                    // pickup tile, visibly bunching around the spawn
                    // cluster making no progress. The double-linger
                    // gives the colony state time to settle before the
                    // smurf re-engages with the priority queue.
                    else if (s.ConsecutiveTaskFailures >= TaskFailureForceWander)
                    {
                        s.CurrentTask = NewWanderTask(s.SimPos, map, rng);
                        s.ConsecutiveTaskFailures = 0;
                    }
                    else
                    {
                        s.CurrentTask = SelectTask(s, map, resources, rng, smurfs);
                    }

                    // v0.4.4 — auto-equip the dominant-hand tool that
                    // matches this task's preferred-tools list. Magic
                    // grab from the colony pool until Phase 5 stockpile
                    // zones land; off-hand stays free for shields +
                    // dual-wield.
                    if (s.CurrentTask is { } autoEquipTask)
                        EquipmentSystem.AutoEquipForTask(s, autoEquipTask, resources);
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
                        // smurf right now. Suppress the workAvailable
                        // re-eval clause for ~1 second so the smurf
                        // commits to their chosen leisure activity
                        // instead of re-rolling on every tick because
                        // designations exist somewhere globally. Re-
                        // checked when the cooldown expires; the smurf
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
                    // improves long-route reliability — smurfs no longer
                    // dead-end against concave wall pockets.
                    if (s.CurrentTask is { } pt && map != null
                        && pt.TargetTileX >= 0 && pt.TargetTileY >= 0)
                    {
                        float dx = pt.Target.X - s.SimPos.X;
                        float dy = pt.Target.Y - s.SimPos.Y;
                        float distSq = dx * dx + dy * dy;
                        // v0.4.16 — designation tasks ALWAYS go through A*,
                        // regardless of distance. Short routes (< 8 tiles)
                        // used to fall through to greedy local steering,
                        // which doesn't respect the cut-corner rule the
                        // way the region graph + Pathfinder do. A smurf
                        // claiming a Boulder tile across a concave corner
                        // would oscillate against the diagonal block and
                        // sit there until the 90-tick stuck-detector
                        // fired — visible as "stuck in corners and on
                        // walls". A* fail-fast makes a short search cheap
                        // (typically < 20 expansions), so we eat the
                        // microcost in exchange for robust corner routing.
                        // v0.5.3 — player orders join designation tasks in
                        // the always-A* tier. Same reasoning: a player
                        // right-clicking a tile across a wall corner used
                        // to dead-end on greedy steering. RimWorld pathfinds
                        // every player Goto regardless of distance.
                        bool isDesignation = IsDesignationTaskType(pt.Type);
                        if (isDesignation || pt.IsPlayerOrder
                            || distSq > Pathfinder.PreferAStarDistSqPx)
                        {
                            // v0.4.18 — fill-into-buffer API. The Pathfinder
                            // clears + populates s.PathWaypoints directly,
                            // skipping the per-call List<Vector2> allocation
                            // that previously fired on every task selection.
                            // v0.4.58 — pass the per-tick occupancy grid +
                            // asker's tile index so A* applies the RimWorld
                            // soft-collision cost (175 per other smurf on
                            // a candidate tile). Path naturally routes
                            // around clusters at saturated work faces.
                            bool found = Pathfinder.FindPath(map, s.SimPos,
                                (pt.TargetTileX, pt.TargetTileY), s.PathWaypoints,
                                _smurfPerTile, OccTileIdx(s));
                            // v0.4.13 — fail-fast unreachable. The DF-region
                            // check inside FindPath now returns false in
                            // O(1) when start and goal sit in different
                            // regions. Blacklisting the tile here means
                            // the smurf reprioritises on the very next
                            // tick instead of wasting StuckThreshold (~1.5s)
                            // jittering at the edge of an interior pocket.
                            // Only blacklist designation work; haul / move
                            // / combat orders aren't claim-tracked the
                            // same way and may legitimately be retried
                            // (the player can reroute the destination).
                            if (!found && IsDesignationTaskType(pt.Type))
                            {
                                int oldestIdx = 0;
                                int oldestTtl = int.MaxValue;
                                for (int i = 0; i < s.AvoidTiles.Length; i++)
                                    if (s.AvoidTiles[i].TicksLeft < oldestTtl)
                                    { oldestTtl = s.AvoidTiles[i].TicksLeft; oldestIdx = i; }
                                s.AvoidTiles[oldestIdx] = (pt.TargetTileX, pt.TargetTileY, 360);
                                ReleaseTaskClaim(s, map);
                                s.CurrentTask = null;
                                s.PathWaypoints.Clear();
                            }
                        }
                    }
                }

                if (s.CurrentTask == null) continue;

                // v0.4.14 — pre-movement reachability gate for short routes.
                // v0.4.17 — gated on smurf-pixel passability. If SimPos has
                // briefly drifted into a wall (passability flip mid-tick,
                // save-load race, vegetation regrowth), the check would
                // hit `IsWorkReachable`'s multi-region wall fallback and
                // could blacklist a valid target by picking a neighbour
                // in the wrong region. MoveOneTick's SimPos rescue runs a
                // few lines below; defer the reachability check until the
                // smurf is back on a passable tile.
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

                // v0.3.43 — tick down idle linger so the smurf "stays" at
                // their target a moment after arriving. Stops the rapid
                // re-pick cycle that produced jittering. Scaled by the LOD
                // interval so cold smurfs (which only tick every 6 sim
                // ticks) accumulate linger at the same real-time rate as
                // hot smurfs.
                if (s.IdleLingerTicks > 0)
                    s.IdleLingerTicks -= tickInterval;

                // 2b. Movement (v0.3.22 — rewritten, see MoveOneTick).
                //     SimPos validation, target-tile interaction routing,
                //     multi-direction local steering, stuck detection.
                // v0.3.39 — pass the LOD-scaled effective dt so warm/cold
                // smurfs cover the same distance per second as hot ones.
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
                    // before the smurf arrived, slot depleted
                    // upstream) leave it false.
                    bool wasWorkTask = IsDesignationTaskType(arrivedTask.Type)
                        || arrivedTask.Type == TaskType.Haul;
                    s.TaskDidWork = false;

                    // 2c. On arrival, execute task effect.
                    ApplyTaskEffect(s, arrivedTask, map, resources, dtSeconds, smurfs, rng, currentTick);

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
                    // and is now redundant anyway because the smurf has
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
        //   1. Rescue SimPos: if the smurf is somehow standing inside an
        //      impassable tile (vegetation regrowth, save/load race, etc.),
        //      BFS to the nearest passable tile and snap there immediately —
        //      otherwise the rest of the tick reasons about a position that
        //      shouldn't exist and the smurf will look like it's inside a wall.
        //   2. Normalise the movement target. Tasks like GatherMaterial target
        //      an impassable Boulder/DeadLog/LivingWood tile — the smurf needs
        //      to walk to an *adjacent* passable tile to interact, not to the
        //      tile centre (which is inside the rock). The task target itself
        //      stays unchanged so ApplyTaskEffect still mutates the right tile.
        //   3. Try straight, ±45 °, ±90 °, ±135 ° (six fan-out angles) before
        //      giving up — concave geometry (L-walls, inside corners) defeats
        //      the previous two-angle check.
        //   4. If everything is blocked, stay put. Never teleport.
        //   5. Stuck detection: if SimPos barely moved for `StuckThreshold`
        //      ticks, clear the task so the smurf re-evaluates. Without this
        //      an unreachable designation traps the smurf forever.
        //
        // Phase-4 hook: when `PathWaypoints` is non-empty, treat the head of
        // the list as the per-step target instead of `CurrentTask.Target`.
        // The A* planner that lands in Phase 4 will populate the list; until
        // then it stays empty and this code falls through to greedy steering.
        private const float ArrivalEpsilon  = 0.5f;    // px progress to count as "moved"
        // v0.4.17 — recovery cadence tightened again from 90 → 30 ticks (~0.5
        // sec at 1×). At the v0.4.16 always-A*-for-designations setting,
        // genuine 1.5-second stalls were almost always either a stale path
        // (other smurfs nearby) or a corner-stuck oscillation — both
        // recoverable by an early re-pathfind + blacklist. The shorter
        // threshold makes wall-corner stuck cases visibly snap out within
        // half a second instead of feeling frozen. We also try one
        // re-pathfind at StuckRePathTicks (8) before the final give-up.
        //
        // v0.4.59 — halved from v0.4.36's 30 / 15. At 60 Hz sim tick rate
        // that's 300 ms / 133 ms (was 500 ms / 250 ms). Faster recovery
        // window pairs with v0.4.58's A* crowd cost so smurfs spend
        // less time dwelling at jammed work-faces.
        private const int   StuckThreshold    = 18;
        private const int   StuckRePathTicks  = 8;

        // v0.5.11 — distance-not-decreasing stuck thresholds. RimWorld
        // pawns re-path when not progressing toward the goal. Our existing
        // StuckTicks/StuckRePathTicks fire only on immobility (progressed
        // < ArrivalEpsilon). The corner-stuck pattern Sam still sees has
        // smurfs sideways-oscillating at concave terrain corners — they
        // ARE moving, so StuckTicks doesn't accumulate, so the immobility
        // re-path never fires. This pair of thresholds catches "moving
        // but not getting closer to the next walk target." Slightly more
        // lenient than the immobility thresholds (legit detours can have
        // brief no-progress windows when local steering navigates around
        // an obstacle).
        private const int   NoProgressRePathTicks = 30;   // ≈ 0.5 s at 60 Hz
        private const int   NoProgressGiveUpTicks = 60;   // ≈ 1.0 s post-re-path

        // v0.4.19 — force-wander trip count. When a smurf's last N work
        // tasks (Haul + designation types) all completed as no-ops
        // — the haul item was already gone, the designation was
        // cleared by someone else, the vegetation depleted upstream —
        // the next `needNewTask` block hands them a Wander instead of
        // re-rolling the priority queue. 3 is empirically generous:
        // legitimate "two smurfs racing for the same item, lost the
        // race" cases reset the counter on the next successful
        // completion, but a smurf stuck in a no-op feedback loop
        // breaks out within ~3 ticks instead of indefinitely.
        private const int   TaskFailureForceWander = 3;
        // v0.3.36 (B.17) — precomputed (cos, sin) rotation pairs. Each
        // steering attempt previously called `step.Rotated(angle)` which
        // computes Cos+Sin on every call; with 8 angles × 1000 smurfs × 60 Hz
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

        private static bool MoveOneTick(Smurf s, LocalMap? map, float dtSeconds, Random rng,
            int tickInterval = 1)
        {
            if (s.CurrentTask == null) return false;

            // v0.4.29 — DF-style yield: lying-down smurfs hold position so
            // the smurf they're blocking can climb over them. Decrement
            // the timer and skip movement entirely while it's active.
            // Doesn't reset StuckTicks — if this smurf had its OWN stuck
            // counter rising before being asked to yield, it picks up
            // where it left off after standing back up.
            //
            // v0.4.42 BUGFIX: returns FALSE, not true. The caller reads the
            // return value as "smurf arrived at task target" and fires
            // ApplyTaskEffect on true — which would mutate the designated
            // tile (boulder → mud, vegetation → cleared, etc.) without
            // the smurf actually being there. Sam's report: tiles
            // destroyed after designation despite no smurf walking over.
            // A yielding smurf is NOT arriving anywhere; they're just
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
            // regrowth, another smurf's excavation invalidating a
            // corridor, a player-issued wall placement) we'd waste the
            // stuck-detector window walking into it. Check the head
            // waypoint (the only one we're about to step onto this tick);
            // if it's now in a wall, drop the path and request a fresh
            // A* immediately so the smurf reroutes within the same tick
            // instead of bashing at the new obstacle for 15 ticks.
            if (map != null && s.PathWaypoints.Count > 0)
            {
                Vector2 wp = s.PathWaypoints[0];
                int wpx = (int)(wp.X / LocalMap.TileSize);
                int wpy = (int)(wp.Y / LocalMap.TileSize);
                if (!map.IsPassable(wpx, wpy))
                {
                    s.PathWaypoints.Clear();
                    if (s.CurrentTask is BehaviorTask invalTask
                        && IsDesignationTaskType(invalTask.Type)
                        && invalTask.TargetTileX >= 0 && invalTask.TargetTileY >= 0)
                    {
                        // v0.4.58 — crowd-aware re-path. Same crowd cost
                        // applies on the in-flight path-invalidation
                        // recompute as on initial task assignment.
                        Pathfinder.FindPath(map, s.SimPos,
                            (invalTask.TargetTileX, invalTask.TargetTileY),
                            s.PathWaypoints, _smurfPerTile, OccTileIdx(s));
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
            // smurfs converging on a 10×10 boulder cluster, only ~36
            // perimeter tiles reachable), every adjacent tile is
            // crowded — soft-collision steering vetoes stepping onto
            // them, so the smurf orbits at `ArrivalRadius + ε` and
            // never crosses the threshold. ApplyTaskEffect never fires.
            // Result: cascading stuck-out → abandonment → re-pick same
            // task → cycle.
            //
            // RimWorld's PathFinder uses `PathEndMode.Touch`: the goal
            // is reached when ANY of the 8 cells adjacent to the
            // target is reached, whichever the search hits first.
            // Mirror that here: if the task target tile is impassable
            // (Boulder / DeadLog / LivingWood / LargeMushroom etc.)
            // and the smurf is Chebyshev-distance ≤ 1 from it, fire
            // arrival regardless of which adjacent the path-resolver
            // picked. The cluster-jam dissolves because any smurf in
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
            // an orbital failure mode: when the smurf's destination tile
            // happened to be crowded, the fan-out preferred any uncrowded
            // side-step over the (crowded but correct) primary. The
            // smurf orbited at `ArrivalRadius + ε` and never crossed the
            // arrival threshold, so ApplyTaskEffect never fired —
            // exactly the dig-cluster jitter the player reported.
            //
            // RimWorld's local steering handles this by always taking
            // the primary direction when it's *terrain*-passable and
            // only consulting the soft-collision fallback when the
            // primary is blocked by a wall. v0.4.36 — softened that rule so
            // smurfs prefer walking AROUND each other when there's room to
            // do so.
            //
            // v0.5.10 — softening v0.4.36 was too aggressive. In dense
            // clusters (10+ smurfs converging on 4 work targets, see Sam
            // screenshot) the "first uncrowded side-step wins" rule sent
            // every smurf perpendicular to its path, abandoning the A*
            // route that crowd-cost-soft-cost-A* (v0.4.58) carefully
            // computed. Smurfs oscillated sideways without forward
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
            //      blocker. Keeps the smurf on its planned path through
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
            //      blocker to lie down, then the smurf walks through
            //      naturally because the yielding smurf drops out of
            //      the occupancy grid.
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0
                ? curTileY * _occGridWidth + curTileX
                : -1;

            // v0.4.38 — terrain-based movement-speed multiplier. Shallows
            // (v0.4.37 passable shallow water) slows smurfs to 30 % walk
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

            Vector2 baseStep = diff.Normalized() * s.SimSpeed * terrainSpeedMul * dtSeconds;
            Vector2 bestChosen = Vector2.Zero;
            bool moved = false;

            Vector2 primary = s.SimPos + baseStep;
            bool primaryPassable = map == null || IsPixelPassable(map, primary);
            bool primaryCrowded  = false;
            if (primaryPassable && map != null && _occGridWidth > 0)
            {
                int pTx = (int)(primary.X / LocalMap.TileSize);
                int pTy = (int)(primary.Y / LocalMap.TileSize);
                int pIdx = pTy * _occGridWidth + pTx;
                primaryCrowded = TileHasOtherSmurf(pIdx, curTileIdx);
            }

            // Best case: primary terrain-passable AND uncrowded.
            if (primaryPassable && !primaryCrowded)
            {
                bestChosen = primary;
                moved = true;
            }
            // v0.5.10 — Priority 2: primary passable + crowded, but the
            // climb-over is useful (next-tile-passable in path direction
            // OR touch-arrival to task target). Take primary, climb the
            // blocker. Keeps the smurf on its planned A* path through a
            // dense cluster instead of side-stepping perpendicular and
            // oscillating. RimWorld parity. Sam: "smurfs now are tasking
            // properly, but they're getting stuck on each other while
            // pathfinding... research how rimworld solves this problem."
            else if (map != null && primaryPassable && primaryCrowded
                     && IsClimbOverUseful(map, s, primary, baseStep))
            {
                bestChosen = primary;
                moved = true;
            }
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
                // smurf has somewhere to continue after climbing). Without
                // this guard, smurfs would climb onto a blocker B whose
                // far side is an impassable wall / their excavate target,
                // then oscillate because they can't continue forward and
                // their primary direction keeps pulling them back into B.
                // Sam: "smurfs can't path through another smurf and get
                // stuck when they can't pass over them into an unpassable
                // tile."
                bool    crowdedFallbackHas = primaryPassable
                    && IsClimbOverUseful(map, s, primary, baseStep);
                for (int i = 1; i < SteerVectors.Length; i++)
                {
                    var (c, sn) = SteerVectors[i];
                    Vector2 rotated = new Vector2(
                        baseStep.X * c  - baseStep.Y * sn,
                        baseStep.X * sn + baseStep.Y * c);
                    Vector2 candidate = s.SimPos + rotated;
                    if (!IsPixelPassable(map, candidate)) continue;
                    bool crowded;
                    if (_occGridWidth > 0)
                    {
                        int cTx = (int)(candidate.X / LocalMap.TileSize);
                        int cTy = (int)(candidate.Y / LocalMap.TileSize);
                        int cIdx = cTy * _occGridWidth + cTx;
                        crowded = TileHasOtherSmurf(cIdx, curTileIdx);
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
                // smurf stays put this tick. StuckTicks builds → the
                // v0.4.29 YieldTrigger fires at 12 ticks, asking the
                // blocker to lie down. Once the blocker yields, its tile
                // drops out of the occupancy grid (PopulateOccupancyGrid
                // line 157) so the smurf's next primary step becomes
                // uncrowded and resolves naturally.
            }

            if (moved) s.SimPos = bestChosen;
            // else: fully blocked — stay put (never teleport).

            // 4. Stuck detection — increment when net progress is below epsilon.
            // v0.3.39 (O-H.2) — scale the increment by the LOD tick interval
            // so cold smurfs (which only tick every 6 sim ticks) accumulate
            // stuck-ness at the same real-time rate as hot smurfs. Without
            // this, a cold smurf would take 6× longer to give up than a hot
            // smurf in the same physical configuration.
            float progressed = (s.SimPos - s.PrevSimPos).Length();
            if (progressed < ArrivalEpsilon)
            {
                s.StuckTicks += tickInterval;

                // v0.4.17 — one re-pathfind attempt at the halfway mark.
                // Corner-stuck oscillation usually clears with a fresh A*
                // path from the smurf's current pixel (the v0.4.16 always-
                // A* path was computed at task selection from an earlier
                // SimPos; the smurf may have drifted into a tile from
                // which a different route is needed). Cheap (one A* per
                // smurf per stuck window) and lets the smurf recover
                // without triggering the give-up + blacklist path. Only
                // fires for designation tasks since those are the ones
                // routed through A* in the first place.
                // v0.5.3 — PlayerOrder joins the re-path tier. Pre-v0.5.3
                // a stuck player order rode out the full ~90-tick stuck
                // window before give-up; with re-path enabled the smurf
                // tries an alternative route at ~30 ticks. Same one-shot
                // budget (RePathTried) so a genuinely-blocked order still
                // gives up at StuckThreshold and doesn't loop forever.
                if (map != null && !s.RePathTried && s.StuckTicks >= StuckRePathTicks
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
                        _smurfPerTile, OccTileIdx(s));
                    if (found && s.PathWaypoints.Count > 0)
                    {
                        s.StuckTicks = 0;   // give the new path a clean budget
                    }
                }

                // v0.4.29 — DF-style yield. Re-pathfind didn't help
                // (StuckTicks kept climbing past the trigger), so the
                // obstruction is almost certainly another smurf, not bad
                // routing. Ask the blocker in the primary direction to lie
                // down; on success this resets StuckTicks so the give-up
                // window starts fresh while we walk over them.
                if (map != null && s.StuckTicks >= YieldTriggerTicks)
                    TryTriggerBlockerYield(s, map);

                if (s.StuckTicks > StuckThreshold)
                {
                    // Give up on this task. v0.3.23 routes to a fresh Wander.
                    // v0.3.33 (B.7) — also release the designation claim so
                    // another smurf can pick the tile up.
                    // v0.3.35 — record the abandoned tile in the smurf's
                    // short-term avoid list so SelectTask doesn't immediately
                    // re-pick it. 300 ticks ≈ 5 sec at 1×, enough for the
                    // smurf to wander far enough that a different target
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
                        // matters less; shorter TTL lets the smurf retry
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
                    // smurf's behaviour. Single thought slot (RimWorld
                    // pattern), so multiple stucks just refresh its TTL
                    // rather than stacking.
                    ThoughtRegistry.Add(s, "TaskAbandoned");
                    ReleaseTaskClaim(s, map);
                    // v0.4.57 — post-abandon cooldown. Forces this smurf
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
                    // displace the smurf to a position where a different
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
            // of whether the smurf is moving, because the failure mode is
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
                    // tickInterval so cold smurfs hit the threshold at
                    // the same wall-clock rate as hot smurfs.
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
                            s.PathWaypoints, _smurfPerTile, OccTileIdx(s));
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
                        // so the smurf doesn't immediately re-pick the
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

        // Picks the actual pixel the smurf should walk toward this tick.
        //
        //   • If Phase 4's planner has populated PathWaypoints, the head of
        //     the list is the next waypoint. When the smurf is within
        //     ArrivalRadius of that waypoint we pop it and the next tick
        //     advances to the one after.
        //   • If the *task* target is an impassable tile (GatherMaterial),
        //     we route to the nearest passable neighbour of that tile rather
        //     than into the rock itself. The smurf will arrive at the
        //     neighbour, ApplyTaskEffect will fire while the smurf stands
        //     adjacent to the boulder, and the task's tile coordinates still
        //     drive the harvest/excavation effect.
        //   • Otherwise the task target is used directly.
        private static Vector2 ResolveWalkTarget(Smurf s, LocalMap? map)
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
                if (RequiresAdjacentApproach(task.Type)
                    && !map.IsPassable(task.TargetTileX, task.TargetTileY))
                {
                    // v0.4.14 — pass smurf tile so the picker chooses the
                    // approach square closest to the smurf and in the
                    // smurf's own DF region. Previously this returned the
                    // first-found cardinal neighbour of the *target*
                    // (always west, due to iteration order) regardless of
                    // whether the smurf could actually reach it — that's
                    // what bunched diggers up at the work face and
                    // tripped the 90-tick stuck-detector.
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
        // happens on the smurf's own tile, so it isn't in this list.
        private static bool RequiresAdjacentApproach(TaskType t) =>
            // v0.3.38 — ChopWood and CutVegetation join the adjacent-approach
            // list because LargeMushroom variants are impassable until
            // their cap clears, and a smurf trying to walk to the tile
            // centre would get blocked by the same wall it's trying to
            // harvest. ResolveWalkTarget routes them to a neighbour.
            t == TaskType.GatherMaterial || t == TaskType.ChopWood || t == TaskType.CutVegetation;

        // v0.4.57 — RimWorld PathEndMode.Touch semantics. True iff the
        // smurf's current tile is Chebyshev-distance ≤ 1 from an
        // impassable-target work task's target tile. Used to fire
        // ApplyTaskEffect arrival as soon as the smurf is at ANY of
        // the 8 neighbours of the work target, not specifically the
        // adjacent tile NearestAdjacentPassableTile picked at
        // task-assignment time. Critical at saturated work sites
        // where the picker's chosen adjacent is occupied by another
        // smurf and soft-collision steering blocks entry.
        private static bool IsAtTouchArrival(Smurf s, LocalMap? map)
        {
            if (map == null) return false;
            if (s.CurrentTask is not { } ct) return false;
            if (!RequiresAdjacentApproach(ct.Type)) return false;
            if (ct.TargetTileX < 0 || ct.TargetTileY < 0) return false;
            if (map.IsPassable(ct.TargetTileX, ct.TargetTileY)) return false;
            // Smurf must also be on the same DF region (no leaping
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
            || t == TaskType.ChopWood || t == TaskType.CutVegetation;

        // Returns the 8-neighbour passable tile coordinate closest to (sx, sy).
        // v0.4.14 — picks the neighbour closest to the SMURF, not the target,
        // and prefers neighbours in the smurf's own DF region. The old "first
        // cardinal" rule routed every smurf to the west-side neighbour of
        // every impassable tile, which was the wrong side for diggers
        // approaching from the east / south and produced the diagonal pile-up
        // the player reported. Falls back to a target-relative pick if the
        // smurf coordinate is unknown (sx < 0) or if no neighbour matches
        // the smurf's region — at worst we keep the v0.4.13 behaviour.
        private static (int x, int y)? NearestAdjacentPassableTile(
            int tx, int ty, LocalMap map, int sx = -1, int sy = -1)
        {
            // v0.4.19 — claim-aware approach picker. The previous version
            // (v0.4.14) preferred the in-region neighbour closest to the
            // smurf; ties were broken by iteration order, so multiple
            // diggers converging on adjacent Boulders would route to the
            // same approach tile. The four-bucket scan below ranks
            // candidates as:
            //   1. in-smurf-region AND unoccupied   (best)
            //   2. in-smurf-region (occupied)
            //   3. any region AND unoccupied
            //   4. any region (occupied)            (last-resort)
            // Distance from the smurf still breaks ties within each
            // bucket. Smurfs heading to the same work face now naturally
            // spread across distinct approach tiles instead of all
            // pointing at the same one.
            (int x, int y)? regionUnocc = null; int bestRegionUnocc = int.MaxValue;
            (int x, int y)? regionAny   = null; int bestRegionAny   = int.MaxValue;
            (int x, int y)? anyUnocc    = null; int bestAnyUnocc    = int.MaxValue;
            (int x, int y)? anyAny      = null; int bestAnyAny      = int.MaxValue;

            ushort smurfRegion = (sx >= 0 && sy >= 0) ? map.GetRegion(sx, sy) : (ushort)0;
            bool haveSmurf = (sx >= 0 && sy >= 0);
            int curIdx = haveSmurf && _occGridWidth > 0 ? sy * _occGridWidth + sx : -1;

            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = tx + dx, ny = ty + dy;
                if (!map.IsPassable(nx, ny)) continue;

                int d;
                if (haveSmurf)
                {
                    int ddx = nx - sx, ddy = ny - sy;
                    d = ddx * ddx + ddy * ddy;
                }
                else
                {
                    d = dx * dx + dy * dy;
                }

                bool inRegion = smurfRegion != 0 && map.GetRegion(nx, ny) == smurfRegion;
                bool unocc = _occGridWidth > 0
                    && !TileHasOtherSmurf(ny * _occGridWidth + nx, curIdx);

                if (inRegion && unocc) { if (d < bestRegionUnocc) { bestRegionUnocc = d; regionUnocc = (nx, ny); } }
                if (inRegion)          { if (d < bestRegionAny)   { bestRegionAny   = d; regionAny   = (nx, ny); } }
                if (unocc)             { if (d < bestAnyUnocc)    { bestAnyUnocc    = d; anyUnocc    = (nx, ny); } }
                if (d < bestAnyAny)    { bestAnyAny = d; anyAny = (nx, ny); }
            }
            return regionUnocc ?? regionAny ?? anyUnocc ?? anyAny;
        }

        // BFS outward from the smurf's current tile to find the nearest
        // passable tile centre. Bounded to keep the worst case cheap on
        // dense maps (radius 8 = 64 tiles inspected). Returns null only on
        // pathologically enclosed maps, in which case the caller leaves
        // SimPos where it is — the smurf will be effectively frozen, but
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
        private static BehaviorTask SelectTask(Smurf s, LocalMap? map,
            ColonyResources resources, Random rng, IReadOnlyList<Smurf> smurfs)
        {
            // ── Tier 1: critical needs ──────────────────────────────────────
            float eatThreshold   = HasTrait(s, "Glutton")   ? 70f : 50f;
            float sleepThreshold = HasTrait(s, "Sleepyhead")? 60f : 40f;
            float safetyBonus    = HasTrait(s, "Worrywart") ? 20f : 0f;
            float socialThres    = HasPersonality(s, "Introvert") ? 10f : 20f;

            if (s.Nutrition < 20f) return MakeEat(s, resources);
            if (s.Rest      < 15f) return MakeSleep(s);
            if (s.Safety    < 20f) return MakeSeekSafety(s, safetyBonus);
            if (s.Social    < socialThres) return MakeSocialize(s, MoodAdjust(s));
            if (s.MagicResonance < 15f) return MakeAttune(s, MoodAdjust(s));

            if (s.Nutrition < eatThreshold)   return MakeEat(s, resources, priority: 70f);
            if (s.Rest      < sleepThreshold) return MakeSleep(s, priority: 65f);

            // ── Tier 2: role tasks ──────────────────────────────────────────
            // v0.3.21 — player-issued designations take precedence over the
            // autonomous role behaviour. Designated targets ignore the colony
            // food/material thresholds entirely: if the player drew a Gather
            // box, the Forager should go pick from it whether or not pantry
            // is full. Anyone else (any role) will pick up designations as a
            // fallback when no role-matching smurf is available — this is how
            // "nearest available smurf with the highest job priority carries
            // out the order" emerges from each smurf independently scanning
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
                // smurf is forbidden from this work (player set them to
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
                bool jobOk(string category)
                {
                    if (s.WorkPriorities == null) return true;
                    return !s.WorkPriorities.TryGetValue(category, out var v) || v != 0;
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
                // abandon cooldown. A smurf that gave up on an excavate /
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
                // Cut drop items on the world, any smurf with a Haul
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
            return SelectIdleActivity(s, map, rng, smurfs);
        }

        // v0.3.43 — Tier-3 idle picker. Builds a weight table per activity,
        // weighted by personality + preferences, then samples one. The
        // picker is the load-bearing part of "smurfs feel alive": every
        // tick a smurf without work selects from a varied pool instead of
        // always wandering, and the picked activity carries its own
        // ArrivalLinger so the smurf actually stays where they ended up.
        private static BehaviorTask SelectIdleActivity(Smurf s, LocalMap? map, Random rng,
            IReadOnlyList<Smurf> smurfs)
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
            if (s.Role == "Mage")    wMeditate += 12;
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
            if ((roll -= wConverse) < 0) return NewConverseTask(s, map, rng, smurfs);
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

        // v0.5.1 — call from MoveOneTick's arrival branches. First arrival
        // for an idle task flips IdleArrived=true and resets the linger
        // counter to ArrivalLinger so the post-arrival dwell starts from
        // full. Subsequent arrival ticks (smurf still at target) no-op
        // because IdleArrived is already true. Non-idle tasks are
        // unaffected — their CurrentTask gets cleared by ApplyTaskEffect
        // anyway, so the arrival flag stays unused.
        private static void MarkIdleArrivalIfNeeded(Smurf s)
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
        private static void TryLockConversePartner(Smurf s, Smurf partner, int lingerTicks)
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

        // Roadmap §3.4: distressed-or-worse smurfs gain +10 priority on
        // comfort tasks (Socialize / Attune).
        private static float MoodAdjust(Smurf s) =>
            s.MoodState <= MoodState.Distressed ? 10f : 0f;

        // If a task's current priority is below the would-be critical-need
        // priority for this smurf right now, allow override.
        private static bool CriticalNeedsOverride(Smurf s, float currentPriority)
        {
            if (s.Nutrition < 20f && currentPriority < 100f) return true;
            if (s.Rest      < 15f && currentPriority <  95f) return true;
            if (s.Safety    < 20f && currentPriority <  85f) return true;
            return false;
        }

        // v0.4.61 (E6 from rimport.md) — life-threatening needs that MUST
        // override even non-interruptible tasks (e.g. an in-flight player
        // PlayerOrder/Haul). A starving smurf walking on a "Move here"
        // order should still drop the order to eat — otherwise they
        // starve to death obeying. RimWorld parallel: `JobGiver_Work`
        // emergency tier always overrides drafted-state movement when
        // the pawn is below health-critical thresholds. Hard floor at
        // 5f so the bypass is reserved for genuine emergencies — a
        // smurf at Nutrition=18 still respects the player order.
        private static bool IsLifeThreatening(Smurf s)
        {
            return s.Nutrition < 5f || s.Rest < 5f;
        }

        // ── Task constructors ───────────────────────────────────────────────
        private static BehaviorTask MakeEat(Smurf s, ColonyResources r, float priority = 100f)
        {
            // No designated kitchen yet; target is current position so smurf
            // eats in place if food is available in the ledger.
            return new BehaviorTask(TaskType.Eat, s.SimPos, priority,
                interruptible: priority < 95f);
        }
        private static BehaviorTask MakeSleep(Smurf s, float priority = 95f) =>
            new(TaskType.Sleep, s.SimPos, priority, interruptible: priority < 90f);
        private static BehaviorTask MakeSocialize(Smurf s, float bonus) =>
            new(TaskType.Socialize, s.SimPos, 80f + bonus);
        private static BehaviorTask MakeAttune(Smurf s, float bonus) =>
            new(TaskType.Attune, s.SimPos, 75f + bonus);
        private static BehaviorTask MakeSeekSafety(Smurf s, float bonus) =>
            new(TaskType.SeekSafety, s.SimPos, 85f + bonus);

        // v0.3.43 — per-activity arrival linger (in sim ticks at 60/sec).
        // The higher the linger, the longer the smurf stands at the
        // destination before re-evaluating. These bracket "feels alive"
        // pacing: Observe (a smurf gazing) lingers longest; Wander
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
            // the per-smurf overload below which also seeds WanderHops.
            return PickIdleDestination(from, map, rng, TaskType.Wander, LingerWander, 8, 28);
        }

        // v0.5.5 — multi-hop wander factory. "Taking a walk" should be
        // a real walk: 2-4 destinations chained, walking between each,
        // then a final linger. Sam: "a smurf should actually take a short
        // walk and finish it." NewWanderTask(Smurf) seeds the hop counter;
        // ApplyTaskEffect's Wander case consumes it on each arrival,
        // chaining a fresh destination + bumping WorkSearchCooldownTicks
        // so the chained legs don't trigger a re-eval.
        private static BehaviorTask NewWanderTask(Smurf s, LocalMap? map, Random rng)
        {
            // 1-3 additional hops after the first arrival → 2-4 legs total.
            s.WanderHopsRemaining = rng.Next(1, 4);
            return PickIdleDestination(s.SimPos, map, rng, TaskType.Wander, LingerWander, 8, 28);
        }

        // Short-distance idle: stays close to where the smurf already is.
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
        private static BehaviorTask NewObserveTask(Vector2 from, LocalMap? map, Random rng)
        {
            return PickIdleDestination(from, map, rng, TaskType.Observe, LingerObserve, 3, 7);
        }

        // Converse: head toward another nearby alive smurf and stop a tile
        // short. Boosts both smurfs' Social on arrival. Falls back to a
        // loiter if no partner is found within range — solo smurfs don't
        // wander pointlessly looking for a chat.
        private const float ConversePartnerRangePx = 20f * 16f;     // 20 tiles
        private static BehaviorTask NewConverseTask(Smurf s, LocalMap? map, Random rng,
            IReadOnlyList<Smurf> smurfs)
        {
            // Find the nearest other alive smurf within range. Prefer
            // liked smurfs (LikedSmurfs list) when one is in range — the
            // existing social-affinity makes the choice feel intentional.
            Smurf? best = null;
            float bestDist = ConversePartnerRangePx * ConversePartnerRangePx;
            bool foundLiked = false;
            foreach (var other in smurfs)
            {
                if (other == s || !other.IsAlive) continue;
                float dx = other.SimPos.X - s.SimPos.X;
                float dy = other.SimPos.Y - s.SimPos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > bestDist) continue;
                bool liked = s.Preferences != null && s.Preferences.LikesSmurf(other.Name);
                if (foundLiked && !liked) continue;
                if (liked && !foundLiked) { best = other; bestDist = d2; foundLiked = true; continue; }
                best = other; bestDist = d2;
            }

            if (best == null)
            {
                // No partner in range — fall back to a loiter so the smurf
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
        private static BehaviorTask NewMeditateTask(Vector2 from, LocalMap? map, Random rng)
        {
            return new BehaviorTask(TaskType.Meditate, from, 6f,
                interruptible: true, arrivalLinger: LingerMeditate);
        }

        // VisitFavorite: today this is "walk to a slightly farther random
        // tile" — once smurfs remember their favourite spots (Phase 4
        // workshops, last-good-meal location), this picks one of them. The
        // long-tail wander variant.
        private static BehaviorTask NewVisitFavoriteTask(Smurf s, LocalMap? map, Random rng)
        {
            return PickIdleDestination(s.SimPos, map, rng, TaskType.VisitFavorite,
                LingerVisitFav, 10, 22);
        }

        // Shared destination sampler — replaces v0.3.35's NewWanderTask
        // inner logic. Tries progressively wider radii within the [minR,
        // maxR] bracket so a smurf in a pocket still finds somewhere to go.
        // v0.3.43 — parameterised by activity to keep the picker DRY.
        private static BehaviorTask PickIdleDestination(Vector2 from, LocalMap? map,
            Random rng, TaskType activity, int linger, int minRadius, int maxRadius)
        {
            if (map == null)
                return new BehaviorTask(activity, from, 5f,
                    interruptible: true, arrivalLinger: linger);

            int cx = (int)(from.X / LocalMap.TileSize);
            int cy = (int)(from.Y / LocalMap.TileSize);

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
                    return new BehaviorTask(activity,
                        TileToPixel((tx, ty)), 5f,
                        tileX: tx, tileY: ty,
                        interruptible: true, arrivalLinger: linger);
                }
            }
            return new BehaviorTask(activity, from, 5f,
                interruptible: true, arrivalLinger: linger);
        }

        // ── Task effects (Roadmap §3.8) ─────────────────────────────────────
        private static void ApplyTaskEffect(Smurf s, BehaviorTask t, LocalMap? map,
            ColonyResources r, float dt, IReadOnlyList<Smurf> smurfs,
            Random rng, long globalTick)
        {
            switch (t.Type)
            {
                case TaskType.Eat:
                    // v0.3.46 (Phase 4) — Eat now resolves a specific item
                    // from the colony Inventory instead of decrementing a
                    // float. Pulls the highest-scoring (nutrition × quality
                    // × preference) Fresh/Stale food stack; consumes one
                    // unit; restores nutrition from the item's actual
                    // BaseNutrition × QualityMul. Emits a thought keyed
                    // off the item's quality + the eater's preference for
                    // its sub-type, so a smurf who likes Smurfberries and
                    // ate a Fine Smurfberry stack gets the
                    // AteFavorite/+8 thought.
                    var stack = r.Inventory.FindBestFood(s);
                    if (stack != null)
                    {
                        bool wasFamished = s.Nutrition < 15f;
                        var def = ItemRegistry.Get(stack.Kind, stack.SubType);
                        float perUnit = (def?.BaseNutrition ?? 5f)
                                      * QualityMeta.NutritionMul(stack.Quality);
                        if (stack.State == ItemState.Stale) perUnit *= 0.6f;
                        s.Nutrition = MathF.Min(100f, s.Nutrition + perUnit);
                        r.Inventory.Consume(stack, 1);

                        // Pick the thought: preference + quality decide.
                        string key;
                        if (s.Preferences != null && s.Preferences.LikesItem(stack.SubType))
                            key = "AteFavorite";
                        else if (s.Preferences != null && s.Preferences.DislikesItem(stack.SubType))
                            key = "AteDisliked";
                        else
                            key = QualityMeta.MealThoughtKey(stack.Quality);
                        if (wasFamished) key = "AteHungry";
                        ThoughtRegistry.Add(s, key, stack.SubType);
                    }
                    break;
                case TaskType.Sleep:
                    s.Rest      = MathF.Min(100f, s.Rest      + SleepRate * dt);
                    // v0.3.43 — no beds yet, so sleeping always emits a
                    // negative "SleptOnGround". Phase 5 buildings will gate
                    // this with a positive "WellRested" when a bed is found.
                    if (s.Rest > 80f) ThoughtRegistry.Add(s, "SleptOnGround");
                    break;
                case TaskType.Socialize:
                    s.Social    = MathF.Min(100f, s.Social    + SocializeRate * dt);
                    SkillRegistry.GainXp(s, "Social", 0.06f);   // sustained — per-tick
                    break;
                case TaskType.Attune:
                    s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt);
                    if (s.MagicResonance > 85f) ThoughtRegistry.Add(s, "Attuned");
                    SkillRegistry.GainXp(s, "Arcane", 0.08f);   // sustained — per-tick
                    break;
                case TaskType.SeekSafety:
                    s.Safety    = MathF.Min(100f, s.Safety    + SeekSafetyRate * dt);
                    if (s.Safety > 80f) ThoughtRegistry.Add(s, "FoundSafety");
                    break;
                case TaskType.Heal:
                    // Heal own most-damaged body part as a placeholder; Phase 4
                    // wires Caretaker targeting other smurfs.
                    string? worst = null;
                    float   low   = 100f;
                    foreach (var (part, cond) in s.BodyParts)
                        if (cond < low) { worst = part; low = cond; }
                    if (worst != null)
                        s.BodyParts[worst] = MathF.Min(100f, low + HealRate * dt);
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
                            int yield = (int)MathF.Round(FoodYield(vegType));
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
                                    skillLevel: SkillLevel(s, "Foraging"),
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
                                    skillLevel: SkillLevel(s, "Foraging"),
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
                            SkillRegistry.GainXp(s, "Foraging", 40f);
                        }
                        // v0.3.21 — once harvested, the Gather designation is
                        // fulfilled. Clear it so the overlay glyph disappears
                        // and the next idle smurf doesn't reroute here.
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
                            int yield = tile.Terrain switch
                            {
                                TerrainType.Boulder    => 4,
                                TerrainType.DeadLog    => 4,
                                TerrainType.LivingWood => 6,
                                _                      => 0,
                            };
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
                            // wood. Smurfs would then walk away leaving
                            // a half-chopped tree standing, the player
                            // would re-designate, and in dense chop
                            // clusters the colony jittered between
                            // adjacent partial trees. Now: one arrival
                            // fells the whole tree, drops total wood,
                            // tile flips passable in the same call.
                            var vegType = slot.Type;
                            int perYield = (int)MathF.Round(WoodYield(vegType));
                            byte taken = map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            int totalYield = perYield * taken;
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
                            SkillRegistry.GainXp(s, "Foraging", 60f);
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.38 — Cut Plants: clear any vegetation. No resource.
                // Used to free up playfield space; for Phase 4 this can be
                // re-tagged to drop a small "Cuttings" item.
                case TaskType.CutVegetation:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var slot = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        if (slot.IsPresent && !slot.IsDepleted)
                        {
                            // v0.4.15 — Cut is now single-shot like
                            // ChopWood: one arrival clears the whole
                            // plant. The intent of Cut is "clear this
                            // tile" — leaving partial yield was just an
                            // accident of routing through HarvestVegetation.
                            // v0.4.17 — decoration vegetation (Underbrush,
                            // MossPatch) has BaseYield = 0 and the
                            // VegetationSlot.IsDepleted check explicitly
                            // excludes them. FullyDepleteVegetation can't
                            // visually "deplete" them — the slot stays
                            // looking alive forever. For these we clear
                            // the slot entirely so the Cut order actually
                            // removes them. Harvestable types still go
                            // through FullyDeplete so they leave a stump
                            // and regrow on schedule.
                            var vegType = slot.Type;
                            if (vegType == VegetationType.Underbrush
                                || vegType == VegetationType.MossPatch)
                                map.ClearVegetation(t.TargetTileX, t.TargetTileY);
                            else
                                map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            // v0.4.2 — Cut now drops a Cuttings item per
                            // the player brief: "Cut should produce items
                            // from any vegetation it designates". Quantity
                            // scales with vegetation size — see
                            // CuttingsFromVegetation.
                            var cut = ItemFactory.CuttingsFromVegetation(vegType);
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            var item = ItemFactory.Create(
                                ItemKind.Material, cut.SubType, cut.Material,
                                rng, globalTick,
                                skillLevel: SkillLevel(s, "Foraging"),
                                quantity: cut.Quantity);
                            item.TilePos = dropPos;
                            map.DropItem(item);
                            EmitWorkThought(s, TaskType.CutVegetation, null);
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Botany XP per cut. Lower than
                            // chop because cut typically clears decorative
                            // vegetation; less skill-relevant.
                            SkillRegistry.GainXp(s, "Botany", 30f);
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.43 — idle effects. The smurf has arrived at their
                // chosen idle destination; ApplyTaskEffect handles the
                // "what does this activity actually do" side-effects, and
                // the main tick loop sets IdleLingerTicks so the smurf
                // holds at the destination for ArrivalLinger ticks instead
                // of immediately re-picking.
                case TaskType.Loiter:
                    // Loiter is the "doing nothing in particular" activity.
                    // Tiny idle thought, no need changes. Emit only on
                    // ~10 % of arrivals so the thoughts pane doesn't get
                    // spammed with the same headline.
                    if (r != null /* keep r warning-free */ && (s.Id.GetHashCode() & 7) == 0)
                        ThoughtRegistry.Add(s, "Wandered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.6f);   // v0.4.63
                    break;

                case TaskType.Observe:
                    // Standing and watching. Boosts Social slightly (people-
                    // watching is social!) and emits a Daydreamed thought.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.3f);
                    ThoughtRegistry.Add(s, "Daydreamed");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.8f);   // v0.4.63
                    break;

                case TaskType.Converse:
                {
                    // Boost both this smurf and the partner's Social, build
                    // friendship over repeated chats, and emit a thought
                    // that depends on whether they like each other.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt);
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt);   // v0.4.63 — full Joy gain
                    Smurf? partner = null;
                    if (t.TargetId != null)
                    {
                        foreach (var o in smurfs)
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
                        // "a smurf should actually have a two way
                        // conversation with another smurf that engages
                        // both and lasts until they're done speaking."
                        TryLockConversePartner(s, partner, t.ArrivalLinger);

                        partner.Social = MathF.Min(100f, partner.Social + SocializeRate * dt);
                        // Build affinity. Three positive chats = friend.
                        bool weDislike   = s.Preferences?.DislikesSmurf(partner.Name) ?? false;
                        bool theyDislike = partner.Preferences?.DislikesSmurf(s.Name) ?? false;
                        if (weDislike || theyDislike)
                        {
                            ThoughtRegistry.Add(s,       "ChatWithEnemy", partner.Name);
                            ThoughtRegistry.Add(partner, "ChatWithEnemy", s.Name);
                        }
                        else
                        {
                            bool weLike   = s.Preferences?.LikesSmurf(partner.Name) ?? false;
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
                    s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt * 0.5f);
                    ThoughtRegistry.Add(s, "Pondered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.7f);   // v0.4.63
                    break;

                case TaskType.VisitFavorite:
                    // Phase 4 will route this to a remembered location; for
                    // now the activity is just a longer-distance wander
                    // with a positive memory thought on arrival.
                    ThoughtRegistry.Add(s, "VisitedSpot");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 1.2f);   // v0.4.63 — best per-tick
                    break;

                // v0.4.0 — Phase-5-deferred task stubs. Both are reachable
                // through the Jobs tab today (the player can set their
                // Haul / Cook priorities) but neither has a workplace yet:
                //   Haul → needs Phase 5 stockpile zones to know where to
                //          carry the item. Without a destination tile the
                //          stub clears the task and the smurf re-evaluates.
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

                case TaskType.Wander:
                    // v0.4.63 (G4) — basic idle wander gives modest Joy.
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.5f);
                    // v0.5.5 — multi-hop chain. If WanderHopsRemaining > 0,
                    // pick a fresh destination, swap it into CurrentTask,
                    // reset arrival state so the smurf walks again, and
                    // bump WorkSearchCooldownTicks high enough to cover
                    // the next leg's walk + linger (so the chain isn't
                    // interrupted by a workAvailable re-eval mid-leg).
                    // Pathfind the new destination immediately so the
                    // section-2a A* gate doesn't need to re-fire.
                    //
                    // Sam: "a smurf should actually take a short walk and
                    // finish it when 'taking a walk'." A 2-4 leg chain at
                    // 8-28 tiles per leg gives ~10-25 sec of visible
                    // wandering, matching the RimWorld feel of pawns
                    // "going for a walk" between work shifts.
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
                                s.PathWaypoints, _smurfPerTile, OccTileIdx(s));
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

        // v0.3.43 — emit a thought matching how a smurf feels about a work
        // task they just completed. The mapping uses Preferences.LikesActivity
        // for the activity itself (Foraging / Excavating / …) and an
        // optional item-axis lookup for the specific yield (Smurfberry vs
        // Pineshroom). Cheap — called once per completion, not per tick.
        private static void EmitWorkThought(Smurf s, TaskType type, string? itemName)
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
        private static string? ItemNameFor(SmurfulationC.World.VegetationType v) => v switch
        {
            SmurfulationC.World.VegetationType.SmurfberryBush  => "Smurfberry",
            SmurfulationC.World.VegetationType.SmallMushroom   => "SmallMushroom",
            SmurfulationC.World.VegetationType.LargeMushroom   => "SmallMushroom",
            SmurfulationC.World.VegetationType.HerbCluster     => "HerbCluster",
            SmurfulationC.World.VegetationType.MagicFlower     => "MagicBerry",
            SmurfulationC.World.VegetationType.PineShroom      => "SmallMushroom",
            SmurfulationC.World.VegetationType.PalmShroom      => "SmallMushroom",
            SmurfulationC.World.VegetationType.SmallSandshroom => "SmallMushroom",
            SmurfulationC.World.VegetationType.LargeSandshroom => "SmallMushroom",
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
        // smurfs' targets are skipped so the colony spreads work across
        // available tiles instead of all rushing the same one.
        // v0.3.40 — pass the FIFO avoid array directly to the find methods.
        // LocalMap iterates the (4-slot) array per candidate designation;
        // entries with TicksLeft == 0 are inactive and skipped.
        //
        // v0.4.29 — passes an `approachBlocked` callback that consults the
        // per-tick occupancy grid. A candidate excavate target is rejected
        // when *every* passable 8-neighbour is already occupied by a
        // *different* smurf — i.e. the only ways into the dig face are
        // currently blocked by colleagues. This prevents the cascade where
        // 5+ smurfs claim adjacent boulders in a single-tile tunnel and
        // immediately jam each other up. A target with no passable
        // neighbours at all is left to IsWorkReachable to filter (already
        // handled). A target with at least one open passable approach
        // passes through unchanged.
        private static (int x, int y)? FindDesignatedExcavation(Smurf s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;

            var pos = map.FindNearestExcavate(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.4.29 — true iff every passable 8-neighbour of (tx, ty) is
        // currently occupied by some smurf other than the caller. If there
        // are no passable neighbours at all, returns false (let
        // IsWorkReachable veto that case — the answer here is "doesn't
        // apply", not "approach is blocked"). Cheap: at most 8 array reads
        // against the per-tick occupancy grid.
        private static bool IsApproachFullyOccupied(LocalMap map, int tx, int ty, int currentSmurfTileIdx)
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
                if (!TileHasOtherSmurf(nIdx, currentSmurfTileIdx))
                    return false;   // at least one open approach → not blocked
            }
            return anyPassable;   // had passable neighbours, all occupied
        }

        // v0.5.7 — Gather / ChopWood / Cut now pass the same approach-
        // occupancy callback as FindDesignatedExcavation (v0.4.29). The
        // omission meant smurfs would claim approach-blocked Gather /
        // Chop / Cut targets even when 50 smurfs were converging on a
        // small cluster, get stuck against the occupied perimeter for the
        // full StuckThreshold window, then re-pick the same tile next
        // idle cycle (per-smurf AvoidTiles only blacklists for one
        // smurf, not globally). RimWorld parity — JobGiver_Work
        // surface vetoes targets whose reservation surface is fully
        // occupied.
        private static (int x, int y)? FindDesignatedGather(Smurf s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestGather(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        private static (int x, int y)? FindDesignatedChopWood(Smurf s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestChopWood(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        private static (int x, int y)? FindDesignatedCut(Smurf s, LocalMap map)
        {
            int curTileX = (int)(s.SimPos.X / LocalMap.TileSize);
            int curTileY = (int)(s.SimPos.Y / LocalMap.TileSize);
            int curTileIdx = _occGridWidth > 0 ? curTileY * _occGridWidth + curTileX : -1;
            var pos = map.FindNearestCut(s.SimPos, s.Id, s.AvoidTiles,
                approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx));
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        // v0.3.33 (B.7) — builds a designation task AND records the soft-
        // claim on the tile so other smurfs scanning won't try to pick the
        // same target. Released when:
        //   • The smurf completes the task (ApplyTaskEffect → ClearDesignationsAt
        //     auto-releases via the lock in LocalMap).
        //   • The smurf is forced into re-evaluation (Wander, critical need).
        //   • The smurf gives up after StuckThreshold ticks of zero progress.
        //   • The player removes the designation via the Remove tool.
        private static BehaviorTask ClaimAndMakeDesignationTask(
            Smurf s, LocalMap map, TaskType type, (int x, int y) tile, float priority)
        {
            map.TryClaim(tile.x, tile.y, s.Id);
            return new BehaviorTask(type, TileToPixel(tile), priority,
                tileX: tile.x, tileY: tile.y);
        }

        // Releases the smurf's claim on the current task's target tile, if
        // the task is a designation type. Called whenever the smurf abandons
        // or replaces its current task. Safe to call when CurrentTask is
        // null or not a designation type — just no-ops.
        private static void ReleaseTaskClaim(Smurf s, LocalMap? map)
        {
            if (s.CurrentTask is not { } t) return;

            // v0.4.7 (bugreport B-3) — release haul reservations on
            // task abandonment. Without this, the per-item reservation
            // dict in HaulSystem leaked an entry every time a smurf
            // got pulled off a Haul (critical need, stuck-detector,
            // player re-order) — eventually every dropped item was
            // "reserved" by long-departed haulers and the colony
            // stopped hauling.
            if (t.Type == TaskType.Haul && t.TargetId != null)
            {
                HaulSystem.ReleaseByIdString(t.TargetId);
            }

            if (map == null) return;
            // v0.3.38 — extended to release claims on the new Chop Wood /
            // Cut Plants task types too.
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

        // NudgeToPassable removed v0.3.22 — its only call site was the
        // "everything is blocked" branch in the inline movement code, which
        // teleported the smurf up to one tile away. The new MoveOneTick stays
        // put when fully blocked and rescues bad SimPos via
        // NearestPassableTileCentre (BFS, not random sampling).

        private static float FoodYield(VegetationType type) => type switch
        {
            VegetationType.SmurfberryBush  => 3f,
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
        // on the smurf's activity preference. +8 for liked, -6 for disliked,
        // 0 neutral. The asymmetric magnitudes mirror RimWorld's "liked
        // activities resist interruption more strongly than disliked ones
        // are avoided" pattern — work still has to get done.
        private static float PreferenceTilt(Smurf s, string activity)
        {
            var prefs = s.Preferences;
            if (prefs == null) return 0f;
            if (prefs.LikesActivity   (activity)) return +8f;
            if (prefs.DislikesActivity(activity)) return -6f;
            return 0f;
        }

        // v0.3.46 (Phase 4) — skill lookup used by ItemFactory.Create to
        // shift the quality bell upward for a smurf with relevant
        // training. Returns 0 if the smurf hasn't learned this skill.
        private static int SkillLevel(Smurf s, string skill) =>
            s.Skills != null && s.Skills.TryGetValue(skill, out int lvl) ? lvl : 0;

        private static bool HasTrait(Smurf s, string trait) =>
            s.Traits.TryGetValue(trait, out float v) && v >= 0.5f;

        private static bool HasPersonality(Smurf s, string trait) =>
            s.Personality.Contains(trait);
    }

    // Roadmap §3.9 — player order envelope queued from the main thread.
    public readonly struct PlayerOrder
    {
        public readonly string  SmurfName;
        public readonly Vector2 Target;
        public PlayerOrder(string smurfName, Vector2 target)
        { SmurfName = smurfName; Target = target; }
    }
}
