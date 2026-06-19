# Sporeholm

**Sporeholm** is a colony-survival game about a little tribe of mushroom-folk — the **Shroomps** — making a home in a strange fungal world. Designed and developed by **Sam Dotson**.

Current version: **v0.8.9** — in active development.

---

## What is this game? (the plain-English version)

You look after a small group of mushroom creatures who've settled in the wilderness. You **don't** control them directly like pieces on a board. Instead, you set the priorities — *what to build, what to grow, who does which jobs* — and the Shroomps go about their lives on their own: working, eating, sleeping, making friends, getting hurt, recovering, and (with luck) growing the colony.

If you've ever played a "manage your own little settlement and try to keep everyone alive" game, that's the idea. Your job is to turn an empty patch of wilderness into a thriving home before hunger, the cold, or a wolf pack gets the better of you.

**A typical playthrough, in everyday terms:**

- **Clear some land** — dig out stone, chop big mushrooms for wood.
- **Build a shelter** — walls, a door, floors, beds, a campfire, a kitchen.
- **Grow food** — mark a field, pick a crop, and your farmers plant, tend, and harvest it over the in-game days.
- **Raise animals** — tame wild creatures, keep them in a pasture, and they'll give you milk, wool, and eggs — and breed into a herd.
- **Hunt** — send a hunter after wild game and butcher it for meat, hide, and bone.
- **Cook + craft** — turn raw food into meals; make tools, clothes, and weapons at workbenches.
- **Survive** — fend off hostile wildlife; your Shroomps take real wounds, bleed, and need a healer to patch them up.
- **Keep everyone happy** — feed them, give them beds and nice rooms, and manage moods, friendships, and the occasional meltdown.

Everything saves and loads, so you can put a colony down and pick it back up later.

> **Heads-up:** it's still being built. The full play loop above works today, but you currently launch it through the game engine (see **How to run** below). An easy one-click installer that updates and launches the game for you — and opens the door to player-made mods — is planned next (see **Roadmap**).

---

## Project status at a glance

Sporeholm is mid-development, but the **core game is playable end-to-end**: you can found a colony, build, farm, hunt, tame and breed animals, fight, cook, and keep your Shroomps alive — and save your progress. The current chapter (Phase 8 — *farming and animals*) is **complete**. What's left is more flavour and depth: weather, random events, research, and disease.

| Layer | State |
|---|---|
| Worldgen + 10 biomes | Shipped |
| Per-tile local map (up to 720 × 450) | Shipped |
| Pawn behavior, needs, mood, skills, traits | Shipped |
| Designation orders (Cut / Chop / Gather / Mine / Haul) | Shipped |
| Stockpile zones + Haul system | Shipped |
| Construction (walls / floors / doors / furniture) | Shipped |
| Crafting bills at workbenches (59 recipes) | Shipped |
| Room detection + room types (Bedroom / Kitchen / Workshop / Storage) | Shipped |
| Natural cavern roofs | Shipped |
| Per-tick mining scaled by skill + tools | Shipped |
| **Wildlife (30 species: friendly + neutral + hostile)** | **Shipped — v0.6.0 (15), expanded to 30 in v0.8.0a** |
| Save / load | Shipped |
| **Combat** | **Shipped — v0.7.x** (body-part combat, wounds, pain/venom, healer + rescue, layered armor, draft, training, weapons) |
| **Farming (crops + grow-zones + sow/harvest)** | **Shipped — v0.8.0** (9 crops, 3 Botany tiers, Grow priority, Husbandry skill) |
| **Hunting + butchery** | **Shipped — v0.8.1** (Hunt order, carcasses, Butcher Slab, meat/hide/bone) |
| **Animal husbandry (taming + produce + breeding + pastures)** | **Shipped — v0.8.2–0.8.5** (Tame order, Husbandry job, milk/wool/eggs, breeding, pasture containment) |
| **Weather & temperature** | Insulation half — Phase 10 |
| **Disease, research, eras** | Future phases |

