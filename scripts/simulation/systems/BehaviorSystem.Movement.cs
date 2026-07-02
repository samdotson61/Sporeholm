using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — movement — steering the active shroomp toward its task target (v0.3.22+).
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
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

    }
}
