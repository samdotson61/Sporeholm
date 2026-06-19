# Sporeholm Codebase Audit Report

**Date:** 2026-06-16
**Scope:** Full codebase audit across 8 passes (Phase 1–8 features)
**Version:** v0.8.x (Phase 8 husbandry/hunting/farming)

---

## Executive Summary

**1 bug found** (low severity, cosmetic). 8 of 8 audit passes clean after correction. The codebase is in excellent shape for the scale and complexity — data registries are consistent, behavior flows are correct, skill curves are well-bounded, save/load is complete, and UI panels are comprehensive.

---

## Pass A: Data Registry Consistency ✅ PASS

All production-path items resolve to registered entries in both ItemRegistry and MaterialRegistry.

| Source | Output | ItemRegistry | MaterialRegistry | Status |
|--------|--------|-------------|-----------------|--------|
| Crop: SpringGreens | Food/SpringGreens | line 112 | Plant/SpringGreens (line 145) | ✅ |
| Crop: Sunberry | Food/Sunberry | line 113 | Plant/Sunberry (line 146) | ✅ |
| Crop: Pumpkin | Food/Pumpkin | line 114 | Plant/Pumpkin (line 147) | ✅ |
| Crop: SmallMushroom | Food/SmallMushroom | line 78 | Plant/SmallMushroom (line 127) | ✅ |
| Crop: Capberry | Food/Capberry | line 77 | Plant/Capberry (line 126) | ✅ |
| Crop: HerbCluster | Food/HerbCluster | line 79 | Plant/HerbCluster (line 129) | ✅ |
| Crop: MagicBerry | Food/MagicBerry | line 80 | Plant/MagicBerry (line 130) | ✅ |
| Butcher: Meat | Food/Meat | line 118 | Meat/Meat (line 189) | ✅ |
| Butcher: Egg | Food/Egg | line 119 | Meat/Egg (line 190) | ✅ |
| Butcher: Milk | Food/Milk | line 120 | Meat/Milk (line 191) | ✅ |
| Butcher: Hide | Material/Hide | line 121 | Hide/Generic (line 180) | ✅ |
| Butcher: Bone | Material/Bone | line 127 | Bone/Generic (line 177) | ✅ |
| Produce: Wool | Material/Wool | line 131 | Cloth/Wool (line 164) | ✅ |
| Craft: MossCloth | Material/MossCloth | line 97 | Cloth/Mossleaf (line 162) | ✅ |
| Craft: GrassLinen | Material/GrassLinen | line 98 | Cloth/Grass (line 163) | ✅ |
| Craft: BerryJuice | Food/BerryJuice | line 96 | Plant/Cooked (n/a — juice uses Plant family roll) | ✅ |
| Craft: RefinedPlank | Material/RefinedPlank | line 104 | Wood family roll | ✅ |
| Craft: Pebblestone | Material/Pebblestone | line 105 | Stone family roll | ✅ |

**Note:** Crafted items (MossCloth → Cloth/Mossleaf, GrassLinen → Cloth/Grass) use explicit material keys in their recipes, not the RollMaterial random roll. This is correct — crafted items should have deterministic materials.

---

## Pass B: Behavior → Designation Flow ✅ PASS

All husbandry/farming behaviors delegate to SimulationManager for designation ID generation. No direct `new Designation(...)` calls in BehaviorSystem.

| Behavior | Method | Delegates? | Details |
|----------|--------|-----------|---------|
| Tame | `DoTame()` → `SimulationManager.AddOrder(..., DesignationId = -1)` | ✅ | Comment: "delegate to SimulationManager for ID gen" |
| Hunt | `DoHunt()` → `SimulationManager.AddOrder(..., DesignationId = -1)` | ✅ | Same pattern |
| HarvestCrop | `DoHarvestCrop()` → finds designation by tile, no new designation created | ✅ | Consumes existing harvest designation |
| PlantSeed | `DoPlantSeed()` → calls `simulation.CreateFarmPlotTile()` → creates harvest designation via SimulationManager | ✅ | FarmPlot creates its own harvest designation |

**Tamed exclusion from hunt targets:** `BehaviorSystem.Hunt()` filters candidates with `if (e.Id == shroomp.Id || e.IsShroomp || e.IsTame) continue;` — verified at line ~2075. ✅

---

## Pass C: Task → Skill Progression Wiring ✅ PASS

Every task type that BehaviorSystem processes calls `SimulationManager.RecordSkillProgress()` with the correct skill mapping:

| Task Type | Skill Type | Location | Status |
|-----------|-----------|----------|--------|
| ChopWood | Logging | DoChopWood() | ✅ |
| GatherFood | Foraging | DoGatherFood() | ✅ |
| GatherMaterial | Mining | DoGatherMaterial() | ✅ |
| Build | Construction | DoBuild() | ✅ |
| HarvestCrop | PlantWork | DoHarvestCrop() | ✅ |
| PlantSeed | PlantWork | DoPlantSeed() | ✅ |
| Dig | Mining | DoDig() | ✅ |
| CutVegetation | PlantWork | DoCutVegetation() | ✅ |
| Tame | Husbandry | DoTame() | ✅ |
| Hunt | Weapons | DoHunt() (kill + butcher) | ✅ |
| Meditate | Scholar | DoMeditate() | ✅ |
| Attune | Scholar | DoAttune() | ✅ |

All orders flow through `BehaviorSystem.ProcessOrders()` → `switch (order.Type)` → `DoXxx()` → `RecordSkillProgress()`. No orders bypass the behavior system.

---

## Pass D: Corpse / Crop Lifecycle ✅ PASS

**Crop lifecycle:**
- FarmPlot.GrowCrop() spawns a Plant entity with State.Alive and a CropInfo (HarvestSubType, HarvestKind, HarvestFamily, TicksToMature, IsMature=false)
- TickCrops() in SimulationCore advances ticks; when mature, state → Dead (edible), yield data preserved
- Orphaned crops (farm plot removed) continue ticking via SimulationCore.OrphanedCrops dict until harvested or TTL expires
- Harvest designations auto-cleaned after harvest

**Corpse lifecycle:**
- EntitySystem.SpawnCorpse() creates terrain corpse with health=1.0f, TTL based on EntityType.MaxHealth
- Tamed entities → AwaitingButchery=true, ButcherTtlTicks=10800 (30 min)
- Wild entities → left as terrain, same TTL system
- Corpse reaper in SimulationCore removes corpses when TTL expires
- Auto-butcher: tamed corpses → auto-queued for butchery if ButcherSlab exists

**Breeding lifecycle:**
- Pasture designation creates BreedingZone
- Compatible unpaired entities in zone → GestationTicks = GestationDuration (10800–21600 ticks)
- Gestating entities tick down; on completion → SpawnOffspring() → new juvenile entity released to map
- Mated entities excluded from future pairing until offspring born

---

## Pass E: Save/Load Consistency ✅ PASS

**EntitySnapshot fields (Phase 8 complete):**
- Core: Id, Kind, SimPos, SimTarget, State, Health, MaxHealth
- Combat: Speed, AttackPower, RandomSeed, AttackCooldownTicks, TargetShroompId
- Taming: IsTamed, TamedByName, MarkedForTame, TamingProgress
- Produce: ProduceCooldownTicks
- Breeding: GestationTicks
- Hunt/Butchery: MarkedForHunt, AwaitingButchery, ButcheryTtlTicks
- Needs: Nutrition, Rest, MoodLabel

**SaveManager.SaveGame() persists:**
- Map (LocalMap → JSON)
- Shroomps (ShroompSnapshot[] → JSON)
- Items (Item[] → JSON)
- Entities (EntitySnapshot[] → JSON)
- Zones (Zone[] → JSON)
- Designations (Designation[] → JSON)
- Simulation state (tick, orders, orphaned crops)

**SaveManager.LoadGame() reconstructs in order:**
1. Map tiles
2. Shroomps (from snapshots)
3. Items (from saved inventory)
4. Entities (from snapshots — includes all Phase 8 fields)
5. Zones (from saved zone data)
6. Designations (re-added to SimulationManager)

All EntitySnapshot fields map 1:1 to Entity properties. No data loss on save/load cycle.

---

## Pass F: UI Panel Completeness ⚠️ FIXED (minor)

### CORRECTION — "Missing FarmPlot button" is NOT a bug

The DesignationTool enum has NO `BuildFarmPlot` entry. Farm plots are created via zone painting (`DesignationTool.Farm`) through the ZonesPanel, not individual build designations. This is by design — farm zones paint multiple tiles at once with crop selection, which is the appropriate interaction pattern for agriculture. No fix needed.

### Minor fix — BuildButcherSlab missing from isBuildTool check ✅ FIXED

BuildButcherSlab had a button and was in the mapping switch, but was missing from:
1. `isBuildTool` check (controls material chip visibility)
2. Button array in `SyncButtons()` (controls which buttons clear their pressed state)

**Impact:** Low/cosmetic. The Butcher Slab button works for designating builds, but the BuildPanel material chips wouldn't show when selecting it, and the button state sync was incomplete.

**Fix applied:** Added `BuildButcherSlab` to both locations in BuildPanel.cs.

---

## Pass G: Skill Curve Math ✅ PASS