---

## Everything in the game right now (the detailed list)

*The sections below are the full feature breakdown for the curious — feel free to skim. If you just want to play, jump to **How to run** and **How to play**.*

### World

- Procedurally generated world map (up to 192 × 192 tiles) with 10 biomes: Forest, Hills, Mountains, Peaks, Desert, Swamp, Coast, Island, MagicGrove, Plains.
- Per-biome local maps (up to 720 × 450 tiles) with stone subtype variation (Granite / Limestone / Marble / Obsidian / Quartz / Magicstone / MagicCrystal), wood subtypes (DeadWood / LivingWood / FungalWood), and gen-time features: caves, ruins, ore veins, buried treasure, partial skeletons, animal spawn points.
- **Resource scarcity slider** (Abundant → Scarce) on worldgen — controls vegetation density, ore vein chance, and minimum mushroom guarantees.

### Shroomps (the colonists)

- Five core needs: **Nutrition**, **Rest**, **Social**, **Magic Resonance**, **Safety**, with derived **Joy** and mood.
- **Relationships**: colonists build a numeric opinion of one another from social encounters, forming **Acquaintance / Friend / Rival / Lover** bonds (the last a hook for a future courtship system). Repeated good chats build friendship; repeated slights build rivalry.
- **Mental breaks**: a colonist whose mood collapses may briefly lose control — **sad wandering**, a **tantrum**, or a **daze** — overriding work and orders (but not self-defense), then recovering with a catharsis. A message-log alert fires when one starts.
- **13 skills**: Botany, Mining, Athletics, Melee, Ranged, Crafting, **Cooking**, Construction, Magic, Social, Study, Healing, **Husbandry** (added v0.8.0 for Phase 8 farming + animals). Level 0–20 with diminishing XP curves. Farming reads **Botany** (gates plantable crops + harvest yield); taming reads **Husbandry** (gates taming speed); butchery yield reads **Cooking**.
- **7 roles**: Forager, Crafter, Guardian, Caretaker, Scholar, Sage, Elder. Each role has skill bonuses + default work priorities.
- **13 mushroom-themed biological traits** (penetrance 0–1) — active ones include **MyceliumAttuned** (magic resonance lasts longer), **ClusterFruiting** (social decays slower around colony-mates), **EfficientGills** (hunger decays slower), **RapidMetabolism** (hunger decays faster — biological cost), **SporeResonant**, **CompactStature** + **WispyFrame** (carry-capacity penalties). Plus personality archetypes + backstories + the **Pacifist** trait (auto-blocks weapon equipping, ~8% incidence).
- Full body-part hierarchy (Cap, Stalk, Gills, Spore Vent, Filter, legs, feet, hands) with damage, bleeding, downed state, natural healing.
- Sleep on the ground / in beds with mood thoughts (**WellRested**, **SleptInBedroom**, **SleptOnGround**).
- Visible animations: walking bob, sleeping (lying horizontal), eating (chew animation), bleeding (red drip).

### Wildlife (v0.6.0)

The map is populated with **30 species** (15 in v0.6.0, 15 more in v0.8.0a) across friendly, neutral, and hostile dispositions — including mounts (Sky Pony, Shore Frog, Royal Antelope…), shear/egg producers (Hamspore, Honey Bee Swarm), and huntable game (Grumper, Truffleboar, Pygmy Rabbit…). Each has its own sprite, stats, AI behaviour, butcher drops, and agricultural tags; per-individual stats jitter ±10 % at spawn so a pack of three wolves isn't three clones.

- **Friendly / Passive** — Glowbunny, Shroomgoat, Shroomalo (the very-friendly mushroom-hamster), Mouse, Ladybug, Hermit Crab.
- **Neutral** — Squirrel, Bonecrest Beetle, Forest Boar, Cave Lizard.
- **Hostile** — Ant Soldier, Wasp Renegade, Snake, Wolf (pack hunter), Magic Wisp.

