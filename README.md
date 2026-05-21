# Sporeholm

A colony simulation game about a tribe of mushroom-people (**Shroomps**) trying to survive in a strange, fungal world. Built solo in **Godot 4.6** (C#).

Current version: **v0.5.84** (active development — Phase 5 / 5.5 complete, Phase 6 next).

---

## Project status at a glance

Sporeholm is mid-development. The core simulation loop — colony of pawns, needs, work, construction, crafting, mood, save/load — is shipped and stable. Combat, animals, weather, events, and disease are the next phases.

| Layer | State |
|---|---|
| Worldgen + 10 biomes | Shipped |
| Per-tile local map (up to 720 × 450) | Shipped |
| Pawn behavior, needs, mood, skills, traits | Shipped |
| Designation orders (Cut / Chop / Gather / Mine / Haul) | Shipped |
| Stockpile zones + Haul system | Shipped |
| Construction (walls / floors / doors / furniture) | Shipped |
| Crafting bills at workbenches (41 recipes) | Shipped |
| Room detection + room types (Bedroom / Kitchen / Workshop / Storage) | Shipped |
| Natural cavern roofs | Shipped |
| Per-tick mining scaled by skill + tools | Shipped |
| Save / load | Shipped |
| **Combat** | Stubs only — Phase 7 |
| **Animals & farming** | Stubs only — Phase 9 |
| **Weather & temperature** | Insulation half — Phase 10 |
| **Disease, research, eras** | Future phases |

---

## What's in the game right now

### World

- Procedurally generated world map (up to 192 × 192 tiles) with 10 biomes: Forest, Hills, Mountains, Peaks, Desert, Swamp, Coast, Island, MagicGrove, Plains.
- Per-biome local maps (up to 720 × 450 tiles) with stone subtype variation (Granite / Limestone / Marble / Obsidian / Quartz / Magicstone / MagicCrystal), wood subtypes (DeadWood / LivingWood / FungalWood), and gen-time features: caves, ruins, ore veins, buried treasure, partial skeletons, animal spawn points.
- **Resource scarcity slider** (Abundant → Scarce) on worldgen — controls vegetation density, ore vein chance, and minimum mushroom guarantees.

### Shroomps (the colonists)

- Five core needs: **Nutrition**, **Rest**, **Social**, **Magic Resonance**, **Safety**, with derived **Joy** and mood.
- **11 skills**: Botany, Mining, Athletics, Melee, Ranged, Crafting, Construction, Magic, Social, Study, Healing. Level 0–20 with diminishing XP curves.
- **7 roles**: Forager, Crafter, Guardian, Caretaker, Scholar, Sage, Elder. Each role has skill bonuses + default work priorities.
- **13 mushroom-themed biological traits** (penetrance 0–1) — active ones include **MyceliumAttuned** (magic resonance lasts longer), **ClusterFruiting** (social decays slower around colony-mates), **EfficientGills** (hunger decays slower), **RapidMetabolism** (hunger decays faster — biological cost), **SporeResonant**, **CompactStature** + **WispyFrame** (carry-capacity penalties). Plus personality archetypes + backstories + the **Pacifist** trait (auto-blocks weapon equipping, ~8% incidence).
- Full body-part hierarchy (Cap, Stalk, Gills, Spore Vent, Filter, legs, feet, hands) with damage, bleeding, downed state, natural healing.
- Sleep on the ground / in beds with mood thoughts (**WellRested**, **SleptInBedroom**, **SleptOnGround**).
- Visible animations: walking bob, sleeping (lying horizontal), eating (chew animation), bleeding (red drip).

### Work + designations

- Drag-paint orders: Gather food, Excavate stone/wood, Chop trees, Cut plants, Build (walls/floors/doors/furniture), Stockpile zones, Allowed Areas, Demolish.
- **Stockpile zones** with priority levels + per-zone item-type filters + Forbid/Allow flag.
- **Haul system** with destination reservation + crowd-aware pathing.
- **Per-tick mining** — skill curve activates: a level-0 novice takes ~8 sec / boulder; a level-20 master with a Masterwork Pick clears it in ~0.1 sec.
- **Tool bonuses**: equipping the right tool for the task (Pick for mining, Sickle for cutting, Sage Staff for Attune) gives a 1.30 × QualityMul speed multiplier.

### Construction

- Place blueprints; Crafters haul materials + frame the structure per-tick (skill-scaled).
- Structure types: Wall, Floor, Door, Shelf, Workbench, Hearth, Bed, Meditation Shrine, Shroom Board, Gossip Bench, Table, **Torch** (new this patch — wood haft + flame, +2°C per torch, light emission stubbed for Phase 10).
- Material choice per blueprint: 5 stone subtypes + 3 wood subtypes + **Pebblestone** (refined cobblestone). Each material has a distinct tint + Comfort / Beauty multiplier.
- 16-variant autotile walls so wall stretches blend horizontally.
- Demolish refunds 50% of original material cost.

### Crafting (Phase 5.5 Bills System)

A workbench holds a queue of bills. Crafters pick them up, consume ingredients from colony inventory, work for N ticks (skill-scaled), and produce items dropped on the workbench tile.

41 recipes across:

- **Food**: Cook Meal (4 of any food → 1 Prepared Meal), Juice Berries, Weave Moss Cloth, Weave Grass Linen.
- **Tools**: Knife / Pick / Hammer / Sickle / Sage Staff (Focus) / Basket — multi-variant per material family (Bone / Wood / Stone / Fungal).
- **Materials**: Saw Plank (3× input), Refine Pebblestone (4× input, per stone subtype).
- **Weapons**: Spear / Club / Sling / Bow / Crossbow / Atlatl / Sword / Axe — calibrated damage + accuracy (12 dmg / 0.70 acc Spear; 20 dmg / 0.70 acc Crossbow; 6 dmg / 0.55 acc Sling).
- **Defense**: Shield (3 material variants, 0.25 base block chance).
- **Medicine**: Magic Herb Poultice.

### Rooms

- Auto-detected via flood-fill when walls close off a space.
- **Room types** inferred from furniture: Bedroom (any bed), Kitchen (Hearth, no bed), Workshop (Workbench, no bed/hearth), Storage (Shelf only), Generic. Type drives mood thoughts and (future) work assignment.
- **Beauty score** from quality-weighted furniture + floors − corpses. High beauty → **BeautyPretty +3** mood; low → **BeautyUgly −3**.
- **Room temperature** offset folds in Hearths (+10°C each) + Torches (+2°C each) + insulation baseline.
- **Natural cavern roofs**: every tile inside a solid mass (Boulder, DeadLog, LivingWood, Skeleton, or cave interior) is auto-roofed at worldgen. Roofs persist when you mine the solid out — you get a real "you dug a cave" feel with a subtle dark blue tint over roofed tiles.

### Items + economy

- Procedural item system: every dropped item has Kind / SubType / Material (family + subtype) / Quality (Crude → Legendary) / Condition / Age / State (Fresh / Stale / Spoiled).
- Per-tile drops with 250-stack cap + type-locked tiles + spiral overflow.
- Equipment system: per-body-part slots (hands, head, torso, feet) with auto-equip for the current task.
- **Opportunistic weapon upgrade**: shroomps scan colony inventory for a better weapon and swap in (scored by damage × accuracy × quality × condition × skill bias). Pacifists never auto-equip a weapon.
- **Drop-unsuitable-tool**: when a task ends and the next task doesn't want the held tool, it's dropped on the ground (unforbidden) for haulers to return to a stockpile. Role-canonical tools (Sage's Sage Staff, Crafter's Hammer, Forager's Basket) are kept.
- **Item-drop icons**: 49 dedicated pixel-art variants so wood / stone / berries / cloth / bone / weapons all read at a glance.

### Pathfinding

- A* on an 8-connected grid with diagonal corner-cut check.
- Crowd cost (175 × per-tile shroomp count) so paths route around crowds.
- Reachability gating: idle destinations are never picked across walls.
- Per-tick movement claim counter eliminates doorway pileups.
- Stuck detection with tile-progress (not just pixel-progress), yield-on-stuck (blocker lies down to let asker climb over), and re-path on pawn-blocked cooldown.

### UI

- Bottom task bar (Orders, Build, Zones, Areas, Jobs, Resources, Shroomps, Animals).
- Tile-hover info + per-tile properties panel (Terrain, Roof status, Room, Vegetation, Stone, Items, Structure).
- Selection bracket on shroomps + tiles.
- Alert pane for urgent colony events.
- In-game message log (births, deaths, mood drops, **starvation alerts**, joining wanderers).
- Settings: UI scale, zoom speed, pan speed, save/load multi-slot.
- Music player widget on main menu + in-game with playlist crossfade.
- Dev panel (F12) with live perf counters: tick ms, A* calls/tick, success %, behavior/needs phase breakdown.

---

## Roadmap

| Phase | Theme | Status |
|---|---|---|
| 0 | System hardening | Complete |
| 1 | Population dynamics | Complete |
| 2 | World + local map | Complete |
| 2.5 | Scale refactor, terrain features, vegetation | Complete |
| 2.6 | River generation | Complete |
| 3 | Shroomp behavior system | Complete |
| 4 | Resource gathering, procedural items, starting inventory | Complete |
| 5 | Tile-based construction | Complete |
| 5.5 | Crafting bills | Complete |
| **6** | **Entity system** (animals + creatures) | **Next** |
| 7 | Combat (with Healer + Rescue + Weapons/Apparel) | Stubs ready |
| 8 | Events + Storyteller (Balanced / Patient / Cataclysmic) | Stub |
| 9 | Agricultural systems (animal husbandry, farming, hunting) | — |
| 10 | Weather + Environment (Insulation half done) | — |
| 11 | Technology + Culture (research + power) | — |
| 12 | Disease | — |
| 13 | Era system + Campaign mode | — |
| 14 | Polish + Individual mode | — |
| 14.5 | Sprite + Texture pass | — |

Full per-version detail in [`changelog.md`](changelog.md).

---

## How to run

You'll need **Godot 4.6+** with **.NET / C# support** (the Mono build).

1. Clone the repo.
2. Open Godot, import the project (`project.godot`).
3. Wait for the editor to finish importing assets + compiling C# scripts.
4. Press **F5** (or click Run) to launch.

The OpenGL Compatibility renderer is locked in for wider hardware compatibility, so the project should run on most modern integrated GPUs.

---

## How to play

### Starting a colony

1. From the **Main Menu**, click **New Game**.
2. On the **WorldGen** screen:
   - Set a world name + seed (optional — random by default).
   - Pick world size (96 / 128 / 192) and level (local map) size (160 × 100 up to 720 × 450; 240 × 150 is recommended).
   - Adjust **generation bias** sliders: Elevation, Rainfall, Temperature, Magic Density.
   - **Resource Scarcity** slider: drag from *Abundant* (default, normal density) to *Scarce* (¼ vegetation + ore veins) for a tougher start.
   - Click **Generate**.
3. On the world map, click a tile to preview its biome, expected resources, and elevation. Click **Begin Colony** to land there.
4. The **Scenario** screen lets you customize the founding 7 Shroomps — names, sex, age, role, traits, personality, preferences. Reroll any field, then click **Begin Colony**.

### Controls

| Action | Input |
|---|---|
| Pan camera | WASD / arrow keys / middle-mouse drag |
| Zoom (3 discrete levels) | Tab / mouse wheel |
| Select shroomp / tile | Left-click |
| Drag-paint orders | Left-click + drag |
| Issue move order | Right-click on a tile |
| Chain move orders | Shift + right-click |
| Pause / play | Spacebar |
| Speed up (2× / 3×) | Number bar on top-right |
| Dev panel | F12 |
| Open shroomp card | Click a shroomp |
| Open tile properties | Click a tile |

### The basic loop

1. **Bottom task bar** — open **Orders** to paint Gather / Excavate / Chop / Cut / Haul / Demolish designations.
2. **Build** tab — pick a structure (Wall / Floor / Door / Workbench / Bed / Hearth / Torch / Bed / Joy furniture), pick a material chip (Granite / Marble / DeadWood / etc.), drag to place blueprints.
3. **Zones** tab — paint stockpile rectangles, set priority + accepted item types.
4. **Areas** tab — paint per-shroomp allowed areas (or the shared *Home* area).
5. **Jobs** tab — 15-category priority grid per shroomp.
6. **Resources** tab — colony-wide item totals.
7. **Shroomps** tab — list of all colonists with quick navigation to their card.

### What to do first

A typical first colony:

1. Paint **Excavate** over a few Boulder tiles to get StoneBlocks.
2. Paint **Chop** on a couple of LargeMushroom tiles to get Fungal Wood.
3. Open **Build → Structure → Wall**, pick **DeadWood** or **Fungal Wood** as material, drag a small shelter perimeter.
4. **Build → Structure → Floor**, then **Door** for an entrance.
5. **Build → Furniture → Bed**, place a few inside the room.
6. **Build → Furniture → Workbench + Hearth**.
7. Click the Workbench tile, scroll to **Bills**, and queue **Cook Meal**.
8. Set up a **Stockpile zone** near the workbench for cooked food storage.
9. As skills rise, queue better recipes: **Carve Knife**, **Craft Spear**, **Saw Plank**, **Refine Pebblestone**, **Magic Herb Poultice**, etc.
10. Watch the **message log** — starvation alerts, mood drops, and births surface there. Mood drops to *Distressed* or below mean trouble.

### Tips

- **Beds + bedrooms**: a room with one bed becomes a Bedroom; sleeping there grants the **SleptInBedroom +2 mood** thought.
- **Cavern roofs**: dig into solid stone or wood to create roofed pockets — items inside decay slower (½× or ¼× with a Hearth).
- **Sages + Sage Staff**: Sages with a Sage Staff get a 1.3× × QualityMul bonus on Attune speed. Sages won't pick up weapons.
- **Pacifists** (~8% of pawns) refuse to auto-equip weapons. Check their card to see the trait.
- **Tool bonuses** stack with skill — even a Crude Pick speeds up mining by ~17%; a Masterwork Pick adds ~75%.
- **Save often** — F9 quicksave or via the menu. Multi-slot saves browse with rename/overwrite/delete.

---

## Credits

Music: see in-game **Credits** panel for full attribution. All bundled tracks are CC-BY / CC0 / royalty-free per the asset spec.

Engine: [Godot Engine 4.6](https://godotengine.org/) (MIT) with C# / .NET 8.

---

## Project structure

```
Sporeholm/
├── assets/              Sprites, music, fonts
├── scenes/              Godot scene files
├── scripts/
│   ├── simulation/      Sim thread — pure C#, no Godot UI dependencies
│   │   ├── systems/     Behavior / Cook / Bill / Equipment / Needs / etc.
│   │   ├── items/       ItemRegistry / Inventory / EquipSlot / Materials
│   │   └── crafting/    Recipe / RecipeRegistry / Bill
│   ├── world/           LocalMap / LocalMapGenerator / Pathfinder / RoomDetector
│   └── ui/              GameController + every panel / overlay
├── changelog.md         Full per-version detail
└── project.godot
```

The sim runs on its own thread; UI consumes snapshots via `SimulationSnapshot`. Save/load round-trips through `SaveManager.ColonySave` (JSON).

---

## Contributing


Sporeholm is a solo project in active development by Sam Dotson. Issues + feedback welcome via GitHub Issues; PRs are not currently accepted as the architecture is still in flux.

---

## License

To be determined before public release. Source is currently visible for transparency + portfolio purposes.