All curves verified for reasonable ranges and bounded outputs:

| Curve | Lvl 0 | Lvl 8 | Lvl 20 | Notes |
|-------|-------|-------|--------|-------|
| ConstructionSpeed | 0.30× | 1.00× | 2.05× | RimWorld-parity |
| ConstructSuccess | 75% | 95% | 95% (cap) | Botch = 1-success |
| MiningSpeed | 0.04× | 1.00× | 2.44× | Reserved for future use |
| MiningYield | 60% | 80% | 110% | Capped at 1.25× |
| PlantSpeed | 8% | 100% | 238% | Reserved for future use |
| PlantYield | 50% | 100% | 130% | Capped at 1.30× |
| HarvestBotch | 25% | 0% | 0% | Ruin chance decreases with skill |
| ButcherYield | 60% | 76% | 100% | v0.8.1 — Cooking skill drives butchery |
| TameProgress | 12/visit | 28/visit | 52/visit | v0.8.2 — ~2-9 visits to tame |
| CookingSpeed | 40% | 100% | 190% | RimWorld-parity |
| ToolQuality | Crude 0.90× → Legendary 1.50× | N/A | N/A | Per-quality tier |
| MeleeSkillFactor | 0.40 | 0.84 | 1.50 | Feeds hit-chance formula |
| DodgeChance | 2% | 12% | 26% | Capped at 50% |
| HitChance | Clamped [5%, 95%] | N/A | N/A | No perfect hits/misses |

Quality roll: bell-curve centered on skill level. Lvl 0 → Crude/Normal, lvl 8 → Normal/Fine, lvl 20 → Superior/Masterwork tail. No Legendary without inspired creativity (not implemented). ✅

---

## Pass H: Combat Profile Coverage ✅ PASS

**Weapon classification (TypeForSubType):**
- Piercing: Spear, Knife (classified as Edged — acceptable for a knife), Sickle (Edged — correct)
- Edged: Sword, Axe, Knife, Sickle
- Blunt: Club, Hammer, Pick
- Ranged: Sling, Bow, Crossbow, Atlatl
- Magical: Focus (Sage Staff)
- Unarmed: fallback for unknown items

**Natural weapons — all Phase 8 entities covered:**
- Grumper → Blunt "maul" (0.76 acc, 1.7 range) ✅
- Truffleboar → Blunt "tusks" (0.78 acc, 1.6 range) ✅
- FennecFox → Edged "fangs" (0.80 acc, 1.5 range) ✅
- SkyPony → Blunt "hooves" (0.70 acc, 1.5 range) ✅
- RoyalAntelope → Piercing "horns" (0.72 acc, 1.5 range) ✅
- PygmyTortoise → falls to default Unarmed "nip" (harmless tortoise) ✅
- Mushroomoise → falls to default Unarmed "nip" (decorative) ✅
- Hamspore → falls to default Unarmed "nip" (livestock) ✅

**Natural armor — Phase 8 entities:**
- Grumper → 0.25 (thick swamp hide) ✅
- Truffleboar → 0.20 (bristled hide) ✅
- PygmyTortoise → 0.45 (domed shell — highest non-beetle) ✅
- Mushroomoise → 0.40 (mushroom-garden shell) ✅
- FennecFox, SkyPony, RoyalAntelope, Hamspore → 0.0 (no natural armor) ✅

**Blood colors:** Mammal=red, Insect=pale green, Reptile=dark red, Crustacean=pale blue, Bird=red, Mythical=violet, Shroomp=blue. Phase 8 mammals (Grumper, Truffleboar, FennecFox, SkyPony, RoyalAntelope) → red. Hamspore (mushroom-kin) → spore-teal via explicit case. ✅

---

## Recommendations

### Fixed in this session
1. **BuildButcherSlab missing from isBuildTool check** — ✅ FIXED. Added to `isBuildTool` check and button sync array in BuildPanel.cs. Material chips now show correctly when selecting Butcher Slab.

### Future enhancements (not bugs)
2. **Consider a BuildPanel Farm Plot tool** — if the player wants individual farm plot placement (vs zone painting), add a `BuildFarmPlot` enum value to DesignationTool/DesignationToolbar, and wire a button in BuildPanel. This would allow placing single plots outside of farm zones. Currently not needed — zone painting is the primary interaction pattern.

### No issues found
- Data registries are complete and consistent
- All behavior flows delegate designation IDs correctly
- Skill progression is wired for all task types
- Corpse/crop/breeding lifecycles are correct
- Save/load captures all Phase 8 state
- Skill curves are well-bounded and RimWorld-parity
- Combat profiles cover all entities and weapons
- UI panels are comprehensive (all build tools have buttons)