AI state machine: Wander / Flee / Hunt / Graze / Tamed / Dead. Hostiles aggro on the nearest non-pacifist shroomp within their range and attack on contact, applying damage that flows through the existing body-part / bleeding / downed pipelines — wolves will actually injure you. Friendlies flee from threats. Spawning is biome-tagged and population-capped; the map respawns ambient fauna on day boundaries to keep the world feeling alive.

Event-only big creatures (Bear / Leopard Tortoise / Tasmanian Mauler / Dragon / Mushroom Drake) are deferred until the Phase 9 Storyteller layer — they're scripted events, not random spawns.

Click any creature to open the **Entity Card** — a compact inspector showing species, description, health, mood (derived from health % + needs + AI state), and the simplified Nutrition + Rest needs. Updates in real time while open.

### Combat (v0.7.0)

A full body-part combat system layered on the wildlife AI. Shroomps and creatures share one combat engine: a strike resolves through range → accuracy → block → crit → hit-location → armor → damage → wound, and routes into the same body-part / bleeding / downed pipeline as the rest of the sim.

- **Wounds (Hediffs)** — every hit records a persistent wound on the struck part (Bruise / Cut / Fracture / Puncture / Mangle / Sever / Concussion, by weapon type × damage). Wounds carry severity, contribute **pain**, heal over time (tended wounds ~6× faster), and won't regrow a severed part.
- **Pain + venom** — high pain spoils a combatant's attacks and can knock them unconscious. Venomous attackers (Snake, Wasp) inject a venom load that thins the blood and decays over time; tending clears it.
- **Layered armor** — worn apparel mitigates damage scaled by material (hard plate beats hide beats cloth), condition, and quality. Mitigation varies by weapon type — blunt punches through plate, edged is well-stopped, magic bypasses entirely.
- **Healer + medicine** — a Doctor or Caretaker walks to the nearest wounded colonist and tends the worst wound in place, consuming a **Magic Herb Poultice** if one is stocked. Tending heals faster, restores part condition, and clears venom.
- **Rescue** — a Doctor or Caretaker carries a *downed* colonist to the nearest free bed to recover and be treated.
- **Training buildings** — a **Sparring Yard** (trains Melee) or **Training Dummy** (trains Ranged); idle Guardians and off-duty colonists drill there for combat XP in peacetime, with no real damage.
- **Draft** — a drafted colonist holds its post, auto-engages threats at a wider range, and will fight even unarmed.
- **Patrol** — select colonist(s), pick the **Patrol** order, and click two points (hold **Shift** for more) to set a looping route they walk as a standing order (yielding to needs + combat, resuming after). A plain right-click move cancels it.
- **Feedback + controls** — floating damage numbers, species-coloured blood decals, a combat message log, and sprite animations (attacker lunge, defender recoil + red flash). **Right-click a hostile creature to order an attack.**

### Work + designations

- Drag-paint orders: Gather food, Excavate stone/wood, Chop trees, Cut plants, Build (walls/floors/doors/furniture), Stockpile zones, **Farm grow-zones**, Allowed Areas, Demolish.
- **Stockpile zones** with priority levels + per-zone item-type filters + Forbid/Allow flag.
- **Haul system** with destination reservation + crowd-aware pathing.
- **Per-tick mining** — skill curve activates: a level-0 novice takes ~8 sec / boulder; a level-20 master with a Masterwork Pick clears it in ~0.1 sec.
- **Tool bonuses**: equipping the right tool for the task (Pick for mining, Sickle for cutting, Sage Staff for Attune) gives a 1.30 × QualityMul speed multiplier.

### Farming (v0.8.0)

