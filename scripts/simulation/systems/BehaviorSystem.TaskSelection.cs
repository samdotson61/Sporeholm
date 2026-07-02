using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — task selection — the three-tier picker (critical needs → role → idle).
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
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

            // v0.7.1 (Phase 7) — drafted-colonist hold is handled in the
            // per-shroomp loop (after the combat / medical passes), not here, so
            // it doesn't re-run task selection + auto-equip every tick.

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

                // v0.8.0 (Phase 8) — farm work (Grow priority). A grow-zone tile
                // is any tile carrying a CropSlot (LocalMap._crops). Harvest ripe
                // crops first (don't let yield rot), then sow empty plots. Mirrors
                // the GatherFood designation-work lifecycle: claim the tile, walk,
                // ApplyTaskEffect on arrival. Time-based growth (TickCrops) runs in
                // SimulationCore regardless of who tends.
                if (designationsOk && jobOk("Grow") && map.HasAnyCrop())
                {
                    // Finders skip tiles reserved by another shroomp + recently-
                    // abandoned (avoid) tiles and return the nearest REACHABLE plot,
                    // so Growers spread across the field instead of converging.
                    var ripe = FindDesignatedHarvest(s, map);
                    if (ripe.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.HarvestCrop,
                            ripe.Value, 55f + jobTilt("Grow"));
                    var sow = FindDesignatedSow(s, map);
                    if (sow.HasValue)
                        return ClaimAndMakeDesignationTask(s, map, TaskType.PlantCrop,
                            sow.Value, 45f + jobTilt("Grow"));
                }

                // v0.8.1 (Phase 8) — butcher a hunted creature's corpse (Hunt
                // priority). Reservation-aware finder so butchers don't converge.
                if (designationsOk && jobOk("Hunt"))
                {
                    var corpse = FindNearestButcherCorpse(s, map);
                    if (corpse != null)
                    {
                        int btx = (int)(corpse.SimPos.X / LocalMap.TileSize);
                        int bty = (int)(corpse.SimPos.Y / LocalMap.TileSize);
                        map.TryClaim(btx, bty, s.Id);
                        return new BehaviorTask(TaskType.Butcher, TileToPixel((btx, bty)),
                            52f + jobTilt("Hunt"), tileX: btx, tileY: bty,
                            targetId: corpse.Id.ToString());
                    }
                }

                // v0.8.2 (Phase 8) — tame a marked wild creature (Husbandry job).
                // No tile claim: multiple tamers just add to the shared
                // TamingProgress (team taming), so convergence is harmless.
                if (designationsOk && jobOk("Husbandry"))
                {
                    var beast = FindNearestTameTarget(s, map);
                    if (beast != null)
                    {
                        int ttx = (int)(beast.SimPos.X / LocalMap.TileSize);
                        int tty = (int)(beast.SimPos.Y / LocalMap.TileSize);
                        return new BehaviorTask(TaskType.Tame, TileToPixel((ttx, tty)),
                            48f + jobTilt("Husbandry"), tileX: ttx, tileY: tty,
                            targetId: beast.Id.ToString());
                    }
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

                // v0.6.2 — Demolish-as-task. Construct-priority shroomps
                // pick up MarkedForDemolition tiles when no Build work is
                // available (the explicit build queue still wins on
                // priority — see canFrameForBuild above). Returns null if
                // no marked-and-reachable structure exists.
                if (jobOk("Construct"))
                {
                    var demT = DemolishSystem.SelectTarget(s, map, resources);
                    if (demT.HasValue)
                    {
                        var c = demT.Value;
                        return new BehaviorTask(c.Type, c.Target,
                            (isCrafter ? 50f : 35f) + jobTilt("Construct"),
                            interruptible: c.Interruptible,
                            tileX: c.TargetTileX, tileY: c.TargetTileY);
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
            // v0.7.2 — combat drill. Small base so off-duty colonists
            // occasionally train; Guardians get a big nudge below. Only
            // actually fires when a training building is reachable
            // (NewTrainTask returns null → wander fallback otherwise).
            int wTrain     = 2;

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
            if (s.Role == "Guardian") wTrain  += 18;   // v0.7.2 — Guardians drill

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
            if (wTrain    < 0) wTrain    = 0;

            int total = wWander + wLoiter + wObserve + wConverse + wMeditate + wVisitFav + wTrain;
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
            // v0.7.2 — Train when a building is reachable, else fall back to a
            // multi-hop wander (no in-place drill — the equipment is the point).
            if ((roll -= wTrain)    < 0) return NewTrainTask(s, map, rng) ?? NewWanderTask(s, map, rng);
            return NewVisitFavoriteTask(s, map, rng);
        }

        private static bool IsIdleType(TaskType t) =>
               t == TaskType.Wander
            || t == TaskType.Loiter
            || t == TaskType.Observe
            || t == TaskType.Converse
            || t == TaskType.Meditate
            || t == TaskType.VisitFavorite
            || t == TaskType.Train;   // v0.7.2 — idle-selected; needs may interrupt

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

    }
}
