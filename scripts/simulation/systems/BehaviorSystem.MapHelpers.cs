using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — map helpers — tile queries shared by selection/effects, plus supporting types.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
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

        // v0.8.0 (Phase 8) — farm-work finders. Mirror FindDesignatedGather but pass
        // NO approach-occupancy callback: crops sit on passable tiles the shroomp
        // stands on, so the per-tile reservation (not a blocked impassable perimeter)
        // is the anti-convergence guard. Sow is additionally Botany-gated.
        private static (int x, int y)? FindDesignatedHarvest(Shroomp s, LocalMap map)
        {
            var pos = map.FindNearestRipeCrop(s.SimPos, s.Id, s.AvoidTiles);
            return pos.HasValue ? (pos.Value.X, pos.Value.Y) : null;
        }

        private static (int x, int y)? FindDesignatedSow(Shroomp s, LocalMap map)
        {
            var pos = map.FindNearestSowable(s.SimPos, s.Id, SkillLevel(s, "Botany"), s.AvoidTiles);
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
                && t.Type != TaskType.ChopWood && t.Type != TaskType.CutVegetation
                && t.Type != TaskType.PlantCrop && t.Type != TaskType.HarvestCrop  // v0.8.0 Phase 8
                && t.Type != TaskType.Butcher) return;                             // v0.8.1 Phase 8
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
