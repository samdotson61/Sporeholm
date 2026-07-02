using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — task constructors — builders for every task the selector can issue.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
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
        private const int LingerTrain     = 600;   // ≈ 10 sec — v0.7.2 drill session
        // v0.7.2 — combat drill XP, per second of training. Slower than live
        // combat (CombatXpPerSwing ≈ 8/sec) so fighting still outpaces the
        // yard, but a peacetime colony can build Guardians up over time.
        private const float TrainXpPerSecond = 2.4f;

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
        // introduces points-of-interest (workshops, bonfires, item piles),
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

        // v0.7.2 (Phase 7) — combat drill. Routes an idle colonist to the
        // nearest reachable Sparring Yard (Melee) or Training Dummy (Ranged).
        // Returns null when no training building is reachable, so the caller
        // falls back to a normal idle activity rather than queueing a doomed
        // walk — unlike Meditate, training has NO in-place fallback (the whole
        // point is the equipment). The skill trained is resolved at
        // ApplyTaskEffect time from the structure type at the target tile.
        private static BehaviorTask? NewTrainTask(Shroomp s, LocalMap? map, Random rng)
        {
            if (map == null) return null;
            int tx = (int)(s.SimPos.X / LocalMap.TileSize);
            int ty = (int)(s.SimPos.Y / LocalMap.TileSize);
            var furn = map.FindNearestJoyFurniture(tx, ty,
                new[] { StructureType.SparringYard, StructureType.TrainingDummy });
            if (!furn.HasValue) return null;
            if (!map.AreReachable(tx, ty, furn.Value.X, furn.Value.Y)) return null;
            var pos = new Vector2(
                furn.Value.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                furn.Value.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            return new BehaviorTask(TaskType.Train, pos, 6f,
                tileX: furn.Value.X, tileY: furn.Value.Y,
                interruptible: true, arrivalLinger: LingerTrain);
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

    }
}