- **Farm tool** in the Zones tab: pick a crop, then drag a rectangle over fertile ground (or roofed cave tiles for cave crops) to lay out a grow-zone. The **Grow** work priority drives it (Foragers farm by default; Elders / Caretakers / unassigned colonists fill in).
- **Nine crops, three Botany tiers**: Simple (Small Mushroom, Cave Moss, Spring Greens — Botany 0), Medium (Capberry, Sunberry, Pumpkin — Botany 3–5), Hard (Magic Herb, Large Mushroom, Magic Flower — Botany 6–9). Each crop chip shows its Botany requirement. Fungal crops yield more underground; the rest favour the surface.
- **Sow → grow → harvest loop**: crops grow autonomously through five stages (Sown → Sprouting → Growing → Ripening → Ripe) over **2–12 in-game days** depending on tier (v0.8.4 tuned grow times to the day/season calendar). **Botany** gates which crops a colonist can plant and scales the harvest yield. Harvested plots reset and re-sow themselves, so a tended field is a standing food supply.
- A translucent **grow-zone tint** shades each plot from tilled brown through green to gold when ripe; **hover** for the crop + stage, **click** for a Grow Zone inspector (crop / stage / Botany requirement / yield). The **Remove** brush clears grow-zone cells. Crops persist through save/load.

### Hunting + butchery (v0.8.1)

- **Hunt order**: drag a box over wild creatures to mark them. A colonist with the **Hunt** job (Guardians by default, Foragers as backup) pursues and kills each one through the combat engine — only armed, non-pacifist colonists give chase, and a hunter that can't catch fast fleeing prey gives up and moves on. The **Remove** tool cancels a hunt mark.
- **Carcasses**: a butcherable creature's body stays on the ground (a greyed corpse) instead of vanishing. A colonist butchers it in place into **Meat, Hide, and Bone** (drops vary by species); the **Cooking** skill drives the yield. A built **Butcher Slab** nearby boosts it. Un-butchered carcasses decay away over time.
- Raw meat is food — it feeds straight into the existing **Cook Meal** recipe. Hunt marks and carcasses persist through save/load.

### Taming + livestock (v0.8.2)

- **Tame order**: drag a box over tameable wild creatures to mark them. A colonist with the new **Husbandry** job (Caretakers by default; Foragers + Elders help) visits each and tames it over repeated trips — the **Husbandry skill** sets how fast. A marked creature holds still so the handler can walk up; even a marked predator can't lash out mid-tame. The **Remove** tool calls off a taming. Hunt and Tame are mutually exclusive.
- **Tamed livestock** join the colony: they never turn on you, graze peacefully near home, and are kept fed so they don't starve.
- **Produce on a cycle**: tamed milkable / shearable / egg-laying animals drop **milk / spore wool / eggs** about twice a day, hauled to your stockpiles (a Shroomgoat gives both milk and wool). Milk + eggs cook into meals; wool is a cloth material.
- **Breeding** (v0.8.3): two tamed, well-fed animals of the same breeding species, kept near each other, raise young — one of the pair gestates (~1.5 in-game days) and births a tamed offspring. A per-species **population cap** (8) lets a herd grow to a ceiling and hold, refilling after losses. Pregnant animals read "Expecting".
- **Pastures** (v0.8.5): paint a **Pasture** in the Zones tab to give your livestock a home — tamed animals gather and graze within the nearest pasture instead of wandering off. Soft containment (no fences needed); the Remove tool clears it, and pastures persist through save/load. This completes the Phase 8 farming-and-animals chapter.

### Construction

