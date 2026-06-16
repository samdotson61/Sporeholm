using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Entities;
using Sporeholm.Simulation.Combat;
using Sporeholm.Simulation.Items;
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
        // v0.8.5 — a tamed animal in a pasture grazes within this many tiles of
        // its current spot; beyond that it heads back toward the nearest pasture cell.
        private const int PastureGrazeRadiusTiles = 6;

        public static void Tick(
            IReadOnlyList<Entity> entities,
            IReadOnlyList<Shroomp> shroomps,
            LocalMap map,
            float dt,
            Random rng,
            int currentTick,
            List<Entity> birthsOut)   // v0.8.3 — breeding appends newborns here; caller merges into the roster
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

                // v0.8.2 (Phase 8) — tamed livestock: stay fed by the colony (slow
                // Nutrition trickle so they don't starve in a pen) and drop produce
                // (milk / wool / eggs) on a cooldown, hauled to stockpiles.
                if (e.IsTamed)
                {
                    e.Nutrition = Math.Clamp(e.Nutrition + needDecay * 1.5f, 0f, 100f);
                    e.Rest      = Math.Clamp(e.Rest      + needDecay * 1.5f, 0f, 100f);   // v0.8.3 — cared-for livestock rest too
                    if (e.ProduceCooldownTicks > 0) e.ProduceCooldownTicks--;
                    else if (TryDropProduce(e, map, rng, currentTick))
                        e.ProduceCooldownTicks = ProduceCooldownTicksFull;
                    // v0.8.3 (Phase 8) — breeding: a tamed Breeds-tagged pair raises young.
                    if (AgriculturalTags.Has(e.Kind, AgriculturalTag.Breeds))
                        TickBreeding(e, entities, birthsOut, rng);
                }
                // v0.8.2 — a hungry WILD grazer switches to Graze (abstract
                // foraging) to recover; recovers fully then resumes wandering.
                bool grazer = AgriculturalTags.Has(e.Kind, AgriculturalTag.Grazer);
                if (e.State == EntityState.Graze)
                    e.Nutrition = Math.Clamp(e.Nutrition + needDecay * 4f, 0f, 100f);

                // State transitions ─────────────────────────────────────
                switch (e.State)
                {
                    case EntityState.Wander:
                    case EntityState.Graze:
                        // Hostile → look for shroomp in aggro range. A TAMED
                        // creature never aggros the colony (even a tamed predator).
                        if (!e.IsTamed && def.Disposition == Disposition.Hostile && def.AggroRangePx > 0f)
                        {
                            var target = FindNearestShroomp(e.SimPos, shroomps, def.AggroRangePx);
                            if (target != null)
                            {
                                e.State = EntityState.Hunt;
                                e.TargetShroompId = target.Id;
                            }
                        }
                        // Hungry wild grazer → graze; sated grazer → back to wander.
                        else if (grazer && !e.IsTamed && !e.MarkedForTame)
                        {
                            if (e.State == EntityState.Wander && e.Nutrition < 45f)
                                e.State = EntityState.Graze;
                            else if (e.State == EntityState.Graze && e.Nutrition > 85f)
                                e.State = EntityState.Wander;
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
            // v0.8.2 (Phase 8) — a creature the player marked for taming holds
            // still (but isn't yet tamed) so the Husbandry colonist can walk up
            // to it instead of chasing a wandering target.
            if (e.MarkedForTame && !e.IsTamed) return;

            // Acquire / refresh target by state.
            switch (e.State)
            {
                case EntityState.Wander:
                case EntityState.Graze:
                    if (e.WanderHopsRemaining <= 0 || (e.SimPos - e.SimTarget).LengthSquared() <= ArrivalPx * ArrivalPx)
                        e.SimTarget = PickWanderTarget(e, map, rng);
                    break;
                case EntityState.Tamed:
                    // v0.8.5 (Phase 8) — tamed livestock graze within the nearest
                    // PASTURE if one is painted (soft containment); otherwise roam
                    // near where they were tamed (WanderHome).
                    if (e.WanderHopsRemaining <= 0 || (e.SimPos - e.SimTarget).LengthSquared() <= ArrivalPx * ArrivalPx)
                    {
                        var pasture = map.HasAnyPasture()
                            ? map.PastureWanderTarget(e.SimPos.X, e.SimPos.Y, PastureGrazeRadiusTiles, rng)
                            : null;
                        if (pasture.HasValue)
                        {
                            e.SimTarget = new Vector2(
                                pasture.Value.X * LocalMap.TileSize + LocalMap.TileSize / 2,
                                pasture.Value.Y * LocalMap.TileSize + LocalMap.TileSize / 2);
                            e.WanderHopsRemaining = 1;
                        }
                        else e.SimTarget = PickWanderTarget(e, map, rng);
                    }
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

        // v0.8.2 (Phase 8) — produce cooldown for tamed livestock (~half an
        // in-game day at the TickBody rate). Public so the tame-completion path
        // can seed it (a freshly-tamed animal waits a full cooldown before its
        // first drop instead of dumping produce the instant it's tamed).
        public const int ProduceCooldownTicksFull = 30000;

        // v0.8.2 (Phase 8) — drop a tamed creature's produce on its tile when the
        // cooldown lapses: Milkable→Milk, EggLayer→Egg, Shearable→Wool (a creature
        // can carry several tags, e.g. Shroomgoat = Milk + Wool). Returns true if
        // anything dropped, so the caller resets the cooldown. The normal haul
        // loop carries the produce to a stockpile.
        private static bool TryDropProduce(Entity e, LocalMap map, Random rng, int currentTick)
        {
            bool any = false;
            var pos = e.SimPos;
            void Drop(string sub, Sporeholm.Simulation.Items.MaterialKey? mat = null)
            {
                var kind = ItemRegistry.KindForSubType(sub);
                if (kind == null) return;
                var item = ItemFactory.Create(kind.Value, sub, mat, rng, currentTick, quantity: 1);
                item.TilePos = pos;
                map.DropItem(item);
                any = true;
            }
            if (AgriculturalTags.Has(e.Kind, AgriculturalTag.Milkable))  Drop("Milk");
            if (AgriculturalTags.Has(e.Kind, AgriculturalTag.EggLayer))  Drop("Egg");
            // v0.8.7 — sheared wool is explicitly Spore Wool (Cloth/Wool) so it
            // resolves a real material instead of rolling a random cloth subtype.
            if (AgriculturalTags.Has(e.Kind, AgriculturalTag.Shearable)) Drop("Wool", new Sporeholm.Simulation.Items.MaterialKey("Cloth","Wool"));
            return any;
        }

        // v0.8.3 (Phase 8) — breeding tuning. Gestation ~1.5 in-game days at the
        // TickBody rate; a per-species tamed cap so a herd grows to a ceiling and
        // then holds (RimWorld-style) instead of exploding.
        private const int GestationTicksFull  = 90000;
        private const int BreedPopCapPerKind  = 8;
        private const float BreedRangeTiles   = 5f;

        // v0.8.3 (Phase 8) — one tamed, well-fed Breeds creature per nearby
        // same-species cluster gestates (the lowest-Guid member, so a cluster
        // yields one pregnancy at a time, not one per animal). At term it births
        // a tamed young into birthsOut, respecting the per-species cap.
        private static void TickBreeding(Entity e, IReadOnlyList<Entity> entities, List<Entity> birthsOut, Random rng)
        {
            if (e.Nutrition < 50f) return;   // only well-fed animals breed
            if (e.GestationTicks > 0)
            {
                e.GestationTicks--;
                // Count pending newborns from THIS tick too — they aren't in the
                // entity roster yet (they sit in birthsOut until the caller merges
                // them), so without this two clusters birthing on the same tick
                // would both read a stale count and overshoot the cap.
                if (e.GestationTicks <= 0
                    && CountTamedOfKind(entities, e.Kind) + CountKindIn(birthsOut, e.Kind) < BreedPopCapPerKind)
                {
                    var young = Entity.SpawnAt(e.Kind, e.SimPos, rng);
                    young.IsTamed              = true;
                    young.TamedByName          = e.TamedByName;
                    young.State                = EntityState.Tamed;
                    young.WanderHome           = e.SimPos;
                    young.ProduceCooldownTicks = ProduceCooldownTicksFull;
                    birthsOut.Add(young);
                }
                return;
            }
            if (CountTamedOfKind(entities, e.Kind) + CountKindIn(birthsOut, e.Kind) >= BreedPopCapPerKind) return;
            float r = LocalMap.TileSize * BreedRangeTiles, r2 = r * r;
            bool hasPartner = false, isLowestId = true;
            for (int i = 0; i < entities.Count; i++)
            {
                var p = entities[i];
                if (p == e || !p.IsAlive || !p.IsTamed || p.Kind != e.Kind || p.Nutrition < 50f) continue;
                if (e.SimPos.DistanceSquaredTo(p.SimPos) > r2) continue;
                hasPartner = true;
                if (p.Id.CompareTo(e.Id) < 0) { isLowestId = false; break; }
            }
            if (hasPartner && isLowestId) e.GestationTicks = GestationTicksFull;
        }

        private static int CountTamedOfKind(IReadOnlyList<Entity> entities, EntityKind kind)
        {
            int n = 0;
            for (int i = 0; i < entities.Count; i++)
                if (entities[i].IsAlive && entities[i].IsTamed && entities[i].Kind == kind) n++;
            return n;
        }

        // v0.8.3 — count this-tick newborns of `kind` still queued in birthsOut
        // (not yet in the roster), so same-tick births can't overshoot the cap.
        private static int CountKindIn(List<Entity> list, EntityKind kind)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Kind == kind) n++;
            return n;
        }

        // v0.7.0 (Phase 7) — react to being attacked by a shroomp. Friendlies
        // and flee-when-struck hostiles (Magic Wisp) flee toward safety; all
        // other hostiles and provoked neutrals turn on the attacker. Called by
        // BehaviorSystem when a colonist lands (or attempts) a strike, so a
        // hunted boar fights back and a struck glowbunny bolts.
        public static void ProvokeEntity(Entity e, Guid attackerId)
        {
            if (e == null || !e.IsAlive) return;
            // v0.8.2 (Phase 8) — a tamed colony animal never turns on the colony,
            // even if struck (friendly-fire, a future slaughter order, stray AoE).
            // It stays tamed + peaceful rather than flipping to Hunt/Flee.
            if (e.IsTamed) return;
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
