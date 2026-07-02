using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — rescue pass (Phase 7, v0.7.2) — carrying downed colonists to beds.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
        // ── v0.7.2 (Phase 7) — Rescue pass ────────────────────────────────
        // A Doctor / Caretaker carries a DOWNED colonist to the nearest bed and
        // deposits them there to rest + be treated. Self-contained movement
        // (CombatStepToward); the downed victim cannot move itself, so the
        // carrier drives the victim's SimPos each tick.
        private const float RescuePickupRangeTiles = 1.6f;

        private static bool TryHandleRescue(Shroomp s, LocalMap? map,
            IReadOnlyList<Shroomp> shroomps, float dt)
        {
            if (map == null || !s.IsAlive || s.IsDowned || s.IsBeingCarried) return false;
            if (s.CombatTargetId != null) return false;                       // combat preempts
            if (!(JobPriorityOn(s, "Doctor") || s.Role == "Caretaker")) return false;

            // Resolve the current carry victim (if any).
            Shroomp? victim = s.CarriedShroompId.HasValue
                ? FindShroompById(shroomps, s.CarriedShroompId.Value) : null;
            // v0.7.2 review fix — release the carry if the victim died OR recovered
            // (stood back up) mid-carry: set them down so they become a valid
            // rescuee/patient again and the carrier is free to re-evaluate. Before
            // this, a victim that healed past the stand-up threshold while carried
            // kept IsBeingCarried=true and was dragged while also running its own
            // pipeline (dual position writer).
            if (victim != null && (!victim.IsAlive || !victim.IsDowned))
            {
                DepositCarried(s, victim, s.SimPos);
                victim = null;
            }

            if (victim == null)
            {
                // No bed anywhere → nothing to carry them to; leave the downed
                // for the medical pass to treat in place.
                int rtx = (int)(s.SimPos.X / LocalMap.TileSize);
                int rty = (int)(s.SimPos.Y / LocalMap.TileSize);
                if (!map.FindNearestBed(rtx, rty).HasValue) return false;

                victim = FindRescuee(s, shroomps, map);
                if (victim == null)
                {
                    if (s.CurrentTask is { Type: TaskType.Rescue }) s.CurrentTask = null;
                    return false;
                }
            }

            bool carrying = s.CarriedShroompId == victim.Id;

            if (!carrying)
            {
                // Phase 1 — walk to the downed victim, then pick up.
                s.CurrentTask = new BehaviorTask(TaskType.Rescue, victim.SimPos, 90f,
                    interruptible: true, targetId: victim.Id.ToString());
                float r = RescuePickupRangeTiles * LocalMap.TileSize;
                if (s.SimPos.DistanceSquaredTo(victim.SimPos) <= r * r)
                {
                    s.CarriedShroompId = victim.Id;
                    victim.IsBeingCarried = true;
                }
                else
                {
                    CombatStepToward(s, victim.SimPos, map, dt);
                }
                return true;
            }

            // Phase 2 — carrying: head to the nearest bed + deposit.
            int tx = (int)(s.SimPos.X / LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
            // v0.7.2 review fix — prefer a bed not already occupied by another
            // downed/being-carried colonist so two rescuers don't stack victims
            // on one bed. Falls back to the bare nearest bed if every bed is
            // taken (better to double up than to strand them on the floor).
            var bed = FindNearestFreeBedForRescue(victim, shroomps, map, tx, ty)
                      ?? map.FindNearestBed(tx, ty);
            if (!bed.HasValue)
            {
                DepositCarried(s, victim, s.SimPos);   // bed vanished mid-carry — set down here
                return true;
            }
            Vector2 dest = new Vector2(
                bed.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                bed.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            s.CurrentTask = new BehaviorTask(TaskType.Rescue, dest, 90f,
                interruptible: false, tileX: bed.Value.X, tileY: bed.Value.Y,
                targetId: victim.Id.ToString());

            float arrive = 0.75f * LocalMap.TileSize;
            if (s.SimPos.DistanceSquaredTo(dest) <= arrive * arrive)
            {
                s.SimPos = dest;
                s.PrevSimPos = dest;
                DepositCarried(s, victim, dest);
                return true;
            }

            CombatStepToward(s, dest, map, dt);
            // The downed victim cannot move itself, so drag it along.
            victim.SimPos = s.SimPos;
            victim.PrevSimPos = s.SimPos;
            victim.SimTarget = s.SimPos;
            victim.PathWaypoints.Clear();
            return true;
        }

        private static void DepositCarried(Shroomp carrier, Shroomp victim, Vector2 at)
        {
            victim.SimPos = at;
            victim.PrevSimPos = at;
            victim.SimTarget = at;
            victim.PathWaypoints.Clear();
            victim.IsBeingCarried = false;
            carrier.CarriedShroompId = null;
            carrier.CurrentTask = null;
        }

        // v0.7.2 review fix — release an in-progress rescue carry, setting the
        // carried victim down at `at` so it becomes a valid FindRescuee /
        // FindPatient target again. Called from every path that ABANDONS a carry
        // mid-route (carrier downed, carrier pulled into combat). Normal arrival
        // uses DepositCarried; carrier death is handled in SimulationCore.
        private static void ReleaseCarry(Shroomp carrier, IReadOnlyList<Shroomp> shroomps, Vector2 at)
        {
            if (!carrier.CarriedShroompId.HasValue) return;
            var carried = FindShroompById(shroomps, carrier.CarriedShroompId.Value);
            if (carried != null)
            {
                carried.SimPos = at;
                carried.PrevSimPos = at;
                carried.SimTarget = at;
                carried.PathWaypoints.Clear();
                carried.IsBeingCarried = false;
            }
            carrier.CarriedShroompId = null;
        }

        // v0.7.3 (N20) — point the shroomp at a specific patrol waypoint: assign
        // a Patrol task and compute the A* route to it. Mirrors the chain-order
        // pop so the movement pipeline follows a real path across walls.
        // v0.7.4 (#16) — returns false when the waypoint is unreachable (walled
        // off / different region) so the caller can skip ahead instead of letting
        // the shroomp thrash against a wall until stuck-detection bails.
        private static bool AssignPatrolHop(Shroomp s, LocalMap? map, Godot.Vector2 target)
        {
            int tx = (int)(target.X / LocalMap.TileSize);
            int ty = (int)(target.Y / LocalMap.TileSize);
            s.CurrentTask = new BehaviorTask(TaskType.Patrol, target, 95f,
                isPlayerOrder: true, interruptible: true, tileX: tx, tileY: ty);
            s.SimTarget = target;
            s.PathWaypoints.Clear();
            s.StuckTicks = 0;
            s.RePathTried = false;
            if (map == null) return true;
            return Pathfinder.FindPath(map, s.SimPos, (tx, ty),
                s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
        }

        // v0.7.4 (#16) — point at the current patrol waypoint, skipping ahead to
        // the next REACHABLE one if the current is walled off. Returns false when
        // no waypoint is reachable from here (the last hop is still assigned, so
        // MoveOneTick + stuck-detection handle it gracefully and a later tick or
        // map change can open a route). Each waypoint is tried at most once, so
        // this never loops forever even if the whole route is unreachable.
        private static bool AssignReachablePatrolHop(Shroomp s, LocalMap? map)
        {
            int n = s.PatrolWaypoints.Count;
            for (int tries = 0; tries < n; tries++)
            {
                if (AssignPatrolHop(s, map, s.PatrolWaypoints[s.PatrolIndex])) return true;
                s.PatrolIndex = (s.PatrolIndex + 1) % n;
            }
            return false;
        }

        // v0.7.3 (N8) — mental-break tuning.
        private const int   MentalBreakDurationTicks = 720;    // ~12 s spell at 1×
        private const int   MentalBreakCooldownTicks = 3600;   // ~1 min before another can fire
        private const float MentalBreakChancePerTick = 0.0010f;

        // Begin a mental break: drop the current task, pick a kind. The mental
        // break pass then owns the shroomp until MentalBreakTicks elapses.
        private static void StartMentalBreak(Shroomp s, LocalMap? map, Random rng)
        {
            if (s.CurrentTask != null) ReleaseTaskClaim(s, map);
            s.PathWaypoints.Clear();
            int roll = rng.Next(100);
            s.MentalBreak = roll < 45 ? MentalBreakType.SadWander
                          : roll < 75 ? MentalBreakType.Daze
                          : MentalBreakType.Tantrum;
            s.MentalBreakTicks   = MentalBreakDurationTicks;
            s.BreakRetargetTicks = 0;
            s.BreakWanderTarget  = Godot.Vector2.Zero;
            s.CurrentTask = new BehaviorTask(TaskType.MentalBreak, s.SimPos, 90f, interruptible: false);
        }

        // Drive an active mental break + end it on expiry (Catharsis relief
        // thought + a cooldown so it doesn't immediately re-trigger).
        private static void TickMentalBreak(Shroomp s, LocalMap? map, float dt, Random rng, int tickInterval)
        {
            s.MentalBreakTicks -= tickInterval;
            if (s.MentalBreakTicks <= 0)
            {
                s.MentalBreak = MentalBreakType.None;
                s.MentalBreakCooldown = MentalBreakCooldownTicks;
                s.CurrentTask = null;
                s.BreakWanderTarget = Godot.Vector2.Zero;
                ThoughtRegistry.Add(s, "Catharsis");
                return;
            }
            if (s.MentalBreak == MentalBreakType.Daze)
            {
                s.PrevSimPos = s.SimPos;   // stand catatonic
                return;
            }
            // SadWander / Tantrum — drift to a periodically re-rolled nearby point.
            s.BreakRetargetTicks -= tickInterval;
            if (s.BreakRetargetTicks <= 0 || s.BreakWanderTarget == Godot.Vector2.Zero)
            {
                float radiusTiles = s.MentalBreak == MentalBreakType.Tantrum ? 5f : 3f;
                // v0.7.4 (#23) — pick a PASSABLE nearby point so the break-wander
                // doesn't lock the shroomp against a wall. Try a few candidates;
                // if none are passable, stand put for this retarget window.
                Godot.Vector2 cand = s.SimPos;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    double ang = rng.NextDouble() * System.Math.PI * 2.0;
                    float dist = (float)rng.NextDouble() * radiusTiles * LocalMap.TileSize;
                    var c = s.SimPos + new Godot.Vector2(
                        (float)System.Math.Cos(ang), (float)System.Math.Sin(ang)) * dist;
                    if (map == null
                        || map.IsPassable((int)(c.X / LocalMap.TileSize), (int)(c.Y / LocalMap.TileSize)))
                    { cand = c; break; }
                }
                s.BreakWanderTarget = cand;
                s.BreakRetargetTicks = s.MentalBreak == MentalBreakType.Tantrum ? 45 : 90;
            }
            CombatStepToward(s, s.BreakWanderTarget, map, dt);
        }

        // v0.7.2 review fix — nearest Bed not already occupied by another downed
        // or being-carried colonist (so rescuers don't stack victims on one bed).
        // Returns null when every bed is occupied; the caller then falls back to
        // the bare nearest bed. The occupancy set is tiny (only incapacitated
        // shroomps), so the per-bed inner check stays cheap.
        private static (int X, int Y)? FindNearestFreeBedForRescue(
            Shroomp victim, IReadOnlyList<Shroomp> shroomps, LocalMap map, int fx, int fy)
        {
            Span<int> occX = stackalloc int[16];
            Span<int> occY = stackalloc int[16];
            int n = 0;
            for (int i = 0; i < shroomps.Count && n < occX.Length; i++)
            {
                var p = shroomps[i];
                if (ReferenceEquals(p, victim) || !p.IsAlive) continue;
                if (!p.IsDowned && !p.IsBeingCarried) continue;
                occX[n] = (int)(p.SimPos.X / LocalMap.TileSize);
                occY[n] = (int)(p.SimPos.Y / LocalMap.TileSize);
                n++;
            }
            (int X, int Y)? best = null; int bd = int.MaxValue;
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width;  x++)
            {
                if (map.GetStructure(x, y).Type != StructureType.Bed) continue;
                bool taken = false;
                for (int k = 0; k < n; k++)
                    if (occX[k] == x && occY[k] == y) { taken = true; break; }
                if (taken) continue;
                int ddx = x - fx, ddy = y - fy;
                int d = ddx * ddx + ddy * ddy;
                if (d < bd) { bd = d; best = (x, y); }
            }
            return best;
        }

        // Nearest downed colonist that needs carrying to a bed (reachable, not
        // already on a bed, not already being carried).
        private static Shroomp? FindRescuee(Shroomp rescuer, IReadOnlyList<Shroomp> shroomps, LocalMap map)
        {
            int dx = (int)(rescuer.SimPos.X / LocalMap.TileSize);
            int dy = (int)(rescuer.SimPos.Y / LocalMap.TileSize);
            Shroomp? best = null; float bd = float.MaxValue;
            for (int i = 0; i < shroomps.Count; i++)
            {
                var p = shroomps[i];
                if (ReferenceEquals(p, rescuer) || !p.IsAlive) continue;
                if (!p.IsDowned || p.IsBeingCarried) continue;
                int px = (int)(p.SimPos.X / LocalMap.TileSize);
                int py = (int)(p.SimPos.Y / LocalMap.TileSize);
                // Already lying on a bed → no need to move them.
                if (map.InBounds(px, py) && map.GetStructure(px, py).Type == StructureType.Bed) continue;
                if (!map.IsWorkReachable(dx, dy, px, py)) continue;
                float d2 = rescuer.SimPos.DistanceSquaredTo(p.SimPos);
                if (d2 < bd) { bd = d2; best = p; }
            }
            return best;
        }

        private static Shroomp? FindShroompById(IReadOnlyList<Shroomp> shroomps, Guid id)
        {
            for (int i = 0; i < shroomps.Count; i++)
                if (shroomps[i].Id == id) return shroomps[i];
            return null;
        }

    }
}