- Place blueprints; Crafters haul materials + frame the structure per-tick (skill-scaled).
- Structure types: Wall, Floor, Door, Shelf, Workbench, **Cooking Table** (dedicated cook station for the Cooking skill split), Bonfire (renamed from Hearth — heat source + half-speed cooking fallback), Bed, Meditation Shrine, Shroom Board, Gossip Bench, Table, Torch (wood haft + flame, +2°C per torch, light emission stubbed for Phase 10), **Sparring Yard** + **Training Dummy** (combat-practice furniture — Phase 7).
- **Production / Furniture tab split** in the Build panel: Workbench + Cooking Table sit in the new Production tab (where workstations belong); Bed / Table / Shelf / Bonfire / Torch / Sparring Yard / Training Dummy stay in Furniture.
- Material choice per blueprint: 5 stone subtypes + 3 wood subtypes + **Pebblestone** (refined cobblestone). Each material has a distinct tint + Comfort / Beauty multiplier.
- 16-variant autotile walls so wall stretches blend horizontally.
- **Demolish is a paintable task** (rewritten in v0.6.2): paint a built structure to mark it for tear-down (red X overlay), a Crafter walks to it and performs the work over many ticks (Construction skill drives speed). Refund is **20%–60% of material cost based on Construction skill** — skilled crafters salvage more. Blueprints still cancel instantly + refund delivered materials.

### Crafting (Phase 5.5 Bills System)

A workbench holds a queue of bills. Crafters pick them up, consume ingredients from colony inventory, work for N ticks (skill-scaled), and produce items dropped on the workbench tile.

59 recipes across:

- **Cooking** (Cooking Table; Bonfire fallback at half speed): Cook Meal (4 of any food → 1 Prepared Meal), Juice Berries. The Cooking Table is the dedicated full-speed cook station; a Bonfire can cook the same recipes at × 2.0 work-ticks so a bare colony can still feed itself before a Cooking Table is built.
- **Crafting** (Workbench): Weave Moss Cloth, Weave Grass Linen.
- **Tools** (Workbench): Knife / Pick / Hammer / Sickle / Sage Staff (Focus) / Basket — multi-variant per material family (Bone / Wood / Stone / Fungal).
- **Materials** (Workbench): Saw Plank (3× input), Refine Pebblestone (4× input, per stone subtype).
- **Weapons** (Workbench): Spear / Club / Sling / Bow / Crossbow / Atlatl / Sword / Axe — calibrated damage + accuracy (12 dmg / 0.70 acc Spear; 20 dmg / 0.70 acc Crossbow; 6 dmg / 0.55 acc Sling).
- **Defense** (Workbench): Shield (3 material variants, 0.25 base block chance).
- **Armor** (Workbench): Hat, Cloak, and Boots — each in a fabric tier (woven cloth) and two non-fabric tiers (hide, bone), giving a light → solid → heavy protection ladder.
- **Medicine** (Workbench): Magic Herb Poultice.

### Rooms

- Auto-detected via flood-fill when walls close off a space.
- **Room types** inferred from furniture: Bedroom (any bed), Kitchen (Bonfire OR Cooking Table, no bed), Workshop (Workbench, no bed/bonfire/cooking table), Storage (Shelf only), Generic. Type drives mood thoughts and (future) work assignment.
- **Beauty score** from quality-weighted furniture + floors − corpses. High beauty → **BeautyPretty +3** mood; low → **BeautyUgly −3**.
- **Room temperature** offset folds in Bonfires (+10°C each) + Torches (+2°C each) + insulation baseline.
- **Natural cavern roofs**: every tile inside a solid mass (Boulder, DeadLog, LivingWood, Skeleton, or cave interior) is auto-roofed at worldgen. Roofs persist when you mine the solid out — you get a real "you dug a cave" feel with a subtle dark blue tint over roofed tiles.

### Items + economy

