using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — task effects (Roadmap §3.8) — what happens on arrival at each target.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
        // ── Task effects (Roadmap §3.8) ─────────────────────────────────────
        private static void ApplyTaskEffect(Shroomp s, BehaviorTask t, LocalMap? map,
            ColonyResources r, float dt, IReadOnlyList<Shroomp> shroomps,
            Random rng, long globalTick)
        {
            switch (t.Type)
            {
                case TaskType.Eat:
                {
                    // v0.3.46 (Phase 4) — Eat resolves a specific item from
                    // the colony Inventory instead of decrementing a float.
                    // v0.5.68 — also tries map-drop food at the destination
                    // tile (food foraged onto stockpiles never reached the
                    // inventory under the old code path, so a colony with
                    // 40+ Food on the HUD could still starve to death).
                    // Eating order:
                    //   1. Colony Inventory FindBestFood (meals, raw produce).
                    //   2. Map drop at TargetTileX/Y (where MakeEat routed us).
                    // When starving (Nutrition < 25) both paths widen to
                    // include Spoiled food + Corpses (RimWorld urgent-food
                    // fallback). After the ingest the task is cleared so
                    // SelectTask re-evaluates next tick — keeps the shroomp
                    // from looping on a stale Eat task when food has been
                    // exhausted between SelectTask and ApplyTaskEffect.
                    bool wasFamished = s.Nutrition < 15f;
                    bool starving    = s.Nutrition < 25f;

                    Items.Item? consumed = null;
                    bool fromMap = false;
                    bool fromInventory = false;

                    var stack = r.Inventory.FindBestFood(s, allowSpoiled: starving);
                    if (stack != null)
                    {
                        var def = ItemRegistry.Get(stack.Kind, stack.SubType);
                        float perUnit = (def?.BaseNutrition ?? 5f)
                                      * QualityMeta.NutritionMul(stack.Quality);
                        if (stack.State == ItemState.Stale)        perUnit *= 0.6f;
                        else if (stack.State == ItemState.Spoiled) perUnit *= 0.3f;
                        s.Nutrition = MathF.Min(100f, s.Nutrition + perUnit);
                        r.Inventory.Consume(stack, 1);
                        consumed = stack;
                        fromInventory = true;
                    }
                    else if (map != null)
                    {
                        // Map fallback — look on the tile the shroomp is
                        // standing on (MakeEat parked them here). Also accept
                        // food on adjacent tiles to handle the case where the
                        // shroomp arrives close-enough via the climb/yield
                        // path but not exactly on the food tile.
                        int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                        int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                        Items.Item? pick = map.PickupBestFoodAt(curTx, curTy, s,
                            allowSpoiled: starving, allowCorpse: starving);
                        if (pick == null && t.TargetTileX >= 0 && t.TargetTileY >= 0
                            && (t.TargetTileX != curTx || t.TargetTileY != curTy))
                        {
                            pick = map.PickupBestFoodAt(t.TargetTileX, t.TargetTileY, s,
                                allowSpoiled: starving, allowCorpse: starving);
                        }
                        if (pick != null)
                        {
                            float baseNutrition = pick.Kind == Items.ItemKind.Corpse
                                ? 15f
                                : (ItemRegistry.Get(pick.Kind, pick.SubType)?.BaseNutrition ?? 5f);
                            float perUnit = baseNutrition * QualityMeta.NutritionMul(pick.Quality);
                            if (pick.State == Items.ItemState.Stale)        perUnit *= 0.6f;
                            else if (pick.State == Items.ItemState.Spoiled) perUnit *= 0.3f;
                            else if (pick.Kind == Items.ItemKind.Corpse)    perUnit *= 0.5f;
                            s.Nutrition = MathF.Min(100f, s.Nutrition + perUnit);
                            consumed = pick;
                            fromMap = true;
                        }
                    }

                    if (consumed != null)
                    {
                        // Pick the thought: corpse + spoiled take precedence
                        // over normal quality / preference thoughts. AteHungry
                        // overrides quality thoughts (RimWorld "Finally ate"
                        // long-tail mood from going below the Urgent
                        // threshold) but NOT the AteCorpse / AteSpoiled
                        // trauma — eating a body is bad even when starving.
                        string key;
                        if (consumed.Kind == Items.ItemKind.Corpse)
                            key = "AteCorpse";
                        else if (consumed.State == Items.ItemState.Spoiled)
                            key = "AteSpoiled";
                        else if (s.Preferences != null && s.Preferences.LikesItem(consumed.SubType))
                            key = "AteFavorite";
                        else if (s.Preferences != null && s.Preferences.DislikesItem(consumed.SubType))
                            key = "AteDisliked";
                        else if (wasFamished)
                            key = "AteHungry";
                        else
                            key = QualityMeta.MealThoughtKey(consumed.Quality);
                        ThoughtRegistry.Add(s, key, consumed.SubType);

                        // v0.5.37 — AteWithoutTable mood penalty. If the
                        // shroomp is NOT adjacent to a built Table, emit
                        // AteWithoutTable (-3). AteHungry / AteSpoiled /
                        // AteCorpse all suppress the penalty since the
                        // shroomp had no choice in the matter.
                        bool suppressTablePenalty = key == "AteHungry"
                            || key == "AteSpoiled" || key == "AteCorpse";
                        if (map != null && !suppressTablePenalty)
                        {
                            int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            bool nearTable = false;
                            for (int dy = -1; dy <= 1 && !nearTable; dy++)
                            for (int dx = -1; dx <= 1 && !nearTable; dx++)
                            {
                                int nx = curTx + dx, ny = curTy + dy;
                                if (!map.InBounds(nx, ny)) continue;
                                if (map.GetStructure(nx, ny).Type == StructureType.Table)
                                    nearTable = true;
                            }
                            if (!nearTable)
                                ThoughtRegistry.Add(s, "AteWithoutTable");
                            // v0.5.60 B1 — eating at a table satisfies
                            // Social mildly when another shroomp is also
                            // adjacent to a table within 3 tiles. Only the
                            // inventory + table path qualifies; eating off
                            // the floor from a map drop doesn't.
                            else if (fromInventory)
                            {
                                int partnersAtTable = 0;
                                for (int oi = 0; oi < shroomps.Count && partnersAtTable < 1; oi++)
                                {
                                    var o = shroomps[oi];
                                    if (o == s || !o.IsAlive) continue;
                                    int oTx = (int)(o.SimPos.X / LocalMap.TileSize);
                                    int oTy = (int)(o.SimPos.Y / LocalMap.TileSize);
                                    int ddx = oTx - curTx, ddy = oTy - curTy;
                                    if (ddx * ddx + ddy * ddy <= 9) partnersAtTable++;
                                }
                                if (partnersAtTable > 0)
                                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.25f);
                            }
                        }
                    }

                    // v0.5.68 — always clear the Eat task at the end. Whether
                    // we ate or not (food gone, starving with nothing to
                    // find), the task is done. Next tick SelectTask picks
                    // again — re-Eat if still hungry and food exists, or
                    // move on to something else if no food remains. Without
                    // this clear a shroomp could loop forever standing at a
                    // table with an empty inventory.
                    _ = fromMap;   // reserved for future map-eaten hediff hooks
                    s.CurrentTask = null;
                    break;
                }
                case TaskType.Sleep:
                {
                    // v0.5.35 — RestEffectiveness depends on whether the shroomp
                    // is sleeping in a Bed (1.0×) or on the floor (0.8×).
                    // Mirrors RimWorld's bed effectiveness table — sleeping
                    // spot 0.80, vanilla bed 1.00, royal 1.05. Quality
                    // multiplier (Crude 0.86 / Normal 1.0 / Masterwork 1.25)
                    // is applied via Items.QualityMeta.ValueMul/2+0.5
                    // approximation so a masterwork bed grants noticeably
                    // faster rest restoration.
                    float effectiveness = 0.80f;
                    bool atBed = false;
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var bedSlot = map.GetStructure(t.TargetTileX, t.TargetTileY);
                        if (bedSlot.Type == StructureType.Bed)
                        {
                            atBed = true;
                            effectiveness = 1.0f;
                            // Quality bonus (mirrors RimWorld bed quality multipliers
                            // 0.86 → 1.60 across Awful → Legendary; we map our
                            // Crude → Masterwork to a milder 0.86 → 1.25 range).
                            effectiveness *= bedSlot.Quality switch
                            {
                                Items.Quality.Crude      => 0.86f,
                                Items.Quality.Normal     => 1.00f,
                                Items.Quality.Fine       => 1.08f,
                                Items.Quality.Superior   => 1.14f,
                                Items.Quality.Masterwork => 1.25f,
                                Items.Quality.Legendary  => 1.40f,
                                _                        => 1.00f,
                            };
                            // v0.5.84i — material comfort bonus on top
                            // of bed-vs-floor + quality. Sam: "fungalwood
                            // beds and wood beds should be comfortable
                            // while stone beds are less so." Granite bed
                            // (Comfort=0.70) vs fungalwood bed (Comfort=
                            // 1.05) → ~50 % slower rest restoration on
                            // stone. Stack multiplicatively with quality
                            // so a Granite Masterwork bed (0.70 × 1.25 =
                            // 0.875) still beats a FungalWood Crude bed
                            // (1.05 × 0.86 = 0.903) only marginally —
                            // material matters but masterwork crafting
                            // still rewards the player.
                            effectiveness *= StructureMatMeta.Comfort(bedSlot.Material);
                        }
                    }
                    s.Rest = MathF.Min(100f, s.Rest + SleepRate * dt * effectiveness);
                    // v0.5.35 — Wake-time mood thought. WellRested for bed
                    // sleepers, SleptOnGround for floor-sleepers. Fires once
                    // per sleep arc when Rest crosses 80 %.
                    if (s.Rest > 80f)
                    {
                        ThoughtRegistry.Add(s, atBed ? "WellRested" : "SleptOnGround");
                        // v0.5.84t — extra mood boost when the bed is inside
                        // a Bedroom-typed room (Room.Type == Bedroom). RimWorld
                        // parity: "Slept in bedroom" comfort thought.
                        if (atBed && map != null)
                        {
                            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            map.EnsureRooms();
                            var sleepSlot = map.GetStructure(sx, sy);
                            if (sleepSlot.RoomId != 0 && sleepSlot.RoomId != RoomDetector.OutdoorRoomId)
                            {
                                var sleepRoom = map.GetRoom(sleepSlot.RoomId);
                                if (sleepRoom != null && sleepRoom.Type == RoomType.Bedroom)
                                    ThoughtRegistry.Add(s, "SleptInBedroom");
                            }
                        }
                    }
                    // v0.5.68 — wake up when fully rested. RimWorld parity:
                    // Toils_LayDown ends when need_rest >= 1.0. Without this
                    // clause a shroomp who hit Rest=100 during the night-sleep
                    // window (priority 75) keeps sleeping until Nutrition
                    // drops below 20 — wasting the rest of the night and
                    // letting Nutrition degrade far below the comfort floor.
                    // Sam's screenshot: shroomps slept the entire night at
                    // Rest=100 then died of starvation. Clearing the task
                    // forces SelectTask to re-evaluate next tick; if it's
                    // still night they'd re-pick Sleep only when Rest drops
                    // below 80 again (the in-window threshold).
                    if (s.Rest >= 100f)
                    {
                        s.CurrentTask = null;
                        break;
                    }
                    // v0.5.60 B1 — multi-need activity location. Sleeping
                    // near a partner (within 2 tiles) ticks Social mildly.
                    // DF pattern: dwarves sharing bedrooms gain social
                    // need fulfilment passively from proximity. Mild effect
                    // so it doesn't replace Converse / interactions — just
                    // makes shared sleeping quarters feel cohesive.
                    if (atBed && map != null)
                    {
                        int sleepTx = (int)(s.SimPos.X / LocalMap.TileSize);
                        int sleepTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                        for (int oi = 0; oi < shroomps.Count; oi++)
                        {
                            var o = shroomps[oi];
                            if (o == s || !o.IsAlive) continue;
                            if (o.CurrentTask is not { Type: TaskType.Sleep }) continue;
                            int oTx = (int)(o.SimPos.X / LocalMap.TileSize);
                            int oTy = (int)(o.SimPos.Y / LocalMap.TileSize);
                            int dxs = oTx - sleepTx, dys = oTy - sleepTy;
                            if (dxs * dxs + dys * dys <= 4)
                            {
                                s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.15f);
                                break;
                            }
                        }
                    }
                    break;
                }
                case TaskType.Socialize:
                    s.Social    = MathF.Min(100f, s.Social    + SocializeRate * dt);
                    SkillRegistry.GainXp(s, "Social", 0.06f);   // sustained — per-tick
                    break;
                case TaskType.Attune:
                    {
                        // v0.5.84t — apply Focus tool bonus to Attune rate.
                        float attuneToolBonus = GetToolBonusFor(s, TaskType.Attune);
                        s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt * attuneToolBonus);
                        if (s.MagicResonance > 85f) ThoughtRegistry.Add(s, "Attuned");
                        SkillRegistry.GainXp(s, "Magic", 0.08f);   // sustained — per-tick (v0.5.84r: Arcane → Magic)
                    }
                    break;
                case TaskType.SeekSafety:
                    s.Safety    = MathF.Min(100f, s.Safety    + SeekSafetyRate * dt);
                    if (s.Safety > 80f) ThoughtRegistry.Add(s, "FoundSafety");
                    break;
                case TaskType.Heal:
                    // Heal own most-damaged body part as a placeholder; Phase 7
                    // Healer system wires the proper tend-at-bed loop (rescue
                    // downed pawn → carry to bed → Healer treats wounds with
                    // medicine items).
                    // v0.5.84r — flat HealRate replaced by natural biological
                    // healing in NeedsSystem (runs passively on every pawn).
                    // This Heal task now layers an active-tending boost on
                    // top of natural healing using the Healing skill — the
                    // self-tend stub stays in place so the Heal TaskType
                    // remains exercised until Phase 7 ships the real Healer
                    // mechanics. Healer skill scales the bonus 1.0× (lvl 0)
                    // → 2.0× (lvl 20). Awards Healing XP per completion.
                    {
                        string? worst = null;
                        float   low   = 100f;
                        foreach (var (part, cond) in s.BodyParts)
                            if (cond < low && cond > 0f) { worst = part; low = cond; }
                        if (worst != null)
                        {
                            int healSkill = SkillLevel(s, "Healing");
                            float skillMul = 1.0f + 0.05f * healSkill;   // lvl 0 = 1.0, lvl 20 = 2.0
                            float tend = 1.0f * dt * skillMul;          // tend-quality on top of natural heal
                            s.BodyParts[worst] = MathF.Min(100f, low + tend);
                            SkillRegistry.GainXp(s, "Healing", 30f * dt);   // ~30 XP per second of tending
                        }
                    }
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
                            int baseFoodYield = (int)MathF.Round(FoodYield(vegType));
                            // v0.5.30 — Foraging skill scales the food yield.
                            // Lvl 0 = 50 %, lvl 8 = 100 %, lvl 20 = 130 %.
                            // Botched harvest at low skill drops to 50 %.
                            int forageSkillFood = SkillLevel(s, "Botany");
                            // v0.5.84t — apply tool bonus (Basket with GatherFood).
                            float gatherToolBonus = GetToolBonusFor(s, TaskType.GatherFood);
                            int yield = baseFoodYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseFoodYield,
                                SkillCurve.PlantYieldFactor(forageSkillFood) * gatherToolBonus,
                                rng);
                            if (yield > 0 && SkillCurve.HarvestBotch(forageSkillFood, rng))
                                yield = Mathf.Max(1, yield / 2);
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
                                    skillLevel: SkillLevel(s, "Botany"),
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
                                    skillLevel: SkillLevel(s, "Botany"),
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
                            SkillRegistry.GainXp(s, "Botany", 40f);   // v0.5.84r: Foraging merged into Botany
                        }
                        // v0.3.21 — once harvested, the Gather designation is
                        // fulfilled. Clear it so the overlay glyph disappears
                        // and the next idle shroomp doesn't reroute here.
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.PlantCrop:
                    // v0.8.0 (Phase 8) — sow an empty grow-zone tile. The crop is
                    // fixed by the grow-zone (the player paints the crop when
                    // designating); sowing is instant-on-arrival since growth is
                    // autonomous (LocalMap.TickCrops). Botany gates which crops the
                    // shroomp can plant and grants planting XP.
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var cs = map.GetCrop(t.TargetTileX, t.TargetTileY);
                        if (cs.IsEmpty && cs.Crop != CropType.None
                            && CropRegistry.CanPlant(cs.Crop, SkillLevel(s, "Botany")))
                        {
                            cs.Stage = CropStage.Sown;
                            cs.GrowthTicks = 0;
                            map.SetCrop(t.TargetTileX, t.TargetTileY, cs);
                            var pdef = CropRegistry.Get(cs.Crop);
                            EmitWorkThought(s, TaskType.PlantCrop, pdef?.DisplayName ?? "crop");
                            s.TaskDidWork = true;   // v0.4.19
                            SkillRegistry.GainXp(s, "Botany", 12f);   // sowing < harvest XP
                        }
                        map.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
                    }
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.HarvestCrop:
                    // v0.8.0 (Phase 8) — harvest a ripe crop. Drops the crop's yield
                    // item (rolled YieldMin..Max × Botany factor × biome multiplier),
                    // then resets the slot to Empty so the grow-zone re-sows the same
                    // crop. Mirrors the GatherFood drop/XP lifecycle.
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var cs = map.GetCrop(t.TargetTileX, t.TargetTileY);
                        var hdef = CropRegistry.Get(cs.Crop);
                        if (cs.IsRipe && hdef != null && !string.IsNullOrEmpty(hdef.YieldItemSubType))
                        {
                            int botany = SkillLevel(s, "Botany");
                            int baseYield = hdef.YieldMin + rng.Next(hdef.YieldMax - hdef.YieldMin + 1);
                            // Biome emphasis: fungal crops favour roofed (cave) tiles.
                            float biomeMul = map.IsRoofedTile(t.TargetTileX, t.TargetTileY)
                                ? hdef.UndergroundYieldMul : hdef.AboveGroundYieldMul;
                            float harvestToolBonus = GetToolBonusFor(s, TaskType.HarvestCrop);
                            int yield = SkillCurve.ApplyYieldMul(
                                baseYield,
                                SkillCurve.PlantYieldFactor(botany) * biomeMul * harvestToolBonus,
                                rng);
                            if (yield > 0 && SkillCurve.HarvestBotch(botany, rng))
                                yield = Mathf.Max(1, yield / 2);
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            if (yield > 0)
                            {
                                var item = ItemFactory.Create(
                                    hdef.YieldItemKind, hdef.YieldItemSubType,
                                    null, rng, globalTick,
                                    skillLevel: botany, quantity: yield);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            EmitWorkThought(s, TaskType.HarvestCrop, hdef.DisplayName);
                            s.TaskDidWork = true;   // v0.4.19
                            SkillRegistry.GainXp(s, "Botany", 40f);
                            // Reset to Empty (keep Crop) so the plot re-sows itself.
                            cs.Stage = CropStage.Empty;
                            cs.GrowthTicks = 0;
                            map.SetCrop(t.TargetTileX, t.TargetTileY, cs);
                        }
                        map.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
                    }
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.Butcher:
                    // v0.8.1 (Phase 8) — process a hunted creature's corpse into
                    // Meat / Hide / Bone. Yield = ButcherDrops rolled × Cooking-skill
                    // factor × a Butcher-Slab proximity bonus. Grants Cooking XP (+ a
                    // little Husbandry). The corpse is then cleared so it's pruned.
                    if (map != null && t.TargetId != null
                        && Guid.TryParse(t.TargetId, out var butcherId))
                    {
                        var corpse = FindEntityAnyState(butcherId);
                        if (corpse != null && corpse.AwaitingButchery)
                        {
                            int cookSkill = SkillLevel(s, "Cooking");
                            float yieldMul = SkillCurve.ButcherYieldFactor(cookSkill);
                            // A built Butcher Slab within range → +30 % yield.
                            if (HasBuiltStructureNear(map, t.TargetTileX, t.TargetTileY,
                                    StructureType.ButcherSlab, ButcherSlabBonusRadius))
                                yieldMul *= 1.30f;
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            var drops = Entities.EntityRegistry.Get(corpse.Kind).ButcherDrops;
                            for (int di = 0; di < drops.Count; di++)
                            {
                                var (sub, mn, mx) = drops[di];
                                int baseQty = mn + rng.Next(System.Math.Max(1, mx - mn + 1));
                                int qty = SkillCurve.ApplyYieldMul(baseQty, yieldMul, rng);
                                if (qty <= 0) continue;
                                var kind = ItemRegistry.KindForSubType(sub);
                                if (kind == null) continue;   // unknown drop subtype — skip safely
                                var item = ItemFactory.Create(kind.Value, sub, null, rng,
                                    globalTick, skillLevel: cookSkill, quantity: qty);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            EmitWorkThought(s, TaskType.Butcher,
                                Entities.EntityRegistry.Get(corpse.Kind).DisplayName);
                            s.TaskDidWork = true;   // v0.4.19
                            SkillRegistry.GainXp(s, "Cooking", 40f);
                            SkillRegistry.GainXp(s, "Husbandry", 12f);
                            corpse.AwaitingButchery = false;   // cleared → pruned next tick
                        }
                    }
                    // v0.8.1 — release the corpse-tile claim on EVERY exit path
                    // (success / already-butchered / unparseable target), not only
                    // inside the parse guard, so the reservation can never leak.
                    map?.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.Tame:
                    // v0.8.2 (Phase 8) — add taming progress to a marked wild
                    // creature when adjacent. Husbandry scales the per-visit gain;
                    // at 100 it joins the colony. The creature holds still while
                    // marked (EntitySystem), so a single walk-up reaches it; if it
                    // somehow drifted out of range we just re-evaluate + walk again.
                    if (t.TargetId != null && Guid.TryParse(t.TargetId, out var tameId))
                    {
                        var beast = FindEntityAnyState(tameId);
                        if (beast != null && beast.IsAlive && beast.MarkedForTame && !beast.IsTamed)
                        {
                            float near = LocalMap.TileSize * 2.5f;
                            if (s.SimPos.DistanceSquaredTo(beast.SimPos) <= near * near)
                            {
                                int husb = SkillLevel(s, "Husbandry");
                                beast.TamingProgress += SkillCurve.TameProgressPerVisit(husb);
                                SkillRegistry.GainXp(s, "Husbandry", 30f);
                                s.TaskDidWork = true;   // v0.4.19
                                if (beast.TamingProgress >= 100f)
                                {
                                    beast.IsTamed       = true;
                                    beast.TamedByName   = s.Name;
                                    beast.MarkedForTame = false;
                                    beast.MarkedForHunt = false;   // v0.8.2 — a tamed animal is no longer a hunt target
                                    beast.State         = Entities.EntityState.Tamed;
                                    beast.TargetShroompId = null;
                                    // Wait a full cooldown before the first produce drop (don't
                                    // dump milk/wool/eggs the instant it's tamed).
                                    beast.ProduceCooldownTicks = EntitySystem.ProduceCooldownTicksFull;
                                    EmitWorkThought(s, TaskType.Tame,
                                        Entities.EntityRegistry.Get(beast.Kind).DisplayName);
                                    SkillRegistry.GainXp(s, "Husbandry", 60f);   // taming bonus
                                }
                            }
                        }
                    }
                    s.CurrentTask = null;  // re-evaluate next tick
                    break;
                case TaskType.GatherMaterial:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var tile = map.Get(t.TargetTileX, t.TargetTileY);
                        if (tile.DesignatedForExcavation)
                        {
                            // v0.5.84t — per-tick mining. Pre-v0.5.84t mining
                            // was instant-on-arrival, which made the dormant
                            // SkillCurve.MiningSpeedFactor curve invisible.
                            // Now we accumulate work per tick:
                            //   delta = 10 × MiningSpeedFactor(skill) × ToolBonus
                            //   target = WorkAmount(terrain)
                            // Reset progress when the shroomp targets a
                            // different tile or abandons the task. Stays in
                            // this branch until progress reaches the target,
                            // then the original yield/terrain-mutate logic
                            // fires at the bottom.
                            int workTarget = tile.Terrain switch
                            {
                                TerrainType.Boulder    => 200,
                                TerrainType.DeadLog    => 150,
                                TerrainType.LivingWood => 200,
                                TerrainType.Skeleton   => 100,
                                _                      => 0,
                            };
                            if (workTarget > 0)
                            {
                                // Reset progress if we switched targets.
                                if (s.GatherTargetTileX != t.TargetTileX || s.GatherTargetTileY != t.TargetTileY)
                                {
                                    s.GatherProgress    = 0;
                                    s.GatherTargetTileX = t.TargetTileX;
                                    s.GatherTargetTileY = t.TargetTileY;
                                }
                                int miningSkill = SkillLevel(s, "Mining");
                                float toolBonus = GetToolBonusFor(s, TaskType.GatherMaterial);
                                int delta = Mathf.Max(1, (int)(10f * SkillCurve.MiningSpeedFactor(miningSkill) * toolBonus));
                                s.GatherProgress += delta;
                                s.TaskDidWork = true;
                                // Trickle Mining XP per work tick so cold
                                // shroomps still level up while mining (the
                                // 80 XP/boulder grant only fires on completion).
                                SkillRegistry.GainXp(s, "Mining", 0.4f);
                                if (s.GatherProgress < workTarget)
                                {
                                    // Not yet complete — keep the task alive
                                    // for next tick. Don't fall through to
                                    // the yield block.
                                    break;
                                }
                                // Progress reached target — reset and fall
                                // through to the existing yield logic below.
                                s.GatherProgress    = 0;
                                s.GatherTargetTileX = -1;
                                s.GatherTargetTileY = -1;
                            }
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
                            // v0.5.16 — Skeleton terrain drops Bone material
                            // instead of Stone/Wood. Uses the existing
                            // mapping pipeline via ItemFactory.MaterialFromTerrain
                            // (no special branch needed once that helper
                            // recognises Skeleton → Bone). Yield 3 = small
                            // pile per skeleton fragment (rib bone, partial
                            // skull). Sam: "imitate the look of a rib bone
                            // or partial animal skull poking out of the
                            // ground." Provides early-game Bone material
                            // before Phase 8 animal butchery lands.
                            if (tile.Terrain == TerrainType.Skeleton)
                            {
                                mapping = (ItemKind.Material, "BoneFragment",
                                    new MaterialKey("Bone","Generic"));
                            }
                            int baseYield = tile.Terrain switch
                            {
                                TerrainType.Boulder    => 4,
                                TerrainType.DeadLog    => 4,
                                TerrainType.LivingWood => 6,
                                TerrainType.Skeleton   => 3,   // v0.5.16
                                _                      => 0,
                            };
                            // v0.5.30 — Mining yield scaled by skill (RimWorld
                            // pattern). Lvl 0 = 60 %, lvl 8 = 80 %, lvl 16 = 100 %,
                            // lvl 20 = 110 %. A level-0 novice still extracts
                            // some material (never zero); a master gets the
                            // full yield + a small bonus. DeadLog/LivingWood
                            // share the Mining skill since the player drives
                            // both via the Excavate designation.
                            int yield = baseYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseYield,
                                SkillCurve.MiningYieldFactor(SkillLevel(s, "Mining")),
                                rng);
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
                            // v0.5.14 (Phase 5C — rimport.md N18) — buried
                            // treasure quest hook. Tile flagged at gen time
                            // by ScatterBuriedTreasure drops a bonus Trinket
                            // alongside the standard StoneBlock. Same
                            // mechanism the future "sleeping creatures"
                            // hook (Phase 8) will use — different on-excavate
                            // effect. Sam: "what will I find under there?"
                            if (tile.Terrain == TerrainType.Boulder
                                && map.HasBuriedTreasure(t.TargetTileX, t.TargetTileY))
                            {
                                var trinket = ItemFactory.Create(
                                    ItemKind.Trinket, "AncientRelic",
                                    new MaterialKey("Magic","CrystalShard"),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Mining"),
                                    quantity: 1);
                                trinket.TilePos = dropPos;
                                map.DropItem(trinket);
                                map.RemoveBuriedTreasure(t.TargetTileX, t.TargetTileY);
                                ThoughtRegistry.Add(s, "FoundTreasure");
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
                        // v0.7.3 (E8) — re-validate the designation at apply time:
                        // another shroomp may have cleared it, or the player may
                        // have cancelled the Chop order, between path-start and
                        // arrival this tick. Skip the work if it's gone (the task
                        // still clears below so the shroomp re-evaluates).
                        if (slot.IsPresent && !slot.IsDepleted
                            && map.HasChopWoodDesignation(t.TargetTileX, t.TargetTileY))
                        {
                            // v0.4.15 — single-shot felling (RimWorld
                            // semantics). The previous version called
                            // `HarvestVegetation` once (decrement yield
                            // by 1) and then `ClearDesignationsAt`, so a
                            // LargeMushroom (BaseYield = 3) shed its chop
                            // designation after producing 1/3 of its
                            // wood. Shroomps would then walk away leaving
                            // a half-chopped tree standing, the player
                            // would re-designate, and in dense chop
                            // clusters the colony jittered between
                            // adjacent partial trees. Now: one arrival
                            // fells the whole tree, drops total wood,
                            // tile flips passable in the same call.
                            var vegType = slot.Type;
                            int basePerYield = (int)MathF.Round(WoodYield(vegType));
                            byte taken = map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            // v0.5.30 — Plant yield scaled by Foraging skill.
                            // Lvl 0 = 50 %, lvl 8 = 100 %, lvl 20 = 130 %.
                            // Per-stalk botch chance at low skill can drop
                            // total to 50 % of baseline (HarvestBotch roll).
                            int forageSkill = SkillLevel(s, "Botany");
                            int baseTotal = basePerYield * taken;
                            // v0.5.84t — apply tool bonus (Sickle/Knife with ChopWood).
                            float chopToolBonus = GetToolBonusFor(s, TaskType.ChopWood);
                            int totalYield = baseTotal == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                baseTotal,
                                SkillCurve.PlantYieldFactor(forageSkill) * chopToolBonus,
                                rng);
                            if (totalYield > 0 && SkillCurve.HarvestBotch(forageSkill, rng))
                                totalYield = Mathf.Max(1, totalYield / 2);
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
                            SkillRegistry.GainXp(s, "Botany", 60f);   // v0.5.84r: ChopWood now awards Botany (chopping LargeMushroom is plant work)
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.38 — Cut Plants: clear any vegetation tile and drop
                // the relevant resource for that plant.
                // v0.5.69 — yield split by vegetation kind (Sam):
                //   • Undergrowth / MossPatch → Cuttings (compost biomass;
                //     reserved for decoration plants — repurposed later)
                //   • Wood-yielding shrooms (LargeMushroom, LargeSandshroom,
                //     PalmShroom) → Fungal Wood (matches ChopWood yield path
                //     so cutting a large shroom is functionally equivalent
                //     to chopping it)
                //   • Food-yielding plants (CapberryBush, SmallMushroom,
                //     HerbCluster, MagicFlower, etc.) → their food drop
                //     (matches GatherFood yield path)
                // Pre-v0.5.69 every Cut dropped Cuttings regardless of plant
                // type — a large shroom cut yielded biomass instead of wood,
                // which Sam called out as wrong.
                case TaskType.CutVegetation:
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        var slot = map.GetVegetation(t.TargetTileX, t.TargetTileY);
                        // v0.7.3 (E8) — re-validate the Cut designation at apply
                        // time (another shroomp cleared it / player cancelled).
                        if (slot.IsPresent && !slot.IsDepleted
                            && map.HasCutDesignation(t.TargetTileX, t.TargetTileY))
                        {
                            var vegType = slot.Type;
                            var dropPos = new Vector2(
                                t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
                            bool isDecoration =
                                vegType == VegetationType.Underbrush
                                || vegType == VegetationType.MossPatch;
                            bool isWoodYielding = LocalMap.IsWoodYielding(vegType);

                            // Deplete the slot. Decoration veg (BaseYield = 0)
                            // can't be HarvestVegetation'd, so ClearVegetation
                            // removes it outright. Harvestable veg goes
                            // through FullyDeplete so it leaves a stump and
                            // regrows on its normal schedule.
                            byte taken;
                            if (isDecoration)
                            {
                                map.ClearVegetation(t.TargetTileX, t.TargetTileY);
                                taken = 1;
                            }
                            else
                            {
                                taken = map.FullyDepleteVegetation(t.TargetTileX, t.TargetTileY);
                            }

                            if (isDecoration)
                            {
                                // Decoration → biomass cuttings. v0.5.70 splits:
                                //   Underbrush → Cuttings (Plant/Cuttings)
                                //   MossPatch  → Mosslet  (Plant/Mosslet),
                                //               reserved for a future system
                                //               (Sam: "we'll use later").
                                string sub = vegType == VegetationType.MossPatch
                                    ? "Mosslet"
                                    : "Cuttings";
                                int qty = vegType == VegetationType.MossPatch ? 2 : 1;
                                var item = ItemFactory.Create(
                                    ItemKind.Material, sub, new MaterialKey("Plant", sub),
                                    rng, globalTick,
                                    skillLevel: SkillLevel(s, "Botany"),
                                    quantity: qty);
                                item.TilePos = dropPos;
                                map.DropItem(item);
                            }
                            else if (isWoodYielding)
                            {
                                // Wood-yielder → Fungal Wood (matches
                                // ChopWood yield curve so Cut/Chop are
                                // interchangeable on large shrooms).
                                int basePerYield = (int)MathF.Round(WoodYield(vegType));
                                int forageSkill = SkillLevel(s, "Botany");
                                int baseTotal = basePerYield * (taken == 0 ? 1 : taken);
                                // v0.5.84t — apply tool bonus (Sickle/Knife with CutVegetation).
                                float cutToolBonusWood = GetToolBonusFor(s, TaskType.CutVegetation);
                                int totalYield = baseTotal == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                    baseTotal,
                                    SkillCurve.PlantYieldFactor(forageSkill) * cutToolBonusWood,
                                    rng);
                                if (totalYield > 0 && SkillCurve.HarvestBotch(forageSkill, rng))
                                    totalYield = Mathf.Max(1, totalYield / 2);
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
                            }
                            else
                            {
                                // Food-yielder → drop the relevant food
                                // (matches GatherFood yield curve). Magic
                                // vegetation also drops Raw Essence as in
                                // GatherFood.
                                var mapping = ItemFactory.FoodFromVegetation(vegType);
                                int baseFoodYield = (int)MathF.Round(FoodYield(vegType));
                                int forageSkillFood = SkillLevel(s, "Botany");
                                // v0.5.84t — apply tool bonus (Sickle/Knife with CutVegetation).
                                float cutToolBonusFood = GetToolBonusFor(s, TaskType.CutVegetation);
                                int yield = baseFoodYield == 0 ? 0 : SkillCurve.ApplyYieldMul(
                                    baseFoodYield,
                                    SkillCurve.PlantYieldFactor(forageSkillFood) * cutToolBonusFood,
                                    rng);
                                if (yield > 0 && SkillCurve.HarvestBotch(forageSkillFood, rng))
                                    yield = Mathf.Max(1, yield / 2);
                                if (mapping.HasValue && yield > 0)
                                {
                                    var item = ItemFactory.Create(
                                        ItemKind.Food, mapping.Value.SubType,
                                        mapping.Value.Material, rng, globalTick,
                                        skillLevel: SkillLevel(s, "Botany"),
                                        quantity: yield);
                                    item.TilePos = dropPos;
                                    map.DropItem(item);
                                }
                                if (ItemFactory.VegetationYieldsMagicEssence(vegType))
                                {
                                    var essence = ItemFactory.Create(
                                        ItemKind.Magic, "RawEssence",
                                        new MaterialKey("Magic","RawEssence"),
                                        rng, globalTick,
                                        skillLevel: SkillLevel(s, "Botany"),
                                        quantity: 1);
                                    essence.TilePos = dropPos;
                                    map.DropItem(essence);
                                }
                            }
                            EmitWorkThought(s, TaskType.CutVegetation, null);
                            s.TaskDidWork = true;   // v0.4.19
                            // v0.4.62 (G3) — Botany XP per cut. Lower than
                            // chop because cut covers a wider mix including
                            // small decoration plants; less skill-relevant.
                            SkillRegistry.GainXp(s, "Botany", 30f);
                        }
                        map.ClearDesignationsAt(t.TargetTileX, t.TargetTileY);
                    }
                    s.CurrentTask = null;
                    break;
                // v0.3.43 — idle effects. The shroomp has arrived at their
                // chosen idle destination; ApplyTaskEffect handles the
                // "what does this activity actually do" side-effects, and
                // the main tick loop sets IdleLingerTicks so the shroomp
                // holds at the destination for ArrivalLinger ticks instead
                // of immediately re-picking.
                case TaskType.Loiter:
                    // Loiter is the "doing nothing in particular" activity.
                    // Tiny idle thought, no need changes. Emit only on
                    // ~10 % of arrivals so the thoughts pane doesn't get
                    // spammed with the same headline.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    if (r != null /* keep r warning-free */ && (s.Id.GetHashCode() & 7) == 0)
                        ThoughtRegistry.Add(s, "Wandered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.6f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Loiter));
                    BumpJoyTolerance(s, TaskType.Loiter);
                    break;

                case TaskType.Observe:
                    // Standing and watching. Boosts Social slightly (people-
                    // watching is social!) and emits a Daydreamed thought.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt * 0.3f);
                    ThoughtRegistry.Add(s, "Daydreamed");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.8f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Observe));
                    BumpJoyTolerance(s, TaskType.Observe);
                    break;

                case TaskType.Converse:
                {
                    // v0.5.59 — RimWorld-parity recreation exit. RimWorld's
                    // JoyGiver / SocialRelax aborts the job the moment the
                    // pawn's Joy / Social need crosses 90 % — recreation
                    // stops being chosen, and any in-progress recreation
                    // ends. Sam: "pawns chat for far too long at 100 social
                    // while others deliver resources infinitely to
                    // blueprints that never get built." Pre-v0.5.59 the
                    // Converse case unconditionally clamped Social to 100
                    // but never cleared CurrentTask — so once a shroomp
                    // started chatting, they rode out the full
                    // LingerConverse window (300 ticks ≈ 5 sec at 1×)
                    // regardless of need state, then re-picked Converse
                    // again next idle roll because the idle weight didn't
                    // account for Social either (separate fix in
                    // SelectIdleActivity below). Net effect: paired shroomps
                    // chained 5-second chats indefinitely. Now: if Social
                    // is already at/near full, exit immediately — and free
                    // the locked partner too so they don't sit chatting
                    // at thin air for another 5 seconds.
                    if (s.Social >= 95f)
                    {
                        if (t.TargetId != null)
                        {
                            foreach (var o in shroomps)
                            {
                                if (!o.IsAlive || o.Name != t.TargetId) continue;
                                if (o.CurrentTask is { } ot
                                    && ot.Type == TaskType.Converse
                                    && ot.TargetId == s.Name)
                                {
                                    o.CurrentTask = null;
                                    o.IdleArrived = false;
                                    o.IdleLingerTicks = 0;
                                }
                                break;
                            }
                        }
                        s.CurrentTask = null;
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                        break;
                    }
                    // Boost both this shroomp and the partner's Social, build
                    // friendship over repeated chats, and emit a thought
                    // that depends on whether they like each other.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Social = MathF.Min(100f, s.Social + SocializeRate * dt);
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Converse));
                    BumpJoyTolerance(s, TaskType.Converse);
                    Shroomp? partner = null;
                    if (t.TargetId != null)
                    {
                        foreach (var o in shroomps)
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
                        // "a shroomp should actually have a two way
                        // conversation with another shroomp that engages
                        // both and lasts until they're done speaking."
                        TryLockConversePartner(s, partner, t.ArrivalLinger);

                        partner.Social = MathF.Min(100f, partner.Social + SocializeRate * dt);
                        // Build affinity. Three positive chats = friend.
                        bool weDislike   = s.Preferences?.DislikesShroomp(partner.Name) ?? false;
                        bool theyDislike = partner.Preferences?.DislikesShroomp(s.Name) ?? false;
                        if (weDislike || theyDislike)
                        {
                            ThoughtRegistry.Add(s,       "ChatWithEnemy", partner.Name);
                            ThoughtRegistry.Add(partner, "ChatWithEnemy", s.Name);
                            // v0.7.3 (N9) — a sour chat erodes opinion further.
                            s.Preferences?.AdjustOpinion(partner.Name, -1.5f);
                            partner.Preferences?.AdjustOpinion(s.Name, -1.5f);
                        }
                        else
                        {
                            bool weLike   = s.Preferences?.LikesShroomp(partner.Name) ?? false;
                            ThoughtRegistry.Add(s,       weLike ? "ChatWithFriend" : "NiceChat", partner.Name);
                            ThoughtRegistry.Add(partner, weLike ? "ChatWithFriend" : "NiceChat", s.Name);
                            // v0.7.3 (N9) — a good chat builds opinion; crossing the
                            // friend threshold promotes them to friends (the binary
                            // Liked list stays in sync inside AdjustOpinion).
                            s.Preferences?.AdjustOpinion(partner.Name, +2f);
                            partner.Preferences?.AdjustOpinion(s.Name, +2f);
                        }
                    }
                    break;
                }

                case TaskType.Meditate:
                    // Mage-style idle: standing meditation lifts MagicResonance
                    // at a fraction of the dedicated Attune task rate so the
                    // need can keep up without making Attune redundant.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    float meditateToolBonus = GetToolBonusFor(s, TaskType.Meditate);   // v0.8.6 — the Sage Staff's advertised Meditate bonus now actually applies
                    s.MagicResonance = MathF.Min(100f, s.MagicResonance + AttuneRate * dt * 0.5f * meditateToolBonus);
                    ThoughtRegistry.Add(s, "Pondered");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.7f * JoyFurnitureMul(s, map)
                        * JoyToleranceMul(s, TaskType.Meditate));
                    BumpJoyTolerance(s, TaskType.Meditate);
                    break;

                case TaskType.Train:
                {
                    // v0.7.2 (Phase 7) — combat drill. A Sparring Yard trains
                    // Melee; a Training Dummy trains Ranged. Skill is resolved
                    // from the structure standing at the target tile (the same
                    // tile NewTrainTask walked us to) so one task type serves
                    // both buildings. No real damage; slow, steady XP plus a
                    // small purposeful-activity Joy nudge.
                    var trainType = map?.GetStructure(t.TargetTileX, t.TargetTileY).Type;
                    // v0.7.4 (#24) — guard the single-tick race where the building
                    // is demolished between IsTaskStillValid and here: only train
                    // at a real yard/dummy, else drop the task (no free Melee XP
                    // granted at an empty tile).
                    if (trainType != StructureType.SparringYard
                        && trainType != StructureType.TrainingDummy)
                    {
                        s.CurrentTask = null;
                        break;
                    }
                    bool ranged = trainType == StructureType.TrainingDummy;
                    SkillRegistry.GainXp(s, ranged ? "Ranged" : "Melee", TrainXpPerSecond * dt);
                    ThoughtRegistry.Add(s, "Trained");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.4f);
                    break;
                }

                case TaskType.VisitFavorite:
                    // Phase 4 will route this to a remembered location; for
                    // now the activity is just a longer-distance wander
                    // with a positive memory thought on arrival.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    ThoughtRegistry.Add(s, "VisitedSpot");
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 1.2f
                        * JoyToleranceMul(s, TaskType.VisitFavorite));
                    BumpJoyTolerance(s, TaskType.VisitFavorite);
                    break;

                // v0.4.0 — Phase-5-deferred task stubs. Both are reachable
                // through the Jobs tab today (the player can set their
                // Haul / Cook priorities) but neither has a workplace yet:
                //   Haul → needs Phase 5 stockpile zones to know where to
                //          carry the item. Without a destination tile the
                //          stub clears the task and the shroomp re-evaluates.
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
                    // v0.6.2 audit Fix 2 — CookSystem.Apply now manages its
                    // own CurrentTask lifecycle (multi-tick progress on the
                    // shroomp's TaskProgressTicks accumulator; clears on
                    // completion). Don't clobber it here or the cook never
                    // finishes — the task would re-fire selection every
                    // tick instead of accumulating.
                    CookSystem.Apply(s, t, map, r);
                    break;
                // v0.6.2 — Demolish-as-task. Like Build, DemolishSystem.Apply
                // accumulates per-tick DemolitionProgress on the StructureSlot
                // itself and manages CurrentTask lifecycle (clears on
                // completion / when the marker is gone). Don't clobber here.
                case TaskType.Demolish:
                    DemolishSystem.Apply(s, t, map, r);
                    break;
                // v0.5.84s — Phase 5.5 bills dispatch.
                case TaskType.DoBill:
                    BillSystem.Apply(s, t, map, r);
                    // BillSystem.Apply manages CurrentTask itself: keeps
                    // the task across multiple Apply ticks until ProgressTicks
                    // hits the recipe's WorkTicks, then nulls.
                    break;

                case TaskType.BuildHaul:
                    // v0.5.60 — RimWorld-parity "WorkGiver_ConstructDeliver
                    // Resources" equivalent. Gated by Haul priority (any
                    // role can deliver materials). Stages:
                    //   A. AT SOURCE, BuildSiteTileX/Y set → pickup matching
                    //      material into Inventory; re-route to blueprint
                    //   B. AT BLUEPRINT, carrying matching material → deposit
                    //      ONE UNIT per tick. v0.5.60 S2 drops a VISIBLE
                    //      Item on the blueprint tile (IsForbidden=true so
                    //      HaulSystem doesn't try to haul it away). Player
                    //      sees materials pile up at the build site.
                    //      MaterialsDelivered counter increments alongside.
                    //   C. AT BLUEPRINT, no matching carry, blueprint
                    //      under-supplied → abandon (SelectTask re-routes)
                    //   D. AT BLUEPRINT, supplied → done. Abandon, let a
                    //      Crafter pick up the framing via TaskType.Build.
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        bool routingFromSource = s.BuildSiteTileX >= 0 && s.BuildSiteTileY >= 0;
                        int bpTx = routingFromSource ? s.BuildSiteTileX : t.TargetTileX;
                        int bpTy = routingFromSource ? s.BuildSiteTileY : t.TargetTileY;
                        var bpSlot = map.GetStructure(bpTx, bpTy);
                        if (!bpSlot.IsBlueprint)
                        {
                            // v0.5.84t — go through ReleaseTaskClaim so the
                            // LayerBuildHaul reservation is freed + any picked-
                            // up surplus is dropped on the current tile.
                            ReleaseTaskClaim(s, map);
                            s.CurrentTask = null;
                            break;
                        }
                        byte cost = StructureSlot.BuildMaterialCost(bpSlot.Type);
                        int needed = cost - bpSlot.MaterialsDelivered;
                        if (needed <= 0)
                        {
                            // v0.5.84t — supplied by another hauler in between
                            // our pickup and arrival (pre-v0.5.84t the single-
                            // hauler reservation prevents this entirely, but
                            // keep the guard for race-condition safety).
                            // ReleaseTaskClaim drops the carried surplus so
                            // HaulSystem cleans it up rather than the shroomp
                            // riding around with it.
                            ReleaseTaskClaim(s, map);
                            s.CurrentTask = null;
                            break;
                        }
                        string family  = StructureMatMeta.ConsumeFamily(bpSlot.Material);
                        string? subType = StructureMatMeta.ConsumeSubType(bpSlot.Material);
                        // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
                        string? itemSubType = StructureMatMeta.ConsumeItemSubType(bpSlot.Material);

                        // Stage A — pickup at source
                        if (routingFromSource)
                        {
                            int curTx = (int)(s.SimPos.X / LocalMap.TileSize);
                            int curTy = (int)(s.SimPos.Y / LocalMap.TileSize);
                            int pickupCap = System.Math.Min(needed,
                                System.Math.Max(0, s.CarryingCapacity - s.CurrentCarriedCount));
                            if (pickupCap <= 0)
                            {
                                // Carry cap exceeded — walk to blueprint anyway
                                // to dump what (matching) material we have.
                                RetargetBuildHaulToBlueprint(s, t, map, bpTx, bpTy);
                                break;
                            }
                            int taken = map.PickupDroppedAt(curTx, curTy,
                                Items.ItemKind.Material, family, subType, pickupCap, itemSubType);
                            if (taken > 0)
                            {
                                var matKey = new Items.MaterialKey(family, subType ?? "");
                                Items.Item? topUp = null;
                                foreach (var inv in s.Inventory)
                                {
                                    if (inv.Kind != Items.ItemKind.Material) continue;
                                    if (inv.Material.Family != family) continue;
                                    if (subType != null && inv.Material.SubType != subType) continue;
                                    if (itemSubType != null && inv.SubType != itemSubType) continue;
                                    topUp = inv;
                                    break;
                                }
                                if (topUp != null)
                                {
                                    topUp.Quantity += taken;
                                }
                                else
                                {
                                    s.Inventory.Add(new Items.Item
                                    {
                                        Kind     = Items.ItemKind.Material,
                                        SubType  = itemSubType ?? subType ?? "Generic",
                                        Material = matKey,
                                        Quality  = Items.Quality.Normal,
                                        State    = Items.ItemState.Fresh,
                                        Quantity = taken,
                                        OwnerShroompId = s.Id,
                                    });
                                }
                                SkillRegistry.GainXp(s, "Construction", 4f);
                            }
                            RetargetBuildHaulToBlueprint(s, t, map, bpTx, bpTy);
                            break;
                        }

                        // Stage B — deposit at blueprint
                        if (ConsumeOneFromShroompInventory(s, bpSlot.Material))
                        {
                            bpSlot.MaterialsDelivered++;
                            map.SetStructure(bpTx, bpTy, bpSlot);
                            // v0.5.60 S2 — drop visible material item ON the
                            // blueprint tile. Player sees the deposit pile up.
                            // IsForbidden=true so HaulSystem doesn't try to
                            // haul these back to a stockpile (matches RimWorld
                            // Frame.resourceContainer behaviour — materials
                            // belong to the frame, not the haul pool).
                            var depositItem = new Items.Item
                            {
                                Kind     = Items.ItemKind.Material,
                                SubType  = subType ?? "Generic",
                                Material = new Items.MaterialKey(family, subType ?? ""),
                                Quality  = Items.Quality.Normal,
                                State    = Items.ItemState.Fresh,
                                Quantity = 1,
                                TilePos  = new Vector2(
                                    bpTx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                                    bpTy * LocalMap.TileSize + LocalMap.TileSize * 0.5f),
                                IsForbidden = true,
                            };
                            map.DropItem(depositItem);
                            s.TaskDidWork = true;
                            // If still under-supplied AND still carrying → keep
                            // depositing next tick (don't clear task).
                            // If carry depleted AND still under-supplied → fall
                            // through to clear task; SelectTask will re-route
                            // to a fresh source.
                            if (bpSlot.MaterialsDelivered >= cost
                                || !ShroompCarriesMatchingBuildMaterial(s, bpSlot.Material))
                            {
                                // v0.5.84t — release LayerBuildHaul + drop
                                // any leftover carried surplus to current tile.
                                ReleaseTaskClaim(s, map);
                                s.CurrentTask = null;
                            }
                            break;
                        }
                        // No matching carry — abandon, SelectTask routes again.
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = null;
                    }
                    else
                    {
                        ReleaseTaskClaim(s, map);
                        s.CurrentTask = null;
                    }
                    break;

                case TaskType.Build:
                    // v0.5.60 — Build is now FRAMING ONLY. Hauling moved to
                    // TaskType.BuildHaul (gated by Haul priority, any role).
                    // Build is gated by Construct priority (Crafter preferred).
                    // Stages:
                    //   D. Tick BuildProgress per tick by SkillCurve factor
                    //   E. On BuildProgress >= target: complete, consume all
                    //      deposited material items on the tile, flip
                    //      blueprint → built, roll Quality, release claim
                    if (map != null && t.TargetTileX >= 0 && t.TargetTileY >= 0)
                    {
                        int bpTx = t.TargetTileX;
                        int bpTy = t.TargetTileY;
                        var bpSlot = map.GetStructure(bpTx, bpTy);
                        if (!bpSlot.IsBlueprint && !bpSlot.IsBuilt)
                        {
                            s.BuildSiteTileX = -1;
                            s.BuildSiteTileY = -1;
                            s.CurrentTask = null;
                            break;
                        }

                        // Defensive: if blueprint isn't fully supplied (race —
                        // a hauler abandoned with partial delivery), abandon
                        // framing so a BuildHaul task gets re-issued.
                        if (bpSlot.IsBlueprint)
                        {
                            byte cost = StructureSlot.BuildMaterialCost(bpSlot.Type);
                            if (bpSlot.MaterialsDelivered < cost)
                            {
                                s.CurrentTask = null;
                                break;
                            }
                            // Stage D framing.
                            // v0.5.30 — RimWorld-parity Construction curve.
                            // delta = 10 × ConstructionSpeedFactor(skill)
                            // — lvl 0 → 3/tick (~3.3s to 600), lvl 8 →
                            // 10/tick (~1s), lvl 20 → 20/tick (~0.5s).
                            // The ~6.7× spread matches RimWorld's published
                            // Construction Speed table (0.30 → 2.05).
                            int builderSkill = SkillLevel(s, "Construction");
                            // v0.5.84t — apply tool bonus (Hammer with Build).
                            float buildToolBonus = GetToolBonusFor(s, TaskType.Build);
                            int delta = Mathf.Max(1, (int)(10f * SkillCurve.ConstructionSpeedFactor(builderSkill) * buildToolBonus));
                            int newProg = bpSlot.BuildProgress + delta;
                            if (newProg < StructureSlot.BuildProgressTarget)
                            {
                                bpSlot.BuildProgress = (ushort)newProg;
                                map.SetStructure(t.TargetTileX, t.TargetTileY, bpSlot);
                                // Don't clear CurrentTask — stay on the build
                                // for more ticks. Shroomp already at the tile;
                                // ApplyTaskEffect re-fires next tick.
                            }
                            else
                            {
                                // Stage 3: complete.
                                var built = bpSlot;
                                built.Type = bpSlot.Type switch
                                {
                                    StructureType.WallPlanned       => StructureType.Wall,
                                    StructureType.DoorPlanned       => StructureType.Door,
                                    StructureType.ShelfPlanned      => StructureType.Shelf,       // v0.5.21
                                    StructureType.WorkbenchPlanned  => StructureType.Workbench,   // v0.5.22
                                    StructureType.BonfirePlanned     => StructureType.Bonfire,      // v0.5.24
                                    StructureType.BedPlanned        => StructureType.Bed,         // v0.5.35
                                    StructureType.MeditationShrinePlanned => StructureType.MeditationShrine,   // v0.5.36
                                    StructureType.ShroomBoardPlanned      => StructureType.ShroomBoard,        // v0.5.36
                                    StructureType.GossipBenchPlanned      => StructureType.GossipBench,        // v0.5.36
                                    StructureType.TablePlanned      => StructureType.Table,       // v0.5.37
                                    StructureType.TorchPlanned      => StructureType.Torch,       // v0.5.84t — was missing, completed as Floor
                                    StructureType.CookingTablePlanned => StructureType.CookingTable, // v0.6.2 (Phase 5.6)
                                    StructureType.SparringYardPlanned  => StructureType.SparringYard,  // v0.7.2
                                    StructureType.TrainingDummyPlanned => StructureType.TrainingDummy, // v0.7.2
                                    StructureType.ButcherSlabPlanned   => StructureType.ButcherSlab,   // v0.8.1
                                    _                               => StructureType.Floor,       // FloorPlanned + safety default
                                };
                                built.BuildProgress = StructureSlot.BuildProgressTarget;
                                // v0.5.30 — roll Quality from Construction
                                // skill at completion. SkillCurve.Roll
                                // StructureQuality returns Crude/Normal/
                                // Fine/Superior/Masterwork (Legendary
                                // reserved for inspired-creativity events).
                                // Quality drives BeautyScore + future
                                // bed RestEffectiveness + tooltip display.
                                built.Quality = SkillCurve.RollStructureQuality(builderSkill, rng);
                                map.SetStructure(t.TargetTileX, t.TargetTileY, built);
                                // v0.5.60 S2 — consume the visible deposited
                                // material items off the blueprint tile.
                                // Pre-v0.5.60 materials disappeared into the
                                // MaterialsDelivered counter on first delivery;
                                // now they sit visibly on the tile until
                                // completion, then get consumed (fold into
                                // the structure). RimWorld pattern: frame.
                                // resourceContainer is emptied on
                                // CompleteConstruction.
                                string famConsume = StructureMatMeta.ConsumeFamily(built.Material);
                                string? subConsume = StructureMatMeta.ConsumeSubType(built.Material);
                                // v0.5.84t — Item.SubType discriminator (StoneBlock vs Pebblestone, etc.).
                                string? itemSubConsume = StructureMatMeta.ConsumeItemSubType(built.Material);
                                byte consumeCount = StructureSlot.BuildMaterialCost(built.Type);
                                map.PickupDroppedAt(t.TargetTileX, t.TargetTileY,
                                    Items.ItemKind.Material, famConsume, subConsume, consumeCount, itemSubConsume);
                                // v0.5.84t — unforbid any leftover dropped
                                // material on the just-built tile so HaulSystem
                                // can move it to a stockpile. Without this,
                                // over-deposit from pre-v0.5.84t multi-hauler
                                // races (or from legacy saves) stays forbidden
                                // forever on the built tile.
                                map.UnforbidDroppedAt(t.TargetTileX, t.TargetTileY);
                                s.TaskDidWork = true;
                                SkillRegistry.GainXp(s, "Construction", 80f);
                                // v0.5.59 — release the blueprint claim on completion.
                                map.ReleaseClaim(t.TargetTileX, t.TargetTileY, s.Id);
                                s.BuildSiteTileX = -1;
                                s.BuildSiteTileY = -1;
                                s.CurrentTask = null;
                            }
                        }
                        else
                        {
                            // Blueprint vanished (demolished mid-build) — abandon.
                            s.CurrentTask = null;
                        }
                    }
                    else
                    {
                        s.CurrentTask = null;
                    }
                    break;

                case TaskType.Wander:
                    // v0.4.63 (G4) — basic idle wander gives modest Joy.
                    // v0.5.60 — joy gain scaled by JoyTolerance for boredom.
                    s.Joy = MathF.Min(100f, s.Joy + JoyRate * dt * 0.5f
                        * JoyToleranceMul(s, TaskType.Wander));
                    BumpJoyTolerance(s, TaskType.Wander);
                    // v0.5.5 — multi-hop chain. If WanderHopsRemaining > 0,
                    // pick a fresh destination, swap it into CurrentTask,
                    // reset arrival state so the shroomp walks again, and
                    // bump WorkSearchCooldownTicks high enough to cover
                    // the next leg's walk + linger (so the chain isn't
                    // interrupted by a workAvailable re-eval mid-leg).
                    // Pathfind the new destination immediately so the
                    // section-2a A* gate doesn't need to re-fire.
                    //
                    // Sam: "a shroomp should actually take a short walk and
                    // finish it when 'taking a walk'." A 2-4 leg chain at
                    // 8-28 tiles per leg gives ~10-25 sec of visible
                    // wandering, matching the RimWorld feel of pawns
                    // "going for a walk" between work shifts.
                    // v0.5.79 — break the auto-chain when the shroomp has
                    // a more pressing need or a pending player order.
                    // Pre-v0.5.79 the chain ran unconditionally → night-
                    // sleep-window shroomps with Rest=19 kept wandering
                    // through the night (Sam screenshot: Ethan, Elder,
                    // Rest=19 at hour 23, Wandering); right-click move
                    // orders queued in MoveOrderQueue stayed queued until
                    // the chain naturally exhausted because the section-
                    // 2a re-eval skipped the chain-pop block whenever
                    // s.CurrentTask was non-null (Wander auto-chain kept
                    // it non-null indefinitely).
                    //
                    // Three break conditions:
                    //   1. Life-threatening need (Nutrition<5 / Rest<5)
                    //   2. Pending player move order in MoveOrderQueue
                    //   3. Night-sleep gate fires (in-window + Rest<80)
                    //      — same condition SelectTask Tier-1 line 2118
                    //      uses to enqueue a high-priority Sleep, so the
                    //      auto-chain ducking out here lets that branch
                    //      take over on the next SelectTask iteration.
                    bool breakChain = IsLifeThreatening(s)
                        || s.MoveOrderQueue.Count > 0;
                    if (!breakChain && s.WanderHopsRemaining > 0)
                    {
                        // Night-sleep check (matches SelectTask line 2118)
                        bool nightOwlW = HasPersonality(s, "Night Owl");
                        int hr = _currentHourOfDay;
                        bool sleepWin = nightOwlW
                            ? (hr >= 10 && hr < 18)
                            : (hr >= 22 || hr <  6);
                        if (sleepWin && s.Rest < 80f) breakChain = true;
                    }
                    if (breakChain)
                    {
                        // Drop the current Wander task + remaining chain.
                        // Section-2a's needNewTask gate fires on the next
                        // tick (CurrentTask == None) and SelectTask picks
                        // the appropriate high-priority response.
                        s.CurrentTask = null;
                        s.PathWaypoints.Clear();
                        s.WanderHopsRemaining = 0;
                        s.IdleArrived = false;
                        s.IdleLingerTicks = 0;
                        s.WorkSearchCooldownTicks = 0;
                        break;
                    }

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
                                s.PathWaypoints, _shroompPerTile, OccTileIdx(s));
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

        // v0.3.43 — emit a thought matching how a shroomp feels about a work
        // task they just completed. The mapping uses Preferences.LikesActivity
        // for the activity itself (Foraging / Excavating / …) and an
        // optional item-axis lookup for the specific yield (Capberry vs
        // Pineshroom). Cheap — called once per completion, not per tick.
        private static void EmitWorkThought(Shroomp s, TaskType type, string? itemName)
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
        private static string? ItemNameFor(Sporeholm.World.VegetationType v) => v switch
        {
            Sporeholm.World.VegetationType.CapberryBush  => "Capberry",
            Sporeholm.World.VegetationType.SmallMushroom   => "SmallMushroom",
            Sporeholm.World.VegetationType.LargeMushroom   => "SmallMushroom",
            Sporeholm.World.VegetationType.HerbCluster     => "HerbCluster",
            Sporeholm.World.VegetationType.MagicFlower     => "MagicBerry",
            Sporeholm.World.VegetationType.PineShroom      => "SmallMushroom",
            Sporeholm.World.VegetationType.PalmShroom      => "SmallMushroom",
            Sporeholm.World.VegetationType.SmallSandshroom => "SmallMushroom",
            Sporeholm.World.VegetationType.LargeSandshroom => "SmallMushroom",
            _ => null,
        };

    }
}
