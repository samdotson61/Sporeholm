using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — combat pass (Phase 7, v0.7.0) — strike resolution, draft, patrol, engagement.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
        // ── v0.7.0 (Phase 7) — Combat pass ────────────────────────────────
        // Self-contained: combatants move + strike here, bypassing the normal
        // task/path/arrival pipeline (which assumes static targets). Returns
        // true when the shroomp is engaged this tick (caller skips the rest).
        private const float CombatXpPerSwing = 8f;

        private static bool TryHandleCombat(Shroomp s, LocalMap? map, float dt, Random rng)
        {
            bool engaged = ResolveCombatAndAct(s, map, dt, rng);
            if (!engaged)
            {
                // Don't leave a stale combat display-task behind when disengaging.
                if (s.CurrentTask is { } ct && (ct.Type == TaskType.Attack || ct.Type == TaskType.Flee))
                    s.CurrentTask = null;
                if (s.CombatTargetName == "enemy") s.CombatTargetName = null;
                s.CombatPursuitTicks = 0;
            }
            return engaged;
        }

        private static bool ResolveCombatAndAct(Shroomp s, LocalMap? map, float dt, Random rng)
        {
            // 1. Explicit (player-ordered) target takes priority.
            if (s.CombatTargetId.HasValue)
            {
                var ordered = FindCombatEntity(s.CombatTargetId.Value);
                if (ordered == null)
                {
                    s.CombatTargetId = null;            // target died / despawned
                    s.CombatPursuitTicks = 0;
                }
                else if (s.IsPacifist)
                {
                    s.CombatTargetId = null;            // pacifists never hold an attack order
                    s.CombatPursuitTicks = 0;
                    return DoFlee(s, ordered.SimPos, map, dt);
                }
                else if (s.CombatPursuitTicks > Combat.CombatTuning.MaxPursuitTicks)
                {
                    // gave up — couldn't close on a faster / fleeing target
                    s.CombatTargetId = null;
                    s.CombatPursuitTicks = 0;
                    // v0.8.1 — record the abandoned target on a cooldown so Hunt
                    // acquisition doesn't instantly re-lock the same uncatchable
                    // prey next tick (which would livelock the hunter). Harmless
                    // for player attack orders — those don't re-acquire via Hunt.
                    s.RecentHuntGiveUp      = ordered.Id;
                    s.RecentHuntGiveUpTicks = HuntGiveUpCooldownTicks;
                }
                else if (s.HealthFraction < Combat.CombatTuning.FleeHealthFraction)
                {
                    // Wounded: break off and flee, but KEEP the order so the
                    // colonist re-engages once healed above the threshold.
                    return DoFlee(s, ordered.SimPos, map, dt);
                }
                else
                {
                    return DoAttack(s, ordered, map, dt, rng);
                }
            }

            // 2. Auto-defense (no standing order).
            bool hasWeapon = s.EquippedWeapon is { } ew
                && ew.State != Sporeholm.Simulation.Items.ItemState.Broken;

            if (s.IsPacifist || (!hasWeapon && !s.IsDrafted))
            {
                // Pacifists, and unarmed colonists who AREN'T drafted, flee a
                // creature actively hunting them rather than trade blows. A
                // drafted colonist holds the line with fists if unarmed.
                var threat = NearestHuntingHostile(s, Combat.CombatTuning.SelfDefenseEngageTiles);
                return threat != null && DoFlee(s, threat.SimPos, map, dt);
            }

            // Armed (or drafted) colonist — engage nearby threats. Guardians and
            // the drafted range further. Skip the scan when no threats exist.
            if (_combatHasHostiles)
            {
                float tiles = (s.Role == "Guardian" || s.IsDrafted)
                    ? Combat.CombatTuning.GuardianEngageTiles
                    : Combat.CombatTuning.SelfDefenseEngageTiles;
                var foe = NearestHostile(s, tiles);
                if (foe != null)
                {
                    if (s.HealthFraction < Combat.CombatTuning.FleeHealthFraction)
                        return DoFlee(s, foe.SimPos, map, dt);
                    return DoAttack(s, foe, map, dt, rng);
                }
            }
            return false;
        }

        private static bool DoAttack(Shroomp s, Entities.Entity target, LocalMap? map, float dt, Random rng)
        {
            EnterCombatTask(s, map, TaskType.Attack, target);
            s.CombatTargetName = "enemy";   // ⚔ sword icon

            var profile = s.GetAttackProfile();
            float rangePx = profile.RangeTiles * LocalMap.TileSize;
            if (s.SimPos.DistanceSquaredTo(target.SimPos) <= rangePx * rangePx)
            {
                s.CombatPursuitTicks = 0;   // closed on the target — reset the leash
                s.SimTarget = s.SimPos;
                s.PathWaypoints.Clear();
                s.PrevSimPos = s.SimPos;
                if (Combat.CombatSystem.TryAttack(s, target))
                {
                    EntitySystem.ProvokeEntity(target, s.Id);
                    bool ranged = Combat.CombatProfiles.IsRanged(profile.Type);
                    SkillRegistry.GainXp(s, ranged ? "Ranged" : "Melee", CombatXpPerSwing);
                }
            }
            else
            {
                s.CombatPursuitTicks++;     // still closing — counts toward the leash
                CombatStepToward(s, target.SimPos, map, dt);
            }
            return true;
        }

        private static bool DoFlee(Shroomp s, Vector2 threatPos, LocalMap? map, float dt)
        {
            EnterCombatTask(s, map, TaskType.Flee, null);
            s.CombatTargetName = null;
            Vector2 dir = s.SimPos - threatPos;
            if (dir.LengthSquared() < 0.01f) dir = new Vector2(1f, 0f);
            dir = dir.Normalized();
            CombatStepToward(s, s.SimPos + dir * (LocalMap.TileSize * 4f), map, dt);
            return true;
        }

        // Set a combat display-task (so the roster shows Fighting / Fleeing),
        // releasing any prior work claim once on entry. Refreshed each tick.
        private static void EnterCombatTask(Shroomp s, LocalMap? map, TaskType type, Entities.Entity? target)
        {
            bool alreadyCombat = s.CurrentTask is { } ct
                && (ct.Type == TaskType.Attack || ct.Type == TaskType.Flee);
            if (!alreadyCombat)
            {
                if (s.CurrentTask != null) ReleaseTaskClaim(s, map);
                s.IdleLingerTicks = 0;
            }
            Vector2 tpos = target != null ? target.SimPos : s.SimPos;
            s.CurrentTask = new BehaviorTask(type, tpos, 95f, interruptible: true,
                targetId: target?.Id.ToString());
        }

        // Direct steering toward a pixel target at the colonist's move speed
        // (folding in injury slowdown), halting at impassable tiles. Mirrors the
        // entity stepper; used only by the combat pass.
        private static void CombatStepToward(Shroomp s, Vector2 targetPos, LocalMap? map, float dt)
        {
            s.SimTarget = targetPos;
            s.PathWaypoints.Clear();
            float speed = s.SimSpeed * Math.Max(0.15f, s.ComputeMovingCapacity());
            float step  = speed * dt;
            Vector2 to  = targetPos - s.SimPos;
            float dist  = to.Length();
            if (dist <= 0.5f) { s.PrevSimPos = s.SimPos; return; }
            Vector2 dir = to / dist;
            float stepLen = Math.Min(step, dist);
            Vector2 newPos = s.SimPos + dir * stepLen;
            if (map != null)
            {
                int tx = (int)(newPos.X / LocalMap.TileSize);
                int ty = (int)(newPos.Y / LocalMap.TileSize);
                if (!map.InBounds(tx, ty) || !map.IsPassable(tx, ty))
                {
                    s.PrevSimPos = s.SimPos;
                    return;   // blocked — hold position (open-field combat assumption)
                }
            }
            s.PrevSimPos = s.SimPos;
            s.SimPos = newPos;
        }

        private static Entities.Entity? FindCombatEntity(Guid id)
        {
            for (int i = 0; i < _entities.Count; i++)
                if (_entities[i].Id == id)
                    return _entities[i].IsAlive ? _entities[i] : null;
            return null;
        }

        // v0.8.1 — like FindCombatEntity but returns the entity in ANY state
        // (incl. a dead corpse AwaitingButchery), used by the Butcher task.
        private static Entities.Entity? FindEntityAnyState(Guid id)
        {
            for (int i = 0; i < _entities.Count; i++)
                if (_entities[i].Id == id) return _entities[i];
            return null;
        }

        // v0.8.1 — how long a hunter skips a prey it just gave up chasing (the
        // pursuit leash bailed). Long enough that it does other work / hunts other
        // prey in between, instead of instantly re-locking the same uncatchable
        // target (~30 s at 1×; the leash itself is MaxPursuitTicks = 600).
        private const int HuntGiveUpCooldownTicks = 1800;

        // v0.8.1 — nearest alive, untamed, player-marked huntable creature the
        // colonist can reach. Fed into CombatTargetId so the combat pass kills it.
        private static Entities.Entity? FindNearestHuntTarget(Shroomp s, LocalMap? map)
        {
            Entities.Entity? best = null; float bestD = float.MaxValue;
            int cx = (int)(s.SimPos.X / LocalMap.TileSize);
            int cy = (int)(s.SimPos.Y / LocalMap.TileSize);
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (!e.IsAlive || !e.MarkedForHunt || e.IsTamed) continue;
                // Skip a prey this colonist recently gave up chasing (cooldown).
                if (s.RecentHuntGiveUpTicks > 0 && s.RecentHuntGiveUp == e.Id) continue;
                if (map != null)
                {
                    int ex = (int)(e.SimPos.X / LocalMap.TileSize);
                    int ey = (int)(e.SimPos.Y / LocalMap.TileSize);
                    if (!map.AreReachable(cx, cy, ex, ey)) continue;
                }
                float d = s.SimPos.DistanceSquaredTo(e.SimPos);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // v0.8.2 — nearest alive, untamed, player-marked tameable creature the
        // colonist can reach. No reservation (team taming is fine).
        private static Entities.Entity? FindNearestTameTarget(Shroomp s, LocalMap map)
        {
            Entities.Entity? best = null; float bestD = float.MaxValue;
            int cx = (int)(s.SimPos.X / LocalMap.TileSize);
            int cy = (int)(s.SimPos.Y / LocalMap.TileSize);
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (!e.IsAlive || !e.MarkedForTame || e.IsTamed) continue;
                int ex = (int)(e.SimPos.X / LocalMap.TileSize);
                int ey = (int)(e.SimPos.Y / LocalMap.TileSize);
                if (!map.AreReachable(cx, cy, ex, ey)) continue;
                float d = s.SimPos.DistanceSquaredTo(e.SimPos);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // v0.8.1 — nearest reachable, unclaimed corpse awaiting butchery. The
        // reservation skip keeps two butchers from converging on one corpse.
        private static Entities.Entity? FindNearestButcherCorpse(Shroomp s, LocalMap map)
        {
            Entities.Entity? best = null; float bestD = float.MaxValue;
            int cx = (int)(s.SimPos.X / LocalMap.TileSize);
            int cy = (int)(s.SimPos.Y / LocalMap.TileSize);
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (!e.AwaitingButchery) continue;
                int ex = (int)(e.SimPos.X / LocalMap.TileSize);
                int ey = (int)(e.SimPos.Y / LocalMap.TileSize);
                if (map.IsClaimedByOther(ex, ey, s.Id)) continue;
                if (!map.AreReachable(cx, cy, ex, ey)) continue;
                float d = s.SimPos.DistanceSquaredTo(e.SimPos);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        // v0.8.1 — does a built structure of `type` sit within `radius` tiles of
        // (x,y)? Used for the Butcher-Slab proximity yield bonus.
        private const int ButcherSlabBonusRadius = 8;
        private static bool HasBuiltStructureNear(LocalMap map, int x, int y, StructureType type, int radius)
        {
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (!map.InBounds(nx, ny)) continue;
                if (map.GetStructure(nx, ny).Type == type) return true;
            }
            return false;
        }

        // Nearest threat within `tiles`: a Hostile-disposition creature OR any
        // creature (incl. a provoked Neutral) actively hunting THIS colonist.
        private static Entities.Entity? NearestHostile(Shroomp s, float tiles)
        {
            float r = tiles * LocalMap.TileSize, r2 = r * r;
            Entities.Entity? best = null; float bd = float.MaxValue;
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (!e.IsAlive) continue;
                if (e.IsTamed) continue;   // v0.8.2 — tamed colony animals are never foes
                bool threat = (e.State == Entities.EntityState.Hunt && e.TargetShroompId == s.Id)
                    || Entities.EntityRegistry.Get(e.Kind).Disposition == Entities.Disposition.Hostile;
                if (!threat) continue;
                float d2 = s.SimPos.DistanceSquaredTo(e.SimPos);
                if (d2 <= r2 && d2 < bd) { bd = d2; best = e; }
            }
            return best;
        }

        // Cheap once-per-tick scan: are there any threats on the map at all?
        private static bool AnyCombatHostile(IReadOnlyList<Entities.Entity> es)
        {
            for (int i = 0; i < es.Count; i++)
            {
                var e = es[i];
                if (!e.IsAlive || e.IsTamed) continue;   // v0.8.2 — tamed animals aren't threats
                if (e.State == Entities.EntityState.Hunt) return true;
                if (Entities.EntityRegistry.Get(e.Kind).Disposition == Entities.Disposition.Hostile) return true;
            }
            return false;
        }

        // Nearest live entity actively Hunting (any disposition — a provoked
        // neutral counts) within `tiles`. Used for flee decisions.
        private static Entities.Entity? NearestHuntingHostile(Shroomp s, float tiles)
        {
            float r = tiles * LocalMap.TileSize, r2 = r * r;
            Entities.Entity? best = null; float bd = float.MaxValue;
            for (int i = 0; i < _entities.Count; i++)
            {
                var e = _entities[i];
                if (!e.IsAlive || e.IsTamed || e.State != Entities.EntityState.Hunt) continue;   // v0.8.2 — skip tamed
                float d2 = s.SimPos.DistanceSquaredTo(e.SimPos);
                if (d2 <= r2 && d2 < bd) { bd = d2; best = e; }
            }
            return best;
        }

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
                if (s.CurrentTask is { Type: TaskType.Sleep or TaskType.Rescue } st
                    && st.TargetTileX >= 0 && st.TargetTileY >= 0)
                {
                    s.SimPos = walkTo;   // land ON the bed tile (sleep, or rescue-deposit)
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
                    // v0.8.0 — use IsDesignationTaskType (now includes PlantCrop/
                    // HarvestCrop) so a Grower that gives up on a jammed plot
                    // blacklists it, matching the other two abandon paths.
                    if (s.CurrentTask is BehaviorTask gtask
                        && IsDesignationTaskType(gtask.Type)
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
            || t == TaskType.BuildHaul                      // v0.5.60
            || t == TaskType.PlantCrop || t == TaskType.HarvestCrop  // v0.8.0 Phase 8
            || t == TaskType.Butcher;                                 // v0.8.1 Phase 8

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

    }
}