- Procedural item system: every dropped item has Kind / SubType / Material (family + subtype) / Quality (Crude → Legendary) / Condition / Age / State (Fresh / Stale / Spoiled).
- Per-tile drops with 250-stack cap + type-locked tiles + spiral overflow.
- Equipment system: per-body-part slots (hands, head, torso, feet) with auto-equip for the current task. Worn apparel (**cloak / hat / boots**) renders on the colonist and provides material-scaled armor in combat.
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
| 6 | Entity system (animals + creatures) | Shipped (v0.6.0 — 15 species; expanded to 30 in v0.8.0a) |
| 7 | Combat (Healer + Rescue + Training + Weapons/Apparel) | Shipped (v0.7.2 — full body-part combat) |
| **8** | **Agricultural systems** (farming, animal husbandry, hunting) | **Complete** (v0.8.0 farming · v0.8.0a roster→30 · v0.8.1 hunting + butchery · v0.8.2 taming + produce · v0.8.3 breeding · v0.8.4 grow-time balance · v0.8.5 pastures) |
| **8.5** | **Launcher** — one-click install / auto-update / play, mod-ready | **Planned next** (before Phase 9) |
| 9 | Events + Storyteller (Peaceful / Random / Adventure — extensible) | Stub |
| 10 | Weather + Environment (Insulation half done) | — |
| 11 | Technology + Culture (research + power) | — |
| 12 | Disease | — |
| 13 | Era system + Campaign mode | — |
| 14 | Polish + Individual mode | — |
| 14.5 | Sprite + Texture pass | — |

Full per-version detail in [`changelog.md`](changelog.md).

---

## How to run

> **Coming soon:** a simple **one-click launcher** that downloads the latest version, keeps it updated, and starts the game for you — no engine or setup required (see the **Roadmap**). Until that lands, the game runs from the Godot engine, as below.

**Today (developer setup):** you'll need **Godot 4.6+** with **.NET / C# support** (the Mono build).

1. Clone the repo.
2. Open Godot, import the project (`project.godot`).
3. Wait for the editor to finish importing assets + compiling C# scripts.
4. Press **F5** (or click Run) to launch.

It uses the OpenGL Compatibility renderer for wide hardware support, so it should run on most modern computers, including ones with built-in (integrated) graphics.

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
2. **Build** tab — pick a structure (Wall / Floor / Door / Workbench / Cooking Table / Bed / Bonfire / Torch / Joy furniture), pick a material chip (Granite / Marble / DeadWood / etc.), drag to place blueprints. Sub-tabs: **Structure** (walls, floors, doors), **Production** (Workbench, Cooking Table), **Furniture** (Bed, Table, Shelf, Bonfire, Torch, Sparring Yard, Training Dummy), **Joy** (Shrine, Board, Bench).
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
6. **Build → Production → Workbench** (for crafting recipes) and **Build → Production → Cooking Table** (for meals). **Furniture → Bonfire** if you want room heat + a half-speed cooking fallback before the Cooking Table is built.
7. Click the Cooking Table tile, scroll to **Bills**, and queue **Cook Meal**. (Workbench bills run Crafting recipes only — knives, planks, cloth, weapons, etc.)
8. Set up a **Stockpile zone** near the kitchen for cooked food storage.
9. As skills rise, queue better recipes: **Carve Knife**, **Craft Spear**, **Saw Plank**, **Refine Pebblestone**, **Magic Herb Poultice**, etc.
10. Watch the **message log** — starvation alerts, mood drops, and births surface there. Mood drops to *Distressed* or below mean trouble.

### Tips

- **Beds + bedrooms**: a room with one bed becomes a Bedroom; sleeping there grants the **SleptInBedroom +2 mood** thought.
- **Cavern roofs**: dig into solid stone or wood to create roofed pockets — items inside decay slower (½× or ¼× with a Hearth).
- **Sages + Sage Staff**: Sages with a Sage Staff get a 1.3× × QualityMul bonus on Attune speed. Sages won't pick up weapons.
- **Pacifists** (~8% of pawns) refuse to auto-equip weapons. Check their card to see the trait.
- **Training**: build a **Sparring Yard** (Melee) or **Training Dummy** (Ranged) — idle Guardians and off-duty colonists drill there to raise combat skill in peacetime.
- **Combat**: right-click a hostile creature to order an attack. Wounded colonists bleed and can be **downed**; a Doctor or Caretaker will tend them — and carry a downed colonist to a bed.
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
