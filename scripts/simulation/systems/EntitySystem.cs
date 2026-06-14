using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Entities;
using Sporeholm.Simulation.Combat;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.6.0 (Phase 6 — Roadmap §6.3) — entity tick driver. State
    // machine per Roadmap: Wander / Hunt / Flee / Graze / Tamed / Dead.
    // Movement is direct (no A*); entities don't navigate built structures
    // and instead just step toward their target, respecting tile
    // passability. This keeps the per-tick cost dominated by the shroomp
    // pipeline — entities run at a tiny fraction of shroomp cost (no
    // pathfinder, no inventory, no skills, no equipment).
    //
    // Per-tick cost budget: ~50 entities × ~5 µs each = 250 µs total at
    // 250 Hz sim tick — well under the 4 ms tick budget. Bear (event-only,
    // 1 instance) doesn't change the picture; large hostile mobs (Wolf
    // pack of 4) are still ~20 µs.
    public static class EntitySystem
    {
        // v0.6.0 — used for line-of-sight + tile-step movement. Larger
        // than the shroomp threshold (0.5 px) because entities have no
        // crowd-aware steering layer; we just want them to stop when they
        // walk into a wall.
        private const float ArrivalPx = 6f;
        private const float StuckPx   = 0.3f;
        // v0.7.0 — ticks a struck Friendly / flee-type entity runs before
        // settling back to Wander (reuses AttackCooldownTicks as the flee timer,
        // matching the existing Flee→Wander gate).
        private const int FleeDurationTicks = 180;   // ~3s @ 60Hz

        public static void Tick(
            IReadOnlyList<Entity> entities,
            IReadOnlyList<Shroomp> shroomps,
            LocalMap map,
            float dt,
            Random rng,
            int currentTick)
        {
            // Two passes per shroomp for proximity queries:
            // (a) Each entity walks the shroomp list to find nearest valid
            //     target (for Hunt / Flee transitions). Linear scan is OK
            //     at colony scale; if we ever hit 250 shroomps × 60 entities
            //     × 60 Hz we'll grid-index this, but not before.
            // v0.6.2 — Nutrition + Rest decay. Tuned slow: ~0.6/in-game-hour
            // for both, which at 60-sec/in-game-hour = ~0.01/sec → ~0.004 per
            // 250 Hz tick. Wild entities full→0 in ~4 in-game days; no behavior
            // impact today (EntityCardPanel just surfaces the value).
            const float NeedDecayPerSec = 0.01f;
            float needDecay = NeedDecayPerSec * dt;

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (!e.IsAlive) continue;
                if (e.AttackCooldownTicks > 0) e.AttackCooldownTicks--;
                e.Nutrition = Math.Clamp(e.Nutrition - needDecay,        0f, 100f);
                e.Rest      = Math.Clamp(e.Rest      - needDecay * 0.6f, 0f, 100f);   // rest decays slower than nutrition

                var def = EntityRegistry.Get(e.Kind);

                // State transitions ─────────────────────────────────────
                switch (e.State)
                {
                    case EntityState.Wander:
                    case EntityState.Graze:
                        // Hostile → look for shroomp in aggro range.
                        if (def.Disposition == Disposition.Hostile && def.AggroRangePx > 0f)
                        {
                            var target = FindNearestShroomp(e.SimPos, shroomps, def.AggroRangePx);
                            if (target != null)
                            {
                                e.State = EntityState.Hunt;
                                e.TargetShroompId = target.Id;
                            }
                        }
                        break;
                    case EntityState.Hunt:
                    {
                        // Target invalid → back to Wander.
                        var target = LookupShroomp(e.TargetShroompId, shroomps);
                        if (target == null || !target.IsAlive)
                        {
                            e.State = EntityState.Wander;
                            e.TargetShroompId = null;
                        }
                        break;
                    }
                    case EntityState.Flee:
                        // Run for N ticks then return to wander.
                        if (e.AttackCooldownTicks <= 0) { e.State = EntityState.Wander; e.TargetShroompId = null; }
                        break;
                    case EntityState.Tamed:
                        // Phase 8 husbandry — follow tamer. No-op for now;
                        // tamed entities just stand near their wander home.
                        break;
                }

                // Behaviour ─────────────────────────────────────────────
                StepEntity(e, def, shroomps, map, dt, rng);
            }
        }

        // Find the nearest shroomp within range. Skips downed / sleeping
        // shroomps from aggro consideration so hostiles don't camp beds —
        // they engage moving / standing targets only.
        private static Shroomp? FindNearestShroomp(Vector2 from, IReadOnlyList<Shroomp> shroomps, float range)
        {
            float r2 = range * range;
            float bestDist2 = float.MaxValue;
            Shroomp? best = null;
            for (int i = 0; i < shroomps.Count; i++)
            {
                var s = shroomps[i];
                if (s == null || !s.IsAlive) continue;
                if (s.IsDowned) continue;
                if (s.IsPacifist) continue;   // hostiles ignore pacifists (don't pick a fight)
                float dx = s.SimPos.X - from.X;
                float dy = s.SimPos.Y - from.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > r2) continue;
                if (d2 < bestDist2) { bestDist2 = d2; best = s; }
            }
            return best;
        }

        private static Shroomp? LookupShroomp(Guid? id, IReadOnlyList<Shroomp> shroomps)
        {
            if (id == null) return null;
            for (int i = 0; i < shroomps.Count; i++)
                if (shroomps[i].Id == id.Value) return shroomps[i];
            return null;
        }

        // Per-tick movement + interaction. Movement is direct (no A*);
        // the entity steps toward its target up to its speed budget.
        // Wander entities pick a new target tile within ±5 tiles of their
        // WanderHome every time they arrive or get stuck.
        private static void StepEntity(Entity e, EntityDef def, IReadOnlyList<Shroomp> shroomps, LocalMap map, float dt, Random rng)
        {
            // Acquire / refresh target by state.
            switch (e.State)
            {
                case EntityState.Wander:
                case EntityState.Graze:
                    if (e.WanderHopsRemaining <= 0 || (e.SimPos - e.SimTarget).LengthSquared() <= ArrivalPx * ArrivalPx)
                        e.SimTarget = PickWanderTarget(e, map, rng);
                    break;
                case EntityState.Hunt:
                {
                    var target = LookupShroomp(e.TargetShroompId, shroomps);
                    if (target != null) e.SimTarget = target.SimPos;
                    break;
                }
                case EntityState.Flee:
                {
                    var threat = LookupShroomp(e.TargetShroompId, shroomps);
                    if (threat != null)
                    {
                        // Step away from the threat (along the threat-to-self
                        // vector, scaled to flee range).
                        var dir = (e.SimPos - threat.SimPos).Normalized();
                        e.SimTarget = e.SimPos + dir * def.FleeRangePx;
                    }
                    break;
                }
            }

            // Step toward target. Stop at walls (don't try to climb / path).
            float stepBudget = e.Speed * dt;
            var toTarget = e.SimTarget - e.SimPos;
            float dist = toTarget.Length();
            if (dist <= ArrivalPx)
            {
                e.WanderHopsRemaining = 0;
                return;
            }
            var dirN = toTarget / dist;
            float stepLen = Math.Min(stepBudget, dist);
            var newPos = e.SimPos + dirN * stepLen;
            // Wall-clip: if the new tile is impassable, halt this tick and
            // re-roll a new wander target next tick.
            int tx = (int)(newPos.X / LocalMap.TileSize);
            int ty = (int)(newPos.Y / LocalMap.TileSize);
            if (!map.InBounds(tx, ty) || !map.IsPassable(tx, ty))
            {
                e.WanderHopsRemaining = 0;
                return;
            }
            e.SimPos = newPos;

            // Attack contact for Hunt state — routed through the shared Phase 7
            // combat engine, which owns range, cooldown, hit/dodge/block rolls,
            // body-part wounds + bleeding, and death via CauseOfDeath.Combat.
            // (Replaces the v0.6.0 ApplyEntityAttack stub, whose pre-rename
            // part-name pool silently no-op'd ~half of all hits.)
            if (e.State == EntityState.Hunt)
            {
                var target = LookupShroomp(e.TargetShroompId, shroomps);
                if (target != null)
                    CombatSystem.TryAttack(e, target);
            }
        }

        // Wander target picker — within a ~5-tile radius of WanderHome,
        // landing on a passable tile. Falls back to current pos if the
        // map is too cramped (prevents infinite loops in worst case).
        private static Vector2 PickWanderTarget(Entity e, LocalMap map, Random rng)
        {
            int homeTx = (int)(e.WanderHome.X / LocalMap.TileSize);
            int homeTy = (int)(e.WanderHome.Y / LocalMap.TileSize);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int dx = rng.Next(-5, 6);
                int dy = rng.Next(-5, 6);
                int tx = homeTx + dx, ty = homeTy + dy;
                if (!map.InBounds(tx, ty) || !map.IsPassable(tx, ty)) continue;
                e.WanderHopsRemaining = 1;
                return new Vector2(
                    tx * LocalMap.TileSize + LocalMap.TileSize / 2,
                    ty * LocalMap.TileSize + LocalMap.TileSize / 2);
            }
            e.WanderHopsRemaining = 0;
            return e.SimPos;   // sat in place if everything's blocked
        }

        // v0.7.0 (Phase 7) — react to being attacked by a shroomp. Friendlies
        // and flee-when-struck hostiles (Magic Wisp) flee toward safety; all
        // other hostiles and provoked neutrals turn on the attacker. Called by
        // BehaviorSystem when a colonist lands (or attempts) a strike, so a
        // hunted boar fights back and a struck glowbunny bolts.
        public static void ProvokeEntity(Entity e, Guid attackerId)
        {
            if (e == null || !e.IsAlive) return;
            var def = EntityRegistry.Get(e.Kind);
            bool fleer = def.Disposition == Disposition.Friendly
                         || (def.Disposition == Disposition.Hostile && def.FleeRangePx > 0f);
            if (fleer)
            {
                e.State = EntityState.Flee;
                e.TargetShroompId = attackerId;
                e.AttackCooldownTicks = FleeDurationTicks;   // reused as the flee timer
            }
            else
            {
                e.State = EntityState.Hunt;        // neutral retaliation / focus the attacker
                e.TargetShroompId = attackerId;
            }
        }
    }
}
