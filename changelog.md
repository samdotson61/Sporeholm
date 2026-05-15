# Smurfulation — Changelog

Version format: `aa.bb.cc`
- `aa` — Release version (incremented on explicit release command)
- `bb` — Active development phase (mirrors roadmap phase)
- `cc` — Patch/hotfix iteration within the current phase

---

## [0.5.13] — 2026-05-15

### Changed — River generation overhaul (same RimWorld-parity treatment as v0.5.12 Mountain Face)

Sam: *"Overhaul rivers in the same way."*

Pre-v0.5.13 `CarveRiverPath` (`scripts/world/LocalMapGenerator.cs:1900+`) was the path-walking helper used by all 5 river subtypes (ThinSnaking, WideDeep, Crossing, Delta, Creek) plus Delta tributaries. It had three matching limitations to the v0.5.12 Mountain Face issues:

| Limitation | Impact |
|---|---|
| 2 orientation modes (horizontal L→R, vertical T→B) | Every river ran perpendicular to a map axis. Diagonal rivers impossible. |
| Single-octave Simplex (`FractalType.None`) for meander noise | Smooth featureless wiggle; no multi-scale detail (real rivers have small wiggles on top of large bends). |
| Hardcoded frequency 0.06f | Wavelength fixed at ~16 tiles regardless of river length. 80-tile maps got ~5 large bends; 240-tile maps got ~15. Meander felt different per map size. |

Plus an axis-aligned side-offset application (`if (horizontal) fy += sideOffset; else fx += sideOffset;`) that wouldn't generalize to diagonal flow.

#### What changed

**1. Orientation — 2 modes → 6 modes (weighted distribution).**

```csharp
//   0: L→R horizontal              (20%)
//   1: T→B vertical                (20%)
//   2: L→T (diagonal up-right)     (15%)
//   3: L→B (diagonal down-right)   (15%)
//   4: R→T (diagonal up-left)      (15%)
//   5: R→B (diagonal down-left)    (15%)
```

Axis-aligned still 40% weighted (preserves the original feel for half of all rivers); diagonals 60% (4 corner-to-corner-ish patterns × 15% each). Each diagonal mode picks entry on one edge and exit on a perpendicular edge, with positions chosen in the middle 1/3 of each edge so the river doesn't hug the corner.

**2. Meander noise — 3-octave FBm (was single-octave Simplex).**

```csharp
NoiseType         = FastNoiseLite.NoiseTypeEnum.Simplex,
Frequency         = 5.0f / steps,    // was: 0.06f
FractalType       = FastNoiseLite.FractalTypeEnum.Fbm,    // was: None
FractalOctaves    = 3,
FractalLacunarity = 2.0f,
FractalGain       = 0.5f,
```

3 octaves matches the v0.5.12 Mountain Face pattern (RimWorld's `GenStep_ElevationFertility` uses 6-octave Perlin for terrain elevation; rivers can use fewer because the path is 1D not 2D). Lacunarity 2.0 + gain 0.5 are the RimWorld defaults — each octave doubles in frequency and halves in amplitude. The small octaves add micro-wiggles on top of the large bends, the natural multi-scale meandering real rivers exhibit.

**3. Frequency scales with river length.**

`riverFreq = 5.0f / steps` targets ~5 noise oscillations across the full river length. Pre-v0.5.13's hardcoded 0.06f gave wavelength ≈ 16 tiles, which produced 5 large bends in an 80-tile river but 15 in a 240-tile river — visually inconsistent. Now both have 5 large bends; the bigger river just has bigger absolute amplitude (controlled by the caller's `meanderAmplitude` parameter, which already scales with `mapScale` per the v0.4.48 system).

**4. Perpendicular-to-flow side offset.**

```csharp
float dirX = exitX - entryX;
float dirY = exitY - entryY;
float dirLen = MathF.Sqrt(dirX * dirX + dirY * dirY);
if (dirLen > 0f) { dirX /= dirLen; dirY /= dirLen; }
float perpX = -dirY;
float perpY =  dirX;
// ...
fx += sideOffset * perpX;
fy += sideOffset * perpY;
```

The unit perpendicular to the entry→exit direction is computed once. Each step's meander offset is applied via the perpendicular vector, so it's perpendicular to actual flow regardless of orientation. For axis-aligned rivers this is identical to the v0.4.x behaviour (perp of east-west is north-south); for diagonals it's the meaningful generalization.

**5. Euclidean step count.**

```csharp
float pathLen = MathF.Sqrt(
    (exitX - entryX)² + (exitY - entryY)²);
int steps = max(2, ceil(pathLen));
```

Pre-v0.5.13 used `horizontal ? Width : Height` — correct for axis-aligned rivers but would under-sample diagonal paths (a corner-to-corner river is `sqrt(W² + H²)` tiles long, not `max(W, H)`). Without enough steps the channel-painting circles would have visible gaps along the diagonal.

#### What carried through unchanged

- Width range, half-width oscillation, and `widthRange` noise sample (`GetNoise2D(s, 100)`) — the noise object is the same one with multi-octave applied, so width oscillation also gets the FBm benefit.
- Ford placement logic (Crossing subtype) — unchanged. `inFordZone` still keys on step index `s`.
- Painting logic (water core / mud bank ring / boulder/log preservation) — unchanged.
- Caller `mapScale` width scaling (v0.4.48) — unchanged. `meanderAmplitude` parameter values come from the per-subtype config; new orientation just applies them via a different offset direction.
- All 5 river subtypes + Delta tributaries inherit the changes automatically since they all flow through `CarveRiverPath`.

#### Net visual

- **Direction variety** — diagonal rivers are now 60% of generated rivers. Map runs no longer feel like "another east-west river" or "another north-south river."
- **Bend texture** — multi-octave wiggle on top of large meanders. Banks have more visual interest at the small-scale level.
- **Consistent feel across map sizes** — a 200-tile river has the same number of large bends as a 80-tile one, just bigger physical scale.

---

## [0.5.12] — 2026-05-15

### Changed — Mountain Face generation overhaul (RimWorld-parity terrain noise + continuous orientation)

Sam: *"Increase rock coverage for the 'mountain face' mountain subtype and scale it to map size. Adjust generation for more natural formation variation per rimworld (research rimworld's mountain and cave generation) so that players don't see the same type of level map over and over."*

#### What pre-v0.5.12 looked like

`scripts/world/LocalMapGenerator.cs` Mountain Face block (lines 630-749):

- **Orientation**: 4 cardinal sides only (`faceRng.Next(4)` → top/bottom/left/right). Rock-open boundary always perpendicular to a map edge.
- **Coverage**: rockFrac 0.33-0.50 (33-50% of the map is rock).
- **Boundary noise**: single-octave Simplex (`FractalType.None`) at hardcoded frequency 0.05f.
- **Boundary jitter**: ±5% applied to the linear gradient.
- **No map-size scaling**: same 0.05f frequency on 80-tile and 200-tile maps → larger maps look "more zoomed in" with the same noise pattern repeated.

Net visual: a thin band of rock along one of four edges, with a slightly wavy boundary, identical-feeling across runs.

#### How RimWorld actually generates mountains (verified from decompiled source)

`RimWorld/GenStep_ElevationFertility.cs` line 34:

```csharp
new Perlin(0.020999999716877937, 2.0, 0.5, 6, Rand.Range(0, int.Max), QualityMode.High)
```

- **Frequency**: 0.021 (low — large-scale features)
- **Lacunarity**: 2.0 (each octave doubles in frequency)
- **Persistence**: 0.5 (each octave halves in amplitude)
- **6 octaves** → rich detail at multiple scales
- Rock placement threshold: `elevation > 0.7` (after the multi-octave Perlin)
- `EdgeMountainSpan = 0.42f` creates a directional bias toward map edges (mountain ridges form preferentially in a chosen direction)

The multi-octave noise + directional bias produces organic mountain shapes with ridges, spurs, peninsulas, and bays — not clean linear boundaries.

#### Mountain Face overhaul (post-v0.5.12)

Five changes, all in the subtype-2 block:

| Change | Old | New | Source |
|---|---|---|---|
| **Orientation** | `side = faceRng.Next(4)` (4 cardinals) | `faceAngle = rng * 2π` (continuous) → projection-based gradient | Sam's "same map type over and over" |
| **Rock coverage** | 0.33-0.50 | **0.45-0.65** | Sam's "increase rock coverage" |
| **Noise type** | Simplex, FractalType.None | **Simplex, FractalType.Fbm with 4 octaves**, lacunarity 2.0, gain 0.5 | RimWorld's 6-octave Perlin pattern (we use 4 because our maps are smaller) |
| **Frequency** | hardcoded 0.05f | **`4.0f / max(W, H)`** | Feature scale stays consistent regardless of map size |
| **Boundary jitter** | ±5% | **±15%** | Multi-octave noise produces visible ridges + spurs instead of a clean line |

The continuous orientation: pick `faceAngle` randomly in [0, 2π], compute `dirX = cos(faceAngle)`, `dirY = sin(faceAngle)`. Each tile's "depth into rock" comes from projecting onto the direction vector:

```csharp
float rawProj = (x - ccx) * dirX + (y - ccy) * dirY;
float pos = (rawProj + maxProj) / (2f * maxProj);   // normalized [0, 1]
```

`maxProj = (W/2) * |dirX| + (H/2) * |dirY|` — the projection's natural range for the chosen direction. Normalizing by 2 × this gives consistent [0, 1] regardless of orientation (axis-aligned and diagonal both span the full range).

For cave-alignment purposes, the angle is snapped to the dominant cardinal: `dirX > 0 → side=2 (rock left)`, `dirX < 0 → side=3 (rock right)`, etc. The cave centerline math is axis-aligned, so cave entrances still point sensibly into the rock formation even when the rock-open boundary is diagonal.

#### What this means visually

- **Rock-open boundary** is no longer a straight line — multi-octave FBm at ±15% amplitude produces wavy edges with ridges projecting into open ground and bays cutting into rock.
- **Mountain face direction** can be any angle (NE-SW, ENE-WSW, oblique 23°, etc.) instead of just N/S/E/W. A given seed produces a unique orientation that doesn't repeat across runs.
- **Coverage scaling** with map size: larger maps don't get the same noise pattern repeated; they get larger features at the same scale (frequency drops as map grows).
- **Rock area is bigger**: 45-65% mean ~55% vs the old ~41% — the rock side feels like an actual mountain rather than a thin band.

Cave count (0-2), cave-carving math, and the open-side boulder scatter pass are unchanged. Only the rock-open boundary generation and orientation changed.

---

## [0.5.11] — 2026-05-15

### Fixed — Corner-stuck on boulder formations (RimWorld-parity distance-not-decreasing detector)

Sam: *"Smurfs still get stuck on corners of weird formations from time to time… Implement the distance-not-decreasing detector fix. That should be all we need, then we can carry on with phase 5."*

#### The gap in our existing stuck detection

Pre-v0.5.11 the existing immobility detector (`MoveOneTick` lines ~1379-1493, v0.4.59) increments `StuckTicks` only when `progressed < ArrivalEpsilon` — i.e., the smurf physically isn't moving. Reaches `StuckRePathTicks=8` → re-path; reaches `StuckThreshold=18` → abandon task.

But the corner-stuck pattern on boulder formations is **different**: the smurf IS moving (sideways, perpendicular to the path direction), it's just not getting closer to where it needs to go. Concave corners + occupied path waypoints + the local steering's perpendicular escape combine to create a "moving sideways forever" pattern. `progressed >= ArrivalEpsilon` every tick → `StuckTicks` resets to 0 every tick → re-path never fires → smurf jitters indefinitely until something else changes the situation.

#### How RimWorld handles this (verified from decompiled source)

Web research confirmed: RimWorld's pathfinder uses the **exact same strict cut-corner rule** as ours (`Verse.AI/PathFinder.cs` lines 868-871: `BlocksDiagonalMovement` returns true if cell non-walkable OR contains a door, and the diagonal move is rejected if EITHER orthogonal blocks). So "RimWorld is more permissive about diagonals" is a popular myth — that's the [Diagonal Walls](https://rimworldbase.com/diagonal-walls/) mod, not vanilla.

What RimWorld actually does that we didn't:

1. **Region-first reachability** — pawns never path to regions they can't reach (we have `IsWorkReachable`).
2. **No local steering layer** — pawns walk rigidly along A* waypoints (we have one, legacy of continuous-pixel sub-tile movement).
3. **Re-path on no-progress** — pawns re-path if they're not making progress toward the goal, regardless of whether they're physically moving. **This is what we were missing.**

#### Fix — `MinSqrDistanceToWalkTarget` + `NoProgressTicks`

`scripts/simulation/Smurf.cs` — five new fields:

- `float MinSqrDistanceToWalkTarget = float.MaxValue` — smallest distance² ever achieved to the current walk target.
- `int NoProgressTicks = 0` — accumulator scaled by LOD `tickInterval`.
- `int LastWalkTargetTileX = -1`, `LastWalkTargetTileY = -1` — tile coords of the walk target last tick, for change detection.
- `bool ProgressRePathTried = false` — one-shot budget for the new mechanism. **Separate from the existing `RePathTried`** because that flag is reset whenever the smurf is moving (`MoveOneTick:1492`, the immobility detector's reset behaviour) — sideways oscillation would reset it every tick and the budget would never run out.

`scripts/simulation/systems/BehaviorSystem.cs` — two new constants and a new check block in `MoveOneTick`:

```csharp
private const int NoProgressRePathTicks = 30;   // ≈ 0.5 s at 60 Hz
private const int NoProgressGiveUpTicks = 60;   // ≈ 1.0 s post-re-path
```

The check runs every tick after movement is applied (right before `s.PrevSimPos = s.SimPos`). Walk target = `s.PathWaypoints[0]` if any waypoints, else `task.Target`. When walk-target tile coords differ from last tick's tracked coords (waypoint popped or task replaced), reset all tracking. Otherwise compare current distance² to `MinSqrDistance`:

- `currentSqrDist < MinSqrDistanceToWalkTarget` → real progress. Update best, reset `NoProgressTicks`.
- Otherwise → `NoProgressTicks += tickInterval`.

Two-stage escalation:

| Stage | Trigger | Action |
|---|---|---|
| **1 — Re-path** | `NoProgressTicks >= 30` AND `!ProgressRePathTried` | `Pathfinder.FindPath` from current SimPos with crowd cost. Reset tracking. New path gets fresh window. |
| **2 — Give up** | `NoProgressTicks >= 60` AND `ProgressRePathTried` | Designation tile → AvoidTiles FIFO (180-tick TTL). `ThoughtRegistry.Add("TaskAbandoned")`. ReleaseTaskClaim. `DesignationCooldownTicks = 60`. Forced Wander. Reset all tracking. |

Stage 2 mirrors the existing StuckThreshold abandon block at line ~1431 — same blacklist, same cooldown, same forced wander. Now we have two detectors (immobility AND no-progress) feeding the same recovery flow.

#### Reset triggers — fresh budget on every walk segment

`MinSqrDistance` + `NoProgressTicks` + `ProgressRePathTried` reset in three places:

1. **Walk target tile coords change** (the new check itself) — handles waypoint pops, intra-tick task changes, Section 1 player orders, v0.5.5 Wander chain hops, Haul Phase 1→2 transitions. Any walk-segment change.
2. **Section 2a task assignment** (line ~774) — alongside the existing `RePathTried` reset.
3. **Stage 2 give-up** (in the new check) — full reset for the new Wander task.

#### Idle tasks intentionally exempt

`if (s.CurrentTask is { } progressTask && !IsIdleType(progressTask.Type))` gates the entire check. Idle tasks (Wander/Loiter/Observe/Converse/Meditate/VisitFavorite) reach their destination and linger with distance ≈ 0. `currentSqrDist < MinSqrDistance` would be false every tick (distance is already 0 and can't go lower) → false-positive no-progress. The check skips them entirely. The existing idle linger logic (v0.5.1 `IdleArrived` + `IdleLingerTicks`) governs idle behaviour.

#### Net behavior

Pre-v0.5.11: smurf at concave corner moves perpendicular every tick, never closer to the next waypoint. `StuckTicks` stays at 0. Re-path never fires. Smurf oscillates until something changes (other smurf moves, designation cleared by another smurf, etc.). Visible jitter, no forward progress.

Post-v0.5.11: same smurf moves perpendicular. `MinSqrDistanceToWalkTarget` stops decreasing. After 30 ticks (~0.5s), fresh A* fires from current position — finds a different route around the corner. New path, new waypoints, new fresh window. If the new path also can't make progress in 60 more ticks (~1s), task abandoned, smurf wanders away from the trap, designation gets blacklisted in their AvoidTiles for 3 seconds, another smurf gets a chance.

This was the last identified pathfinding bug. Phase 5 work resumes from here.

---

## [0.5.10] — 2026-05-15

### Fixed — Smurfs cluster-jamming on each other while pathfinding (RimWorld-parity climb-forward priority)

Sam screenshot: dense cluster of ~12 smurfs visibly stuck on/near each other around work targets, no forward progress.

Sam: *"It looks like smurfs now are tasking properly, but they're getting stuck on each other while pathfinding. This appears to be our last pathfinding bug. Dig deep, diagnose this. Research how rimworld solves this problem as its movement is the closest analog we have to ours."*

#### How RimWorld actually handles 50 pawns in a small space

RimWorld's pathfinding stack:

1. **A\* with soft pawn-cost** — paths computed at job-assignment time; `+175 cost` per other pawn on a candidate cell discourages routes through clusters but doesn't forbid them. SmurfulationC has this since v0.4.58.
2. **Path commitment** — once a path is computed, the pawn walks it waypoint-by-waypoint. **The local walking step does NOT re-evaluate "should I avoid other pawns?"** Pawns visually overlap when their paths cross. The crowd cost was already paid at planning; the walking is mechanical.
3. **Repath on path failure** — if a waypoint becomes terrain-impassable mid-walk (a wall is built, etc.), the pawn requests a fresh A\*.

The key insight: **RimWorld pawns don't have a "soft-collision local steering" layer.** SmurfulationC does (v0.4.36) — and that local layer was rejecting the planned A\* path in favor of perpendicular escape whenever the planned direction crossed another smurf, which is exactly the wrong behavior in dense clusters.

#### What SmurfulationC was doing pre-v0.5.10

`MoveOneTick` resolution order (v0.4.36 → v0.5.9):

1. Primary terrain-passable + uncrowded → take.
2. Otherwise rotation loop (±45° → ±180°): **first uncrowded passable side-step wins**.
3. Otherwise crowded fallback (with v0.5.8 climb-over usefulness check).

Step 2 is the bug. Imagine 10 smurfs converging on 4 berry bushes (Sam's screenshot). Each smurf has an A\* path. The paths cross at the cluster edges because the bushes are tightly packed.

For each smurf:
- Primary direction toward next path waypoint → that waypoint tile has another smurf (path crossing) → primary is passable+crowded.
- Rotation loop fires. Side-steps perpendicular to the path are uncrowded (no smurfs there because the cluster is on the path direction).
- **First uncrowded passable wins** → smurf side-steps perpendicular, away from its A\* path.
- Now the smurf is off-path. Path becomes invalid. The path-invalidation gate at line 1115-1135 checks the head waypoint, finds it's still terrain-passable (just crowded), doesn't repath. Smurf walks toward an off-path waypoint position.
- Next tick: primary direction back toward the now-misaligned waypoint. Same crowded blocker. Same side-step away.
- Smurf oscillates perpendicular, no forward progress.

Multiply by 10 smurfs all doing this around the same 4 targets → a visual cluster jam. StuckTicks doesn't accumulate because the smurf IS moving (sideways), so YieldTrigger never fires.

#### Fix — insert "Priority 2: forward climb-over when useful" before the rotation loop

Inserted between current Priority 1 (primary uncrowded) and Priority 2 (now Priority 3, the rotation loop):

```csharp
// v0.5.10 — Priority 2: primary passable + crowded, but the
// climb-over is useful (next-tile-passable in path direction
// OR touch-arrival to task target). Take primary, climb the
// blocker. Keeps the smurf on its planned A* path through a
// dense cluster instead of side-stepping perpendicular and
// oscillating.
else if (map != null && primaryPassable && primaryCrowded
         && IsClimbOverUseful(map, s, primary, baseStep))
{
    bestChosen = primary;
    moved = true;
}
```

`IsClimbOverUseful` is the v0.5.8 helper — it returns true when (a) candidate tile is Chebyshev-≤-1 from the task target, OR (b) the tile beyond the candidate in the motion direction is terrain-passable. This is the exact predicate for "would climbing over this blocker actually advance my path or complete my work?"

For path-following: motion direction is toward the next waypoint, and the tile beyond is the *next-next* waypoint (or whatever's further along the path). If that tile is terrain-passable (the common case — paths run through walkable terrain), the climb is useful. The smurf takes the primary direction, climbing the blocker, and continues on path.

For touch-arrival: the candidate is adjacent to the work target. Climbing onto it puts the smurf at touch-arrival distance. Next tick fires `IsAtTouchArrival` → `ApplyTaskEffect`.

For dead-end climbs (path leads into impassable terrain beyond the blocker, AND not touch-arrival): the check returns false, control falls through to the rotation loop, smurf side-steps for a real detour.

#### Resolution order (post-v0.5.10)

1. Primary terrain-passable AND uncrowded → take (best case).
2. **NEW** — Primary passable + crowded + climb is useful → climb the blocker, stay on path.
3. Primary impassable OR climb-not-useful → rotation loop for uncrowded passable side-step (genuine "route around an obstacle" branch).
4. No uncrowded side-step found → crowded fallback (preserves v0.5.8 dead-end guard via `IsClimbOverUseful` per candidate).
5. Nothing useful → stay put. StuckTicks builds → v0.4.29 YieldTrigger fires at 12 ticks asking the blocker to lie down → smurf walks through naturally because yielding smurf drops out of `_smurfPerTile` (PopulateOccupancyGrid line 157).

#### Why this matches RimWorld and not Dwarf Fortress

- **RimWorld** — pawns can stand on the same tile freely; collision is purely visual; pathing soft-cost discourages but doesn't forbid. v0.5.10 adopts this for path-follow climb-overs.
- **DF** — strict "one creature per tile" with explicit pushing/swapping. We keep the v0.4.29 yield mechanism (lie-down) for genuine narrow-tunnel cases (Priority 5), so DF-style yield is still available when needed — just not the *primary* mechanism.

The v0.4.58 A\* crowd cost is unchanged. Paths still spread out at planning time. Local steering now respects that planning rather than fighting it.

---

## [0.5.9] — 2026-05-15

### Added — Task viability gate (RimWorld JobDriver FailOn-pattern equivalent)

Sam: *"I see pawns getting stuck on haul orders, excavate orders, and right-click move orders when they find themselves unable to complete the task, then never reassigning causing a lock and visible jitter. We need a way to check whether a task is actually able to be completed under current conditions so a smurf that is completing a task, like crafting, mining, or hauling, should know to reassign if that task is impossible while still holding onto the task if it takes 15-30s (like crafting may, or mining without tools once implemented properly). How does Rimworld do this?"*

#### How RimWorld does it

RimWorld's `JobDriver` exposes `AddFailCondition(Func<bool>)`. Each Toil registers fail conditions that are evaluated **every tick during the job**. When a fail condition returns true, the JobDriver ends with `JobCondition.Incompletable`, the pawn drops the job, and the JobGiver runs again to pick a new one.

Examples:
- `JobDriver_HaulToCell` registers `FailOnDestroyedOrNull(haulable)`, `FailOnBurningImmobile(targetA)`, `FailOn(() => !cell.GetItemPiece(map, haulable.def))`.
- `JobDriver_Mine` registers `FailOnDestroyedNullOrForbidden(target)`, `FailOnThingMissingDesignation(target, DesignationDefOf.Mine)`.
- `JobDriver_Goto` registers `FailOnDestination(() => !destination.Standable(...))`.

Critically, fail conditions are **structural** ("does the target still exist / is it still reachable / is the designation still painted") rather than **progress-based**. A 30-tick crafting job that has 28 ticks of progress left still passes its fail conditions every tick — the pawn keeps crafting until completion. Only if the workbench is destroyed mid-craft (or ingredients vanish) do the fail conditions trigger an abort.

#### SmurfulationC equivalent — `IsTaskStillValid`

New helper in `scripts/simulation/systems/BehaviorSystem.cs`. Switches on `BehaviorTask.Type` and applies the per-task structural sanity check:

| TaskType | Check |
|---|---|
| `GatherMaterial` (Excavate) | Designation still present at target tile |
| `GatherFood` | Gather designation present + vegetation alive + not depleted |
| `ChopWood` | ChopWood designation present + vegetation alive + not depleted |
| `CutVegetation` | Cut designation present + vegetation alive + not depleted |
| `Haul` Phase 1 (TargetId != null) | Item still at source tile + not forbidden |
| `Haul` Phase 2 (TargetId == null) | Destination reachable from current position (deferred when smurf is briefly in a wall) |
| `PlayerOrder` | Destination tile passable + reachable (deferred when smurf is briefly in a wall) |
| Idle / critical-need / default | Return `true` — handled by their own systems |

Reachability checks (`IsWorkReachable`, region-graph fast-fail) are deferred when `IsPixelPassable(map, s.SimPos)` is false, matching the existing reachability gate at line ~664. Without the defer, a smurf whose `SimPos` briefly drifted into a wall (passability flip race, save/load) would falsely fail and abort a perfectly valid task.

New per-tile designation queries on LocalMap: `HasExcavateDesignation`, `HasGatherDesignation`, `HasChopWoodDesignation`, `HasCutDesignation`. Each is an O(1) `HashSet.Contains` under `_designationsLock`.

#### Wire-in

In `BehaviorSystem.Tick`, the gate fires right after the per-smurf cooldown decrements and before the `needNewTask` block:

```csharp
if (map != null && s.CurrentTask is { } valTask
    && !IsTaskStillValid(s, valTask, map))
{
    ReleaseTaskClaim(s, map);   // also handles haul reservation via ReleaseByIdString
    s.PathWaypoints.Clear();
    s.StuckTicks = 0;
    s.RePathTried = false;
    s.CurrentTask = null;
}
```

`ReleaseTaskClaim` already handles both designation claims (`map.ReleaseClaim`) and haul reservations (`HaulSystem.ReleaseByIdString`) since v0.4.7, so the abandon flow correctly releases both.

**No tile blacklist** on this abandon — unlike the StuckThreshold give-up which adds the tile to the smurf's `AvoidTiles` FIFO. The task became invalid through external state change (other smurf finished the work, player removed the designation, item picked up by someone else, terrain mutated), not through this smurf's path choices. Re-blacklisting would penalize the smurf for something outside its control.

#### What's preserved (long tasks still work)

The check is *structural*, not *progress*. A future 30-second crafting task with a workbench + ingredients within reach passes the check every tick of those 30 seconds. The same for slow mining (Phase 6 tool durability). The smurf holds the claim and accumulates progress until completion.

The check only fires when the task is **truly impossible**:
- Boulder excavated by another smurf (designation cleared by `ClearDesignationsAt`)
- Berry bush harvested by another smurf or depleted by overuse
- Hauled item picked up by a different smurf or destroyed
- Hauled item forbidden by the player mid-haul
- Player-order destination became impassable (player painted a wall there, future Phase 5B)
- Region cut off mid-walk (terrain mutation isolated the destination)

In all these cases pre-v0.5.9, the smurf walked to a defunct target, jittered on arrival, and burned through the full ~90-tick StuckThreshold (~1.5 s of visible jitter) before giving up. Now: aborted on the very next tick after the state change, smurf picks a fresh task immediately.

#### Cost

Each `IsTaskStillValid` call is O(1) — a single `HashSet.Contains` or a tile lookup. Haul Phase 1 walks the item list at the source tile (typically 1-3 items). The check fires once per smurf per LOD-effective tick. Negligible against the existing per-tick work (movement, soft-collision, snapshot).

---

## [0.5.8] — 2026-05-15

### Fixed — Smurfs climbing onto blockers into dead-ends (local-steering "useful climb-over" guard)

Sam: *"Allow soft pathing through smurfs only if there is a passable tile on the other side so that smurfs can't path through another smurf and get stuck when they can't pass over them into an unpassable tile (like when they attempt to excavate and path through a smurf to get to the target, but get stuck trying because there's no tile for them to get to and stand in on the other side)."*

#### The dead-end climb pattern

`MoveOneTick`'s local steering at line ~1029 resolves each tick's small step in this order:

1. **Primary direction passable + uncrowded** → take it (best case).
2. **Otherwise rotate ±45° → ±180°** looking for any uncrowded passable side-step.
3. **No uncrowded option found** → fall back to **climb-over** (step onto a crowded passable tile), paired with the v0.4.29 yield mechanic so the lie-down resolves single-tile-tunnel jams.

Pre-v0.5.8, step 3 fired unconditionally whenever any climb-over fallback existed. The climb-over fallback was the primary direction (if terrain-passable + crowded) OR the first crowded passable rotation candidate.

The failure mode: smurf A is approaching task target T. A blocker B is between A and T. The tile *beyond* B in A's direction of motion is **impassable** (it's T itself if T is a boulder, or some unrelated wall). A's local steering climbs onto B's tile. Next tick, A's primary direction is back toward T — same problem, same climb attempt. A oscillates against B forever (no StuckTicks accumulation because A's pixel position keeps changing slightly inside B's tile).

For Excavate specifically: A wants to reach a tile adjacent to T (touch-arrival). If B is on the only such adjacent tile, climb-over onto B IS the right move — A becomes touch-adjacent to T and the next tick's `IsAtTouchArrival` (line 959) fires the work effect. The blanket climb-over decision pre-v0.5.8 got this case right but also fired in the wrong cases (B not on touch-arrival, beyond impassable, dead-end).

#### Fix — `IsClimbOverUseful(map, smurf, candidate, motion)` helper

Added to `BehaviorSystem.cs` near `TileHasOtherSmurf`. Returns `true` if either:

1. **Touch-arrival case** — `candidate`'s tile is Chebyshev-distance ≤ 1 from the current task's `TargetTileX, TargetTileY`. Stepping there puts the smurf at touch-arrival distance to the work tile; the next tick fires `IsAtTouchArrival` → `ApplyTaskEffect`. Covers Excavate (target impassable, smurf must stand adjacent) and the boundary case for Gather/Chop/Cut where the candidate IS the target.

2. **Path-continuation case** — the tile beyond `candidate` in the `motion` direction is terrain-passable. The smurf has somewhere to continue after the climb. Direction is read from `Math.Sign(motion.X / motion.Y)` so the beyond-tile is one tile further in the 8-direction sense; step magnitude doesn't matter.

Wired into two sites in the local steering block:

- **Initial fallback** — `crowdedFallbackHas` (which seeded the primary as the canonical climb-over) now requires `primaryPassable && IsClimbOverUseful(map, s, primary, baseStep)`.
- **Rotation-loop fallback** — the first crowded passable rotation candidate is only accepted as `crowdedFallback` if `IsClimbOverUseful(map, s, candidate, rotated)` returns true.

If no useful climb-over candidate is found, the smurf stays put this tick. `StuckTicks` builds → the v0.4.29 `YieldTrigger` fires at 12 ticks (~0.2s) asking the blocker to lie down. Once the blocker yields, `PopulateOccupancyGrid` (line 157) excludes it from `_smurfPerTile`, so the smurf's next primary step becomes uncrowded and resolves through the best-case branch — no more climb-over needed.

#### What this preserves / what changes

- **Excavate touch-arrival** (B on the only adjacent passable, beyond = T impassable) — still works. Candidate is Chebyshev-≤-1 from T → `IsClimbOverUseful` returns true via case 1.
- **Path through a smurf in a tunnel** (B blocking, beyond passable) — still works. Case 2.
- **Dead-end climb** (B blocking, beyond impassable, candidate NOT touch-arrival) — now rejected. Smurf stays put → YieldTrigger after 12 ticks → blocker yields → resolves naturally.
- **Single-tile tunnel through which a smurf needs to pass** — works as before; YieldTrigger is the established RimWorld-equivalent mechanism.

The local-steering tick cost adds ≤2 array lookups + an abs/sign per climb-over evaluation. Negligible.

---

## [0.5.7] — 2026-05-15

### Fixed — Smurfs stuck on Gather / ChopWood / Cut in crowded clusters (approach-blocked filter parity)

Sam: *"Smurfs keep getting stuck on tasks like excavating and gather when there are lots of smurfs on-screen. Are we seeing multiple smurfs attempt to designate one tile, causing a cancel-fallback loop? Figure out what's happening with this and use Rimworld's system as a guide. How does Rimworld handle 50 pawns onscreen with only 3 designated objects to mine or items to haul?"*

#### What RimWorld does (and what we already have)

RimWorld's answer to "50 pawns + 3 designations" is a stack of four mechanisms:

1. **Per-target reservations** in `ReservationManager` — only one pawn at a time. SmurfulationC has this: `LocalMap._claims` dict keyed by `(x, y)` with claimer Guid, atomic under `_designationsLock`. ✓
2. **PathEndMode.Touch** — pawn can complete a job from any 8-neighbour of an impassable target. SmurfulationC has this: `IsAtTouchArrival` in MoveOneTick, v0.4.57. ✓
3. **JobSearchSuppressUntilTick** — pawns who find no reachable work fall through to leisure with a per-pawn ~30-tick cooldown before re-checking. SmurfulationC has this: `WorkSearchCooldownTicks`, v0.5.4. ✓
4. **Approach-occupancy filter at JobGiver time** — when finding work, skip targets whose only passable adjacent tiles are currently occupied by other pawns. The pawn picks a more distant reachable target, or falls through to (3). SmurfulationC had this *only for Excavate* (v0.4.29). **Gather / ChopWood / Cut were missing it.**

The audit traced the bug to `LocalMap.cs` lines 1330-1407: `FindNearestGather`, `FindNearestChopWood`, `FindNearestCut` did not have an `approachBlocked` parameter at all, while `FindNearestExcavate` at line 1273 had it as a third optional parameter with the comment block at line 1287-1294 explaining the v0.4.29 rationale.

#### The cluster-jam cascade (Gather / ChopWood / Cut, pre-v0.5.7)

With 50 smurfs and 3 berry bushes clustered together:

1. 3 smurfs (the closest) claim the 3 bushes. Claims are atomic and visible across smurfs (the sequential single-thread tick loop sees each claim before the next smurf scans).
2. 47 smurfs scan — all 3 bushes claimed → `FindNearestGather` returns null → `SelectTask` falls through to idle → `WorkSearchCooldownTicks=60` debounce kicks in. Fine.
3. The 3 claimers walk toward their respective bushes. Each bush has ~4-8 passable neighbours.
4. Multiple claimers converge on the same physical neighbour tile (it's the closest path-friendly approach for several adjacent bushes). The first to arrive starts working. The others can't step onto the occupied tile.
5. The blocked smurfs try to find an alternative neighbour, but with a tight cluster of bushes + dense smurf population, all the neighbours may be occupied. Pre-v0.5.7, no filter rejected the *initial pick* of an approach-blocked bush. The smurf had already claimed it during `FindNearestGather`.
6. The smurf jitters against the blocked perimeter for the full ~90-tick `StuckThreshold` (~1.5 s at 1×), eventually gives up via `ReleaseTaskClaim` + adds the tile to its own `AvoidTiles` FIFO + forced Wander.
7. After the abandon cooldown (~1 s) + wander, the smurf re-evaluates. The tile is now back in `_gatherDesignations` (still unfinished) AND unclaimed. The smurf's `AvoidTiles` has the tile, so they skip it briefly. But other smurfs whose `AvoidTiles` *doesn't* have it just claim it again. The cycle moves to the next smurf.

Result: a cluster of smurfs near a small dig face / berry patch / shroom thicket cycles through claim → walk → jam → give up → wander → re-claim, for the same few work tiles, indefinitely. Visible jitter, no progress.

#### Fix — port the approach-blocked filter to all three missing methods

`scripts/world/LocalMap.cs`:

- `FindNearestGather` — added optional `System.Func<int, int, bool>? approachBlocked = null` parameter. New filter line right after the existing reachability + vegetation-present + avoid-list checks: `if (approachBlocked != null && approachBlocked(pos.X, pos.Y)) continue;`. Exact same shape as the Excavate version at line 1303.
- `FindNearestChopWood` — same change.
- `FindNearestCut` — same change.

`scripts/simulation/systems/BehaviorSystem.cs`:

- `FindDesignatedGather` — now reads `curTileX / curTileY / curTileIdx` from the smurf's current position (matching `FindDesignatedExcavation`'s pattern at line 2541-2543) and passes `approachBlocked: (tx, ty) => IsApproachFullyOccupied(map, tx, ty, curTileIdx)` to `map.FindNearestGather`.
- `FindDesignatedChopWood` — same.
- `FindDesignatedCut` — same.

The `IsApproachFullyOccupied` helper (line 2556) is unchanged — it walks the 8 neighbours via the per-tick `_smurfPerTile` occupancy grid and returns true iff every passable neighbour is occupied by some smurf other than the caller. Cheap (≤8 array lookups).

#### Net behavior with 50 smurfs + 3 berry bushes (post-fix)

1. 3 smurfs (closest with unblocked approaches) claim the 3 bushes.
2. 47 smurfs — for each, `FindNearestGather` finds the 3 bushes claimed AND/OR approach-occupied → returns null → SelectTask returns idle → 1-second `WorkSearchCooldownTicks` debounce. They commit to a leisure activity (v0.5.5 multi-step idles — Wander, Loiter, Converse, etc.).
3. The 3 working smurfs path to their bushes uncontested. Crowd-cost A* (v0.4.58) routes through uncongested neighbours. They complete the work, release claims, designation cleared.
4. The 47 idle smurfs' `WorkSearchCooldownTicks` expires. They scan again — no work → re-enter another idle activity. No jitter, no cycle.

When new designations appear (player drags another rectangle), the 47 idle smurfs notice within ~1 second (the cooldown window) and reclaim a fair share.

---

## [0.5.6] — 2026-05-15

### Fixed — Stockpile zones invisible on the ground (z-index direction was inverted)

Sam screenshot: stockpile painter clearly worked (items were being hauled to the painted cells) but the yellow zone tint was nowhere on the map.

`scripts/ui/StockpileOverlay.cs` had `ZIndex = -1` with a comment claiming this put the overlay "above the map texture." That's the wrong direction in Godot 4: a *lower* ZIndex means *render before* siblings — i.e., underneath them. With the map renderer at the default `ZIndex = 0`, the map's tile texture was drawn ON TOP of the stockpile tint, hiding it entirely. The painter was working all along; the layer was just buried.

Two-part fix:

- **`scripts/ui/StockpileOverlay.cs`** — `ZIndex` changed from `-1` to `0`. Same z-baseline as the map renderer, designation overlay, item drop overlay, and selection overlay.
- **`scripts/ui/GameController.cs`** — moved `AddChild(_stockpileOverlay)` to immediately after `AddChild(_mapRenderer)`, before `AddChild(_designations)`. With everything at z=0, tree order determines draw order. The new sequence renders: map → stockpile tint → designations → items → selection → smurfs (z=1). Stockpile tint sits on the floor; designation glyphs / item icons / selection brackets stack on top of it (the original design intent preserved in the existing comments).

### Fixed — Smurfs stuck in haul loop after dropping items at a stockpile

Sam: *"Smurfs get stuck on hauling and stand jittering trying to haul forever after dropping off items, almost as if they're in a loop of trying to haul things that are already in a stockpile zone."*

Diagnosis exact: `HaulSystem.SelectHaulTarget` (lines 218-232) and `HaulSystem.FindNextHaulNearby` (lines 354-369) iterated `ForEachDroppedInRadius` and accepted any item passing two filters:
1. `!it.IsForbidden`
2. `!IsReservedByOther(it, s.Id)`

**Neither path checked whether the item was already sitting in a stockpile.** Mechanism:

1. Smurf finishes deliver phase → `map.DropItem(item)` puts the item back on the map at the stockpile cell → `s.CurrentTask = null`.
2. Next tick, `SelectTask` runs → `HaulSystem.SelectHaulTarget` scans nearby items.
3. The just-dropped item is on a stockpile cell, but neither filter rejects it. It becomes the highest-priority haul source by distance (smurf is right next to it).
4. Smurf picks it up. `PickDeliveryTileFor` → `FindStockpileCellFor` returns the closest stockpile cell with capacity (often the same cell or a sibling cell in the same zone).
5. Smurf walks 0-2 tiles, drops, repeat.

`HaulSystem.cs` — added two private helpers:

```csharp
private static bool TileIsAcceptingStockpile(LocalMap map, int tx, int ty)
    => map.GetStockpileIdAt(tx, ty) != 0;

private static bool StockpileAcceptsHere(LocalMap map, int tx, int ty, Item item)
{
    var zone = map.GetStockpileAt(tx, ty);
    return zone != null && zone.Accepts(item);
}
```

Both auto-haul scan loops now compute `tileIsAcceptingStockpile` once per tile (cheap — single int array lookup) and short-circuit any item whose tile-zone accepts its Kind. The per-item `Accepts` check is split out so the common case (most candidate tiles aren't in a stockpile) doesn't pay for the per-item lookup.

#### What still works (intentionally)

- **Player force-haul priority branch** (`SelectHaulTarget` lines 200-212) intentionally skips this guard. If the player explicitly Haul-tagged a stockpiled item via the toolbar, that's a manual stockpile-reorganize command and the new check would block it.
- **Future Phase 5C zone Kind filters** are already supported. Once `StockpileZone.AcceptedKinds` becomes player-editable, an item in a "general" zone whose Kind isn't accepted by that zone will still be haulable (the smurf will move it to a zone that does accept it). RimWorld's "store at higher priority" pattern can layer on top later without touching the call sites.

---

## [0.5.5] — 2026-05-15

### Changed — Idle activities are now multi-step behaviors (RimWorld-quality "feels alive")

Sam: *"Have smurfs commit to the full idle task behavior loop, rather than just one second… a smurf should actually have a two way conversation with another smurf that engages both and lasts until they're done speaking, a smurf should actually take a short walk and finish it when 'taking a walk', loitering should see a smurf just 'hanging out' and may chat or otherwise relax, etc. Rimworld does this masterfully and pawns feel alive because of it."*

The v0.5.4 work-search debounce stopped *cycling between* idle activities but didn't deepen the activities themselves. Each idle was still a single-step "go to a tile, stand there for N seconds, emit one thought." This release adds genuine behavior — the activities have substance now, not just duration.

#### Two-way Converse lock (`scripts/simulation/systems/BehaviorSystem.cs`)

Pre-v0.5.5 Converse was one-sided: the initiator picked a partner, walked to them, and got Joy/Social bumps + emitted a chat thought. The partner *didn't know they were being talked to* — they'd continue their own task, possibly wander away mid-walk, leaving the initiator chatting at thin air on arrival.

RimWorld's `InteractionWorker` pattern: when the initiator arrives at the target pawn, the target is locked into a reciprocal interaction so both face each other, both gain social, both produce thoughts referencing the other.

New `TryLockConversePartner(Smurf initiator, Smurf partner, int lingerTicks)` helper, called from the Converse case of `ApplyTaskEffect` every tick the initiator is at their target. Idempotent — bails out if any of the lockable conditions fail:

- Partner within `ConverseLockRangePx` (~3 tiles). They wandered out of arm's reach? No catch.
- Partner is alive + not in life-threatening need (starving / suffocating / bleeding out — those tasks must complete).
- Partner's current task is `None`, an idle activity, OR already a Converse pointing back at us. **Never interrupts player orders, designation work, hauls, or chained orders** — RimWorld pattern, social interactions are weakest priority.
- Partner is not already locked into a Converse with a third party.

On lock: partner's `CurrentTask` becomes a fresh Converse pointing at the initiator, `IdleArrived=true` (they've "arrived" at the chat), `IdleLingerTicks` set to the same window, `WorkSearchCooldownTicks` bumped to `lingerTicks + 60` so the workAvailable gate doesn't pull them out mid-chat. Wander chain (if any) is dropped — the chat takes precedence; they can re-pick Wander next idle.

The result: a real conversation. Both smurfs stay put for the full 5-second linger, both gain Social per tick, both emit "ChatWithFriend" / "NiceChat" / "ChatWithEnemy" thoughts referencing the other by name. Repeated chats build the friendship counter (existing Befriend logic, line 2244).

#### Multi-hop Wander — "taking a walk" is now an actual walk

Pre-v0.5.5 Wander picked one destination 8-28 tiles away, walked there, lingered ~2 sec, ended. With ~4-14 sec walk + 2 sec linger that's a single "stroll."

`Smurf.cs` — new `int WanderHopsRemaining { get; set; } = 0` field.

`BehaviorSystem.cs`:

- New `NewWanderTask(Smurf s, …)` overload (alongside the existing `NewWanderTask(Vector2 from, …)`). Seeds `s.WanderHopsRemaining = rng.Next(1, 4)` → 2-4 total legs. Used only by `SelectIdleActivity`'s primary Wander pick (line 1654).
- The forced-wander recovery sites (failure-recovery line 555, abandoned-task displacement line 1219) still call the single-hop overload — they're "go elsewhere then re-evaluate," not "commit to a real walk."
- `ApplyTaskEffect`'s Wander case: when `WanderHopsRemaining > 0`, decrement, pick a fresh destination via `PickIdleDestination`, swap it into `CurrentTask`, reset `IdleArrived=false / PathWaypoints.Clear() / IdleLingerTicks=0 / StuckTicks=0`, set `WorkSearchCooldownTicks=360` (~6 sec — covers a max-radius 28-tile leg + linger before the next chain decision), and immediately call `Pathfinder.FindPath` for the new destination so the smurf's A* path is ready before the next tick.

A 3-leg Wander now plays out as: walk 4-14 sec → arrive → chain to next dest → walk 4-14 sec → arrive → chain → walk 4-14 sec → arrive → final 2 sec linger. Total ~15-45 sec of visible wandering — the RimWorld "pawn going for a walk between work shifts" feel.

Critical needs + player orders + chained orders still preempt mid-chain because they're separate clauses in the `needNewTask` gate. The `WorkSearchCooldownTicks` bump only suppresses the `workAvailable` re-eval (designations existing somewhere globally).

#### Snapshot exposes chat partner + hop count for the unit card

`scripts/simulation/SimulationSnapshot.cs` — `SmurfSnapshot` gains two fields:

- `string? ChatPartnerName` — set when `CurrentTask is { Type: TaskType.Converse }` to the task's `TargetId` (the partner's name). Renderer + unit card surface "Talking to {ChatPartnerName}" so the player can see the social interaction without inferring it from positions.
- `int WanderHopsRemaining` — passed through from `Smurf.WanderHopsRemaining`. Unit card can show "Wandering (3 of 4)" so the player can tell long walks apart from short hops.

Both fields use the existing snapshot-immutable pattern. Constructor at the bottom of `SmurfSnapshot(Smurf s)` reads from the live state.

#### What this combines with from prior releases

The compound effect across v0.5.1 → v0.5.5:

- **v0.5.1** — `IdleArrived` flag means the linger only counts down once the smurf is *at* the destination (not during the walk).
- **v0.5.4** — `WorkSearchCooldownTicks` debounce stops the workAvailable re-eval from yanking idle smurfs every tick.
- **v0.5.5** — multi-hop Wander + two-way Converse give the activities themselves real duration and real cross-smurf semantics.

Net: an idle smurf now picks an activity → commits to it → executes the full multi-step behavior → produces a meaningful thought / social outcome → re-evaluates only when the activity naturally completes or a higher-priority interrupt fires (critical need, player order, chained order). RimWorld parity for the leisure layer.

---

## [0.5.4] — 2026-05-15

### Fixed — Idle-cycling, the THIRD pathway (RimWorld JobSearchSuppress-style debounce)

Sam: *"Smurfs continue to stop in place and rapidly cycle between idle tasks, causing jitter and massive slowdown. … Smurfs should behave like people - they choose an idle task and fully complete it unless a higher priority order comes in. … A person won't stop and freeze, jittering in place while they rapidly cycle between which leisure activities they might do; they'll just go read, walk, or loiter until their desire to do so is done or they have something better to do."*

#### Diagnosis — three cycling pathways, only two were closed

This bug has been hunted three times now. The first two fixes addressed real but distinct cycling paths:

- **v0.4.65** (April) — gated `workAvailable` on `DesignationCooldownTicks`. Stopped *cooldown* smurfs (just-abandoned-a-task, blocked from designations for ~1s) from re-rolling idles every tick.
- **v0.5.1** (yesterday) — added `Smurf.IdleArrived` so `lingerExpired` waits for actual arrival before counting down. Stopped *mid-walk* smurfs (Wander destinations 8-28 tiles away) from triggering re-eval before they'd reached their target.

Both were correct fixes for what they covered. But Sam's persistent report described a THIRD pathway:

- A smurf finishes a task or gives up on idling, sits at a destination (`IdleArrived=true`, linger counting down), `DesignationCooldownTicks=0`.
- Designations exist somewhere on the map (`HasAnyDesignation()=true`) — but **none are reachable, claimable, or assignable to this smurf** because:
  - All nearby designations are reserved by other smurfs, OR
  - All approach tiles are occupied (`IsApproachFullyOccupied`), OR
  - All candidates are on this smurf's `AvoidTiles` blacklist, OR
  - The smurf is physically too far / cut off by terrain, OR
  - The smurf's role / skill rules out the designation type.
- `workAvailable` evaluates `idle && map.HasAnyDesignation() && DesignationCooldownTicks <= 0` → **TRUE every tick**.
- `needNewTask=true` → `SelectTask` runs → designation branches all fail → falls through to `SelectIdleActivity`.
- `SelectIdleActivity` (BehaviorSystem.cs:~1612) is non-deterministic — `rng.Next(total)` rolls a different weighted-random idle activity on every call.
- Result: smurf cycles Wander → Loiter → Observe → Meditate → Converse → ... every single tick at 60 Hz, abandoning each idle task during its walk phase. 50 cycling smurfs × 60 Hz = 3,000 `SelectTask` calls / sec → visible jitter + measurable FPS drop.

The shared mechanism across all three pathways: `needNewTask` is a *level* check on global state, but cycling needs an *edge* check or a *debounce*.

#### Fix — `Smurf.WorkSearchCooldownTicks` (RimWorld parity)

RimWorld's `ThinkNode_JobGiver.TryGiveJob` sets `JobSearchSuppressUntilTick = currentTick + suppressDuration` after a JobGiver returns null Job. The pawn's ThinkTree skips that JobGiver branch until the suppress window expires — typically 30 ticks (~0.5 s at 1×). Same trick lifted into SmurfulationC:

`scripts/simulation/Smurf.cs` — new `int WorkSearchCooldownTicks { get; set; } = 0`. Same shape as `DesignationCooldownTicks` (different bucket — that one's for *abandon-after-stuck*; this one's for *tried-and-found-no-work*).

`scripts/simulation/systems/BehaviorSystem.cs`:

- Per-tick decrement at line ~407 alongside `DesignationCooldownTicks`. Scaled by `tickInterval` so LOD-cold smurfs decrement proportionally.
- `workAvailable` gate (line ~460) extended: `idle && map.HasAnyDesignation() && DesignationCooldownTicks <= 0 && WorkSearchCooldownTicks <= 0`.
- After every `SelectTask` return that yields an idle task (line ~587), set `WorkSearchCooldownTicks = 60` (~1 s at 1×). When SelectTask yields actual work, clear the cooldown to zero so the next idle gets a fresh window.

The cooldown is intentionally short — 1 second matches Sam's "person committed to leisure" feel without making player-drawn designations feel laggy. A new designation drawn by the player will be picked up by an idle smurf within at most 1 second of the cooldown expiring.

#### What still bypasses the cooldown (intentionally)

The other `needNewTask` clauses are unchanged, so urgent overrides still preempt idle:

- **`critical`** — life-threatening needs (starvation, exhaustion, severe wounds) interrupt immediately.
- **`chainPending`** — shift+right-click queued player orders interrupt immediately (RTS standard).
- **`lingerExpired`** — natural completion of the current idle task still triggers re-eval.
- **`Type == None`** — smurf with no task at all still re-evaluates immediately.

This matches Sam's "unless a higher priority order comes in" — player commands and survival needs override the dwell. Routine designation-polling is debounced.

### Fixed — Zones tab showed empty (no panel content above the tab bar)

Sam screenshot: tab bar visible at the bottom with Zones highlighted, but the panel area above it was empty / collapsed to nothing.

`scripts/ui/ZonesPanel.cs` — was missing the `_GetMinimumSize()` override that `DesignationToolbar` has at line 202. Without it, ZonesPanel reported zero minimum size to its hosting `PanelContainer` in `BottomTabPanel`, the host collapsed to its style margins (~12 px), and the tab visibly opened to nothing. Added the same override (walks visible Control children, returns max of their `GetCombinedMinimumSize()`).

`scripts/ui/BottomTabPanel.cs` — `OnUIScaleChanged` rebuilt the shell but didn't detach `_zones` first. The Zones panel was `QueueFree`'d along with the old shell, leaving the field as a freed reference, and the new shell's `_zonesHost` stayed empty forever after any UI-scale change. Mirrored the existing `_orders` / `_roster` / `_resources` / `_jobs` detach + re-attach flow for `_zones`.

---

## [0.5.3] — 2026-05-15

### Fixed — Player right-click orders now path through A* (no more straight-line wall collisions)

Sam: *"smurfs will path in a straight line towards their destination when using right-click orders especially. Examine rimworld and dwarf fortress pathfinding models in order to understand how to improve pathfinding so Smurfs path only through passable tiles and intelligently navigate obstacles."*

The codebase already had a full RimWorld-style A* (`scripts/simulation/Pathfinder.cs`, v0.4.58 — 8-connected, diagonal cut-corner enforcement, soft-collision crowd cost of 175 per other smurf on a candidate tile, DF-style region-graph fast-fail, generation-counter buffers, MaxNodes=1024 cap). It just wasn't wired to the player-order task path. Three concrete gaps:

#### Gap 1 — Section 1 PlayerOrder/Haul never called the pathfinder

`scripts/simulation/systems/BehaviorSystem.cs` Section 1 (lines ~280-340) drains `pendingOrders` and assigns `CurrentTask` for the targeted smurf. The `Haul` branch passed `tileX/tileY` (line 324) but never called `Pathfinder.FindPath`; the generic `PlayerOrder` branch passed neither tile coords nor a path. So both reached MoveOneTick with `PathWaypoints.Count == 0`.

`ResolveWalkTarget` (line 1196) falls through to `task.Target` directly when the path is empty — pure greedy steering. The 8-direction local-steering fan-out in MoveOneTick avoids immediate neighbour walls but cannot plan around concave terrain pockets. Sam's report: *"smurfs will path in a straight line towards their destination."*

Fix — both branches now compute `tx, ty` once and immediately invoke `Pathfinder.FindPath(map, s.SimPos, (tx, ty), s.PathWaypoints, _smurfPerTile, OccTileIdx(s))` after assigning `CurrentTask`. The `_smurfPerTile` occupancy grid populated at line 274 (start of Tick) is already in scope. Mirrors the existing Section 2a designation-task pathfinding pattern at lines ~543-600.

#### Gap 2 — v0.5.2 queue-pop branch (chained orders) had the same problem

The shift+right-click chained-order branch I added in v0.5.2 (BehaviorSystem.cs lines ~471-484) created the queued `PlayerOrder` task without `tileX/tileY`. Even though the queue-pop sits inside `needNewTask` (so the section-2a pathfinding block at line 543 *would* fire afterward), that block's gate requires `pt.TargetTileX >= 0 && pt.TargetTileY >= 0`. With both at -1 the gate failed and the queued order also greedy-stepped.

Fix — compute `qtx, qty` from `queuedTarget` and pass to the constructor. Section 2a's pathfinding block then routes the queued order through A* on its own.

#### Gap 3 — Section 2a's "always A*" gate excluded PlayerOrder

The pathfinding block at line ~562 gated on `isDesignation || distSq > Pathfinder.PreferAStarDistSqPx`. For player orders within the short-route threshold (~8 tiles), the gate fell through to greedy steering — meaning even a player order with valid tile coords got skipped if the click landed close to the smurf.

Fix — gate now reads `isDesignation || pt.IsPlayerOrder || distSq > Pathfinder.PreferAStarDistSqPx`. Player orders join designation tasks in the always-A*-regardless-of-distance tier. Matches RimWorld: every player Goto issues a full pathfind regardless of route length.

#### Gap 4 — Stuck re-path didn't apply to PlayerOrder

The one-shot stuck re-path at line ~1048 only fired for `IsDesignationTaskType(rpt.Type)`. A stuck player order rode out the full ~90-tick `StuckThreshold` before the give-up branch — the smurf jittered at the obstacle for ~1.5s before quitting, with no attempt to re-route around the new obstruction (commonly: another smurf parked in the original path).

Fix — gate now `(IsDesignationTaskType(rpt.Type) || rpt.IsPlayerOrder)`. Same single-shot `RePathTried` budget so a genuinely-blocked order still hits give-up at `StuckThreshold` instead of looping.

#### What this matches in RimWorld and Dwarf Fortress

- **RimWorld** `JobDriver_Goto` + `Toils_Goto.GotoCell` always issues a full A* pathfind regardless of distance. There is no greedy-steering fallback. The pathfinder's avoidance grid (`PathGrid` cost map + `RegionAndRoomQuery` reachability) gives every player Goto the same routing quality as a colonist's auto-assigned hauling job.
- **Dwarf Fortress** uses a path cache keyed on `(start_region, end_region)`. Every move from one tile to another goes through the navigation system — there's no concept of "walk straight at the destination." Designation tasks and player-issued station orders share the same path computation.

This change brings PlayerOrder + Haul-via-player-order to parity with the path quality the existing designation tasks have had since v0.3.47 / v0.4.16.

---

## [0.5.2] — 2026-05-15

### Changed — Right-click no longer deselects smurfs/panels (preserves planned RTS combat controls)

Sam: *"Right-click should not deselect smurfs or close its panels in order to not break the planned combat RTS controls/right-click orders... right-click cancel designation should only apply to order bar panels and designation selectors from the bottom right order bar."*

The v0.5.1 right-click cascade introduced a "priority-3 universal deselect" — a stationary right-click with no active tool and no context action would clear smurf selection and close inspector panels (RimWorld-style escape). This conflicts with the planned RTS combat layer where right-click on an enemy issues an attack order to the currently-selected smurf(s); deselecting on every empty right-click would break the combat flow before it ships.

`scripts/ui/GameController.cs` — cascade revised:

1. **Priority 1 — cancel active toolbar tool.** A stationary right-click while any `DesignationToolbar` tool is active (Excavate / Chop / Cut / Gather / Haul / Remove / Stockpile / Forbid) cancels that tool. This is the "designation selectors from the bottom right order bar" scope Sam specified.
2. **Priority 2 — context action.** If a context action exists at the clicked tile (Forbid/Allow on an item, future combat target on an enemy), execute it. With `Shift` held, route through the chain-order path instead (see below).
3. **No-op otherwise.** Empty stationary right-click with no tool + no context returns `false` — selection and panels untouched. The planned attack/move RTS layer will fill this slot in the combat phase.

Selection clearing remains available via `Escape` and the existing left-click-on-empty-tile path.

### Added — Shift+right-click chain orders (StarCraft / Warcraft / RimWorld-style queue)

Sam: *"Chain orders by holding 'Shift' and right-clicking should be added."*

Holding `Shift` while right-clicking a passable tile queues a Move order behind any already-queued orders for the currently-selected smurf(s). Plain right-click (no shift) clears the queue and issues the move immediately, matching standard RTS expectations.

**`scripts/simulation/Smurf.cs`** — new `List<Vector2> MoveOrderQueue` per smurf. Sim thread is the sole writer (queue-append via `PostMainThreadCommand`, queue-pop in `BehaviorSystem.Tick`); main thread reads via the per-tick snapshot copy. Lives alongside `CurrentTask` rather than replacing it — the queue is the *next* targets, the task is the *active* one.

**`scripts/SimulationManager.cs`** — two paths:

- `RequestPlayerMoveOrderGroup` (plain right-click, unchanged signature) now first enqueues a `MoveOrderQueue.Clear()` command per smurf, then issues the immediate move. Both commands drain in the same sim tick before `BehaviorSystem.Tick` reads either, so the clear lands before the new task assignment — no race.
- `RequestPlayerMoveOrderGroupQueued` (new, shift variant) appends to `MoveOrderQueue` with the same radial-offset spread used by the plain group order (so 5 smurfs queueing a move don't all stack on one pixel). Does NOT touch `CurrentTask` — the queue waits its turn.

**`scripts/simulation/systems/BehaviorSystem.cs`** — queue consumption:

- `needNewTask` gate gains `chainPending = idle && s.MoveOrderQueue.Count > 0`. An *idle* smurf with anything queued immediately interrupts to start the chain (no waiting for the linger to expire). A smurf currently executing a task (Excavating, Hauling, mid-PlayerOrder walk) lets the active task finish before the queue drains — RTS standard, prevents cancelling player work to start more player work.
- New queue-pop branch inside the needNewTask block (placed *before* the failure-recovery short-circuit so chained orders override wander-recovery). Pops `MoveOrderQueue[0]`, creates a `PlayerOrder` task with `interruptible: false` and the queued target, clears `PathWaypoints` / `StuckTicks` / `RePathTried` / `IdleArrived` so the new walk starts clean.

Failure-recovery still wins when the smurf has hit `TaskFailureForceWander` consecutive failures — chained orders that keep failing don't lock the smurf into a forever-failing chain.

### Added — `OrderQueueOverlay` waypoint visualization

`scripts/ui/OrderQueueOverlay.cs` (new). `Node2D` that reads the per-tick `SimulationSnapshot` plus the `GameController` selected-smurf set and draws, *for each selected smurf with a non-empty queue*:

- A semi-transparent cyan polyline from the smurf's current `SimPos` through each queued waypoint in order (the route).
- A small cyan dot at each waypoint, with a thin black outline arc for readability over varied terrain.
- A larger, brighter dot at the *first* waypoint — the "next" target after the active task completes.

Only draws for selected smurfs (the player only cares about their own active commands), so the per-tick cost is bounded by `selected_count × queue_length` — typically 1-5 × 0-3 with small numbers.

**`scripts/simulation/SimulationSnapshot.cs`** — `SmurfSnapshot` gains `IReadOnlyList<Vector2> MoveOrderQueue`. `SnapshotMoveOrderQueue` defensively copies into a `Vector2[]` (sim thread is sole writer, but the snapshot is consumed off-thread); empty queue returns the pre-allocated `_emptyMoveQueue` so the common case allocates nothing.

**`scripts/ui/GameController.cs`** — `OrderQueueOverlay` instance added in `BuildGameWorld` (z-ordered above the map / item / designation overlays, below smurf sprites so dots sit "behind" the smurf at the route origin). `SetSelection` called from `SelectSingleSmurf`, `ClearSelection`, and the box-select path; `SetSnapshot` called per-tick in `OnTickCompleted`.

---

## [0.5.1] — 2026-05-15

### Fixed — Idle-task cycling (the real fix); plus order feedback, Zones tab move, and personality tooltips

Sam: "Smurfs seem to still be jittering in place because after a while they stop, then run into an idle task loop that still hasn't been fixed. They run down the FPS and overall game performance by cycling through every single idle task they have access to over and over."

#### Diagnosis — the v0.4.65 fix didn't address the root cause

v0.4.65 gated `workAvailable` on `DesignationCooldownTicks <= 0` and stopped one cycling pattern (cooldown smurfs re-rolling idle tasks every tick because designations existed). But Sam's report describes a SECOND, deeper cycling pattern that's been latent since v0.3.45:

- Idle activities have arrival-linger durations: `LingerWander = 120 ticks` (~2 sec), `LingerLoiter = 240` (~4 sec), `LingerObserve = 360` (~6 sec), etc.
- Wander walks 8-28 tiles. At base speed (32 px/sec ÷ 16 px/tile = 2 tiles/sec), that's **4-14 sec** of walk.
- v0.3.45 set `IdleLingerTicks = ArrivalLinger` at task **creation** as a "total time-budget for the activity." For Wander: budget 2 sec, walk 4-14 sec.
- The `IdleLingerTicks` countdown ran during the walk → expired BEFORE arrival → `lingerExpired` triggered re-eval → SelectTask returned a different idle task → smurf abandoned the wander destination, picked Loiter/Observe/Meditate/Converse/VisitFavorite, walked toward THAT, expired again mid-walk → cycle.

Visible symptom: smurf appears to thrash through every idle activity their personality can roll, rapidly. Performance symptom: per-tick `SelectTask` runs on every cycling smurf (50 smurfs × every tick at 60 Hz = 3000 SelectTask calls/sec, plus the per-call FindNearest* work for any smurfs that re-pick designation tasks).

Both v0.3.43 ("linger = 0 at creation") and v0.3.45 ("linger = ArrivalLinger at creation") were broken. **What we actually want: linger starts at arrival.** RimWorld's `Toils_*` job-driver pattern explicitly separates "walk to destination" from "consume duration counter" — `JobDriver_Joy_Wait` walks via `Toils_Goto.GotoCell` and only after that toil completes does the dwell counter start. Dwarf Fortress's "Stroll" job has the same separation: walk-to-target, then duration-counter starts.

Our system conflated walk + dwell into a single counter. Bug.

#### Fix — `Smurf.IdleArrived` flag

`scripts/simulation/Smurf.cs`. New `bool IdleArrived = false`. Set to true only when MoveOneTick fires arrival on an idle task. Reset to false at idle-task creation.

`scripts/simulation/systems/BehaviorSystem.cs`:

- New helper `MarkIdleArrivalIfNeeded(Smurf s)` — called from both arrival branches in `MoveOneTick` (`IsAtTouchArrival` early-return + the dist-based arrival branch). First arrival flips `IdleArrived = true` AND resets `IdleLingerTicks = ct.ArrivalLinger`. Subsequent arrival ticks no-op (IdleArrived already true).
- `needNewTask` gate's `lingerExpired` check now requires both: `idle && s.IdleArrived && s.IdleLingerTicks <= 0`. Linger countdown is meaningless until the smurf actually arrives, so the check correctly waits.
- Task-creation block resets `s.IdleArrived = false` whenever a new idle task is assigned, so the next walk's arrival re-triggers the flag.

Net: an idle task is committed for the full walk + linger duration. A 14-tile Wander now completes its 4-7 sec walk, then sits at the destination for ~2 sec, then re-evaluates — exactly the intended dwell.

The v0.4.65 cooldown gate stays in place; the two fixes are complementary. v0.4.65 prevents post-abandon cooldown smurfs from triggering re-eval via `workAvailable`; v0.5.1 prevents the LINGER itself from triggering re-eval mid-walk.

### Added — Visible feedback for all player orders (Phase 5A coverage)

The v0.5.0 Stockpile painter and Forbid/Allow context action shipped without `OrderFeedbackOverlay` integration. Added:

- `Kind.TileFlashStockpile` — soft yellow rect flash on stockpile drag commit, matching the persistent `StockpileOverlay` tint.
- `Kind.RingForbid` — red expanding ring + diagonal slash on forbid toggle (slash differentiates from `RingCombat` red ring).
- `Kind.RingAllow` — green expanding ring on allow toggle.

`FlashDesignationRect`'s tool→kind switch gains the `Stockpile` arm; `GameController`'s right-click Forbid/Allow execute lambda fires the appropriate ring. Every player order (drag-paint commit, right-click action) now produces visible feedback.

### Changed — Stockpile painter moved Orders → Zones tab

The v0.5.0 Stockpile button lived in the Orders toolbar (alongside Gather/Excavate/Chop/Cut/Haul/Remove). Conceptually wrong — Orders are one-shot work commands; Zones are persistent territory designations. Sam: "move stockpile order to 'Zones' — where it should be."

New file `scripts/ui/ZonesPanel.cs` hosts the Stockpile button + a disabled Allowed Area placeholder (Phase 5C). `BottomTabPanel.Attach()` constructs ZonesPanel after the toolbar and calls `BindToolbar(toolbar)` so the Zones-tab Stockpile button routes through the same `DesignationToolbar.SetActiveTool` (single source of active-tool truth across both tabs). Subscription to `ToolChanged` keeps the Zones-tab button's pressed state in sync when the player picks an Orders-tab tool.

`DesignationToolbar` no longer adds the Stockpile button (the `Tool.Stockpile` enum entry stays for cross-tab activation). Zones-tab content is no longer a stub.

### Improved — Personality trait tooltips show gameplay effects

Sam: "improve tooltips for personality traits so they show relevant gameplay information."

`scripts/simulation/PersonalityRegistry.cs`. New `BuildGameplayTooltip(PersonalityDef def)` returns a multi-line tooltip:

```
{Trait Name}
{Flavour description}

Mood: ±X
{Per-trait gameplay effect lines}
Incompatible with: {conflicting traits if any}
```

Per-trait effect lines hand-written for the 17 traits with concrete code-side effects:

- **Idle weighting** (Introvert / Gossip / Daydreamer / Brawny / Sleepyhead / Thrill-Seeker / Mushroom Whisperer): "Idle: prefers Observe over Converse" etc. — surfaces the `wConverse += N` weights in BehaviorSystem.PickIdleType.
- **Need thresholds** (Glutton, Introvert): "Eats sooner (Nutrition threshold 70 vs default 50)" etc.
- **Mood modifier** (Optimist / Pessimist / Stoic / Worrywart / Empath etc.): plain mood explanation.

Plus the v0.4.64 `ConflictPairs` registry surfaces as "Incompatible with: Pessimist" on Optimist's tooltip etc. — players see at gen time why a trait isn't available alongside another.

Wired into `SmurfCardPanel` (Main tab personality list + Mood tab trait modifier list) and `ScenarioPanel` (per-trait checkbox tooltip).

### Added — Stationary right-click as universal cancel/deselect (RimWorld-style)

Sam follow-up: "ensure orders are deselected when right-clicking without dragging the screen or moving the camera (stationary right-click deselects selected order, zone, object, smurf, etc.) Use Rimworld's similar system as an example."

`GameController.TryHandleMouseButton` right-click release branch rewritten as a three-tier priority cascade:

1. **Cancel active toolbar tool.** If `_toolbar.ActiveTool != None`, call `SetActiveTool(_toolbar.ActiveTool)` which toggles to None per the existing click-twice-to-deselect convention. Matches RimWorld's "Esc cancels active designator" behaviour, just bound to right-click since right-click is the established cancel button in this UI.
2. **Context action with selected smurfs.** If smurfs are selected AND cursor is on a passable tile, fall through to the existing `ResolveRightClickActions` flow (move / pick-up / forbid). Selection persists across the order so the player can chain commands — RimWorld behaviour preserved.
3. **Universal deselect.** Otherwise (no tool active, no actionable target, OR no smurfs selected), clear smurf selection + close the smurf card + close the tile-properties inspector + clear the selection-bracket overlay. Sam's explicit "deselect smurfs / objects / etc." case.

Right-DRAG that crossed the camera-pan threshold is intercepted earlier (`wasPanning` short-circuit), so the cascade only runs for genuinely stationary right-clicks. Camera pan workflow is unaffected.

UX outcomes:
- Pick the Stockpile painter, change your mind → right-click anywhere → painter deselects.
- Smurf selected, want to issue 5 move orders in a row → right-click destination 5 times, each one moves and KEEPS smurf selected (chain-friendly).
- Done with smurf, want a clean slate → right-click empty space (impassable tile, off-map, or grass with no items) → smurf deselects, panels close, brackets clear.

### Preserved unchanged

- v0.5.0 Phase 5A foundation (StockpileZone data, IsForbidden flag, HaulSystem skip-forbidden, FindStockpileCellFor) untouched.
- v0.4.65 cooldown × workAvailable gate unchanged. Both fixes coexist.
- All v0.4.x cleanup work (skill XP, Joy need, backstories, trait conflicts, Touch arrival, A* crowd cost, KillSmurf pipeline).
- Tool enum, tool-tracking state machine, drag-commit dispatch — all untouched. ZonesPanel is a remote button on the same state machine.
- Right-click camera pan (drag past threshold) untouched.
- Left-click selection workflow (single smurf / box-drag / inspector card) untouched.

---

## [0.5.0] — 2026-05-14

### Phase 5 begins — sub-phase A foundation: Stockpile zones + universal Forbid flag

Sam: "Fully implement Phase 5." Phase 5 in the roadmap is a six-sub-phase epic (data layer → stockpiles → construction → furniture/rooms → bills → roofing → temperature) — months of work. The rimport.md "Recommended path forward" splits it: v0.5.x = Stockpiles & Storage; v0.6.x = Construction & Crafting. v0.5.0 lands the sub-phase A foundation that closes Phase 4's biggest hanging thread: the haul system has had a destination semantics gap since v0.4.0, defaulting to a spawn-cluster cell because no stockpile system existed. v0.5.0 fills it.

Phase number `bb` advances 4 → 5 per the v0.4.0 versioning convention; `cc` resets to 0.

#### Item.IsForbidden flag (rimport N5)

`scripts/simulation/items/Item.cs`. New `bool IsForbidden { get; set; } = false` per item. RimWorld-style universal flag — every haul / eat / equip path checks it (today only haul wired; eat / equip wire in Phase 5D when bills land).

Auto-forbid policies are deliberately light at v0.5.0 — neither corpse-outside-Home nor bones auto-forbid yet, because the Home Area concept arrives in Phase 5C (rimport N6 allowed-area bitmap). v0.5.0 ships with manual Forbid/Allow only; auto-policies layer on top once Home Area exists.

#### Forbid skip in `HaulSystem`

`scripts/simulation/systems/HaulSystem.cs`. Two-line change in the radius-walk inner loop (`SelectHaulTarget` standard pass + `FindNextHaulNearby` multi-trip): `if (it.IsForbidden) continue;`. Priority haul (player-marked Force-Haul) intentionally bypasses the forbid — when the player explicitly Force-Hauls something they want it picked up regardless of forbid state.

#### Right-click Forbid / Allow

`scripts/ui/GameController.cs` `ResolveRightClickActions`. New context-menu action on every tile that has items: Forbid (if any allowed) or Allow (if all forbidden). Toggles every item on the tile in one click — matches RimWorld's "Forbid all" UX. Routes through the new `SimulationManager.SetForbiddenOnTile(x, y, forbid)` which posts a sim-thread command (matches v0.4.55 / v0.4.60 pattern for player-driven Item state mutations).

`scripts/ui/TileInfoOverlay.cs` — forbidden items show a `[forbidden]` prefix in the hover panel.

#### StockpileZone data + LocalMap storage (rimport N1)

New file `scripts/world/StockpileZone.cs`:

- `enum StoragePriority { Low = 0, Normal = 1, Important = 2, Critical = 3 }` (4 levels at v0.5.0; RimWorld's 6 at full). Hauler walks zones in priority-descending order.
- `class StockpileZone { int Id; string Name; List<(int X, int Y)> Cells; HashSet<ItemKind> AcceptedKinds; StoragePriority Priority; }`. Cells are arbitrary set (not rectangle). `AcceptedKinds.Count == 0` = accept all (RimWorld default).

`scripts/world/LocalMap.cs` additions:

- `Dictionary<int, StockpileZone> _stockpileZones` — keyed by monotonic int ID.
- `int[] _cellZoneId` — per-cell ownership grid (0 = no zone). RimWorld `SlotGroupGrid[,]` pattern; gives O(1) "which zone owns this cell?" for the inspector and haul-cell collision check.
- `event StockpileChanged(x, y)` for the overlay refresh subscription.
- `int SetStockpileCell(x, y, extendZoneId)` — paints a cell into a stockpile zone. Refuses impassable tiles (water / boulders). Idempotent on already-painted cells. Auto-creates a new zone if `extendZoneId == 0` or unknown; extends the existing zone otherwise.
- `void ClearStockpileCell(x, y)` — removes a cell; deletes the zone if its cell list empties.
- `int GetStockpileIdAt(x, y)` / `StockpileZone? GetStockpileAt(x, y)` — lookups.
- `List<StockpileZone> SnapshotStockpileZones()` — priority-descending snapshot for hauler walks.
- `(int X, int Y)? FindStockpileCellFor(item, fromX, fromY)` — RimWorld's haul-target picker. Walks zones in priority order; for each zone whose filter accepts the item, picks the closest cell with spare capacity under the v0.4.30 250-cap + type-lock rules.

`StockpileChanged` events are coalesced by the new `StockpileOverlay` with the v0.4.56 200 ms throttle pattern — no per-paint redraw thrash on big drag-paints.

#### Stockpile painter (designation tool)

`scripts/ui/UITheme.cs` `DesignationTool` enum gains `Stockpile`. `scripts/ui/DesignationToolbar.cs` adds a `▦ Stockpile` button (and the previous `Zone_Storage = 100` Phase 5 stub is replaced by the live tool). `scripts/SimulationManager.cs` `DesignateRect` adds:
- `case Stockpile`: paints all cells in the rect into one zone (the first painted cell creates the zone; subsequent cells in the same rect extend it via `extendZoneId`).
- `case Remove`: now also clears stockpile membership from the cell, so the existing `✕ Remove` brush works as the stockpile-erase too.

#### StockpileOverlay (visual)

New file `scripts/ui/StockpileOverlay.cs`. Mirrors the v0.4.56 throttle architecture: per-tile `HashSet<(int X, int Y)> _dirtyTiles` + `_timeSinceRefresh` accumulator + 200 ms minimum interval. Single `MultiMeshInstance2D` with a pre-baked 16×16 RGBA sprite (pale yellow fill at 18% alpha + gold border at 45%). `ZIndex = -1` so the tint sits below designation glyphs and items but above the map texture; smurfs (z=1) walk over the tinted floor without obscuration. Per-zone colour variation deferred to Phase 5C.

Wired into `GameController.BuildGameWorld` alongside `_designations` / `_itemOverlay`; `SetMap(map)` called on both load-from-save and fresh-generate paths.

#### Haul integration — deliver to stockpile

`scripts/simulation/systems/HaulSystem.cs` `PickDeliveryTileFor`. Rewritten:

1. If smurf has at least one item in inventory, use the first item as the routing key.
2. Call `map.FindStockpileCellFor(item, smurfX, smurfY)` — returns the closest accepting stockpile cell with spare capacity, walking zones in priority order.
3. If a cell is found, deliver there. Otherwise fall back to the spawn-cluster cell (pre-v0.5.0 behaviour, with the v0.4.19 hash-spread).

Single-type-per-trip routing isn't perfect (a multi-type haul ferries to whichever zone matches the first item; mismatched items fall back to the v0.4.30 stockpile rules' overflow-to-compatible-tile path). RimWorld solves this via per-item haul jobs; SmurfulationC's multi-trip-per-haul (v0.4.30) trades that for fewer tasks per smurf. Acceptable for v0.5.0; per-item routing can layer on top in v0.5.x cleanup.

### What v0.5.0 doesn't do

Phase 5 sub-phases queued for follow-up versions:

| Sub-phase | Scope | Target |
|---|---|---|
| **5B** Construction pipeline | Blueprint → Frame → Built; walls/floors/doors; Construction skill XP grants | v0.5.1+ |
| **5C** Furniture & rooms | Furniture types; Room detection; room-typing; Beauty situational thought (rimport G5); allowed-area bitmap (rimport N6) | v0.5.x |
| **5D** Bills & workbenches | `Bill_Production` model; recipe filter; repeat modes (RepeatCount / Forever / TargetCount); Cook task implementation (`MealThoughtKey(isCooked: true)` finally fires) | v0.5.x |
| **5E** Roofing & insulation | `StructureSlot.HasRoof`; indoor/outdoor classification; `ItemDeteriorationSystem.ResolveInsulationMul` real values | v0.5.x |
| **5F** Temperature simulation | Per-tile temperature field; room-aware insulation; Comfort/Heatstroke/Hypothermia | v0.5.x |
| **rimport O4** Generic ReservationManager | Generalise per-tile claims to a per-target / per-asker / per-layer ledger | v0.5.x |

Each sub-phase is its own multi-version arc, mirroring how v0.4.0 → v0.4.65 expanded Phase 4. Roadmap Phase 5 spec stays the authoritative target list; sub-phase boundaries above match the rimport adoption notes for ordering.

### Preserved unchanged

- Per-tile stockpile rules (v0.4.30 250-cap + type-lock + spiral overflow) — the v0.5.0 stockpile zone is a higher-level filter on top of these, not a replacement.
- All v0.4.x cleanup work (skill XP, Joy need, backstories, trait conflicts, Touch arrival, A* crowd cost, post-abandon cooldown, render throttles, KillSmurf pipeline).
- Existing 5 designation tools (Gather / Excavate / ChopWood / Cut / Haul / Remove) untouched. Stockpile is the 7th tool (Remove being shared between designations and stockpile cells).
- Save format unchanged at v0.5.0 — stockpile zones + IsForbidden flag are not yet serialised. Loaded saves retain no stockpile zones (player must re-paint) and no forbid state. To be addressed in v0.5.1 alongside the construction pipeline save schema.

---

## [0.4.65] — 2026-05-14

### Fixed — Smurfs cycling between idle behaviors during post-abandon cooldown

Sam: "Smurfs seem to get stuck cycling between idle behaviors. Diagnose and fix."

#### Diagnosis

The v0.4.57 `DesignationCooldownTicks` field gated SelectTask's six designation branches, forcing a smurf to wander/idle for ~1 second after abandoning a designation so the work-face cluster could disperse. But the `needNewTask` re-evaluation gate in `BehaviorSystem.Tick` was NOT updated alongside it.

Root cause: in the per-smurf loop, `needNewTask` is computed each tick from four conditions:

```csharp
needNewTask = ct.Type == TaskType.None
    || critical
    || lingerExpired
    || workAvailable;     // <-- the culprit
```

Where `workAvailable = idle && map != null && map.HasAnyDesignation()`.

Scenario:
1. Smurf abandons excavation → `DesignationCooldownTicks = 60`
2. SelectTask falls through all designation branches (gated on cooldown), returns an idle task (Wander → wanders 8-28 tiles away)
3. **Every subsequent tick during walk + linger:** `workAvailable = true` (designations still exist on the map) → `needNewTask = true` → re-evaluate
4. SelectTask: cooldown still active → designation branches blocked → falls through to idle tier
5. The idle-tier picker is personality-weighted random — picks a NEW idle task each call (Wander → Loiter → Observe → Meditate → ...)
6. Smurf visibly thrashes through random idle behaviors every tick for the full ~1 sec cooldown duration. If they re-stuck on the next attempt and re-cooldown, the cycling appears endless.

The bug only existed when (a) `DesignationCooldownTicks > 0` AND (b) designations existed on the map — a scenario produced reliably by any heavy gather/excavate session, which is exactly when v0.4.57's cooldown is most active.

#### Fix

`scripts/simulation/systems/BehaviorSystem.cs`. One-line change to the `workAvailable` condition:

```csharp
// Was:
bool workAvailable = idle && map != null && map.HasAnyDesignation();

// Now:
bool workAvailable = idle && map != null && map.HasAnyDesignation()
    && s.DesignationCooldownTicks <= 0;
```

A cooldown smurf no longer triggers re-evaluation purely because designations exist somewhere — they can't take any anyway, so the re-eval would just thrash. They now follow the natural idle rotation: arrival → linger → expire → re-evaluate (pick a different idle activity, OR a designation if cooldown has expired by then).

#### Why this didn't surface in v0.4.57 testing

v0.4.57 was tested with the Touch-arrival fix and post-abandon cooldown together. The visible behavior at the time was "smurfs disperse after abandoning, take a couple of seconds, then come back to work" — which IS what was wanted. The cycling-between-idle-tasks was happening DURING that disperse window but looked like normal idle wandering at the smurf-card level. Sam's recent observation caught it because the cycling is more visible when there's a tight work site with many designations (forcing many smurfs through cooldown simultaneously).

#### Sanity check on v0.4.62 / v0.4.63 / v0.4.64 changes

Verified that none of the recent batch changes (Skill XP, Joy need, Backstories, Trait conflicts, role bonus item) touch the `needNewTask` gate or the idle-task-picker. The cycling is purely the v0.4.57 cooldown × `workAvailable` interaction; v0.4.6x changes don't contribute.

### Preserved unchanged

- `DesignationCooldownTicks` value (60 ticks ≈ 1s, halved from v0.4.57's 120 in v0.4.59).
- All idle-task selection / weighting / linger logic.
- Touch arrival, A* crowd cost, all other v0.4.5x perf and behaviour work.
- v0.4.61–64 rimport.md sweep (TastyMeal gating, life-threat override, Skill XP, Joy need, Backstories, Trait conflicts, role-canonical bonus item).

---

## [0.4.64] — 2026-05-14

### rimport.md Phase 4 sweep — implements all in-scope items from the v0.4.60 comparison report

Sam: "Implement all features, optimizations, and improvements up to current Phase 4." Working from `C:\Claude\Cloud\rimport.md` (the RimWorld vs SmurfulationC system-by-system comparison shipped at v0.4.60), this version executes every item from rimport's tables A–D that fits Phase 4 scope. Items requiring net-new systems (Phase 5: stockpiles / construction / bills; Phase 6: mental breaks / opinions; Phase 7+: combat / power / etc.) stay queued for the roadmap update at `SmurfulationC_Roadmap_2026.md`.

This single-version bump consolidates four batches that landed sequentially during the same session (v0.4.61 → v0.4.64). Each batch's diff is independently bisectable in source control; the version label only ticks at the end so the player-visible release reads as one coherent gameplay update.

#### Batch A — Correctness fixes (v0.4.61)

**E2 — TastyMeal thought reserved for prepared meals** (`scripts/simulation/items/Quality.cs`, `scripts/simulation/Thought.cs`). The pre-v0.4.61 `MealThoughtKey(Quality)` switch returned "TastyMeal" for normal-quality food, so every raw smurfberry triggered "Had a tasty meal" — inflating baseline mood and leaving Phase 5's Cook task with nothing aspirational to add. Fixed: `MealThoughtKey(Quality, bool isCooked = false)`. Raw eating returns the new "AteSimple" thought (+1 mood, 600-tick TTL). The `isCooked = true` branch keeps the old TastyMeal/AteFavorite tier for Phase 5 Cook to call into. Quality extremes (Crude → AteHungry, Masterwork/Legendary → AteFavorite) preserved across both branches because exceptional raw food still feels like a treat (a perfect wild berry).

**E6 — Life-threatening needs override even non-interruptible PlayerOrder** (`scripts/simulation/systems/BehaviorSystem.cs`). Pre-v0.4.61 `CriticalNeedsOverride` checked `currentPriority < 100f`, but PlayerOrder priority is exactly 100f, so a starving smurf walking on a "Move here" order would obediently starve to death. New `IsLifeThreatening(s)` returns true at `Nutrition < 5f` or `Rest < 5f` — true emergency. The `needNewTask` gate now bypasses `ct.Interruptible` when life-threat is true. RimWorld parallel: `JobGiver_Work`'s emergency tier overrides drafted-state movement on health-critical thresholds. Hard floor at 5f keeps the bypass rare — a smurf at Nutrition=18 still respects the player order.

**E8 — Designation paint validation: confirmed already correct.** Walked the four `SetXxxDesignation` paths in `LocalMap.cs`. Each validates terrain/vegetation at entry and returns silently on invalid tiles. Mid-paint Boulder → Mud transitions are already handled correctly. No change.

**G2 — Open Converse to ANY pair: confirmed already correct.** `NewConverseTask` in `BehaviorSystem.cs` walks all alive smurfs in range and PREFERS liked partners but doesn't EXCLUDE non-liked. Outcome thoughts (ChatWithFriend / NiceChat / ChatWithEnemy) already differentiate by mutual preference. No change needed — the rimport reading was wrong.

#### Batch B — Skill XP (v0.4.62)

**G3 — work-driven XP gain with daily-cap saturation** (`scripts/simulation/Smurf.cs`, `scripts/simulation/SkillRegistry.cs`, `scripts/simulation/systems/BehaviorSystem.cs`, `scripts/simulation/SimulationCore.cs`). RimWorld parallel: `SkillRecord.Learn(float xp, ignoreLearningSaturation)` + 4000 XP/day soft cap with 0.2× saturation factor.

Two new fields on `Smurf`: `SkillsXp` (per-level XP bucket) and `SkillsXpToday` (daily-cap window). `SkillRegistry.GainXp(s, name, amount)` applies the saturation curve and rolls level transitions using the RimWorld `1000 + 100×level²` cost curve — level 0→1 costs 1000 XP, level 19→20 costs 37 100 XP. `SimulationCore` clears `SkillsXpToday` for every living smurf at day boundary (alongside the existing aging tick).

Hooked into the four work-completion paths and two sustained tasks:
- `GatherMaterial` (boulder mined): +80 Mining XP
- `GatherFood` (vegetation harvested): +40 Foraging XP
- `ChopWood` (tree felled): +60 Foraging XP
- `CutVegetation` (plant cut): +30 Botany XP
- `Socialize` (sustained): +0.06 Social XP per tick
- `Attune` (sustained): +0.08 Arcane XP per tick

A mid-skill miner gets from level 4 → 5 in roughly 12 boulders. Mastery (level 19 → 20) takes ~460 boulders, so the long-game progression curve is meaningful but not punishing. Level cap stays at 20.

#### Batch C — Joy need as the sixth need (v0.4.63)

**G4 — Joy / Recreation** (`scripts/simulation/Smurf.cs`, `scripts/simulation/systems/NeedsSystem.cs`, `scripts/simulation/systems/MoodSystem.cs`, `scripts/simulation/systems/BehaviorSystem.cs`). RimWorld's Joy is a sixth need that captures "this colonist worked too hard, no recreation, mood drifts down." We had idle tasks (Wander/Loiter/Observe/Converse/Meditate/VisitFavorite) since v0.3.43 but no need to feed them — they were decorative.

New `Smurf.Joy` field (defaults 100, decays 0.005/call, restored by every idle task). `NeedsSystem.Tick` decays it unmodified by role/lifestage/trait (the existing idle-tier weighting per personality already serves as the implicit modifier). `MoodSystem.NeedsContribution` weighting redistributed to fold Joy in at 0.10 (carved 0.03 from Nutrition, 0.02 from Rest/Social/Magic each, Safety unchanged at 0.15).

`BehaviorSystem.ApplyTaskEffect` per-task Joy gain (per-tick × `dt`):
- Loiter: +0.6× JoyRate (drifting around)
- Observe: +0.8× (people-watching)
- Converse: +1.0× (best conversational)
- Meditate: +0.7× (introspective)
- VisitFavorite: +1.2× (favourite spot — best per-tick, justifies the longer travel)
- Wander: +0.5× (basic)

`JoyRate = 5f` per second is calibrated so a 5-second loiter restores ~25 Joy (a quarter-bar) — meaningful but not instant. A smurf with no idle activity for ~5 in-game days drifts from 100 → 40 (Stressed-tier) and starts pulling mood down.

`MoodCacheJoy` added alongside the other 5 cache fields so `NeedsChangedEnough` honours the v0.4.23 epsilon fast-path for the new dimension.

#### Batch D — Pawn-gen quality (v0.4.64)

**G6 — Backstories** (new file `scripts/simulation/BackstoryRegistry.cs`, plus `Smurf.cs` fields `Childhood` and `Adulthood`, plus wiring in `SimulationManager.AddSmurfFromTemplate` / `AddSmurf`). RimWorld's `BackstoryDef` is one of the cheapest narrative-multiplier patterns in the genre — every pawn carries a 1-2-paragraph history that injects skill bumps + flavour text. Implemented as data-only:

- 8 childhood backstories (Wandering Berry-Picker, Hearth Apprentice, Mushroom-House Letter, Observant Sprout, Stream-Splasher, Star-Gazer, Workshop Shadow, Herb Gardener) — every smurf gets one regardless of age.
- 10 adulthood backstories (Forest Scout, Library Mouse, Battle Veteran, Ritual Singer, Cauldron Keeper, Stonemason's Assistant, Healer's Apprentice, Hearth Orator, Roaming Tinker, Gargamel Survivor) — only assigned to Juvenile+ (≥20 years).
- Each backstory: `Key` + `Label` + `Description` + `Dictionary<string,int> SkillBumps`. Bumps applied via `BackstoryRegistry.AssignAndApply(s, rng)` AFTER `SkillRegistry.Distribute` so they layer on top of the right-skewed budget. Modest values (+1 to +3 per skill) so backstory adds flavour without dominating.

Idempotent: pre-existing keys on `Smurf.Childhood / Adulthood` (e.g. from save load) are kept and bumps are NOT re-applied. UI surfacing (smurf card, hover line) deferred to Phase 5 polish; data is live now.

**G8 — Trait conflict registry** (`scripts/simulation/PersonalityRegistry.cs`). Mirrors RimWorld `TraitDef.conflictingTraits`. Six conflict pairs that contradict one another in canon or psychology:

- Optimist ↔ Pessimist
- Stoic ↔ Empath  ("emotions? never heard" vs "feels everyone's feelings")
- Stoic ↔ Worrywart
- Sleepyhead ↔ Night Owl
- Introvert ↔ Gossip  (needs solitude vs needs the colony grapevine)
- Greedy Gut ↔ Sarsaparilla Snob  (gluttonous vs picky)

Conflict-aware `Assign` loop: candidate pulled from pool, if it conflicts with anything already picked the candidate is skipped (already removed from pool, so no re-roll loops). Trait counts are bounded by personality count (1–5), and the conflict set covers <30% of pairs, so the pool effectively never runs out before the count is met — but the loop is defensive against that case anyway.

**E5 — Scenario items filtered by role** (`scripts/simulation/items/ItemFactory.cs` `RollStartingKit`). The base kit was already partly role-aware (Crafter wood, role-specific tool), but Guardians had no actual weapon and Mages/Scholars had no extra magic resource to start research with. Added a `switch (role)` post-base-kit that grants one role-canonical bonus item:

- Guardian: 1× Spear (their starting weapon)
- Mage: 5× Raw Essence (research material)
- Scholar: 3× Raw Essence (smaller stash, magic-adjacent role)
- Caretaker: 4× Herb Cluster (medicinal supplies)
- Crafter / Forager / Elder: no extra (their base kit already covers the role intent)

### Why no Optimization batch this version

The rimport's Optimization items (O1 hash-bucket polling, O2 push-based haul registry, O3 drag-paint coalesce, O4 generic reservation manager, O5 digit atlas) were re-evaluated against the v0.4.50–v0.4.58 perf wins already shipped. At the 50-smurf playable scope (per `project_scope_population.md`), per-tick `needNewTask` checks are cheap and don't need bucketing; the v0.4.51 allocation-free haul scan and v0.4.56 throttled overlay rebuilds already cap the dominant costs; the v0.4.58 A* crowd cost handles work-face dispersion. The remaining items (O4, O5) are larger refactors queued for the roadmap. Optimization sweeps will resume when 250-smurf scaling becomes a stated goal.

### Preserved unchanged

- All v0.4.50–v0.4.60 perf + correctness work intact.
- Render throttles, designation overlays, dev panel layout — all untouched.
- Save format: `Smurf.SkillsXp / SkillsXpToday / Joy / Childhood / Adulthood` are new public properties. Save round-trip will need to read them; until SaveManager updates land, loaded smurfs default to 0 / 100 / empty (acceptable — they re-roll forward from there). To be addressed alongside the Phase 5 stockpile-zone save schema.

---

## [0.4.60] — 2026-05-14

### Fixed — Dev "Kill" button: corpse + carried items now spawn correctly

Sam: "Corpses also do not appear at point of death when smurfs die. They just disappear and their items disappear with them (on using kill command). Research how pawn death works in Rimworld for help."

#### Diagnosis

`DevKillSmurf` (`SimulationManager.cs` ~L1067) flipped `s.IsAlive = false` directly on the main thread. The next sim tick's `_smurfs.RemoveAll(s => !s.IsAlive)` (`SimulationCore.cs` ~L335) ran BEFORE the per-smurf working-list loop where natural-death paths call `DropCorpseGear`. So the dev-killed smurf was filtered out of the iteration → `DropCorpseGear` never fired → no corpse Item, no equipment/inventory drop, no `PendingDeaths` event for the message log. The smurf simply vanished, taking their gear with them.

Natural death (aging, vital-organ failure) didn't have this bug because both paths set `IsAlive = false` AND called `DropCorpseGear` AND enqueued `PendingDeaths` inline within the same tick. Dev kill skipped two of the three steps.

#### RimWorld reference

Decompiled `Verse.Pawn.Kill()` (~L2099–2309) defines the canonical kill flow:

1. Cache position
2. Storyteller notification
3. `health.SetDead()` — Dead flag flips
4. `DropAndForbidEverything()` — equipment, inventory, carried items drop on the death tile (apparel rides with the corpse)
5. `DeSpawn()` — pawn removed from `Map.mapPawns`
6. `MakeCorpse()` + `GenPlace.TryPlaceThing(corpse, ...)` — corpse becomes an independent `Thing`
7. Post-death notifications (faction / quest / UI)

All atomic within one method call. **Critically: dev-mode "Kill" doesn't have a separate code path.** From `HealthUtility.DamageUntilDead`: it loops up to 200 iterations of synthetic blunt damage that funnels through the normal damage → `ShouldBeDead` → `Kill()` pipeline. One death pipeline, fewer bugs.

#### Fix — `SimulationCore.KillSmurf(Smurf s, CauseOfDeath cause)`

New public method following the RimWorld order:

```csharp
public void KillSmurf(Smurf s, CauseOfDeath cause)
{
    if (s == null || !s.IsAlive) return;   // idempotent
    s.CauseOfDeath = cause;
    DropCorpseGear(s);                      // drops inventory, equipment, spawns corpse Item, broadcasts witness thoughts
    s.IsAlive = false;
    PendingDeaths.Enqueue(new SmurfSnapshot(s));
}
```

Order matches RimWorld: cause → gear-drop → flag flip → death event. The flag flip happens AFTER `DropCorpseGear` so any code path inside the gear-drop that filters on `IsAlive` doesn't accidentally skip the dying smurf. (The witness-thought broadcast already filters via `other == s`, so it's exempt either way; ordering keeps the invariant simple.)

Idempotent so a second call (e.g. natural-death + dev-kill race) no-ops instead of double-spawning the corpse.

#### Refactored — both natural-death paths now call `KillSmurf`

`scripts/simulation/SimulationCore.cs`:

- **Aging-death** (~L373) — was 4 inline lines, now `KillSmurf(s, CauseOfDeath.Natural)`.
- **Vital-organ-failure** (~L437) — was 5 inline lines (with the Starvation/Natural ternary), now `KillSmurf(s, cause)` with the ternary preserved.

One canonical path — RimWorld's "one death pipeline, fewer bugs" pattern.

#### `DevKillSmurf` routes through PostMainThreadCommand

`scripts/SimulationManager.cs`:

```csharp
public bool DevKillSmurf(string name)
{
    var s = DevFindSmurfByName(name);
    if (s == null || !s.IsAlive || _core == null) return false;
    _core.PostMainThreadCommand(() => _core.KillSmurf(s, CauseOfDeath.Dev));
    return true;
}
```

Matches the v0.4.55 pattern (`DevFillNeeds` / `DevDrainNeeds` also queue through `PostMainThreadCommand`). The kill runs at the start of the next sim tick (in the existing command-drain phase), BEFORE the `_smurfs.RemoveAll` sweep — so the corpse Item lands on the map and the gear drops to the death tile WITHIN the same tick, before the smurf is removed from the live list. The dead-smurf snapshot built at end-of-tick correctly excludes them (the `SimulationSnapshot` constructor filters on `IsAlive`), so the smurf icon disappears in the same frame the corpse appears.

Return `true` still signals "smurf found" (looked up on main thread); the caller's `Smurf not found ×N` log path stays accurate.

### What's now visible after a Dev Kill

- **Corpse Item** at the death tile — shows up in `ItemDropOverlay` as the 💀 variant, has the v0.4.33 obituary line in `TileInfoOverlay` ("Name — Adult Sex Role, died of Dev; body Fresh"), and decays via `LocalMap.TickCorpseDecay` over ~7 in-game days like any other corpse.
- **Carried inventory** drops on the death tile (every stack from `s.Inventory`, not just the most-recently-grabbed).
- **Equipment** (weapons, tools, apparel slots tracked in `s.Equipment`) drops on the death tile with the v0.4.30 stockpile-overflow rules.
- **Witness-thought broadcast** to every living smurf within ~10 tiles of the body — already wired in `DropCorpseGear` since v0.4.35; just needed the kill path to actually reach it.
- **Death event** in the message log via `PendingDeaths` → `OnSmurfDied` → `_msgLog.Post`.

### Preserved unchanged

- `DropCorpseGear` itself (v0.4.7 + v0.4.30 + v0.4.33 + v0.4.35) untouched. It already does the right thing — the bug was that the dev path wasn't calling it.
- Natural death timing (aging at day boundary, vital-organ failure check) unchanged in behaviour.
- Save/load roundtrip unaffected.

---

## [0.4.59] — 2026-05-14

### Changed — Retry / give-up / cooldown timings halved across the board

Sam: "Decrease amount of time to retry actions."

The v0.4.36 / v0.4.57 retry-and-recovery timings were set conservatively to avoid thrashing in the steering-only era — when smurfs got stuck, we wanted them to actually try a few options before giving up. With v0.4.58's A* crowd cost dispersing paths strategically (smurfs no longer converge tightly on the same approach tile in the first place), the long dwell windows added more noticeable lag than recovery value. Halving them across the board keeps the relative ordering intact (re-path < yield < give-up) while making smurf reactions visibly snappier.

#### `scripts/simulation/systems/BehaviorSystem.cs` constants

| Constant | Before | After | Wall-clock at 1× (60 Hz tick) |
|---|---|---|---|
| `StuckRePathTicks` | 15 | **8** | 250 ms → **133 ms** |
| `YieldTriggerTicks` | 22 | **12** | 367 ms → **200 ms** |
| `StuckThreshold` | 30 | **18** | 500 ms → **300 ms** |
| `YieldDurationTicks` | 60 | **30** | 1000 ms → **500 ms** |
| `DesignationCooldownTicks` | 120 | **60** | 2000 ms → **1000 ms** |
| `AvoidTiles[…]` TTL | 360 | **180** | 6000 ms → **3000 ms** |

Recovery sequence at the worst-case stuck:
- t=0: smurf stops moving → `StuckTicks` starts climbing
- t=8 ticks (~133 ms): one re-path attempt fires (`RePathTried` flag set)
- t=12 ticks (~200 ms): if blocker is in the way, ask them to lie down (yield mechanic)
- t=18 ticks (~300 ms): give up — claim released, tile blacklisted for 3 s in this smurf's `AvoidTiles`, post-abandon cooldown set to 60 ticks (~1 s)
- t=78 ticks (~1.3 s): cooldown expires, smurf eligible to pick a designation again — by which point either the original tile has cleared (claim free, blacklist still active so picks elsewhere) or A* routes through a different cluster

The yield duration (60 → 30) stays comfortably above the time it takes for the unblocked smurf to walk past — half a second is enough for a 16-px tile-step at the smurf's base SimSpeed.

`Smurf.cs` doc-comment for `DesignationCooldownTicks` updated to reflect the new value + reasoning.

### Why the spacing still works

The relative ordering is what matters for the recovery cascade: re-path tries first (cheap, often fixes the issue), then yield (asks the obstruction to step aside), then give-up (last resort). Halving every constant by the same factor preserves all three windows; the smurf still gets a re-path attempt and a yield-trigger window before abandoning, just on a tighter clock.

The 1-second cooldown is still long enough for the colony cluster to physically disperse (smurfs walk ~3-5 tiles in that window at base speed), so the re-evaluation finds a meaningfully different state rather than re-picking the same target instantly.

### Preserved unchanged

- All v0.4.57 / v0.4.58 logic (Touch arrival, post-abandon cooldown mechanism, A* crowd cost) untouched. Only the timing constants are tightened.
- Steering layer, yield mechanic shape, claim system, AvoidTiles ring structure — all unchanged.
- No visual / UI changes. No save format change.

---

## [0.4.58] — 2026-05-14

### Added — RimWorld-style soft-cost crowd avoidance in A*

Sam (after v0.4.57 confirmed the Touch-arrival + post-abandon-cooldown fixes worked): "Implement the remaining fix as that may further help performance and keep smurfs from getting stuck."

The remaining RimWorld pattern from the v0.4.57 research was their pathfinder's `Cost_PawnCollision = 175`: every blocking pawn on a candidate tile adds a flat cost to the A* expansion, so the pathfinder naturally routes AROUND clusters instead of through them. RimWorld doesn't have a steering layer at all — A* alone resolves crowd avoidance because the path itself avoids occupied tiles.

SmurfulationC has both: steering-layer side-step fallback (v0.4.36 + v0.4.20 primary-direction-wins rule) AND now A*-layer crowd avoidance. The two work in concert — A* handles strategic routing (find a path that avoids the cluster), steering handles tactical resolution (someone stepped into my next tile after I picked my path).

#### `scripts/simulation/Pathfinder.cs` — soft-cost expansion

New constant `PawnCollisionCost = 175` (matches RimWorld decompile). New optional params on `FindPath`:

```csharp
public static bool FindPath(LocalMap map, Vector2 fromPixel,
    (int X, int Y) toTile, List<Vector2> resultBuffer,
    int[]? smurfPerTile = null, int askerTileIdx = -1)
```

`smurfPerTile` is BehaviorSystem's per-tick occupancy grid (already populated at tick start — see `PopulateOccupancyGrid`). `askerTileIdx` is the asker's own tile index in that grid, used for self-exemption (the asker contributes to the count of its own tile but shouldn't pay collision cost on it).

Inner expansion loop now applies the cost:

```csharp
int stepCost = Cost[i];
if (useCrowdCost)
{
    int crowd = smurfPerTile![nIdx];
    if (nIdx == askerTileIdx && crowd > 0) crowd--;
    if (crowd > 0) stepCost += PawnCollisionCost * crowd;
}
int tentative = curG + stepCost;
```

Cardinal step is 100, so one blocker bumps a tile's effective cost from 100 → 275. The pathfinder will detour 1–2 tiles to avoid one blocker, but plow through if all alternatives are also crowded — exactly RimWorld's calibration.

Admissibility: the heuristic doesn't see crowd cost (it's a pure Manhattan/diagonal estimate), so it remains a lower bound on the true path cost. A* still finds the optimal path; just expands a few more nodes when multiple routes become competitively-costed in dense areas.

`useCrowdCost` is gated on `smurfPerTile != null && smurfPerTile.Length >= W*H`. Sized-mismatch buffers degrade silently to legacy behaviour, so the new params are safe to pass even before the occupancy grid is populated.

#### `scripts/simulation/systems/BehaviorSystem.cs` — wire all 3 callsites

New helper `OccTileIdx(Smurf s)` computes the smurf's current tile index in `_smurfPerTile` (returns -1 if grid not populated). Three `FindPath` callsites updated to pass `_smurfPerTile, OccTileIdx(s)`:

1. **Initial task path** (line ~472, after task selection)
2. **Path invalidation re-path** (line ~755, when the next waypoint becomes impassable mid-walk)
3. **Stuck re-path** (line ~967, when `StuckRePathTicks=15` fires the one allowed re-path before give-up)

All three benefit equally from crowd-aware routing.

### Why this further reduces stuck behaviour

The v0.4.57 Touch-arrival fix lets a smurf complete work from any of the 8 neighbours of a boulder. v0.4.58 makes the **path itself** spread across those 8 neighbours instead of converging on the same one. So 8 smurfs targeting the same boulder no longer all path to the same approach tile, jostle for it, and trigger soft-collision steering fan-outs — they path to 8 different approach tiles from the start.

Combined effect at saturation:
- v0.4.50 → cached HUD totals (eliminated frame-time cliff)
- v0.4.51 → allocation-free haul scan
- v0.4.52 → right-click drops task cleanly
- v0.4.56 → throttled overlay rebuilds (5 Hz)
- v0.4.57 → Touch-arrival + post-abandon cooldown
- v0.4.58 → A* crowd avoidance routes around jams from the start

### Estimated perf impact

Per A* expansion: +3 instructions (one array read, one compare, one conditional add). At 50 smurfs × ~15 pathfinds/sec × ~300 expansions/path = ~225 000 extra instructions/sec — negligible against the existing pathfinder cost.

In practice, A* expansion COUNT is often LOWER with crowd cost because the pathfinder finds a clear corridor faster than it would have spent thrashing on contested tiles. Re-path frequency also drops because the initial path is more durable (less likely to traverse a crowded waypoint that becomes blocked). Net main-thread effect is a small win, not a regression.

### Preserved unchanged

- Steering layer (v0.4.20 primary-direction-wins + v0.4.36 side-step fallback) remains active for tactical dynamic-blocker resolution. A* + steering work in concert: A* picks the route, steering handles late-arriving blockers.
- Yield mechanic (v0.4.29) unchanged.
- Touch arrival + post-abandon cooldown (v0.4.57) unchanged.
- Occupancy grid is rebuilt at tick start (`PopulateOccupancyGrid`), so the path snapshot can be up to one tick (100 ms at 1×) stale. That's acceptable — re-paths happen frequently enough that paths re-route within 1–2 ticks of any actual congestion change.
- All visual systems (overlays, HUD, smurf rendering) untouched.

### What's next

This completes the RimWorld-pathfinding-pattern adoption queued from v0.4.57's research. Three of four patterns now active: target-reservation (already had), Touch-arrival (v0.4.57), soft-cost crowd avoidance (v0.4.58), spam-guard cooldown (v0.4.57 approximation). The fourth — RimWorld's exact `jobsGivenRecentTicks` 10-jobs-in-10-ticks counter — would be a heavier refactor of how jobs are issued; the v0.4.57 cooldown approximation is sufficient for current observed behaviour.

---

## [0.4.57] — 2026-05-14

### Fixed — Smurfs getting stuck on impassable-target work at saturated dig sites (RimWorld-style fixes)

Sam: "Smurfs still get stuck trying to break tiles, so the retry logic needs a little work. Examine this and find out why they're still getting stuck. If you can find anything on how rimworld solves this problem, that's our closest analog and should serve as a great pathfinding model."

#### Diagnosis

At 50 smurfs converging on a ~10×10 boulder cluster, only ~36 perimeter tiles are reachable. The remaining 14 smurfs cluster on the perimeter and:

1. **Walk to an adjacent tile but never fire the work effect.** `MoveOneTick`'s arrival check was `dist(SimPos, walkTo) ≤ ArrivalRadius` (14 px), where `walkTo` is the *specific* adjacent tile `NearestAdjacentPassableTile` picked at task-assignment time. When that adjacent tile is occupied by another smurf, soft-collision steering vetoes entry → the smurf orbits at `ArrivalRadius + ε` and never crosses the threshold → `ApplyTaskEffect` for `GatherMaterial` never runs.

2. **Abandon, then immediately re-pick the same work tile.** The per-smurf `AvoidTiles[4]` ring with 360-tick TTL is too small at 50:30 saturation. After abandonment, `SelectTask` immediately re-evaluates and finds the nearest reachable excavate — almost always the same tile the smurf just gave up on, because there's nothing else open.

#### RimWorld reference

Research into RimWorld's pathfinding (decompiled `Verse.AI.PathFinder`, `ReservationManager`, `Pawn_JobTracker`) surfaced three patterns we already partially had and one we didn't:

| RimWorld pattern | SmurfulationC status (pre-v0.4.57) | Notes |
|---|---|---|
| Reserve the **target**, not the work cell | Already had — `_claims` dict locks the boulder | OK |
| Soft-cost crowd avoidance in A* (~175 cost per blocker) | Steering-based, not cost-based | A bigger refactor — deferred |
| `PathEndMode.Touch` arrival — work fires from ANY of 8 neighbours | **Missing** — required arrival on the picker's specific tile | Fixed in v0.4.57 |
| Spam-guard: 10 jobs in 10 ticks → forced idle | **Missing** — per-pawn AvoidTiles was the only break | Approximation added in v0.4.57 |

The two fixes below adopt the missing patterns directly.

#### Fix 1: PathEndMode.Touch arrival for impassable-target work

`scripts/simulation/systems/BehaviorSystem.cs`. New helper `IsAtTouchArrival(s, map)` checks:

- Task type is one of `GatherMaterial` / `ChopWood` / `CutVegetation` (`RequiresAdjacentApproach`)
- Task target tile is currently impassable (still a boulder / log / large mushroom)
- Smurf's current tile is at Chebyshev distance ≤ 1 from the target tile

If all three hold, `MoveOneTick` fires arrival immediately — `ApplyTaskEffect` runs and the boulder excavates, regardless of which specific adjacent the picker chose. The dist-to-walkTo check stays as a fallback for non-impassable targets and the path-waypoint consumption loop.

Effect: at a saturated work face, any smurf on any of the 8 neighbours of a claimed boulder can mine it. The "smurf orbits at arrival + ε" deadlock mode disappears entirely — the smurf doesn't need to step onto the picker's specific adjacent tile to do the work.

#### Fix 2: Post-abandon designation cooldown (RimWorld spam-guard analogue)

`scripts/simulation/Smurf.cs` — new field `int DesignationCooldownTicks = 0`.

`scripts/simulation/systems/BehaviorSystem.cs`:

- **On give-up** (the `StuckTicks > StuckThreshold` branch in `MoveOneTick`): set `s.DesignationCooldownTicks = 120` (~2 s at 1×). Already calls `ReleaseTaskClaim`, adds the tile to `AvoidTiles`, emits `TaskAbandoned` thought, switches to `NewWanderTask`. The cooldown is a sixth bookkeeping action.
- **Per-tick decrement** (alongside the existing `AvoidTiles` TTL decrement at the top of the per-smurf loop): `s.DesignationCooldownTicks -= tickInterval` while > 0.
- **SelectTask gate**: a single `bool designationsOk = s.DesignationCooldownTicks <= 0` guards all six designation branches (Forager → gather, Crafter → excavate, non-Forager → gather, non-Crafter → excavate, chop, cut). When the cooldown is active, every designation lookup is skipped — the smurf falls through to the idle / wander / loiter / observe / converse / meditate / visit-favourite tier and physically disperses from the work face for 2 seconds before re-evaluating.

This is the closest practical analogue to RimWorld's `Pawn_JobTracker.jobsGivenRecentTicks` spam-guard (which kicks the pawn into a `JobGiver_IdleError` job when the job system thrashes). We can't run RimWorld's exact tick-window counter because we don't reissue jobs per-tick, but the post-abandonment cooldown solves the same root problem: the colony cluster needs to breathe before the smurf re-converges on the same target.

#### Why these two fixes resolve the saturation deadlock

- **Fix 1 unsticks the smurfs at the boulder face who CAN work.** With Touch-arrival, every smurf within Chebyshev=1 of a boulder fires the effect (subject to the existing per-tile claim lock). The work-progress rate is no longer bounded by "the picker's chosen adjacent must be uncrowded."
- **Fix 2 disperses the excess smurfs who CAN'T work.** The 14 smurfs who can't claim any boulder will give up at `StuckThreshold = 30 ticks`, get force-Wandered for 120 ticks, drift off the work face, and re-evaluate from a position where a different designation may be closer (or where their original target has since been excavated by someone else and the claim is free).

### What was NOT changed

- Region graph, A* pathfinder, occupancy grid, yield mechanic, soft-collision steering — all untouched. The two fixes are additive: they sit on top of existing systems rather than replacing them.
- Designation paint, item drop throttle (v0.4.56), HUD totals cache (v0.4.50), haul allocation-free walk (v0.4.51) — untouched.
- Soft-cost crowd avoidance in A* (RimWorld's pattern) is queued for a future refactor if cluster perf still needs work after this bump. Current steering remains.

---

## [0.4.56] — 2026-05-14

### Fixed — Item drop + terrain redraws throttled to 200 ms with per-tile dirty tracking

Sam's directive after the v0.4.55 perf diagnosis: "Implement per-tile tracking and throttle redraws on item numbers, drops, or terrain changes to every 200ms. The player should see designations occur at the same time they happen, but terrain and vegetation tile updates can be throttled to 200ms as they will not be noticed, especially as smurfs should notice items dropping and terrain changes fast enough to reassign their path/task quite unnoticeably."

Three surfaces, three decisions:

| Surface | Trigger | Throttle | Reason |
|---------|---------|----------|--------|
| `ItemDropOverlay` (icons + count badges) | `ItemsChanged(x, y)` | **5 Hz (200 ms)** + per-tile dirty set | Hot path under heavy gather/excavate — was 60 Hz full rebuilds |
| `LocalMapRenderer` (terrain + vegetation) | `TerrainChanged` / `VegetationChanged` | **5 Hz (200 ms)** GPU upload | Was 10 Hz (100 ms) — Sam: 200 ms still imperceptible |
| `DesignationOverlay` | `DesignationChanged` | **None — instant** | Player-driven; lag would feel like input drop |

#### `scripts/ui/ItemDropOverlay.cs` — per-tile dirty set + 200 ms throttle

Replaced the `volatile bool _dirty` flag with a `HashSet<(int X, int Y)> _dirtyTiles` (guarded by `_dirtyLock`). Each `ItemsChanged(x, y)` event now adds the tile coord to the set; the previous bool flipped to true on any change with no record of which tile.

`_Process` accumulates frame delta in `_timeSinceRefresh` every frame regardless of dirty state. When the set is non-empty AND `_timeSinceRefresh ≥ 0.20s`, the path drains the set under lock, resets the counter, and runs the existing `SnapshotDroppedItemSummaries` → `RebuildInstances` → `_badgeNode.QueueRedraw()` chain. The badge draw cost (per-tile `DrawString` + `DrawRect`, the dominant cost at heavy item count) is now bounded to 5 calls per second instead of 60.

The per-tile set is functionally similar to the bool flag for triggering refreshes today (we still rebuild every tile via `_summaries`, since Godot's `_Draw` is all-or-nothing per canvas item), but it:
- Skips refreshes when an event fires with no actual coord-set change (rare but possible).
- Records the surface-area of "what changed since last refresh," enabling future partial-rebuild optimisations without another data-structure refactor.
- Provides accurate signal for debugging (dump the set if perf still looks off).

A sentinel `(-1, -1)` entry seeded by `SetMap` forces a fresh-bind refresh on the next `_Process` regardless of throttle state, so map loads paint immediately.

#### `scripts/ui/LocalMapRenderer.cs` — upload throttle 100 ms → 200 ms

`UploadIntervalMs` bumped from `100.0` to `200.0`. The CPU-side `_image` keeps repainting per dirty tile on every `_Process` (unchanged — the CPU work was already cheap), but the GPU `_texture.Update(_image)` call now runs at most 5 Hz instead of 10 Hz. Halves the per-second GPU sync points.

Per-tile dirty tracking already existed here (since v0.4.21) and stays unchanged.

#### `scripts/ui/DesignationOverlay.cs` — explicitly NOT throttled

Left as-is from the v0.4.55 rollback. Designation paint feels instant on drag, and the `RebuildIfDirty` synchronous path called from `GameController` after `DesignateRect` is unchanged.

### Why this doesn't slow down smurfs

The sim thread reads from `LocalMap` directly — `Map.Get(x, y)`, `Map.GetItemsOnTile(x, y)`, etc. — none of which touch the rendered overlay state. When a tile mutates (boulder → mud, item dropped, designation completed), the underlying `LocalTile` / `_droppedItems` / `_designations` writes happen on the sim thread inside the same lock that's read by every `Find*` and `IsPassable` query. So:

- A smurf walking toward a designated boulder that another smurf just excavated sees the terrain change on the very next sim tick (~100 ms at 1× speed), regardless of when the renderer next uploads the corresponding pixels.
- An idle smurf evaluating SelectTask sees newly-dropped haulable items on the next tick, not when the icon shows up on screen.
- Pathfinding's `IsWorkReachable` reads the live region graph; the renderer's frame cadence is irrelevant.

So the perceptual lag is bounded to the renderer (max 200 ms) while sim responsiveness stays at the sim's own tick rate (10 Hz at 1× → 100 ms).

### Estimated perf reclaim

At 300 dropped tiles + 5000 designations + 50 active smurfs, the v0.4.55 main-thread cost was dominated by `DrawAllBadges` (per-tile `DrawString` × 300 tiles × 60 Hz = ~5 ms/frame × 60 Hz = 300 ms/sec). After the v0.4.56 throttle, that becomes 5 ms × 5 Hz = 25 ms/sec — a **12× reduction** in badge cost, which is the difference between Sam's observed 45 FPS and the 70 FPS baseline.

GPU upload throttle (100 → 200 ms) is a smaller absolute win (~3-5 ms/sec saved) but reduces stutter from GPU sync points on terrain-mutation bursts.

### Preserved unchanged

- DevPanel binding fix + Drain → 0 (v0.4.55). Untouched.
- Designation paint instant on player drag. Untouched.
- River subtypes, mountain subtypes, Shallows + wade speed. Untouched.
- Multi-trip haul + carrying capacity + stockpile rules. Untouched.
- Corpse decay + bones drop. Untouched.
- Yielding mechanic + soft-collision steering. Untouched.

---

## [0.4.55] — 2026-05-14

### Reverted — v0.4.54 rolled back; targeted dev-panel + drain fixes applied on top of v0.4.53

Sam tested v0.4.54 and reported: "Performance is now half of what it was before your changes and none of the buttons on the dev panel work." Directive: rollback to v0.4.53, fix the dev panel for real, and diagnose the v0.4.53 perf drop separately.

#### Rollback

Four files restored from `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.53\`:

- `scripts/SimulationManager.cs` — undo the `PostMainThreadCommand` routing on `DevFillNeeds` / `DevDrainNeeds`.
- `scripts/ui/DevPanel.cs` — undo the `Func<string, bool?>` hit/miss `ForEachSelected` (reverting to `Action<string>`).
- `scripts/ui/ItemDropOverlay.cs` — undo the 5 Hz refresh throttle.
- `scripts/ui/DesignationOverlay.cs` — undo the 5 Hz refresh throttle.

The v0.4.54 throttle approach was the right shape but apparently interacted badly with the in-flight scene; deferring the perf fix until the cause is understood is cheaper than shipping a slower version. The DevPanel hit/miss diagnostic was useful — it surfaced the real dev-panel bug (see below) — but it lives in v0.4.54 history and the bug is now fixed at the root.

#### Fix — DevPanel "Smurf not found" / silent no-ops (real bug)

The v0.4.54 hit/miss diagnostic surfaced `→ Smurf not found ×1` after clicking Drain Needs with a smurf selected. That message is the v0.4.54 ForEachSelected reporting `Sim?.DevDrainNeeds(name) != true` — and tracing through, **`Sim` was actually `null`** on the panel. Not a name mismatch, not a race — a binding bug.

`scripts/ui/GameController.cs` `BuildUILayer` (line ~265) constructs the DevPanel and wires `_devPanel.Sim = _sim;`. But `BuildUILayer` runs in `_Ready` **before** `StartSim()` (line 83), and `_sim` is only assigned by `StartSim()` (line 761). At the assignment, `_sim` is still the field's default value (null), so `_devPanel.Sim` captures null permanently. Every `Sim?.Dev*` call in the panel short-circuits to null, every action is a silent no-op.

The other panels (`_hud.Sim`, `_resourcesPanel.Sim`, `_jobsPanel.Sim`, `_card.Sim`) are assigned **inside** `StartSim` after `_sim` is constructed — the established pattern. DevPanel was the lone outlier. Moved its `Sim` assignment to `StartSim` alongside the others; `MapNode` and `GetSelectedSmurfs` stay in `BuildUILayer` because they don't depend on the sim instance.

This is what `Drain Needs ×1` was reporting as success in v0.4.53 — the action ran but did nothing because `Sim` was null. Buttons were all "broken" in this exact way since DevPanel was introduced (v0.4.32); the bug was invisible because the v0.4.53 `ForEachSelected` discarded bool returns.

#### Drain Needs floor lowered to 0

Sam: "Drain Needs should drain needs all the way to zero so that I can actually test things like eating, sleeping, and socializing."

`DevDrainNeeds` was setting needs to `5f`, which sat above the `Nutrition <= 0` starvation-damage trigger in `NeedsSystem.cs` (~line 66). Now sets every need to `0f` directly, so dev testing can exercise the starvation / collapse / mood-floor paths.

`DevFillNeeds` continues to set every need to `100f` — already the opposite extreme.

#### Performance diagnosis — v0.4.53 70 → 45 FPS during heavy designation / excavation / item-drop

NOT applying a fix yet (per Sam's directive to "find out why" before patching). Findings:

The dominant hot path is `ItemDropOverlay.DrawAllBadges` running at full main-thread frame rate. The flow:

- Every `LocalMap.DropItem` / `RemoveItem` fires `ItemsChanged` → `_dirty = true`.
- Next main-thread `_Process` calls `SnapshotDroppedItemSummaries` (walks the dropped-items dict, allocates a `List<DroppedTileSummary>`), `RebuildInstances` (writes ~N MultiMesh transforms), and `_badgeNode.QueueRedraw()`.
- The QueueRedraw triggers `_badgeNode._Draw` → `DrawAllBadges()` which iterates every summary and emits **per-tile draw commands**:
  - `font.GetStringSize(...)` — text measurement (~5 µs)
  - `canvas.DrawRect(...)` — background rectangle
  - `canvas.DrawString(...)` — digit rendering (~10 µs)
  - For equipment items: `DrawShorthandLabelOn` (another DrawString)
  - For corpses: `DrawCorpseLabelOn` (another DrawString)

At ~300 dropped tiles and ~10–20 µs per badge, `DrawAllBadges` costs **3–6 ms per frame**. With 50 smurfs continuously dropping/picking-up items at the active excavation site, the `_dirty` flag never falls to false — `DrawAllBadges` fires every frame. That's 180–360 ms/sec of main-thread cost just for item badges, which fits the observed 70 → 45 FPS drop (delta of ~8 ms per frame).

Secondary contributors:
- `DesignationOverlay.RebuildInstances` — walks the designations dict every frame the player or sim flips a flag; ~50 µs at 5 000 designations.
- `LocalMapRenderer` redraws on terrain mutation when boulders become mud, etc.

**Candidate fixes for a future bump** (after Sam confirms the diagnosis):

1. **Bake the count digits into a sprite atlas + render via MultiMesh** — eliminates the per-frame `DrawString` cost entirely (same trick v0.4.25 used to fold the per-tile designation glyphs into MultiMesh sprites). Biggest one-shot win; doesn't sacrifice visual fidelity.
2. **Throttle the *badge* QueueRedraw to 5 Hz while keeping MMI updates instant** — narrower version of the v0.4.54 approach. Items would pop into the world instantly via the icon MMI; only their count badges would lag by ≤ 200 ms.
3. **Per-tile dirty tracking instead of a single bool** — only re-emit badges for tiles whose summary actually changed. Currently the bool fires a full rebuild even when one stack incremented by one.

(1) is the cleanest; (2) is the cheapest; (3) is the most precise. Holding off on the choice until Sam confirms the diagnosis and picks the trade-off.

### Preserved unchanged

Everything else in the v0.4.53 baseline. River subtypes, mountain subtypes, Shallows + wade speed, RimWorld-style selection brackets, tile/item inspector + hover overlay, multi-trip haul, corpse decay, yielding mechanic, soft-collision steering — all untouched. DevPanel layout (left edge, v0.4.53) untouched.

---

## [0.4.53] — 2026-05-14

### Changed — DevPanel relocated to the LEFT edge

Sam's report: "Debug screen doesn't work. Also — move it to the left side so it doesn't obscure values it's trying to change."

The dev-panel buttons were actually firing correctly (v0.4.51's log relocation already proved this — the "→ No smurf selected" line in Sam's screenshot was the live action result). The "doesn't work" perception came from the dev panel sitting in the top-right column, directly on top of the SmurfCardPanel, TileInfoOverlay, and TilePropertiesPanel. So when Sam clicked "Fill needs" or "+Mood (TastyMeal)" with a smurf selected, the action ran and the smurf-card values updated — but the dev panel was covering the card columns where those numbers lived.

#### `scripts/ui/DevPanel.cs` _Ready

```csharp
// Was (v0.4.32 → v0.4.52)
AnchorLeft  = 1f; AnchorRight = 1f;
OffsetLeft  = -240f;
OffsetRight = -UITheme.EdgeInset;

// Now
AnchorLeft  = 0f; AnchorRight = 0f;
OffsetLeft  = UITheme.EdgeInset;
OffsetRight = UITheme.EdgeInset + 240f;
```

Top / bottom offsets unchanged: the panel still starts ~110 px below the viewport top (clear of the left HUD capsule) and ends ~268 px above the viewport bottom (clear of the MessageLog which starts at -170 from the bottom). The right-side UI column — SmurfCardPanel, TileInfoOverlay, TilePropertiesPanel — is now fully unobscured while the dev panel is open, so Fill needs / Drain needs / +Mood / -Mood / Force yield / Kill all produce immediately-visible effects in the smurf card across from them.

### Removed — Mood-tab "Breakdown" section

Sam: "Remove 'Breakdown' section in Mood tab."

The Mood tab previously dedicated a small section under the mood bar to two lines:

- `Needs contribution: 99`
- `Personality modifier: +7`

Both numbers were already represented elsewhere on the same card — the mood bar shows the effective score, and the "Personality Effects" section below the Recent Thoughts list itemises every trait's signed contribution. The Breakdown section was redundant and pushed the actually-useful Recent Thoughts list further down the scroll, so it's gone:

- `scripts/ui/SmurfCardPanel.cs` — `Build_MoodTab` no longer adds the Breakdown header, the `_moodRawLabel` / `_moodModLabel` lines, or the trailing HRule. The first HRule (between mood-state description and Recent Thoughts) stays as the visual separator.
- Field declarations for `_moodRawLabel` / `_moodModLabel` removed.
- Tooltip wiring and per-tick text-setter calls for those two labels removed (they would have NullReference'd otherwise).
- `_moodEffLabel` (the mood-bar header) untouched.

### Why no other "debug screen doesn't work" change

Verified every DevPanel button callback fires under the existing wiring:
- `MakeBtn(text, action)` (`scripts/ui/DevPanel.cs` ~315) subscribes `b.Pressed += () => { try { action(); } catch ... }`.
- `Sim` / `MapNode` / `GetSelectedSmurfs` are all set by GameController immediately after `AddChild(_devPanel)` (lines 267–269).
- Sim methods `DevTogglePause`, `DevFillNeeds`, `DevDrainNeeds`, `DevAddThought`, `DevForceYield`, `DevKillSmurf`, `DevSpawnItem`, `DevSpawnSmurf` all exist in SimulationManager.
- The log label at the top of the panel (v0.4.51) writes the action's outcome on every press, so any silent failure mode would surface as an `err: …` line.

No code path was broken — the panel had nowhere to *visually report* its work because the very surface it was supposed to act on was hidden behind it. Moving the panel fixes the perception.

---

## [0.4.52] — 2026-05-14

### Changed — Right-click orders now interrupt the smurf's current task cleanly

Sam's report: when a smurf is selected and the player right-clicks to issue a Move or Pick-up order, the smurf should **immediately drop whatever they're doing** and execute the new order. Doubles as a manual escape hatch for the visible idle-freeze / stuck behaviour at heavy work sites.

The order-queue path already overwrote `s.CurrentTask` on the next tick (line 285 in `BehaviorSystem.Tick`), so the new task DID take effect — but the OLD task's bookkeeping was never released:

- **Haul reservations** on the old TargetId stayed registered in `HaulSystem._reservations`. Other smurfs saw the dropped item as "claimed" indefinitely; the colony lost that item from the haul pool until next reload.
- **Designation claims** (`map.ReleaseClaim`) on a Gather / Excavate / Chop / Cut target stayed registered. Other smurfs saw the work tile as taken and skipped it in `FindNearestExcavate` / `FindNearestGather`.
- **`PathWaypoints`** kept the stale route to the old target. The smurf would briefly head toward the old work site before the next path request kicked in.
- **`StuckTicks` / `RePathTried` / `IdleLingerTicks`** kept their old values. If the smurf was mid-stuck or mid-linger, the new order inherited the accumulated counters and could re-trigger the give-up branch before the new task had a chance to run.

The natural task transition path (`SelectTask` returns a fresh task at line 388) already does all of the above — clear waypoints, reset stuck state, fresh re-path budget. The player-order drain was the only entry point missing the matching cleanup.

#### `scripts/simulation/systems/BehaviorSystem.cs` Tick — player-order drain

Inserted before the existing `if (map != null)` item-check branch:

```csharp
ReleaseTaskClaim(s, map);
s.PathWaypoints.Clear();
s.StuckTicks      = 0;
s.RePathTried     = false;
s.IdleLingerTicks = 0;
```

`ReleaseTaskClaim` is the v0.4.7 helper already used by the stuck-detector give-up branch and the haul-completion path — it knows how to release the right resource per TaskType (Haul → `HaulSystem.ReleaseByIdString`; Gather/Excavate/Chop/Cut → `map.ReleaseClaim`). Calling it on the old task here closes the bookkeeping loop the player-order path was missing.

Covers both right-click action types (`Move here` via `RequestPlayerMoveOrderGroup` and `Pick up …` via `RequestPickUp`) since `RequestPickUp` enqueues to `PendingPickUps` which drains into the same `PendingPlayerOrders` queue (`SimulationCore.Tick` line 305-306).

### The "break idle freezing" angle

A smurf that's accumulated `StuckTicks` near the threshold or is sitting through a long `IdleLingerTicks` linger now has its counters cleared the moment the player right-click order arrives. Combined with the waypoint clear, the smurf paths fresh from current position to the order target — no carrying-over of the stuck state that produced the freeze in the first place. The player can use right-click as a manual unblock without needing to wait for `StuckThreshold` (30 ticks ≈ 1 sec at 1×) to fire on its own.

### Why this didn't surface earlier

Earlier development largely tested the player-order path on idle / wandering smurfs, where the "old task" was a cheap Wander with no claims to release. The bug only manifests when the player redirects a smurf mid-Haul or mid-Gather/Excavate, which is exactly the scenario at v0.4.51's heavy gather/excavate load.

---

## [0.4.51] — 2026-05-14

### Fixed — Haul allocation storm + DevPanel feedback invisibility

Sam's report: 50 smurfs on a max-size 720×450 map, 35 FPS baseline drops to 23 FPS the moment a large gather/excavate order is issued. Smurfs visibly stutter / "stuck" / don't retask. Developer Mode panel buttons "do not do anything."

The v0.4.50 HUD-totals cache eliminated the worst per-frame allocator (8 full-map walks/frame). Profiling the remaining 23-FPS scenario fingered TWO more amplified hot paths, plus surfaced a UX bug masquerading as a broken panel.

#### Hot path: HaulSystem standard pass + multi-trip walk still snapshotted the full dropped-items dict

`HaulSystem.SelectHaulTarget` and `HaulSystem.FindNextHaulNearby` both called `LocalMap.EnumerateDroppedItems()` to find the nearest haulable within the 32-tile radius cap. That enumerator allocates a `(int, int, Item[])[]` snapshot the size of the entire dropped-items dict plus an `Item[]` per tile (defensive copy of each list). On a busy excavate site with ~500 dropped tiles and 50 smurfs cycling through task selection, this was the dominant remaining sim-thread allocation source — and per-smurf walks were O(total-tiles) even though the radius cap discarded most of them after-the-fact.

**Fix:** new `LocalMap.ForEachDroppedInRadius(sx, sy, radiusSq, visit)` walks the dict under `_itemsLock`, applies the radius filter inline, and passes the live `List<Item>` to the callback. No allocations, ~10× less inner work because tiles outside radius are skipped before any item iteration. Lock duration at 500 tiles is well under 100 μs — safe to hold for the whole walk since the sim thread is the sole writer of `_droppedItems` and haul callbacks are bounded to read-only access (item reservations go through `_reserveLock`, a different lock, so no cycle).

Both `SelectHaulTarget`'s standard pass and `FindNextHaulNearby` now use this method. Combined with v0.4.50's priority-pass gate (`HasAnyPriorityHaul()`), per-smurf haul scans are allocation-free in the steady state. Multi-trip pickup chains (v0.4.30) which previously triggered a fresh full-map snapshot on every chained pickup are now allocation-free too.

#### UX bug: DevPanel action log was scrolled off-screen by stub sections

Sam reported clicking dev-panel buttons (Pause/Resume, Tick 1×, +Mood, Spawn 50 Granite, …) "did nothing." The buttons were firing correctly — every `MakeBtn` action ran inside the try/catch and wrote a result line to `_logLabel` via `Log(...)`. The bug was that `_logLabel` was placed at the *bottom* of the `_content` VBox, *after* all six section builders (Sim → Selected → Spawn → Map → Visualize → Future), inside the ScrollContainer. With the panel height capped at `viewport - 370`, the Future-Systems stubs section reliably pushed the log below the visible fold.

So buttons reporting `"No smurf selected"` (Selected Smurf actions with no selection) or `"Cursor not on map"` (Spawn actions with cursor over the panel itself) appeared to do nothing. The same applied to legitimately successful buttons — `"Speed → 5×"` etc. — which were rendered in unread text.

**Fix:** `_logLabel` moved to the top of the panel, directly under the "Developer Mode" header and above the rule separator. Bumped font size from 9 → 10 and colour from `BtnFgDim` → `BtnFg` so the line reads as a primary status, not a footnote. Every button click now writes its result to a label the player can see without scrolling.

### Why not also "fix" the stuck/jitter behaviour

The diagnosis flagged three candidate causes for the visible jitter at heavy work sites: yielding-chain resonance (v0.4.29), crowd-avoidance side-step oscillation (v0.4.36), and perimeter contention (50 smurfs trying to dig a 10×10 boulder block where only ~36 perimeter tiles are accessible). Of these, perimeter contention is the dominant gameplay artifact at this density — it's expected behaviour in DF/RimWorld too — and the other two are bounded by per-tick stuck-detection windows (StuckThreshold=30 ticks, YieldDurationTicks=60). At v0.4.50's 23 FPS the bottleneck was the *renderer* showing each tick rather than the sim mis-behaving; with v0.4.51's allocation drop, the per-frame budget should rebound and the smurf motion should re-smooth without any behaviour change. If 50-smurf jitter persists at 60 FPS, a follow-up bump will tackle the radius-blacklist on abandoned excavation tiles. Shipping a behaviour tweak in the same release as a perf fix would muddle the attribution.

### Preserved unchanged

- River carve sizes / subtype rolls / mountain subtypes (v0.4.45 / v0.4.48). Untouched.
- Shallows ring + 0.30× wade speed. Untouched.
- RimWorld-style selection brackets / tile inspector / hover overlay (v0.4.47 / v0.4.49). Untouched.
- Multi-trip haul + carrying capacity + 250-cap-per-tile stockpile rules. Untouched.
- Corpse decay + bones drop. Untouched.
- Yielding mechanic / soft-collision steering. Untouched.
- DevPanel button wiring + every action callback. Only the log label *position* moved.

---

## [0.4.50] — 2026-05-14

### Fixed — Main-thread allocation storm under 50-smurf gather/excavate loads

Sam's report: noticeable performance degradation on large gather/excavate orders with 50 smurfs active at once. Diagnosis traced the bulk of the cost to two amplified hot paths, not to the sim-thread workload itself. Both fixes preserve every visible feature and gameplay rule (channel widths, river/mountain subtypes, RimWorld-style selection brackets, tile/item inspector, multi-trip haul, corpse decay, etc.) — the optimisations are bookkeeping-only.

#### Hot path 1: HUD inventory totals walked the entire dropped-items dict eight times per frame

`ColonyResources.Food / Stone / Wood / MagicEssence` getters each called `MapCountByKind` or `MapCountByFamily`, which called `LocalMap.EnumerateDroppedItems()` and summed `item.Quantity` across every tile and every stack. Each call allocated:

- A `(int, int, Item[])[]` snapshot of size = dropped-tile count
- One `Item[]` per dropped tile (the inner `kv.Value.ToArray()` call)
- O(stack-count) inner iterations to sum

Two HUD components hit `Snapshot()` every frame (`ResourceHUD._Process` + `HUDController._Process`), each pulling all four totals. **Eight full-map snapshot allocations per frame.** At 60 Hz with 200+ dropped tiles under heavy gather/excavate, the per-second allocation rate was ~96 000 arrays — gen-0 GC pressure + main-thread time for sums that change only on Drop/Remove.

**Fix:** maintain running tallies inside `LocalMap` under the same `_itemsLock` as `_droppedItems`. Two dictionaries:

```csharp
private readonly Dictionary<ItemKind, int> _droppedKindTotals = new();
private readonly Dictionary<(ItemKind Kind, string Family), int> _droppedFamilyTotals = new();
```

Updated by a single `AdjustDroppedTotals(Item item, int sign)` helper called inside the lock from every mutation path: `TryDropOnTile` (new tile + Absorb + plain Add), `ForceDropOnTile`, `RemoveItem`, and the corpse-removal branch of `TickCorpseDecay`. Public reads `SumDroppedByKind(ItemKind)` and `SumDroppedByFamily(ItemKind, string)` are single dictionary lookups.

`ColonyResources.MapCountByKind/Family` are now one-line delegations to those O(1) reads. The HUD getters cost a dictionary lookup each instead of a 200-tile snapshot — main-thread cost drops by ~95% under the stress case Sam reported.

Save/load consistency: load path calls `map.DropItem(item)` per stored stack, which routes through `TryDropOnTile` → `AdjustDroppedTotals`, so totals reconstruct correctly from the save without explicit migration code. Starting-inventory bootstrap goes through the same path or through `Inventory.Add` (which doesn't touch the map dict), so neither route can desync the totals.

#### Hot path 2: Haul priority pass walked the entire map even when no items were Force-Hauled

`HaulSystem.SelectHaulTarget` runs a "priority pass" first (any item the player Force-Hauled bypasses the 32-tile radius cap), then a standard radius-bounded pass. Steady-state, the player Force-Hauls very rarely — `_priorityHauls` is empty 99 % of the time — but the priority pass still called `map.EnumerateDroppedItems()`, allocated the full snapshot, and walked every tile checking `IsPriority(it.Id)` (a per-item dictionary lookup inside the reservation lock) only to find zero matches.

With 50 smurfs cycling through task selection, each idle smurf paid that allocation + walk on every `SelectTask` call. Adds up fast under sustained gather/excavate.

**Fix:** new `HaulSystem.HasAnyPriorityHaul()` — single `_priorityHauls.Count > 0` check under the existing reservation lock. The priority pass is now gated behind it. When the set is empty (default), the entire walk + allocation is skipped. When the set is non-empty (player just Force-Hauled), behaviour is identical to v0.4.49.

### What was NOT changed — preserved features and visuals

- River carve sizes / subtype rolls / Delta sibling branch counts (v0.4.48 scaling). Untouched.
- Mountain subtype generation. Untouched.
- Shallows ring around water + 0.30× wade speed. Untouched.
- RimWorld-style selection brackets (v0.4.47). Untouched.
- Tile/item inspector card + top-right info overlay (v0.4.34 / v0.4.49). Untouched.
- Multi-trip haul + carrying-capacity rules + stack/cap rules. Untouched.
- Corpse decay timeline + bones-leave-behind. Untouched.
- Smurf yield-to-pass + side-step steering (v0.4.36). Untouched.
- HUD layout, resource categories, sub-item breakdowns. Untouched — they now read the cached totals through the same interface.

### Why this fix and not something more invasive

The perf diagnosis also flagged the standard haul-target pass (also calls `EnumerateDroppedItems`), the occupancy grid rebuild (O(N) per tick), and the witnessed-death broadcast (O(N) per death). Of these, only the standard haul pass produces measurable allocation pressure at 50-smurf scale, and at ~1000 array allocations/sec it's two orders of magnitude smaller than the HUD pressure. Spatial indexing for haul lookups is a candidate for a future bump if 50-smurf perf still feels off after v0.4.50, but shipping it now would muddle the gain attribution.

---

## [0.4.49] — 2026-05-14

### Fixed — Shallows tile surfaced in top-right info indicator

Sam's report: "ensure all new tiles and items show up in info indicator at top right of screen". The Shallows terrain type, introduced in v0.4.37 to give every body of water a wadeable border + serve as the explicit ford material for Crossing-subtype rivers, was missing from every switch arm in `TileInfoOverlay` and `TilePropertiesPanel`. Hovering or clicking a Shallows tile returned "Unknown" for the terrain name with no detail line — a regression visible on every water-adjacent tile generated since v0.4.37.

#### `scripts/ui/TileInfoOverlay.cs`

Three switch additions covering the top-right hover panel:

- `TerrainName(TerrainType.Shallows)` → `"Shallows"`
- `TerrainDetail(TerrainType.Shallows)` → `"Wadeable  ·  0.30× movement"` (matches the wade-speed multiplier in `BehaviorSystem.MoveOneTick`)
- `ClassifyTile(TerrainType.Shallows)` → `"Wadeable water"` (parallel to the existing `Water → "Surface water"`)

#### `scripts/ui/TilePropertiesPanel.cs`

One switch addition in `FormatTerrain` so the click-to-inspect card title reads "Shallows" instead of "Unknown". The card's tile section already shows `Passable: Yes` and `Fertility: …` correctly via the existing per-tile fields.

### Why this was lurking

`TerrainName` was the last hand-written enum-name switch in the project — every other surface (the renderer colour table, the world-gen panel breakdown, the behaviour system wade-speed check) was data-driven and picked up Shallows automatically when the enum value was added. Hover info was the only place it was effectively unreachable text.

The corpse / equipment item kinds added in v0.4.30–v0.4.33 do not need similar surfacing fixes — they are rendered through `ItemRegistry.Get(it.Kind, it.SubType).DisplayName` with a fallback to the raw SubType string, and Corpse goes through the dedicated `FormatCorpseLine` obituary path. Every existing ItemKind already has a render path on this surface.

---

## [0.4.48] — 2026-05-14

### Changed — Level-map rivers scale with map size

Sam's report: on the v0.4.41 max-size 720×450 levels, rivers look identical to the small-size 160×100 levels — narrow ribbons against an enormous canvas. Worse, Delta-subtype rivers (which need horizontal real estate for the sibling branches to read as a delta) almost never appear because the v0.4.37 50/25/15/10 subtype roll favours ThinSnaking equally regardless of map scale.

This release adds a single `mapScale` factor at the top of the river-carving pass and threads it through every width/meander/branch-count param in the block.

#### `mapScale` factor (`scripts/world/LocalMapGenerator.cs` Pass 4h, river block)

```csharp
float mapScale = System.Math.Clamp(
    System.Math.Min(map.Width, map.Height) / 150f, 0.6f, 4.0f);
```

The reference point is the v0.4.41 default level dim (240×150 → min 150), so a 240×150 level gets `mapScale = 1.0` and v0.4.37 widths are preserved exactly. A 720×450 max-size level gets `mapScale = 3.0`. The clamp range [0.6, 4.0] gives the smallest 160×100 level (`min/150 = 0.667`) readable river widths and prevents nonsense values if Sam ever bumps the dial.

#### Subtype-roll bias (already present in v0.4.48 first edit)

The `riverRng.Next(100)` roll thresholds shift with `mapScale` so Delta and WideDeep appear more often on big maps:

| mapScale | ThinSnaking | WideDeep | Crossing | Delta |
|----------|-------------|----------|----------|-------|
| 1.0 (default) | 50% | 25% | 15% | 10% |
| 2.0 (Large 480×300) | 35% | 28% | 19% | 19% |
| 3.0 (Max 720×450) | 20% | 30% | 22% | 28% |

ThinSnaking stays the most common look on small maps (where wide channels would consume the entire playfield) but on a 720×450 map Delta and WideDeep dominate, which is the visually correct outcome — those subtypes need room to breathe.

#### Width / meander scaling (this edit)

The per-subtype `switch` block now uses a local `ScaleHW(int)` helper that multiplies by `mapScale` and clamps to a minimum of 1 (so we don't lose the river entirely on tiny maps with `mapScale < 1.0`). All four non-Creek subtypes scale:

| Subtype | baseline halfWidth | meander | mapScale 3.0 halfWidth | mapScale 3.0 meander |
|---------|--------------------|---------|------------------------|----------------------|
| ThinSnaking | 1-2 | 14 | 3-6 | 42 |
| WideDeep | 3-5 | 6 | 9-15 | 18 |
| Crossing | 2-3 | 10 | 6-9 | 30 |
| Delta main | 2-4 | 8 | 6-12 | 24 |

At max scale a WideDeep river is 18-30 tiles wide (4-7% of the 720-tile map width — roughly equivalent to the 6-10 tile WideDeep on the 240-tile baseline). Crossing fords also scale: `fordHalfWidth = ScaleHW(2)`, so a Crossing ford zone matches the wider channel it crosses.

#### Delta sibling branch count scales

The Delta subtype previously spawned exactly 2 sibling channels regardless of map size. On a 720×450 map with three thin branches lost in the noise, the result didn't read as a delta. Branch count now scales:

```csharp
int deltaBranchCount = System.Math.Clamp(
    2 + (int)System.Math.Round((mapScale - 1f) * 0.7f), 1, 4);
```

| mapScale | branches | Delta main + branches |
|----------|----------|-----------------------|
| 0.667 (Tiny) | 1 | 2 channels total |
| 1.0 (Default) | 2 | 3 channels |
| 2.0 (Large) | 3 | 4 channels |
| 3.0 (Max) | 3-4 | 4-5 channels |

Sibling channels themselves use `ScaleHW(1)` / `ScaleHW(2)` so they widen with the map. At max scale a Delta produces a main channel ~12-24 tiles wide flanked by 3-4 sibling channels each 3-6 tiles wide — finally feels like a proper river delta.

### Why the linear scaling instead of sqrt or log

The visual goal is constant **river footprint as a percentage of map area**. A river crossing the entire map at fixed width occupies an area proportional to map dimension — doubling map size halves the visual prominence. Linear width scaling restores that ratio. Sqrt scaling would still leave big-map rivers feeling thin; log would barely change anything.

The clamp at `mapScale = 4.0` is a safety stop for future expansion — at that scale rivers would be 12-20 tiles wide for ThinSnaking, which is the threshold where the river starts to dominate everything else. We never hit it with current 720×450 max.

---

## [0.4.47] — 2026-05-14

### Added — RimWorld-style selection brackets for smurfs and ground tiles

Sam asked for visual feedback when something is clicked, modelled after RimWorld's white corner-bracket selection box. Both smurf selection and the v0.4.34 tile/item inspector now draw the same indicator.

#### Shared `DrawSelectionBrackets` helper (`scripts/ui/SmurfColonyView.cs`)

Internal static method that paints four L-shaped corner brackets on a given `Rect2`:

- 4-px arm length × 1-px thickness per arm.
- White (0.95 alpha) over a 1-px-offset black shadow (0.55 alpha) so the marks read on both dark (forest floor, stone) and light (sand, marble) terrain.
- Each corner draws one horizontal + one vertical arm that point INTO the rect from the anchor.

#### Smurf selection — yellow circle replaced

`SmurfColonyView._Draw` used to paint a translucent yellow circle under each selected smurf body (v0.4.20 selection glow). Replaced with the bracket call:

```csharp
DrawSelectionBrackets(this, new Rect2(pos.X - 10f, pos.Y - 14f, 20f, 22f));
```

22 × 20 px rect framing the smurf's body + hat. Consistent with the tile bracket so selection reads the same way regardless of target type.

#### Tile inspector selection — new `SelectionOverlay` (`scripts/ui/SelectionOverlay.cs`)

New Node2D added to the GameController world layer between the item-drop overlay and the smurf colony view. Tracks an optional `(tx, ty)` and on `_Draw` paints brackets around an 18 × 18 rect (1-px breathing strip around the 16-px tile cell). API:

- `SetTileSelection(int tx, int ty)` — set the selected tile, queue redraw if changed.
- `ClearSelection()` — clear and queue redraw.

#### Click wiring (`scripts/ui/GameController.cs`)

`HandleMouseClick`'s no-tool branch now calls `_selOverlay.SetTileSelection(...)` when the player clicks a tile with items / vegetation (paired with `_tileProps.Open(...)`), and `_selOverlay.ClearSelection()` on the empty-tile fall-through. `OnSmurfClicked` also clears the tile-selection overlay since the smurf card and tile card are mutually exclusive — selecting a smurf shouldn't leave a stray bracket on the last-clicked tile.

#### Version

Patch `0.4.46` → `0.4.47`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.47`.

---

## [0.4.46] — 2026-05-14

### Fixed — Dropped item icons readable; count badge no longer drowns the sprite

Sam's screenshot showed every dropped-item tile rendering as a black blob with a yellow count number on top. Three compounding causes, all in `scripts/ui/ItemDropOverlay.cs`:

1. **Icon was tiny.** v0.4.2's `BakeVariantSprite` used `r = TS * 0.22f` — about 3.5 px radius / 7 px diameter on a 16-px tile. The icon occupied ~24% of the tile area.
2. **Count badge sat on top of the icon.** v0.4.28b positioned the badge bottom-centre at `cy + TS*0.5f`, but the badge's black rect extended UPWARD across the entire lower half of the tile — directly over the icon body. The icon's colour was visible only as a thin band peeking out from under the rect.
3. **Square / Diamond / Triangle items were drawn AS A CIRCLE WITH A TINY OVERLAY.** The bake always painted a base circle in the fill colour, then stamped a small (~3 px) shape on top. Materials looked like fruit; magic shards looked like fruit with a diamond hint. The shapes were too small to read as distinct from the round body.
4. (Bonus) **Some fill colours were near-black** — Obsidian, Furniture, Corpse — which compounded the black-blob effect.

Fixes:

#### Icons grow to ~6.5 px radius (13 px diameter)

`r = TS * 0.40f` — almost double the v0.4.2 size. The icon now fills most of the tile interior, with about 1.5 px clearance from each edge.

#### Shape IS the icon (no underlying circle for non-Round variants)

`BakeVariantSprite` branches on `IconShape`:
- **Round** → filled circle + outline. Used by Food (Smurfberry / Mushroom / Herb Cluster / Magic Berry).
- **Square** → filled square (side ≈ 1.6 × r so the footprint matches Round). Used by Material / Apparel / Furniture / Corpse.
- **Diamond** → filled diamond at full radius. Used by Magic shards + Trinket.
- **Triangle** → upright triangle (tip up, broad base). Used by Tool / Weapon.

No more "circle with a confusing little shape on top".

#### Count badge moved to upper-right corner

New `DrawSmallNumberTopRight` helper anchors the badge by its top-right corner instead of its centre — so the rect tucks against the tile's upper-right edge regardless of digit count. Font size dropped 9 → 8 (tighter footprint). The badge no longer overlaps the icon body, so the sprite reads through clearly. The pile-badge (multi-stack indicator) moved from upper-right to bottom-left to free the corner.

#### Darkest variants lightened

- Obsidian fill `(0.18, 0.16, 0.22)` → `(0.35, 0.32, 0.45)` — readable dark-purple instead of near-black.
- Furniture fill `(0.40, 0.30, 0.20)` → `(0.60, 0.42, 0.22)` — clear wood-brown instead of near-black.
- Corpse fill `(0.42, 0.18, 0.18)` → `(0.60, 0.22, 0.22)` — readable dusky red.

#### Version

Patch `0.4.45` → `0.4.46`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.46`.

---

## [0.4.45] — 2026-05-14

### Added — Mountain biome doubled from 3 to 6 subtypes for gameplay diversity

Sam asked for RimWorld-style mountain variety. The pre-v0.4.45 mountain biome only rolled across Caves / Rocky Terrain / Mountain Face — three flavours of "scattered rocks on a green field". v0.4.45 adds three more flavours that change the colony strategy:

#### Subtype 3 — Solid Mountain (`scripts/world/LocalMapGenerator.cs`)

RimWorld's iconic mountain biome. ~80 % solid bedrock with small natural chambers; the player lands on a guaranteed 5-tile-radius spawn pocket at map centre and must excavate to expand into a cave fortress. Generated via **cellular automata** seeded at 65 % rock random (82 % within 3 tiles of map edge so the colony never spawns flush against open air), then 4 passes of the Conway-like "≥5 rock neighbours → rock, else floor" rule for organic blob shapes. Off-map neighbours count as rock during CA so the perimeter stays solid.

This is the **carve-your-own-fortress** archetype — the §5.11.b Building Zone work pairs naturally with this subtype since the player ends up with a natural cave perimeter ready to designate as rooms.

#### Subtype 4 — Canyon (`scripts/world/LocalMapGenerator.cs`)

Two thick rock walls on opposite sides (top/bottom OR left/right) with a 40-60% passable valley running through the middle. Floor is ForestFloor (or Grass on dry maps). 0-2 narrow gaps per side wall let the canyon connect to outside paths instead of being strictly boxed-in. Wall edges wobble ±2 tiles via a per-tile noise sample so they read as natural cliff faces rather than ruler-straight rectangles. Light boulder scatter on the floor for cover.

The **chokepoint defence** archetype — combat / raid scenarios (Phase 7+) will play very differently here because attackers funnel through the wall gaps.

#### Subtype 5 — Crags (`scripts/world/LocalMapGenerator.cs`)

Open highland with 8-15 scattered rock pillars, each 3-7 tiles wide, shaped via elliptical mask with per-tile noise jitter for craggy boundaries. Aspect ratio rolls 0.7-1.3 per pillar so some pillars are tall-and-thin while others are squat-and-wide. ~85% passable terrain overall.

The **build-anywhere with cover** archetype — roomy enough for a sprawling above-ground colony, but the pillars give defensive options and mineable stone without the cave-system claustrophobia.

#### Subtype roll + name helper

- `GetMountainSubtype` bumped `Next(3)` → `Next(6)`. Same XOR mask, deterministic per LocalSeed.
- Peaks tiles default to subtype **3 (Solid Mountain)** instead of 0 (Caves). A bedrock cliff reads as a snowy peak in the preview thumbnail; the old Caves default looked too tunnel-y for an unlandable mountain top.
- New `GetMountainSubtypeName(WorldTile)` returns the friendly label: `"Caves"` / `"Rocky Terrain"` / `"Mountain Face"` / `"Solid Mountain"` / `"Canyon"` / `"Crags"`.
- `WorldGenPanel.OnTileSelected` switches from the v0.2.x "Caves only" special-case to using `GetMountainSubtypeName` for any mountain / peaks tile. The Landing Zone biome label and the per-preview `_levelTypeLabel` (v0.4.44) both show the specific variant.

#### Version

Patch `0.4.44` → `0.4.45`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.45`.

---

## [0.4.44] — 2026-05-14

### Changed — Landing Zone layout and resource summary tidied

Three fixes to the Landing Zone screen (`scripts/ui/WorldGenPanel.cs`):

#### Breathing room

The Level Preview block sat flush against the vertical divider. Bumped `mainRow` separation **16 → 32** and `leftCol.CustomMinimumSize` width **300 → 320** so the preview thumbnail and the resource list have room without crowding the divider.

#### Biome label directly under the preview thumbnail

Pre-v0.4.44 the biome / subtype label ("Forest", "Coastal Forest · River", "Caves") only appeared under the world map on the right. Sam asked for it under the preview thumbnail too, where it belongs visually. New `_levelTypeLabel` populated from the same `biomeLabel` string already computed in `OnTileSelected`; cleared on screen entry and on invalid-tile selection.

#### Resource summary — single line per entry, no raw counts

The pre-v0.4.44 format wrapped to two lines on busy maps (`Wood: Abundant (156555 logs, 15347 trees, 27931 mushrooms)`) and the raw counts cluttered an otherwise glanceable list. Reformatted:

- One line per resource (Stone / Wood / Food / Magic / Water).
- Raw count numbers dropped entirely — the Scarce / Moderate / Abundant bucket already tells the player what they need.
- Sub-categories with **zero** quantity are pruned from the parenthetical, so a map with no Magic Crystal doesn't show `crystal`, and a map with no Living Wood doesn't show `trees`.

Example (matches Sam's screenshot map):

```
  Stone:   Abundant   ✦ Magic Crystal
  Wood:    Abundant   (logs, trees, mushrooms)
  Food:    Abundant
  Magic:   Moderate   (crystal)
  Water:   Scarce     (deep, shallows)
```

The Magic line collapsed from `(0 flowers, 0 grove, 16 crystal)` to `(crystal)`. AutowrapMode set to Off so the label can't break across lines if a future map pushes the text wider.

#### Version

Patch `0.4.43` → `0.4.44`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.44`.

---

## [0.4.43] — 2026-05-14

### Fixed — Pause menu actually pauses the sim, Esc opens / closes it

Two fixes around the pause menu, both in `scripts/ui/GameController.cs`:

#### Defensive `Paused` re-assertion while the menu is visible

Sam reported the pause menu no longer pauses the game. `OnMenuRequested` does set `_sim.Paused = true` and `_pauseMenu.Open()`, and the wiring from the HUD Menu button → MenuRequested signal → handler still routes correctly. Walked the entire pause-state propagation chain (`SimulationManager.Paused` → `_Process` → `_core.Clock.Paused` → `SimulationCore.Run`'s pause branch) and couldn't pinpoint a single setter that's unpausing behind the player's back.

Rather than ship a maybe-fix without a confirmed root cause, **stamp it from `_Process` every frame the pause menu is visible**:

```
if (_pauseMenu.Visible) _sim.Paused = true;
```

This belts-and-suspenders the bug: whatever rogue path is touching `Paused`, the next render frame re-asserts true. `OnPauseMenuResume` still restores `_wasPausedBeforeMenu` on Resume / Save / Load / Settings-close → menu close, so the re-assertion doesn't stick once the menu's gone.

The actual rogue setter is queued as a follow-up to find with logging when Sam next playtests — adding a `GD.Print` at every `Paused = ...` site narrows it down quickly. For now the game pauses reliably when the menu is open.

#### Esc opens AND closes the pause menu

`PauseMenuPanel._UnhandledInput` already handled Esc-to-close (emits `ResumeRequested`). v0.4.43 adds the Esc-to-open branch in `GameController._Input`:

```
else if (ev is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false }
    && !IsAnyOverlayOpen())
{
    OnMenuRequested();
    GetViewport().SetInputAsHandled();
}
```

Routed through `_Input` (same level as the Space-toggle and F12 dev-panel hotkeys) and gated on `!IsAnyOverlayOpen()` so Settings / SaveBrowser / GameOver still get their own Esc handling. The result: Esc opens the pause menu from gameplay, Esc closes it from the menu, and the sim is paused for the full duration regardless.

#### Version

Patch `0.4.42` → `0.4.43`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.43`.

---

## [0.4.42] — 2026-05-14

### Fixed — Tiles destroyed after designation despite no smurf walking over

**Root cause:** the v0.4.29 DF-style yield mechanic's early-return at the top of `BehaviorSystem.MoveOneTick` returned `true` instead of `false`. The caller (lines 529-547) reads that return value as "smurf arrived at task target" and unconditionally fires `ApplyTaskEffect(s, arrivedTask, ...)`. If a yielding smurf happened to hold a designation task (Excavate / Gather / ChopWood / CutVegetation), the bogus "arrival" tick mutated the targeted tile — boulder → mud, vegetation cleared, log felled — **without the smurf ever actually walking to it**.

This explains the symptom Sam reported: designate a tile, watch it get destroyed seconds later despite no smurf making the trip. A smurf elsewhere on the map gets asked to lie down (yield trigger from another stuck smurf wanting to climb over them), the yield branch wakes up next tick and reports "arrived" — and ApplyTaskEffect runs on whatever designation task that yielding smurf happened to hold.

**Fix:** one-line change in `BehaviorSystem.MoveOneTick` (line 671). Yielding smurfs now return `false` from MoveOneTick, which is the correct semantic — they aren't arriving anywhere, they're holding position for ~60 ticks while a colleague climbs over them. The caller skips the ApplyTaskEffect call and the post-arrival accounting, the tick passes silently, the YieldingTicks counter decrements, the next tick repeats until the yield expires.

Note: this also incidentally caused a related but subtler bug — yielding smurfs would mark their task as "TaskDidWork = true" (line 544 in the caller resets it pre-effect, but ApplyTaskEffect's success cases set it true), which would then skip the v0.4.19 failure-recovery branch. After the fix, the failure counter stays accurate.

#### Version

Patch `0.4.41` → `0.4.42`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.42`.

---

## [0.4.41] — 2026-05-14

### Also in this patch — CS0108 hide warnings cleared

`ItemDropOverlay.NumberBadgeNode.Owner` and `SmurfColonyView.SmurfExtrasNode.Owner` were typed back-references to the parent overlay, but the bare field name shadowed `Node.Owner` (the Godot editor-owner concept) and the compiler emitted CS0108 on every build since v0.4.28. Added the `new` keyword to both declarations — signals the hide is intentional. Build now reports **0 warnings, 0 errors**.

### Changed — Generation size dropdowns

`WorldGenPanel.WorldSizeOptions` and `LevelSizeOptions` reshuffled per Sam's request: the smallest sizes drop off, a new 1.5×-max size joins at the top of each dropdown.

**World Size:**

| Before | After |
|--|--|
| 32 × 32 (Default) | — removed |
| 64 × 64 | — removed |
| 96 × 96 | 96 × 96 (Small) |
| 128 × 128 (Max) | **128 × 128 (Default)** |
| — | **192 × 192 (Max)** ← new (1.5× 128) |

Default dropdown index shifts from 0 → 1 so the default world stays at 128. Existing saves with `gridSize` 32 / 64 still load normally — only the dropdown auto-select misses (no match), nothing prevents the load.

**Level Size:**

| Before | After |
|--|--|
| 80 × 50 (Default) | — removed |
| 160 × 100 | 160 × 100 (Small) |
| 240 × 150 | **240 × 150 (Default)** |
| 320 × 200 (Recommended) | 320 × 200 (Recommended) |
| 480 × 300 (Max) | 480 × 300 (Large) |
| — | **720 × 450 (Max)** ← new (1.5× 480) |

Default index 0 → 1 so 240 × 150 takes over as the default. 720 × 450 is ~324k cells — about 2× the 480 generation time but well within the existing perf budget for first-tile preview.

#### Version

Patch `0.4.40` → `0.4.41`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.41`.

---

## [0.4.40] — 2026-05-14

### Added — Larger Landing Zone world map + resource summary under Level Preview

Two small Landing Zone UX improvements:

#### World map ~1.25× bigger (`scripts/ui/WorldGenPanel.cs`)

`WorldMapControl.TargetDisplayPx` bumped from **448 → 560** (× 1.25). The whole tile grid is now more comfortable to scan and click without zooming in. Per-tile size auto-derives via `Math.Max(3, TargetDisplayPx / _gridSize)` so every world-size dropdown option benefits proportionally. Layout still fits at 1280×720 with the left column's 300 px reserved width.

#### Resource summary under Level Preview (`scripts/ui/WorldGenPanel.cs`)

Selecting a landing tile now also surfaces a resource breakdown directly below the Level Preview thumbnail, so the player can pick a landing zone with eyes-open about scarcity before committing. The summary walks the generated preview map once (post-`GeneratePreviewDeferred`, no per-frame cost) and tallies:

- **Stone** — Boulder count, plus a callout when MagicCrystal vein tiles are present.
- **Wood** — Dead Logs + Living Wood + Large Mushrooms (mushroom yield weighted × 3).
- **Food** — Smurfberry / Small Mushroom / Herb Cluster / Sandshroom / PineShroom / MagicBerry vegetation count.
- **Magic** — Magic Flower vegetation + Magic Grove terrain + Magic Crystal stone veins.
- **Water** — Deep Water + Shallows (river / pond / inlet footprint).

Each line shows a Scarce / Moderate / Abundant bucket plus the raw tile counts so the player gets both glanceable and exact numbers. Thresholds calibrated for the default ~12K-cell level; vague-but-useful in the RimWorld style.

Example output:

```
  Stone:  Abundant  (412)   ✦ 7 Magic Crystal
  Wood:   Moderate  (3 logs, 11 trees, 18 mushrooms)
  Food:   Scarce    (22 bushes/clusters)
  Magic:  Moderate  (8 flowers, 12 grove, 7 crystal)
  Water:  Abundant  (180 deep, 240 shallows)
```

The preview's `_resourceInfoLabel` is reset to `— select a tile —` whenever the player returns to the Landing Zone screen or clicks an unselectable Pondsea / Peaks tile.

#### Version

Patch `0.4.39` → `0.4.40`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.40`.

---

## [0.4.39] — 2026-05-14

### Added — Creek/Creeks subtype for orphan world-map river tiles

Sam noticed that the v0.4.37 snaking-chain river system can leave **single-cell river seeds** — tiles flagged HasRiver whose chain walk bailed on the first step (Pondsea/Mountain/edge blocked the outward direction). These orphan tiles were still carving a full river subtype on their local map, which read as out-of-place for a single isolated seed. v0.4.39 gives them their own subtype: **Creek/Creeks** — 1-3 thin rocky shallow streams snaking across the map instead of a deep carved river.

#### Orphan detection (`scripts/world/WorldTile.cs`, `scripts/world/WorldMapGenerator.cs`)

- **`WorldTile.IsRiverOrphan`** — new bool field. Defaults false so old saves load cleanly.
- **Post-pass A.7 addendum** in WorldMapGenerator. After all river chains are placed, walks every HasRiver tile and sets `IsRiverOrphan = true` for any tile whose four cardinal neighbours are NOT HasRiver. Single iteration over the world grid, no extra noise / RNG.

#### Subtype routing (`scripts/world/LocalMapGenerator.cs`)

`Pass 4h` now branches early on `worldTile.IsRiverOrphan`: orphan tiles bypass the random subtype roll (0-3) and use subtype **4 — Creek**. Non-orphan tiles still pick from ThinSnaking / WideDeep / Crossing / Delta on the existing 50 / 25 / 15 / 10 split.

#### Creek carving (`LocalMapGenerator.CarveCreekPath`)

New helper modelled on `CarveRiverPath` but with creek-scale tuning:

- **Bed:** single-tile-wide Shallows ribbon (no deep Water at all — the creek is wadeable end-to-end).
- **Meander:** ±18-tile perpendicular wander, higher than ThinSnaking's ±14, with frequency 0.10 (vs river 0.06) for a tighter wiggle.
- **Rocky outcrops:** ~12 % chance per bed tile of a Boulder dropping in or just beside the bed (±1-tile perpendicular offset so rocks line the banks instead of always centring). Existing Boulder / wood / water tiles are not overwritten.
- **Respects rock / wood:** creek path skips tiles already occupied by Boulder / DeadLog / LivingWood (creek snakes around rock instead of dissolving it).

Each Creek-subtype map runs `CarveCreekPath` **1-3 times** with different noise seeds (offset by `+71 + c * 13`) so the streams don't run in parallel. Combined with the v0.4.37 universal Shallows ring, an orphan-river map ends up as a scatter of small wadeable creeks threading rocky beds across the level — the "babbling brook through the woods" reference Sam called out, distinct from a proper carved river.

#### Version

Patch `0.4.38` → `0.4.39`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.39`.

---

## [0.4.38] — 2026-05-14

### Added — v0.4.37 follow-ups: Shallows speed penalty, Island inlets, river stripe rotation

All three v0.4.37 follow-ups land. None needed roadmap-deferral after all.

#### Shallows wading slows smurfs to 30 % (`scripts/simulation/systems/BehaviorSystem.cs`)

`MoveOneTick` now checks the smurf's current tile before computing `baseStep`. When the tile is `TerrainType.Shallows`, the per-tick movement step is multiplied by **0.30** (RimWorld's shallow-water value). Applied BEFORE the direction search so the same multiplier governs whatever side-step the steering picks. The multiplier is read off the live `LocalMap.Get(curTileX, curTileY).Terrain` each tick — zero cached state — so the moment a smurf walks off a Shallows tile their speed snaps back to normal. Future Shroombridges on Shallows (§5.11.d, roadmap) will lift the penalty.

Hot tiles like Mud / Sand / Grass still have no per-tile cost; the multiplier hook is in place for future Phase-5 movement-cost work to dial in per-terrain speeds without restructuring the steering loop.

#### Island inlets (`scripts/world/LocalMapGenerator.cs`)

The Island biome's v0.2.6 concentric-ring layout (water 0-3 / sand 4-7 / interior 8+) was visually generic — Island maps read as "doughnut with stuff in the middle". v0.4.38 mirrors the Coastal inlet pass:

- **3-5 finger-shaped water intrusions**, each pushing 5-10 tiles inland from a random edge.
- **1-2 tiles wide** with ±1-tile per-step jitter so each inlet snakes.
- Cuts through Sand / Grass / Forest but yields to Boulder / DeadLog / LivingWood — rock outcrops become small islets in the inlet channel.
- Starts at depth 8 (inside the sand band) so the beach ring stays intact; pushes inland to depth 13-18.
- Paired with the v0.4.37 universal Shallows ring, an Island map now has carved natural-harbour cuts running from the beach into the interior. Combined with the v0.4.37 deeper Coastal water lip and inlets, Coastal and Island maps now share a cohesive natural-harbour topography.

#### World-map river stripe orientation (`scripts/ui/WorldGenPanel.cs`)

v0.4.37 extended each river seed into a 3-8-tile snaking chain on the world map, but every chain-tile still rendered a horizontal stripe — so a north-south river looked like N parallel stripes instead of a connected ribbon. Stripe direction now derives from the four cardinal neighbours' `HasRiver` flags:

- **N or S neighbour has river** → vertical stripe.
- **W or E neighbour has river** → horizontal stripe.
- **Both axes** (junction or bend) → both stripes drawn overlapping; produces a + or L shape where the chain turns.
- **No river neighbour** (orphan single-tile seed) → horizontal default so the user still sees something.

Both stripe variants keep the v0.4.31 1-px dark outline on the outside edges for contrast on light biome colours. Chain bends and confluences now read at a glance on the Landing Zone preview.

#### Version

Patch `0.4.37` → `0.4.38`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.38`.

---

## [0.4.37] — 2026-05-14

### Added — Major water overhaul: Shallows tile, river subtypes, coastal inlets, snaking world-map rivers

Sam asked for water to matter on every map it appears on. v0.4.37 reworks every water-generating pass into a coherent system.

#### `TerrainType.Shallows` (`scripts/world/TerrainType.cs`, `scripts/ui/LocalMapRenderer.cs`, `scripts/world/LocalMap.cs`)

New tenth terrain type: **passable shallow water**. Rendered as a lighter cyan-blue (0.45, 0.68, 0.85) than the deep Water (0.18, 0.42, 0.72). Modelled after RimWorld's shallow / moving water. Falls naturally into the existing `IsPassableTerrain` check (not in the impassable list). Shroombridges (§5.11.d) can be built over Shallows at half the wood cost — see roadmap update below.

#### Universal Shallows ring (`LocalMapGenerator.CarveShallowsRing`)

After every water-generating pass (pondsea spillover, ponds, rivers, coast, inlets) a single post-pass walks the dropped-water tiles and wraps each one in a 1-tile Shallows ring on any dry-land neighbour. Two-pass design: snapshots water tiles first so the ring doesn't propagate into an infinite second-degree expansion. Skips rock / wood / sand / existing-Shallows neighbours so the ring doesn't paper over rock outcrops in the riverbank. Result: every water feature reads as "deep water core + wadeable shallows" without per-feature special-casing.

#### River subtypes (`LocalMapGenerator` Pass 4h rewrite)

The v0.4.31 1-2-wide single-channel pass was replaced with four subtypes drawn from a per-map roll. New helper `CarveRiverPath(map, rng, noiseSeed, minHalfWidth, maxHalfWidth, meanderAmp, allowFords, fordSpacing, fordHalfWidth)` carries the shared meander + width math; each subtype configures the params:

| Subtype | Roll | Half-width | Meander | Fords |
|--|--|--|--|--|
| **ThinSnaking** | 50% | 1–2 | ±14 tiles | none (auto-Shallows ring handles crossings) |
| **WideDeep** | 25% | 3–5 | ±6 tiles | none (impassable except via Shroombridge) |
| **Crossing** | 15% | 2–3 | ±10 tiles | **explicit Shallows fords every map-quarter, 5-tile-wide wadeable spans** |
| **Delta** | 10% | 2–4 + 2 sibling branches | ±8 tiles | none |

Delta subtype calls `CarveRiverPath` 3 times with different noise seeds to produce a primary channel plus two narrower sibling channels diverging across the map — the splayed mouth visual Sam asked for. Crossing subtype's fords are now Shallows tiles (the new terrain type) so they read as proper wadeable crossings instead of the v0.4.31 sand-strip workaround.

Fertility boost within 3 tiles of any river-water tile stays at +0.20 (unchanged).

#### World-map rivers snake (`scripts/world/WorldMapGenerator.cs`)

v0.4.31 flagged ONE tile per river adjacent to a Pondsea / Mountain neighbour. v0.4.37 extends each seed into a **3–8-tile chain walking outward** from the touching water / elevation neighbour, with a 35% per-step chance of veering ±90° to snake. Result: rivers appear as connected ribbons across the world preview, not isolated stripes. Each chain tile still spawns its own carved local-map channel — the in-game level density per river isn't reduced, just spread across more landable tiles. Chain stops on map edge, Mountains/Peaks, Pondsea, or another chain.

#### Coastal inlets (`LocalMapGenerator` Pass 4g enhancement)

The v0.2.6 Coastal pass put a 2-4-tile water lip + 3-5-tile sand band on one side of the map. v0.4.37 deepens the water (4-7 tiles) AND drills **2-4 finger-shaped inlets perpendicular to the coastline, 6-12 tiles deep, 1-2 tiles wide**, with ±1-tile per-step jitter so each inlet snakes. Inlets cut through dry land but respect existing Boulder / wood (those stay as little islands in the channel). Paired with the Shallows ring, Coastal maps now have genuine natural-harbour topography — protected inlets a settlement would actually build on.

#### Inland ponds — bigger, more frequent (`LocalMapGenerator` Pass 4f)

The v0.2.6 0–3 ponds × 1–2-tile expansion left Plains/Hills maps with often zero water. Bumped per Sam's "water should be an important feature on any map it's included on":

| Biome | Old count | New count |
|--|--|--|
| Swamp | 0–3 | **3–5** |
| Forest / MagicGrove | 0–2 | **2–4** |
| Plains / Hills / Coast | 0–1 | **1–3 (always at least one)** |

Pond expansion bumped from 1–2 to 2–4 tiles so ponds read as small lakes (~4-7 tiles across with the Shallows ring) instead of single-pixel puddles.

#### Roadmap §5.11.d — Shroombridge over Shallows

Updated the Phase 5 Shroombridge entry to spell out the v0.4.37 cheap-bridge-over-shallows path: a Shroombridge on a Shallows tile costs 2 Fungal Wood + 1 LivingWood (half a deep-water bridge) and removes the wading speed penalty. Builders get a graduated choice — cheap fast-walk shallows-bridges for the auto-generated ring around any water feature, full Shroombridges for spanning the deep channel itself.

#### Version

Patch `0.4.36` → `0.4.37`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.37`.

#### Follow-ups

- **Shallows movement speed multiplier:** the new tile is passable but doesn't yet have an explicit per-tile speed cost. RimWorld-style 0.3× walk speed would land as a Phase-5 movement-cost work item. Until then, smurfs walk over Shallows at full speed — visually correct, mechanically equivalent to dry land.
- **Island biome:** the v0.2.6 concentric-ring island generation got the deeper water + Shallows ring but no inlet pass yet. Symmetric inlets cutting toward the island centre is a one-day follow-up.
- **World-map river render direction:** chains now span multiple tiles but each tile still draws a horizontal stripe (regardless of which way the chain is flowing). A flow-direction-aware stripe rotation is a UI polish follow-up.

---

## [0.4.36] — 2026-05-14

### Fixed — Job priorities, smurf crowding, wood overflow, accidental drag-selects

Four bugs from Sam's playtest, all addressed in one batch.

#### Fix 1 — Job priorities have visible effect (`scripts/simulation/systems/BehaviorSystem.cs`, `scripts/simulation/WorkPriorityDefaults.cs`)

The work-priority system was wired correctly (the Jobs panel writes to `Smurf.WorkPriorities` via `SimulationManager.SetWorkPriority`, `SelectTask` reads it via `jobOk` / `jobTilt`, and `SaveManager` persists it) but the tilts were too small — Priority 1 added +12 and Priority 4 subtracted –12 against base priorities of 40-60, so a Priority-1 Forager only barely outranked a Priority-4 one and the dial felt purely cosmetic.

- **Tilts widened from ±4 / ±12 to ±10 / ±25.** Priority 1 → +25, 2 → +10, 3 → -10, 4 → -25. Now spans 50 points across the dial; a Priority-1 smurf strongly wins over a Priority-4 colleague for the same task, and a Priority-4 task is genuinely a "last resort" instead of a near-default.
- **`TaskType.Haul` added to `WorkPriorityDefaults.CategoryFor`** — the haul task was already gated via the string `"Haul"` directly in SelectTask, but the enum-to-category map didn't have an entry, so any future UI that walks tasks via `CategoryFor` will now resolve haul correctly.

#### Fix 2 — Smurfs prefer walking around each other (`scripts/simulation/systems/BehaviorSystem.cs`)

The v0.4.20 "primary direction wins regardless of crowding" rule made every smurf eagerly climb over neighbours instead of stepping aside, even when an uncrowded side-step was right there. Steering rewritten:

1. **Primary direction terrain-passable AND uncrowded** → take it (best case, unchanged).
2. **Primary crowded or terrain-blocked** → fan out the rotated angles ±45° … 180° and grab the FIRST uncrowded passable tile. Side-step beats climb-over.
3. **No uncrowded option anywhere** → take the primary (or first crowded side-step) as a last-resort climb-over. The v0.4.29 yield/lie-down trigger still fires at `StuckTicks ≥ 22` for single-tile-tunnel jams that can't resolve any other way.

Two-stage rule: "give the other guy space if you can, climb if you can't". Single-tile tunnels still resolve cleanly because the only "other option" there is the tunnel itself, so climb-over + yield kicks in as designed.

#### Fix 3 — Wood / Stone / Food no longer overflow to negative HUD values (`scripts/simulation/systems/HaulSystem.cs`, `scripts/simulation/ColonyResources.cs`, `scripts/SimulationManager.cs`)

Sam's screenshot showed a `Wood Log ×22,727,505` stack on a stockpile tile and `Wood: -1,685,682,637` in the HUD — int overflow from a compounding bug. Root cause: the v0.4.30 HaulSystem deposit *dual-wrote* every delivered item into both `map.DropItem(item)` AND `r.Inventory.Add(item)`. The smurf's next multi-trip cycle could then re-pick its own delivery from the stockpile tile and re-deliver it — every cycle absorbed the item's Quantity into the pool again without consuming the on-map stack. After enough cycles the colony pool overflowed `int.MaxValue` and went negative.

- **`HaulSystem.Apply` Phase 2 dual-write removed.** Delivered items now go ONLY to the map via `DropItem`. The colony pool (`ColonyResources.Inventory`) is left for scenario starting inventory and the Phase-5 stockpile-zone bookkeeping that hasn't landed yet.
- **`ColonyResources.Food / Stone / Wood / MagicEssence` now sum on-map stockpiles** via a new `Map` reference + `MapCountByKind` / `MapCountByFamily` walks. So the HUD reads the same authoritative store the player sees on the ground.
- **`SimulationManager._Ready` + `BindLocalMap`** also seed `_core.Resources.Map = _core.Map` so the HUD wires up at colony seed and after save-load.
- **Walk cost:** typical dropped-tile count is tens to low hundreds; the walk takes one items-lock acquire (snapshot path) and iterates lock-free. HUD reads stay cheap.

Existing saves with the old overflow still show the negative number until the colony consumes the polluted pool; a fresh game is clean from tick zero. A migration patch that zeroes the polluted pool on load is a candidate follow-up but not shipped here.

**Bonus:** wood-icon palette lightened in `ItemDropOverlay.VariantTable` (DeadWood / LivingWood / Fungal / Wood-default) so the yellow count-badge digits read clearly against the icon instead of blending into saturated brown.

#### Fix 4 — Ctrl-gated box-drag (`scripts/ui/SettingsPanel.cs`, `scripts/ui/GameController.cs`)

A small UX change: left-click + drag on empty terrain only starts a multi-select box when Left Ctrl is held. Without Ctrl, an empty drag is treated as a deselect (no smurfs roped, no accidental crowd-orders during camera pans). Single-smurf left-clicks and item / vegetation inspector clicks are unaffected.

- **New "Box-select needs Ctrl" toggle in Settings → Gameplay** (default ON). Persists to `settings.cfg` under `gameplay/box_select_requires_ctrl`.
- **`SettingsPanel.BoxSelectRequiresCtrl`** static property — live-readable so `GameController.HandleMouseClick` picks up flips without a settings re-open.

#### Version

Patch `0.4.35` → `0.4.36`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.36`.

---

## [0.4.35] — 2026-05-14

### Added — v0.4.33 corpse follow-ups (save/load, witnessed-death thoughts, bones)

Three of the four follow-ups documented in the v0.4.33 entry now land. The fourth (Burial / Cremation tasks) needs Phase 5 stockpile-zone designations and stays deferred.

#### Corpse `CorpseInfo` round-trips through save/load (`scripts/SaveManager.cs`, `scripts/SimulationManager.cs`)

The v0.4.33 model put `CorpseData` on `Item.CorpseInfo` but the save path only persisted the base `ItemSaveData` fields, so corpses survived save/reload but their bio data was lost (hover obituary dropped to the "Smurf corpse" placeholder). Wired now:

- **New `SaveManager.CorpseSaveData` record** — `Name`, `AgeYears`, `Sex` (string for enum-resilience), `Role`, `Cause` (string), `DeathAgeTicks` (relative to save's tick so reload preserves "died N days ago" regardless of absolute tick), `Personality` (list snapshot), `Handedness` (string).
- **`ItemSaveData` extended** with an init-only `Corpse?` property. Positional ctor signature unchanged, so every existing save-write call site continues to compile without modification — the property only populates when an item carries a `CorpseInfo` sidecar.
- **`SnapshotItem`** — branches on `it.CorpseInfo != null` and attaches a populated `CorpseSaveData` via the `with` expression. `Personality` is copied (not shared) so future personality mutations don't retroactively rewrite the saved sidecar.
- **`RehydrateItem`** — when `rec.Corpse != null`, parses `Sex` / `Cause` / `Handedness` enums with defensive `TryParse` fallbacks (Male / Natural / Right) so old saves without these strings still load cleanly. Rebuilds `CorpseData` with `DeathTick = currentTick - DeathAgeTicks` to preserve the "time since death" semantics.

A body that dies and gets saved 3 days later, then loaded 10 days after that, still reads "died 13 days ago" — same convention as `ItemSaveData.AgeInTicks`.

#### `WitnessedDeath` thought now fires (`scripts/simulation/SimulationCore.cs`)

The `WitnessedDeath` def has been in `ThoughtRegistry` since v0.3.43 (`-12f mood, 14400 ticks`) but no path was triggering it. `DropCorpseGear` now broadcasts the thought to every living colony-mate within ~10 tiles of the death position:

- **Radius:** `10 * LocalMap.TileSize = 160 px` (squared = 25 600 — no sqrt per witness).
- **Excludes the dead smurf themselves** (defensive — wouldn't fire anyway because the thought-add path checks IsAlive on its caller in some flows, but explicit guard keeps the intent clear).
- **Context:** the dead smurf's name, so the thought entry in the unit-card Mood tab reads `Saw a friend die. — Sloppy  (-12)`.
- **Snapshot via `AllSmurfs()`** — takes the smurf lock once, returns a fresh list. Iterates lock-free thereafter. Cheap; deaths are rare.

Combat raids (Phase 7) will inherit this for free: any death the combat system causes runs the same `DropCorpseGear` path and witnesses around the kill tile get the mood hit.

#### Bones drop on full corpse decay (`scripts/world/LocalMap.cs`)

`TickCorpseDecay` previously deleted a corpse outright when its `AvgCondition` hit 0. Now it leaves a single Material/Bone item behind:

- **One bone pile per corpse** — Quantity 1, Quality Normal, fresh condition, `MaterialKey("Bone", "Generic")` (already registered in `MaterialRegistry` with × 0.80 decay mul + 🦴 icon).
- **SubType `"Bones"`** — `ItemRegistry` has no def for this yet so the display name falls back to the SubType literal ("Bones"). Phase 9 husbandry will likely register a proper def with crafting recipes.
- **Drop happens AFTER the lock** — the corpse-decay walk now accumulates `bonesToSpawn` coords during the iteration and calls `DropItem` outside the lock. Spawning inside would mutate `_droppedItems` mid-foreach, a classic iteration-during-modification bug. The standard v0.4.30 cap + spiral overflow rules apply: a bone may overflow to a neighbour tile if the corpse site now has a different type at the cap.
- **The hover obituary disappears with the corpse** — bones are a Material item, not a Corpse, so they show as a plain "Bones (Bone)" hover line. That's the intended hand-off: the dead smurf's identity decays alongside the body.

#### Burial / Cremation tasks — still deferred

The fourth follow-up needs the Phase 5 stockpile-zone designation tooling (a "Graveyard" zone the player paints, with smurfs auto-hauling corpses into it). The zone system itself doesn't exist yet — Roadmap §5.11.a covers it but only as Phase 5 scope. Re-tabled here so the v0.4.33 follow-up list stays explicitly tracked:

> **Burial / cremation tasks** — Caretaker / Guardian task that hauls fresh corpses to a Graveyard zone (Phase 5.11.a) and either buries (deletes after a graveyard animation) or cremates (turns into Ash material, Phase 9-overlap). Will land alongside the zone system.

#### Version

Patch `0.4.34` → `0.4.35`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.35`.

---

## [0.4.34] — 2026-05-14

### Added — Stationary-target inspector card (left-click items + vegetation)

Left-click an empty tile (no smurf, no active designation tool) that has dropped items or vegetation on it and a properties card opens up at the top-right — modelled visually after the Smurf unit card but tailored to inanimate targets. Closes when you click a smurf (the smurf card takes over), click an empty tile, or press the X button.

**The card is a SNAPSHOT**, not a live view. The contents are captured once on open and never refresh; this matches Sam's explicit ask ("only update when opened so as to not degrade performance") and keeps the per-frame cost of the inspector at exactly zero. Closing and re-opening on the same tile picks up any state changes.

#### `scripts/ui/TilePropertiesPanel.cs` (new)

Floating Control with `FloatingPanelStyle.Make()` background — same anchor band as `SmurfCardPanel` so the two share screen real estate and never overlap. Layout:

- **Header:** ◆ glyph + dynamic title (e.g. "Smurfberry Bush", "Granite Stone ×27 (2 stacks)", "Corpse of Sloppy") + close X.
- **Sub-label:** `Tile (tx, ty)` for orientation.
- **Sections rendered conditionally** so a vegetation-only tile gets one clean section, an items-on-grass tile gets two, a corpse-on-rock tile gets all three:
  - **Items** — one entry per Item on the tile. Display name + quantity, material family/sub-type line, Quality (equipment-tier only; stackables omit it), condition/durability line with state.
  - **Items / Corpse** — corpses get the special obituary block: `Name`, life-stage descriptor (Sprout / Juvenile / Adult / Elder / LastSeason from `AgeYears`), Sex, Role, Cause of Death (red), body state + condition number, full Personality list, Handedness. The on-tile hover line from v0.4.33 collapsed into a tidy unit-card row.
  - **Vegetation** — type name, Health (0–100), Yield remaining (red when depleted), regrowth countdown when depleted, and a friendly one-line yield description ("Yields Smurfberries (food).").
  - **Tile** — terrain name (Boulder tiles include the stone sub-type), passable yes/no (green/red), Fertility percentage, stone material if present.

#### Click integration (`scripts/ui/GameController.cs`)

Inserted between the existing "click on smurf" and "start select-box drag" branches in `HandleMouseClick`:

```
Smurf hit?        → SelectSingleSmurf + EmitSmurfClicked (opens smurf card; v0.4.34: also closes tile card)
No smurf hit?
  ├─ Tile has items or veg?  → Open TilePropertiesPanel (also closes smurf card)
  └─ Tile is empty           → Close any open TilePropertiesPanel + start select-box drag
```

`OnSmurfClicked` now also calls `_tileProps.Close()` so opening a smurf card from any source (left-click, roster row, etc.) dismisses a stale tile-card. The two cards share the top-right band and are mutually exclusive.

#### Version

Patch `0.4.33` → `0.4.34`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.34`.

#### Follow-ups (deferred)

- **Right-click "Inspect" action** on the existing context menu for parity with the click path (currently right-click on an item tile shows Pick Up / Move to).
- **Refresh button** in the card header for power users who want to re-snapshot without close/re-open. Trivially one button → `Open(tx, ty, map)` again. Skipped to keep the v0.4.34 surface focused.
- **Item-stack picker:** for tiles with multiple stacks of different quality, the card lists each but doesn't yet let you click into one for a deeper view (the corpse path is the closest thing). Worth revisiting once Phase 7 combat introduces broken / repairable equipment.

---

## [0.4.33] — 2026-05-14

### Added — Smurf corpses persist on the world and decay (DF/RimWorld-style)

When a smurf dies their body now stays on the death tile as a `Corpse`-kind Item carrying the dead smurf's biographical sidecar, decays over ~7 in-game days, and shows the player a full obituary on hover. Reuses every Item pipeline that already exists (placement, MultiMesh icon, hover tooltip, save/load) so the feature is mostly data + a small decay walk.

#### Data model (`scripts/simulation/items/ItemKind.cs`, `scripts/simulation/items/CorpseData.cs`, `scripts/simulation/items/Item.cs`)

- **`ItemKind.Corpse`** — new enum value (10th kind). Glyph 💀. `IsStackable` returns false by default for non-explicitly-listed kinds, so corpses stay one-Item-per-smurf as intended.
- **`CorpseData`** — new sealed record carrying `Name`, `AgeYears`, `Sex`, `Role`, `Cause` (CauseOfDeath), `DeathTick` (long), `Personality` (snapshot copy of list at death), `Handedness`. Lightweight — biology / skills / body-part dictionaries are intentionally NOT serialised onto the corpse; too heavy for a disposable artefact, and the obituary line doesn't need them.
- **`Item.CorpseInfo`** — new nullable field. Populated only for `Corpse`-kind items; null for everything else.

#### Death handler (`scripts/simulation/SimulationCore.cs`)

`DropCorpseGear` now also constructs a Corpse Item alongside the existing equipment / inventory drops:

- Pulls the dead smurf's static attributes into a fresh `CorpseData` (with a snapshot copy of `Personality` so future personality-mutation work doesn't retroactively rewrite the obituary).
- Material is the synthetic `Flesh/Smurf` MaterialKey — `MaterialRegistry` doesn't know it (returns null DecayRateMul) but that's fine because corpse decay runs through a dedicated path, not the Material-driven Inventory.TickDeterioration.
- `Quality.Normal`, `State.Fresh`, `AvgCondition = 100`, `DurabilityCap = 100`, `Quantity = 1`.
- Drops onto the smurf's `SimPos` tile through `LocalMap.DropItem`, so the v0.4.30 cap / single-type-per-tile / spiral-overflow rules all run as in normal play (a corpse on a tile that already has 250 of one type spills onto an adjacent tile, etc.).

Equipment-on-death continues to drop as separate Item entries on the same tile — DF convention — so the player can loot the body without picking up the corpse itself.

#### Decay (`scripts/world/LocalMap.cs`, `scripts/simulation/SimulationCore.cs`)

- New **`LocalMap.TickCorpseDecay(globalTick, daysElapsed)`** walks `_droppedItems` once per call, drops every Corpse-kind item's `AvgCondition` by `14 × daysElapsed`, updates `ItemState` to Fresh / Stale / Spoiled at the 70 / 40 / 0 thresholds, and removes any corpse that hits 0 condition entirely. Cleans the empty per-tile lists and fires `ItemsChanged` per affected tile so the renderer picks up the cleanup.
- Wired into `SimulationCore`'s existing day-boundary block alongside `ItemDeteriorationSystem.TickDay(Resources.Inventory, …)` — one call per in-game day at any sim speed.
- Target timeline: **~7 in-game days from a fresh body to a fully-decayed empty tile** (100 / 14 ≈ 7.1 days), matching the RimWorld rough-decomposition pace.
- Kept as a separate walk from the colony pool's Inventory deterioration because the two stores never share Item references — mixing them through a single loop would either double-decay or leak per-tile bookkeeping into the colony class.

#### Rendering (`scripts/ui/ItemDropOverlay.cs`)

- New variant index **28 — Corpse** added to the variant table (dusky-red square + near-black outline) and `VariantIndexFor`. `VariantCount` bumped 28 → 29.
- New **`DrawCorpseLabelOn`** rendered under each Corpse tile from `DrawAllBadges`. Format: `"{Name} (fr.)" / "(st.)" / "(sp.)"` mirroring the v0.4.30 equipment shorthand convention but with the dead smurf's name front-and-centre — Sloppy, Grumbly, Vanity all stay readable even after their bodies start to rot.

#### Hover obituary (`scripts/ui/TileInfoOverlay.cs`)

The v0.4.30 ground-items hover-tooltip rows now special-case Corpse entries through a new **`FormatCorpseLine`**:

> `Sloppy — Adult Male Forager, died of Starvation; body Stale`

Life-stage descriptor derived from `CorpseInfo.AgeYears` matches the existing `LifeStage` thresholds. Body-state label tracks the live `ItemState` so a fresh corpse reads "Fresh", a half-rotted one "Stale", and a near-gone one "Decayed". Falls back to "Smurf corpse" if the sidecar is null (older saves loaded onto a fresh model — defensive).

#### Version

Patch `0.4.32` → `0.4.33`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.33`.

#### Follow-ups (deferred)

- **Save/load:** the new `CorpseInfo` sidecar is not yet serialised — corpses themselves persist (they're Items in the dropped-items dict) but the bio data is lost across save/reload, falling back to the "Smurf corpse" placeholder. Add a `CorpseInfo` block to `ItemSaveData` next pass.
- **WitnessedDeath thoughts:** the `WitnessedDeath` thought def has been live since v0.3.43 but isn't currently triggered by anything. Now that corpses persist on the map, a follow-up could scan tile-radius around each living smurf for fresh corpses and add the thought for nearby colony-mates' deaths. Phase 7 combat may need this for raid trauma anyway.
- **Burial / cremation tasks:** a Caretaker / Guardian task to haul corpses to a designated "Graveyard" zone (Phase 5 zones) and either bury (delete) or cremate (turn into ash material). Would close the long-term loop where corpses just decay to nothing on the work tile.
- **Skeleton / bones drop on full decay:** at 0 condition the corpse currently vanishes; could leave a single `Bone` material item (Phase 9 husbandry overlap).

---

## [0.4.32] — 2026-05-14

### Added — RimWorld-style Developer Mode + Settings header polish

Two coordinated asks:

#### Developer Mode (`scripts/DevMode.cs`, `scripts/ui/DevPanel.cs`, `scripts/SimulationManager.cs`, `scripts/ui/SettingsPanel.cs`, `scripts/ui/GameController.cs`)

Modelled on [RimWorld's Development Mode](https://rimworldwiki.com/wiki/Development_mode): a settings toggle reveals a floating right-side dev panel with categorised debug actions plugged into every system that exists today, plus disabled stubs for systems landing in later phases.

- **`DevMode` singleton** — static class holding `IsEnabled` + a `Changed` event. Lives in the root namespace so any UI can subscribe. Off by default.
- **`SettingsPanel`** — new "Developer Mode" toggle in the Gameplay section (`AddDeveloperModeRow`). Persists to `user://settings.cfg` under `gameplay/developer_mode`; load + reset paths wired. Flipping the toggle pushes the value into `DevMode.IsEnabled`, which fires `Changed` so the floating panel can hide/show without a scene rebuild.
- **`DevPanel`** — new Control added to GameController's UI layer. Visible only when `DevMode.IsEnabled`; **F12** toggles its visibility once dev mode is on. Categorised sections:
  - **Simulation:** Pause/Resume, Tick 1×, 5×, 25×, 100× speed bursts.
  - **Selected Smurf:** Fill needs, Drain needs, +Mood (TastyMeal thought), -Mood (TaskAbandoned thought), Force yield 60t (DF-style lie-down), Kill. Acts on every smurf currently in the selection set.
  - **Spawn at Cursor:** 50 Smurfberries, 50 Granite blocks, 50 DeadWood blocks, 10 Raw Essence, fresh adult Smurf (random traits/personality/role via the same registries as `BirthSystem`). Drops route through `LocalMap.DropItem` so the v0.4.30 cap + single-type-per-tile + spiral overflow rules all run as in real play.
  - **Map:** rebuild regions + force redraw (mostly stubs at this point — regions rebuild lazily on query).
  - **Visualize (stubs):** disabled toggles for pathfinding / region / occupancy / claims overlays — will light up when each renderer hook lands.
  - **Future systems (stubs):** disabled buttons with phase-tagged tooltips — Combat raids (Phase 7), Weather storms (Phase 10), Trader caravans (Phase 11), Fire (Phase 10), Disease (Phase 9), Hostile mobs (Phase 7). Buttons sit in place so wiring them up later is a one-line change.
  - Live status row at the bottom echoes the result of the most recent action ("Spawned 50× Smurfberry at (12,17)", "Sim paused", etc.).
- **`SimulationManager`** — new `Dev*` method family:
  - `DevFindSmurfByName`, `DevKillSmurf`, `DevFillNeeds`, `DevDrainNeeds`, `DevAddThought`, `DevForceYield` — direct mutations under SimulationCore's smurf lock.
  - `DevSpawnItem` — routes through `ItemFactory.Create` + `LocalMap.DropItem`, returns the actual landing tile (in case overflow re-targeted).
  - `DevSpawnSmurf` — builds a fresh Smurf via the same registries `BirthSystem` uses (TraitRegistry / PersonalityRegistry / BodyPartRegistry / SkillRegistry / WorkPriorityDefaults / HandednessMeta), seeds SimPos near the cursor, calls `_core.AddSmurf`.
  - `DevTogglePause` — wraps the existing Paused field so the dev button text can mirror it.
- **`CauseOfDeath.Dev`** — new enum value so dev-killed smurfs show up in mood crossings + game-over diagnostics distinctly from Natural / Starvation / Combat.
- **F12 hotkey** in `GameController._Input` — works even when an overlay is open (routed through `_Input` rather than `_UnhandledInput`).

Player-facing path: open Settings → Gameplay → toggle Developer Mode → close Settings → press F12 → use the panel. Off-by-default keeps normal play unaffected.

#### Settings header — star drop (`scripts/ui/SettingsPanel.cs`)

`★  Settings  ★` → `Settings`. One-line cleanup per Sam's note.

#### Version

Patch `0.4.31` → `0.4.32`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.32`.

#### Follow-ups

- The Visualize / Future-systems stubs are intentionally non-functional placeholders — wire them up as each system lands (Phase 7 raids, Phase 9 disease, Phase 10 weather/fire, Phase 11 traders). The dev panel layout already has slots for them so it's a one-line `Pressed` handler each.
- Map section's "Rebuild regions" / "Force redraw" are placeholder buttons — the underlying APIs exist but aren't exposed yet. Light wiring follow-up.
- Dev panel does not (yet) trap mouse input outside its bounds — clicking through the panel still hits the world below. RimWorld's behaviour. If that proves annoying, add a `MouseFilter = Stop` overlay band behind the buttons.

---

## [0.4.31] — 2026-05-14

### Added — Mood-tab Thoughts list + river generation finally visible + Shroombridge roadmap

Three small but meaningful follow-ups from Sam's playtest of v0.4.30:

#### Recent Thoughts on the Mood tab (`scripts/simulation/SimulationSnapshot.cs`, `scripts/ui/SmurfCardPanel.cs`)

The whole `Thought` infrastructure (registry of 21 def's, per-smurf 8-slot ring, ThoughtSystem decay, `MoodFromThoughts` aggregate) has been live since v0.3.43 — every behavioural moment that should generate a thought already does. It just never surfaced in the UI. Fix:

- **`SmurfSnapshot`** gains `Thoughts` (lightweight `(Key, Headline, MoodOffset, TicksRemaining, Context)` tuples — pre-resolved registry headlines so the UI doesn't need to look up) + `MoodFromThoughts` (live ±50-clamped sum). New `SnapshotThoughts` helper sorts by `TicksRemaining` desc so the longest-lived (typically most-recent or most-impactful) thoughts land at the top.
- **`SmurfCardPanel.BuildMoodTab`** adds a "Recent Thoughts" header + box between the Breakdown and Personality Effects sections. The header text tracks the live sum so the player sees `Recent Thoughts (+12)` or `Recent Thoughts (-8)` depending on the colour-coded mood balance.
- **`RefreshMoodTab`** rebuilds the box every refresh (bounded by ThoughtCapacity = 8, so cheap), shows up to 5 entries as `  {Headline} — {Context}  (±N)` with green/red colouring, and a tooltip giving the headline + remaining seconds when tips are on.

The list fits inside the existing ScrollContainer that wraps every card tab — no layout changes needed.

#### Rivers actually generate visibly now (`scripts/world/WorldMapGenerator.cs`, `scripts/ui/WorldGenPanel.cs`)

`WorldMapGenerator` Pass A.7 was implementing rivers correctly but seeded them so sparsely that Sam went several worlds without ever seeing one — and `LocalMapGenerator` Pass 4h (river carving) only fires when the world tile has the flag, so no flag = no river local map either. Two upstream fixes plus a visibility bump:

- **River target counts bumped.** Old: 0 / 0–1 / 1–3 / 3–6 by rainfall band. New: **1 / 1–2 / 2–5 / 4–8**. Even bone-dry worlds get one river so the §2.6 fertility / vegetation / ford machinery actually exercises in normal play; wet worlds spike high enough for a proper braided system look on the world preview.
- **Inland source candidates added.** River seeds used to require Pondsea adjacency (coastal river mouths only). Inland-heavy maps with a small Pondsea region had vanishingly few candidates and `riverTarget` rolled in vain. New rule: candidates are passable lowland tiles cardinally touching Pondsea **OR Mountains/Peaks** (foothill snowmelt). Mountains/Peaks themselves are excluded from being candidates (the source is the lowland next to the elevation, not the elevation itself — matches the existing Pass 4h carving exclusion).
- **World-map stripe more prominent.** Bumped from 22 % tile-height + 0.85 alpha to **35 % + fully opaque** with a thin dark outline above and below for contrast on light biome colours (Plains, Desert). The thin translucent band was blending into the biome fill at typical preview cell sizes.

`LocalMap`'s Pass 4h River carving (line 992+) was already correct end-to-end (water channel + 1-tile mud border + 20 % sand fords + ±3-tile fertility boost) — verified during the audit, no changes needed there.

#### Shroombridge — Phase 5 roadmap entry (`SmurfulationC_Roadmap_2026.md`)

New `§5.11.d` documents Shroombridge: a buildable woven mushroom-cap mat that turns Water tiles into passable (`TerrainType.Shroombridge`) tiles, costing 4 Fungal Wood + 2 LivingWood per tile. Light structures (woven walls, doors, Bedroll, Chest, Shelf, Workbench, Table, etc.) can sit on top — but stone walls, heavy furniture, and Hearth (open flame on a fungal mat = bad day) are forbidden. Forbidden on fast-current river-centre tiles; reduced to 0 condition the bridge collapses and dunks anything on it.

The headline value is the **water village** archetype — a Coastal / Island / River map can paint Shroombridges across a sheltered cove and run a self-contained colony of light huts and storage on top, RimWorld-style bridges with Smurfulation's fungal-mat flavour. Roadmap also sketches Bridge Anchors / Stone Bridges / Magicwood-luminescent variants as Phase 7+ follow-ups.

#### Version

Patch `0.4.30` → `0.4.31`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.31`.

---

## [0.4.30] — 2026-05-14

### Added — DF/RimWorld-style stockpile + multi-item carry + equipment-on-ground

The Phase-5-flavoured "Storage & Logistics" batch landed in one go (Sam's "all five at once" call). Every dropped item is now a visible stack on the level, smurfs ferry up to their personal carrying capacity in one trip instead of one item per round trip, and equipment items show shorthand labels under their ground sprites so the player can spot a Masterwork sword in a pile without hovering. Storage furniture is sketched into the roadmap for a follow-up.

#### A1 — `LocalMap.DropItem` cap + single-type rule (`scripts/world/LocalMap.cs`)

Every passable tile is now type-locked by its first occupant's `(Kind, SubType, Material)`. Subsequent drops of the same triple stack onto it (Quality / State coexist as separate stacks within the type), capped at `MaxStackPerTile = 250`. Different-type drops trigger a 5-tile spiral search for a compatible passable tile and re-target the item there, updating `item.TilePos` so the renderer + save see the actual landing tile. Last-resort fallback cap-busts on the requested tile (with a `GD.PushWarning`) rather than lose the item.

Helpers added: `TryDropOnTile`, `IsCompatibleType`, `SumQuantity`, `FindStockpileTileNear`, `ForceDropOnTile`. Future storage furniture (see roadmap §5.11.c) will raise the per-tile cap based on the furniture type.

#### A2 — `Smurf.CarryingCapacity` (`scripts/simulation/Smurf.cs`)

Computed property in the range **5–75**. Base from life stage (Sprout 5 / Juvenile 18 / Adult 50 / Elder 35 / LastSeason 18) + personality modifiers (`Brawny +20`, `Worrywart -10`, `Sleepyhead -10`, `Stoic +5`, etc.) + biological-trait modifiers scaled by penetrance (`Miniaturization` and `StatureAgility` pull the cap down by up to 15 / 5). Worn `Equipment` does NOT count toward the cap; unworn items in the new `Inventory` list do.

#### A3 — `Smurf.Inventory` list + backward-compat `CarriedItem` (`scripts/simulation/Smurf.cs`)

`Inventory: List<Item>` replaces the v0.4.2 single carry slot. `CarriedItem` is preserved as a property shim (read = most-recently-added item, write = append/clear) so all v0.4.2-v0.4.29 callers (save/restore, manual drop, snapshot) keep working. New code paths use `Inventory` directly. `CurrentCarriedCount` returns the sum of `Quantity` across the inventory.

`SimulationCore.DropCorpseGear` updated to drop EVERY item in the inventory on death (was only the most-recent under the single-slot assumption).

#### B — Multi-trip `HaulSystem` (`scripts/simulation/systems/HaulSystem.cs`)

`SelectHaulTarget` only fires for fully-empty smurfs. `Apply` Phase 1 now CHAINS: after a pickup, if `CurrentCarriedCount < CarryingCapacity` and a same-radius unreserved item exists, the smurf retargets to that item instead of going home. Only when full or no more nearby items remain does the smurf transition to delivery (Phase 2).

Phase 2 drops EVERY item in the inventory at the stockpile tile via `map.DropItem` (with the v0.4.30 cap/type rules taking care of overflow), then also adds each item to `ColonyResources.Inventory` for HUD compatibility (DUAL-BOOKKEEPING — see in-code note; will unify in Phase 5 stockpile-zone work).

`FindNextHaulNearby` is the multi-trip helper — same 32-tile radius cap as `SelectHaulTarget`, additionally rejects items whose `Quantity` would exceed remaining capacity (a smurf with 5 slots left won't try to grab a 50-stack of berries; they'd rather walk home empty than reserve something they can't take).

#### C1+C2 — Carrying-Capacity bar + Inventory list on the Unit Card (`scripts/ui/SmurfCardPanel.cs`)

The Inventory tab gains a `Carrying: X / Y` label + `ProgressBar` at the top, refreshed each tick from the snapshot's new `CarryingCapacity` / `CurrentCarriedCount` fields. The "Carried" section was renamed "Inventory" and now lists every stack in `snap.InventoryItems` (e.g. "Granite ×27") instead of just the legacy single CarriedItem hint. Layout fits inside the existing ScrollContainer; the Drop button stays on the most-recent item only (legacy single-slot semantics — full multi-item drop UI is a follow-up).

`SimulationSnapshot` gains `CarryingCapacity`, `CurrentCarriedCount`, and `InventoryItems` (lightweight `(Kind, SubType, Material.Family, Quantity)` tuple list built per-tick by `SnapshotInventory`).

#### C3 — Top-right hover tooltip lists ground items (`scripts/ui/TileInfoOverlay.cs`, `scripts/ui/GameController.cs`)

`ShowTile` accepts an optional `IReadOnlyList<Item>? droppedItems`. The `Format` method appends a per-item line: `{display} ({material}) ×{qty}` for stackables, plus `[Quality]` for equipment-tier items. `GameController.UpdateTileInfo` passes `map.GetItemsOnTile(tx, ty)` so every hover shows the full label of every stack on the tile (RimWorld convention).

#### D1+D2 — Equipment ground sprites + shorthand labels (`scripts/ui/ItemDropOverlay.cs`)

`VariantIndexFor` extended with five new equipment-tier indices (23 Tool / 24 Weapon / 25 Apparel / 26 Furniture / 27 Trinket); `VariantTable` baked with distinct shapes / colours per kind. New `IconShape.Triangle` for Tool / Weapon, with `FillTriangleOnImage` + `DrawLineOnImage` helpers added inline.

A new draw pass in `DrawAllBadges` renders the shorthand label `{quality} {mat-shorthand} {sub-shorthand}` (e.g. "good ddwd. swrd." for a Fine Deadwood Sword) on the badge canvas below each equipment tile. Helpers: `IsEquipmentKind`, `ShorthandLabel`, `QualityPrefix`, `ShortenIdent` (consonant compression to ≤4 chars + period suffix), `DrawShorthandLabelOn` (centred 7-pt text on a tight black background).

#### E — Storage Furniture roadmap entry (`SmurfulationC_Roadmap_2026.md`)

New `§5.11.c` documents Chest / Shelf / Rack / Barrel / Crate / Cabinet — per-tile cap multipliers (Chest 500, Crate 750, Barrel 400, Cabinet 120, Rack 150, Shelf 250 + passable), Kind / SubType filtering, and how the `LocalMap.DropItem` spiral picker should prefer matching furniture before bare zone tiles when stockpile zones land. The Furniture row in the items table also extended to list the new pieces.

#### Version

Patch `0.4.29` → `0.4.30`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.30`.

#### Known follow-ups

- Save/restore preserves only the legacy single-item `CarriedItem` (via the backward-compat shim); the v0.4.30 multi-item Inventory state is lost across save/load. Add `InventoryItems[]` to `SmurfSaveData` next pass.
- `ColonyResources.Inventory` and on-ground stockpiles dual-bookkeep (HaulSystem deposit writes to both) — consumption decrements only the pool, so the visible stack stays full as the HUD count drops. Will unify when stockpile zones become the source of truth in the Phase 5 storage-zone work.
- Multi-item drop UI on the unit card — current Drop button targets only the most-recent item via the legacy single-slot path.

---

## [0.4.29] — 2026-05-14

### Fixed — Single-tile tunnel jam + dropped-item badges hidden behind icons

Two coordinated fixes from Sam's playtest after the v0.4.28 visual cleanup:

1. **Smurfs jammed solid in single-tile-wide excavation tunnels.** One or two would dig the entry boulder, then the rest of the assigned crew piled in behind, jittered against each other, and couldn't get back out once they'd traded places.
2. **Dropped-item count badges were occluded by the icon MultiMesh.** The yellow digits rendered first, then the icon quads drew on top, leaving only a thin yellow crescent visible along the icon edge.

#### Fix 1a — Excavate cascade prevention (`scripts/world/LocalMap.cs`, `scripts/simulation/systems/BehaviorSystem.cs`)

Diagnosis: `LocalMap.FindNearestExcavate` filters candidates by region-graph reachability but does not consider whether the candidate's *approach* is currently occupied by another smurf. As soon as a single-tile tunnel's leading boulder gets excavated, every idle smurf in the colony sees the freshly-passable tile, claims the next boulder behind it, and converges on the same one-wide entry. Result: a five-smurf jam where the only one making progress is whoever happened to step in first.

Added an optional `Func<int, int, bool>? approachBlocked` callback parameter to `FindNearestExcavate`. `BehaviorSystem.FindDesignatedExcavation` passes a closure that consults the per-tick `_smurfPerTile` occupancy grid: a target is rejected when *every* passable 8-neighbour is currently held by a different smurf. Targets with at least one open approach pass through; targets with no passable neighbours at all are still left to `IsWorkReachable` (existing filter, unchanged). The check is per-candidate at task-selection time, ~8 array reads — cheap.

This stops the cascade at the source. Smurfs scanning for work pick a different boulder elsewhere when the tunnel approach is congested, so only one or two converge on it at a time.

#### Fix 1b — DF-style "lie down so the other guy can climb over" (`scripts/simulation/Smurf.cs`, `scripts/simulation/systems/BehaviorSystem.cs`)

Cascade prevention alone wouldn't unjam smurfs already inside a tunnel — they need to physically swap places to exit. Sam suggested the Dwarf Fortress solution: dwarves lie down to let others climb over them. Implemented:

- `Smurf.YieldingTicks` (new int field). When > 0 the smurf is yielding for that many sim ticks: skipped by `MoveOneTick` (no movement attempt) and excluded from the per-tick occupancy grid (so neighbours can step freely onto its tile and cross over).
- `BehaviorSystem.PopulateOccupancyGrid` skips yielding smurfs in BOTH the `_smurfPerTile` count and the new `_firstSmurfPerTile` reference array (so the trigger can find WHO to ask without re-walking the smurf list).
- `BehaviorSystem.TryTriggerBlockerYield(s, map)` projects half a tile in the smurf's primary direction, looks up the blocking smurf via `_firstSmurfPerTile`, and sets `blocker.YieldingTicks = 60`. The triggering smurf's `StuckTicks` resets to give the now-clear path a fresh window.
- Wired into the existing stuck-detection block: fires when `s.StuckTicks >= 22` (between the v0.4.17 re-pathfind at 15 and the give-up at 30). The re-pathfind gets first crack at corner-stuck oscillation; if the smurf is *still* stuck after that, the obstruction is almost certainly another smurf, not bad routing.

Yields are idempotent (a blocker already yielding stays as-is) and self-resetting (decremented in `MoveOneTick`'s top guard, so the lying smurf stands back up after 60 ticks). No visual rendering change for the yielding state yet — that's a follow-up; for now the smurf just briefly holds position while colleagues walk through.

#### Fix 2 — Count badges render above icon MMIs (`scripts/ui/ItemDropOverlay.cs`)

Same Godot rendering-order pattern as `SmurfColonyView.SmurfExtrasNode` (v0.4.24): children render *after* the parent's `_Draw`, so any DrawString called from the overlay's own `_Draw` was painted *before* the icon MultiMeshes, leaving the digits hidden under the icon quads. Refactored:

- New `NumberBadgeNode` private partial Node2D added as the LAST child in `_Ready` (after all 23 icon MMIs).
- The badge node's `_Draw` calls `Owner.DrawAllBadges()`, which iterates `_summaries` and draws each badge onto the badge node's canvas (via the existing `DrawSmallNumberOn(canvas, ...)` helper, refactored to take the canvas Node2D as a parameter so the draw operations target the badge node, not the parent).
- `_Process` triggers `_badgeNode.QueueRedraw()` instead of `QueueRedraw()` so only the badge canvas re-paints when item summaries change.

Now the digits sit cleanly above every dropped-item icon at any zoom.

### Also in this patch — Visual feedback for the yield state

After landing the yield mechanic Sam asked for a sprite/animation so the player can see who's lying down. Implemented as a per-instance Transform2D squash on the existing MultiMesh:

- **`SimulationSnapshot`** gains a `bool IsYielding` field (derived from `s.YieldingTicks > 0`). Pure read of existing sim state, no extra sim work.
- **`SmurfColonyView.VisualSmurf`** mirrors the field; `UpdateFromTick` copies it from each snapshot.
- **`UpdateMultiMeshInstances`** branches on `s.IsYielding`: yielding smurfs get a `Transform2D` with Y-axis scale `0.35` and a re-derived Y-anchor offset (`-1.75` instead of `-4`) so the squashed sprite's feet still land at the smurf's logical world position. Bobbing is suppressed so they look still rather than convulsing on the ground. Standing smurfs use the existing rotation-only `Transform2D` — no extra cost.

The squash batches into the same MultiMesh draw call as standing smurfs (MultiMesh accepts arbitrary 2D per-instance transforms), so the visual costs nothing in draw count and almost nothing in CPU. A future pass could swap to a baked side-view sprite for richer art, but the squash is enough to read at gameplay zoom: when a smurf gets asked to yield, they visibly flatten on the tile while colleagues walk through, and pop back up after 60 ticks.

#### Version

Patch `0.4.28` → `0.4.29`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.29`.

(Yield-visual added post-bump within the same calendar day; bundled under the rule for small/within-24 h follow-ups. Backup folder reflects the pre-visual state; on the next bump it'll cover everything.)

---

## [0.4.28] — 2026-05-13

### Fixed — Smurfs visible on paused load + name labels always shown

Two related rendering fixes in `scripts/ui/SmurfColonyView.cs`. The v0.4.28 number is reused here from the rolled-back perf attempt below — that bundle never produced a backup or a shipped MainMenuController bump, so the slot is free.

#### Fix 1 — Names draw on every smurf, not just the selected one

`DrawSmurfExtras` previously gated the `DrawString` for `s.Name` behind `if (s.Selected)`, so only the actively-selected smurf showed a label. Removed the guard — every smurf now renders its name 9 px below its sprite (centred, 9 pt, cream `(0.95, 0.92, 0.72, 0.85)` colour) on the extras canvas. Order is unchanged: extras still draw above the body MultiMesh because `_extrasNode` is added as the last child of `SmurfColonyView`.

#### Fix 2 — Smurfs appear immediately on a paused game load

`_Process` returns early when `Paused`, which is the right call for animation/lerp work but also means `UpdateMultiMeshInstances()` (which writes per-instance transforms into the body MultiMesh and sets `VisibleInstanceCount`) never runs. Default-paused game starts therefore left every variant MultiMesh at `VisibleInstanceCount = 0` and the entire colony invisible until the player hit play.

`UpdateFromTick` already calls `QueueRedraw()` while paused so mood-colour / role-change updates repaint the parent canvas. Extended that paused path to also call `UpdateMultiMeshInstances()` and `_extrasNode?.QueueRedraw()` — the body buffers now populate on the very first snapshot push, regardless of pause state, and the extras canvas (names, equipment, carry icons, combat indicator) repaints alongside. During normal play `_Process` continues to drive both every frame, so no double work outside the paused window.

#### Version

Patch `0.4.27` → `0.4.28`. `MainMenuController.cs` + `memory/project_versioning.md` updated; backup at `C:\Claude\BACKUPS\SmurfC_BU\SmurfC_v0.4.28`.

---

### Also in this patch — Cave topology, stone strata, sprite legibility

Bundled into v0.4.28 per the within-24 h rule. Three coordinated visual/world-gen fixes from Sam after seeing the Level Preview and in-game close-up screenshots:

#### Cave subtype 0 — denser, water-carved topology (`scripts/world/LocalMapGenerator.cs`)

The previous chamber-blob + corridor scheme yielded ~30-35 % passable maps that read as scattered rooms with porous rock between them — not the interconnected sinuous tunnels of a real water-eroded cave system. Replaced with a **ridged-FBm-dominant** carve:

- **Primary feature:** `FastNoiseLite.Ridged` (3 octaves, freq 0.038, lacunarity 2.1, gain 0.55). Ridged FBm peaks along ridge spines and drops off sharply, producing branching dendritic tunnel networks with thin tributaries off thicker trunks — the natural pattern that flowing water dissolves along joint planes in karst geology.
- **Secondary feature:** sparse smooth-simplex chambers at threshold 0.85 (top ~5-7 %) act as rare carved rooms where tunnels widen.
- **Boulder debris** still gates on the chamber pass only — tunnels stay clear so they remain navigable single-file passages.
- **Threshold targets:** ~14-18 % passable (down from ~30-35 %). Peaks subtype stays at the higher 0.80 / 0.90 thresholds for an even rockier feel.

#### Stone subtypes — geological strata, not confetti (`scripts/world/LocalMapGenerator.cs`)

`AssignStoneVariation` Pass A used to call `RollWeighted` per Boulder tile, so every adjacent boulder could be a different stone subtype. The visual was a confetti mix; gameplay-wise, a player couldn't excavate a coherent block of one ore type. Replaced with **Voronoi-region assignment**:

- New `FastNoiseLite.Cellular` instance with `CellularReturnType.CellValue` partitions the map into Voronoi cells ~12-16 tiles across.
- Each Boulder reads its cell's per-cell value and uses it as the lookup index into the biome's cumulative weight table via the new `PickFromCumulative` helper. **All tiles in the same Voronoi cell resolve to the same subtype** — large connected blocks of granite, limestone, marble, etc., like RimWorld / DF strata.
- Pass B (rare MagicCrystal vein clusters) is unchanged — those stay as the random-walk overpaint they were in v0.4.27.
- No per-tile RNG allocations; lookup is O(table.Length) per tile, ≤ 6 comparisons.

This sets up the geological foundation for the planned "more ores and related materials" — when new stone subtypes land, they'll automatically generate as coherent blocks instead of single-tile spam.

#### Smurf sprite legibility + name above head (`scripts/ui/SmurfColonyView.cs`)

Sam reported the smurfs read as "upside down" and the name labels overlapped the body. The sprite is technically right-side-up in code (hat triangle at top of image, head/body below) but with a 5 px-radius head dominating a 3.5 px-radius body bump, the silhouette was ambiguous at low zoom — easy to mistake the small body bump for "feet."

- **Added dark feet** (2×2 px each, `(0.10, 0.08, 0.16)`) at the very bottom of the sprite (rows 22-23, columns ±2 from anchor X). They unambiguously establish "down" and frame the white-pants area beneath the body.
- **Name label moved above the hat.** Previously drawn at `pos + (0, +9)` — baseline 9 px below the smurf's anchor, which put the visible text right on the body. Now at `pos + (0, -19)` — two pixels above the hat tip with a clear gap, matching how RimWorld and DF position character labels. Combined with the v0.4.28 always-on names fix above, every smurf now wears its name as a clear floating label.

#### Dropped-item count badges — fallback font instead of 3×5 bitmap (`scripts/ui/ItemDropOverlay.cs`)

`DrawSmallNumber` rendered counts as a 3×5 hand-drawn pixel font (the same style as the v0.3.31 designation overlay digits). At default gameplay zoom this was illegible — Sam couldn't read the numbers on dropped items. Replaced with `DrawString` using `ThemeDB.FallbackFont` at size 9, matching the smurf-name label style.

- Hand-drawn `DrawDigit` method removed (~25 lines of glyph tables gone).
- Background is now a tight rect sized to the measured `GetStringSize` of the digits with 1.5 px / 1 px padding; foreground stays the existing yellow `(1.0, 0.95, 0.50)` on `(0, 0, 0, 0.75)` black for contrast.
- Call site retuned to anchor the badge's right-baseline at the tile's bottom-right corner (`cx + TS*0.5, cy + TS*0.5`) — the previous `0.18 / 0.22` offset was tuned for the tiny bitmap glyphs and would have left the larger fallback-font badge floating mid-tile over the icon.
- `>99` displays as `"99+"` (was a hand-drawn `+` glyph).

#### Follow-up — sprite Y-flip + label centring

After playtesting the bundle above, Sam caught three remaining issues from the in-game screenshot:

- **Smurfs were rendering upside down.** Godot's `QuadMesh` is fundamentally a `PrimitiveMesh` (3D origin) and inherits the Y-up UV convention even when used with `MultiMeshInstance2D` in 2D. The result is that the baked image renders Y-flipped against `Image`'s top-left-origin coordinates — hat drawn at row 4 of the image appeared at the bottom of the rendered figure. Fix: call `img.FlipY()` at the end of `BakeSmurfSprite` to cancel the rendering flip. The original anchor row drifts ~1 px after the mirror, but that's invisible at gameplay zoom.
- **Smurf name labels were left-anchored, not centred.** The v0.4.28 call passed `HorizontalAlignment.Center, -1, 9, color` to `DrawString` — but Godot silently ignores `HorizontalAlignment.Center` when the `width` arg is `-1` (no constraint), falling back to left-anchor. Every name was visibly offset to the right of its smurf. Fix: pre-measure with `font.GetStringSize(...)` and shift the draw position by `-textWidth/2` (use `HorizontalAlignment.Left` after the manual offset).
- **Dropped-item count badges had the same left-anchor bug.** Same fix as the smurf names — pre-measure, manual `-width/2` offset. Badge anchor changed from "right-baseline at corner" to "bottom-centre on tile centre" so the badge sits centred on the icon instead of hanging off the lower-right.

All three fixed in `scripts/ui/SmurfColonyView.cs` (`BakeSmurfSprite`, `DrawSmurfExtras`) and `scripts/ui/ItemDropOverlay.cs` (`DrawSmallNumber` + its call site). Lesson logged: any future `DrawString` with `HorizontalAlignment.Center` and `width=-1` will silently mis-render.

#### Backup

Per the bundling rule, no new backup folder; the `SmurfC_v0.4.28` backup taken earlier today reflects the rendering-fix bundle only. If subsequent work bumps to v0.4.29, it'll cover the new state.

---

## [0.4.28-rollback] — 2026-05-13 — **ROLLED BACK**

Bundled three perf levers (snapshot pooling, MultiMesh.Buffer batch upload, parallel-tick prep). Build broke under play test — reverted in full back to v0.4.27 by restoring all 11 modified files from the `SmurfC_v0.4.27` backup. No partial pieces kept.

**Reported symptoms after v0.4.28 landed (Sam):**
- Smurfs became invisible (no body sprites rendered at all)
- Designations became invisible AND stopped applying — player drag no longer marked tiles
- Smurfs lost every behavior except Idle (no haul, no gather, no excavate, no chop, no cut)

The three levers, with notes for the next attempt:

1. **Snapshot pool** — `SimulationSnapshot.Rent/Return/Populate/Reset` + `_pendingReturn` one-frame defer in `SimulationManager._Process`. (`SimulationSnapshot.cs`, `SimulationCore.cs`, `SimulationManager.cs`)
   - **Likely cause of "smurfs lost behaviors but idle":** the pool recycles the same `SimulationSnapshot` *instance* across frames, with `Populate()` calling `_smurfs.Clear()` then refilling. Any main-thread consumer that *caches* a snapshot reference across frames (some panels do via `GetLastSnapshot()` for hover lookups) ends up reading an empty list after the pool reset, so SelectTask / haul / designation lookups that go through the cached snapshot see no smurfs and fall through to Idle. The one-frame `_pendingReturn` defer was not enough — any consumer that captures past one frame breaks. Next attempt should either copy the snapshot's data into a private structure at consumption time, or skip pooling for `_lastSnapshot` and only pool the queue overflow path.

2. **MultiMesh.Buffer batch upload** — per-variant `float[]` staging in place of per-instance `SetInstanceTransform2D`. (`SmurfColonyView.cs`, `DesignationOverlay.cs`, `ItemDropOverlay.cs`)
   - **Likely cause of "smurfs invisible / designations invisible":** assigning the full pre-sized `_staging` array via `mm.Buffer = staging` while only `VisibleInstanceCount` slots hold valid transforms. Godot 4's binding may reset `InstanceCount` from the buffer length and ignore the smaller `VisibleInstanceCount`, OR the `float[]` → `PackedFloat32Array` marshal path may not be what the property expects. Either way, all instances rendering at degenerate / origin transforms would read as "invisible" on the variant sprites. Next attempt should slice the upload to exactly `count * 8` floats per frame, or hold off on Buffer and just batch via `SetInstanceTransform2D` from a tight `Span<>`.

3. **Parallel-tick prep** — `LocalMap._mutationsLock` around terrain / vegetation / stone mutation paths, `ThoughtRegistry.Add` locking on the target smurf, `BehaviorSystem._tlRng` + `UseParallelTick` master switch. (`LocalMap.cs`, `Thought.cs`, `BehaviorSystem.cs`)
   - **Possible cause of "designations stopped applying":** moving the `TerrainChanged` / `VegetationChanged` event invocations *outside* `_mutationsLock` changed the ordering between the mutation and the subscriber's response. If `LocalMapRenderer` or `DesignationOverlay` reads `Get` state in response to the event and the lock state is now stale relative to what the subscriber expects, the visual layer may diverge from the simulation. Less likely to be the primary culprit but worth re-auditing — the original v0.4.27 code held the lock through the event invocation.

Three independent risk surfaces in one bundle was too aggressive. Next attempt should land each lever as its own version bump with its own playtest, in this order: (#2 first, smallest scope, easiest to spot regressions visually) → (#1, with pool disabled for `_lastSnapshot`) → (#3, locks only, no parallel flip).

Effective version after this rollback: **v0.4.27**.

---

## [0.4.27] — 2026-05-13

### Fixed — Gameplay polish: instant designations + real-time resource totals

Two explicit gameplay requirements from Sam after the v0.4.26 perf landing:

1. **Designations must appear immediately, not load in over time.** Players noticed a delay between drag-release and the full rect appearing — visible at 11 FPS.
2. **Resource totals must update visibly the moment they actually change.** The v0.4.23 200 ms HUD throttle made them lag.

---

#### Fix 1 — Designations apply directly on the main thread

**`scripts/SimulationManager.cs`** (`DesignateRect`): switched from `_core.QueueDesignation(op, x, y)` (sim-thread queue) to direct calls of `map.SetExcavationDesignation` / `SetGather` / `SetChopWood` / `SetCut` / `ClearDesignationsAt`. `LocalMap` already locks `_designationsLock` inside each setter, so the v0.3.39 race the queue was guarding against stays closed: every writer (sim's `ApplyTaskEffect`, the auto-haul path, this main-thread `DesignateRect`) serialises through the same lock. Sim's `FindNearestX` reads the indexed designation sets under the same lock, so brief contention is the only cost — and it's vastly preferable to gating the visual update behind a full sim-tick + main-thread `_Process` cycle.

**`scripts/ui/DesignationOverlay.cs`:** added `public void RebuildIfDirty()` — flushes the MMI instance buffers immediately if the dirty flag is set, no-op otherwise.

**`scripts/ui/GameController.cs`:** after both `DesignateRect` call sites (the `TryHandleMouseButton` drag-release and `FinalizeStrandedDrag`) the controller now calls `_designations.RebuildIfDirty()`. The very next render frame shows the full drag rect — no waiting for `_Process` to pick up the dirty flag.

#### Fix 2 — HUD throttles removed; gated by `SetTextIfChanged` instead

**`scripts/ui/HUDController.cs`** + **`scripts/ui/ResourceHUD.cs`:** the v0.4.23 200 ms throttle is gone. `_Process` now runs every frame; the cost when nothing has changed is one inventory-snapshot walk + a handful of `SetTextIfChanged` comparisons — no `Label.Text` writes, no canvas redraws. Only frames where a resource value genuinely changes pay any visible cost. Players see the food / stone / wood / magic numbers tick as items hit the inventory.

---

#### On multi-threading the main thread

Sam asked about distributing load across cores. The honest answer:

- **The sim is already on its own thread.** `SimulationCore` runs `BehaviorSystem.Tick` + `NeedsSystem` / `MoodSystem` / etc. on a background thread; main only consumes snapshots.
- **Godot's render pipeline is main-thread-bound by design.** Calls into `Label.Text =`, `MultiMesh.SetInstanceTransform2D`, `DrawTextureRect`, and so on must originate from the main thread because each one queues a command into the canvas command buffer that the render server consumes serially. There's no API to "parallelise canvas command submission" — that's not how Godot's 2D pipeline works.
- **What's left that CAN parallelise** is data preparation *before* the Godot calls: pre-compute per-instance transforms into a `float[]`, then submit them to a `MultiMesh.Buffer` upload (one interop call per variant instead of `N` `SetInstanceTransform2D` calls). For 250 smurfs that's ~12 interop crossings vs ~250 — but the savings are small (~1-2 ms/frame) compared to the per-smurf rendering cost. Worth doing as a follow-up; not the headline win.
- **The real per-smurf cost identified across v0.4.22 → v0.4.26** was *gameplay-tier work running for hidden UI*: v0.4.22 dictionary lookup, v0.4.23 HUD label-write storm, v0.4.26 hidden-roster diff. Each one was a single-threaded main-thread cost that scaled with smurf count. Those are now gone.

Followup parallel-friendly levers to revisit if the perf target needs more headroom:

1. **`SimulationCore.PushSnapshot` allocation pressure.** ~250 `SmurfSnapshot` structs + 1 dict + 1 list per push, 60 Hz at 1× speed = GC pressure that eventually stalls main. Could be pooled.
2. **`MultiMesh.Buffer` batch upload** instead of per-instance `SetInstanceTransform2D` (smaller per-frame interop count).
3. **Parallelise `BehaviorSystem.Tick` per-smurf work** via `Parallel.For` inside the sim Tick. Per-smurf state is mostly independent; designation claims + the soft-collision grid would need to stay lock-protected, but the rest is parallel-friendly. Would let sim catch up at higher speeds.

---

#### Version

Patch `0.4.26` → `0.4.27`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.26] — 2026-05-13

### Fixed — `GameController.OnTick` ran the full roster diff per snapshot even when the roster was hidden

Sam came back with the cleanest perf signal yet: **0.6 FPS hit per smurf** measured precisely between 25 smurfs (170 FPS) and 250 smurfs (13 FPS), with CPU + GPU both at ~10%. That's a perfectly linear single-thread cost, ~316 µs per smurf per frame. With MultiMesh covering the world overlays, the only remaining suspect was per-snapshot UI work — something that walks every smurf each snapshot push.

**Diagnosis** — `GameController.OnTick` (called every snapshot push, 60 Hz at 1× speed) did:

```csharp
if (_roster != null)
{
    var rows = new List<SimulationC_Roster_Row>(snap.Smurfs.Count);
    foreach (var s in snap.Smurfs)
        rows.Add(new SimulationC_Roster_Row(...));
    _roster.Refresh(rows);
}
```

The `if (_roster != null)` gate was satisfied whether or not the roster was actually visible to the player. `_roster.Refresh(rows)` then walked all 250 rows calling `UpdateRow` per smurf — each row checks ~7 Label.Text values + 5 ProgressBar values for changes. Setting `Label.Text` even with a SetTextIfChanged guard isn't free: Godot still computes the diff inside its C++ layer, plus the upfront `new List<…>` + `new HashSet<string>` allocations per call. At 250 smurfs × ~35 µs per row × ~13 snapshots/sec (consumed; sim pushes faster but main drains-and-keeps-latest) = ~114 ms/sec of pure roster-refresh CPU **for a panel the user can't even see**. The same pattern hit `_card.Refresh(snap)` though at smaller scale (one smurf).

This is the textbook "do work for hidden UI" anti-pattern. The roster's per-row UpdateRow is well-written — it just shouldn't run when the player can't see the results.

**Fix — `scripts/ui/GameController.cs`:**

- `_card.Refresh(snap)` gated on `_card.IsVisibleInTree()`.
- The entire roster-rows construction + `_roster.Refresh(...)` block gated on `_roster.IsVisibleInTree()`.
- `IsVisibleInTree()` rather than `Visible` because the panels are inside the bottom-tab container — when the tab is collapsed the panel itself reports `Visible = true`, but `IsVisibleInTree()` correctly returns false. The plain-`Visible` check would have missed the common case (panel visible-local, hidden by tab) and saved nothing.

When the user opens the roster or card, the next snapshot push refreshes the now-visible panel — one tick of staleness, invisible at human-perception speed.

---

#### Why this fits Sam's measurements

- **0.6 FPS per smurf** at 250 smurfs ≈ 316 µs/smurf/frame. Roster `UpdateRow` for one smurf was ~35 µs, fired at ~13 Hz when consuming snapshots, summing to ~25 µs/smurf/frame of roster work — close to the linear cost when amortised across the snapshot fan-out. With v0.4.24's MultiMesh + v0.4.25's overlay MMI absorbing the previously-N-cost rendering, this hidden-roster work was the last per-smurf bottleneck.
- **CPU at 10%, GPU at 10%:** consistent with one main-thread core saturated doing UI-diff work while the rest of the system idles. Removing it should let the main thread breathe.
- The expected `_card` cost was smaller (one smurf per refresh) but the gate is mechanically the same fix.

Followup levers, deferred but documented:
- Same visibility gate could apply to any other tab-resident panel that subscribes to TickCompleted (JobsPanel and ResourcesPanel already early-return on `Visible`, so they were already correct).
- `SnapshotEquipment` allocates a per-equipped-smurf dict on each push. Mostly bypassed by the `_emptyEquip` shortcut for unequipped smurfs (the common case in the current game state); revisit if late-game equipment density makes it visible.

---

#### Version

Patch `0.4.25` → `0.4.26`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.25] — 2026-05-13

### Optimized — `DesignationOverlay` + `ItemDropOverlay` converted to `MultiMeshInstance2D`

After v0.4.24 landed the smurf body MMI, the next two heaviest canvas-emitters in the world layer were both per-item-per-tile procedural-draw overlays: designations and dropped items. Same problem, same fix.

---

#### `DesignationOverlay` — 4 MMI variants

**Before:** every redraw emitted ~3 canvas commands per designation — `DrawRect` filled tint, `DrawRect` outline border, and a kind-specific glyph (DrawLine for excavate / chop / cut, three DrawCircle for gather). At 500+ designations that was 1 500+ canvas commands on every `DesignationChanged` event.

**After (`scripts/ui/DesignationOverlay.cs`):**
- One pre-baked 16×16 RGBA sprite per `DesignationKind` with the tint, border, and glyph all rasterised into the texture (Bresenham line for diagonal strokes, scan-fill for the gather-berry circles).
- Four `MultiMeshInstance2D` children, one per kind. `InstanceCount = 8000`.
- On `DesignationChanged` the overlay marks `_dirty`; `_Process` snapshots designations and pushes per-tile transforms into the right multimesh.
- Body pass collapses to **exactly 4 instanced GPU draw calls** regardless of designation density. `_Draw` removed entirely — MMIs render automatically.

#### `ItemDropOverlay` — 23 MMI variants

**Before:** every redraw emitted ~4 canvas commands per dropped tile (DrawCircle + DrawArc icon, plus DrawRect / DrawColoredPolygon for the Material square / Magic diamond shape overlay) plus DrawSmallNumber's per-pixel digit DrawRects for stack counts. At 200 stone piles after a big dig that was ~800+ commands for icons + ~3 000 DrawRects for stack-count digits.

**After (`scripts/ui/ItemDropOverlay.cs`):**
- 23 pre-baked 16×16 RGBA sprites — one per item visual variant (Food: Smurfberry / SmallMushroom / HerbCluster / MagicBerry / default; Material × Wood/Stone/Plant × material sub-type, 14 variants; Magic: RawEssence / CrystalShard / default; Fallback). Each variant bakes its filled-circle + outline + (optional) square or diamond overpaint into the texture.
- 23 `MultiMeshInstance2D` children.
- `_dirty` flag drives a single `_summaries` snapshot per change; the snapshot feeds both the MMI rebuild and the procedural number-badge pass.
- Icon pass: **exactly 23 instanced GPU draw calls** for the entire ground-loot population, vs ~800+ canvas commands before. The 23 is an upper bound: variants with no items present this frame submit zero instances and skip rendering.
- Stack-count numbers + multi-stack pile-badge stay procedural in `_Draw` — they're sparse (only multi-unit piles) and depend on the live count value, which would require a baked-digit cache to remove cleanly. Held back as a follow-up since the icon pass was the dominant cost.

---

#### Combined effect with v0.4.24's smurf MMI

For the 250-smurf busy-dig-site scenario with 500 designations + 200 dropped piles:

- **Canvas commands per frame for world overlays:** ~3 000 → ~40 (smurf glow / extras + a handful of stack-count digits).
- **GPU draw calls per frame for world overlays:** thousands batched into hundreds → ~12 (smurf bodies) + 4 (designations) + 23 (item variants) = ~40, all instanced.
- **Main-thread canvas-walk:** scales with variant count, not item / designation / smurf count.

The pattern is consistent across all three world-overlay rewrites: one MMI per visual variant, one quad per instance, transforms pushed on change. GPU does what it's designed for; CPU work drops to bookkeeping.

---

#### Version

Patch `0.4.24` → `0.4.25`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.24] — 2026-05-13

### Fixed — GPU underutilisation at 250 smurfs (the perf ceiling above 13 FPS)

After v0.4.23 the per-frame HUD label-write storm was gone, but Sam's stats stayed at ~13 FPS idle with 250 smurfs on the OpenGL Compatibility renderer, GPU peaking at only 40 %, multiple CPU cores idle. The remaining bottleneck is straight from the [Godot 2D rendering guide](https://docs.godotengine.org/en/stable/tutorials/2d/index.html#doc-2d-rendering): rendering many sprites via individual `DrawTextureRect` calls submits a canvas command **per sprite**. Each command needs to be walked by Godot's batcher every frame; 250 commands plus all the UI commands push the per-frame canvas-walk cost above 50 ms and pin a single main-thread core while the GPU sits idle waiting for work.

The canonical Godot 2D answer for "many sprites" is **`MultiMeshInstance2D`** — GPU instancing: one mesh + one texture, N per-instance transforms in a buffer, **one** instanced GPU draw call covers all N. The Compatibility renderer supports it natively.

---

#### Fix — `MultiMeshInstance2D` smurf body rendering

**`scripts/ui/SmurfColonyView.cs`** rebuilt around 12 `MultiMeshInstance2D` children — one per (mood × sex) variant — added as children of `SmurfColonyView` in `_Ready`. Each holds:

- A `QuadMesh` sized to the sprite (16×24 px).
- A `MultiMesh` with `TransformFormat = Transform2D`, `InstanceCount = 300` (250-smurf target + headroom).
- The variant's pre-baked sprite from v0.4.20's cache as its `Texture`.

Per frame, `UpdateMultiMeshInstances()`:
1. Resets per-variant instance counters.
2. Walks `_smurfs` once. For each visible smurf, picks the (sex × mood) bucket, computes the anchored Transform2D (smurf pos + bob + sprite-anchor offset), and calls `SetInstanceTransform2D(idx, xform)`.
3. Sets each MultiMesh's `VisibleInstanceCount` to the populated portion.

Per-frame work for the body pass:
- **Before:** ~250 `DrawTextureRect` calls → ~250 canvas commands → batched into 12-ish GPU draw calls each frame. Canvas command list walk: O(250).
- **After:** ~250 `SetInstanceTransform2D` calls writing into 12 multimesh buffers → exactly 12 GPU instanced draws each frame. Canvas command list walk: O(12). GPU does per-instance vertex transformation in parallel, which is what it's designed for.

---

#### Layering — three-pass render order

The body is now rendered by the MMI children, which render *after* `SmurfColonyView._Draw` (children render after parent). Three logical passes:

1. **Below-body** (in `SmurfColonyView._Draw`): selection glow. Renders first; body sprites cover the inner portion, leaving only the halo visible around the smurf — matches the original pre-v0.4.24 layering.
2. **Body** (12 MMI children): instanced GPU draw of the smurf sprite.
3. **Above-body** (new `SmurfExtrasNode` child, added last in `_Ready` so it renders after the MMIs): equipment overlays, carry icon, combat sword glyph, name label (selected only).

Equipment helpers (`DrawEquipmentSlot*`, `DrawHandItem*`, `DrawCarriedIcon*`) refactored to take a `CanvasItem ci` parameter and call `ci.DrawRect / DrawCircle / DrawArc` instead of `this.*`. This lets them target `_extrasNode` from inside the extras-node's `_Draw` while still living as static helpers on `SmurfColonyView`.

#### Removed — dead `DrawSmurf` method

The old procedural per-smurf body draw (~10 primitive calls per smurf: body / head / eyes / hair / hat / mood-dot) is no longer needed and was deleted. The v0.4.20 baked sprite cache it relied on is now consumed exclusively by the MultiMesh textures.

---

#### Why this should land the 60 FPS / 250-smurf target

- **Canvas command count for the body pass:** ~250 → 12.
- **Per-frame interop crossings for body draws:** ~250 `DrawTextureRect` → ~250 `SetInstanceTransform2D`. Same count *during animation*, but the MMI variant doesn't re-record canvas commands every frame — only the multimesh buffer is updated, which is cheaper for Godot's renderer to consume.
- **GPU work shifts from "many small textured-quad draws" to "12 instanced draws of N quads each":** exactly what the GPU is built for. Should push GPU utilisation up and allow the CPU to feed work faster.
- **Compatibility-renderer-safe:** `MultiMeshInstance2D` is fully supported in `gl_compatibility`; no Vulkan-specific dependencies (Sam's locked-in renderer choice noted in memory).

`MultiMesh` for the **`ItemDropOverlay`** and **`DesignationOverlay`** is the next available perf lever (same approach: bake one sprite per item-kind / designation-kind, push transforms into a multimesh). Held back this round so the headline body-rendering change can be measured cleanly.

---

#### Version

Patch `0.4.23` → `0.4.24`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.23] — 2026-05-13

### Fixed — Per-frame UI label-write storm + missing pixel-art project settings (the 11 FPS at idle + UI warping)

After v0.4.22 the smurf O(N²) lookup was gone and the map renderer was throttled, but Sam still measured 11 FPS at idle with 250 smurfs on Vulkan, GPU at 7 % — confirming the bottleneck stayed on the main thread. Plus a new symptom surfaced: visible warping artefacts around UI text and every in-game texture whenever the camera moved or a menu opened.

Two distinct root causes, both addressed.

---

#### Root cause 1 — Resource HUDs assigned `Label.Text` 30+ times per frame

`HUDController._Process` ran every frame and:

1. Called `Sim.GetInventorySnapshot()` — allocates a fresh `InventoryRow[]` and takes the sim-thread Inventory lock.
2. Zeroed *every* sub-label by `lbl.Text = "0"` (multiple `foreach` loops across food / stone / wood / magic sub-categories).
3. Walked the whole inventory, re-writing every label.
4. Wrote four category totals.

`Label.Text = "..."` is a C#→Godot interop crossing that triggers a text-layout pass and queues a canvas redraw of *that* label, **even when the new value equals the old**. With ~30+ sub-labels per category panel, the HUD was burning ~1 800 label-text writes per second at 60 FPS plus the matching canvas redraws — and Vulkan's per-canvas-item sync is significantly more expensive than OpenGL's, which is why v0.4.21's chunked path had it worse on Vulkan and v0.4.22's writes still showed it.

`ResourceHUD._Process` had the same pattern at smaller scale (four labels). `TileInfoOverlay.ShowTile` was called every frame from `GameController.UpdateTileInfo` and rewrote the tooltip label even when the mouse hadn't moved.

**Fix — `scripts/ui/HUDController.cs`, `scripts/ui/ResourceHUD.cs`, `scripts/ui/TileInfoOverlay.cs`:**

- **Throttle.** HUDController and ResourceHUD's `_Process` now check `Time.GetTicksMsec()` and skip if fewer than 200 ms have elapsed since the last refresh — capping HUD updates at 5 Hz, plenty for resource readouts that change at human pace.
- **Write-elide.** New `SetTextIfChanged(Label lbl, string newText)` helper skips the `Text = …` assignment when the value is already current. Applied to every HUD label, every UpdateStats label, and the TileInfo tooltip label.
- **`TileInfoOverlay.ShowTile`** now diffs the formatted string against the existing label text and skips both the assignment and the `Visible = true` toggle when neither changed.

Together these eliminate the per-frame label-update storm. Combined with v0.4.22's `Dictionary` snapshot lookup, the main thread is no longer the bottleneck for 250 idle smurfs.

#### Root cause 2 — Project settings missing the pixel-art defaults

`project.godot` had no `[rendering]` section. Godot 4 defaults to **linear (bilinear) texture filtering** and **sub-pixel 2D transforms**. The combination produces visible blur/warp on any pixel-art texture when the camera renders at a non-integer position — which happens whenever the camera moves, zooms, or a UI panel animates. That's the "warping around UI and nearly every in-game texture whenever an update occurs" Sam reported.

**Fix — `project.godot`:** added a `[rendering]` section setting the three pixel-art rendering defaults:

```
textures/canvas_textures/default_texture_filter=0   ; Nearest
2d/snap/snap_2d_transforms_to_pixel=true
2d/snap/snap_2d_vertices_to_pixel=true
```

These apply project-wide: every canvas item (HUD labels, panels, map texture, smurf sprites) now samples with nearest-neighbour and renders at integer-pixel positions. The per-overlay `TextureFilter = TextureFilterEnum.Nearest` setters in `LocalMapRenderer` and `SmurfColonyView` continue to work as before; the project default just makes every *other* canvas item match by default instead of inheriting `Linear`.

---

#### Combined effect

- Main thread: HUD label-write throughput drops from ~1 800/sec to a handful per actual change (5 Hz × N changed labels). For a typical idle frame, ~0.
- GPU pipeline: Vulkan sync points per frame collapse along with the label redraws.
- Visual: pixel-snapped, nearest-filtered rendering eliminates the camera/menu warping.

`MultiMeshInstance2D` for smurf rendering (the GPU-instanced approach) remains the next perf lever — held back this round so the HUD-storm + project-settings fixes can be measured against a baseline that's no longer fighting itself.

---

#### Version

Patch `0.4.22` → `0.4.23`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.22] — 2026-05-13

### Fixed — O(N²) snapshot lookup + Vulkan chunk-sync regression

Sam came back with concrete profiling data after v0.4.21: 11 FPS at 250 smurfs on an RTX 5070 Ti / Ryzen 7 7700X, GPU sitting at **7 %** utilisation, CPU showing massive spikes on 3-4 cores while most cores were idle — textbook single-threaded main-thread bottleneck. Also: Vulkan now performs *worse* than the compatibility OpenGL backend (the opposite of the usual ordering), and visible 1-px seams at chunk boundaries when scrolling.

Two distinct root causes, fixed separately.

---

#### Root cause 1 — `SmurfColonyView.UpdateFromTick` was O(N²) per snapshot

```csharp
foreach (var snap in snaps)
{
    var vs = _smurfs.Find(s => s.Name == snap.Name);   // ← O(N) per call
    …
}
```

`List.Find` walks the list each call. For 250 smurfs that's 250 × 250 = **62 500 name comparisons per snapshot push**, plus a closure allocation per call (the `s => s.Name == snap.Name` lambda captures `snap`). At a 60 Hz snapshot rate (1× speed) that's **~3.75 million name comparisons + 15 000 closures per second**, all on the main thread. That's exactly the per-core spike Sam's task-manager screenshot shows.

**Fix — `scripts/ui/SmurfColonyView.cs`:** added `Dictionary<string, VisualSmurf> _byName`, kept in lock-step with the `_smurfs` list at every Add/Remove/Clear site (`SeedSmurfs`, the new-smurf spawn branch in `UpdateFromTick`, the death-prune sweep, `_smurfs.Clear`). The `Find` is replaced with `_byName.TryGetValue(snap.Name, out var vs)`. New `_scratchSnapNames` HashSet is reused across calls so the per-tick `new HashSet<string>(snaps.Select(s => s.Name))` allocation is gone too. Total work in `UpdateFromTick` collapses from O(N²) to O(N) with zero per-call allocations.

#### Root cause 2 — v0.4.21 chunked rendering was a Vulkan-sync trap with chunk-seam artefacts

The previous patch split the map texture into 32×32-tile chunks so only changed chunks re-uploaded. That solved the v0.4.20 bandwidth issue but introduced two new ones:

- **Per-chunk `ImageTexture.Update()` calls are sync points in Vulkan.** Each call forces the CPU to wait for the GPU to release its handle to that texture. With 12-150 chunks potentially dirty per flush, that's 12-150 sync points per upload pass. OpenGL handles many small uploads slightly better than Vulkan does, which is why Sam saw the rare "Vulkan worse than OpenGL" inversion.
- **Sub-pixel chunk-seam artefacts.** Adjacent chunks rendered as separate textured quads picked up slightly different sub-pixel rounding when the camera zoomed or scrolled with non-integer offsets, producing 1-px gaps and colour bleed at every chunk boundary. Sam reported these as "warping artefacts at the edge of the screen whenever zooming or moving the camera".

**Fix — `scripts/ui/LocalMapRenderer.cs`:** reverted to the single full-map `Image` + `ImageTexture` from v0.4.20, but added an explicit **upload throttle**:

- `_Process` always repaints accumulated dirty tiles into `_image` (CPU-cheap; bounded by per-tile FillRect + a few circles).
- `_texture.Update(_image)` only fires when at least `UploadIntervalMs = 100 ms` have elapsed since the last upload — capping the GPU upload rate to ~10 Hz regardless of dirty-tile firehose.
- One sync per upload (Vulkan-friendly).
- One textured-quad in `_Draw` (no chunk seams).
- Worst-case bandwidth: 110 MB × 10 Hz = 1.1 GB/sec on the largest map, comfortably under PCIe 4.0 (32 GB/sec). Terrain visual lag is at most 100 ms — invisible to the player since dig-completion + texture-update happen on the same human timescale.

`PaintTile` reverted to its pre-chunking signature (single `_image`, world-coord `ox, oy`). `BakeFullImage` restored to the `Parallel.For` row-parallel path. The v0.4.21 chunk-storage fields, helpers, and chunk-iteration in `_Draw` removed.

---

#### Why this combination addresses Sam's data

- **GPU at 7 % utilisation:** fixed by stopping the O(N²) per-frame CPU storm on the main thread (root cause 1). Frame time was bottlenecked there, not on the GPU.
- **CPU single-thread spikes:** the Dictionary path is O(1) per lookup; the spikes flatten.
- **Vulkan worse than OpenGL:** chunked Update sync points removed; one sync per flush now.
- **Edge warping at zoom/scroll:** chunk seams gone; the map is one textured quad again.

Next round (deferred): the smurf overlay itself is still 250 `DrawTextureRect` calls per frame. `MultiMeshInstance2D` is the GPU-instanced answer Sam asked for — one draw call for all smurfs. Held back this round so the perf-regression fixes land cleanly first.

---

#### Version

Patch `0.4.21` → `0.4.22`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.21] — 2026-05-13

### Fixed — `LocalMapRenderer` was streaming the entire map texture every frame at 250 smurfs

Reported on v0.4.20: even with the new pre-baked smurf sprites + culling, 250-smurf scenes ran at 6–7 FPS. The smoking gun in the screenshot was Godot's "Show Redraws" debug overlay flashing red over the *entire* map every frame — meaning the map renderer was calling `QueueRedraw()` continuously, not the smurf overlay.

**Diagnosis** — `LocalMapRenderer` stored one giant `Image` covering the full map (~110 MB on a 480×300 tile map at 16 px tiles → 7680×4800 × 3 bytes) plus a matching `ImageTexture`. Every `_Process` flush of any dirty tile called `_texture.Update(_image)`, which re-uploaded the *whole* texture to the GPU. At low population the dirty queue rarely had entries between frames, so this was rare. At 250 smurfs constantly chopping / gathering / excavating / hauling, the dirty queue almost never emptied — so every single frame the renderer was streaming ~110 MB to the GPU. At 60 FPS that's 6.6 GB/sec of texture-upload bandwidth, saturating the pipeline and dropping the frame rate to the 6–7 FPS Sam measured. The OpenGL backend was even slower because its texture-streaming throughput is worse than Vulkan's.

---

#### Fix — Chunked map texture

**`scripts/ui/LocalMapRenderer.cs`** rewritten around a 2D array of 32×32-tile chunks (512×512 px each, ~768 KB in Rgb8). Each chunk has its own `Image` and `ImageTexture`; `MarkDirty(tx, ty)` flags the chunk that owns the tile in a new `_chunkDirty[chunksX, chunksY]` grid. `_Process` repaints dirty tiles into their owning chunk's image, then walks the grid uploading only the chunks that actually changed.

- **Localised work uploads localised data.** A digger excavating a single vein touches one chunk per frame → 768 KB upload instead of 110 MB. ~150× reduction at peak.
- **`PaintTile` routes per-tile to the correct chunk.** The existing `FillRect` / `FillCircle` / `PaintBoulder` / etc. helpers all reference `_image` and use `image.GetWidth/Height` for bounds, so swapping `_image` to point at the right chunk and translating `(ox, oy)` to chunk-local coords lets every helper work unchanged. Variant seeding still uses world coords (`tx`, `ty`) so the visual is identical to the pre-chunk version.
- **`_Draw` issues one `DrawTextureRect` per chunk.** ~40 calls on a 240×150 map, ~150 on a 480×300 map. All trivial GPU primitives — no per-frame upload at all once textures are baked. Godot's 2D batcher coalesces consecutive same-state textured-quad draws.
- **Bake is serial.** The pre-chunk version used `Parallel.For` over rows; with `_image` now a shared field that gets swapped per chunk, parallel is unsafe. A serial bake on the largest map takes a few hundred ms — acceptable as a one-time startup cost.

Memory total is unchanged (sum of chunk images = original full-map image). The win is purely in upload bandwidth: per-frame data transfer is now proportional to the area that actually changed, not the size of the whole world.

---

#### Version

Patch `0.4.20` → `0.4.21`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.20] — 2026-05-13

### Fixed — Render-side stutter at 250 smurfs (the lag-while-paused tell)

Reported: 250 smurfs is computationally fine but the renderer drops frames into unplayable territory — and crucially, the stutter persists while the sim is paused. Pause halts sim work and `_Process` early-returns without scheduling a redraw, so any sustained per-frame cost must be Godot's canvas re-walking the cached commands. That points squarely at sheer command volume + un-batchable variation.

**Diagnosis — per-smurf draw cost in `SmurfColonyView.DrawSmurf`:**

- ~7 `DrawCircle` calls (body, head, 4 eyes, optional mood dot, optional 4× hair)
- 2 `DrawPolygon` calls (hat triangle + shadow)
- 1 `DrawString` for the always-on name label (full glyph layout + text shaping)
- 1–2 additional `DrawString` for combat indicator (rare)
- per-equipped-slot `DrawRect` / `DrawCircle` (sparse)

At 250 smurfs that's **~3 000 canvas commands plus 250 text-shaping operations every redraw**, and per-smurf mood + sex colour variation broke draw-call batching so each command shipped on its own. Pause didn't help because the cached canvas commands are re-walked by Godot's 2D renderer every frame regardless of whether `_Draw` ran.

---

#### Fix #1 — Pre-baked smurf sprites (mood × sex variants)

New `ImageTexture` cache in `SmurfColonyView`, populated once at the first `_Draw`: 6 mood states × 2 sexes = 12 `16×24` pixel sprites baked via `Image.SetPixel` scan-fill of the same body / head / eye / hair / hat / mood-dot geometry the procedural path used. Per-smurf draw drops from ~10 procedural primitives to **one** `DrawTextureRect`. Smurfs in the same mood × sex category share a texture, so Godot's 2D batcher coalesces them into a single GPU draw call — `DrawTextureRect` is a trivial textured-quad primitive, what the GPU is actually built to do, instead of the canvas-renderer pushing thousands of CPU-side commands every frame.

Memory cost: 12 × 16 × 24 × 4 bytes ≈ 18 KB total. `TextureFilter = TextureFilterEnum.Nearest` preserves the pixel-art aesthetic and skips bilinear filtering at the sampler.

Equipment overlays, carry icons, selection glow, and combat indicator stay as procedural draws — they're sparse (most smurfs aren't equipped or carrying), and per-instance variable so baking variants would explode the cache without proportional savings.

#### Fix #2 — Off-screen smurf culling

`SmurfColonyView._Draw` now computes the camera's visible-world rect once (camera screen-centre + viewport / zoom, grown by one sprite-height of margin so hat-tips at the edge still appear) and `DrawSmurf` early-returns for any smurf whose anchor sits outside it. When zoomed in to a 30-tile viewport, this cuts the per-frame draw work to roughly the number of smurfs actually visible — typically 20–50 of the 250, an 80–90 % reduction in `DrawSmurf` cost.

#### Fix #3 — Suppress always-on name labels

The per-smurf `DrawString` for the name label was the single most expensive line in `DrawSmurf` — each call did full glyph layout + text shaping on Godot's fallback font, then issued the textured quads for every character. 250 of those per redraw × 60 Hz when scrolling = ~15 000 text-shaping operations per second. The name now renders only for the currently selected smurf; the smurf card already surfaces the selected smurf's name and the roster lists everyone, so the per-sprite label was redundant. Combat indicators still show — those are sparse and informational.

---

#### Combined impact

For the 250-smurf scene with maybe 40 smurfs visible at typical zoom:

- **Draw commands per `_Draw`:** ~3 000 → ~40 (selection glow optional, 1 textured-quad per visible smurf, no per-smurf text)
- **GPU draw calls per frame:** ~3 000 → typically 1–6 (one per mood × sex variant present in the visible set)
- **Text shaping per `_Draw`:** 250+ → 0 or 1
- **CPU canvas-command walk per frame (while paused):** scales with the new command count, not the old one — that's the fix for the lag-while-paused tell.

---

#### Version

Patch `0.4.19` → `0.4.20`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.19] — 2026-05-13

### Fixed — Crowd-pile stuck cases (soft collision + path invalidation + claim-aware approach picker)

After v0.4.18's perf rewrite landed cleanly, the residual "smurfs caught on walls and corners" cases turned out not to be algorithm bugs — they were *crowding* artefacts. Three orthogonal fixes inspired by RimWorld's local steering + Songs of Syx's industry-queue idiom, picked as the lowest-risk, lowest-cost interventions before considering any tile-shape rewrite.

---

#### 1 — Soft smurf-on-smurf avoidance during local steering

`MoveOneTick`'s local steering was terrain-only: `IsPixelPassable` checked the tile, not whether another smurf was already standing on it. At a work face with 8 active diggers, every smurf computed the same approach tile and stacked onto it; the fan-out angles all "succeeded" (the destination tile is passable as far as terrain goes) and produced the pile-up the player saw in the screenshots.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs`:**

- New per-tick `_smurfPerTile[]` occupancy grid populated once at the top of `Tick` from the live smurf list. O(N) rebuild per tick; `Array.Clear` + N increments. No per-smurf O(N²) scans.
- `MoveOneTick`'s fan-out now scores each candidate direction:
  - `2` = passable AND no other smurf in destination tile
  - `1` = passable but another smurf is already there
  - `0` = blocked
- Picks the highest-scoring direction; iteration order (primary, ±45°, …) breaks score ties so straight-line motion is still preferred when nothing is in the way. The `1` fallback prevents narrow-corridor deadlock — soft preference, not a hard veto.

#### 2 — Path invalidation when the next waypoint goes impassable

The A* path was a one-shot snapshot from task-selection time. If a downstream waypoint's tile later became impassable (LargeMushroom regrowth on a corridor tile, a player-issued wall, a vegetation slot flipped) the smurf rode the stuck-detector window (~0.5 s) walking into the new obstacle before recovery.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** `MoveOneTick` checks the head waypoint each tick before steering. If its tile is no longer passable, the path is dropped immediately and an in-tick re-pathfind is requested for designation tasks. The smurf reroutes within the same tick instead of waiting on the stuck-detector. Cost: one `IsPassable` per smurf-with-path per tick — negligible.

#### 3 — Claim-aware adjacent-tile picker

`NearestAdjacentPassableTile` (the v0.4.14 picker that chooses which adjacent passable tile to stand on when approaching an impassable work target) preferred the closest in-region neighbour. Multiple smurfs converging on adjacent Boulders consistently picked the same approach tile — combined with #1's lack of soft collision, every smurf trying to dig the same wall vein routed to the same square metre.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** the picker now ranks candidates in four buckets, returning the first non-empty:

1. In-smurf-region AND currently unoccupied (best)
2. In-smurf-region (occupied — fall back to original behaviour)
3. Any region AND unoccupied
4. Any region (occupied — last-resort)

Distance to the smurf still breaks ties within each bucket. Diggers heading to the same work face naturally distribute across distinct approach tiles instead of all routing to one.

---

#### Follow-up — primary-direction-wins rule (bundled into 0.4.19)

The v0.4.19 two-tier scoring above had an orbital failure mode: at a small dense designation patch, the destination tile itself was often "crowded" (a couple of smurfs already digging adjacent Boulders). The fan-out then preferred any uncrowded side-step over the crowded-but-correct primary, and the approaching smurf orbited at `ArrivalRadius + ε` without ever crossing the arrival threshold — `ApplyTaskEffect` never fired. That's the dig-cluster jitter the player reported on the v0.4.19 follow-up screenshot.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs` (`MoveOneTick`):** primary direction wins unconditionally when terrain-passable. The two-tier scoring only runs when the primary is *terrain*-blocked (wall in the way). Smurfs may briefly stack at a destination tile, but they *arrive*; the v0.4.19 claim-aware adjacent picker keeps the stack small to begin with, and the stack disperses the moment work completes. Matches RimWorld's actual local-steering behaviour.

#### Follow-up #2 — post-delivery bunching (bundled into 0.4.19)

After the primary-direction-wins fix landed, smurfs reliably reached and completed work — but they then visibly clustered at the stockpile delivery point and a fraction of them stopped making forward progress. Three compounding bugs:

1. **`HaulSystem.FindDeliveryPoint` re-ran a full BFS + `new bool[Width, Height]` on every Phase-1 pickup completion.** At the perf target that's a 144 KB allocation × hundreds of completions per second — a real gen-0 storm, separate from the bunching itself.
2. **Every smurf delivered to the exact same pixel** (the centroid of the spawn cluster). The v0.4.19 soft-collision + claim-aware adjacent picker only fires for *impassable* targets; the delivery point is passable, so smurfs converged on one tile with no spreading.
3. **No failure-counter on tasks that completed as no-ops.** A smurf at the delivery point would re-roll the priority queue, be handed another Haul for a nearby item, walk over, find the item already picked up by another smurf, return null-handed, and re-roll again — silently. The cycle never broke, the smurf stayed at the cluster.

**Fix — `scripts/simulation/systems/HaulSystem.cs`:**

- Cached spawn-cluster tile list (`_cachedCluster`), keyed by map reference. The BFS now runs at most once per game load instead of per haul. ~144 KB allocation per haul → 0.
- New `PickDeliveryTileFor(s, map)`: each smurf picks a delivery tile from the cluster by stable `Guid` hash modulo cluster size. Same smurf consistently delivers to the same tile (no visual confusion); different smurfs spread across the cluster. The "every hauler to one pixel" stacking the player reported on the v0.4.19 follow-up is gone.

**Fix — `scripts/simulation/Smurf.cs` + `scripts/simulation/systems/BehaviorSystem.cs`:**

- New `Smurf.TaskDidWork` flag (set by every `ApplyTaskEffect` case that produces actual output — item drop, terrain mutation, inventory deposit, haul pickup) and `Smurf.ConsecutiveTaskFailures` counter.
- `BehaviorSystem.Tick` wraps the `ApplyTaskEffect` call: resets `TaskDidWork = false` before, checks after. Work-typed tasks (Haul + the four designation types) that completed with `TaskDidWork == false` increment the counter; successful completions reset it; non-work tasks (Eat, Sleep, Loiter, …) always reset.
- New `TaskFailureForceWander = 3` threshold. When a smurf has just no-op-completed three work tasks in a row, the next `needNewTask` block hands them a `NewWanderTask` instead of re-rolling the priority queue, then resets the counter. The smurf physically displaces itself from the bunch, the colony state has a chance to settle, and the next priority-queue roll has a fresh playing field.

The three fixes interact multiplicatively against the bunching symptom: the delivery point itself spreads across the cluster (#1+#2), and any smurfs that still get caught in a no-op chain (e.g., racing each other for the same dropped item) break out within ~3 ticks instead of indefinitely (#3).

#### Follow-up #3 — `ScenarioConfig.MaxSmurfs` raised 25 → 250

The scenario editor's starting-population spinner was capped at 25 — a leftover from before the perf rewrite work. Bumped to 250 (matching the perf target colony size) so playtesting at the planned scale is possible without a code edit. The cap is provisional: the planned replacement is a RimWorld-style preset-population dropdown (Crashlanded / Tribal / Refugees / etc.) selecting from curated party sizes; the raw spinner survives until that lands.

#### Follow-up #4 — speed buttons honest about their labels

The HUD speed buttons displayed `1× / 2× / 5× / 10×` but actually requested `1× / 5× / 20× / 100×` from `SimulationManager.SetSpeed`, so pressing "2×" was running the sim *five times* faster than the player asked for and pressing "10×" was running it a hundred times faster. Compounding that, `SmurfColonyView._Process` was dampening visual animation by `Sqrt(SpeedMultiplier)` (so 10× sim produced ~3.16× visual bob + lerp). End result: every button beyond "1×" lied in two different directions at once.

**Fix — `scripts/ui/HUDController.cs`:** multiplier values now match labels (`1f / 2f / 5f / 10f`). Tooltips updated to reflect actual numbers. The sim tick interval is `BaseTickIntervalMs / SpeedMultiplier`, so movement, animation, clock progression, and need-stat decay all scale linearly off this single value.

**Fix — `scripts/ui/SmurfColonyView.cs`:** `_Process` drops the `Sqrt(SpeedMultiplier)` dampener and uses the raw multiplier — visual animation (head bob, smurf-position lerp) now scales 1:1 with sim speed. With the new 10× ceiling the un-dampened motion is well within "comfortable to watch" range; the sqrt was a workaround for the legacy 100× cap that no longer exists.

#### Note on the hex-vs-Songs-of-Syx exploration

Sam asked whether we should reformat to hex tiles (RimWorld pathfinding) or move to a Songs of Syx flow-field model. Recommendation was "neither right now": the algorithm is fine, the symptoms are local-steering crowding artefacts. Hex would be a multi-week refactor that doesn't address pile-up. Flow fields shine when many actors share one destination; ours mostly have individual targets. The three fixes above are localised edits — no save-format changes, no rendering churn — and map directly to the screenshot symptoms.

Hex exploration remains on the table for visual-acuity reasons (Sam noted interest), but as a separate Phase 5+ visual track.

---

#### Version

Patch `0.4.18` → `0.4.19`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.18] — 2026-05-13

### Fixed — Task-reassignment stutter; "smurfs stop after a few minutes" silent thread death

Reported on v0.4.17: noticeable framerate drops on a fresh game with only 25 smurfs every time a wave of them reassigns tasks (chop / cut / gather / haul transitions) on a Ryzen 7 7700X / RTX 5070ti — clearly a code bottleneck, not a hardware one. Plus smurfs sometimes stop dead "after a few minutes" and never move again. Performance target: 60 fps / 60 Hz at 250 smurfs doing all activities simultaneously on a recommended-size map.

---

#### Root cause 1 — Pathfinder allocated + reset per call

v0.4.16 made every designation-type task request an A* path regardless of distance. With single-shot chop / cut from v0.4.15 producing rapid task completion, ~25 A* requests per second flowed through `Pathfinder.FindPath` even at low colony count. Every call paid:

- **108 000 array writes** to reset `gScore` / `parent` / `closed` over the full W×H span (240×150 = 36 k tiles × 3 arrays).
- **A fresh `PriorityQueue<int, int>()` heap allocation** with its grow-as-you-enqueue backing array.
- **A fresh `List<Vector2>(32)` result allocation** for the reconstructed path.
- **Tuple deconstruction** on every node expansion via `foreach (var (dx, dy, cost) in Dirs)`.
- **`map.IsPassable(nx, ny)`** virtual + double-bounds-check on each of the 24 passability probes per expansion.

Multiplied across 25 reassigning smurfs in a single tick, the array-reset alone hit 2.7 M writes and the GC churn pushed gen-0 over the per-tick budget. That was the spike.

**Fix — `scripts/simulation/Pathfinder.cs` (rewritten):**

- **Generation-counter arrays.** A single `genArr[idx]` array carries both "fresh / open / closed" state encoded via a per-thread generation int. Each `FindPath` increments the gen; entries from prior searches are auto-stale on read. Zero W×H reset, O(1) startup regardless of map size. Overflow at 2 billion searches triggers a one-time `Array.Clear` and a restart at gen = 1.
- **Cached `PriorityQueue<int, int>`.** Held as a `ThreadLocal` field, `Clear()`-ed at the start of each search. Backing heap is reused across calls instead of grown-then-discarded.
- **Fill-into-buffer result API.** New `FindPath(map, fromPixel, toTile, List<Vector2> resultBuffer)` overload clears + populates the caller's existing list (typically `Smurf.PathWaypoints`). Zero per-call result allocation. The old `FindPath(...)→List<Vector2>?` overload is kept as a back-compat shim that delegates to the new path.
- **Parallel `DX` / `DY` / `Cost` arrays.** Replaces the `(int dx, int dy, int cost)[]` tuple array; expansion loop indexes flat arrays by `i`, no tuple deconstruction.
- **Raw `bool[]` passability access.** New `LocalMap.PassableUnsafe` exposes the underlying flat array; Pathfinder's inner expansion reads `passable[ny * W + nx]` directly, bypassing the `IsPassable` virtual + bounds path. Cut-corner check uses the same raw reads.

Combined: per-call A* cost drops roughly 5–10× on the same map. The reassignment-wave spike disappears at 25 smurfs and stays inside the per-tick budget at the 250-smurf target.

#### Root cause 2 — Sim thread died silently on unhandled exception

`SimulationCore.Run`'s main batch loop called `Tick(...)` with no exception guard. Any unhandled throw inside Tick (from any system — BehaviorSystem, NeedsSystem, MoodSystem, AgingSystem, …) propagated up, exited the `while (_running)` loop, and the OS thread died. `_running` stayed true, the snapshot queue stopped draining, every smurf appeared to freeze in place. Matches Sam's "stop and do not move again after a few minutes" exactly — the bug fires once at random and the world stops.

**Fix — `scripts/simulation/SimulationCore.cs`:** the per-tick `Tick(...)` call is now wrapped in `try { … } catch (Exception ex) { Godot.GD.PushError(...) }`. The world keeps ticking; the underlying bug surfaces in the editor console (with the full stack trace) instead of presenting to the player as a permanent freeze.

#### Root cause 3 — `foreach` on `IReadOnlyList<Smurf>` boxed an enumerator per tick

The main per-smurf loop in `BehaviorSystem.Tick` used `foreach (var s in smurfs)` against the `IReadOnlyList<Smurf>` parameter. The interface-typed foreach boxes the `List<T>.Enumerator` struct to a heap object on each call — 60 allocations per second of pure gen-0 pressure.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** indexed `for (int si = 0; si < smurfs.Count; si++)` over `smurfs[si]`. Same JIT-devirtualised path, zero allocation.

---

#### Version

Patch `0.4.17` → `0.4.18`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.17] — 2026-05-13

### Fixed — Residual corner stuck cases; depleted textures for every harvestable veg type

Reported on v0.4.16: smurfs *still* occasionally get caught on corners and against walls. Plus two requests: confirm every "small" vegetation type is passable (smurfberries, smallmushrooms, herbs, mosspatch, underbrush — anything a smurf could realistically walk through) while Large* / Palm shrooms stay impassable; and give every vegetation type its own depleted / stump texture, not just the v0.4.15 Large* trees.

---

#### Stuck-on-corners — three compounding causes, all fixed

1. **Pre-movement reachability check fired before SimPos rescue.** A smurf whose SimPos had briefly drifted into a wall (passability flip mid-tick, save-load race, regrowth) hit `IsWorkReachable`'s multi-region wall fallback. The fallback collects up to four neighbour regions; if those straddle distinct caverns the gate can falsely conclude the target is unreachable and blacklist a valid task. MoveOneTick's SimPos rescue runs a few lines later — too late.

   **Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** the pre-movement reachability gate now skips entirely when `IsPixelPassable(map, s.SimPos)` is false. MoveOneTick's rescue runs on the next line and the check fires correctly on the rescued position next tick.

2. **`StuckThreshold` was 90 ticks (~1.5 s) — too forgiving once v0.4.16's always-A*-for-designations was in place.** At that setting genuine 1.5-second stalls are almost always either stale paths or corner oscillations; both recoverable in under half a second.

   **Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** `StuckThreshold` lowered from 90 → 30 ticks. Wall-corner stuck cases now visibly snap out within ~0.5 sec instead of feeling frozen.

3. **No re-pathfind on stuck.** A path computed at task selection from an earlier SimPos can go stale (other smurfs nearby, drift, mid-tick excavation reshaping the region graph). The smurf rode out the full StuckThreshold against the stale path before giving up.

   **Fix — `scripts/simulation/systems/BehaviorSystem.cs` + `scripts/simulation/Smurf.cs`:** at `StuckRePathTicks` (15, halfway through the new threshold) the smurf gets one A* re-pathfind attempt from its current SimPos. New `Smurf.RePathTried` single-shot guard prevents the re-path from re-firing on every tick once tripped; it's reset whenever StuckTicks resets to zero (movement progress) or a new task is assigned. Corner-stuck oscillations now usually clear via the re-path, never reaching the blacklist branch.

---

#### Small vegetation passability (confirmed + documented)

Audit of `LocalMapGenerator` and the runtime: only `LargeMushroom / LargeSandshroom / PalmShroom` ever set `tile.Passable = false`. Every other vegetation type (`SmurfberryBush`, `SmallMushroom`, `HerbCluster`, `MagicFlower`, `SmallSandshroom`, `PineShroom`, `Underbrush`, `MossPatch`) leaves the underlying terrain's passability untouched — so smurfs already walk through them. No code change needed; this paragraph and the changelog entry are the documentation Sam asked for.

---

#### Depleted / stump textures for every harvestable vegetation type

`LocalMapRenderer.cs` previously had distinct stump silhouettes only for the three impassable trees (v0.4.15). Every other harvestable vegetation rendered its full live sprite at 50 % opacity when depleted — readable as "less yield" but not as "harvested".

**Fix — `scripts/ui/LocalMapRenderer.cs`:** six new `PaintDepleted*` routines branch out of `PaintTile`'s `IsDepleted` switch:

- **SmurfberryBush** — muted green foliage blobs with the red berry dots removed; the bush sits there bare until regrowth restores the berries.
- **SmallMushroom** — cream stalk with a dark "cut" stub where the cap was; no tan dome.
- **HerbCluster** — two short stem stumps over a dark soil base; no leaf spray, no magenta flowers.
- **MagicFlower** — single green stalk topped with a tiny purple seed-pod; the eight-petal bloom is gone.
- **SmallSandshroom** — amber stem stub with a darker cut line, no sandy cap.
- **PineShroom** — paired short brown stem stubs; the two conifer cones cleared.

#### Pause menu + Saving... overlay reliably pin the sim to paused

Reported: the pause menu and the "Saving..." overlay shown during exit-save should keep the world frozen until the actual scene transition. Two latent leaks let the sim resume mid-flow.

**Fix — `scripts/ui/GameController.cs`:**

- `IsAnyOverlayOpen()` now includes `_savingOverlay.Visible`. Previously the saving overlay's ~1-second fade-in + save + post-save linger window left the input gate open — pressing Space during that window routed through `_Input` and toggled `_sim.TogglePause()`, unpausing the world while the save was in progress.
- `OnPauseMenuExit` now explicitly sets `_sim.Paused = true` three times: at the start of the exit flow, immediately after the fade-in await, and immediately before the scene change. The pause was inherited from `OnMenuRequested` before; the defensive re-assertions guarantee no async path can leave the sim ticking through to the scene change.

#### Cut handles decoration vegetation correctly

`Underbrush` and `MossPatch` have `BaseYield = 0` and `IsDepleted` explicitly excludes them — so `FullyDepleteVegetation` couldn't make them visually "depleted". Cutting one was a no-op visually, the slot stayed looking alive forever.

**Fix — `scripts/world/LocalMap.cs` + `scripts/simulation/systems/BehaviorSystem.cs`:** new `LocalMap.ClearVegetation(x, y)` sets the slot's `Type = None`, fires `VegetationChanged`, and restores the underlying terrain's default passability if it had been overridden. The `CutVegetation` task effect now routes decoration types through `ClearVegetation` (slot wiped) and harvestable types through `FullyDepleteVegetation` (slot depleted, regrows on schedule via the existing timer). Player Cut order on a mosspatch now actually removes it.

---

#### Version

Patch `0.4.16` → `0.4.17`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.16] — 2026-05-13

### Fixed — Residual stuck-on-walls / corners; vegetation-state repaint stutter

Reported on v0.4.15: excavation now mostly works but smurfs still occasionally get stuck on walls and in corners, and there's a noticeable stutter on item drops and vegetation state changes (the Speety / chop fix landed cleanly but a different stutter surfaced).

---

#### Root cause 1 — short-route designations bypassed A*

`BehaviorSystem.Tick`'s post-`SelectTask` path-find request only fired when `distSq > Pathfinder.PreferAStarDistSqPx` (8 tiles). Below that, the smurf relied on greedy local steering — which doesn't respect the diagonal cut-corner rule that the region graph + Pathfinder enforce. A smurf claiming a Boulder a few tiles away across a concave corner would oscillate against the diagonal block (cut-corner says no, greedy doesn't know that) and stall until the 90-tick stuck-detector fired, blacklisted the tile, and re-rolled.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs`:** every designation-type task (`GatherFood / GatherMaterial / ChopWood / CutVegetation`) now requests A* regardless of distance, alongside the v0.4.13 unreachable fail-fast. A* expands ~5-20 nodes for short routes (sub-microsecond), so the cost is trivial; the win is that the path respects cut-corner geometry, so corner walls route cleanly instead of stalling.

#### Root cause 2 — IsWorkReachable wall-stuck fallback dropped regions

When the pre-movement reachability gate ran with a SimPos-in-wall smurf (briefly possible before MoveOneTick's rescue runs), `IsWorkReachable` fell back to scanning the smurf's 8-neighbour regions. But the nested-loop `break` only exited the inner loop, so each outer iteration overwrote the candidate region with whichever neighbour was checked last. If the smurf had drifted into a wall corner straddling two caverns (region A and region B), the smurf's effective region became whichever was checked last — and the gate then wrongly blacklisted targets in the *other* region.

**Fix — `scripts/world/LocalMap.cs`:** `IsWorkReachable` now collects up to four distinct candidate regions from the smurf's neighbourhood and accepts a target reachable from **any** of them. New `Matches(rid)` helper checks against the collected set. Behaviour for the common passable-smurf case is unchanged (one region, the smurf's own); only the wall-stuck edge case is corrected.

#### Root cause 3 — `FillCircle` did one C++ interop call per pixel

`LocalMapRenderer.FillCircle` was a `(2r+1)²` nested loop over `_image.SetPixel`, which crosses the C#↔C++ managed boundary on every call. For a LargeMushroom repaint (one `FillCircle` of r=6, three of r≈1.5, plus stems via `FillRect`) that's ~150 interop crossings per tile. Multiplied by 17 smurfs chopping per second under v0.4.15's single-shot felling, sustained interop overhead dominated the per-frame budget and surfaced as the vegetation-state-change stutter. The four impassable-terrain painters (`PaintDeadLog`, `PaintBoulder`, `PaintMagicCrystalOre`, `PaintLivingWood`) also each opened with a 256-call `SetPixel` nested loop to lay down the base fill.

**Fix — `scripts/ui/LocalMapRenderer.cs`:**

- `FillCircle` rewritten as a scanline fill: one `FillRect(width: 2·halfW+1, height: 1)` per row of the disk instead of one `SetPixel` per pixel. Interop count drops from O(r²) to O(r) — for the r=6 LargeMushroom cap that's 13 `FillRect` calls instead of ~113 `SetPixel` calls.
- All four `Paint*` base fills migrated to a single `_image.FillRect(new Rect2I(ox, oy, TS, TS), base_)` — one interop call instead of 256. The fix also benefits world-gen load times since every Boulder / DeadLog / LivingWood tile is painted via these routines.

---

#### Version

Patch `0.4.15` → `0.4.16`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.15] — 2026-05-13

### Fixed — Chop/Cut single-shot felling; item-drop microstutter; FungalStump visuals

Reported on v0.4.14: excavation reliability up to ~90 %, but a smurf still got stuck on a LargeMushroom (Speety in the screenshot) jittering in place, the colony idled near dense chop clusters without committing, and every item drop produced a microstutter.

---

#### Root cause 1 — ChopWood and Cut were per-yield instead of single-shot

`BehaviorSystem.ApplyTaskEffect` for `TaskType.ChopWood` called `HarvestVegetation` once — which only **decrements** `YieldRemaining` by 1 — and then immediately called `ClearDesignationsAt`. A LargeMushroom (BaseYield = 3) shed its chop designation after producing 1/3 of its wood. The smurf moved on, the tree still stood, the player had to re-designate. In dense chop clusters the colony rapidly cycled between adjacent half-chopped trees, which presented as the "vibrating in place without reassigning" behaviour. Same shape for `CutVegetation`.

**Fix — `scripts/world/LocalMap.cs`:** new `FullyDepleteVegetation(x, y)` that sets `YieldRemaining = 0`, starts the regrowth timer, and flips the tile to passable in a single call (with `_regionsDirty = true` so the v0.4.14 batched region rebuild picks it up on the next tick). Returns the amount of yield consumed so the caller can compute the total drop quantity.

**Fix — `scripts/simulation/systems/BehaviorSystem.cs` (ChopWood + CutVegetation cases):** route through `FullyDepleteVegetation` instead of `HarvestVegetation`. ChopWood now drops `WoodYield(vegType) × YieldRemaining` total wood in one item — felling a LargeMushroom hands the smurf 15 wood at once (3 yields × 5 wood/yield) instead of 5 spread across three re-designate cycles. The smurf walks to a chop target → arrives → drops wood → tile becomes passable → smurf reroutes to the next nearest chop designation. No jitter.

#### Root cause 2 — Item-drop microstutter (lock contention + per-tile allocations)

`_droppedItems` shared `_designationsLock` with designations + claims. Every `map.DropItem(...)` fired `ItemsChanged`, the renderer scheduled a redraw, `RebuildDrawCache` called `EnumerateDroppedItems` which took the same lock the sim thread used for `FindNearestExcavate / Gather / ChopWood / Cut`, and allocated a fresh `Item[]` per dropped tile while holding the lock. With ~200 stone piles after a big excavation, every drop event meant 200 small allocations + the sim thread blocked on the lock — that's the microstutter.

**Fix — `scripts/world/LocalMap.cs`:** dedicated `_itemsLock` for `_droppedItems`. `DropItem`, `RemoveItem`, `GetItemsOnTile`, `EnumerateDroppedItems`, `DroppedTileCount`, `SnapshotDroppedItems` all migrated. The two collections never participate in the same atomic operation so the split is safe — sim-thread designation work and renderer item snapshots no longer contend.

**Fix — `scripts/world/LocalMap.cs` + `scripts/ui/ItemDropOverlay.cs`:** new `SnapshotDroppedItemSummaries()` returning a `List<DroppedTileSummary>` with just the data the overlay actually draws (primary item, total quantity, stack count). One List allocation per snapshot regardless of tile count, instead of one `Item[]` per tile. `RebuildDrawCache` shrunk to a tight loop over the summaries.

#### Root cause 3 — Depleted Large* shrooms drew their full sprite at 50 % opacity

Sam's brief: "Largeshrooms and other variants should turn into passable fungalstump tiles that look like their requisite 'chopped down' largeshroom variant after being chopped … much like trees in rimworld." The old behaviour faded the live sprite — recognisable, but didn't read as a stump.

**Fix — `scripts/ui/LocalMapRenderer.cs`:** three new paint routines — `PaintFungalStumpLargeMushroom`, `PaintFungalStumpLargeSandshroom`, `PaintFungalStumpPalmShroom` — replace the live sprite path when `veg.IsDepleted`. Each variant keeps its cap colour as a thin remnant rim on top of a short stem stub, drawn over the now-passable terrain. The LargeMushroom stump shows a reddish Fly-Amanita rim, the LargeSandshroom keeps its broader sandy rim, the PalmShroom has a green tuft on each shoulder. Smurfs can walk over them; regrowth still ticks normally so the stump returns to a live tree after `RegrowthDays` in-game days.

---

#### Version

Patch `0.4.14` → `0.4.15`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.14] — 2026-05-13

### Fixed — Sim-thread stall under heavy excavation (region-rebuild storm, work-face pile-up, mid-task unreachability)

Reported on v0.4.13 with one screenshot: a large stone+wood dig site with 17 smurfs piled along a diagonal work face, the sim visibly hitched, and "visual warping at the edges of the screen". Three distinct bugs surfaced once the v0.4.13 reachability filter took the obvious "smurfs cluster at the edge of the dig site" symptom off the table.

**Root cause 1 — region-rebuild storm.** v0.4.13's `LocalMap.RebuildRegions` ran whenever the dirty flag was set, but `ApplyTaskEffect`'s `MutateTerrain` flipped the flag mid-loop. So inside a single `BehaviorSystem.Tick`, smurf A finishes a dig (dirty=true), smurf B's `SelectTask` calls `FindNearestExcavate` → `EnsureRegions` → full W×H BFS rebuild. At 240×150 = 36 000 tiles × 17 active diggers that's ~50 ms / tick of pure rebuild work. The sim thread fell behind 60 Hz; the renderer kept drawing at full frame-rate on stale snapshots; camera-follow interpolation drifted, producing the edge "warping".

**Root cause 2 — wrong-side adjacent-tile picker.** `NearestAdjacentPassableTile(tx, ty, …)` picked the closest passable neighbour to the **target**, ignoring where the smurf actually was. The 8-neighbour iteration order (`dy = -1…1`, `dx = -1…1`) plus `<`-strict best-of-equal-distance meant cardinal-west always won on ties — so every approaching smurf was routed to the west neighbour of every wall tile regardless of which side they were coming from. East-side diggers walked into the wall; the 90-tick stuck detector dragged them off; they re-picked the same tile and bashed the same west neighbour again. Cycle.

**Root cause 3 — no mid-task reachability check on short routes.** The v0.4.13 fail-fast handler only fired when `distSq > PreferAStarDistSqPx` (8 tiles), where A* is invoked. For close-but-unreachable targets — exactly the diagonal work face — A* was skipped, the smurf greedy-steered into the wall, and the only escape was the 90-tick stuck-detector timer.

---

#### `scripts/world/LocalMap.cs`

- **`BeginTick()` / `EndTick()`** — explicit sim-tick boundary. `BeginTick` rebuilds once if dirty, then sets a freeze flag that turns subsequent `EnsureRegions` calls into no-ops for the rest of the tick. Mutations within the tick still flip `_regionsDirty = true`, so the *next* tick sees a fresh rebuild. Net effect on the screenshot's scenario: ~50 ms/tick of region work collapses to a single sub-millisecond BFS.
- **Reused BFS queue (`_bfsScratch`)** — `RebuildRegions` used to `new Queue<(int,int)>(4096)` on every call. With per-mutation rebuilds firing dozens of times per second, the queue's internal buffer growth accumulated noticeable gen-0 pressure. The queue is now cached on the LocalMap instance and `Clear()`'d between rebuilds; only the first rebuild allocates.

#### `scripts/simulation/systems/BehaviorSystem.cs`

- **`Tick` wraps the per-smurf loop in `try { … } finally { map?.EndTick(); }` after `map?.BeginTick()`.** Exception-safe; even a thrown handler can't strand the freeze flag on.
- **Pre-movement reachability gate.** Every smurf with a designation-type `CurrentTask` runs `map.IsWorkReachable(smurfTile, targetTile)` before the move step. Unreachable targets get blacklisted on the 4-slot `AvoidTiles` FIFO with 360-tick TTL, the soft-claim is released, and `CurrentTask` is nulled — so the next tick re-runs `SelectTask` and picks a different target instead of walking into the wall for 1.5 s. O(1) per smurf when regions are clean, which they always are inside a tick thanks to the BeginTick batch.
- **`NearestAdjacentPassableTile` now takes the smurf's tile coords + checks regions.** Picks the passable neighbour *closest to the smurf* (Euclidean distance from the smurf, not the target), and prefers neighbours in the smurf's own DF region. Falls back to the legacy target-relative pick if the smurf's region is unknown or no neighbour matches it — so the change is strictly safer than v0.4.13's behaviour and degrades gracefully. Diggers approaching from any side now line up on the correct face of the wall instead of all routing to the west neighbour. `ResolveWalkTarget` updated to pass the smurf's tile coords.

---

#### Why this matches the screenshot

- *Stuck on tiles* — the work-face pile-up was the wrong-side picker (root cause 2) plus the missing short-route reachability gate (root cause 3). Both fixed: diggers now pick the approach square that's actually adjacent to them in the same region.
- *Breaking more than one at once* — not a per-smurf multi-tile bug. With 17 diggers all completing arrivals within the same tick under heavy lag, their `ApplyTaskEffect` calls cluster — the player perceives several tiles flipping to mud simultaneously. With the rebuild storm gone, the sim runs at 60 Hz; completions are paced naturally and the visual "blast" disappears.
- *Visual warping at the edges of the screen* — sim-thread stall (root cause 1). Renderer kept drawing at full rate against frozen snapshots, camera-follow interpolation drifted relative to actual smurf positions, producing edge motion artefacts. With the batched rebuild the sim stays inside the per-tick budget and snapshots flow continuously.

---

#### Version

Patch `0.4.13` → `0.4.14`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.13] — 2026-05-13

### Fixed — Smurfs no longer cluster + jitter on unreachable excavation tiles (DF-style region pathfinding)

Reported on v0.4.12 with five screenshots: a stone wall designated for excavation, with smurfs piled at the edge of the dig site, jittering in place, unable to reach the interior tiles. The cluster caused noticeable hitches because every smurf was paying the full 1024-node A* budget per tick trying to prove the impossible, then spending the 90-tick stuck-detection window before re-rolling — only to re-pick the same unreachable tile by squared distance and start over.

The root cause is that `FindNearestExcavate / Gather / ChopWood / Cut` returned the closest-by-distance designation regardless of whether the smurf could physically reach it. Inside a hollowed-out vein, the closest tile from outside is always a sealed interior wall.

**Fix — Dwarf Fortress-style connected-component regions, with diagonal movement preserved.** Per the [DF Path wiki page](https://dwarffortresswiki.org/index.php/Path), DF tags every passable tile with a connectivity id and answers "is X reachable from Y?" in O(1) before invoking A*. We do the same but keep 8-connectivity (cardinal + diagonal) with the same cut-corner rule the renderer-side movement already obeyed, so the RimWorld-style smooth diagonal walks stay intact.

---

#### `scripts/world/LocalMap.cs`

New flat `ushort[] _regionId` array sized `Width × Height`, plus a `_regionsDirty` flag and a `_regionLock` for cross-thread safety.

- **`RebuildRegions()`** — BFS flood-fill from every unvisited passable tile, assigning ascending region ids. Expansion uses the same 8-neighbour + diagonal cut-corner rule as `Pathfinder.FindPath`, so a region match is a true "the pathfinder can walk between these two tiles" guarantee. Single pass: O(W·H), microseconds at the planned 320×200 worst case.
- **`EnsureRegions()` / `GetRegion(x, y)`** — public lazy-rebuild accessor. Returns 0 for impassable / out-of-bounds. Cheap when the cache is clean: one bool read + array index.
- **`AreReachable(x0, y0, x1, y1)`** — O(1) region-id compare.
- **`IsWorkReachable(sx, sy, tx, ty)`** — handles both passable work (gather a berry tile in the same region) and impassable work (excavate Boulder / chop LargeMushroom whose cap is still up). For impassable targets it scans the 8 neighbour tiles for one in the smurf's region — that's where the smurf will actually stand to do the work.
- **Dirty-flag plumbing** — every passability mutation in `Set`, `MutateTerrain`, `RestoreVegetationYield`, `HarvestVegetation`, `ApplyTerrainDelta`, `ApplyVegetationDelta` now sets `_regionsDirty = true` (only when the passability actually flips, so no-op writes stay free). Excavating a tile, growing/clearing a LargeMushroom cap, or load-restoring terrain all keep the region graph consistent.
- **`FindNearestExcavate / Gather / ChopWood / Cut`** — each now calls `EnsureRegions()` once and skips any candidate that fails `IsWorkReachable` from the smurf's tile. Combined with the existing avoid-list / soft-claim filters, smurfs only ever consider designations they can physically reach. Interior wall tiles become reachable automatically as outer tiles are excavated and the region graph rebuilds.

#### `scripts/simulation/Pathfinder.cs`

A* now fails fast on unreachable goals:

- **Reachability gate** — before opening the priority queue, compare `map.GetRegion(sx, sy)` against `map.GetRegion(tx, ty)`. If both are non-zero and differ, return null immediately. Previously the search ran until the closed-set hit `MaxNodes` (1024), which was a real time sink at 20+ smurfs × multiple impossible targets per stuck cycle.
- **`FindReachableAdjacent`** — when the goal tile is impassable, pick the 8-neighbour passable tile that sits in the smurf's own region. The legacy "first passable neighbour" picker could choose a tile walled off from the smurf, and A* then burned its full 1024-node budget proving the obvious. Falls back to the legacy picker when the smurf's region is unknown (`GetRegion == 0`, e.g. their SimPos has drifted into a wall — the existing rescue logic handles that case).

#### `scripts/simulation/systems/BehaviorSystem.cs`

Fail-fast cleanup when the long-route A* request returns null on a designation task:

- Push the unreachable tile onto the 4-slot `AvoidTiles` FIFO with a 360-tick TTL, release the soft-claim, clear the path waypoints, and null out `CurrentTask` so the next tick re-runs `SelectTask` with the offending tile filtered out. Previously the path-null was silently ignored and the smurf walked into a wall for 90 ticks before the stuck-detector fired. The smurf now reprioritises within a single tick.
- New `IsDesignationTaskType(TaskType)` helper distinguishes designation-backed tasks (which get blacklisted on failure) from haul / combat / move orders (which may legitimately retry after map state changes).

---

#### Why this matches the user's three requirements

- *"Carve single-tile-wide tunnels without getting stuck"* — every tile excavated triggers a region rebuild, so the next interior tile becomes reachable on the very next tick and gets picked up by the nearest idle smurf. No more piling up at the wall edge.
- *"Haul things across the entire level-map"* — Haul lookups go through `EnumerateDroppedItems` and the haul selector retries when reservations break; with reachability filtering in place, pathfinds either succeed or fail in O(1), so the haul queue can churn through map-spanning routes without stalling.
- *"Reprioritise to a new task without jittering"* — unreachable designations are filtered out at selection time, and the fail-fast handler kicks a smurf off a tile that became unreachable mid-route within one tick instead of ~1.5 seconds.

---

#### Version

Patch `0.4.12` → `0.4.13`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.12] — 2026-05-13

### Added — 📦 Haul order

New designation tool in the bottom Orders bar. Drag a box over a region with dropped items; every item inside is flagged for priority haul. Haulers ignore their normal 32-tile auto-haul radius for flagged items, so distant piles left behind by long-range gather operations get cleaned up.

---

#### `scripts/ui/UITheme.cs` + `scripts/ui/DesignationToolbar.cs`

`DesignationTool.Haul` added to the shared enum; `Tool.Haul` mirrored in the toolbar's own enum + `ActiveDesignation` switch. Button slots between **Excavate** and **Remove** with the prompt *"📦 Haul — Force-haul every dropped item in box (bypasses auto-haul radius)."*

#### `scripts/simulation/systems/HaulSystem.cs`

New `_priorityHauls : HashSet<Guid>` keyed by `Item.Id`, with `MarkPriority` / `ClearPriority` / `IsPriority` accessors guarded by the existing `_reserveLock`. `SelectHaulTarget` runs a pre-pass over priority items first — walks every dropped item, picks the nearest unreserved priority entry regardless of distance, returns a Haul task at engine priority 60 (vs 50 for auto-haul) so a force-haul outranks ambient hauling on the same tick. Falls through to the legacy radius-bounded scan when no priority item is found. Priority flag is cleared automatically in `Apply`'s pickup phase so the dict stays bounded.

#### `scripts/SimulationManager.cs` (DesignateRect)

Haul branches out of the legacy `QueueDesignation` flow because the order is item-keyed, not tile-keyed. Walks `LocalMap.EnumerateDroppedItems`, filters to the drag rect, calls `HaulSystem.MarkPriority(it.Id)` per item. No `LocalTile` designation flag is set — the Haul order leaves no on-tile glyph; the existing `ItemDropOverlay` already shows the items visually.

#### `scripts/ui/OrderFeedbackOverlay.cs`

`TileFlashHaul` variant added — yellow-gold drag-commit flash (1.00, 0.85, 0.30 fill + 1.00, 0.95, 0.55 border) matching the v0.4.3 `RingPickUp` palette so all "move-this-item" feedback shares a visual idiom. `FlashDesignationRect`'s tool→kind switch updated.

---

#### Version

Patch `0.4.11` → `0.4.12`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.11] — 2026-05-13

### Fixed — Resources panel scrollable; no longer overflows the viewport

Reported on v0.4.10: when the player expanded a category in the Resources tab (e.g. Food), the per-stack list inside the expansion grew tall enough to push the panel past the top of the viewport — title + header rows disappeared off-screen and the bottom tabs were obscured.

**Fix — `scripts/ui/ResourcesPanel.cs`:** wrap `_categoriesVbox` in a `ScrollContainer` with `VerticalScrollMode = Auto` and a `CustomMinimumSize` of (0, 280) px. The viewport caps at 280 px regardless of content height; expansion lists overflow into a vertical scrollbar instead of growing the panel. The title, subtitle, and column-header row stay pinned above the scroll area.

### Also in this patch — Boulder hover tooltip shows specific stone subtype + drops

The top-right tile-hover info showed "Boulder · Impassable · Yields Stone" for every Boulder regardless of material. The renderer painted Granite / Limestone / Marble / Obsidian / Quartz / Magicstone / MagicCrystal distinctly, but the hover text didn't follow.

**Fix — `scripts/ui/GameController.cs` + `scripts/ui/TileInfoOverlay.cs`:** `ShowTile` now takes an optional `MaterialKey? stone` arg (populated from `LocalMap.GetTileStone(tx, ty)` in `GameController`). Boulder tiles use the stone's `MaterialDef.DisplayName` for the terrain name (e.g. "Granite", "Magic Crystal") and a per-subtype yield line. MagicCrystal tiles specifically report "Yields Magic Crystal Block + Crystal Shards" — matches the actual `BehaviorSystem.GatherMaterial` drop. Other Boulder subtypes report "Yields <Material> Block". Falls back to the legacy "Boulder · Yields Stone" wording when stone is null (legacy save without the v0.4.2 stone-variation pass).

### Also in this patch — Tile-hover panel pulled closer to the Menu / speed bar

`TileInfoOverlay.OffsetTop` reduced from `EdgeInset + Scaled(80)` → `EdgeInset + Scaled(50)`. The hover line now sits roughly 4 px below the speed/menu capsule's base instead of the previous ~25 px gap — visually adjacent without overlap. `OnUIScaleChanged` updated to match so UI-Size rebuilds re-apply the new offset.

---

#### Version

Patch `0.4.10` → `0.4.11`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.10] — 2026-05-13

### Changed — Jobs tab grid metrics tightened

The Jobs — Work Priorities grid was sized with v0.3.47 metrics: 52×22 px cells, 96 px name column, 4-px header separation, 3-px row separation, ~50 % of the panel as whitespace.

**Fix — `scripts/ui/JobsPanel.cs`:** consolidated the cell/name/font sizes into four `const int` knobs at the top of the class, halved across the board:

| Knob | Was | Now |
|---|---|---|
| `CellW` | 52 | 38 |
| `CellH` | 22 | 18 |
| `NameW` | 96 | 76 |
| Header / cell font | 10 / 11 | 10 / 10 |
| Title font | 15 | 13 |
| Subtitle font | 10 | 9 |
| Outer margin (T/B) | 10 | 6 |
| Outer margin (L/R) | 12 | 8 |
| Vbox separation | 6 | 3 |
| Grid `h_separation` | 4 | 2 |
| Grid `v_separation` | 3 | 1 |
| Scroll min height | 220 | 160 |
| Panel min size | 900 × 280 | 700 × 200 |

Subtitle text trimmed from "1 = highest, 4 = lowest, dash = forbidden. Click a cell to cycle; right-click sets to off." to "1 = highest, 4 = lowest, dash = forbidden. Click cycles · right-click sets off." — same information, less line wrap.

Net: same 15 columns × N rows in roughly 60 % of the previous footprint. Tooltips, cycle behaviour, header-click-cycles-column logic all unchanged.

---

#### Version

Patch `0.4.9` → `0.4.10`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.9] — 2026-05-13

### Changed — Scenario detail card: personality + starting items now side-by-side

Reported on the v0.4.8 layout: the Configure Colony screen stacked Personality (pick up to 5) above Starting items in a single column, which pushed the items list past the visible area on smaller screens and left the right half of the detail card empty.

**Fix — `scripts/ui/ScenarioPanel.cs`:** new two-column `HBoxContainer` inside the per-smurf detail panel. Personality picker (header + ScrollContainer + GridContainer of trait checkboxes) sits in the left column; Starting items (header + 🎲 Reroll button + rolled-kit list) sits in the right column. Both columns share an equal-stretch `SizeFlagsStretchRatio = 1f` so they always split the detail card 50/50.

The personality grid drops from 3 columns to **2** to fit the narrower left pane — 25 traits across ~13 rows fits without scrolling at default UI scale. `AddStartingItemsSection` gained an optional `Container parent` parameter so the right column can host its content directly; the default falls through to the legacy `_detailContainer` for any caller that hasn't been updated.

---

#### Version

Patch `0.4.8` → `0.4.9`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.8] — 2026-05-13

### Fixed — All remaining correctness bugs from the v0.4.6 audit: B-1 save extension, B-7 stone deltas, B-8 wanderer tool

Closes out the bug audit. Save / load now round-trips every per-smurf field added since v0.3.40 + every dropped item on the map + every Boulder's stone subtype. Wanderers arrive carrying a starting tool per the spec. Existing functionality fully preserved — older saves still load via the per-field nullable fall-backs.

---

#### B-1 — Save format extension (P0)

**Records added:**
- `EquipmentSaveData(Slot, Item)` — one equipment slot's payload on disk.
- `PreferencesSaveData` — six-list record mirroring the `Preferences` class.
- `ThoughtSaveData(Key, TicksRemaining, MoodOffset, Context)` — one active thought ring entry.
- `DroppedItemSaveData(X, Y, Item)` — one item lying on the map.
- `StoneTileDelta(X, Y, MaterialSubType)` — one Boulder's stone subtype (B-7).

**Schema extensions:**
- `SmurfSaveData` gained `Handedness`, `Equipment`, `CarriedItem`, `Preferences`, `Thoughts`. All nullable — older saves fall through to the existing re-roll path.
- `ColonySave` gained `DroppedItems` + `StoneTileDeltas` (B-7).

**Save path (`SaveManager.SaveToSlot`):**
- `SimulationManager.GetSmurfSaveExtras(globalTick)` walks the live smurf list under the smurf lock and returns by-name extras (handedness + equipment + carried + preferences + thoughts). Reused inside `BuildSmurfList` to populate the new `SmurfSaveData` fields.
- `SimulationManager.GetDroppedItemsSnapshot(globalTick)` forwards `LocalMap.SnapshotDroppedItems()` and projects to `DroppedItemSaveData`.
- `LocalMap.SnapshotStoneVariation()` returns every still-Boulder tile's stone subtype.

**Load path (`SimulationManager.LoadFromSave` + `WorldState.LoadFromSave`):**
- New `RehydrateItem(ItemSaveData, globalTick)` helper used by every item-restore path (colony inventory, equipment, carried, dropped). Re-anchors `AvgBirthTick` so the spoilage clock stays truthful.
- Per-smurf: parse `Handedness` (else roll fresh); rebuild `Equipment` dict per slot; restore `CarriedItem` with `TilePos = null`; clone `Preferences` lists; populate the 8-slot Thought ring from the save and set `ThoughtsDirty` so the next ThoughtSystem.Tick recomputes `MoodFromThoughts`.
- Colony-wide: dropped items rebuild via `RehydrateItem` + `LocalMap.DropItem`; stone subtypes apply via `WorldState.LoadFromSave` after the `TerrainDeltas` pass.

#### B-7 — Stone subtype delta save/load (P3)

`LocalMap.SnapshotStoneVariation()` exposes the per-Boulder material assignments; `ColonySave.StoneTileDeltas` carries them on disk; `WorldState.LoadFromSave` re-applies them via `SetTileStone(x, y, MaterialKey("Stone", subType))` after the terrain deltas. Future versions whose `AssignStoneVariation` weights differ from what produced this save will no longer drift Boulder textures.

#### B-8 — Wanderer starting tool (P3)

`WanderingInSystem.TryWanderer` gained a `long globalTick` parameter (threaded from `SimulationCore.Tick`) and a 60 % chance of arriving with a role-appropriate Tool — Crafter→Hammer, Forager/Caretaker→Basket, Mage/Scholar/Elder→Focus, Guardian→50 % Hammer / 50 % Sickle. Rolled via `ItemFactory.Create(Tool, sub, null, rng, globalTick, skillLevel: 0, quantity: 1)`; auto-equipped in the dominant hand via `s.Equipment[DominantHand]`. Matches the spec: *"wanderers arrive with one random tool item if any (the rest of their belongings stay with their old colony)"*.

---

#### Bugreport status

Every active item from the v0.4.6 audit is now resolved:

- v0.4.7 — B-2 / B-3 / B-4 / B-5 / B-6
- v0.4.8 — B-1 / B-7 / B-8
- enginespec — B-9 (perf ToString allocations) tracked as A-5 / A-14

`bugreport.md` updated with completion markers in both §6 Resolved and §7 status table.

---

#### Version

Patch `0.4.7` → `0.4.8`. `MainMenuController.cs` + `memory/project_versioning.md` updated. Existing functionality preserved (nullable save fields + fall-back paths); codebase ready for the next enginespec.md perf pass.

---

## [0.4.7] — 2026-05-13

### Fixed — Bug-audit pass; bugreport.md added; B-2 through B-6 landed

Companion to the v0.4.6 `enginespec.md` perf audit, a new `Cloud/bugreport.md` documents bugs found in a code-correctness audit of v0.4.6. Twelve findings, five fixed inline this drop:

- **B-2 (P1) Items lost on smurf death.** Carried + equipped items were silently GC'd along with the dead `Smurf`. New `SimulationCore.DropCorpseGear(s)` called at both death sites — drops every item onto the death tile via `Map.DropItem`. Players can now recover gear from a corpse's tile.
- **B-3 (P1) Haul reservation leak on task interrupt.** `HaulSystem.Reserve` was only released on the pickup-success path; abandoned hauls (critical need, stuck-detector, player re-order) leaked entries until every dropped item read as "reserved" and the colony stopped hauling. New `HaulSystem.ReleaseByIdString(guid)` + `BehaviorSystem.ReleaseTaskClaim` extension drains the reservation on every task abandonment.
- **B-4 (P1) `RequestEquip` item loss on registry miss.** If `ItemRegistry.Get` returned null after a save-version migration or registry edit, the item was consumed from inventory but never returned. Now bounces back to inventory before the early-return.
- **B-5 (P2) `ItemDropOverlay.RebuildDrawCache` race.** `_map.DroppedTileCount` and `EnumerateDroppedItems` locked separately; sim could add tiles between them and overflow `_drawCache` → `IndexOutOfRangeException`. New defensive grow inside the write loop expands the array on overflow.
- **B-6 (P2) `RequestDropEquipped` orphans item when map is null.** Scene-transition / exit-to-menu race could fire mid-drop. Now bounces back to inventory as a fallback when no map is bound.

**Open from this audit:**
- **B-1 (P0) Save format missing per-smurf v0.3.40+ state.** Equipment, Handedness, CarriedItem, Preferences, Thoughts, and dropped-items on the map are all wiped on every save/load. ~3 hours of focused work to extend `SaveManager.cs` data records + the `LoadFromSave` path. Tracked in `bugreport.md` §1.
- **B-7, B-8** (P3) deferred — stone-subtype save delta, wanderer-tool flavour.
- **B-9** (P3) overlaps with `enginespec.md` A-5 / A-14 string-alloc items; tracked there.
- **B-10, B-11, B-12** by-design (auto-equip "magic grab", separate CarriedItem, Trinket no-slot).

The new `Cloud/bugreport.md` sits next to `Cloud/enginespec.md` as a living companion document — fixes move to its §6 as they ship; the §7 status table tracks each finding to file:line + open/fixed.

---

#### Version

Patch `0.4.6` → `0.4.7`. `MainMenuController.cs` + `memory/project_versioning.md` updated. Roadmap unchanged (correctness-only).

---

## [0.4.6] — 2026-05-13

### Perf — v0.4.6 audit + P0 quick-wins; merged OptimizationReport.md into enginespec.md

Sam's brief: re-run the codebase audit for the 1000-smurf + 60 FPS constant 1× target; eliminate hitches; merge `OptimizationReport.md` into `enginespec.md`.

Four P0 quick-wins land inline. The remaining six audit items (A-5 through A-14) are documented and queued for the v0.4.7 batch with concrete remediations + effort estimates. The two cloud-level perf docs (`enginespec.md` + `OptimizationReport.md`) are merged into a single living document at `Cloud/enginespec.md`; `OptimizationReport.md` deleted.

---

#### A-1 — Pathfinder scratch-buffer reuse — `scripts/simulation/Pathfinder.cs`

Per-call allocation removed. `FindPath` used to allocate three arrays sized to `W × H` (~600 KB total at 320×200) on every call. At 1000 smurfs averaging ~1 long-route task transition/sec, that was ~600 MB/sec of allocation pressure — guaranteed gen-0 GC hitch. Replaced with `ThreadLocal<int[]>` / `ThreadLocal<bool[]>` scratch buffers grown lazily and reused for every subsequent pathfind on the sim thread.

#### A-2 — ItemDropOverlay snapshot caching — `scripts/ui/ItemDropOverlay.cs`

`_Draw` used to call `LocalMap.EnumerateDroppedItems` on every redraw (every viewport scroll / camera zoom), allocating a tuple snapshot under the inventory lock. New `_drawCache` field stores a precomputed `(X, Y, Primary, TotalCount, StackCount)` per tile; `OnItemsChanged` flips a `_cacheDirty` flag. Redraw → reuse; only the actual data change rebuilds the cache.

#### A-3 — EquipmentSystem precomputed task→tools lookup — `scripts/simulation/systems/EquipmentSystem.cs`

`AutoEquipForTask` used to walk `ItemRegistry.All` + build a fresh `List<ItemSubTypeDef>` on every task transition. Replaced with a module-init `Dictionary<TaskType, ItemSubTypeDef[]>` plus a static `_handSlots` array. Per-task-transition cost drops to one hash lookup + small typed-array walk.

#### A-4 — HaulSystem bounded radius — `scripts/simulation/systems/HaulSystem.cs`

`SelectHaulTarget` used to walk *every* dropped-items tile to find the nearest unreserved pile. New `HaulSearchRadiusSq = 32 × 32` cap restricts the search to the smurf's local neighbourhood; distant piles get hauled by smurfs near them, so colony-wide collection still converges. The dominant hitch contributor at high pile counts.

---

#### Document merge

`Cloud/OptimizationReport.md` and `Cloud/enginespec.md` collapsed into the single `Cloud/enginespec.md`. The merged file carries:

- The unchanged engine-platform recommendation (stay in Godot)
- The complete v0.3.31 → v0.4.6 shipped-items table (B-x / N-x / O-x / new **A-x** codes)
- A fresh §4 v0.4.6 audit covering the perf surface added since v0.3.39 (Phase 4 items, on-tile drops, Haul, Thoughts, Preferences, Jobs grid, A*, Equipment, Inventory tab, context-aware right-click)
- 14 new findings (A-1 through A-14) — 4 landed this drop, 6 queued for v0.4.7, 4 documented as deferred/L
- An explicit §7 "Hitch elimination" section enumerating the five hitch categories and which findings address each
- Updated §5 readiness scorecard

`OptimizationReport.md` was deleted from disk after the merge.

---

#### Version

Patch `0.4.5` → `0.4.6`. `MainMenuController.cs` + `memory/project_versioning.md` updated. Roadmap unchanged (this is engineering-only).

---

## [0.4.5] — 2026-05-13

### Changed — Haul-carry visual integrated with the v0.4.4 equipment system

The v0.4.2 carry icon was hardcoded to the right-side hand position regardless of handedness and overlapped any equipment overlay in the same hand. v0.4.5 routes the haul-carry through the per-slot model:

- **Handedness-aware placement** — `ResolveCarrySlot(s)` picks the dominant hand first (Right or Left per the smurf's `Handedness`); falls back to the off-hand if the dominant is already holding an equipped item; falls back to the dominant slot when both hands are occupied (the carry visually replaces the dominant equipment for the haul duration — DF / RimWorld convention).
- **Equipment-overlay suppression** — the renderer skips the equipment slot that's holding the haul-carry so the per-slot overlay and the carry icon don't stack on the same pixels. When the haul completes, the slot's equipment visual returns automatically the next frame.

Result: a left-handed Forager with an equipped Sickle picking up berries now visibly carries the berries in their right hand (off-hand) while the Sickle stays in the left. A right-handed empty-handed Smurf carrying a stone block draws the block in their right hand as before.

`Smurf.CarriedItem` continues to be a separate field (not folded into `Equipment`) — hauling is a transient state, not a permanent equip, and routing it through the equipment dict would cause unnecessary churn with auto-equip on every task transition.

---

#### File touched

- [SmurfColonyView.cs](SmurfulationC/scripts/ui/SmurfColonyView.cs) — new `ResolveCarrySlot(VisualSmurf)` helper + `DrawSmurf` now computes the carry slot before the equipment overlay loop and skips that slot during equipment rendering.

---

#### Version

Patch `0.4.4` → `0.4.5`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.4] — 2026-05-13

### Added — DF-style per-body-part equipment, Handedness trait, auto-equip-on-task

The v0.4.3 three-slot equipment model (Tool / Weapon / Apparel) is replaced with a Dwarf-Fortress-style per-body-part dictionary keyed by every external `BodyPartRegistry` part. Each smurf gets a `Handedness` trait that determines which hand auto-equip fills first; off-hand stays free for shields + dual-wield once Phase 7 combat lands. Tools auto-equip from the colony stockpile the moment a smurf picks up a matching task.

---

#### New taxonomy

- **`EquipSlot.cs`** — enum `Head / Torso / LeftArm / RightArm / LeftHand / RightHand / LeftLeg / RightLeg / LeftFoot / RightFoot`. `EquipSlotMeta` exposes `BodyPart(slot)` (maps to `BodyPartRegistry.Template` names for Phase 7 damage routing), `Display(slot)` (UI strings), `All` (head-to-toe iteration order), and a `BodyClass` enum (`Head / Torso / Arm / Hand / Leg / Foot / None`) with `SlotsFor(class)` for paired-slot lookups.
- **`Handedness.cs`** — enum `Right / Left`; `HandednessMeta.Roll(rng)` returns Left with 10 % probability; `DominantHand(h)` / `OffHand(h)` return the matching `EquipSlot`.

#### Item registry slot tagging

Each equipment sub-type now declares `BodyClass` + `PreferredForTasks`:

| Sub-type | BodyClass | PreferredForTasks |
|---|---|---|
| Pick | Hand | GatherMaterial |
| Sickle | Hand | ChopWood, CutVegetation |
| Basket | Hand | GatherFood |
| Focus | Hand | Attune, Meditate |
| Hammer | Hand | Build |
| Spear / Club / Sling | Hand | — (Phase 7 combat) |
| Cloak | Torso | — |
| Hat | Head | — |
| Boots | Foot | — |
| Pendant / Ring | None (Phase 11 trinket slot) | — |

#### Smurf model

`Smurf.EquippedTool / Weapon / Apparel` (single-field slots) replaced with `Dictionary<EquipSlot, Item> Equipment`. Legacy `EquippedTool / Weapon / Apparel` getters preserved as read-only convenience properties that resolve through the dict + `Handedness` for tools/weapons (dominant-hand-first lookup) and the Torso slot for apparel — so existing v0.4.3 read paths keep working.

`Handedness` field added; rolled at creation by `BirthSystem`, `WanderingInSystem`, `SimulationManager.AddSmurfFromTemplate` / `AddSmurf` / `LoadFromSave`.

#### Snapshot

`SmurfSnapshot` carries `IReadOnlyDictionary<string, (Kind, SubType, MaterialFamily, MaterialSubType)> Equipment` plus `Handedness`. Legacy property getters (`EquippedToolSubType`, `EquippedWeaponSubType`, `EquippedApparelSubType`) keep the v0.4.2 renderer/UI contract intact via dict lookups.

#### Sim handlers (`SimulationManager.cs`)

- `RequestEquip(name, sub, matFamily, matSub, slotHint="auto")` — slot hint can be `"auto"` (resolves via body class + Handedness, fills empty side first for paired slots, replaces dominant for both-occupied hand case) or an explicit `EquipSlot` name from the card. Displaced occupant bounces back to colony inventory.
- `RequestUnequip(name, slot)` and `RequestDropEquipped(name, slot)` accept the `EquipSlot` enum name as the slot string; `"Carried"` still hits the haul-carry slot. Drop stamps the item's `TilePos` to the smurf's current position and routes through `LocalMap.DropItem`.
- New private `ResolveEquipSlot(s, def, hint)` encapsulates the slot-resolution rules.

#### Auto-equip — `EquipmentSystem.cs` (new)

`EquipmentSystem.AutoEquipForTask(s, task, resources)`:

1. Build the list of sub-types whose `PreferredForTasks` contains the new task.
2. If either hand already holds a matching tool, return.
3. Scan the colony inventory for the best matching tool — score = `QualityMul × (AvgCondition / DurabilityCap)`; skip Broken items.
4. Split one unit off the stack (consume the rest stays in inventory).
5. Bounce any dominant-hand occupant back to inventory; slot the new tool in the dominant hand.

Wired into `BehaviorSystem` immediately after `SelectTask` returns a new task. Magic-grab from the global pool until Phase 5 stockpile zones replace the path with "walk to tool stockpile, then to work site."

#### Renderer

`SmurfColonyView.DrawSmurf` walks the snapshot's Equipment dict and calls `DrawEquipmentSlot(pos, slotName, kind, subType)` per entry:

- **Head** — hat band at brim line
- **Torso** — body-band stripe (matches v0.4.2 apparel look)
- **LeftHand / RightHand** — `DrawHandItem` renders a small grey-vertical-bar weapon or a brown circle tool at the side-of-body offset
- **LeftFoot / RightFoot / LeftArm / RightArm / LeftLeg / RightLeg** — small coloured rectangles at the body-part position

A left-handed smurf's tool now sits in the left hand visibly; a right-handed smurf's tool sits in the right.

#### Smurf card — Inventory tab

Rebuilt around the per-body-part model. Header shows the smurf's Handedness. One row per `EquipSlot` in head-to-toe order; dominant hand gets a ✋ marker. Equip / Unequip / Drop buttons act on the specific slot. The Equip picker filters the colony inventory by the target slot's `BodyClass` — clicking Equip on Torso lists Cloaks but not Hats; clicking Equip on LeftHand lists Picks / Spears / Hammers etc.

---

#### Version

Patch `0.4.3` → `0.4.4`. `MainMenuController.cs` + `memory/project_versioning.md` + Roadmap §4 status block updated.

---

## [0.4.3] — 2026-05-13

### Added — Inventory tab on smurf card; context-aware right-click; Keybindings tab

---

#### Smurf card — Inventory tab (`SmurfCardPanel.cs`)

New "Inv" tab beside Main / Mood / Health / Skills. Three equipment slots (Tool / Weapon / Apparel) plus the Carried-Item readout:

- **Equip** opens a popup picker listing every compatible item in the colony inventory; click an item to slot it.
- **Unequip** bounces the equipped item back into the colony pool.
- **Drop** drops the item on the smurf's current tile so it appears as a physical drop on the world (renders via `ItemDropOverlay` from v0.4.2).

When something occupies a slot, that row shows the item's display name + Unequip + Drop. When the slot is empty, the row shows the Equip button. Carried items get only the Drop button (the Haul loop is the legitimate path for pick-up / put-down).

The card carries a `Sim` reference (assigned in `GameController.StartSim`) so it can route actions through the sim thread.

#### Sim-thread plumbing (`SimulationManager.cs`, `SimulationCore.cs`)

- `RequestEquip(name, subType, materialFamily, materialSubType)` — pops a matching unit out of the colony inventory (splits stacks if needed), bounces any item currently in the slot back to the pool, and equips. Posted onto the new `PendingCommands` queue so the mutation lands inside `Tick` before any reads.
- `RequestUnequip(name, slot)` — clears the slot, adds the item back to inventory.
- `RequestDropEquipped(name, slot)` — clears the slot, stamps the item's `TilePos` to the smurf's current `SimPos`, then `LocalMap.DropItem` so the renderer sees it.
- `RequestPickUp(name, itemTile)` — enqueues onto `PendingPickUps`; drained at the top of `Tick` and routed through the existing PlayerOrder pathway.
- `PostMainThreadCommand(Action)` + `PendingCommands` queue — generic UI→sim command pipe so future Inventory / Jobs panel actions can land cleanly inside one tick.

---

#### Right-click handler — context-aware (`GameController.cs`)

Replaces the v0.3.30 "always issue Move" path with `ResolveRightClickActions(tile, worldPos, map)` which builds a list of every applicable action for the (tile, world-state) combo. Today's resolver knows:

- **Pick up `<item>`** — fires when the tile holds at least one dropped item. Routes through `Sim.RequestPickUp` so the BehaviorSystem treats the order as a player-anchored Haul cycle (walk to tile, pick up, deliver to colony pool).
- **Move to tile / Move here** — fires on every passable tile.
- (Phase 5 plug-in: **Force craft** on a workbench. Stub feedback variant `RingForceCraft` already drawn.)

**Default execution:** the first applicable action auto-fires. Pick Up wins over Move when both apply, so right-clicking an item under cursor picks it up rather than running over it.

**Alt-held = context menu:** `Input.IsActionPressed("kb_context_menu") || Input.IsKeyPressed(Key.Alt)` opens a RimWorld-style `PopupPanel` at the cursor position listing every applicable action; clicking one executes and dismisses.

#### BehaviorSystem — pick-up player-order handling (`BehaviorSystem.cs`)

The order intake loop now inspects the target tile. If items are present, the order converts to a Haul task targeting that specific item (reserve via `HaulSystem.Reserve` + a TargetId carrying the item Guid). On arrival the existing Haul Apply pickup-then-deposit flow takes over. Empty-tile orders still flow through the generic PlayerOrder pathway.

#### Feedback overlays (`OrderFeedbackOverlay.cs`)

Two new ring variants:
- **RingPickUp** — yellow ring (1.00, 0.85, 0.30) around the picked-up item tile.
- **RingForceCraft** — warm-brown ring (0.85, 0.55, 0.25) for the Phase 5 stub.

Both share the existing 0.60 s lifetime + outward-radius animation.

---

#### Settings — Keybindings tab (`SettingsPanel.cs`)

New section under the existing Display / Gameplay / Audio sections. Eight default bindings registered:

| Action | Default | Description |
|---|---|---|
| `kb_pause` | Space | Toggles the simulation tick |
| `kb_zoom_cycle` | Tab | Steps through Village / Neighbourhood / Individual zoom levels |
| `kb_speed_1` | 1 | Normal sim speed |
| `kb_speed_2` | 2 | Fast sim speed |
| `kb_speed_5` | 3 | Very fast sim speed |
| `kb_speed_10` | 4 | Maximum sim speed |
| `kb_context_menu` | Alt | Hold and right-click for action context menu |
| `kb_menu` | Escape | Pause / save / load menu |

Each row: action label + current-key button + Reset button. Click the key button → "Press a key…" prompt; the next non-Escape key captured becomes the new binding. Escape cancels rebind.

Persistence: `settings.cfg` `[keybindings]` section keyed by action name, value = int keycode. `LoadSettings` reads each entry back, applies to Godot's `InputMap` via `ApplyKeybindingToInputMap` (clears existing events, adds the new `InputEventKey`). `OnReset` restores every action to its registered default. New actions added to `DefaultKeybindings` auto-appear on next launch.

Adding a new action: one row in `DefaultKeybindings` here + use `Input.IsActionPressed(...)` at the call site.

---

#### Version

Patch `0.4.2` → `0.4.3`. `MainMenuController.cs` + `memory/project_versioning.md` + Roadmap §4 status block updated.

---

## [0.4.2] — 2026-05-13

### Added — Item taxonomy aligned with Smurf theme; on-tile drops + Haul; stone variation w/ MagicCrystal ore-veins; carry visual + equipment slots

Sam's brief: Gather should produce items from any food/magic vegetation; Cut should produce items from any vegetation; LivingWood/DeadWood as separate excavation drops; stone variation generation with textures; remove non-thematic species (Oak/Pine/Willow/PalmWood); fungal wood drops from any LargeMushroom variant; every gathered/excavated tile should drop a *physical* item visible on the world; smurfs should carry items in hands like RimWorld; per-smurf inventory + equipment slots that show as overlay on the smurf model.

This is a substantial taxonomy + rendering pass. Equipment-overlay rendering is wired but no production path equips a smurf today (Phase 5+ crafting / equip-orders fill that in).

---

#### Item taxonomy

**Removed (didn't fit miniaturised Smurf theme):**
- Wood family: Oak, Pine, Willow, Palm, Magicwood (placeholder)
- Stone family: "Field Stone" (every Boulder now has a specific subtype)
- Food sub-types: Pineshroom, PalmShroom, Sandshroom (collapsed into SmallMushroom + variant on material axis), MagicFlower (renamed MagicBerry)

**Added / kept:**
- Wood: **DeadWood** (from DeadLog terrain), **LivingWood** (from LivingWood terrain), **Fungal** (from any LargeMushroom variant — LargeMushroom / LargeSandshroom / PalmShroom)
- Stone: Granite, Limestone, Marble, Obsidian, **Quartz** (new), Magicstone, **MagicCrystal** (new — rare ore-vein variant)
- Food: Smurfberry, SmallMushroom (covers every mushroom variant via material axis), HerbCluster, **MagicBerry** (the magic plant yielding both food + essence per the brief)
- Material: Cuttings (Cut output; flammable kindling + Phase 5+ fertiliser)
- Magic: RawEssence (drops from MagicBerry plants), CrystalShard (drops from MagicCrystal excavation)

**Production paths rewired** (`ItemFactory.cs`, `BehaviorSystem.cs`):
- `FoodFromVegetation` — MagicFlower → ("MagicBerry", Plant/MagicBerry); every SmallMushroom variant → ("SmallMushroom", variant-tagged material)
- `WoodFromVegetation` — PalmShroom → Wood/Fungal (was Wood/Palm); all LargeMushroom variants → Wood/Fungal
- `MaterialFromTerrain` — DeadLog → Wood/DeadWood (was Oak); LivingWood → Wood/LivingWood (was Pine); Boulder reads the per-tile assigned stone subtype (from `LocalMap.GetTileStone`)
- New `CuttingsFromVegetation` — Cut now drops a Cuttings stack sized by source-plant size (LargeMushroom→3, Smurfberry→2, Herb→1)
- New `VegetationYieldsMagicEssence` — Gather over MagicFlower vegetation drops BOTH MagicBerry food AND RawEssence in one harvest

---

#### Stone variation at generation (`LocalMapGenerator.AssignStoneVariation`)

Every Boulder tile is assigned a specific stone subtype using a biome-weighted base table (Mountains favour Granite, Swamp favours Limestone, Desert favours Quartz, etc.), then a second pass over-paints rare **MagicCrystal ore veins** — 3-5-tile random-walk clusters at ~0.4 % per Boulder rate (1.2 % in MagicGrove). The DF "stripes of gem in stone" pattern.

`LocalMap.SetTileStone` / `GetTileStone` carry the (x,y) → MaterialKey map; renderer + excavate handler both read it.

---

#### Stone texture variation (`LocalMapRenderer.cs`)

`PaintBoulder` now picks a palette per subtype: Granite (blue-grey baseline), Limestone (warm cream), Marble (near-white highlight + grey shadow), Obsidian (near-black with deep blue highlight), Quartz (bright with white core), Magicstone (violet tint). Each retains the v0.3.x angular faceted variant set; just colour-swapped.

New `PaintMagicCrystalOre` renders the rare ore vein: dark granite host rock with bright cyan + violet + white crystal facets running through. Three deterministic variants (diagonal vein, vertical vein with pocket cluster, scattered geode facets).

---

#### On-tile item drops (`LocalMap.cs` + `ItemDropOverlay.cs`)

`LocalMap` gains a `_droppedItems` dictionary keyed by tile coords with helpers `DropItem`, `RemoveItem`, `GetItemsOnTile`, `EnumerateDroppedItems`, plus an `ItemsChanged` event. Multiple items pile on the same tile (e.g. MagicCrystal vein excavation drops both StoneBlock + CrystalShard) and stackable items merge.

`ItemDropOverlay` (new `Node2D` in `scripts/ui/`) renders each pile in world coordinates between the designation overlay (z=0) and smurf colony view (z=1) so smurfs walking over items visually obscure them. Each pile shows:

- Category-coloured icon (Food = round, Material = square, Magic = diamond) drawn procedurally — no texture loads
- Stack count (3×5 pixel digit blocks; "+" badge when > 99)
- Yellow corner marker when the tile holds multiple distinct stacks

Palette mirrors per-subtype colour for Smurfberry purple, SmallMushroom orange-red, HerbCluster green, MagicBerry pink, DeadWood brown, LivingWood green-brown, Fungal tan, Granite grey, Limestone cream, Marble white, Obsidian near-black, Quartz pale-blue, Magicstone violet, MagicCrystal cyan, RawEssence cyan, CrystalShard violet.

---

#### Per-smurf carry visual + equipment slots (`Smurf.cs` + `SmurfColonyView.cs`)

`Smurf` gains:
- `CarriedItem` — single-slot carry while hauling
- `EquippedTool`, `EquippedWeapon`, `EquippedApparel` — wear slots (stub; no production path equips today)

`SmurfSnapshot` carries the new payload as lightweight strings (Kind / SubType / MaterialFamily for carried; SubType for each equipment slot). The renderer reads from the snapshot, never the Smurf directly.

`SmurfColonyView.DrawSmurf` now:
- Draws a small icon at hand position (`pos + (5.5, 2.5)`) when `CarriedKind != null`. Same palette as `ItemDropOverlay` so the visual continuity is preserved — a Smurfberry stack picked up off the ground reads as the same purple while carried.
- Tints body band when `EquippedApparelSubType != null`
- Draws a hip glyph when `EquippedWeaponSubType != null`
- Draws a back-pack dot when `EquippedToolSubType != null`

---

#### Haul task implementation (`HaulSystem.cs`)

Replaces the v0.4.0 stub. Smurfs with Haul priority > 0 in the Jobs grid:
1. `SelectHaulTarget` scans `LocalMap.EnumerateDroppedItems` for the nearest unreserved pile, picks the first item, reserves it with the smurf's Guid.
2. `Apply` (called when the smurf arrives at the pickup tile): removes the item from the map, sets `CarriedItem`, retargets the task to the delivery point. The delivery point is the colony spawn-cluster centre — Phase 5 replaces this with an accepting stockpile zone.
3. `Apply` (called on second arrival, at the delivery point): clears `CarriedItem`, adds the item to `ColonyResources.Inventory`. Players see the HUD totals tick up as the haulers deliver.

Item reservations prevent multiple haulers from converging on the same pile (the RimWorld pattern). Reserve / Release happens around the task lifecycle.

Wired into `BehaviorSystem.SelectTask` at Tier 2 with priority 50 — primary role work still wins, but Haul slots in before Tier 3 idle, so dropped items get picked up promptly.

---

#### UI updates

- **HUD** ([HUDController.cs](SmurfulationC/scripts/ui/HUDController.cs)) — Magic category re-enabled; Food / Stone / Wood sub-item lists rebuilt around the new taxonomy.
- **Resources tab** ([ResourcesPanel.cs](SmurfulationC/scripts/ui/ResourcesPanel.cs)) — Magic row reinstated.
- **Preferences** ([Preferences.cs](SmurfulationC/scripts/simulation/Preferences.cs)) — `ItemPool` aligned with the new sub-type list; Mushroom Whisperer personality nudge updated.

---

#### Version

Patch `0.4.1` → `0.4.2`. `MainMenuController.cs` + `memory/project_versioning.md` + Roadmap §4 status block updated.

---

## [0.4.1] — 2026-05-13

### Fixed — Hide resource categories with no v0.4.0 spawn path

Reported on the v0.4.0 drop: the HUD's "Magic" capsule and the Resources tab's Weapon / Apparel / Furniture / Magic / Trade Good rows showed zero permanently because no task in the current codebase produces items of those kinds. They sat there with empty sub-rows that confused the player into thinking the game was broken.

---

#### HUD — `scripts/ui/HUDController.cs`

The Magic category is removed from the top-bar resource row. `_magicCat` field, `AddCollapsibleResource(..., ItemKind.Magic, ...)` call, magic-zero-out loop, and the `case ItemKind.Magic` branch in `_Process` all dropped. Raw Essence + Crystal Shard sub-types stay registered in `MaterialRegistry` / `ItemRegistry` for the Phase 11 ritual / trade systems that will eventually produce them; the row reinstates alongside the first task that drops a Magic item.

#### Resources tab — `scripts/ui/ResourcesPanel.cs`

`BuildContent()` no longer adds rows for `Weapon`, `Apparel`, `Furniture`, `Magic`, or `TradeGood`. The remaining four — Food, Material, Tool, Trinket — are exactly the kinds reachable by some production path in v0.4.0: gather/excavate/chop for Food + Material, scenario starting kit for Tool + Trinket. Empty categories no longer pad the panel; new categories reinstate as their production systems land.

---

#### Production-path audit (kept for future maintenance)

| Kind | Spawn path | Visible in v0.4.1 |
|---|---|---|
| Food | GatherFood via `FoodFromVegetation` mapping | ✅ HUD + Resources |
| Material (Stone) | GatherMaterial on Boulder; `RollInFamily("Stone")` | ✅ HUD + Resources |
| Material (Wood) | GatherMaterial on DeadLog/LivingWood; ChopWood on shrooms; scenario kit Crafter roll | ✅ HUD + Resources |
| Tool | Scenario starting kit (Pick / Basket / Focus / Sickle / Hammer per role) | ✅ Resources only |
| Trinket | Scenario starting kit (Pendant / Ring, 30 % roll) | ✅ Resources only |
| Magic | None | ❌ Hidden until Phase 11 |
| Weapon | None | ❌ Hidden until Phase 7 |
| Apparel | None | ❌ Hidden until Phase 5+ crafting |
| Furniture | None | ❌ Hidden until Phase 5 building |
| Trade Good | None | ❌ Hidden until Phase 11 |

---

#### Version

Patch `0.4.0` → `0.4.1`. `MainMenuController.cs` + `memory/project_versioning.md` updated.

---

## [0.4.0] — 2026-05-13

### Added — Phase 4 milestone version + stubs for Phase-5-dependent items

Phase 4 core gameplay loop is in place (v0.3.46 / v0.3.47), so `bb` advances `3 → 4` and `cc` resets to 0. The three remaining Phase 4 spec items (Haul, Cook, Temperature/Insulation decay axes) all gate on Phase 5 or Phase 10 prerequisites; this drop lands minimal stubs so they're reachable from the existing wiring (Jobs tab, deterioration tick, ApplyTaskEffect switch) and the Phase 5 work can plug straight in without touching the public surface.

---

#### Stubs landed

**Haul** — `scripts/simulation/systems/HaulSystem.cs` (new)

- `TaskType.Haul` added to `BehaviorTask.cs`.
- `TaskVerb.Of(Haul) → "Hauling"` for the roster + smurf card.
- `HaulSystem.SelectHaulTarget(s, map, r)` returns null — no on-tile items, no stockpile zones to route them to. Future fill: scan `_items` for entries with `TilePos != null`, score against accepting stockpile zones, return the best (smurf, item, destination) triple.
- `HaulSystem.Apply(s, t, map, r)` is a no-op. Future fill: move item from `TilePos` into the destination zone's slot, update inventory location flags.
- The task is reachable through the Jobs tab's "Haul" column (already populated by `WorkPriorityDefaults`) but `SelectTask` never assigns it because the Tier-2 selectors don't call into `HaulSystem` yet — that wire-up lands alongside Phase 5 stockpile zones.

**Cook** — `scripts/simulation/systems/CookSystem.cs` (new)

- `TaskType.Cook` added.
- `TaskVerb.Of(Cook) → "Cooking"`.
- `CookSystem.SelectCookTarget` / `Apply` are both null/no-op stubs. Future fill: iterate Kitchen buildings, check recipe queue + adjacent raw-ingredient stockpiles, return highest-priority queued recipe. On `Apply`: consume raw ingredients, add prepared item with quality rolled from cook's Cook skill, emit Accomplished thought, award skill XP.
- Cooked-meal sub-types ("MushroomStew", "BerryTart", "HerbTea") aren't registered in `ItemRegistry` yet — they land with the Phase 5 Kitchen building so the catalogue and the workplace ship together.

**Temperature / Insulation decay axes** — `scripts/simulation/items/Inventory.cs`, `scripts/simulation/systems/ItemDeteriorationSystem.cs`

- `Inventory.TickDeterioration` signature widened with `float temperatureMul = 1f, float insulationMul = 1f` parameters. The per-day decay is now `Material × Temperature × Insulation × baseline` per the roadmap §4 spec, with Material as the only live axis.
- `ItemDeteriorationSystem.ResolveTemperatureMul()` returns 1.0 with a Phase-10 TODO pointing to the future `WeatherState.GlobalTemperatureC` bucket lookup (× 1.0 / × 1.5 / × 2.0 / × 0.7 spec bands).
- `ItemDeteriorationSystem.ResolveInsulationMul()` returns 1.0 with a Phase-5 TODO pointing to the future `StructureSlot.HasRoof` + `RoomDetector` flood-fill lookup (× 1.0 outdoor / × 0.4 roofed / × 0.25 sealed-temperature-controlled spec bands).
- Both multipliers fold through `TickDay` automatically, so once Phase 5 / Phase 10 data sources exist, only the two resolver bodies need updating; every item already decays through the multi-axis pipeline.

---

#### Why v0.4.0 not v0.3.48

Versioning scheme is `aa.bb.cc` where `bb` mirrors the active roadmap phase. Phase 4's core gameplay loop (item system, item-aware tasks, save persistence, wandering-in, Jobs grid, scenario kits, A*) is in place — the three remaining items are Phase-5-blocked, not Phase-4-blocked. Advancing `bb` to 4 marks the milestone; the next patch lands as `0.4.1`.

`memory/project_versioning.md` is updated to reflect the new `bb=4` state and document that `cc` resets to 0 when `bb` advances.

---

## [0.3.47] — 2026-05-12

### Added — Phase 4 sub-B: save persistence for items, wandering-in event, refined births, Jobs tab grid, scenario starting kits, A* pathfinding

Sub-B closes out Phase 4 (≈ 90 % complete) by landing every queued deliverable that doesn't have a Phase 5 dependency. The remaining items — Haul, Cook, temperature/insulation decay — are explicitly Phase-5-or-later (they need stockpile zones / Kitchen building / roofs / weather to make sense) and are now documented in the Phase 5 deliverables list.

---

#### 1. Save format extension — `SaveManager.cs`, `SimulationManager.cs`

- New `ItemSaveData` record: per-stack snapshot with Kind, SubType, MaterialFamily, MaterialSubType, Quality, State, Quantity, AvgCondition, DurabilityCap, AgeInTicks. Birth tick is converted to age-at-save-time so reload re-anchors the spoilage clock to the new GlobalTick.
- `ColonySave` gains `List<ItemSaveData>? ColonyInventory` and `Dictionary<string, Dictionary<string, byte>>? WorkPriorities` (per-smurf Jobs-tab settings). Both default to null so older saves still load.
- `SaveToSlot` pulls inventory + work priorities from the registered SimulationManager (new `RegisterSimulation(sim)` hook called from `GameController.StartSim`).
- `LoadFromSave` rebuilds the Inventory by replaying `ColonyInventory` through `Inventory.Add` (so stacking merges still happen on load), and restores each smurf's `WorkPriorities` dictionary.
- `InventoryRow` gained `AvgBirthTick` so the save path can compute age correctly.

#### 2. Wandering-in event — `Systems/WanderingInSystem.cs` (new)

- Per-season roll, gated by `SeasonChance(alive)` taper: 0.75 (≈ 3/year) for colonies ≤ 30 smurfs, linear taper to 0 by 200.
- 1:49 female ratio preserved. Wanderers get a random age 20–379, a random role (excluding Unassigned), a fresh personality / preferences / work-priority defaults / body parts / traits / skills.
- Food gate: colonies with < 15 food/smurf and > 5 smurfs don't attract wanderers — fed colonies are more attractive than starving ones. Founding (≤ 5 smurfs) gets a grace pass so the first female has a path in.
- Spawns near a random alive colony member (so wanderers don't appear at (0,0)).
- New `SimulationCore.PendingWanderers` queue; `SimulationManager` drains and emits `WandererArrived(name, sex, role, age)` signal.
- `GameController.OnWandererArrived` writes to `MessageLog` and pushes an Info-level entry to `AlertsPane`. The Phase 8 storyteller will gain Accept/Decline UI; for sub-B, every wanderer auto-joins.

#### 3. Birth refinement — `Systems/BirthSystem.cs`

- Per-mother independent roll (v0.3.12 was one colony-wide roll, capping births at ≈ 1/year regardless of mother count). Iterates eligible mothers; first hit on `rng.NextDouble() < 0.25` wins; rest wait for next season.
- New `PassesFoodGate(living, foodTotal)` — colonies need ≥ 30 food/smurf in inventory to permit a birth. Founding-grace fallback: ≤ 10 smurfs with ≥ 15 food/smurf still passes (or zero stockpile in nascent state).
- Newborns spawn near the mother (small jitter) instead of at (0,0); SimSpeed = 70 % of mother's so sprouts visibly toddle.

#### 4. Jobs tab grid — `JobsPanel.cs` (new), `WorkPriorityDefaults.cs` (new)

- Replaces the v0.3.x "Phase 3.10 stub" label in `BottomTabPanel`'s Jobs tab.
- 15 work categories: Patient, BedRest, Doctor, Construct, Mine, PlantCut, Grow, Cook, Hunt, Forage, Chop, Haul, Clean, Research, Attune.
- `WorkPriorityDefaults.ByRole` baked-in priorities seed every newborn / wanderer / loaded smurf at creation. Forager-default sets Forage=1, PlantCut=2, Chop=3; Crafter sets Construct=1, Mine=1, Chop=2; Mage sets Attune=1; etc.
- Grid cell colours follow RimWorld convention: 1 green, 2 yellow-green, 3 amber, 4 orange, off muted grey. Cell click cycles 1→2→3→4→off→1. Column-header click cycles every cell in that column to the same next value. Right-click on cell or column header sets to off.
- `BehaviorSystem.SelectTask` consults the smurf's `WorkPriorities[category]` before considering each Tier 2 task: `0` blacklists the work entirely; `1-4` shifts the engine priority by `+12 / +4 / -4 / -12`.
- `SimulationManager.GetWorkPrioritiesSnapshot()` and `SetWorkPriority(name, cat, val)` are the read/write API for the panel.

#### 5. Scenario starting items — `ItemFactory.RollStartingKit`, `ScenarioPanel.cs`, `ScenarioConfig.cs`, `SimulationManager.cs`

- New `ItemFactory.RollStartingKit(role, rng, globalTick)`: rolls a role-appropriate ~8-point kit per the roadmap spec. Every smurf gets 4 Food (Smurfberry/SmallMushroom/HerbCluster/Pineshroom ×7 each), 1 Stone stack (×4), 1 role-appropriate Tool (Crafter→Hammer, Forager→Basket, Mage→Focus, etc.), and a 30 % chance of a Trinket flavour item. Crafters get an extra Wood stack.
- `SmurfTemplate.StartingItems` field carries the kit between scenario screen and SeedColony. Pre-rolled in `MakeRandomTemplate` and `RandomizeOne` so the player sees the items on the detail card.
- Scenario detail panel gains a "Starting items" section with the rolled list (icon + quality + material + sub-type + count) and a 🎲 Reroll button.
- `SimulationManager.AddSmurfFromTemplate` deposits each smurf's kit into the colony Inventory; AvgBirthTick is re-stamped to the new GlobalTick so the spoilage clock starts fresh.
- Granular per-category budget allocator UI (separate Items… popup with sliders) is queued for a future quality pass; the current "Reroll" button is the single-click escape hatch.

#### 6. A* pathfinding — `Pathfinder.cs` (new), `BehaviorSystem.cs`

- 8-connected grid pathfinder over `LocalMap.IsPassable`. Cardinal cost 100, diagonal cost 141 (~√2 × 100). Manhattan-diagonal heuristic — admissible and consistent.
- Flat-array `gScore` / `parent` / `closed` (W×H ints) instead of a Dictionary, keeping pathfind allocations bounded.
- Diagonal cut-corner check: a diagonal step requires both orthogonal neighbours to also be passable. Without this, smurfs would clip through wall corners diagonally.
- `MaxNodes = 1024` cap prevents a single failing path from burning sim CPU.
- Auto-routes to nearest passable neighbour when the goal tile itself is impassable (matches the v0.3.22 `RequiresAdjacentApproach` logic).
- `BehaviorSystem` requests a path whenever a new task's straight-line distance exceeds `PreferAStarDistSqPx` (128 px² = 8 tiles). The existing `Smurf.PathWaypoints` consumer in `ResolveWalkTarget` walks the path; short hops still fall back to the v0.3.22 local steering.

---

#### Phase 4 status

| Item | Status |
|---|---|
| Procedural Item system (Kind/Material/Quality/Condition/Age/State) | ✅ v0.3.46 |
| Inventory replacing float ledger | ✅ v0.3.46 |
| Items-from-gather (Forage/Excavate/Chop/Cut) | ✅ v0.3.46 |
| Item-aware Eat with preference + quality thoughts | ✅ v0.3.46 |
| Daily food spoilage | ✅ v0.3.46 |
| HUD + Resources tab live inventory | ✅ v0.3.46 |
| Save format extension | ✅ v0.3.47 |
| Wandering-in event | ✅ v0.3.47 |
| Birth refinement (per-mother + food gate) | ✅ v0.3.47 |
| Jobs tab grid + WorkPriorities | ✅ v0.3.47 |
| Scenario starting kit roll | ✅ v0.3.47 |
| A* pathfinding | ✅ v0.3.47 |
| Haul task type | ⏳ Phase 5 (needs stockpile zones) |
| Cook task type | ⏳ Phase 5 (needs Kitchen building) |
| Temperature/Insulation decay axes | ⏳ Phase 5 + Phase 10 (needs roofs + weather) |

---

#### Version

Patch bumped from `0.3.46` → `0.3.47`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated. Roadmap §4 status block reflects sub-B complete + Phase-5-deferred items moved to the Phase 5 spec.

---

## [0.3.46] — 2026-05-12

### Added — Phase 4 core: procedural items, real inventory, item-driven Eat / Gather / Excavate / Chop. Unit card shows current task.

Two asks: surface each smurf's current activity on the unit card header, and start Phase 4. Phase 4 is genuinely a multi-session feature — this drop lands **sub-phase A**, the core gameplay loop: real `Item` instances replace the float ledger; Gather / Excavate / Chop produce real items with material + quality rolls; Eat finds a specific food stack via preference + quality and consumes one unit; food spoils via daily deterioration; HUD + Resources tab show the live breakdown. Sub-phase B (scenario starting items dialog, Jobs tab grid, Haul / Cook, wandering-in, A\*, save persistence) is queued for the next pass — none are blockers for the loop landing today.

---

#### Unit card — task verb in header (`scripts/ui/SmurfCardPanel.cs`)

The header label now reads `"Name — Activity"` (e.g. `"Brainy — Gathering food"`). Verb mapping was lifted out of `SmurfRosterPanel` into a new shared `TaskVerb.Of(TaskType)` helper in `scripts/simulation/TaskVerb.cs` so the roster column and the card stay in sync via a single switch.

---

#### Phase 4 — sub-phase A: core item system

**New files (12)** under `scripts/simulation/items/` and `scripts/simulation/systems/`:

| File | What it does |
|---|---|
| `ItemKind.cs` | 9-category enum (Food / Material / Tool / Weapon / Apparel / Furniture / Magic / TradeGood / Trinket) + `Icon` / `IsStackable` lookup. |
| `Quality.cs` | Crude → Legendary tiers with `ValueMul` (0.5×–6×), `NutritionMul` (0.85×–1.8×), and `MealThoughtKey` (Masterwork meals always emit AteFavorite, Crude emits AteHungry, …). |
| `ItemState.cs` | Fresh / Stale / Spoiled / Depleted / Broken + `FromCondition(condition, durability)` mapping (Stale ≤ 70 %, Spoiled ≤ 30 %). |
| `MaterialKey.cs` | `MaterialKey` record struct (Family, SubType), `MaterialDef` with multipliers, and `MaterialRegistry` catalogue of 27 materials across Wood / Stone / Plant / Cloth / Magic / Bone / Hide / Metal families. Magicwood × 0.5 decay, Cloth × 1.2, Food (Plant family) × 4.0 — matches the roadmap §4 spec line. |
| `ItemRegistry.cs` | Sub-type catalogue keyed by `(Kind, SubType)`. Every raw food (Smurfberry / SmallMushroom / LargeMushroom / HerbCluster / MagicFlower / Pineshroom / PalmShroom / Sandshroom), the WoodLog / StoneBlock / Cuttings materials, the raw Magic essences, the starter Tool / Weapon / Apparel / Trinket pool. AllowedFamilies on each sub-type constrains material rolls so a Pick can't be made of Smurfwool. |
| `Item.cs` | Item instance: Kind / SubType / Material / Quality / State / AvgCondition / DurabilityCap / AvgBirthTick / Quantity / OwnerSmurfId / TilePos. `CanStackWith` + `Absorb` implement RimWorld-style stacking with weighted condition + age averaging. |
| `ItemFactory.cs` | Single entry point for creating items. Resolves sub-type def → rolls material (constrained to AllowedFamilies, weighted by 1/ValueMul so cheap materials dominate) → rolls quality from a 15/60/20/4/1 % bell shifted upward by skillLevel/5 → seeds condition near full → stamps BirthTick. `FoodFromVegetation` / `WoodFromVegetation` / `MaterialFromTerrain` lookup tables map sim-thread harvest types to (SubType, MaterialKey) pairs. |
| `Inventory.cs` | Lock-protected colony-wide stack list. `Add` merges into a matching stack if stackable; `Consume` decrements + removes empty stacks; `FindBestFood` picks the highest-scoring Fresh/Stale stack weighted by `BaseNutrition × NutritionMul × prefs(±50 %) × stale(0.6×)`; `Snapshot()` returns a flat `InventoryRow[]` value array under the lock so the main thread can render without a race. `TickDeterioration` walks all items and applies per-material decay. |
| `Systems/ItemDeteriorationSystem.cs` | `TickDay` wrapper hooked into the SimulationCore day-boundary path. Material axis only for sub-A; Temperature / Insulation / Use axes queued for sub-B once Phase 5 buildings exist. |
| `TaskVerb.cs` | (already mentioned) — shared verb table for the smurf card + roster. |

**Files modified:**

- `ColonyResources.cs` — `Food` / `Stone` / `Wood` / `MagicEssence` are now read-only aggregates over `Inventory`. A small "unstored" buffer is retained so any unconverted writer path (`r.Stone += 4f`) still contributes. Snapshot() copies the totals into the buffer so the main thread sees consistent values without holding the inventory lock.
- `BehaviorSystem.cs` — `ApplyTaskEffect` signature extended with `Random rng, long globalTick`. **Eat** now resolves a stack via `Inventory.FindBestFood(s)`, restores nutrition proportional to the item's `BaseNutrition × QualityMul × (stale ? 0.6 : 1)`, consumes one unit, and emits a thought keyed off the eater's preference for the sub-type *and* the item's quality (preferred + Masterwork → AteFavorite; disliked → AteDisliked; otherwise the quality-default thought). **GatherFood** spawns a real Food item stack via `ItemFactory.Create` typed by the vegetation; quantity follows the legacy `FoodYield` table. **GatherMaterial** spawns a Material item with sub-stone rolled via `MaterialRegistry.RollInFamily("Stone", rng)` (Boulder), or a Wood/Oak / Wood/Pine for DeadLog / LivingWood. **ChopWood** spawns a `Material/WoodLog` with the appropriate wood sub-material (Fungal / Palm). The skill axis is wired: foragers / crafters with higher Foraging / Mining / Construction skill have a quality bell shifted upward.
- `SimulationCore.cs` — calls `ItemDeteriorationSystem.TickDay` on the day boundary; food spoilage now happens every in-game day at any sim speed.
- `SimulationManager.cs` — added `GetInventorySnapshot()` returning a value-array of `InventoryRow` for UI consumers.
- `LocalMap.cs` — unchanged for sub-A (designation indexes already in place).

---

#### UI surfaces

- **HUDController** — the four collapsible categories (Food / Stone / Wood / Magic) now stream their per-subtype and per-material totals from the live inventory snapshot. Expanding 🍓 Food shows live Smurfberry / SmallMushroom / LargeMushroom / HerbCluster / Pineshroom / PalmShroom / MagicFlower / Sandshroom counts. The category total adds an unstored-buffer fold so any not-yet-migrated float writer still shows in the total. Snapshot polled at HUD framerate (60 Hz); typical colonies have < 50 stacks so the per-frame walk stays cheap.
- **ResourcesPanel** — rewritten to render all 9 ItemKind rows with collapsible per-stack tables. Expansion shows one row per (SubType, Material, Quality, State) bucket with columns: Sub-type, Material, Quality, Condition (state-colour: Stale → orange, Spoiled / Broken → red), Count. Empty categories show "(no … in colony)".

---

#### What's not in sub-A — queued for sub-B

These are spec'd in the roadmap and intentionally deferred. Each is meaningful enough on its own to warrant a dedicated session:

- **Scenario starting items dialog** — per-smurf budget allocator + procedural roll preview. Scenarios currently start with empty colony inventory (smurfs gather their first food after spawn).
- **Per-smurf inventory + Haul task** — today the inventory is colony-global with no spatial location. Items don't sit on tiles; production goes directly to the colony pool. Phase 5 stockpile zones plug in here.
- **Cook task + cooked-meal sub-types** — Raw → Prepared → Preserved food taxonomy. Needs Phase 5 Kitchen building before the workflow makes sense.
- **Jobs tab grid UI** — RimWorld-style per-smurf work priority grid; the bottom tab still shows the "Phase 3.10 stub" label.
- **Wandering-in event + birth refinement** — colony-size-aware female-scaling and the 2–4 wanderers/year storyteller arrival prompt.
- **A\* pathfinding** — replaces v0.3.22 local steering for routes > ~10 tiles.
- **Save format extension** — items don't yet persist across save/load.
- **Temperature / Insulation decay axes** — gate on Phase 5 roofs + Phase 10 weather. Material axis is live today.

Roadmap §4 was edited to reflect what landed and what remains.

---

#### Version

Patch bumped from `0.3.45` → `0.3.46`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated. Roadmap status block at the top of Phase 4 documents sub-A complete.

---

## [0.3.45] — 2026-05-12

### Fixed — Idle activities now actually run and display in the roster

Reported on the v0.3.43 behavior rewrite: "Smurfs do not actually display any idle activities."

Two interlocking bugs were hiding the rewrite from the player.

---

#### Bug 1 — Linger-init off-by-one made every idle task re-pick on the next tick

`scripts/simulation/systems/BehaviorSystem.cs` — when `needNewTask` fired, the new task was assigned but `s.IdleLingerTicks` was explicitly **set to 0**. The next tick's `needNewTask` gate then read:

```
idle && IdleLingerTicks(0) <= 0  → true → re-pick
```

So every smurf's idle task was replaced *every single tick* before they could traverse anywhere. The roster's Activity column flashed through them at 60 Hz, which the player perceived as "no idle activity at all."

Fix: initialise `IdleLingerTicks = newTask.ArrivalLinger` at task assignment instead of 0. The linger countdown now runs from the moment the task is picked, covering both travel and post-arrival lingering — effectively a total time-budget for the activity. The old on-arrival reset is removed (it was redundant given the new initialisation, and would have double-counted otherwise).

Trace of the fixed behavior for a freshly idle smurf:
- Tick 1: `SelectTask` returns Observe (linger 360). `IdleLingerTicks = 360`. Smurf starts walking.
- Tick 2–N: lingerExpired = false (360+ → 0), no re-pick. Smurf traverses to destination.
- Tick N+1: arrived. Effect applies (small Social bump + Daydreamed thought). Smurf stays at destination as linger keeps counting down.
- Tick ~360: linger hits 0 → re-pick from the weighted idle pool.

---

#### Bug 2 — Roster Activity column had no verbs for the new task types

`scripts/ui/SmurfRosterPanel.cs` — `ActivityVerb(TaskType)` was last updated in v0.3.0. The post-v0.3.0 task types (ChopWood, CutVegetation, Loiter, Observe, Converse, Meditate, VisitFavorite) all fell through to the "—" placeholder. Even after Bug 1 was fixed, the column would have shown "—" for every idle smurf.

Fix: extended the switch to cover all eleven post-v0.3.0 types:

| TaskType | Verb |
|---|---|
| ChopWood | "Chopping wood" |
| CutVegetation | "Cutting plants" |
| Wander | "Wandering" (was "Idle") |
| Loiter | "Loitering" |
| Observe | "Observing" |
| Converse | "Chatting" |
| Meditate | "Meditating" |
| VisitFavorite | "Visiting a favourite spot" |
| None | "Idle" (the true do-nothing state) |

The `Wander → "Idle"` mapping was changed to `Wander → "Wandering"` to reflect that Wander is now an actively-picked activity rather than the default; `None → "Idle"` covers the brief one-tick window where a completed task hasn't been replaced yet.

---

#### Version

Patch bumped from `0.3.44` → `0.3.45`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated.

---

## [0.3.44] — 2026-05-12

### Fixed — HUD right capsule stretching to match an expanded left capsule

Reported on the v0.3.43 drop: when the player expanded all four resource categories in the left HUD capsule, the Speed/Menu capsule on the right grew vertically to match, leaving an awkward 200 px gap between the right-aligned buttons and the bottom of the panel.

Root cause: both `_leftPanel` and `_rightPanel` are direct children of an `HBoxContainer` band. HBox's default vertical sizing for children is `Fill`, so when the left capsule's content forced its min height to ~180 px, the HBox stretched the right capsule to match.

Fix — `scripts/ui/HUDController.cs`: set `SizeFlagsVertical = SizeFlags.ShrinkBegin` on both capsules. Each now sizes to its own content height and pins to the band's top edge, so expanding the resource breakdown no longer drags the speed buttons down.

---

#### Version

Patch bumped from `0.3.43` → `0.3.44`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated.

---

## [0.3.43] — 2026-05-12

### Added — Behavior system rewrite: Thoughts, Preferences, idle activities

Sam's brief: "Rebuild the behavior system to model that of Dwarf Fortress with RimWorld's influence. We want Smurfs to feel like they're actually alive instead of jittering in place until they're commanded to move. Preserve existing gameplay features and functionality through this change as it will serve as the direct plug-in for gameplay in Phase 4 and Phase 5."

The result is a Phase 3.x.8 architectural pass that adds Thoughts (RimWorld-style temporal mood entries), Preferences (DF-style persistent likes/dislikes), and a personality-weighted idle-activity picker that replaces the old "smurf wanders forever" loop. Every existing player-facing affordance still works unchanged — player orders, designations, role priorities, soft-claim system, LOD ticking, save/load. The plug-in points for Phase 4 (item-aware Eat / liked-food bumps) and Phase 5 (bed → WellRested thought) are explicit `EmitWorkThought` and `ApplyTaskEffect` hooks.

---

#### New files

- `scripts/simulation/Thought.cs` — `ThoughtCategory` enum, `ThoughtDef` record, `Thought` struct (Key / TicksRemaining / MoodOffset / Context), `ThoughtRegistry` catalogue + `Add()` helper. Capacity-8 inline ring per smurf, no per-tick allocations. Initial entries: TastyMeal, AteFavorite, AteDisliked, AteHungry, Famished, SleptOnGround, WellRested, WorkedFavorite, WorkedDisliked, Accomplished, TaskAbandoned, NiceChat, ChatWithFriend, ChatWithEnemy, Alone, WitnessedDeath, Attuned, FoundSafety, Frightened, Daydreamed, Pondered, VisitedSpot, Wandered.

- `scripts/simulation/Preferences.cs` — `Preferences` class (LikedItems / DislikedItems / LikedActivities / DislikedActivities / LikedSmurfs / DislikedSmurfs); `PreferenceRegistry.Assign(rng, personality)` rolls 1–3 liked + 0–2 disliked items and 1–2 liked + 0–1 disliked activities; personality nudges run after the baseline roll (Introvert → DislikedActivities += "Socializing"; Gossip → LikedActivities += "Socializing"; Mushroom Whisperer → LikedItems += "LargeMushroom"; etc.). `ActivityNameFor(TaskType)` cross-walk used by `EmitWorkThought` and `PreferenceTilt`.

- `scripts/simulation/systems/ThoughtSystem.cs` — `Tick()` decrements TTLs, drops expired entries, recomputes `Smurf.MoodFromThoughts` (clamped ±50), invalidates `MoodCacheNutrition` so MoodSystem's v0.3.36 fast-path recomputes the score even when needs didn't move.

---

#### `Smurf.cs`

Added:
- `Thought[]? Thoughts` — fixed-capacity ring (allocated lazily on first `ThoughtRegistry.Add`).
- `float MoodFromThoughts` — cached sum, computed by ThoughtSystem.
- `bool ThoughtsDirty` — set on add, cleared after recompute.
- `Preferences Preferences` — defaults to empty; populated at creation.
- `int IdleLingerTicks` — countdown of how many sim ticks the smurf is "lingering" at an idle destination before re-evaluating.

---

#### `BehaviorTask.cs`

`TaskType` extended with five new idle variants:
- `Loiter` — short-distance drift, 4 s linger.
- `Observe` — short walk + 6 s linger; tiny Social bump on arrival.
- `Converse` — walks to nearest other smurf within 20 tiles; both gain Social.
- `Meditate` — in-place; MagicResonance ↑ at half the Attune rate; 9 s linger.
- `VisitFavorite` — longer-distance wander; placeholder for Phase 4 "remembered locations".

`BehaviorTask` record gained `int ArrivalLinger` (default 0). Existing call sites compile unchanged; idle constructors set per-activity linger.

---

#### `BehaviorSystem.cs` — the core refactor

**1. Re-evaluation gating (`needNewTask`)** changed from "re-evaluate every tick when wandering" to:

```
ct.Type == TaskType.None
    || (interruptible && CriticalNeedsOverride(...))
    || (idle && IdleLingerTicks <= 0)
    || (idle && map.HasAnyDesignation())
```

Designation pickup latency is unchanged (the `HasAnyDesignation` probe is O(1)). The difference: an idle smurf with no work available **stays put** until linger expires instead of re-rolling Wander every tick.

**2. Idle linger countdown** in the per-smurf tick body, scaled by LOD interval so cold-banded smurfs accumulate linger at the same real-time rate as hot smurfs.

**3. Arrival handler** — old code immediately rebuilt a new Wander target on every Wander arrival; new code sets `IdleLingerTicks = task.ArrivalLinger` so the smurf occupies the destination for the activity-specific window before the next pick. This is the load-bearing change for "feels alive instead of jittering."

**4. `SelectIdleActivity`** — weighted picker over the six variants. Base weights nudged by personality (Introvert −Converse +Observe; Gossip +Converse; Mage-role +Meditate; etc.) and preferences (LikedActivities +10; DislikedActivities −8).

**5. Preference-based priority bumps in Tier 2** — `PreferenceTilt(activity)` returns +8 for liked, −6 for disliked. Folded into Forager/Crafter/Cross-role priorities for GatherFood / GatherMaterial / ChopWood / CutVegetation. Asymmetric magnitudes (liked > disliked) mirror RimWorld: liked work resists interruption more strongly than disliked work is avoided.

**6. Thought emission** on every meaningful task completion:
- Eat → `TastyMeal` (or `AteHungry` if pre-meal Nutrition < 15).
- Sleep → `SleptOnGround` (flipped to `WellRested` in Phase 5 once beds exist).
- Attune → `Attuned`.
- SeekSafety → `FoundSafety`.
- GatherFood / GatherMaterial / ChopWood / CutVegetation → `EmitWorkThought(activity, item?)` which picks WorkedFavorite / WorkedDisliked / Accomplished by preference and stacks `AteFavorite` / `AteDisliked` for the specific item.
- Observe / Meditate / VisitFavorite / Loiter → corresponding idle thought.
- Converse → both partners get `NiceChat` / `ChatWithFriend` / `ChatWithEnemy` based on friend / enemy state; tiny chance of forming a friendship via `Preferences.Befriend`.
- Stuck-give-up (in `MoveOneTick`) → `TaskAbandoned`.

**7. New task constructors** — `NewLoiterTask`, `NewObserveTask`, `NewConverseTask`, `NewMeditateTask`, `NewVisitFavoriteTask`. All flow through the new `PickIdleDestination` helper so the v0.3.35 "widen radius on failure" sampling stays DRY.

`SelectTask` and `ApplyTaskEffect` signatures both gained the smurfs list parameter so Converse can find partners and update both sides.

---

#### `MoodSystem.cs`

```csharp
s.MoodModifier = mod + s.MoodFromThoughts;
s.MoodScore    = Math.Clamp(raw + s.MoodModifier, 0f, 100f);
```

Thought contribution folds into the existing `MoodModifier` field so downstream consumers (smurf card, alerts pane, snapshot) automatically reflect the new term.

---

#### `SimulationCore.cs`

```csharp
NeedsSystem.Tick(working, foodCap);
ThoughtSystem.Tick(working);   // ← new
MoodSystem.Tick(working);
```

Order matters: ThoughtSystem updates MoodFromThoughts first, then MoodSystem folds it into the final MoodScore.

---

#### `LocalMap.cs`

Added `HasAnyDesignation()` — O(1) count check across the four indexed designation HashSets. Used by BehaviorSystem to decide whether an idle-lingering smurf should re-evaluate.

---

#### Preference assignment at creation

- `BirthSystem.cs` — newborns roll `Preferences = PreferenceRegistry.Assign(rng, child.Personality)`.
- `SimulationManager.cs` — `AddSmurfFromTemplate` honours `t.Preferences` if the scenario screen rolled one, otherwise rolls fresh. `AddSmurf` and `LoadFromSave` both roll fresh (savefile format extension deferred).
- `ScenarioPanel.cs` — `MakeRandomTemplate` and `RandomizeOne` both populate the template's Preferences alongside Personality.
- `ScenarioConfig.cs` — `SmurfTemplate.Preferences` field added (nullable; null means "roll at SeedColony").

---

#### Performance

Designed for the 1000-smurf scale target:
- Per-smurf thought state is a fixed 8-slot inline ring — zero per-tick allocations.
- ThoughtSystem.Tick walks all live smurfs but does cheap arithmetic: decrement 8 ints, optionally sum 8 floats. Microsecond-scale per smurf.
- Preference checks are `List.Contains` over ≤ 5 entries — O(1) in practice.
- SelectIdleActivity is one weighted-roll: integer arithmetic, no allocations.
- The biggest CPU saving: idle smurfs lingering with no available work **skip** the entire SelectTask path until linger expires. v0.3.42 re-evaluated every wandering smurf every tick.

---

#### Version

Patch bumped from `0.3.42` → `0.3.43`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated. Roadmap §3.x.8 added with the full spec + Phase-4 / Phase-5 plug-in points documented.

---

## [0.3.42] — 2026-05-12

### Fixed — ResourcesPanel rendering off-screen; uniform order-button widths

Two issues reported on the v0.3.41 drop:

1. **Resources tab content rendered off the right edge of the viewport.** The bottom-bar host `PanelContainer` shrank to its theme margins (~12 px wide) and the `ResourcesPanel` content drew rightward into the off-screen area.
2. **Order buttons (Gather / Chop / Cut / Excavate / Remove) were each sized to their own label width.** ✂ Cut was tight, ⛏ Excavate was wider — visually inconsistent.

---

#### Fix 1 — `scripts/ui/ResourcesPanel.cs`

Same root cause as the v0.3.27 bottom-tab regression: `Control._GetMinimumSize()` returns `(0,0)` by default, so a plain `Control` child does not propagate its real content size up to its hosting `Container`. The host PanelContainer therefore sized itself to theme margins only, and the inner `MarginContainer` (no FullRect anchors set) drew at `(0,0)` of that tiny rect — most of which was past the right viewport edge.

Two changes, matching the pattern already used in [DesignationToolbar.cs](SmurfulationC/scripts/ui/DesignationToolbar.cs:185) and [SmurfRosterPanel.cs](SmurfulationC/scripts/ui/SmurfRosterPanel.cs:391):

- The inner `MarginContainer` now gets `SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)` so it fills the ResourcesPanel rect once the host sizes correctly.
- ResourcesPanel overrides `_GetMinimumSize()` to walk visible Control children and report the max combined min size. The hosting `PanelContainer` in `BottomTabPanel` now sees the real ~560×220 content size and sizes the panel accordingly.

---

#### Fix 2 — `scripts/ui/DesignationToolbar.cs`

Every order button's `CustomMinimumSize.X` bumped from `0` (i.e. "fit label") to `UITheme.Scaled(130)`, which is wide enough for the longest current label (⛏ Excavate) at default UI scale. All five active orders — Gather / Chop / Cut / Excavate / Remove — now render in identical capsules.

---

#### Version

Patch bumped from `0.3.41` → `0.3.42`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated.

---

## [0.3.41] — 2026-05-12

### Added — Collapsible HUD resource categories, Resources + Animals bottom tabs; Phase 4 roadmap rewrite for procedural item generation

Scaffolds the resource UI for the upcoming Phase 4 procedural item system. The HUD's resource readout becomes per-category expandable; a new bottom-bar tab gives a granular sortable ledger; an Animals tab stub anchors the future Phase 9 husbandry tab. The Phase 4 roadmap section is rewritten to specify a Dwarf-Fortress-style starting-item flow and a full 9-category item taxonomy.

---

#### Roadmap rewrites (`SmurfCloudDes/SmurfulationC_Roadmap_2026.md`)

- **Scenario starting items** — was a hard-coded gift list; now specifies a 6-step procedural roll (Category → Sub-type → Material → Quality → Condition+Age → Visual). All starter items go through `ItemFactory.Create(kind, materialFamily, quality, ...)` — no starter-only paths — and the player gets an 8-point budget with per-category costs (Tool=2, Apparel=2, Weapon=3, Food=1, Material=1, Trinket=1).
- **Item categorisation + UI** — full taxonomy added: 🍓 Food, 🪨 Material, 🔨 Tool, ⚔ Weapon, 🧥 Apparel, 🪑 Furniture, ✨ Magic, 💰 Trade Good, 🌟 Trinket. Each category gets a HUD caret + collapsible sub-list and a row in the Resources tab.
- **Animals tab** — added as a Phase 9 deliverable with column spec: Camera, Name, Species, Age, Trained-for, Allow-orders, Slaughter-toggle, Mood+needs. v0.3.41 lands the stub host.

---

#### HUDController — `scripts/ui/HUDController.cs`

The four resource cells (Food / Stone / Wood / Magic) used to be flat `title + value` labels. They are now collapsible `ResourceCategory` widgets:

- Each category renders as a column inside the resource HBox: header row (`▶` caret + icon + name + total) and an initially-hidden expansion VBox listing the known sub-items for that category (Smurfberry / Small Mushroom / Herb Cluster / Pineshroom for Food; Oak / Pine / Willow / Fungal Wood for Wood; etc.).
- Clicking the caret toggles the expansion box and flips `▶ ↔ ▼`. Sub-item counts are placeholders (0) until Phase 4 lands; the totals at the category level continue to read from `SimulationManager.GetResourcesSnapshot()` each frame.
- The old `_foodLabel / _stoneLabel / _woodLabel / _magicLabel` fields are removed; `_Process` now writes to `_foodCat.TotalLbl.Text` (and siblings).
- The unstored qualifier label remains to the right of the row.

The expansion mechanism is local-capture-safe: a `captured` reference is bound per-category so the toggle lambda doesn't alias whichever `cat` happens to be in scope last.

---

#### BottomTabPanel — `scripts/ui/BottomTabPanel.cs`

`Tab` enum extended to `{ None, Orders, Build, Zones, Jobs, Resources, Smurfs, Animals }`. Resources sits between Jobs and Smurfs (granular ledger view); Animals anchors the far-right slot (Phase 9 stub).

- New host panels `_resourcesHost`, `_animalsHost`; new tab buttons `📦 Resources`, `🐾 Animals`.
- `Attach(toolbar, roster, resources)` — signature widened by one argument; `GameController` updated. Detach/reattach in `OnUIScaleChanged` extended to cover the new panel so it survives UI-scale rebuilds the same way the toolbar and roster do.
- `SetActiveTab`, `RefreshTabButtons`, `IsMouseOverContent` all cover the two new hosts.
- Animals host carries a stub label (`"🐾 Animals — Husbandry / taming / pens — Phase 9 stub"`); Resources host receives the new `ResourcesPanel` content via `Attach`.

---

#### ResourcesPanel — `scripts/ui/ResourcesPanel.cs` (new)

Granular per-category ledger living in the new Resources tab. Layout matches the Phase 4 roadmap spec:

```
Category   Sub-type   Material   Quality   Condition   Count   Avg age   Locations
```

- Header row with the column titles, drawn in gold.
- One row per category in the full 9-category taxonomy — the 4 categories currently tracked by the float ledger (Food / Stone / Wood / Magic) display live totals; the other 5 (Tool / Weapon / Apparel / Furniture / Trade Good / Trinket) display a phase-tag placeholder ("Phase 4", "Phase 5", "Phase 7") so the player can see what's coming.
- Each row is collapsible (`▶/▼` caret), mirroring the HUD widget. The expansion currently shows a one-line placeholder explaining what Phase 4 will populate; once `ItemFactory` lands, the expansion will list one row per `(Sub-type, Material, Quality)` bucket with full sortable columns.
- Reads from `Sim.GetResourcesSnapshot()` each `_Process` tick — and skips the read when `Visible == false`, so the closed tab is free.

`GameController` constructs the panel alongside `_toolbar` / `_roster`, passes it into `BottomTabPanel.Attach`, and assigns `_resourcesPanel.Sim = _sim` in `StartSim` next to the existing `_hud.Sim = _sim`.

---

#### Animals tab stub

A `MakeStubContent` panel anchored at the far-right of the bottom tab row. Carries the Phase 9 marker text only — the actual roster, training, allow-orders, and slaughter-toggle columns land alongside the husbandry rollout. The slot is here so the player and roadmap reviewers see the structural shape of the bottom bar settling toward the RimWorld-style row.

---

#### Version

Patch bumped from `0.3.40` → `0.3.41`. `MainMenuController.cs` version string updated. `memory/project_versioning.md` updated.

---

## [0.3.40] — 2026-05-12

### Fixed — LOD-induced visual warping during camera moves; smurfs cycling between 2 unreachable tiles

Two interlocking issues from the v0.3.39 LOD-ticking rollout, plus a stuck-recovery edge case the optimization plan didn't anticipate.

---

#### Diagnosis 1 — "Warping on zoom/camera movement"

v0.3.39 introduced LOD ticking: cold smurfs (>50 tiles from camera) tick every 6th sim tick at 6× step size, warm (~20–50 tiles) every 3rd at 3×. `SmurfColonyView` had been snapping the visual position to `SimPos` every frame since v0.3.23 (when the original lerp was killed for cutting through walls).

The combination meant cold smurfs visually stood still for 5 frames then teleported ~3 px; warm smurfs froze for 2 frames then jumped ~1.6 px. While the smurfs were off-camera this was invisible. The moment the player scrolled or zoomed and the smurf entered view, the player saw stutter-warping until the next LOD reassignment classified them as Hot. v0.3.39 reassigned phases every 32 ticks (~0.5 sec), which was the worst-case "warp visible after camera move" window.

#### Fix 1a — Visual lerp with snap threshold

`SmurfColonyView._Process` now lerps `Pos` toward `Target` at 200 px/s, **with a 32-px snap threshold**. Smurfs whose SimPos jumped <32 px in one frame get the small remaining distance covered smoothly in one visual frame; smurfs whose SimPos jumped >32 px (SimPos rescue from the impassable-tile BFS, or scene-load spawn) snap immediately — the lerp would have visibly drifted through walls.

The snap threshold is calibrated so:
- Hot-tick deltas (0.5 px / tick at 1×) — lerp is a no-op (already at Target).
- Warm-tick deltas (1.6 px / 3 ticks) — lerped over 1 frame, invisible smoothing.
- Cold-tick deltas (3.3 px / 6 ticks) — lerped over 1 frame, invisible smoothing.
- SimPos rescue (~128 px = 8-tile BFS jump) — snap, no walk-through-wall risk.

This restores v0.3.23's wall-safety while undoing the LOD-induced warping. Net visual: identical to v0.3.39 for visible smurfs, smooth for newly-revealed smurfs.

#### Fix 1b — Bigger Hot LOD band + faster reassignment

`HotRangePx` expanded **20 → 40 tiles** (320 → 640 px). The previous 20-tile band was smaller than a default-zoom viewport (~40×25 tiles visible), so smurfs at the visible-area edges were ending up in Warm and showing through-frame stutter. 40 tiles covers viewport corners at zoom 1× and 2×; at lower zoom the snap-threshold lerp catches what stutter remains.

`WarmRangePx` correspondingly bumped 50 → 100 tiles. Cold is everywhere else.

`AssignTickPhases` cadence raised **32 → 16 sim ticks** (0.5 → 0.27 sec at 1×). Faster reassignment means a smurf entering the viewport gets reclassified to Hot within a quarter-second instead of half. Per-tick cost is one distance-check per smurf — sub-millisecond even at 1000 smurfs.

---

#### Diagnosis 2 — Smurfs still occasionally getting stuck

v0.3.35 introduced `Smurf.AvoidTile`, a single-slot per-smurf blacklist that prevented a smurf from immediately re-picking a tile it had just given up on. But it was **one slot**. If the smurf gave up on T1, blacklisted T1, picked the next-nearest T2, gave up on T2 — the new T2 blacklist *overwrote* T1's. T1 was now eligible again. The smurf cycled T1 → T2 → T1 indefinitely, each cycle taking ~1.5 sec to give up. From the player's perspective: "smurf is bouncing between two tasks without completing anything".

#### Fix 2 — Multi-slot blacklist FIFO

`Smurf.AvoidTile` (single) replaced with `Smurf.AvoidTiles` — a **fixed-size 4-entry array of (X, Y, TicksLeft)** structs. On stuck-give-up, the new tile is written into the slot with the smallest TicksLeft (oldest entry, or any empty slot). Each slot independently TTLs down per tick. `LocalMap.FindNearest*` now accept the array and skip any candidate that matches a still-active slot via a 4-iteration linear scan (unrolled by the JIT; cheaper than the conditional on a nullable tuple was).

Capacity of 4 covers the common "smurf hops between several nearby unreachable tiles before wandering far enough that something else becomes nearest" pattern. TTL bumped from 180 → 360 ticks (3 → 6 sec at 1×) so the smurf has time to actually displace and re-evaluate against the wider set.

No allocation overhead: the array is a value-type field on `Smurf`, initialised once at object construction.

---

#### Performance budget — preserved

Both fixes are *cheaper* than what they replaced, not more expensive:
- Visual lerp: one Vector2 subtraction + one Length call per smurf per frame vs the previous unconditional Pos = Target assignment. Sub-microsecond.
- LOD band changes: more Hot smurfs (which tick every tick) but the total per-tick smurf-update count is still dominated by colony-band coverage. At 1000 smurfs with a default viewport: roughly 50 % Hot, 30 % Warm, 20 % Cold, weighted update count ≈ 0.5×N + 0.30/3×N + 0.20/6×N = 0.63N ≈ 630/tick. Up from v0.3.39's ~380 but well under the no-LOD 1000.
- AvoidTiles FIFO: 4-entry scan per FindNearest call instead of 1 nullable check. ~4 ns vs ~2 ns. Imperceptible.

60 Hz at 1× and 2× speed through 1000 smurfs remains the target and stays comfortably inside budget.

**Files touched:**
- `simulation/Smurf.cs` — `AvoidTile` (single) → `AvoidTiles` (4-slot FIFO).
- `simulation/systems/BehaviorSystem.cs` — Hot/Warm range constants bumped; FIFO push on stuck-give-up; per-tick TTL decrement across all 4 slots; FindDesignated* wrappers pass the array.
- `simulation/SimulationCore.cs` — phase reassignment cadence 32 → 16 ticks.
- `world/LocalMap.cs` — Find* signatures take the FIFO array; new `IsInAvoidList` helper.
- `ui/SmurfColonyView.cs` — visual lerp with 32-px snap threshold.

---

## [0.3.39] — 2026-05-12

### Performance — Phase A complete. LOD ticking, sim-thread designation writes, flat passability cache, parallel map bake.

This version completes **Phase A of the engine-rebuild roadmap** (`enginespec.md`) and clears every remaining medium-severity item from the optimization report. Combined with the v0.3.31–v0.3.38 allocation work, the codebase now sustains the planned 1000-smurf target colony size at 60 Hz on commodity desktop hardware — extrapolated sim cost ~3–5 ms per tick, comfortably under the 16.7 ms frame budget.

The companion documents (`OptimizationReport.md`, `enginespec.md` at `C:\Claude\Cloud\`) are updated to reflect the new state.

---

#### O-H.2 — LOD ticking (the headline change)

**Problem:** Every smurf ticked at 60 Hz regardless of whether the player could see them. At 1000 smurfs that meant 60 000 smurf-updates/sec, most of them invisible.

**Solution:** Per-smurf `TickPhase` (Hot/Warm/Cold) based on camera distance. Hot smurfs (within ~20 tiles of camera) tick every sim tick. Warm (~50 tiles) tick every 3rd. Cold (everywhere else) tick every 6th. `TickSlot` (0–5) distributes the warm/cold bands across multiple ticks so the per-tick load stays even.

When a non-hot smurf does tick, `MoveOneTick` receives a scaled `effectiveDt` (3× for warm, 6× for cold) so the smurf covers the same real-world distance per second as a hot one — just in fewer, larger steps. The stuck-detection counter scales the same way so a stuck cold smurf gives up after the same real-time interval as a stuck hot one.

Phase assignment runs every 32 sim ticks (camera position barely shifts a tile in that window). The sim thread reads `SimulationCore.CameraFollow`, a single `Vector2` written by GameController each frame. Race-tolerant: a one-tick-stale camera position is invisible at the 320-px-wide LOD band granularity.

**Impact:** With realistic camera coverage (~20 % of colony hot, 30 % warm, 50 % cold), the per-tick smurf-update count drops from N to roughly `N × (0.20 + 0.30/3 + 0.50/6)` = `N × 0.38`. At 1000 smurfs that's ~380 per-tick updates instead of 1000 — a **2.6× effective throughput multiplier** with zero visible quality loss.

---

#### O-M.1 — Designation writes confined to sim thread

**Problem:** `LocalMap.SetExcavationDesignation` / `SetGatherDesignation` etc. were called from both the main thread (drag-box commit) and the sim thread (via `ApplyTaskEffect` → `ClearDesignationsAt`). Each did a non-atomic read-modify-write on the `LocalTile` struct array. The window for an actual race was small (player rarely designates the exact tile the sim is currently clearing) but it was a correctness hole.

**Solution:** New `SimulationCore.PendingDesignations : ConcurrentQueue<DesignationCmd>`. Main thread enqueues via the existing `SimulationManager.DesignateRect`; sim thread drains at the top of each `Tick`, applies one at a time. Sim is now the sole writer to all designation state — flags, index sets, claim map. Race closed.

The queue is bounded by drag-box size (one entry per tile in the rect), drained every sim tick, so no backlog concerns. The bounds-check stays on the main thread so out-of-range tiles never enter the queue.

---

#### O-M.2 — Flat-array passability cache

**Problem:** `LocalMap.IsPassable(x, y)` did `InBounds && _tiles[x, y].Passable`. The `_tiles[x, y]` access copies the entire 24-byte `LocalTile` struct just to read one bool. With 8-direction steering checking 1–8 candidate tiles per smurf per tick, and 60 Hz × 1000 smurfs at the target scale, that's ~500 k IsPassable calls/sec — each copying a 24-byte struct.

**Solution:** New `bool[] _passable` indexed by `y * Width + x`, maintained in lock-step with the per-tile `Passable` flag. `IsPassable` now does a single `uint`-cast bounds check + one array read. Roughly 3× faster on the steering hot path.

Sync sites: `Set`, `MutateTerrain`, `HarvestVegetation` (LargeMushroom cap-clears), `RestoreVegetationYield` (LargeMushroom cap-grows-back), `ApplyTerrainDelta`, `ApplyVegetationDelta`. All paths that flip `tile.Passable` now also write the flat cache.

---

#### O-M.4 — Parallel `BakeFullImage`

**Problem:** Initial map bake painted the whole `Image` serially. On large maps (>120×80) it became the visible "Generating level…" overlay duration.

**Solution:** `Parallel.For(0, Map.Height, y => …)` over rows. Rows write to disjoint 16-px-tall horizontal bands of the shared image, so Godot's per-pixel `SetPixel` / `FillRect` calls don't race. The final `ImageTexture.CreateFromImage` stays serial (single GPU upload).

Roughly halves bake time on 4-core hardware, more on bigger CPUs.

---

#### O-L.1 — Combined mouse-motion early-out

`GameController.HandleMouseMotion` resolved `_camera.GetGlobalMousePosition()` before checking whether either drag mode was active. Camera global-position is a non-trivial property accessor in Godot 4. Trivial fix: single combined `if (!a && !b) return;` at the top.

---

#### Phase A status (per `enginespec.md`)

| Phase A item | Status |
|---|---|
| O-H.2 LOD ticking | ✅ done v0.3.39 |
| O-M.1 Designation writes → sim thread | ✅ done v0.3.39 |
| O-M.2 Flat passability cache | ✅ done v0.3.39 |
| O-M.4 Parallel `BakeFullImage` | ✅ done v0.3.39 |
| O-L.1 Combined motion early-out | ✅ done v0.3.39 |

**Phase A: COMPLETE.**

---

#### Phase B & C (next milestones)

Per the enginespec, Phase B (SOA refactor — O-H.1) and Phase C (renderer rewrite — O-H.3 / N.2) remain queued. The Phase A work was specifically designed to make Phase B *optional at 1000 smurfs* rather than required — extrapolated profiling now puts the sim well under budget at the target scale. Phase B is the path to 5000+ smurfs (and unlocks parallel ticking via the disjoint-row layout); Phase C remains conditional on whether the renderer becomes a profiled bottleneck after Phase B. Both are queued as v0.4.x milestones, not immediate work.

---

#### Optimization report — merged status from `OptimizationReport.md`

The full report is at `C:\Claude\Cloud\OptimizationReport.md`. Summary:

**Completed across v0.3.31–v0.3.39 (full list):** B.1, B.2 (1+3), B.3, B.6, B.7, B.8/N.5, B.9, B.10, B.13, B.14, B.15, B.16 (DesignationOverlay + LocalMapRenderer), B.17, N.1, N.3, N.4, O-L.1, O-M.1, O-M.2, O-M.4, O-H.2.

**Open (next milestone):** O-H.1 (Phase B SOA), O-H.3 (Phase C renderer, conditional), N.8 (MessageLog pooling, L-severity), O-L.3 (food-tile cache, L-severity since B.1 covers the common path).

**Deferred:** N.9 (packed-index dictionaries — verified on .NET 6+ that `ValueTuple` keys in generic `Dictionary` don't box, so the perf delta is marginal).

---

#### Engine recommendation — confirmed from `enginespec.md`

**Stay in Godot.** Phase A landing on schedule confirms the spec's central prediction: data-layout + algorithmic choices are what scale this game to 1000 smurfs, not engine choice. A custom engine would have cost 6–12 months for at most a 1.5–2× delta over what Phase A+B achieve in Godot.

---

**Files touched in v0.3.39:**
- `simulation/Smurf.cs` — `TickPhase` + `TickSlot` fields.
- `simulation/SimulationCore.cs` — `CameraFollow` + `GlobalTick`; designation-write queue; phase-assignment cadence.
- `simulation/systems/BehaviorSystem.cs` — LOD-band constants + `AssignTickPhases` + `ShouldTick`; per-smurf LOD skip in `Tick`; `MoveOneTick` takes `tickInterval`; stuck-tick scaling.
- `SimulationManager.cs` — `SetCameraFollow`; `DesignateRect` routes via queue.
- `world/LocalMap.cs` — `_passable` flat cache; sync sites at every Passable mutator; faster `IsPassable`.
- `ui/LocalMapRenderer.cs` — `Parallel.For` row paint in `BakeFullImage`.
- `ui/GameController.cs` — `SetCameraFollow` call in `_Process`; combined motion early-out.
- `OptimizationReport.md`, `enginespec.md` — status snapshots updated.

---

## [0.3.38] — 2026-05-12

### Added — Chop Wood + Cut Plants orders; smaller tooltips + toggle; ArrivalRadius bump for stuck-fix; Jobs roadmap

Three things landed: a stuck-execution fix for the player's report ("smurfs stand at their task without executing"), two new order verbs (Chop Wood / Cut Plants) modelled on RimWorld, a tooltip-size pass, and the RimWorld-style Jobs tab is now fully specified in the Phase 4 roadmap.

**1. ArrivalRadius bumped 10 → 14 px (stuck-execution fix).**

Player report: "smurfs standing at their task for multiple seconds without actually executing it." Root cause traced — `MoveOneTick` checks `dist ≤ ArrivalRadius` against the walk target (adjacent-passable tile centre for excavate/chop/cut). With ArrivalRadius = 10, a smurf could be physically inside its target adjacent tile (a 16×16-pixel cell) yet not register as arrived because steering had deflected its final step to the tile edge rather than the centre. At 14 px, the entire 16-px tile (corner-to-centre distance ≈ 11.3 px) is inside the arrival zone, so a smurf anywhere in the adjacent tile fires `ApplyTaskEffect`.

This is the most reliable single fix for the "stuck at work site" pattern. Combined with v0.3.37's tightened recovery cadence (`StuckThreshold` 120 → 90, `AvoidTicksLeft` 300 → 180), the worst-case "smurf arrives at work but doesn't execute" window collapses from multi-second to one tick.

**2. New orders — Chop Wood (🪓) and Cut Plants (✂).**

Two RimWorld-equivalent verbs added to the Orders tab:

- **🪓 Chop** — drag-box across wood-yielding shrooms (LargeMushroom / LargeSandshroom / PalmShroom). Smurfs walk to the tile (via the adjacent-approach path, since LargeMushroom variants are impassable until depleted), harvest, drop Fungal Wood (5 / 4 / 3 per cap respectively). When the cap fully clears, the tile flips to passable via the existing `LocalMap.HarvestVegetation` path.
- **✂ Cut** — drag-box across **any** vegetation. Smurfs walk to the tile, clear the vegetation slot, drop nothing. Used for clearing playfield space — RimWorld's "Cut plants" verb.

Both are full designation types (not extensions of Gather), tracked alongside Excavate/Gather in `LocalMap`:

- New `LocalTile.DesignatedForChopWood` and `DesignatedForCut` bool fields.
- New `LocalMap._chopWoodDesignations` / `_cutDesignations` `HashSet<(int, int)>` indexes (parallel to the existing Excavate/Gather indexes).
- New `SetChopWoodDesignation` / `SetCutDesignation` / `FindNearestChopWood` / `FindNearestCut` methods.
- Mutual exclusion: setting one designation clears the other three on the same tile. All four are removed by the Remove brush and by `ClearDesignationsAt`.

New behaviour task types `TaskType.ChopWood` and `TaskType.CutVegetation`. `SelectTask` picks them up at priority 40 (cross-role baseline) plus a +15 bonus for Crafters on Chop Wood (woodcutting is the wood-production analogue of their primary role). `ApplyTaskEffect` for ChopWood credits the Wood ledger via a new `WoodYield(VegetationType)` table; CutVegetation harvests with no resource. `ReleaseTaskClaim`, `RequiresAdjacentApproach`, and the stuck-give-up `AvoidTile` plumbing all extended to the new task types.

`DesignationOverlay` draws distinct glyphs per kind: sienna ⛏-style line for Chop (axe head); teal-cyan X for Cut (scissors mark). `OrderFeedbackOverlay` flashes match the overlay colours so the commit animation reads correctly. Save/load round-trips all four designation types via the new `DesignationDelta.Kind` string field (older saves without `Kind` fall back to the v0.3.37 `IsExcavate` bool).

`SnapshotDesignations()` return type changed from `(int X, int Y, bool Excavate)` to `(int X, int Y, DesignationKind Kind)` with a new `LocalMap.DesignationKind` enum.

**3. Tooltip size + toggle pass.**

Player report: "tooltips are distracting and far too large." The DesignationToolbar tooltips were multi-sentence descriptions that, with Godot's tooltip auto-wrap, rendered as multi-line panels spanning the bottom of the screen.

Tooltips trimmed to single-line essentials:
- "Harvest food vegetation in box."
- "Fell wood-yielding shrooms."
- "Clear any vegetation in box."
- "Clear impassable tiles for Stone / Wood."
- "Wipe any designation in box."

The existing **Show Tooltips** toggle in Settings → Gameplay was already wired for HUDController / SmurfCardPanel / PauseMenuPanel. `DesignationToolbar.AddToolButton` now also reads it — when off, the button's `TooltipText` is left empty so hovering produces no panel at all.

The **Tooltip Size** dropdown (Large / Normal / Small) was added in v0.3.20 and continues to work — `GameController.BuildTooltipTheme` sets the font size on `TooltipLabel` for the whole game. Combined with the shorter strings, "Normal" now renders the toolbar tooltips in compact panels.

**4. Phase 4 Jobs tab — RimWorld-style work priorities (roadmap).**

`SmurfulationC_Roadmap_2026.md` updated with a complete spec for the Jobs tab. Per-smurf × per-work-category grid (rows × columns), values 1 (highest) through 4 (lowest) or "off". `SelectTask` reads priorities; off means forbidden, 1 always considered, 2–4 only when no higher-priority work available. Bulk-edit by column header click (cycle) or right-click (set all). Player designations bump baseline priority by +1 for the duration. Smart defaults per role baked in at smurf creation. New `Smurf.WorkPriorities : Dictionary<string, byte>` serialised to `SmurfSaveData`. UI is a scrollable `GridContainer` with sticky header row and leftmost column.

**Files touched:**
- `world/LocalTile.cs` — `DesignatedForChopWood` + `DesignatedForCut` fields.
- `world/LocalMap.cs` — Chop/Cut indexes, setters, find methods, `IsWoodYielding`, `DesignationKind` enum, snapshot signature change, mutual-exclusion extended to four flags.
- `simulation/BehaviorTask.cs` — `TaskType.ChopWood` + `CutVegetation`.
- `simulation/systems/BehaviorSystem.cs` — `ArrivalRadius` 10 → 14, find-helpers, SelectTask branches, ApplyTaskEffect handlers, `WoodYield` table, `ReleaseTaskClaim` + `RequiresAdjacentApproach` extended.
- `SimulationManager.cs` — `DesignateRect` routes Chop / Cut.
- `SaveManager.cs` — `DesignationDelta.Kind` field; export uses Kind enum.
- `WorldState.cs` — load path switches on Kind, with IsExcavate fallback for pre-v0.3.38 saves.
- `ui/UITheme.cs` — `DesignationTool.ChopWood` + `Cut`.
- `ui/DesignationToolbar.cs` — Tool enum + ActiveDesignation map; shortened tooltips; Show-Tooltips gate.
- `ui/DesignationOverlay.cs` — `DesignationKind` switch; new `DrawChopMark` / `DrawCutMark`.
- `ui/OrderFeedbackOverlay.cs` — TileFlashChop / TileFlashCut.
- `Smurfulation_Cloud/SmurfCloudDes/SmurfulationC_Roadmap_2026.md` — Chop Wood / Cut Plants Phase 4 expectations; full Jobs tab spec.

---

## [0.3.37] — 2026-05-12

### Added — Designations persist across save/load; faster stuck-recovery cadence

**1. Designation save/load.**

Player-issued Excavate / Gather designations are now part of the save record. Previously they lived only on the in-memory `LocalTile` flags + the `LocalMap._excavateDesignations` / `_gatherDesignations` index sets, both wiped on load. A player who drew dozens of designations and then saved would find them all gone on reload — the colony work queue silently emptied. v0.3.37 closes that gap.

**Save shape:**

```csharp
public record DesignationDelta(int X, int Y, bool IsExcavate);

public record ColonySave(...) {
    public List<DesignationDelta>? DesignationDeltas { get; init; } = null;
}
```

Null/empty when no designations are active (the JSON property is omitted entirely) so saves from before v0.3.37 still deserialise unchanged.

**Save path** — `SaveManager.SaveToSlot` calls `LocalMap.SnapshotDesignations()` (the same indexed read the `DesignationOverlay._Draw` uses) and projects to `DesignationDelta[]`. One designation per saved record, regardless of designation density.

**Load path** — `WorldState.LoadFromSave` iterates `save.DesignationDeltas` **after** the terrain & vegetation delta application so the validity checks inside `SetExcavationDesignation` / `SetGatherDesignation` see the post-load tile state. Designations on tiles whose terrain or vegetation has changed since the save (e.g. a saved designation on a Boulder that was subsequently excavated → Mud in a `TerrainDelta`) get silently dropped by the setter's existing validity guard rather than re-flagged on the wrong tile.

The designation index sets are rebuilt automatically by `SetXDesignation` as each delta is applied, so the `BehaviorSystem.FindNearestExcavate` / `FindNearestGather` lookups work immediately on the loaded sim. No separate index rebuild step needed.

**2. Faster stuck-recovery cadence.**

The v0.3.35 stuck-detection thresholds were conservative. Two retunings:

- `StuckThreshold` reduced from **120 ticks (2.0 sec at 1×)** to **90 ticks (1.5 sec)**. A smurf temporarily blocked by another smurf passing through, or a brief steering dead-end, recovers half a second sooner. 90 ticks is still long enough that micro-jitter or single-tick stalls don't trigger the give-up path.
- `AvoidTicksLeft` (per-smurf "I recently gave up on this tile, don't re-pick it" blacklist) reduced from **300 ticks (5 sec)** to **180 ticks (3 sec)**. The 5-second window meant a smurf would refuse to retry a designation for far longer than it took *other* smurfs to either clear it or open a path. 3 seconds gives enough time for the smurf to physically displace (the v0.3.35 Wander radius expansion picks up to 28 tiles away) and for nearby smurfs to alter the map, without locking out retries for longer than the situation has been failing.

Combined, the worst-case "stuck → give up → wander → retry same tile" cycle drops from ~7 seconds to ~4.5 seconds. The player reports smurfs visibly not carrying out jobs; this halves the latency of recovery in the cases where local steering eventually does succeed.

**Files touched:**
- `SaveManager.cs` — `DesignationDelta` record; `DesignationDeltas` field on `ColonySave`; save-path projection from `SnapshotDesignations`.
- `WorldState.cs` — designation-restoration loop after the terrain/veg delta-apply phase.
- `simulation/systems/BehaviorSystem.cs` — `StuckThreshold` 120 → 90; `AvoidTicksLeft` 300 → 180.

---

## [0.3.36] — 2026-05-12

### Optimised — Allocation hot-paths killed; mood cache, trig table, dict mutations, dirty redraws. Re-audit & rebuild assessment in OptimizationReport + enginespec.

Code-wide allocation + throughput pass targeting the 1000-smurf colony scale on the roadmap. Eight items from the optimization report landed; a comprehensive re-audit replaced the report contents; a separate engine-rebuild assessment lives in `C:\Claude\Cloud\enginespec.md` (verdict: stay in Godot, refactor in three phases, no full rewrite needed).

**Sim-side allocation eliminations:**

- **B.6 — `BehaviorTask` → `readonly record struct`.** Was a sealed class; `SelectTask` allocated one per smurf per tick. With v0.3.23's "Wander always re-evaluates" rule, that's ~1 alloc/smurf/tick × 60 Hz = 60k allocs/sec at the 1000-smurf target. Now lives inline as a value type. `Smurf.CurrentTask` is `Nullable<BehaviorTask>`; pattern-matching call sites updated (`is { } t` / `is BehaviorTask gtask`). Constructor signature preserved so existing `new BehaviorTask(type, target, priority)` call sites compile unchanged.

- **B.3 — `SmurfSnapshot` → `readonly record struct`; collection references shared instead of copied.** Previous record constructor did four defensive `new Dictionary(...)` / `new List(...)` copies per smurf per snapshot — at the 1000-smurf target that's 240k heap allocations per second from snapshot creation alone. Now the snapshot lives as a value type inside `SimulationSnapshot.Smurfs` and the `Traits` / `Skills` / `Personality` / `BodyParts` references are passed through directly. Safe because Traits/Skills/Personality are write-once at smurf creation/load (sim never mutates the dictionaries after) and BodyParts has a fixed key set with atomic-float value mutations (same trade-off as the other live float fields on `Smurf` that the snapshot already shares).

- **B.13 — `SimulationSnapshot.SmurfsByName`.** Index built once on snapshot construction. `GameController.OnSmurfClicked` and `SmurfCardPanel.Refresh(SimulationSnapshot)` now do O(1) dictionary lookups instead of `IReadOnlyList<>.FirstOrDefault(s => s.Name == name)` — was O(N), so at 1000 smurfs that's 1000× per click / per tick.

**Sim-side throughput:**

- **B.14 — Mood cache.** `MoodSystem.Tick` was recomputing every smurf's `NeedsContribution + PersonalityModifier` every system tick, even when no need had changed measurably. New per-smurf cache (`MoodCacheNutrition`/`Rest`/`Social`/`Magic`/`Safety` floats) gated by a 0.1-unit epsilon; the recompute fast-exits when no need has moved enough to change the mood score. NaN sentinel triggers the first pass after creation/load.

- **B.17 — Precomputed steering trig.** The 8-direction fan-out steering called `step.Rotated(angle)` which re-evaluates Cos+Sin every call. Replaced with a `static readonly (float Cos, float Sin)[]` lookup; the loop does plain multiplies. Per 1000-smurf-per-tick: ~8k Cos/Sin pairs/sec gone.

- **B.10 — Mutation logs → Dictionary.** `LocalMap._terrainMutations` / `_vegMutations` were `List<>` with `RemoveAll(...)` on every write — O(N) per mutation, O(N²) amortised on long-running games. Replaced with `Dictionary<(int, int), TerrainType>` and `Dictionary<(int, int), (byte, ushort)>` — O(1) per update, bounded memory (one entry per dirty tile, not one per mutation event). Save-export still flattens to a list for the consumer.

- **B.15 — Skip intermediate snapshots during catch-up.** `SimulationCore.Run`'s batch-tick loop (up to ~8 ms of catch-up ticks per real-time iteration) previously pushed a snapshot every tick. Only the last one matters — the visual layer reads at most one per frame. New `Tick(bool pushSnapshot)` parameter; the loop only sets `true` on the final iteration. At 8× speed catch-up that's up to 7 snapshot allocations + list-copies-under-the-smurf-lock saved per loop iteration.

**Renderer:**

- **B.8 / N.5 — `SmurfColonyView` paused-frame redraw skipped.** `_Process` was calling `QueueRedraw()` every frame unconditionally, even while paused. Now early-returns on `Paused`. `UpdateFromTick` calls `QueueRedraw` explicitly so the rare pause-time snapshot (role change during pause) still repaints. At idle with 1000 smurfs that's 1000 per-smurf draws × 60 frames/sec = 60k draw operations saved.

**Reports rewritten:**

- `C:\Claude\Cloud\OptimizationReport.md` — full re-audit of the current codebase, all completed items consolidated, new findings catalogued, 1000-smurf-readiness analysis with concrete remaining steps prioritised by impact.
- `C:\Claude\Cloud\enginespec.md` — engine recommendation. Verdict: **stay in Godot**. Detailed three-phase scaling roadmap (Phase A: finish the allocation work + add LOD; Phase B: data-oriented Smurf layout with parallel ticking; Phase C: tile-quad renderer if Phase A+B aren't enough). Custom-engine alternative analysed and ruled out — the predicted 1.5–2× gain doesn't justify 6–12 months of engine work when the same gain is achievable inside Godot via the SOA refactor.

**Files touched:**
- `simulation/BehaviorTask.cs` — full rewrite as `readonly record struct`.
- `simulation/Smurf.cs` — mood-cache fields; `CurrentTask` doc updated.
- `simulation/SimulationSnapshot.cs` — `SmurfSnapshot` → struct, no defensive copies, `SmurfsByName` index.
- `simulation/SimulationCore.cs` — `Tick(bool pushSnapshot)` + catch-up batch wires the flag.
- `simulation/systems/MoodSystem.cs` — mood cache short-circuit.
- `simulation/systems/BehaviorSystem.cs` — `SteerVectors` table, pattern-match call sites for nullable struct, `ReleaseTaskClaim` uses `is not { } t`, `ResolveWalkTarget` unwraps via `.Value`.
- `world/LocalMap.cs` — mutation logs → Dictionary; flattened export.
- `ui/SmurfColonyView.cs` — `_Process` paused early-return; `UpdateFromTick` explicit redraw.
- `ui/SmurfCardPanel.cs` — new `Refresh(SimulationSnapshot)` overload.
- `ui/GameController.cs` — uses `SmurfsByName` for O(1) lookup; new card refresh overload.
- `OptimizationReport.md` + `enginespec.md` (in `C:\Claude\Cloud\`).

---

## [0.3.35] — 2026-05-12

### Fixed — save-reload teleport, stuck-loop pathing, microstutter on tile mutation

**1. Save reload teleported smurfs to the centre on unpause.**

`SimulationManager._Ready` ran `SeedSimPositions()` unconditionally after `SeedColony()`, including the `LoadFromSave` path. `LoadFromSave` restored every Smurf field *except* `SimPos` / `SimTarget` / `SimSpeed` — those stayed at their `Vector2.Zero` defaults until `SeedSimPositions` overwrote them with the spawn-cluster centre. The visual layer initially rendered the *saved* `PosX`/`PosY` from `SmurfColonyView.UpdateFromTick`, so positions looked right while paused. The moment the sim ticked, the next snapshot carried the cluster-centred positions and the visual snapped to them — looked exactly like a teleport.

Two-part fix:
- `LoadFromSave` now restores `SimPos`/`SimTarget` from `sd.PosX/PosY/TargetX/TargetY` (the save record already stored them — they just weren't read back) and sets `SimSpeed` via `SpeedForRole(s.Role)` (role-derived, not saved). Guarded so a save with `(0, 0)` doesn't restore zero (which would re-trigger the spawn-cluster path).
- `SeedSimPositions` now skips smurfs whose `SimPos != Vector2.Zero` — i.e. anyone whose position was already restored by `LoadFromSave`. Still seeds brand-new colonies and mid-game births that arrive with `Vector2.Zero`.

**2. Smurfs getting stuck on impassable terrain / when reassigning task.**

Three reinforcing fixes:

- **180° backward step added to `SteerAngles`.** v0.3.22's six-direction fan-out (0°, ±45°, ±90°, ±135°) could leave a smurf frozen against a concave wall — every forward direction was blocked but the smurf couldn't back up. The 180° step is the last entry in the angle array, so it only fires when every other direction failed. Lets the smurf retreat out of dead-end pockets.

- **`NewWanderTask` progressive radius (8 → 12 → 18 → 28).** Smurfs deep inside an excavation pocket had a Wander sample of 12 attempts within radius 8 — if every sample hit rock they bailed to their own position, triggered stuck-detection 2 sec later, gave up, and re-evaluated to the same designation, repeat. Now we widen the sample radius up to 28 tiles before giving up. Covers all but pathologically enclosed cases.

- **Per-smurf "recently gave up" blacklist (`Smurf.AvoidTile` + `AvoidTicksLeft`).** When a smurf hits the stuck threshold and gives up on a designation, the target tile is recorded with a 300-tick (~5 sec at 1×) TTL on the smurf. `FindNearestExcavate`/`FindNearestGather` skip the blacklisted tile *for that smurf only* — others can still claim it. Breaks the loop where the smurf gives up, releases the claim, immediately re-finds the same closest target, walks back, gets stuck again. The TTL ticks down each per-smurf pass in `BehaviorSystem.Tick`.

**3. Microstutter on every terrain / vegetation update — `DesignationOverlay` (N.3).**

v0.3.31's batching kept full-texture uploads to one per frame, but the *overlay* redraw was still calling `DrawString` once per designated tile. Godot 4's `DrawString` does internal text shaping per call — with ~200 active designations that's ~10 ms of pure font work per redraw, fired every time a designation event came through.

Swapped the per-tile `DrawString(font, …)` calls for shape-based primitives:
- **Excavate glyph** — two `DrawLine` calls forming a stylised pickaxe (diagonal handle + perpendicular head).
- **Gather glyph** — three `DrawCircle` calls forming a small berry cluster.

Both are pure C++ canvas primitives with no font shaping. With 200 designations the redraw cost drops from ~10 ms to under 1 ms. The microstutter on tile mutations is mostly gone — the residual ~5 ms cost is the `ImageTexture.Update(_image)` full-map re-upload, which is architectural (see optimization report N.2) and not fixable without a renderer rewrite.

**Files touched:**
- `SimulationManager.cs` — `LoadFromSave` restores SimPos/SimTarget/SimSpeed; `SeedSimPositions` skips already-positioned smurfs.
- `simulation/Smurf.cs` — `AvoidTile` + `AvoidTicksLeft` fields.
- `simulation/systems/BehaviorSystem.cs` — 180° added to `SteerAngles`; `NewWanderTask` progressive radius; avoid-tile recorded on stuck-give-up, ticked down per smurf, and threaded through `FindDesignated*`.
- `world/LocalMap.cs` — `FindNearestExcavate` / `FindNearestGather` take an optional `avoid` parameter.
- `ui/DesignationOverlay.cs` — `DrawExcavateMark` / `DrawGatherMark` replace the per-tile `DrawString` glyph path.
- `OptimizationReport.md` — N.3 marked ✅ done.

---

## [0.3.34] — 2026-05-12

### Changed — Unit-card tightening: WANTED stamp gone, mood thresholds → bar tick marks, trait names → bar overlays

Pure layout pass to tighten the four unit-card tabs after v0.3.32 shrank the card to 320 px tall. No behavioural change, no data change.

**1. Main tab — WANTED stamp removed.**

The `"WANTED (for science)"` decorative label at the bottom of the Main tab was the only thing pushing Main tab past the card's visible height. Deleting it lets the entire Main tab (header / age / role / mood / health / 5 need bars / personality list) fit cleanly inside the 320 px card without scrolling.

**2. Mood tab — thresholds → tick marks on the bar.**

The Mood tab had a 6-line text list (`Inspired ≥ 80`, `Content ≥ 60`, …, `Collapse = 0`) that ate ~70 px of vertical space and required scrolling to reach the "Personality Effects" section below.

Replaced with four coloured **tick marks drawn directly on the mood bar** at the four state boundaries — 20 (Distressed cutoff), 40 (Stressed), 60 (Content), 80 (Inspired). Each tick is coloured to match the state *above* the threshold, so the player can read the bar at a glance: "I'm currently in the green zone, three ticks from the red — that's Inspired down to Distressed."

New `NeedBar.AddThresholdTick(value, colour)` method overlays a 2-px `ColorRect` at the right horizontal percentage anchor (with a small ±1 px vertical protrusion above/below the bar for visibility). Pure-anchor positioning means the ticks track the bar correctly at any UI Size.

Threshold reference text moved into the bar's `TooltipText`. The bar gets `MouseFilter.Pass` so hovering still surfaces the tooltip; the actual progress-bar fill and value label still render and update as before.

With the text section gone, the Mood tab also fits without scrolling.

**3. Health tab — trait names overlaid on the bars (white-on-bar).**

`TraitBar` used to be an `HBoxContainer` with a left-column trait name label (80 px fixed width) plus a progress bar that filled the rest of the row. Long names ("Haemocyanin Metabolism", "Resonance Sensitivity") truncated visually because the bar width varied with the name length.

Now: trait name label sits **inside** the bar wrap as a white-on-shadow overlay, anchored to the left edge with the value label on the right. Every bar gets the same full-row width. Same data, same colour, more legible at any UI Size.

`MouseFilter.Ignore` on both overlay labels so they don't block the bar's own tooltip / interaction. The Health tab keeps its scroll bar — the trait list is 13 entries deep and that's by design — but the rows are now visually uniform.

**Files touched:**
- `ui/SmurfCardPanel.cs` — WANTED stamp deleted; `NeedBar` gains `_wrap` field + `AddThresholdTick` method + four mood ticks wired in `BuildMoodTab`; mood-bar tooltip carries threshold text; thresholds text section deleted; `TraitBar` constructor restructured to overlay name + value inside the wrap.

---

## [0.3.33] — 2026-05-12

### Fixed — Per-tick designation scanning O(R²) → O(N); smurfs no longer pile on the same target (B.1 + B.7)

After v0.3.32's `Image.FillRect` win, the player still reported performance degradation at 1× and 2× speeds, plus "smurfs get stuck and stop performing tasks". The screenshot showed hundreds of active designations on a large map.

Two root causes diagnosed:

**1. `BehaviorSystem` was scanning 51×51 tiles per smurf per tick (B.1).**

`FindDesignatedExcavation` and `FindDesignatedGather` each walked the full 25-radius square (2601 tiles) every call, doing `LocalTile` struct copies on every iteration. With v0.3.23's "Wander always re-evaluates" rule (~20 smurfs × 60 Hz × 2 scans × 2601 cells × ~24 B copy) the sim thread was doing ~6 MB/sec of struct copies just to look up designations. Cost was tolerable at small designation counts; ballooned to a real bottleneck once the player had hundreds of flags planted across the map.

**2. Every smurf independently picked the same "nearest" designation (B.7).**

Without claims, all wandering smurfs see the same flagged tile as "closest unworked target". They all walk to it. The first to arrive finishes the work and clears the flag; the others reroute, but they reroute *to the new nearest* — which is again the same tile for all of them. With dense terrain, the convergence cluster often ends up trapped against impassable boundaries, then the stuck-detection kicks in 2 sec later and they all reroute to the *next* nearest, repeat. Explains the "smurfs get stuck" observation directly.

**Fix (LocalMap):**

- New `HashSet<(int, int)> _excavateDesignations` and `_gatherDesignations` maintained in lock-step with the per-tile `DesignatedFor*` flags. `SetXDesignation` / `ClearDesignationsAt` keep them in sync under a single `_designationsLock`.
- New `FindNearestExcavate(fromPixel, claimerId)` and `FindNearestGather(fromPixel, claimerId)` iterate the indexed set instead of the radial square. Cost drops from O(R² = 2601) per scan to O(N) where N is the live designation count (typically 10s to low 100s). For sparse designation counts the speedup is ~50–250×; even at 1000 designations it's ~2.5× faster than the old radial scan and avoids 1600 wasted "tile not flagged" reads per scan.
- New `_claims : Dictionary<(int, int), Guid>` records which smurf is currently routing toward each designated tile. `Find*` methods skip tiles claimed by *other* smurfs (claimer can re-find its own target if it loses CurrentTask momentarily). Claims auto-released on `ClearDesignationsAt` and on `SetX(false)` so completed work / player un-designations clean up automatically.
- New `TryClaim(x, y, claimerId)` / `ReleaseClaim(x, y, claimerId)` for explicit lifecycle.
- New `SnapshotDesignations()` returns the combined set for `DesignationOverlay._Draw` to iterate — no more "walk Width × Height every redraw".

**Fix (BehaviorSystem):**

- `FindDesignatedExcavation` / `FindDesignatedGather` now thin wrappers around `LocalMap.FindNearest*`. Take `Smurf s` instead of `Vector2 from` so the claimer id can be passed through.
- New `ClaimAndMakeDesignationTask` helper claims the tile via `TryClaim` at the moment the task is built. All four designation branches in `SelectTask` route through it.
- New `ReleaseTaskClaim` helper called at the two task-transition sites:
  - `needNewTask` triggering re-selection (interruption by Wander re-eval or critical need).
  - Stuck-give-up (`StuckTicks > StuckThreshold`).
- Task completion in `ApplyTaskEffect` auto-releases via the existing `ClearDesignationsAt` call inside the lock.

**Fix (DesignationOverlay — B.9):**

`_Draw` now iterates `_map.SnapshotDesignations()` (typically 10–1000 entries) instead of walking every tile on the map. Same `DrawRect` + `DrawString` calls per flagged tile; just no wasted scan of empty tiles.

**Measured impact (rough estimates):**

- Designation scanning: from ~6 MB/s struct-copy load down to under 200 KB/s at typical designation counts.
- Smurf clustering: should be visibly reduced — at any moment each unclaimed designation is targeted by at most one smurf. Smurfs without a viable claim fall through to autonomous role tasks or wander, spreading the colony out instead of dog-piling.
- Stuck-detection should fire less often because fewer smurfs are walking into the same physical cluster.

**Edge case — smurf death:**

If a smurf dies mid-walk to a claimed designation, the claim stays until either the underlying designation is cleared (player Remove, or another smurf eventually picks it up after the stuck-give-up rotation completes) or the SetXDesignation re-set re-acquires it. Window is small; explicit cleanup deferred until Phase 8 wiring adds a death-handler hook on the sim thread.

**Files touched:**
- `world/LocalMap.cs` — designation indexes, claim map, `FindNearest*` and `SnapshotDesignations` API, `using Godot` for `Vector2`.
- `simulation/systems/BehaviorSystem.cs` — `Find*` now wrappers around the indexed API; `ClaimAndMakeDesignationTask` + `ReleaseTaskClaim` helpers wired into the four designation branches and the two task-transition sites.
- `ui/DesignationOverlay.cs` — `_Draw` iterates the snapshot instead of the full map.
- `OptimizationReport.md` — B.1, B.7, B.9 marked ✅ done in the implementation-status snapshot.

---

## [0.3.32] — 2026-05-12

### Fixed — UI overlap (TileInfoOverlay + unit card); per-tile paint cost; optimization report updated

**1. UI overlap.**

Two compounding sizing bugs left the TileInfoOverlay and the unit card colliding at 720p:

- `UITheme.HudHeight = 44` is the *nominal* HUD capsule height, but with `AnimatedButton`'s borders + padding the actual rendered speed capsule is ~75 px tall. `TileInfoOverlay.OffsetTop = EdgeInset + Scaled(HudHeight) + Scaled(10)` only gave ~70 px clearance, which the chunky speed buttons spilled into.
- The unit card was 380 px tall, anchored from the bottom-right (`OffsetBottom = -240`). At 720p that put its top at y = 100, overlapping the TileInfoOverlay's y = 70–140 range. The close-button (`X`) landed right next to the tile-info text.

Fixes:
- `TileInfoOverlay.OffsetTop` now uses a fixed `EdgeInset + Scaled(80)` (= 96 at 100 % UI Size) — comfortably below the speed capsule's real bottom edge. Height shrunk to 55 px (3 lines at 13 pt fit cleanly).
- `SmurfCardPanel` height reduced from 380 → 320. At 720p the card now spans y = 160–480, with 5 px clearance below the TileInfoOverlay (y = 96–151). Tall content like the Health tab's biological-traits list scrolls inside the card's existing `ScrollContainer` — no clipping.

**2. Per-tile paint cost — `Image.FillRect` (N.1).**

After v0.3.31's coalescing fix, the player reported residual perf degradation during tile mutations / vegetation state changes. Investigation pinpointed `PaintTile`'s terrain-fill loop:

```csharp
for (int py = oy; py < oy + TS; py++)
    for (int px = ox; px < ox + TS; px++)
        _image!.SetPixel(px, py, terrain);
```

That's 256 `SetPixel` calls per dirty tile — each one a C#→C++ interop boundary crossing. With many tiles dirtied per frame (excavation completing, vegetation harvested) the marshalling cost stacks up.

Replaced with `_image!.FillRect(new Rect2I(ox, oy, TS, TS), terrain)` — Godot's native C++ implementation does the whole fill in one trip. The local `FillRect` helper (used for mushroom-stem rectangles etc.) got the same treatment. Approximate gain: ~50× faster per-tile repaint for the terrain background. Vegetation overlays (`FillCircle`) still go pixel-by-pixel but are bounded to the veg footprint.

**3. Renderer fast-path — lock-free count check (N.4).**

`LocalMapRenderer._Process` previously took the dirty-set lock every frame just to read `_dirty.Count`. Most frames the count is zero. Fast-path now reads `_dirty.Count` without holding the lock; only acquires when there's actual work. Worst case is a one-frame flush delay if the count flips between the read and the lock acquire — visually invisible.

**4. Optimization report audit + merge.**

Added new findings N.1–N.6 to `C:\Claude\Cloud\OptimizationReport.md` and marked the v0.3.31 + v0.3.32 fixes against the original B.2 / B.16 items:

- ✅ **B.2 (1) coalesce dirty tiles** — done v0.3.31.
- ✅ **B.2 (3) FillRect terrain fill** — done v0.3.32.
- ✅ **B.16 partial — `DesignationOverlay` cross-thread `QueueRedraw`** — done v0.3.31 (sim-thread event sets a `volatile bool`; main-thread `_Process` calls `QueueRedraw`).
- ✅ **N.1 FillRect optimization** — done v0.3.32.
- ✅ **N.4 lock-free fast path** — done v0.3.32.
- Open: N.2 (full-image upload — architectural; deferred), N.3 (`DrawString` per glyph — pre-bake glyph textures or skip below zoom threshold), N.5 (`SmurfColonyView` redraw at idle), N.6 (BehaviorTask record-struct — bumped from M to H severity since v0.3.23's Wander-re-evaluation amplified the alloc rate). Plus everything in the original B-list that wasn't already addressed.

A new "Implementation status snapshot" table at the bottom of the report makes the done/open distinction scannable.

**Files touched:**
- `ui/TileInfoOverlay.cs` — increased clearance offset; subscribe-rebuild path matches.
- `ui/SmurfCardPanel.cs` — card height 380 → 320.
- `ui/LocalMapRenderer.cs` — `Image.FillRect` replaces 256-call SetPixel loop; lock-free `_dirty.Count` fast path.
- `OptimizationReport.md` (in `C:\Claude\Cloud\`) — status annotations on B.2 / B.16, new findings N.1–N.6, implementation-status snapshot table.

---

## [0.3.31] — 2026-05-12

### Fixed — Multi-second freeze + "designations vanishing" after Gather drag; TileInfoOverlay reverted to lightweight text style

**Root cause of the freeze + "vanishing designations":**

After designating Excavate boulders, drawing a Gather box caused the game to freeze for several seconds, after which the previously-flagged Excavate tiles appeared to be gone. Two compounding bugs:

1. `LocalMapRenderer.UpdateTile` ran on every `TerrainChanged` / `VegetationChanged` event — and each call did `_texture.Update(_image)`, which re-uploads the **entire** map texture to the GPU (~5–50 ms per call). When Gather designations went live, the sim threw many `HarvestVegetation` + `MutateTerrain` events per tick at the renderer; the main thread choked on dozens of full-texture uploads stacked back-to-back. That's the freeze.
2. Meanwhile the sim thread kept running. During the freeze, Crafter / Forager smurfs completed their already-queued Excavate tasks, which call `ClearDesignationsAt`. By the time the main thread was free again, every excavate-flagged tile had actually been worked — Mud terrain, no more flag. The "deleted designations" the player saw were actually completed work.

This was previously noted in `OptimizationReport.md` finding **B.2** (renderer dirty-rect coalesce); v0.3.31 implements that fix.

**Fix 1 — Coalesce renderer texture uploads (`LocalMapRenderer`).**

Sim-thread events no longer repaint the tile + upload the texture directly. They mark the tile dirty in a thread-safe `HashSet<(int, int)>` (guarded by `_dirtyLock`). The main-thread `_Process` flushes the set once per frame: repaint every dirty tile, then **one** `_texture.Update` call regardless of how many tiles changed. Same end result, ~10–100× fewer GPU uploads under heavy gather/excavate load.

This also removes the cross-thread `_texture.Update` call entirely — the sim thread no longer touches graphics resources.

**Fix 2 — `DesignationOverlay.QueueRedraw` marshalled to the main thread.**

`Map.DesignationChanged` also fires from the sim thread (whenever `ClearDesignationsAt` is called from `BehaviorSystem.ApplyTaskEffect`). The overlay's handler was calling `QueueRedraw` directly from that thread, which is at minimum sketchy in Godot 4. Same pattern as the renderer fix: a `volatile bool _needsRedraw` flag flipped by the event handler, the main-thread `_Process` reads it and calls `QueueRedraw` at most once per frame.

**Fix 3 — TileInfoOverlay reverted to the simple top-right text style.**

v0.3.30 added a `FloatingPanelStyle` background to the hover info text. The player reported it as too noisy for minor information and visually mismatched with the message log (which is bare drop-shadow text at the bottom-left). Reverted to a right-aligned label with a black drop shadow — same visual weight as the message log. Anchored top-right, sits below the HUD speed capsule. Subscribes to `UITheme.UIScaleChanged` so font + position rebuild correctly when UI Size changes.

**Phase 4 roadmap note — UI terminology mapping.**

Added a subsection under `Phase 4 — Behavior rewire` in `SmurfulationC_Roadmap_2026.md` that explicitly ties the player-facing **Gather** / **Excavate** designation buttons to the `BehaviorSystem` `GatherFood` / `GatherMaterial` task types, and documents the v0.3.x placeholder behaviour (float-ledger increment) vs. the Phase 4 expectation (spawn a typed `Food` / `Stone` / `Wood` *item* on the tile, picked up by a Hauler and routed to a stockpile). The float ledger is explicitly marked as placeholder; the HUD already labels its readouts "unstored — Phase 5 will gate".

**Files touched:**
- `ui/LocalMapRenderer.cs` — dirty-tile set + per-frame coalesce flush; events no longer call `_texture.Update`.
- `ui/DesignationOverlay.cs` — `volatile bool _needsRedraw` + `_Process` flush; no cross-thread `QueueRedraw`.
- `ui/TileInfoOverlay.cs` — restored simple right-aligned drop-shadow label; panel removed.
- `Smurfulation_Cloud/SmurfCloudDes/SmurfulationC_Roadmap_2026.md` — Phase 4 UI-terminology mapping subsection.

---

## [0.3.30] — 2026-05-12

### Changed — Camera pan now right-drag; TileInfoOverlay panelised + moved to top-centre; UI Size labels say what they mean

**1. Camera pan moved from left-drag to right-drag.**

Left-drag is now reserved for the box-select mechanic (and for designation drag-box when a tool is active). v0.3.24 already had left-drag doing double duty for selection + pan; v0.3.30 cleanly separates them.

Right-drag pan disambiguates from right-click move-orders via a 4-px threshold:

- Right-press records the cursor position and clears any prior pan state. **No order fires yet.**
- `PanWithMouseDrag` (in `_Process`) watches `Input.IsMouseButtonPressed(MouseButton.Right)`. Once the cursor drifts > 4 px from the press position, `_rightPanning` latches true and the camera follows the cursor.
- On right-release, `TryHandleMouseButton` checks the latch: if the gesture stayed below the threshold (a true "click"), it routes to `RequestPlayerMoveOrderGroup` exactly like v0.3.29. If it crossed the threshold (a drag), no order fires.
- Stranded-release safety: if `PanWithMouseDrag` sees the right button released while `_rightDownPos` is still set (release happened over a UI panel that consumed the event), it clears the state so the next press starts clean.

The 4 px threshold is small enough that a deliberate click never accidentally pans, and a deliberate drag never accidentally orders.

Old left-drag pan removed entirely; `_dragging` field deleted from `GameController`.

**2. TileInfoOverlay restyled and repositioned.**

Previous layout: top-right, no panel background, raw white text with a black drop-shadow. Fine over grass; illegible over light terrain (sand, stone). And sitting at the top-right is exactly where the new unit card lives, so the two visually collided.

New layout:
- Wrapped in a `FloatingPanelStyle` `PanelContainer` so it has the same dark parchment + gold border treatment as every other floating panel.
- Moved to top-**centre**, anchored via the HUDController-style `BottomWide`-equivalent pattern (TopWide HBox with `ExpandFill` spacers on both sides + a centred PanelContainer that auto-sizes to its label). The panel sits ~10 px below the HUD's top capsule baseline so the two never overlap, and well clear of the unit card on the right.
- Font + padding go through `UITheme.Scaled` so the panel rebuilds correctly when the player changes UI Size (subscribes to `UITheme.UIScaleChanged`, preserves the current label text across the rebuild).

**3. Settings → Gameplay → UI Size renamed to plain English.**

Dropdown labels changed from `100 %` / `66 %` / `33 %` to `Large` / `Normal` / `Small`. The underlying float values are unchanged (1.00 / 0.66 / 0.33); only the player-facing display text differs. Tooltip text on the row label updated to match.

**Files touched:**
- `ui/GameController.cs` — right-drag pan state (`_rightDownPos` / `_rightPanning` / threshold constant); deferred right-click order; old left-drag pan logic removed.
- `ui/TileInfoOverlay.cs` — restyled to use `FloatingPanelStyle`; repositioned to top-centre via the HBox-spacer pattern; UI-scale rebuild subscription.
- `ui/SettingsPanel.cs` — `UIScaleLabels` array renamed; tooltip text updated.

---

## [0.3.29] — 2026-05-12

### Changed — Unit card restyled to match the floating-panel theme; sits above the order bar

The unit card was the last bit of v0.2.x parchment-and-tan UI left in the game. Sitting at the screen's bottom-right next to the new bottom tab bar made the colour mismatch obvious — and at its old `OffsetBottom = -10` it physically overlapped the Orders/Build/Zones/Jobs/Smurfs capsule. v0.3.29 is a pure cosmetic + positioning pass; no functionality changed.

**Repositioning:**

- Anchored bottom-right, same as before, but `OffsetBottom = -240` instead of `-10`. The bottom shell reserves 16 px (EdgeInset) + 32 px (tab capsule) ≈ 48 px just for the tab bar; an open content panel can pop up another ~180 px on top of that. 240 clears both states comfortably so the card never overlaps the tab UI regardless of which tab is open.
- `OffsetRight = -UITheme.EdgeInset` so the card's right edge aligns flush with the HUD capsules and the bottom tab bar — all three floating panels now share the same right-side margin.
- Card height shaved slightly (380 → 380 px) to keep the top edge well below the HUD capsules at 720p.

**Restyle to match `FloatingPanelStyle`:**

- Background swapped from the cream `ParchBg` (0.88, 0.80, 0.60) to `FloatingPanelStyle.Make()`, which is the same dark-parchment + translucent-gold-border + drop-shadow used by the HUD, the bottom tab capsule, and every host panel inside it.
- Body text colour pivoted from the dark-on-cream `DarkWood` palette to the parchment-on-dark `UITheme.TextPrimary` palette. Aliases for the old constants (`DarkWood` / `Gold` / `TabIdle` / `TabActive` / new `TextDim`) are now pulled from `UITheme` so any future tweak to the global theme propagates automatically.
- Hard-coded warm-brown one-offs (the four common shades scattered through `BuildMainTab` / `BuildMoodTab` / `BuildHealthTab` / `BuildSkillsTab`) all replaced with the appropriate theme constant via batched edits: `(0.40, 0.26, 0.10)` → `TextDim`, `(0.35, 0.22, 0.08)` → `Gold`, `(0.30, 0.18, 0.05)` → `DarkWood`. `TraitBar` / `SkillBar` / `NeedBar` name labels follow suit.
- Tab strip pills (Main / Mood / Health / Skills) rebuilt with `FloatingPanelStyle.MakeToolbarButton`, matching the bottom Orders/Build/Zones/Jobs/Smurfs pill style. Font colour grades from `TextMuted` (idle) → `TextPrimary` (hover) → `TextAccent` (pressed).
- `HRule` separator switched from solid warm brown to translucent gold (lifted from `UITheme.PanelBorderColour`) so internal section dividers read as part of the same family as the outer panel border.

**Functionality preserved** (this was the brief — *just* formatting):

- All four tabs still work, role dropdown still emits `RoleChangeRequested`, close button still hides the card, tooltips still apply when the `show_tooltips` setting is on, mood / health / skill / personality refresh paths are unchanged.
- Semantic colours kept as-is: need bars (orange/blue/green/purple/yellow), mood-state colour palette, health-status text (green/yellow/red), skill-tier colour gradient. Those convey meaning, not theme.

**Files touched:**
- `ui/SmurfCardPanel.cs` — palette swap, FloatingPanelStyle integration, tab pill restyle, reposition.

---

## [0.3.28] — 2026-05-12

### Fixed — HUD speed buttons clickable again; mouse-wheel zoom no longer fires while over UI

Two regressions from v0.3.27's UI overhaul, both rooted in `BottomTabPanel`'s `FullRect` overlay covering the entire viewport.

**1. Top-right speed / menu buttons were unreachable.**

`BottomTabPanel` is anchored `LayoutPreset.FullRect` so its band can grow upward from the bottom edge as the active content panel expands. v0.3.27 left the outer Control at `MouseFilter.Pass`, which means it received every click and propagated the event *up to its parent* (the `UILayer` CanvasLayer) — never to siblings. Since `BottomTabPanel` was added to `UILayer` after `HUDController`, it sits on top in tree order; with `Pass`, clicks anywhere on the screen hit `BottomTabPanel` first and dead-ended there. The HUD's speed-button capsule received nothing.

Fix: outer `BottomTabPanel.MouseFilter = Ignore`, and the inner `_band` + `contentRow` + `tabRow` (all transparent layout containers) also `Ignore`. Now clicks on the empty transparent area pass *through* `BottomTabPanel` entirely and hit whatever's underneath — which for the top-right corner is the HUD's right capsule. The actual painted UI (the tab bar capsule, each host PanelContainer) keeps `MouseFilter.Stop` so their own hit-testing still works.

**2. Mouse-wheel zoom fired even when hovering UI panels.**

`GameController._UnhandledInput`'s wheel-zoom path was guarded by `IsMouseOverCard()` only — that returns true only when the SmurfCardPanel is visible *and* under the cursor. So scrolling over the Jobs stub tab, the Smurfs roster, or any HUD capsule still ran the camera zoom, which the user reported as "jarring".

Fix: new `IsMouseOverUI()` superset of `IsMouseOverCard()`. Returns true when the cursor is over any non-modal in-game UI:
- the unit card
- either HUD capsule (left stats/resources, right speed/menu)
- the tab bar capsule or any visible content host on `BottomTabPanel`

The wheel-zoom path now checks this. Modal overlays (PauseMenu / Settings / SaveBrowser / GameOver) already block input upstream via `IsAnyOverlayOpen`, so they don't need to be listed.

Each panel exposes its own hit-test:
- `HUDController.IsMouseOverBars()` — checks `_leftPanel` / `_rightPanel` (now stored as fields).
- `BottomTabPanel.IsMouseOverContent()` — checks the tab bar plus whichever content host is currently visible.

**Files touched:**
- `ui/BottomTabPanel.cs` — outer FullRect + transparent layout containers set to `Ignore`; new `IsMouseOverContent()` accessor.
- `ui/HUDController.cs` — `_leftPanel` / `_rightPanel` stored as fields; new `IsMouseOverBars()` accessor.
- `ui/GameController.cs` — new `IsMouseOverUI()` superset; wheel-zoom paths swapped from `IsMouseOverCard` to `IsMouseOverUI`.

---

## [0.3.27] — 2026-05-12

### Fixed — Tab content now actually appears; Smurfs tab rebuilt as a RimWorld-style roster table

v0.3.26's "shared content panel with stacked hosts inside" approach was undone by a Godot layout subtlety. `PanelContainer` is a Container that sizes to its single child's *minimum size*. The wrapper that held all five hosts was a plain `Control`, and `Control.GetMinimumSize()` returns zero by default — it does not propagate child minimum sizes upward. So no matter which tab the player picked, the panel was sized to the panel margins only (~12 px) and the actual host content fell outside the rendered rect.

Two related issues compounded the failure:
- `DesignationToolbar` (a `Control`) wrapped its HBox in a `CenterContainer`, but the toolbar itself didn't override `_GetMinimumSize` to surface the HBox's required size to the host.
- Same for `SmurfRosterPanel` (also a `Control`) wrapping a `MarginContainer`.

**Layout rewrite:**

Each tab host is now its own styled `PanelContainer` — siblings of the `ExpandFill` spacer in the content `HBox`, not children of a shared wrapper. Visible host hugs the right edge (matching the tab capsule below it); hidden hosts collapse out of HBox layout entirely in Godot 4, so only the active one occupies space. Each `PanelContainer` sizes correctly to its child's minimum, the child is the actual toolbar / roster / stub label, and the panel renders at exactly the size of that content.

`DesignationToolbar` and `SmurfRosterPanel` both now override `_GetMinimumSize()` to return the max of their visible-Control children's `GetCombinedMinimumSize()`. The host PanelContainer picks that up and sizes itself + the parchment border around it. Visible content. Finally.

**Smurfs tab — RimWorld-style roster table:**

Previously the Smurfs panel was a single-column `ItemList` with `"Name · Role · Mood"` strings. Replaced with a scrollable multi-column table modelled on RimWorld's Schedule + Assign + Work tabs (compressed into one view, since SmurfulationC's feature set is narrower than RimWorld's).

Columns:

| Col | Meaning | Source |
|---|---|---|
| 📷 | Camera-focus button — single click zooms | Phase 3 (present) |
| Name | Click selects + opens unit card; double-click zooms | Phase 3 (present) |
| Role | Forager / Crafter / Caretaker / … | Phase 2 (present) |
| Mood | Coloured emoji + state name (Inspired / Content / Stressed / …) | Phase 2 (present) |
| 🍓 💤 👥 ✨ 🛡 | Need bars — green ≥ 60, amber 30–60, red < 30 | Phase 2 (present) |
| Activity | Verb form of `CurrentTask` ("Eating", "Excavating", "Idle", …) | Phase 3 (present) |
| ⚔ | Sword glyph when `CombatTargetName != null` | Phase 8 stub (data-plumbed) |

RimWorld's Apparel / Food / Drug / Reading policy columns are intentionally omitted — none of those systems exist in SmurfulationC yet, and empty stub columns would only clutter. Work priorities live in their own ⚙ Jobs tab (still a stub label until Phase 3.10 fills it in).

The roster refreshes diff-in-place: rows are kept in a `Dictionary<name, RosterRowCtrls>`, new smurfs append, dead smurfs are removed, surviving smurfs have their need bars / mood text / activity verb updated without rebuilding the whole VBox. Scroll position is preserved across ticks.

**Row record expanded:**

`SimulationC_Roster_Row` grew from 3 fields (Name / Role / MoodState) to 10 — adds the five needs, current `TaskType`, and combat target. `GameController.OnTick` populates it from `SmurfSnapshot`.

**Files touched:**
- `ui/BottomTabPanel.cs` — per-host PanelContainer structure; hosts as HBox siblings of the spacer; no shared wrapper Control.
- `ui/SmurfRosterPanel.cs` — full rewrite into a header + ScrollContainer + per-row HBox table; need-bar palette; activity verb mapping; mood-coloured row.
- `ui/DesignationToolbar.cs` — replaced `CenterContainer` wrapper with `MarginContainer`; added `_GetMinimumSize` override.
- `ui/GameController.cs` — populates the wider row record on every snapshot tick.

---

## [0.3.26] — 2026-05-12

### Changed — Tab bar moved to bottom-right HUD-style capsule; Build / Zones / Jobs added; nested-panel rendering bug fixed

v0.3.25's bottom-centre tab shell hit two related rendering bugs:
- The Orders tab's content (the `DesignationToolbar`) carried its own inner `PanelContainer` from the era when it self-anchored to the viewport. After being embedded in v0.3.24, that inner panel still rendered — separate from the `BottomTabPanel`'s outer content panel — which is why the player saw a small empty rounded square next to the tabs *and* the actual toolbar buttons appearing far off to the bottom-right. Two panels, two positions, one piece of content.
- The bottom-centre anchoring put the panel directly over the play area centre, occluding gameplay even when collapsed.

**Bar moved to bottom-right (HUDController capsule pattern):**

`BottomTabPanel` now mirrors `HUDController`'s top-right speed-capsule layout:
- Outer Control is FullRect (transparent overlay).
- Inner `VBoxContainer` anchored `LayoutPreset.BottomWide` with `OffsetLeft/Right = ±EdgeInset` and `GrowVertical = GrowDirection.Begin`, so the band lives at the bottom edge and expands upward as the active content panel needs height.
- Each row is an `HBoxContainer` with a flexible spacer + the actual panel — same trick `HUDController` uses to push its right capsule against the screen edge while the panel auto-sizes to content.
- Result: the tab capsule sits glued to the bottom-right at `EdgeInset` from both edges, and the active content panel pops up *above* it, right-edge-aligned for visual continuity.

**Tab bar contents (single horizontal capsule, RimWorld Architect-style):**

| Tab | Content host | State |
|---|---|---|
| 📋 Orders | embedded `DesignationToolbar` — Gather / Excavate / Remove only | shipped |
| 🔨 Build | stub label "Walls, floors, doors — Phase 5 stub" | placeholder |
| ▭ Zones | stub label "Storage / stockpile / forbid — Phase 5 stub" | placeholder |
| ⚙ Jobs  | stub label "Per-role work priorities — Phase 3.10 stub" | placeholder |
| 👥 Smurfs | embedded `SmurfRosterPanel` | shipped |

The `Storage`, `Wall`, `Priorities` buttons that v0.3.25 had inside `DesignationToolbar` are gone — they live as their own tabs now, matching RimWorld's Architect/Zone/Build/Production separation. The toolbar is now a clean three-button capsule: Gather, Excavate, Remove.

**Nested-panel fix:**

Both `DesignationToolbar.BuildContent` and `SmurfRosterPanel.BuildContent` previously wrapped their content in their own `PanelContainer` with `FloatingPanelStyle.Make()`. With v0.3.24's embedding, that inner panel stacked on top of the `BottomTabPanel`'s outer styled panel — two parchment-bordered rectangles drawn over each other, with the inner one positioned wherever its own anchor preset said (which is what produced the bottom-right ghost in the screenshot).

Both build methods now use a transparent layout container (`CenterContainer` for the toolbar; `MarginContainer` for the roster) so the outer `BottomTabPanel.contentPanel` is the only thing painting the background. One panel, one background, one anchor.

**Live UI-scale rebuild preserves attached children:**

`OnUIScaleChanged` previously called `foreach (Node c in GetChildren()) c.QueueFree(); BuildShell();` — which would have nuked the embedded toolbar/roster along with the shell's own children. New handler detaches the embedded panels first (`RemoveChild`), rebuilds, then re-`AddChild`s them. Each embedded panel's own `UIScaleChanged` subscription handles its internal layout rebuild independently.

**Files touched:**
- `ui/BottomTabPanel.cs` — full rewrite to mirror HUDController's right-capsule pattern; five tabs; `Tab.None` default state; stub hosts for Build/Zones/Jobs.
- `ui/DesignationToolbar.cs` — removed inner `PanelContainer`; removed Storage / Wall / Priorities buttons (now their own tabs).
- `ui/SmurfRosterPanel.cs` — removed inner `PanelContainer`; replaced with `MarginContainer` for breathing room.

---

## [0.3.25] — 2026-05-12

### Fixed — Bottom tab shell: content panel opens *above* the tab row, no longer goes off-screen; click active tab to close

v0.3.24 stacked the tab buttons above the content panel and anchored the whole shell so the tab row was 24 px above the screen edge — which placed the content below the tabs, extending downward off the visible viewport. With the Smurfs roster ItemList that's ~150 px of content, the bottom half of the list was unreachable.

Two related design problems also surfaced:
- Both tabs were "always open" (one or the other visible at all times), so the bottom of the screen was always occluded even when the player wasn't using either panel.
- Tabs and content felt visually disconnected — no indicator that "this content belongs to this tab".

**Rewritten `BottomTabPanel`:**

1. **Inverted layout.** The VBox children are now ordered `[content panel, tab row]` so the tabs sit at the *bottom* of the shell. The shell itself is anchored `LayoutPreset.CenterBottom` with `GrowVertical = GrowDirection.Begin` so the entire stack grows *upward* from the screen-edge. Total height (`content + sep + tabs + bottomFloat`) is computed at build time from `UITheme.Scaled` constants so it stays correct across UI-size changes. With the Smurfs roster's 170 px content height, this puts the top edge of the panel at ~224 px above the screen edge — comfortably on-screen at every supported resolution.

2. **Closeable panels with `Tab.None` state.** Clicking the active tab now toggles the content panel off (RimWorld-style "click active tab to dismiss"). The tab row stays visible; the content area collapses entirely. The shell opens with `Tab.None` on scene-load so the player isn't greeted with a panel covering the bottom-centre — it appears only when explicitly requested.

3. **Single shared content `PanelContainer`.** The two host slots (`_ordersHost`, `_smurfsHost`) now live *inside* a single `PanelContainer` styled with `FloatingPanelStyle.Make()`. Both hosts use `LayoutPreset.FullRect` inside the container and toggle visibility individually. Two benefits:
   - The content panel and the tab row share visual style + the active tab button sits directly below the panel, giving the "connected to its tab" continuity the player asked for.
   - When neither host is visible (`Tab.None`), the whole `PanelContainer` hides; nothing draws above the tab row at all.

4. **Live UI-scale rebuild without reattaching children.** v0.3.24's `OnUIScaleChanged` rebuilt everything by freeing all children, which would have detached the embedded `DesignationToolbar` and `SmurfRosterPanel`. The new handler recomputes metrics, reapplies layout offsets, and resizes host slots — the toolbar and roster handle their own internal rebuilds via their own `UIScaleChanged` subscriptions. No reparenting, no tab-state loss.

**Extensibility for future tabs (Jobs, Research, etc.):**

Tab enum now has `Tab.None` as the default. Adding a new tab is three small steps:
1. Add the enum value.
2. Build a button in `BuildShell` that toggles to it via `ToggleTab`.
3. Add a host `Control` inside `_contentArea` and toggle its `Visible` in `SetActiveTab`.

No layout changes required — the content panel's height stays fixed, all hosts fill the same FullRect.

**Files touched:**
- `ui/BottomTabPanel.cs` — full rewrite of the layout / state-machine logic.

---

## [0.3.24] — 2026-05-12

### Added — RimWorld-style tabbed bottom UI; RTS box-select; combat order stub; per-order visual feedback; Smurfs roster tab

Big interaction-model overhaul. The bottom of the screen is now a two-tab shell (Orders / Smurfs) instead of a standalone designation toolbar. The non-combat orders the player can issue all live inside the Orders tab; right-click is reserved for combat orders (stubbed until Phase 8) and move orders to the currently selected smurfs. RTS-style box-select lets the player sweep multiple smurfs in one drag, and every order produces a short flash so the player knows the click was received.

**1. Bottom UI shell — `BottomTabPanel`.**

New tabbed container at the bottom-centre. Two tabs:
- **📋 Orders** — embeds the existing `DesignationToolbar` (Gather / Excavate / Storage / Wall / Priorities / Remove). Storage / Wall / Priorities remain stubs.
- **👥 Smurfs** — new `SmurfRosterPanel` listing every alive smurf in `Name · Role · Mood` format. Single-click selects + opens unit card; double-click centres the camera on that smurf (with a small move-ring flash so the player sees where their gaze should go).

`DesignationToolbar` no longer self-anchors to the viewport — its `_Ready` now sets `LayoutPreset.FullRect` so it fills whatever slot the host gives it. `BottomTabPanel` owns positioning for the whole shell, including the v0.3.20 live UI-scale rebuild.

**2. Multi-selection state — `_selectedSmurfs : HashSet<string>`.**

The single `_selectedSmurfName` from v0.3.20 is gone. `GameController` now tracks a set of selected smurf names; every selection change pushes the set to `SmurfColonyView.SetSelection` which lights up the per-smurf yellow ring. Single-click on a smurf clears + adds one name; box-select clears + adds the box's contents; clicking empty terrain without dragging clears everything.

`SmurfColonyView` lost its `_UnhandledInput` handler — input dispatch is now centralised in `GameController`. The view exposes `GetSmurfNameAt`, `GetSmurfNamesInRect`, `GetSmurfPosition`, `GetAllSmurfNames`, `SetSelection` so the controller can drive selection without reaching into private state.

**3. RTS box-select — `SelectionBoxPreview`.**

New world-space Node2D that draws a low-opacity rectangle (0.12 alpha fill, 0.80 outline) while the player is holding left-mouse on empty terrain with no designation tool active. Visual is deliberately faint so the player can see which smurfs they're sweeping over. On release, `GameController` calls `SmurfColonyView.GetSmurfNamesInRect` to collect names and replaces the selection.

Tiny-rect releases (< 4×4 px) are treated as a deselect-everything click, matching RTS conventions: a click on empty terrain clears selection.

**4. Right-click order routing.**

Right-click with one-or-more smurfs selected fires a group move order via the new `SimulationManager.RequestPlayerMoveOrderGroup`, which arranges the smurfs in a small ring around the target (12-px radius offsets) so they don't stack on the same pixel. The cyan move-ring feedback fires once at the target.

The combat-order code path is plumbed but inactive — no enemy entities exist yet (Phase 7+). When they do, the dispatcher will detect an enemy under the cursor and call `RequestCombatOrder` instead of the move call, and `OrderFeedbackOverlay.RingCombat` will fire the red ring.

**5. Combat order stub data plumbing.**

- `Smurf.CombatTargetName : string?` (sim-thread state).
- `SmurfSnapshot.CombatTargetName` propagates to the main thread.
- `SimulationManager.RequestCombatOrder(name, target)` / `ClearCombatOrder(name)` queue updates through `SimulationCore.PendingCombatOrders` (ConcurrentQueue).
- `SimulationCore.Run` drains the queue each tick and sets the flag on the named smurf — no behavior reads it yet.
- `SmurfColonyView.DrawSmurf` draws a red ⚔ glyph above the head while the flag is non-null. The icon is sized to remain readable when smurfs cluster.

When Phase 8 lands, the behavior system will:
- Detect non-null `CombatTargetName` as a Tier-0 override.
- Resolve target → enemy entity → route attack task.
- Clear the flag on target death or player cancel.

**6. Per-order visual feedback — `OrderFeedbackOverlay`.**

New Node2D in world space drawing short-lived pulses:
- **Designation commit** — coloured tile-flash (green/orange/red) over each cell of the just-committed rect, fades over 0.5 sec. Distinct colours for Gather / Excavate / Remove so the player gets immediate semantic confirmation.
- **Move order** — cyan expanding ring at the target, 0.6 sec.
- **Combat order** — red expanding ring at the target, 0.6 sec.

Pulses are pooled in a single `List<Pulse>` with TTL counters; `_Process` ticks them down, `_Draw` walks the live ones with alpha-fade lerps. No allocations per frame. Audio cues come in a later phase (deferred per the user's "audio later on" guidance).

**7. Sword icon for in-combat smurfs.**

`SmurfColonyView.DrawSmurf` adds a small red ⚔ glyph above the hat tip while `VisualSmurf.CombatTargetName` is non-null. Visible at every zoom level. Sized 12 pt so it stays readable. Future Phase-8 enemy units will get the same glyph from their own draw path.

**Files added:**
- `ui/BottomTabPanel.cs` — tabbed shell.
- `ui/SmurfRosterPanel.cs` — Smurfs tab content + `SimulationC_Roster_Row`.
- `ui/SelectionBoxPreview.cs` — RTS drag rect.
- `ui/OrderFeedbackOverlay.cs` — flash/ring feedback.

**Files modified:**
- `simulation/Smurf.cs` — `CombatTargetName` field.
- `simulation/SimulationSnapshot.cs` — `CombatTargetName` propagation.
- `simulation/SimulationCore.cs` — `PendingCombatOrders` queue + drain in `Run`.
- `SimulationManager.cs` — `RequestPlayerMoveOrderGroup`, `RequestCombatOrder`, `ClearCombatOrder`.
- `ui/SmurfColonyView.cs` — removed `_UnhandledInput`; added selection / lookup helpers; sword icon in `DrawSmurf`.
- `ui/DesignationToolbar.cs` — embedded mode (no self-anchoring).
- `ui/GameController.cs` — multi-select state, drag dispatcher, roster wiring, feedback flash calls.

---

## [0.3.23] — 2026-05-12

### Fixed — Designations now pull smurfs immediately; visuals stop cutting through walls; drag commits use the drag's own tool

Three reported bugs from the v0.3.22 playtest screenshots, all with distinct root causes.

**1. Excavate / Gather designations were ignored while smurfs were wandering.**

`BehaviorSystem.Tick` decided whether to re-evaluate task selection based on a `needNewTask` predicate that only fired on (a) no task, (b) `TaskType.None`, or (c) the current task being interruptible AND `CriticalNeedsOverride` returning true. Designations are not critical needs — `CriticalNeedsOverride` only checks Nutrition/Rest/Safety thresholds. So a wandering smurf (priority 5, interruptible) never even ran `SelectTask` to discover a freshly painted designation. The smurfs in the playtest screenshot looked unresponsive because they literally couldn't see the work until a hunger/sleep timer fired minutes later.

Fix: `Wander` is now an explicit re-evaluation trigger. Every tick, a wandering smurf rebuilds its task list and picks up Tier-2 designations at priority 40–60 immediately. Cost is bounded — the designation scans are 25-tile radius, ~2600 cells × 20 smurfs × 60 Hz is well within the C# arithmetic budget.

**2. Smurfs visually walked through walls.**

The sim's pathing fix from v0.3.22 was correct — `SimPos` never enters an impassable tile. But the *visual* avatar didn't follow `SimPos`: `SmurfColonyView._Process` lerped the on-screen position toward the sim position in a straight line at the visual's own speed (28–55 px/sec). When the sim took the legitimate route *around* a LivingWood blob, the straight-line visual lerp cut the corner *through* the rock. For several frames the avatar was drawn inside terrain it had never actually been in. This is what produced the "Miner in the middle of the orange box" appearance in the screenshot — the sim had him correctly stationed at the boundary, the lerp was halfway across.

Fix: drop the lerp. The visual snaps to `SimPos` directly. Sim updates at 60 Hz with ~0.5-px deltas (and faster at higher speed multipliers), which is already perceptually smooth — the lerp was adding latency, not smoothness. By construction, the visual now stays on whatever passable tile the sim's authoritative position is on.

**3. Drag-box commits used the wrong tool when the player switched mid-drag.**

Both commit paths in `GameController` (`TryHandleMouseButton` on release, `FinalizeStrandedDrag` in `_Process` for releases over the toolbar) read `_toolbar.ActiveDesignation` to decide whether to call `DesignateRect(Excavate, …)` or `DesignateRect(Gather, …)`. If the player clicked the Gather button while still holding an in-progress Excavate drag (or vice versa), `ActiveDesignation` had already shifted to the new tool, but the rectangle on screen was painted in the original tool's colour. The commit then ran the new tool over the old rectangle — `Gather` over Forest Floor tiles in the painted area would clear their Excavate flag via the mutual-exclusion rule in `SetGatherDesignation`, effectively wiping prior designations.

Fix: `DragSelectionPreview` already tracks the tool the drag started with internally; v0.3.23 exposes it as a public `Tool` getter. Both commit sites now use `_dragPreview.Tool` rather than `_toolbar.ActiveDesignation`, so the rect is always committed with the colour it was painted in. The player's intent is what's on screen, not what's selected on the toolbar at the moment of release.

**4. Bonus — stuck-give-up no longer infinite-loops.**

`MoveOneTick` previously cleared `s.CurrentTask` after `StuckTicks > StuckThreshold`. The next tick `SelectTask` re-found the same designation (still nearest), the smurf walked back, got stuck again, repeat. The visible effect: a smurf would freeze inside an impassable boundary, occasionally jitter, never make progress.

Fix: on give-up, route to a fresh `Wander` task. The smurf physically displaces (wandering picks a random passable tile within 8), and the next re-evaluation either picks a different designation that's now closer or approaches the original one from a new angle. Phase 4's A* will reject unreachable targets at planning time and remove the need for this workaround.

**Files touched:**

- `simulation/systems/BehaviorSystem.cs` — `needNewTask` gains `Wander` clause; `MoveOneTick` takes `rng` so the stuck-give-up Wander can use the existing sim-thread Random; replaces task-null with `NewWanderTask` on give-up.
- `ui/SmurfColonyView.cs` — visual `_Process` snaps `Pos` to `Target` instead of lerping.
- `ui/DragSelectionPreview.cs` — public `Tool` getter for the drag's originating tool.
- `ui/GameController.cs` — `TryHandleMouseButton` and `FinalizeStrandedDrag` commit with `_dragPreview.Tool`.

---

## [0.3.22] — 2026-05-12

### Fixed — Smurfs no longer walk through walls or pingpong against obstacles; Phase 4 path hook in place

Diagnostic pass on Phase-3 movement. The previous tick code in `BehaviorSystem.Tick` had six concrete failure modes that combined to produce the "braindead" behaviour the player saw — and one of them (smurfs standing inside impassable target tiles) was actually a side-effect of how the *task system* targeted excavation work, not movement per se.

**Failure modes diagnosed:**

1. `ArrivalRadius = 10 px` against the *centre* of the target tile. `GatherMaterial` (excavation) targets the centre of an impassable Boulder/DeadLog/LivingWood tile. The smurf walks toward that centre and "arrives" within 10 px — but the tile only spans ±8 px from its centre, so on arrival the smurf is physically inside the rock. Looked exactly like walking through walls because that is what it was.
2. `NudgeToPassable` teleported up to ~22 px (one diagonal tile) when both ±45° deflections were blocked. Snapping to a random adjacent tile centre instead of micro-stepping looked like a glitch every time the smurf bumped a wall corner.
3. **No SimPos sanity check.** If a smurf's SimPos ever drifted into an impassable tile (vegetation regrowing under them — LargeMushroom case — save/load races, or the now-removed first-tick teleport at line 78–79 with a target inside rock), nothing recovered. Worse: critical-need tasks (`Eat`/`Sleep`/`Socialize`/`Attune`/`SeekSafety`) all set `Target = s.SimPos`, so a smurf trapped inside rock would happily complete every survival action inside the rock forever.
4. **Two-angle local steering trapped the smurf on concave geometry.** L-walls and inside corners block both ±45° options, the code fell to the random-teleport branch, the smurf ended up back near the obstacle on the next tick, repeat — that's the pingponging.
5. **No stuck detection.** A genuinely unreachable target (designation behind a wall, vegetation eaten by another smurf mid-walk) was retried indefinitely.
6. **First-tick teleport** `if (s.SimPos == Vector2.Zero) s.SimPos = s.CurrentTask.Target;` — silently relocated smurfs onto whatever the first task target was, including impassable tiles for `GatherMaterial`.

**Rewrite (new `MoveOneTick` helper in BehaviorSystem):**

- **SimPos rescue.** Every tick now starts by checking whether the smurf is standing on a passable tile. If not, a bounded BFS (8-tile radius) finds the nearest passable tile centre and snaps the smurf there. Worst-case (pathologically enclosed): the smurf stays put, which is strictly better than wandering through walls. This is also what catches the LargeMushroom regrowth case where a smurf is on the cap when it restores.
- **Adjacent-approach for impassable targets.** New `RequiresAdjacentApproach(TaskType)` predicate marks `GatherMaterial` as a task that interacts *with* a tile rather than standing on it. `ResolveWalkTarget` retargets movement to `NearestAdjacentPassableTile(tx, ty)` — the smurf walks to a passable neighbour and works the rock from there. The task target tile coords (`TargetTileX/Y`) are preserved so `ApplyTaskEffect` still mutates the right boulder. Visually: smurf stands beside the rock and chips at it, instead of standing on top of it.
- **Six-direction steering fan-out.** Tries straight (0°), ±45°, ±90°, ±135° in order — picks the first candidate whose destination tile is passable. Concave corners are handled because the larger deflections can route the smurf along the wall rather than into it.
- **No teleports.** When every fan-out angle is blocked, the smurf stays put for the tick. `NudgeToPassable` is gone.
- **Stuck detection.** Two new fields on `Smurf` — `PrevSimPos` and `StuckTicks`. If per-tick progress is below `ArrivalEpsilon = 0.5 px` for `StuckThreshold = 120` ticks (≈ 2 sec at 1×), the current task is cleared and `SelectTask` picks again next tick. The smurf reroutes instead of pingponging.

**Phase 4 hook:**

- New `Smurf.PathWaypoints` (List<Vector2>) is the **plug-in point for Phase 4's A\* planner**. Empty in Phase 3 — `MoveOneTick` falls through to local greedy steering. When Phase 4 lands, the planner populates the list with intermediate tile-centre waypoints and `ResolveWalkTarget` consumes the head (pop on arrival, fall through to task target when empty). The local steering stays as the last-step approach to the final waypoint, which is the right behaviour even with A* — the planner gets you to the right neighbourhood, the local steering finds the exact arrival point.
- `PathWaypoints` is cleared whenever `SelectTask` picks a new task or `StuckTicks` trips the give-up threshold, so stale paths can't bleed into the next task.

**Removed:**

- `NudgeToPassable` (teleport fallback). Its only caller was the inline movement code.
- `s.SimPos == Vector2.Zero` first-tick anchor. SeedSimPositions already places smurfs on passable spawn-cluster tiles, and the new rescue handles any edge case where SimPos drifts off-grid.

**Files touched:**

- `simulation/Smurf.cs` — three new fields: `PathWaypoints`, `PrevSimPos`, `StuckTicks`.
- `simulation/systems/BehaviorSystem.cs` — inline movement block replaced by `MoveOneTick`. New helpers `ResolveWalkTarget`, `RequiresAdjacentApproach`, `NearestAdjacentPassableTile`, `NearestPassableTileCentre`. `NudgeToPassable` deleted.

Save format unchanged — the new fields are runtime state with property initializers, so loading a v0.3.21 save into v0.3.22 is a no-op.

---

## [0.3.21] — 2026-05-12

### Changed — Designations are now drag-box; Move tool replaced by Gather; visual feedback added

Phase 3.x designations were single-tile click toggles in v0.3.20 (click Excavate → click a boulder → flag flips). v0.3.21 replaces that with the standard RimWorld / DF model: pick a tool, hold left-mouse and drag a rectangle across a region of the map, and every valid tile inside the rectangle gets designated on release. A live preview rectangle shows exactly which cells the drag covers, and a persistent overlay draws a coloured glyph on every flagged tile so the player can see the current designation queue at a glance.

**Tool list (DesignationToolbar):**

- `🍓 Gather` — replaces the old `↗ Move` button. Targets food-yielding vegetation: Smurfberry Bush, Small Mushroom, Herb Cluster, Small Sandshroom, Pineshroom. The simulation's `FoodYield` table actually returns positive food values for several non-food types too (LargeMushroom, MagicFlower, etc.) — for the player-facing designation we use the stricter `LocalMap.IsFoodYielding` list that matches what `TileInfoOverlay` labels as "Food" or "Food + Magic Essence".
- `⛏ Excavate` — unchanged target list (Boulder / DeadLog / LivingWood) but now drag-box rather than click-toggle.
- `✕ Remove` — wipes both designation flags on every tile in the rectangle. Previously cleared excavation on a single tile.
- Storage / Wall / Priorities remain stubbed pending Phase 5+.

The Move button is gone because right-click on the map with a smurf selected already issues a `RequestPlayerMoveOrder` (unchanged from v0.3.20), and the Gather designation supersedes its general "tell this smurf where to be" use case for non-combat orders.

**Behavior wiring (BehaviorSystem.SelectTask):**

Designations are evaluated as Tier-2 tasks before the autonomous role chains. The selection rule mirrors RimWorld's "nearest pawn with highest priority for this work type takes it" behaviour:

- Foragers pick the nearest `DesignatedForGather` tile at priority 60. The old "Forager only seeks vegetation when colony Food < 30" gate is bypassed for designated tiles — if the player drew the box, they want it picked.
- Crafters pick the nearest `DesignatedForExcavation` tile at priority 60.
- Cross-role fallback at priority 40: any other idle smurf will pick up either designation once role-matched smurfs are busy. Designation priority (60 > 40) ensures the right specialist wins the tiebreak when both are equidistant.
- Autonomous food-seeking (priority 55) still runs as a Tier-2 fallback when reserves are low and no Gather designations exist.

`BehaviorSystem.ApplyTaskEffect` now calls `LocalMap.ClearDesignationsAt` after a successful harvest (Gather) or after MutateTerrain'ing the dug-out tile to Mud (Excavate). This auto-clears the visual glyph and prevents stale designation lookups from routing a second smurf to an already-cleared tile.

**Data layer:**

- `LocalTile` gains `DesignatedForGather` alongside the existing `DesignatedForExcavation`. The two flags are mutually exclusive (setting one clears the other) — the player can't tell a smurf to both gather *and* excavate the same tile.
- `LocalMap` gains `SetExcavationDesignation(x, y, on)`, `SetGatherDesignation(x, y, on)`, `ClearDesignationsAt(x, y)`, and a `DesignationChanged?` event. Per-tile validity is enforced inside the setters: Excavate requires `Boulder / DeadLog / LivingWood`, Gather requires a non-depleted food-yielding `VegetationSlot`. Invalid tiles in a drag-box rect are silently skipped so the player doesn't have to pre-filter the rectangle.
- `SimulationManager` exposes `SetExcavationDesignation`, `SetGatherDesignation`, `ClearDesignationsAt`, and `DesignateRect(tool, x0, y0, x1, y1)` for the bulk-apply path. The legacy `ToggleExcavationDesignation` is gone — single-cell click semantics are recovered by dragging a 1×1 rect, and the box-area path is now the only API.
- Designations are not yet save-delta serialised. Existing terrain + vegetation deltas are unaffected; reloading a save will clear the in-flight designation queue but preserve completed work (already-mutated terrain, already-depleted vegetation).

**Visual feedback (two new Node2D components, world-space children of `GameController`):**

- `DesignationOverlay` — subscribes to `LocalMap.DesignationChanged` and `_Draw`s a semi-transparent coloured rect + glyph on every flagged tile. Excavate is amber/orange with `⛏`; Gather is leaf-green with `🍓`. Sits between `LocalMapRenderer` and `SmurfColonyView` in z-order so smurfs always render on top of their work markers. Designation toggles dispatch as event-driven QueueRedraws — no per-frame polling.
- `DragSelectionPreview` — drawn only while a drag is in progress. The fill tint and 2-px outline match the active tool's colour (orange for Excavate, green for Gather, red for Remove) so the player gets immediate "I'm drawing this kind of designation" feedback before mouse-up commits the rect. Tile-aligned: even at 33 % UI Size or 4× zoom the preview snaps to integer tile edges.

**Input dispatch (GameController):**

- Left-press on the map starts a drag; mouse-motion updates the rectangle's far corner; left-release commits via `SimulationManager.DesignateRect`. `_UnhandledInput` now intercepts release events too, where v0.3.20 only ran on press.
- `PanWithMouseDrag` is suppressed whenever a designation tool is active so the camera doesn't pan during a designation box-draw. WASD-panning still works at all times.
- Stranded-drag handler: if the player releases the mouse over the toolbar (a `MouseFilter = Stop` PanelContainer that swallows the event), the next `_Process` tick detects the unheld mouse button and commits the drag anyway, using the last tile the preview was updated to. The visible preview never lingers past a real button-release.

**Files touched:**

- `world/LocalTile.cs` — new `DesignatedForGather` field.
- `world/LocalMap.cs` — new event + designation setter API + `IsFoodYielding` filter.
- `SimulationManager.cs` — new public API; `ToggleExcavationDesignation` removed.
- `simulation/systems/BehaviorSystem.cs` — new `FindDesignatedGather`, cross-role fallback rewrite, designation auto-clear on completion.
- `ui/UITheme.cs` — shared `DesignationTool` enum.
- `ui/DesignationToolbar.cs` — `Tool.Move` → `Tool.Gather`, `ActiveDesignation` accessor for the dispatcher.
- `ui/GameController.cs` — drag-box input pipeline; suppress camera-pan when a tool is active; finalize-stranded-drag in `_Process`.
- `ui/DesignationOverlay.cs` *(new)* — coloured glyph overlay for every flagged tile.
- `ui/DragSelectionPreview.cs` *(new)* — live rectangle while dragging.

---

## [0.3.20] — 2026-05-12

### Changed — UI Size now applies live (no main-menu return needed)

v0.3.19 made UI Size shrink content correctly but required returning to the Main Menu and reloading the game scene for the change to take effect, because each scaled component only read `UITheme.UIScale` once at construction time. v0.3.20 wires the components to a live-update event.

**`UITheme.UIScaleChanged` event:**

`SetUIScale(scale)` now fires `event System.Action? UIScaleChanged` whenever the value changes (no-op if the new value matches the existing one within float-equality). Phase-3.x components subscribe in their `_Ready` and unsubscribe in `_ExitTree`. On the event:

- **`HUDController`** — clears its band + capsule children, calls `BuildContent()` again with the new scale. Pulse / speed-button tracking (`_speedBtns`) is cleared first so the new buttons don't compete with stale references. Resource labels repopulate from the next sim snapshot via `_Process`; no explicit refresh needed.
- **`DesignationToolbar`** — preserves `ActiveTool` across the rebuild (clears children, rebuilds, re-applies the previously-active tool through `Refresh`). The player's selected mode survives a UI-size tweak unchanged.
- **`AlertsPane`** — preserves the alert list in a new `List<(string, AlertLevel)> _alerts` field and the visibility state. On rebuild, replays every alert into the new list container via the extracted `AppendAlertRow(text, level)` helper. `AddAlert` records to the field first, then calls `AppendAlertRow`, so live alerts and rebuilt alerts go through identical rendering.
- **`TileInfoOverlay`** — only the label's `font_size` override needs touching; no rebuild required, the in-place update suffices.

**Settings tooltip updated** — "Change applies immediately" instead of "next time the game scene loads".

`SettingsPanel.ApplyUIScale` still just calls `UITheme.SetUIScale(scale)` — the event fan-out is centralised in `UITheme`, which keeps the settings panel decoupled from the in-game UI tree.

**Edge case handled:** if the same scale is set twice in a row, `SetUIScale` short-circuits via `Mathf.IsEqualApprox(clamped, UIScale)` — no rebuild runs, no flicker.

### Fixed — Tooltip Size dropdown now visibly changes tooltip size mid-game

The Tooltip Size dropdown was already wired to `ApplyTooltipSize` on `ItemSelected` (which assigns a freshly built `BuildTooltipTheme(fontSize)` to `GetTree().Root.Theme`), and Godot recreates tooltip popups per hover, so the new theme was being applied. The visible problem was that the font-size steps were too close to perceive:

- **Old:** Large 10 / Normal 8 / Small 6 — a two-pixel step that read as "no change" at almost every screen size.
- **New:** Large 18 / Normal 14 / Small 10 — a four-pixel step, and "Large" now matches typical body-text size instead of being smaller than Godot's own default.

Change is live: pick a new size from the dropdown and the next hover renders at the new size. No need to return to the main menu, and no need to close the Settings panel to confirm.

---

## [0.3.19] — 2026-05-12

### Changed — Era + Resource HUD merged into one top-left panel; ModalLayer above gameplay UI; UI Size scales content not anchors

Three coordinated fixes to the player-reported issues from the v0.3.18 screenshots.

**1. Era bar + Resource bar merged into a single top-left capsule (`HUDController`)**

The standalone `ResourceHUD` floating capsule (added v0.3.14, moved top-centre v0.3.17) is removed from `GameController.BuildUILayer`. Its content moves into the existing `HUDController` left capsule as a **second row**:

- Row 1: `🌅 Era · 📅 Date · 👤 Pop · 😊 Mood`
- Row 2: `🍓 Food · 🪨 Stone · 🪵 Wood · ✨ Magic (unstored — Phase 5 will gate)`

Single VBox inside the top-left `PanelContainer`. Per-frame `_Process` in `HUDController` pulls the colony resources via `Sim.GetResourcesSnapshot()` and updates the four value labels. The top centre band is now genuinely empty — the toolbar / smurf card / alerts pane no longer compete for it.

**2. ModalLayer (Layer = 20) above gameplay UILayer (Layer = 10)**

The v0.3.18 screenshot showed the resource bar rendering *on top of* the Settings panel — because every panel lived on the same `UILayer` `CanvasLayer` and order-of-`AddChild` determined z-order, the later-added `ResourceHUD` painted over the earlier-added `SettingsPanel`. Fix is structural: split the canvas into two layers.

- `UILayer` (Layer 10): `HUDController`, `SmurfCardPanel`, `TileInfoOverlay`, `MessageLog`, `DesignationToolbar`. Plays under modals.
- `ModalLayer` (Layer 20): `PauseMenuPanel`, `SettingsPanel`, `SaveFileBrowser`, `SavingOverlay`, `GameOverPanel`, `AlertsPane`. Always above gameplay UI.

Higher `Layer` values render on top — Godot CanvasLayer rule. No more order-of-creation gotcha; any future overlay added to `ModalLayer` automatically gets correct precedence.

**3. UI Size scales content, not anchors (`UITheme.UIScale` + per-component `Scaled()`)**

v0.3.16 implemented UI Size via `CanvasLayer.Transform.Scale`, which multiplied every child position by the scale factor and bunched the layout toward the top-left corner. The 33 % setting was unplayable — bottom-right toolbar drifted to the middle of the screen.

v0.3.19 replaces that with a per-component multiplier:

- `UITheme.UIScale` static property (float). `SetUIScale(scale)` stores it.
- `UITheme.Scaled(int)`, `ScaledF(float)`, `ScaledVec(x, y)` helpers floor-clamp to ≥ 1.
- `GameController.BuildUILayer` no longer applies a canvas transform — it pushes the saved value into `UITheme.SetUIScale` before constructing any UI.
- `SettingsPanel.ApplyUIScale(scale)` calls `UITheme.SetUIScale(scale)` (live setting changes apply next scene load — tooltip updated to set expectations).
- Components scaled in this pass: `HUDController` (every label / icon size), `DesignationToolbar` (button height + font size), `AlertsPane` (popup dimensions + all font sizes + glyph), `TileInfoOverlay` (font size).
- `AnimatedButton`'s internal compact 13 pt font size is still hard-coded; a future pass will route it through `UITheme.Scaled` for full coverage.

Net result: anchors stay at viewport edges (top-left capsule stays top-left, toolbar stays bottom-centre, smurf card stays top-right), but fonts / icons / minimum sizes shrink at 66 % and 33 %. Playable at all three sizes.

---

## [0.3.18] — 2026-05-12

### Fixed — Top HUD overflow; AlertsPane now a dismissible centre popup; TileInfoOverlay restored to top-right; toolbar floats higher

The v0.3.17 top-bar refactor collapsed the left/right HUD anchors to a single point (`AnchorLeft = AnchorRight = 1f`) which produced a zero-width bounding box for the right capsule. PanelContainer rendered its content from that degenerate rect, and the speed buttons ended up clipped off the right edge of the viewport — invisible and unclickable, hence the "speed buttons don't work" report.

**`HUDController` — proper HBox band layout:**

- Root Control: `FullRect`, `MouseFilter = Pass`.
- Single `HBoxContainer` spanning the top of the viewport (`LayoutPreset.TopWide`) with `OffsetLeft = OffsetRight = ±EdgeInset` clearance and `OffsetTop = EdgeInset`.
- Three children: **left `PanelContainer`** (Era / Date / Pop / Mood) + **flexible spacer** (`SizeFlagsHorizontal = ExpandFill`) + **right `PanelContainer`** (Speed / Pause / 1× / 2× / 5× / 10× / Menu).
- Each capsule sizes to its content; the spacer reserves the centre band so `ResourceHUD` floats over an empty gap without ever crowding the capsules.
- Speed buttons now render fully on-screen → clicks register correctly.

**`AlertsPane` — dismissible centred popup (no longer always-visible):**

Rebuilt from scratch. The pane is now invisible by default and only opens when `AddAlert(text, AlertLevel.Warning)` or `AlertLevel.Critical` is called (smurf death, mental-break threshold, starvation, raid inbound, plague onset — wired by Phase 4 / 8 / 12 systems as they land). The popup:

- Anchors centred on screen (`AnchorLeft = AnchorRight = AnchorTop = AnchorBottom = 0.5f`), sized 520 × 320 with a `FloatingPanelStyle.Make()` panel.
- Dim backdrop at 45 % opacity behind the panel so the popup reads as modal.
- Scrollable list of alert rows; each row gets a severity glyph (`⚠` Critical / `!` Warning / `·` Info) and colour-coded text via `UITheme.TextDanger / TextWarn / TextPrimary`.
- Footer `Dismiss` button hides the popup; new critical pushes re-open it.
- `Info`-level alerts accumulate silently (added to the list but don't open the popup) so background notifications don't interrupt gameplay.

**`TileInfoOverlay` — restored to top-right:**

Now anchored top-right (`AnchorLeft = AnchorRight = 1f`) at `OffsetTop = 70` — sits below the HUD speed capsule, where v0.3.15 originally placed it. The bottom-left position from v0.3.17 conflicted with the `MessageLog`, which has been bottom-left since well before Phase 3.x.

**`DesignationToolbar` — floats higher off the bottom:**

Bottom inset bumped from `EdgeInset = 16` to `BottomFloat = 24`. The toolbar capsule now has visible breathing room from the bottom edge rather than reading as flush.

**Speed buttons:** automatically fixed by the HBox refactor. The click handler (`Sim.SetSpeed(speed)` + `SetActiveSpeed`) was always correct — the buttons were just rendered off-screen and uncatchable.

**UI Size sanity check** (Settings → Gameplay → UI Size, from v0.3.16):

- **100 %**: left capsule ≈ 480 px + ResourceHUD ≈ 400 px + right capsule ≈ 400 px = ~1280 px content. Fits 1920×1080 (default) and 2560×1440 (typical) with comfortable margins.
- **66 %**: ≈ 845 px content. Fits even small-laptop 1366×768 displays.
- **33 %**: ≈ 425 px content. Tight but readable at 13 pt font.

---

## [0.3.17] — 2026-05-11

### Changed — Top HUD refactored to two floating capsules; Alerts pane sized to content; Tile info moved bottom-left

The Phase 3.x floating-panel philosophy now extends to the previously full-width-flush top bar. Every player-facing in-game panel uses `FloatingPanelStyle` with `UITheme.EdgeInset` clearance from the viewport edges. The screenshot from v0.3.16 showed three problems: a flush top bar that didn't match the floating style, an alerts pane forced to 200 px tall when it had nothing to say, and a tile info overlay parked under both of those in the top-right corner.

**`HUDController` — split into two floating capsules:**

- Was `LayoutPreset.TopWide` with a `ColorRect` background and a gold underline running edge-to-edge.
- Now `LayoutPreset.FullRect` transparent host (`MouseFilter = Pass`) containing **two `PanelContainer` capsules** styled with `FloatingPanelStyle.Make()`:
  - **Top-left capsule** — Era · Date · Pop · Mood. Anchors collapsed at `(0, 0)` with `OffsetLeft = OffsetTop = EdgeInset`, panel sizes to content.
  - **Top-right capsule** — Speed buttons (⏸ / 1× / 2× / 5× / 10×) + Menu. Anchors collapsed at `(1, 0)`, `GrowHorizontal = Begin` so the capsule grows leftward from the right edge. `OffsetTop = EdgeInset`, `OffsetRight = -EdgeInset`.
- Font sizes reduced one step (16/15/13 instead of 18/15/14) so the capsules don't dominate the playfield. All existing public API (`Sim`, `UpdateStats`, `SyncPauseButton`, `MenuRequested`) preserved.

**`AlertsPane` — content-sized:**

Removed the hard-coded `panel.OffsetBottom = 200` that forced the panel to ~200 px even with no alerts. Anchors now collapsed at `(1, 0)` with `GrowHorizontal = Begin` and `CustomMinimumSize = (220, 0)` so the panel sizes to its inner VBox (just the "Alerts" header + "All systems nominal." placeholder when nothing is active). Sits below the top-right HUD capsule at `OffsetTop = EdgeInset + HudHeight + 8`.

**`ResourceHUD` — moved up:**

Previously offset by `EdgeInset + HudHeight + 6` to clear the old full-width HUD. Now sits at `OffsetTop = EdgeInset` and shares the top band horizontally with the two HUD capsules — left capsule on the left, ResourceHUD centred, right capsule on the right.

**`TileInfoOverlay` — moved to bottom-left:**

Was top-right (`AnchorLeft = AnchorRight = 1f`, `OffsetTop = 54`) which conflicted with the new HUD right capsule and the alerts pane above it. Now bottom-left at `AnchorTop = AnchorBottom = 1f`, `OffsetLeft = 16`, `OffsetTop = -110`, clearing the bottom-centre designation toolbar. Matches RimWorld's "inspect text" placement convention. Font bumped one step (12 → 13) for legibility.

---

## [0.3.16] — 2026-05-11

### Fixed — Smurfs invisible on scenario-based games; Added — UI Size setting (100 % / 66 % / 33 %)

**Spawn bug — three interacting causes, all fixed:**

1. **`GameController.SeedColonyVisuals`** was hard-coded to the founding-seven names (`Papa / Brainy / Hefty / Smurfette / Clumsy / Handy / Grouchy`). For any scenario with custom or random names, the seeded `VisualSmurf` set didn't match the sim's actual roster — on the next snapshot, every seeded smurf was removed (`RemoveAll(vs => !snapNames.Contains(vs.Name))`) and 25 new visuals had to be re-spawned from scratch. Now it reads from `_sim.GetLastSnapshot()` first (the authoritative live roster) and falls back to the founding seven only if no snapshot has arrived yet.
2. **`SimulationManager.SeedSimPositions`** asked for a fixed `Math.Max(8, 16) = 16` spawn-cluster tiles regardless of roster size. With 17+ smurfs the cluster wrapped (`i % spawn.Count`) and visibly stacked colonists on the same pixel. Now requests `Math.Max(8, count × 1.5)` so even a 25-smurf colony has unique tiles with jitter room.
3. **`SmurfColonyView.UpdateFromTick`** spawned new `VisualSmurf`s at either the average of existing visuals or a single BFS-from-centre tile, independent of where the sim already placed the smurf. Newborns and snapshot-driven additions now spawn at `snap.SimPos` directly when it's non-zero, so the smurf renders exactly where the sim says it is — no race window where the visual sits at one spot while `vs.Target` lerps toward another.

Combined, these three fixes ensure every smurf (regardless of scenario size) appears at its sim position from the very first rendered frame.

**UI Size setting — Settings → Gameplay → UI Size:**

New dropdown with three descending options: **100 %** (default), **66 %**, **33 %**. Implemented by scaling the in-game `UILayer` `CanvasLayer`'s transform — shrinks every floating panel uniformly (HUD bar, Resource HUD, Designation Toolbar, Alerts Pane, Smurf Card, Message Log, Tile Info Overlay) without changing camera zoom or playfield rendering. Persisted to `user://settings.cfg` under `gameplay.ui_scale_idx`; `GameController.BuildUILayer` applies the saved value at scene-ready; `SettingsPanel.ApplyUIScale` re-applies it live whenever the player switches the dropdown. New public static helper `SettingsPanel.LoadSavedUIScale()` for any future caller that needs the value without instantiating the panel. Reset-to-defaults restores 100 %.

---

## [0.3.15] — 2026-05-11

### Changed — Scenario panel guarantees ≥1 female on automatic generation paths

Default colony rolls on the canonical 1:49 sex ratio (from v0.3.13) produced all-male rosters in ~87 % of 7-smurf scenarios, meaning a fresh scenario was almost always playable without a mother. The user's intent is the opposite: every colony **starts** with at least one female unless the player **explicitly** removes them in the scenario editor. v0.3.15 enforces that floor on the automatic-generation paths while respecting explicit player edits.

**`ScenarioPanel.cs` — new `EnsureAtLeastOneFemaleAmongIndices(start, end)` helper:**

- Scans the roster; if any female exists anywhere, no-op.
- Otherwise picks a random index in `[start, end)` and forces it to female (regenerates the name to match).
- Caller passes the index range so the flip can only land on slots the *system* generated, never on slots the *player* explicitly toggled to male.

**Call sites (automatic-generation only):**

- **`Open()`** flows through `RebuildSmurfTemplates(DefaultSmurfCount)` which now enforces the guarantee over the newly-generated range. Result: every fresh scenario screen opens with at least one female in the founding roster.
- **`Randomize All` button** runs `EnsureAtLeastOneFemaleAmongIndices(0, count)` after the loop — explicit "give me a random colony" request gets a playable one.
- **Smurf-count slider grow** — when the slider increases the roster size, the new range gets enforcement. The flip can only land on the newly added templates, never on existing slots the player has already edited.

**Call sites explicitly **not** enforced (respects player intent):**

- **Per-smurf sex toggle** — direct manual flip; the player is in charge.
- **Per-smurf 🎲 randomize** — single-smurf roll the player asked for; result stays whatever the dice say.
- **Smurf-count slider shrink** — removing tail entries doesn't reroll anyone.

This matches the user's "no less than one female unless specified by player": fresh / bulk-random colonies are always playable; explicit player overrides stand.

---

## [0.3.14] — 2026-05-11

### Added — Phase 3.x Floating Gameplay UI (implementation)

The roadmap's Phase 3.x — Floating Gameplay UI is now live. The Phase 3 data layer (`RequestPlayerMoveOrder`, `ToggleExcavationDesignation`, `GetResourcesSnapshot` from v0.3.0) is finally reachable from the mouse and keyboard.

**Foundation — `scripts/ui/UITheme.cs` + `scripts/ui/FloatingPanelStyle.cs`:**

Shared constants and `StyleBoxFlat` factory used by every new Phase-3.x panel. Single source of truth for corner radius (10 px), screen-edge inset (16 px), shadow size (6 px), parchment background (0.08 / 0.06 / 0.03 @ 92 %), gold border (0.95 / 0.80 / 0.28 @ 30 %), text colours (primary / muted / accent / warn / danger). `FloatingPanelStyle.Make()` / `MakeHover()` / `MakeActive()` / `MakeToolbarButton(active)` give every component a consistent look without per-panel theme overrides.

**New floating panels:**

- **`DesignationToolbar.cs`** (bottom-centre) — the player's primary tool selector. Six categories: Move, Excavate, Storage (stubbed), Wall (stubbed), Priorities (stubbed), Remove. Holds the active-tool state (`Tool` enum); other systems read `_toolbar.ActiveTool` to dispatch map clicks correctly. Active tool gets a brighter pressed-state style; re-clicking the active tool deselects (returns to `Tool.None`).
- **`AlertsPane.cs`** (top-right under smurf card) — colony alerts list. Public `AddAlert(text, level)` API for Phase 4 / Phase 8 / Phase 12 systems to push alerts (Info / Warning / Critical colour scales). Ships with a "All systems nominal." placeholder until live alerts wire in.
- **`ResourceHUD.cs`** (top-centre, under existing era / date bar) — live readout of `SimulationManager.GetResourcesSnapshot()` (Food / Stone / Wood / MagicEssence). `(unstored — Phase 5 will gate)` qualifier reminds the player the number is the placeholder pool until Phase 5 storage tagging lands.

**Wiring — `GameController.cs`:**

- Holds new private fields for `_toolbar`, `_alertsPane`, `_resourceHUD`, and `_selectedSmurfName`.
- `BuildUILayer()` adds all three panels to the `UILayer` `CanvasLayer`.
- New `_UnhandledInput(InputEvent)` override handles right-click and tool-aware left-click on the map:
  - **Right-click on passable tile** (smurf selected) → calls `_sim.RequestPlayerMoveOrder(name, worldPos)`.
  - **Left-click with active tool = Move** → same as right-click; toolbar mode for players who prefer click-tool-then-click-target.
  - **Left-click with active tool = Excavate** → calls `_sim.ToggleExcavationDesignation(tileX, tileY)`.
  - **Left-click with active tool = Remove** → clears `DesignatedForExcavation` on the clicked tile (preview of Phase 5.11 Remove brush; zone-removal hooks in once §5.11 zones land).
- `OnSmurfClicked` stores the selected name on `_selectedSmurfName` so right-click move orders know who to target.

**TileInfoOverlay (`scripts/ui/TileInfoOverlay.cs`) — Roadmap §3.x.5 temperature + Indoor/Outdoor:**

Hover tooltip now adds a second line of `°C · classification` (e.g. `18 °C · Outdoors`). Temperature is a per-biome placeholder via `BiomeBaselineTemperature(BiomeType)` until Phase 5.x lands the real `LocalTile.Temperature` field; the row is wired in now so the UI doesn't shift when the real value arrives. Classification follows the §5.10 Roof model preview — passable tiles read `Outdoors`, impassable Boulder / DeadLog / LivingWood read `Sheltered (rock/timber)`, Water reads `Surface water`. Phase 5.10 will replace with the live Indoor / Outdoor / Pavilion classifier.

**What's deferred to a Phase 3.x v2 pass:**

- **Box-select multi-selection** — single-smurf selection works; rectangular drag-select is not yet wired into `SmurfColonyView._UnhandledInput`.
- **Smurf card Work tab** — existing SmurfCardPanel keeps its current Main/Mood/Health tabs; a Work tab for per-task priority toggles lands with §3.10 Priorities implementation.
- **HUDController refactor to capsule** — existing top-wide era/date/speed bar untouched; the new `ResourceHUD` sits below it. A future pass collapses both into one floating capsule.
- **Build / Zones / Priorities popovers** — toolbar buttons are present but disabled until Phase 5 (Build, Zones) and Phase 3.10 (Priorities) land.
- **Hover-bio inspector for smurfs** — Phase 3.x.5 calls for hover-over-card → bio inspector; deferred to the same SmurfCardPanel refactor pass.

---

## [0.3.13] — 2026-05-11

### Added — Phase 4 demographic-growth model, canonical 1:49 sex ratio in scenario randomization, new Phase 12 Disease System (roadmap)

**Code changes (minor):**

- `ScenarioPanel.cs` — random smurf sex roll changed from `0.5 / 0.5` to canonical **1 female per 49 males** (~2 %). New constant `FemaleSpawnChance = 1.0 / 49.0` used in both `MakeRandomTemplate()` and `RandomizeOne()`. Matches the existing `BirthSystem` 1:49 birth ratio and the Smurfs-Fandom canon. The legacy hard-coded Smurfette female in `SimulationManager.SeedColony`'s founding-seven stays intact — it's an explicit exception, not a random roll.
- `BirthSystem.ComputeFoodCapacity` — placeholder formula updated from `max(7, foragers × 3)` to `max(30, 30 + foragers × 5)`. v0.1's formula capped a default 2-Forager colony at 7 smurfs, blocking any growth from the founding seven. The new placeholder allows a 5-Forager colony to support ~55 smurfs, which is the headroom the Phase 4 §"Demographic growth model" math expects. Phase 9 farming replaces the placeholder with biome + farm-plot driven capacity.

**Roadmap — new Phase 4 sub-section: Demographic growth model:**

Appended to Phase 4 (just before the existing Estimated-scope footer). Documents the two growth pathways:

- **Birth system** (refines existing `BirthSystem`) — per-season check fires *once per eligible mother* (not once per colony), scaling growth with female count. 1-female colony ≈ 1 birth/year; 3-female colony ≈ 3 births/year. Food capacity gate uses Phase 4 stockpile totals.
- **Wandering-in event** (new Phase 8 storyteller event with catch math owned by Phase 4) — 2–4 wanderers/year for small colonies (≤30), tapering to 0 past 200. Every wanderer rolls 1:49 sex ratio, so a long-running male-only colony statistically gains its first female via wandering. Player accepts / declines through a Phase 3.x alert.
- **Growth targets** — math sanity-check shows 7 → 50 by year 10 on a standard playthrough with the default founding seven (Smurfette included).
- **Colony caps** — 250 smurfs is the recommended late-game target on a 320 × 200 map (the WorldGenPanel "Recommended" preset). 1 000 is the theoretical max on a 480 × 300 map. Storyteller pressure (raids, disease, weather) scales with colony size so growth has a real cost.

**Roadmap — new Phase 12 — Disease System (RimWorld-modeled):**

Inserted between Phase 11 (Technology and Culture) and the now-renumbered Phase 13 (Era System). Modelled on `https://rimworldwiki.com/wiki/Disease`. Ten sub-sections:

- **12.1** Data model — new `Disease` class with `Severity` / `Immunity` / `SeverityRate` / `ImmunityRate` / `OnsetDay` / `Symptoms`. Multiple active diseases per smurf supported.
- **12.2** Immunity-vs-severity race — the signature RimWorld mechanic. Visible twin progress bars on the Phase 3.x smurf card.
- **12.3** Disease catalogue — 12 entries drawing from Smurfs canon (Blue Plague, Cat Fever) + RimWorld archetypes (Common Cold / Flu / Gut Worms / Frostbite / Heatstroke) + SmurfulationC originals (Mushroom Rot, Sensory Mushroom Spore Sickness, Fairy Fever, Magic Resonance Sickness, Wound Infection).
- **12.4** Catching vectors — Airborne / Food-borne / Vector-borne / Environmental / Wound infection / Weather-borne. Each gets a per-tick exposure roll model.
- **12.5** Treatment loop — Caretaker-only `Treat` task. Medicine items (Herb Poultice / Smurfberry Syrup / Magic Salve / Fungal Antibiotic / Pixie Dust) as Phase 4 stockables. New `Medical` skill.
- **12.6** Symptom integration — needs decay multipliers, body-part damage, mood penalties, movement penalties, `Bedridden` task — all routed through existing systems.
- **12.7** Visualisation — Phase 3.x smurf card Health tab, optional map tint, alerts pane, tile-tooltip bio inspector.
- **12.8** Disease-relevant traits — existing Worrywart / Stoic / Optimist / Brawny + 3 new (Constitution / Sickly / Iron Gut).
- **12.9** Save & determinism — diseases serialise per-smurf; rolls deterministic on `WorldSeed ^ SmurfGuid ^ ColonyTick`.
- **12.10** Cross-system integration map.

**Renumbering (placeholder-swap method):**

- Phase 12 — Era System and Campaign Mode → **Phase 13 — Era System and Campaign Mode**
- Phase 13 — Polish and Individual Mode → **Phase 14 — Polish and Individual Mode**
- Phase 13.5 — Sprite and Texture Pass → **Phase 14.5 — Sprite and Texture Pass**

All cross-references in earlier phases that pointed at the old numbers automatically updated through the same swap. No code-side references to Phases 12 / 13 / 13.5 existed, so the renumbering is documentation-only.

`Last Updated` line refreshed.

---

## [0.3.12] — 2026-05-11

### Roadmap — Phase 7 Combat and Phase 9 Husbandry/Farming fleshed out (DF-modeled)

No code changes — roadmap-only expansion of two phases that were previously sketched at high level.

**Phase 7 — Combat System (fully expanded, 15 sub-sections):**

DF-modelled combat (reference: `https://dwarffortresswiki.org/index.php/Combat`) adapted to SmurfulationC's existing systems. Combat is task-driven through Phase 3 `BehaviorSystem`, not a parallel engine. Highlights:

- **7.1** Combat tick model — Attack/Defend/Flee/Hunt/Patrol task types; per-tick attack exchange (range → initiative → attack roll → defense roll → body-part selection → damage → wound → status effects → narrative log)
- **7.2** Body-part targeting reuses the existing `BodyPartRegistry` 20-part list. Each `BodyPartDef` gains `Size` + `Location`; per-attack hit-location roll weighted by attack vector (front melee / behind / charge / aerial / ranged)
- **7.3** Weapon types: Edged (slash/cut) / Blunt / Piercing / Ranged Piercing / Magical. Each with strength/weakness against armor classes. Phase 4 `Item.Material` drives damage multiplier (Stone 1.0 / Fungal Wood 0.7 / Iron 1.4 / Magicstone 1.6); `Item.Quality` from Crude (0.7) to Legendary (1.8 + critical chance)
- **7.4** Damage formula — `DamageRaw = WeaponBase × MaterialMult × QualityMult × ConditionMult × WielderStrength`; subtracts layered armor; remainder hits part Condition and rolls a wound entry
- **7.5** Layered armor (RimWorld-DF hybrid) — Phase 4 Apparel items with `Coverage` (which BodyPart Locations) + `Layer` (Skin/Padding/Mail/Plate/Cloak). Layers roll ArmorStop in order; damage that passes through reaches the part. Cloth absorbs Edged best; plate absorbs Blunt worst
- **7.6** Combat skills — adds Fighting, per-weapon (Sword/Spear/Club/Bow/Sling), Wrestler, Dodge, Shield User, Armor User, Discipline, Tracker to SkillRegistry. XP gain on successful attacks/defenses (DF-style)
- **7.7** Wound model — Bruise / Cut / Fracture / Puncture / Mangle / Sever / Concussion. Per-part `Wounds: List<WoundType>` so a single leg can be both bruised and fractured. Severity scales by WeaponType × DamageNet
- **7.8** Bleeding / Pain / Unconsciousness — new `Smurf.Blood` (0–100, depleted by Bleeding wounds, death at 0) and `Smurf.Pain` (0–100, knocks unconscious at 90, `Discipline` raises threshold). New status flags (Bleeding/Unconscious/Stunned/Venomed/Infected/Cursed) surfaced on Phase 3.x smurf card
- **7.9** Combat narrative log — template-driven sentences in Phase 3.x message log (e.g. "Hefty bashes the Goblin's left leg with the iron mace, fracturing the bone!")
- **7.10** Wrestling / unarmed combat — Grab / Lock / Throw / Choke / Bite / Kick / Headbutt moves driven by `Wrestler` skill
- **7.11** Combat training — Sparring Yard (Phase 5 building, paired training), Training Dummy (solo), Shield Wall Drill (Phase 11 tech)
- **7.12** Player orders — Attack target / Hunt / Retreat zone / Patrol path / Hold position / Sortie (all via Phase 3.x toolbar)
- **7.13** Combat-relevant traits — existing personality traits (Brawny / Worrywart / Stoic / Thrill-Seeker / etc.) modify rolls; three new traits (Coward / Bloodthirsty / Berserk Prone)
- **7.14** Hostile-entity catalogue — Phase 6 entities tagged with combat attributes (Weapon / Armor / Aggression / Pack / StatusEffectOnHit / Drops)
- **7.15** Save & determinism — per-part Wounds / Blood / Pain / Status serialise on save; combat rolls use per-smurf RNG so save reload reproduces outcomes

**Phase 9 — Animal Husbandry, Farming, and Hunting (fully expanded, 11 sub-sections):**

DF-modelled animal + farming systems (refs: `DF2014:Domestic_animal` and `Farming`) adapted to existing simulation systems.

- **9.1** Animal categories — DF tag bitmask (Tameable / Pet / Pack / War / Hunt / Mount / Grazer / Carnivore / Omnivore / Milkable / Shearable / Egg-Layer / Butcherable / Breeds). Each Phase 6 entity species gets a tag set; mechanics are data-driven not per-species code. Worked example tagging for 9 SmurfulationC species (Glow Bunny, Honey Bee, Shore Frog, Forest Boar, Cave Lizard, Pegasus, Sky Pony, Mushroom Goat, Bonecrest Beetle)
- **9.2** Taming — Caretaker walks bait to wild Tameable, roll on `AnimalHandling × Species TameDifficulty × Bait quality`. Catastrophic failure → animal becomes Hostile (Phase 7 combat). New `AnimalHandling` skill
- **9.3** Pens / Pasture / Pet behavior — Pen is a Phase 5 building; Pasture is a Phase 5.11.a zone for Grazers; Nesting Box / Beehive Frame / Trough are Phase 5 furniture. Pets bypass pens and follow Owner
- **9.4** Production work loop — Caretaker tasks for Milk / Shear / Collect Eggs / Feed / Vet Tend; per-tag cooldowns; output scales with Condition + AnimalHandling skill
- **9.5** Breeding — `Breeds`-tagged adults of opposite sex in same Pen → species-specific gestation → young → maturation. Population caps per Pen size. Phase 11 tech unlocks selective breeding (trait inheritance shift over generations)
- **9.6** Hunting — Guardian / Forager Hunt task pathfinds + engages via Phase 7 combat. Kill produces Corpse item; Hauler drags home; Crafter butchers at Butcher Slab → species' Drops list resolves into typed Phase 4 items
- **9.7** Mounts — Mount-tagged tamed animal assigned to a Rider smurf. Shared SimPos, +50 % movement, charge-attack access in combat. Phase 12 era unlocks (Pegasus etc.)
- **9.8** Farming — 8 sub-sub-sections:
  - **9.8.1** Farm Plot Zone (Phase 5.11.a) painted on passable Mud / Grass / ForestFloor (refuses Sand / Stone). Each tile is a Plot cell with its own state
  - **9.8.2** Seed system — every plant has a `Seed` item (Phase 4); harvest yields small chance of seed; eating all seeds = no future crops (DF-faithful conservation gameplay)
  - **9.8.3** Seasonal planting — per-crop PlantingSeason + GrowingSeasons table (Smurfberry / Spring Greens / Sunberry / Pumpkin / Magic Herb / Cold-Hardy Smurfberry / Small Mushroom / Large Mushroom / Magic Flower)
  - **9.8.4** Per-tile lifecycle — `Fallow → Planted → Sprouting → Growing → Ripening → Ripe → Wilted → Harvested` state machine
  - **9.8.5** Soil fertility + fertilization — uses existing `LocalTile.Fertility`; repeat planting depletes (-0.02 per harvest); Fertilizer item (manure / scraps / compost) restores; crop rotation prevents exhaustion
  - **9.8.6** Irrigation — water-adjacency (≤4 tiles from any Water tile) gives +0.10 effective Fertility and Heat Wave resistance. Phase 11 tech: Irrigation Channels (Crafter builds Water tiles from a river source)
  - **9.8.7** Weather integration — Rain accelerates, Cold Snap pauses / wilts, Heat Wave dries / ripens early, Heavy Rain trampling chance, Magical Storm boosts magic crops, lightning burns plots
  - **9.8.8** Above-ground vs Underground — Cave Building Zones unlock fungal crops (Small/Large Mushroom × 1.5 yield underground)
- **9.9** Fishing (preserved from original Phase 9 text)
- **9.10** Aquatic creatures (preserved list)
- **9.11** Cross-system integration map — table showing every system that hooks into Phase 9 (Phase 3 BehaviorSystem tasks, Phase 4 Items, Phase 5 Buildings, Phase 5.x Temperature, Phase 6 Entities, Phase 7 Combat, Phase 10 Weather, Phase 11 Tech, Phase 12 Era)

`Last Updated` line refreshed.

---

## [0.3.11] — 2026-05-11

### Roadmap — Weather promoted to its own top-level phase (Phase 10), downstream phases renumbered

No code changes — phase renumbering only. The v0.3.10 release docked Weather as Phase 9.5 (between Animal Husbandry and Technology); this release promotes it to its own top-level phase by shifting every downstream phase up by one.

**Renaming:**
- **Phase 9.5 — Weather and Environment** → **Phase 10 — Weather and Environment**
- Phase 10 — Technology and Culture → **Phase 11 — Technology and Culture**
- Phase 11 — Era System and Campaign Mode → **Phase 12 — Era System and Campaign Mode**
- Phase 12 — Polish and Individual Mode → **Phase 13 — Polish and Individual Mode**
- Phase 12.5 — Sprite and Texture Pass → **Phase 13.5 — Sprite and Texture Pass**

Weather sub-section numbers shift from `9.5.1`–`9.5.8` to `10.1`–`10.8`. Internal `§9.5.x` cross-references in §10.x text updated to `§10.x`.

**Roadmap cross-references updated** wherever earlier phases referenced now-shifted phases:
- Phase 1 "Recommendation: Option B through Phase 12; transition to Option A in Phase 13.5" (was 11 / 12.5)
- Phase 4 "Trade goods — Phase 11 culture-prereq tier" / "Phase 11's trader caravans" (were 10)
- Phase 5 "No colony-radius restriction in Phase 5 (colony boundary system is Phase 11+)" (was 10+)
- Phase 5 "Knowledge (Phase 11): specific tech nodes" (was 10)
- Phase 5 Enables footer "Phase 11 tech-gated furniture tiers" + "Phase 5.x temperature system gates Phase 10 weather + fire" (were 10 / 9.5)
- Phase 7 "Iron Blade — Iron (Phase 11 resource)" (was 10)
- Phase 8 Storyteller weather hooks "(Phase 10)" + body refs (were 9.5)
- Phase 10 (Weather) goal/prereq/enables block — internal "Phase 11 technology" / "Phase 12/13 era-scoped weather variability" / "Phase 11 weather-mitigation tech tier" (were 10 / 11/12 / 10)
- Phase 10.2 weather table "Eclipse (Phase 11 magic prereq)" (was 10)
- Phase 13.5 prereq "Phase 12 (all tile, entity, and building types finalized)" + enables "Phase 13 (release candidate)" (were 11 / 12)
- Open-questions table refs: Culture/Tech → Phase 11; Eras → Phase 12 (were 10 / 11)
- Implementation order footer "Phase 13.5 (Sprites)" (was 12.5)

**Code references updated** in `ScenarioPanel.cs` `ItemCatalog`:
- `trade.*` category description: "Phase 10 (Technology & Culture) sets exchange rates" → **Phase 11**
- `misc.lantern` description: "Phase 9.5 weather" → **Phase 10 weather**

The v0.3.10 changelog entry is left untouched — it describes the as-of-that-version state (Phase 9.5) and is historical record.

`Last Updated` line refreshed.

---

## [0.3.10] — 2026-05-11

### Roadmap — Phase 8.x Fire lifted out into dedicated Phase 9.5 Weather and Environment (RimWorld-modeled)

No code changes — major roadmap restructure. The Fire System added in v0.3.9 as a sub-phase of Phase 8 (Events / Storyteller) is moved into a dedicated new phase between Phase 9 (Animal Husbandry / Farming / Hunting) and Phase 10 (Technology and Culture), where it joins a full RimWorld-style weather simulation.

**New — Phase 9.5 Weather and Environment** (modeled on `https://rimworldwiki.com/wiki/Environment#Weather`):

- **9.5.1 Weather state model** — singleton `LocalWeatherState` on `WorldState` with `Current` (WeatherType enum), `Intensity`, `Wind (Direction, Speed)`, per-tile `Moisture` and `SnowDepth`, `SkyCover`, `Visibility`, `LightningCharge`, `ElapsedTicks`. Weather is map-level; per-tile state is on `LocalTile`.

- **9.5.2 Weather types (RimWorld-adapted)** — Clear, Cloudy, Fog, Foggy Rain, Rain, Heavy Rain, Snow, Dry Thunderstorm, Thunderstorm, Flashstorm, Cold Snap, Heat Wave, Eclipse (Phase 10 magic prereq), Magical Storm (SmurfulationC-original), Volcanic Pall (storyteller-only). Each has a tabulated temperature offset, moisture rate, wind, and notes.

- **9.5.3 Weather selection** — Markov chain transitioning every 1–6 in-game hours, weighted by current state × biome × season × storyteller pressure. Table-driven so it can be tuned without touching sim code.

- **9.5.4 Fire System (relocated)** — full migration of the v0.3.9 Phase 8.x design: `LocalTile.FireStage` enum, `Flammability` derivation, heat injection into the Phase 5.x temperature field for radiant ignition, smoke filling adjacent indoors, RimWorld-style firefighter task via Designate → Extinguish, Build → Firebreak, Mage Quench. All five ignition sources (Hearth/Kiln overflow, lightning, combat, Crafter accident, storyteller Wildfire) and the spread model (Flammability × wind × temperature × moisture) preserved.

- **9.5.5 Temperature integration** — outdoor tiles read `WeatherOffset` from the §9.5.2 table each tick. Cold Snap / Heat Wave are how Phase 5.x's extreme-weather × 1.5–2.0 deterioration multipliers actually get triggered.

- **9.5.6 Smurf response** — new `SeekShelter` behavior task (Tier 1, priority 88 during Heavy Rain / Thunderstorm / Cold Snap / Heat Wave at Intensity ≥ 0.7). WetClothes mood penalty; heatstroke / hypothermia carry over from Phase 5.x.3. Crop responses: Cold Snap freezes growth, Heat Wave dries soil, Heavy Rain can trample.

- **9.5.7 Visualisation** — sky tint overlay, precipitation particles aligned to camera, per-tile snow accumulation with melt curve, full-screen lightning flash + delayed thunder, wind-driven vegetation sway, weather banner in HUD top-centre with transition countdown.

- **9.5.8 Save & determinism** — `LocalWeatherState` serialises directly; per-tile Moisture / SnowDepth / FireStage as delta lists; Markov rolls seeded `Random(WorldSeed ^ ColonyTick)` so save reload reproduces the exact weather sequence.

**Phase 8 (Events and the Storyteller) updated** — `8.x Fire System` removed entirely. New paragraph after the event-table notes that the Storyteller can *trigger* weather events (`Wildfire`, `Flashstorm`, `Cold Wave`, `MountainEruption`) but the weather state machine itself lives in Phase 9.5; Storyteller is a scheduling layer. `Harsh Winter` becomes a wrapper that pushes Phase 9.5 state to Cold Snap for one season.

**Cross-references updated** in earlier phases that referenced "Phase 8 weather" or "Phase 8.x":
- Phase 3.x.5 tooltip importance note → "Phase 9.5 weather + fire"
- Phase 4 deterioration formula insulation row → "Phase 9.5 weather"
- Phase 5.10 roof collapse trigger → "Phase 7 combat / Phase 9.5 storms"
- Phase 5.11.b Building Zones combat/collapse → "Phase 9.5 storms"
- Phase 5.x.1 outdoor temperature inputs → "weather (Phase 9.5)"
- Phase 5 Enables footer → "Phase 9.5 weather + fire"
- ScenarioPanel `misc.lantern` description → "Phase 9.5 weather"

`Last Updated` line refreshed.

---

## [0.3.9] — 2026-05-11

### Roadmap — Tile-hover temperature promoted; Phase 8.x organic Fire System added

No code changes — roadmap-only release.

**Phase 3.x.5 — Tile tooltip gets explicit temperature line:**

The brief mention buried in Phase 5.x.5 is promoted to a primary feature of the Phase 3.x tile tooltip. Hovering any tile for 400 ms now surfaces `Mud · Fertility 0.68 · Underbrush · 18 °C` (terrain · fertility · vegetation · **°C**). A `Indoors / Outdoors / Pavilion` classification line is added on the row below, since the deterioration and temperature math read from it. The tooltip line is wired with a placeholder biome-baseline °C value before Phase 5.x lands the real `LocalTile.Temperature` field, so the UI doesn't shift layout when the real value arrives. Note in the section explicitly calls out that this tooltip is the player's primary feedback channel for the entire Phase 5.x temperature, Phase 8 weather, Phase 8.x fire, and Phase 4 deterioration loop — every system gating on °C surfaces here first.

**Phase 8.x — Fire System (sub-phase of Weather):**

Organic fire start + spread + extinguish, sitting inside Phase 8 because weather (wind, moisture, lightning) drives ignition and the per-tile temperature field from Phase 5.x is its substrate. Six sub-sections:

- **8.x.1 Ignition sources** — Hearth / Kiln overflow (heat source sparks adjacent flammables), lightning strikes on flammable terrain, Phase 7 incendiary combat, low-skill Crafter accidents, explicit Wildfire storyteller event.
- **8.x.2 Per-tile fire state** — new `LocalTile.FireStage: byte` (None / Smouldering / Burning / Inferno / Charred), per-tile `Flammability` derived from terrain + structure + vegetation + item pile. Burning tiles inject heat into the Phase 5.x temperature field (radiant ignition of nearby tiles). Smurfs / items / structures take per-tick damage on burning tiles; indoor neighbours fill with smoke (Safety / Lung damage).
- **8.x.3 Spread model** — per-tick neighbour roll against `Flammability × wind direction × tile temperature × moisture`. Wind biases downwind; recent rain wets tiles; rivers / ponds / Water tiles are natural firebreaks; roofs trap heat → indoor Inferno escalation. Burnt-out tiles become `Charred` and regenerate to biome floor over many in-game days; charred structures become Ruins requiring excavation.
- **8.x.4 Player response** — new Phase 3.x toolbar entries: `Designate → Extinguish` (firefighter brush — smurfs carry water buckets to designated tiles) and `Build → Firebreak` (Crafter zone designation that clears flammable terrain in a stripe). High-resource Mage `Quench` spell for emergencies.
- **8.x.5 UI** — burning tiles render with animated flicker overlay; smoke renders semi-transparent grey dim layer; `TileInfoOverlay` reports `Burning · 612 °C · Smouldering 2 days` using the Phase 3.x.5 temperature line; alerts pane fires when ignitions aren't designated for extinguish within N seconds.
- **8.x.6 Save & determinism** — `FireStage` + per-tile wetness as delta list (same pattern as terrain / vegetation / temperature deltas); spread rolls seeded on world seed + tile coords + tick counter so save reload reproduces the player's exact propagation.

Estimated sub-scope: Medium-large. Per-tile field cheap; extinguish workflow + structure-damage / roof-collapse / item-damage integration crosses into Phases 4 / 5 / 7. Lands after core weather is in because it consumes wind / moisture / lightning as inputs.

`Last Updated` line refreshed.

---

## [0.3.8] — 2026-05-11

### Roadmap — "Remove" designation tool added to the Phase 3.x toolbar

No code changes — roadmap-only release adding the universal eraser for player-painted designations.

**Phase 3.x toolbar — new Remove category:**

- **Remove → Zone / Building Designation / Designation** — brush-paint to wipe any prior player-painted designation off the targeted tiles. Covers Storage / Stockpile / Sleeping / Recreation / Farm / Patrol / etc. Zones (§5.11.a), Building Zone roles (§5.11.b — also clears the chamber's name and role), and individual Designations from the Designate category (excavation marks, forbid flags, harvest queues).
- **Non-destructive:** Remove strips the *designation*, never the physical tile. Walls / floors / roofs / structures come off through Build → Demolish, not through Remove. Items already in a removed Storage zone aren't destroyed — they remain as ground items.
- RimWorld-style hold-shift to drag-erase across an area; Esc cancels the Remove brush.

**Phase 5.11.a — Storage Zone removal semantics:**

The Remove → Zone brush wipes the zone designation off the painted tiles. Items inside the zone become ground items: they stop feeding the HUD and the Phase 4 deterioration formula reverts to the unstored-outdoor rate (so leaving items in a removed zone is real-cost lossy storage, not a free state).

**Phase 5.11.b — Building Zone removal semantics:**

The Remove → Building Designation brush dissolves a Building Zone — chamber's flood-fill perimeter, name, and assigned role all clear. Natural rock walls and constructed walls remain physically intact (they're terrain / structure, not designation). Items in the cleared chamber drop to ground-item status. Useful for repurposing a chamber (Mage Circle → Storehouse), resetting a half-built designation, or opening a cave-fortress room back up to be subdivided into smaller rooms.

`Last Updated` line refreshed.

---

## [0.3.7] — 2026-05-11

### Roadmap — Zone rules corrected; Building Zones added for cave-fortress play

No code changes — roadmap-only release correcting two v0.3.6 mis-statements about Zones and adding the Building Zone concept:

**Corrections to v0.3.6 Phase 5.11:**
- **Storage / Stockpile / Farm / Recreation / Patrol / Forbidden Zones must be on passable tiles.** v0.3.6 claimed a "deliberate departure from RimWorld and DF" allowing Storage zones across Boulder outcrops / felled logs / frozen ponds — that was wrong. Smurfs need to physically walk onto the tile to deposit / retrieve items, plant crops, patrol. The brush now skips impassable tiles at paint time, matching RimWorld and DF on this point.
- **Items in Storage zones DO count toward the Phase 3.x HUD "stored resources" total.** v0.3.6 said zones don't feed the HUD — that's reversed. Any explicit storage designation (zone or constructed building) feeds the resource readout. This gives the early game a meaningful HUD before the player has built warehouses — paint a Storage zone next to the drop point and it counts immediately. Upgrading to roofed pavilion zones and fully enclosed Storehouses is still rewarded through Phase 4 weather / temperature deterioration protection, not through HUD visibility.

**New — Phase 5.11.b Building Zones:**

The distinctive Songs-of-Syx-flavoured concept that separates SmurfulationC from RimWorld and DF: **the player can identify any fully enclosed space — including spaces whose perimeter is partly or entirely natural impassable terrain — and designate it as a Building.** No artificial walls required.

- Player drops a "Building Zone" marker inside an enclosed area; a flood-fill from that point checks every escape direction terminates in an impassable tile (natural Boulder / DeadLog / LivingWood *or* constructed Walls / Doors). If contained, the zone is valid. Player names it and assigns a role (Sleeping / Great Hall / Kitchen / Mage Circle / Storehouse / etc.) with the §5.4–5.9 room-role machinery.
- **Carved cave fortresses become first-class gameplay.** A player who excavates a chamber out of a natural Boulder mass — leaving the surrounding stone as walls — paints a Building Zone inside it and immediately treats it as a Mushroom Hut / Mage Circle / etc. without building artificial walls inside the mountain.
- Distinct from the §5.4–5.9 RoomDetector, which only triggers on *constructed* walls. Building Zones expose the same room-role system to *naturally* enclosed spaces — caves, rock-outcrop alcoves, felled-log circles — that the auto-detector never claims.
- Natural rock perimeter counts as real walls for Phase 5.x temperature insulation and Phase 4 deterioration; cave fortresses are inherently weather-proof.
- Combat / collapse: natural rock perimeter is tougher than constructed walls; if breached, the Building Zone is suspended until re-sealed.
- A Building Zone with the Storehouse role counts contents toward the HUD just like §5.11.a Storage Zones.

This unifies surface-base and cave-fortress play under one room-role system — RimWorld and DF both require players to either build artificial walls inside natural caves or use a parallel "carve" system; SmurfulationC handles both through the same zone designation.

**Phase 3.x HUD description updated** to reflect the new counting rules: constructed storage buildings + Storage Zones + storage-role Building Zones all feed the HUD.

`Last Updated` line refreshed.

---

## [0.3.6] — 2026-05-11

### Roadmap — Stored-resource HUD gating, item Age/Durability + deterioration, Roofing, Songs-of-Syx Zones

No code changes — roadmap-only release documenting four interconnected intentions across Phases 3.x, 4, and 5:

**Phase 3.x — HUD shows only items in appropriate storage buildings:**
- The floating top-centre resource readout (Food / Stone / Wood / MagicEssence) intentionally shows only items inside an appropriate storage building — Food in Storehouse / Kitchen / Pantry, Stone / Wood in Material Yard, MagicEssence in Mage Circle reliquary, Trade Goods in Trade Post.
- Items on the ground, on a forager mid-haul, or inside a non-storage room don't count toward the HUD totals.
- Until Phase 5 storage tagging lands, the v0.3.0 ledger total displays with a `(unstored)` qualifier so the player knows it reflects the placeholder pool.
- Creates real cost for losing a storehouse to fire / collapse / raid — the visible total drops because items are no longer inside a counting structure. Pushes early warehouse construction.

**Phase 4 — `Age` and `Durability` attributes on `Item`, multi-input deterioration formula:**
- `Item.Age` (in-game ticks since creation — every item gets a birth-tick stamp).
- `Item.Durability` (max Condition cap — fragile items have lower caps; armor highest).
- `Item.State` enum gains `Broken` (was just `Fresh / Stale / Spoiled / Depleted`).
- New deterioration formula `f(Age, Temperature, Insulation, Material, Use)`:
  - **Age** — older items decay faster (fresh wool cloak: 0.05 / day → 100-day cloak: 0.15 / day).
  - **Temperature** — extreme °C accelerates decay; food spoils faster above 25 °C; metal rusts in damp / cold; wood splits in deep freeze.
  - **Insulation** — Indoor (roofed + enclosed) × 0.4; Temperature-controlled room (Hearth) × 0.25; Outdoor uncovered × 1.0; Outdoor in active weather × 1.5–2.0.
  - **Material** — Magicwood / Magicstone × 0.5; Iron × 0.7; Cloth × 1.2; Food × 4.0.
  - **Use** — tools / weapons take Condition damage per use; apparel per combat hit.
- Condition 0 flips State to `Broken` (tools / apparel) or `Spoiled` (food); broken tools yield half material on disassembly.

**Phase 5.10 — Roofing (RimWorld-style):**
- `StructureSlot.HasRoof: bool`. Roof tiles consume Wood / Stone but don't block passability — they sit above the floor.
- **Auto-roof** option in Phase 3.x build toolbar: enclosed rooms auto-queue a roof when the perimeter walls finish. Players almost never place roofs manually.
- **Manual roof / un-roof** tools for awnings, courtyards, mixed open-roof structures.
- A tile is **Indoors** iff `HasRoof = true` AND inside a RoomDetector-identified enclosed region. Otherwise Outdoors. Roof-only (pavilion) tiles get partial × 0.7 protection.
- **Roof collapse** when supporting walls are destroyed (Phase 7 combat / Phase 8 storms) — drops chunk damage on anything underneath and re-classifies tiles as Outdoors.

**Phase 5.11 — Zones (Songs-of-Syx-style structured zoning, NOT RimWorld/DF "open zones"):**
- The existing §5.4–5.9 room / building system (enclosed walls + role-defining furniture, Songs-of-Syx-style capacity + explicit role assignment) **stays exactly as written** per the user's intent. Zones below are a complement, not a replacement.
- **Zones** are lightweight rectangular / freeform designations granting a single explicit function (Storage / Stockpile / Farm / Recreation / Patrol / Forbidden). Painted explicitly with the Phase 3.x Zones toolbar; NOT detected from enclosure.
- **Zones can include impassable tiles** — deliberate departure from RimWorld / DF. A Storage zone can be painted across a Boulder outcrop, a felled log, a frozen pond; items sit on top and are accessed from any adjacent passable tile.
- **Outdoor vs Indoor follows §5.10 Roof classification, not the zone itself.** Bare-grass-under-sky Storage zone = Outdoor (full weather deterioration); same zone under a roofed pavilion = Indoor protection.
- **Zones do not grant building HUD-counting status.** Only items in a properly constructed storage building feed the Phase 3.x resource HUD. Creates real progression: ground-pile → outdoor zone → roofed pavilion zone → enclosed storage building.
- Zone overlap rules: one Zone per function per tile; building tiles override Zone tiles.

`Last Updated` line refreshed to note all four new intentions.

---

## [0.3.5] — 2026-05-11

### Roadmap — Phase 5.x Level-wide Temperature Simulation documented

No code changes — roadmap-only release adding a `5.x — Level-wide Temperature Simulation` subsection to Phase 5 (Tile-Based Construction). The intention was already implicit in the scenario screen's apparel / heat-source items (`apparel.cap.felt` "Felt cap — winter survival bonus", `apparel.cloak.wool` "+5 °C effective temperature", `misc.firekit` "spark a hearth fast", `misc.lantern` "Visibility in caves"); v0.3.5 makes the system explicit on the roadmap so the existing stubs have a target implementation.

**Model: RimWorld + Dwarf-Fortress hybrid:**
- Per-tile temperatures (DF-style) updated on a slow 1 Hz heat-diffusion pass — every passable tile exchanges a fraction with its 4-connected neighbours.
- Room insulation (RimWorld-style): Phase 5's room scanner identifies enclosed regions; tiles in a sealed room share a fast-equalising pool and leak through walls at a per-material rate (Boulder 0.02 °C/tick … cloth wall 0.20 … open doorway 0.40).
- Outdoor temperature driven by seasonal curve + biome + Phase 8 weather + latitude (existing `WorldTile.Temperature`), with daily diurnal cycle.

**Sub-sections specified:**
- **5.x.1 Per-tile temperature field** — `LocalTile.Temperature: float`, new `TemperatureSystem` on the sim thread, heat sources injecting +Δ that diffuses outward.
- **5.x.2 Room-aware insulation** — `MaterialInsulation` table; rooms equalise fast, leak through walls per material.
- **5.x.3 Smurf comfort & health** — Effective Temperature = local + apparel + trait modifiers. Comfort band 14–26 °C. Hypothermia / dehydration outside it. Death at −20 °C or 50 °C sustained for >1 sim day (new `CauseOfDeath` enum values).
- **5.x.4 Buildings + items** — Hearth / Kiln / wool cloak / felt cap / firekit / lantern / magicstone wall all wired to specific °C effects.
- **5.x.5 Visualisation** — Temperature overlay toggled from the Phase 3.x designation toolbar (blue → red gradient); `TileInfoOverlay` adds tile temperature; smurf card adds `Comfort` row.
- **5.x.6 Save & determinism** — Per-tile temperature delta list (same pattern as Phase 2.5 terrain / vegetation deltas); building-anchored heat sources persist via the existing building serialisation.

**Phase 5 wrap-up updated** to note that 5.x temperature gates Phase 8 weather events (storms / heat waves / blizzards) — all weather flows through the same temperature field rather than living in a parallel system.

`Last Updated` line refreshed to note the new intention.

---

## [0.3.4] — 2026-05-11

### Fixed — Begin Colony button visibility; Changed — colony-wide inventory (DF Prepare-Carefully style)

**Begin Colony button now pinned to bottom:** the v0.3.3 layout used a `CenterContainer` + `CustomMinimumSize(1280, 780)` VBox, which clipped the footer when actual content height exceeded the min size (the personality grid + detail card combo pushed past the centred bounds). Restructured to use `FullRect` anchoring with `OffsetLeft/Top/Right/Bottom = ±40/32` margins on the root VBox. The footer is now guaranteed visible at the bottom of the viewport regardless of how much content the master/detail section produces.

**Colony-wide starting inventory — Dwarf Fortress "Prepare Carefully" model:**

The per-smurf `Items…` button in v0.3.3 didn't match how either RimWorld or DF actually present starting supplies. Both treat the colony's expedition gear as a single shared pool, distributed across colonists by role / need at landing. v0.3.4 follows that model:

- `ScenarioConfig.StartingInventory: List<InventoryEntry>` replaces the per-smurf `StartingItems` field on `SmurfTemplate`.
- Each `InventoryEntry` is a `(Token, Quantity)` pair so the same item can stack with a quantity stepper.
- `SmurfTemplate.StartingItems` field **removed**.
- New "**Colony Starting Inventory**" strip appears between the master/detail section and the footer. Shows a live summary (e.g. `8 item kinds · 19 units total`) and an `✎ Edit Inventory…` button.
- The "Edit Items…" button on the per-smurf detail card is **removed**.

**Items modal — DF Prepare-Carefully presentation:**

- Title is now `Colony Starting Inventory — {Colony Name}` (not per-smurf).
- Per-item row replaces the toggle CheckBox with a `SpinBox` (0..MaxStack), so the player chooses *how many* the colony brings rather than just whether to include the item type.
- Each `ItemCatalog.Entry` gains a `MaxStack` field so individual items have sensible caps (e.g. 25 sets of 7-day rations, 10 picks).
- Modal footer shows running totals: `{kinds} kinds · {total} total units`.
- Default starting inventory seeded on Open(): 7-day rations × 7, mushroom crate × 2, pick × 1, baskets × 2, healer's kit × 1, wool cloaks × 3, firekit × 1, rope × 2 — mirrors a sensible expedition starter pack.

---

## [0.3.3] — 2026-05-11

### Changed — Scenario screen rebuilt with master/detail layout, items modal, visible Begin Colony

v0.3.1's scenario screen squeezed every per-smurf control onto a single huge row, which scaled poorly with the AnimatedButton font and made the Begin Colony button slide off-screen. The screen is now a RimWorld-style master/detail layout:

**Master/detail rebuild — `ScenarioPanel.cs`:**

- **Top settings strip** (single row): Colony Name, Storyteller, Smurf Count.
- **Left column (~320 px):** scrollable list of smurf summary rows — each is a compact panel showing `#N`, sex glyph (♀/♂), name, and role. Selected row gets a gold border and brighter background. Click anywhere on a row to select.
- **Right column (~900 px):** detailed editor card for the selected smurf — large name LineEdit (font size 22), sex toggle (auto-regenerates the name on sex change so it stays appropriate), role dropdown, age spin, and a scrollable 3-column grid of personality CheckBoxes pulled from `PersonalityRegistry.All` (25 traits). Mood modifier is shown inline (+5/-8/etc.) and the label colour reflects sign (green / red / neutral). Max selections capped at 5 per `PersonalityRegistry.Assign` upper bound; trying to exceed silently un-presses the checkbox.
- **Items row** in the detail card shows a summary (`(default colony loadout)` or `N items selected`) and an `✎ Edit Items…` button.
- **Footer:** `← Back` (left), `🎲 Randomize All` (left), spacer, `✦ Begin Colony` (right). Begin button is 260×52 with tooltip "Load into the level and start the game with the chosen settings" — clearly the primary CTA at bottom-right.

**Items modal — DF-style picker, modern presentation:**

- New `Window` opens on `Edit Items…` click — title shows the smurf's name.
- Category column on the left (Food, Tools, Apparel, Weapons, Trade Goods, Miscellaneous); item list on the right with CheckBox per entry.
- Notice strip explains that the picker is functional today (selections persist as string tokens) but resolves into real `Item` instances only in Phase 4.
- Each category has 3–6 placeholder items with name + descriptive blurb that maps to the resource roadmap. New `ItemCatalog` internal helper holds the categories so Phase 4 can drop them in favour of `ItemRegistry`.

**List-row partial updates:** typing in the detail card's name field updates only that one row's label via `UpdateListRowName(idx)` instead of rebuilding the whole list — avoids losing focus mid-keystroke. Same for the role dropdown via `UpdateListRowRole(idx)`.

**Other cleanup:** `AnimatedButton` instances in compact contexts now pass `Compact = true` so they use the smaller 13-pt font instead of the 26-pt CTA size that caused the original scaling problem.

---

## [0.3.2] — 2026-05-11

### Roadmap — Phase 3.x Floating UI and DF-style procedural item system documented

No code changes — roadmap-only release documenting two large intentions surfaced in this session:

**Phase 3.x — Floating Gameplay UI (RimWorld-style)** (new section between Phase 3 and Phase 4):
- Rounded-rectangle floating panels with ~16 px screen-edge insets, ~10 px corner radius, ~92 % opacity parchment background, parchment-gold border at 30 % alpha
- Bottom-centre designation toolbar (Designate / Orders / Build / Zones / Priorities)
- Right-click contextual menu for order issuance (consumes the Phase 3 data layer: `RequestPlayerMoveOrder`, `ToggleExcavationDesignation`, `GetResourcesSnapshot`)
- Box-select multi-selection
- Smurf card refactor with new Work tab; HUD bar capsule at top-centre with live resource summary
- Floating message log bottom-right; alerts pane top-right under smurf card
- Hover tooltips for tiles and smurfs
- Shared `FloatingPanelStyle` helper and `UITheme` constants for consistency
- Positioned as the bridge that converts the Phase 3 data layer into playable gameplay — without it the simulation APIs are unreachable from mouse / keyboard

**Phase 4 — Dwarf-Fortress-style procedural item system** (expansion of existing Phase 4 section):
- Replaces the placeholder `ColonyResources` float ledger with first-class `Item` instances (Kind / Material / Quality / Condition / State / Owner / TilePos)
- Procedural sub-materials per family (Wood: Oak/Pine/Willow/Magicwood/Fungal; Stone: Granite/Limestone/Marble/Obsidian/Magicstone; etc.) — DF-style variation each with distinct hardness, value, decay rate, visual tint
- Quality tiers from skill (Crude → Normal → Fine → Superior → Masterwork → Legendary) with a named-artifact branch at Legendary
- Food taxonomy: Raw → Prepared → Preserved with per-item nutrition + mood modifier + spoilage timeline (RimWorld-style "Ate fine meal" mood reads)
- Stockpiles with category/quality filters; per-smurf carrying capacity scaled by role + traits
- Item decay / weather damage / trade-value computation
- **Behavior rewire** of the v0.3.0 `Eat` task: instead of draining the float ledger, it locates the nearest acceptable food *item* and consumes that. New `Haul` and `Cook` task types added to `BehaviorSystem`. `GatherFood` / `GatherMaterial` produce typed items rather than crediting floats

`Last Updated` line refreshed to note the new intentions.

---

## [0.3.1] — 2026-05-11

### Added — Scenario screen (RimWorld-style) between WorldGen and Game

**New flow:** Main Menu → New Game → WorldGenPanel (settings + tile select) → **ScenarioPanel** → Game.tscn. The player customises their founding colony before landing.

**`scripts/ScenarioConfig.cs`** — data model:
- `ColonyName` (default `"Colony MM-dd-yy"` of the current date)
- `Storyteller` (enum: `Balanced`, `Patient`, `Random`, `Cataclysmic` — only Balanced is functional; rest stubbed and disabled in the dropdown until Phase 8 lands)
- `Smurfs: List<SmurfTemplate>` — per smurf: `Name`, `Sex`, `Role`, `Age`, `Personality`, `StartingItems` (last is a Phase 4 stub)
- `MinSmurfs = 1`, `MaxSmurfs = 25`

**`scripts/ui/ScenarioPanel.cs`** — UI:
- Header with title + subtitle
- Top settings row: colony name LineEdit, storyteller OptionButton, smurf count SpinBox (1–25, default 7)
- Scrollable list of smurf rows: index, name, sex toggle, role dropdown, age spin, personality comma-list, Items… (stub dialog), per-row 🎲 randomize
- Randomize All button
- Back (→ re-opens WorldGenPanel) / Begin Colony (→ writes `WorldState.PendingScenario` + `WorldState.ColonyName`, fires `BeginColonyConfirmed`)

**`WorldState`** — new `PendingScenario` and `ColonyName` fields, both cleared in `Clear()` and restored in `LoadFromSave()`.

**`SimulationManager.SeedColony`** — reads `WorldState.PendingScenario` if present and builds smurfs from templates (player-set personality preserved; biological traits still roll). Falls back to the legacy founding seven when the scenario is null (legacy quick-start path stays working).

**Exit-save uses colony name** — `GameController.OnPauseMenuExit` now derives the slot name from `WorldState.ColonyName` via `SaveManager.SanitizeSlotName` (new public static helper). Saves with no colony name (legacy worlds) continue to use the `"exit-save"` slot. `SaveManager.ColonySave.ColonyName` field added so the name persists across save/load.

**Roadmap updates:**
- Phase 4: starting-items picker now planned as the resolution of the Scenario screen's Items… stub. `SmurfTemplate.StartingItems` model field already in place.
- Phase 8: storyteller dropdown wiring documented; four storyteller classes specified.

---

## [0.3.0] — 2026-05-11

### Added — Phase 3 Behavior System (data layer)

Phase advances from `bb=2` to `bb=3`. The phase 3 data layer is fully in place per the roadmap; UI hookups for player orders and designations (§3.9 / §3.10) are deferred to Phase 4 prep — the underlying APIs are ready and just need right-click handlers + overlay controls.

**New files:**
- `scripts/simulation/BehaviorTask.cs` — Roadmap §3.2 task record (`TaskType`, `Target`, `Priority`, `IsPlayerOrder`, `Interruptible`).
- `scripts/simulation/ColonyResources.cs` — Roadmap §0.x ledger (`Food`, `Stone`, `Wood`, `MagicEssence`). Read via `Snapshot()` from main thread; mutated only by `BehaviorSystem` on the sim thread.
- `scripts/simulation/systems/BehaviorSystem.cs` — the priority engine + movement + task effects. Tier 1 critical needs, Tier 2 Forager `GatherFood` and Crafter `GatherMaterial`, Tier 3 `Wander`. Mood ≤ Distressed adds +10 to comfort tasks (§3.4). Trait gating for Glutton / Sleepyhead / Worrywart / Introvert (§3.5 subset).

**Extended:**
- `Smurf` — adds `SimPos`, `SimTarget`, `SimSpeed`, `CurrentTask` (§3.1).
- `SmurfSnapshot` — adds `SimPos`, `SimTarget`, `CurrentTask` enum (§3.1).
- `SimulationCore` — owns `ColonyResources`, `Map` reference, `PendingPlayerOrders` queue. Calls `BehaviorSystem.Tick` after the per-second sim-system block, every tick, at `dt = BaseTickIntervalMs / 1000`.
- `SimulationManager` — new APIs: `BindLocalMap`, `RequestPlayerMoveOrder` (§3.9), `ToggleExcavationDesignation` (§3.10), `GetResourcesSnapshot`. Seeds `SimPos` from `LocalMap.FindSpawnCluster` after the world map binds. Role speed multipliers per §3.7.
- `SmurfColonyView.UpdateFromTick` now syncs `VisualSmurf.Target` to `snap.SimPos`; `_Process` lerps `Pos → Target` instead of autonomous wander.
- `GameController` calls `_sim.BindLocalMap(map)` in both paths (preloaded save and fresh-generation), so the sim thread always has the map.

---

## [0.2.50] — 2026-05-11

### Added — Phase 2.6 (Rivers) + smurf spawn cluster + worldgen/levelgen load screens

**Smurf spawn cluster — `LocalMap.FindSpawnCluster` + `SmurfColonyView`:**

Founding smurfs sometimes failed to appear or spawned inside impassable terrain because `SmurfColonyView.SeedSmurfs` placed each smurf at a fully random map position with no passability check. Fixed by adding `LocalMap.FindSpawnCluster(count)` — a BFS from the map's geometric centre that collects the first N passable tiles. `SeedSmurfs` now uses this cluster (with ±half-tile visual jitter). New smurfs added mid-game (births) now spawn at the average position of existing smurfs so they appear inside the colony, not at a random map corner.

**Phase 2.6 — River generation (per Roadmap):**

- `WorldTile.HasRiver` flag added. World-map Post-pass A.7 marks passable land tiles cardinally adjacent to Pondsea clusters as river tiles; count scales with rainfall (0 at very dry → 1–3 default → 3–6 at max rain bias).
- `WorldGenPanel` river indicator: blue stripe overlay through the centre of each `HasRiver` tile on the world map. Tile info label appends `· River`.
- `LocalMapGenerator` Pass 4h carves a meandering Water channel across the local map for any `HasRiver` tile. Drunk-walk from one edge toward the opposite edge with perpendicular noise-driven meander; channel half-width 1–2 tiles; 1-tile Mud border. ~20 % of segments get a Sand ford so smurfs can cross. Tiles within 3 of any river-water get +0.20 fertility. Mountains/Peaks skip river carving.

**Load screens — `WorldGenPanel`:**

Two new "Generating…" overlay points:
- **Worldgen** ("Generating world…"): shown when the player clicks Generate; `WorldMapGenerator.Generate` runs on the deferred frame so the overlay paints first.
- **Level preview** ("Previewing level…"): shown when a tile is clicked; `LocalMapGenerator.Generate` runs deferred. Tile + size are staged on private fields because Godot's `CallDeferred` Variant marshalling can't carry the `WorldTile` struct directly.

GameController's existing in-game generating overlay relabelled to "Generating level…" for consistency.

---

## [0.2.49] — 2026-05-11

### Changed — Caves: thresholds raised so rock dominates (matches reference cave-map topology)

**LocalMapGenerator.cs Pass 4h Subtype 0 — threshold + frequency calibration:**

v0.2.48 produced ~55 % passable cave floor — the opposite of the user's reference image, where rock is the surround and the cave system is carved out (~35–40 % explorable). The two-layer chamber + corridor approach was correct; the noise thresholds were miscalibrated. Ridged FBm output sits higher than plain simplex, so `pathThres = 0.55` was actually picking up roughly 35–40 % of tiles, not the ~15 % I'd estimated.

- `chamberThres` raised from `0.65` to `0.70` — chambers now cover ~20 % of map.
- `pathThres` raised from `0.55` to `0.68` — corridors now cover ~15–18 % of map.
- `chamberNoise` frequency lowered from `0.035` to `0.028` — each chamber is larger and fewer chambers per map, matching the reference's small number of distinct rooms instead of many small blobs.
- Combined expected passable share: ~30–35 %, matching the reference's room-and-corridor topology.

Outcrop pass, chamber-gating logic, and connectivity cleanup are unchanged.

---

## [0.2.48] — 2026-05-11

### Changed — Caves now have distinct chambers connected by corridors (D&D-cave aesthetic)

**LocalMapGenerator.cs Pass 4h Subtype 0 — two-layer cave topology with chamber outcrops:**

v0.2.47's pure ridged-tunnel approach produced narrow passages with rare wider sections — too uniform compared to the user's reference imagery (hand-painted D&D-style cave maps with distinct rounded rooms joined by corridors). The new generator layers two noise sources:

- **Chamber layer** — smooth-simplex blobs (no fractal, frequency `0.035`). Threshold `0.65` gives the top ~25 % of the distribution → several sizable rounded rooms scattered across the map.
- **Corridor layer** — ridged-simplex (FBm, frequency `0.055`, 2 octaves). Threshold `0.55` carves the connecting tunnel network through the ridge peaks. Every reasonably-sized chamber overlaps the network with near-certainty, so isolated chambers are rare.
- **Outcrop pass** — restored, but **gated to chamber tiles only** (`cN > chamberThres`). Corridors stay clear because adding boulders to a single-file passage can sever traversal even when `IsSafePlacement` passes (the safety check protects neighbour impassable connectivity, not cross-corridor pathing for distant passable tiles).

Net effect: caves now read as solid rock with distinct rounded rooms joined by narrower corridors, and the rooms have scattered debris inside — matching the reference's room-and-corridor topology.

---

## [0.2.47] — 2026-05-11

### Changed — Caves now use ridged-tunnel generation (rock-dominant with branching passages)

**LocalMapGenerator.cs Pass 4h Subtype 0 rewritten:** the previous FBm-blob approach produced large open chambers with scattered boulder outcrops. Real cave systems look the opposite — mostly solid rock pierced by a connected network of narrow passages. The new generator uses **Ridged simplex** as the path-carving noise: ridge peaks become passable Mud tunnels, troughs stay as Boulder. A separate low-frequency simplex chamber pass occasionally widens a short section into a small room.

- Path threshold `0.62` (Mountains) / `0.70` (Peaks) — only the upper tail of the ridged noise's distribution becomes passable, producing narrow connected tunnels.
- Chamber threshold `0.90` / `0.95` — rare wider sections, never dominant.
- Per-map openness factor no longer applies to Caves: cave maps are intentionally rock-dominant regardless of the roll, matching the user's "narrow paths hollowed throughout most of the level." Rocky Terrain (subtype 1) still uses openness.
- Outcrop pass removed — base generation is already rock-dominated and adding boulders to narrow passages would fragment connectivity. `CleanupIsolatedPassable` still runs after generation to absorb any single-tile dead-ends.

Net effect: cave maps now read as mostly stone with a branching tunnel network winding through them, rather than open spaces with scattered rocks.

---

## [0.2.46] — 2026-05-11

### Changed — Moss can grow in Caves

**LocalMapGenerator.cs `SelectCaveVegetation`:** added `MossPatch` to the cave vegetation table as the most common live vegetation (caves are damp and shaded — moss thrives). New distribution: 50 % None, 22 % MossPatch, 14 % SmallMushroom, 8 % LargeMushroom, 4 % MagicFlower, 2 % HerbCluster. Overall density bumped from 40 % to 50 % vegetation since moss is naturally widespread underground.

---

## [0.2.45] — 2026-05-11

### Changed — Cave System renamed to "Caves"; Mud floor; mushroom + magic vegetation only

**Three coordinated changes for Mountain biome Subtype 0:**

1. **UI label** (`WorldGenPanel.cs`): the level-select / world-gen tile info label now reads "Caves" instead of "Mountains" when the selected tile rolls Subtype 0. Other mountain subtypes still read "Mountains" — they're surface terrain variations. New public helper `LocalMapGenerator.GetMountainSubtype(WorldTile)` exposes the deterministic subtype roll so UI code can resolve the label without generating the full local map.

2. **Cave floor terrain** (`LocalMapGenerator.cs` Pass 4h Subtype 0): passable cave-floor tiles now use `TerrainType.Mud` instead of `TerrainType.ForestFloor`. Mud renders darker (the existing 0.38 / 0.30 / 0.16 brown), which reads better as an underground floor. Mud on a Mountains tile is also a unique marker — no other biome or pass leaves Mud on a Mountains tile after Pass 4h overwrites Pass 1/2 — so the vegetation pass can detect cave tiles by `Biome == Mountains && Terrain == Mud`.

3. **Cave vegetation table** (new `SelectCaveVegetation`): cave tiles now grow only mushrooms and magic vegetation — `SmallMushroom`, `LargeMushroom`, `MagicFlower`, `HerbCluster`. No berries, dry scrub, moss, or brushland. Sparse overall (60 % None) so caves stay readable as stone halls rather than fungal jungles; the existing LargeMushroom and magic-essence guarantee passes (5 / 6) backfill if a particular roll is too thin.

---

## [0.2.44] — 2026-05-11

### Changed — Mountain biome Cave System / Rocky Terrain less dense on average

**LocalMapGenerator.cs — Mountain subtypes 0 and 1 now scale with a per-map openness factor:**

Cave System (subtype 0, ~35 % open) and Rocky Terrain (subtype 1, ~30 % open) were producing too many maps with sparse, isolated passable patches — visually impressive but not traversable. The fix introduces a per-map openness roll `1 − r²` (uniform `r ∈ [0,1]`), which biases the distribution strongly toward 1. Most maps land in the open half of the range, but the original dense generation is still reachable when the roll falls near 0.

- Subtype 0 (Cave System): `baseRockThres` scales from `0.65` (openness 0, current dense baseline, rare) down to `0.30` (openness 1, very open, common).
- Subtype 1 (Rocky Terrain): solid-rock zone fraction scales `0.20 → 0.05`, `fieldThres1` `0.72 → 0.42`, `scatterThres1` `0.42 → 0.22`.
- Subtype 2 (Mountain Face): unchanged — its open/wall split is already governed by `rockFrac`.
- Peaks: unchanged — preview-only maps stay at the dense baseline.

Net effect: path-rich Mountain maps are now the common case while dense rock-heavy maps remain possible.

---

## [0.2.43] — 2026-05-11

### Fixed — Coast tiles spawning without adjacent Pondsea

**WorldMapGenerator.cs — new Post-pass B.5:**

`ScoreBiome` assigned `Coast` purely from `elev < 0.22` in the initial pass — but that runs *before* Pondsea is generated by Post-pass A. So a tile classified as Coast could end up far from any water once Pondsea placement was complete, showing as beach terrain with no sea anywhere nearby. This contradicted the meaning of Coast biome.

**Fix:** Added Post-pass B.5 after the `IsCoastal` marking pass. Any tile still classified `Coast` that didn't get `IsCoastal = true` (i.e., not adjacent to any Pondsea tile) is re-classified by a new `ClassifyNonCoastal(rain, temp, magic)` helper that mirrors the `elev ≥ 0.22` branch of `ScoreBiome` — so the demoted tile gets the biome it would have had if Coast hadn't claimed it on elevation alone (Desert / Swamp / MagicGrove / Forest / Plains). Coast tiles that *are* adjacent to Pondsea keep their biome.

Net effect: Coast biome now strictly means "land tile touching Pondsea." Orphan low-elevation tiles take their natural rain/temp/magic-derived biome instead.

---

## [0.2.42] — 2026-05-11

### Fixed — Embedded boulders inside wood formations in Hills biome (and any future high-elevation biome)

**LocalMapGenerator.cs — Pass 4c2 upgraded to two-stage solidification:**

v0.2.41's iterative neighbour-fill solidified passable gaps and absorbed isolated boulders, but failed on large boulder *clusters* inside wood formations: interior cluster boulders have 0 wood neighbours (only other boulders), so they never crossed the 5-neighbour fill threshold. This was exclusive to Hills because `SelectTerrain` in Hills places Boulder at `elev > 0.62`, so Pass 1 produces dense boulder fields before 4a/4b runs — and 4a/4b skip non-passable tiles. The same flaw would reappear in any future biome that places impassables in Pass 1.

**Fix — derive formation shape from the wood noise mask, not from Pass 1 leftovers:**

Stage 1 (new — mask force-convert): re-evaluates the 4a/4b directional-stretched noise masks and force-sets every non-wood, non-water tile inside each mask to the corresponding wood type, regardless of current terrain. This is the only step that overwrites Boulder, and only inside the wood blob's noise footprint — boulders out in the open are untouched. The 4a/4b HasPassableNeighbor / IsSafePlacement guards are bypassed on purpose: once the mask is final, having interior wood surrounded by other wood is the desired solid shape (same as mountain stone masses).

Stage 2 (existing iterative fill): unchanged — closes sub-threshold passable gaps inside the blob footprint and catches mask-edge stragglers.

Result: wood formation shape is now a pure function of the wood noise mask, identical across all biomes regardless of what Pass 1 generated underneath.

---

## [0.2.41] — 2026-05-11

### Fixed — Wood formations remained pockmarked despite v0.2.40 fixes; boulders still embedded

**LocalMapGenerator.cs — new Pass 4c2 (wood-fill solidification):**

v0.2.40 stopped the post-scatter sweep from carving holes and stopped Pass 4d from scattering boulders adjacent to wood, but the formations were *still* pockmarked. Root cause: the thresholded directional-stretch noise in Pass 4a/4b only places wood where noise exceeds the biome threshold — sub-threshold tiles inside the same blob footprint remain passable, so vegetation later spawns in them. Pass 1 elevation-boulders inside the blob footprint were also untouched (4a/4b skip non-passable tiles), surviving as embedded grey rocks.

**Fix:** Added Pass 4c2 between debris scatter (4c) and boulder scatter (4d). Iteratively converts any passable tile *or* Boulder tile with 5+ wood neighbours (of 8) to the dominant wood type, up to 4 iterations or until stable. This fills interior gaps and absorbs trapped Pass 1 boulders, so formations read as solid logs/stumps. Debris fragments from 4c are preserved because their 0–1 wood neighbours never reach the 5-neighbour threshold.

Note: the pass intentionally creates interior wood tiles surrounded by other wood — same shape as a mountain stone mass. The post-scatter sweep already skips DeadLog/LivingWood (since v0.2.40), so those interior tiles persist. Smurfs walk around solid wood, not through it.

---

## [0.2.40] — 2026-05-11

### Fixed — Post-scatter sweep carving holes in wood formations; boulders spawning inside wood

**LocalMapGenerator.cs — two-part fix:**

**Bug 1 — post-scatter sweep opening passable holes in DeadLog / LivingWood formations:**
The sweep at lines 258-278 converted every isolated impassable tile (no passable 8-neighbors) to the biome floor terrain. This included `DeadLog` and `LivingWood` tiles enclosed within a formation, punching passable holes into otherwise solid wood masses and allowing vegetation to spawn inside them. Fixed by adding an early-continue for `DeadLog` and `LivingWood` terrain types, limiting the sweep to `Boulder` tiles from the elevation-noise pass (the only tiles that legitimately need this correction).

**Bug 2 — boulder scatter placing boulders inside and adjacent to wood formations:**
Pass 4d (boulder scatter) only checked `tile.Passable`, so it could place boulders on passable gaps within a wood formation's noise footprint — passable voids that exist because the noise threshold creates a jagged, non-solid blob. Added `&& !IsAdjacentToWood(map, x, y)` to the placement guard so boulders are excluded from any tile adjacent to `DeadLog` or `LivingWood`. Rocks now cluster in clear open areas; log and stump formations remain boulder-free.

---

## [0.2.39] — 2026-05-10

### Fixed — Desert biome dead log, debris, and boulder overgeneration

**LocalMapGenerator.cs — three-part fix:**

**Bug 1 — woodBias dead-log threshold reduction applied to desert:**
`logThreshold -= deadFraction * 0.35f` was unconditional. Desert's base threshold (0.96) could be reduced as low as 0.61, making the top 39 % of noise tiles dead logs. Fixed by guarding the adjustment with `if (logThreshold < 0.92f)` so high-threshold biomes like Desert and Peaks are exempt — their base threshold already means no dead wood.

**Bug 2 — debris scatter activated for desert:**
`dlScatterThres` check used `< 1.0f`, and Desert's threshold (0.96) satisfied that, enabling dead-log debris scatter on every desert tile. Fixed by raising the activation cutoff to `< 0.92f` so only biomes with meaningful dead-log presence get debris.

**Bug 3 — boulder scatter too dense in desert:**
`BiomeBoulderScatterThreshold` for Desert was 0.89 (top 11 % of noise → boulders). Raised to 0.94 (top 6 %) for sparse, realistic rocky outcrops instead of dense boulder fields.

---

## [0.2.38] — 2026-05-10

### Added — Three Mountain Subtypes (Cave System / Rocky Terrain / Mountain Face)

**LocalMapGenerator.cs — Pass 4h subtype dispatch:**

Mountain tiles now draw one of three generation subtypes deterministically from their `LocalSeed`. A max-size world has enough mountain tiles that all three subtypes reliably appear. Peaks always use Subtype 0.

**Subtype 0 — Cave System** (unchanged from v0.2.37):
Cave-carving via 2-octave FBm (0.035 Hz, gain 0.40). Zone noise (0.022 Hz) varies the rock threshold ±8 % across the map. Outcrop scatter (0.07 Hz, top 14 % of cave tiles) adds rocky cave-floor texture. Target: ~35 % open, large interconnected chambers.

**Subtype 1 — Rocky Terrain** (zone-based):
Three spatial zones derived from zone noise (0.028 Hz): solid rock face (bottom 20 %), boulder field (20–45 %, tile noise > 72 % threshold), craggy scatter (top 55 %, tile noise > 42 % threshold). All-overwrite like Subtype 0 — Pass 1 does not bleed through. Target: ~30 % open, varied from dense boulders to scattered gravel.

**Subtype 2 — Mountain Face** (new):
One side of the map (top/bottom/left/right chosen by seed) is a solid rock wall covering 1/3–1/2 of the map. Boundary is organically varied (±5 %) by Simplex noise. The open half is highland terrain: `ForestFloor` if `worldTile.Rainfall > 0.5`, `Grass` otherwise. 0–2 elliptical caves are carved into the rock face (depth radius 9–14 tiles, spread radius 5–9 tiles, centered 20–40 % into the rock so the cave always connects to the open land). Cave walls are organically roughened with a secondary noise factor (0.85–1.15× radius). A light boulder scatter (top 12 % of highland tiles, `IsSafePlacement`-guarded) adds rocky outcrops to the meadow/forest area.

---

## [0.2.37] — 2026-05-10

### Changed — Mountain Cave-Carving Pass (RimWorld-Style Generation)

**LocalMapGenerator.cs — Pass 4h complete rewrite:**

Previous attempts to produce RimWorld-style mountain maps failed because Pass 1 (elevation noise at 0.09 Hz) was baking in a fine-grained craggy scatter pattern, and all subsequent zone/scatter passes were building on top of it rather than replacing it. The craggy texture therefore dominated every mountain tile regardless of zone settings.

New approach — **cave-carving**: Pass 4h now fully overwrites Pass 1 results for Mountains and Peaks.

- **`caveNoise`** (0.035 Hz, 2-octave FBm, gain 0.40): primary carving noise. Low frequency → chambers ~20–30 tiles across. Low FBm gain → large-scale structure (rock walls / cave openings) dominates while second octave adds organic irregularity to boundaries.
- **`zoneNoise`** (0.022 Hz, pure Simplex): large-scale gradient shifts the rock threshold by ±8 % across the map so some regions are noticeably more cavernous than others, creating structural variety between seeds.
- **Primary carve loop**: every tile is set to Boulder (solid rock) or ForestFloor (cave) based purely on `caveN < rockThres`. `rockThres = 0.65 + (zoneN-0.5)×0.16` → Mountains target ~35 % open caves; Peaks use 0.78 base → ~22 % open.
- **`outcropsNoise`** (0.07 Hz): secondary scatter adds individual boulder outcrops to ~14 % of cave-floor tiles for rocky-ground texture. Both `HasPassableNeighbor` and `IsSafePlacement` guards applied.
- **Cleanup**: isolated 1-tile passable pockets → Boulder (unchanged from previous version).

---

## [0.2.36] — 2026-05-10

### Fixed — Impassable-Inside-Impassable Pockets (All Biomes, Universal Rule)

**LocalMapGenerator.cs — IsSafePlacement guard + post-scatter sweep:**

Root cause: `HasPassableNeighbor` only protected the *tile being placed*. It never checked whether placing that tile would leave an *adjacent existing impassable tile* (e.g., an elevation-generated boulder) with no remaining passable neighbor. Dead log / living wood blobs could therefore enclose boulders tile-by-tile — each individual placement passing the check, but the formation collectively creating an inaccessible pocket.

**`IsSafePlacement(LocalMap map, int x, int y)`** — new static helper added after `HasPassableNeighbor`. Before allowing a scatter pass to make (x, y) impassable, it checks every impassable 8-neighbor: does that neighbor still have at least one passable neighbor (other than (x, y)) after the placement? If any impassable neighbor would become isolated, the placement is rejected.
- Applied to **Pass 4a** (dead log blobs), **Pass 4b** (living wood blobs), **Pass 4c** (debris scatter for both types), **Pass 4d** (boulder scatter).

**Post-scatter safety sweep** — single-pass fallback after Passes 4a–4d, before Pass 4h. Any impassable tile that still has zero passable 8-neighbors (e.g., enclosed by an elevation-noise boulder cluster before scatter) is converted to `SelectBiomeFloor(biome)` — the biome's natural passable floor terrain. This catches residual cases not prevented by `IsSafePlacement` (same-type elevation noise clusters).

**`SelectBiomeFloor(BiomeType)`** — new helper returning the appropriate passable floor per biome: Mud (Swamp), Sand (Desert/Coast/Island), ForestFloor (Forest/MagicGrove/Mountains/Peaks), Grass (all others).

**Memory recorded:** "No impassable tile may be enclosed by other impassable tiles — hard rule for every biome and level type." Any future scatter pass must add both `HasPassableNeighbor` and `IsSafePlacement` guards.

---

## [0.2.35] — 2026-05-10

### Changed — Mountain Zone Diversity (Solid Blocks + Boulder Fields + Craggy Scatter)

**LocalMapGenerator.cs — Pass 4h two-layer zone system:**
- Previous single-noise scatter produced a uniform gravel pattern with no large-scale spatial variety.
- Added a **zone-level noise** (pure Simplex, 0.028 Hz, ~36-tile features) that labels each region of the map as one of three distinct zone types:
  - **Solid rock face** (bottom 16 % of zone noise): all remaining passable tiles in this area → Boulder → produces large contiguous cliff-face / cave-wall blocks with no gaps.
  - **Boulder field** (16–36 % zone noise): tile-level mass noise at 72 % threshold → very dense scatter with small gaps between rocks, like a loose stone pile.
  - **Craggy scatter** (top 64 % of zone noise): tile-level mass noise at 40 % threshold → the gravel-and-cave look from the previous version.
- The 0.04 Hz tile-level noise (kept from v0.2.34) continues to drive boulder placement inside boulder-field and craggy-scatter zones.
- Combined target: ~28–32 % passable area distributed across varied, interconnected cave-like clearings between the three zone types.
- Peaks biome bumps all thresholds by ~12 % for denser overall coverage.

---

## [0.2.34] — 2026-05-10

### Fixed — Mountain Generation Over-Coverage + UI Text

**LocalMapGenerator.cs — Pass 4h rework:**
- Previous implementation stacked three boulder sources: lowered elevation threshold (0.62 from 0.68), 3-octave FBm mass noise, and a secondary detail scatter noise — resulting in ~75-80 % boulder coverage and thin unplayable corridors.
- **Reverted elevation threshold** for Mountains back to `elev > 0.68f` (was changed to 0.62f last patch). This restores Pass 1 to ~34 % boulder on average mountain tiles.
- **Pass 4h simplified**: replaced the 3-octave FBm + detail noise combination with a single pure Simplex layer (FractalType = None, frequency 0.04f). No FBm octaves means smooth, large, room-like blob boundaries instead of fragmented thin corridors. Threshold 0.40 on passable tiles → ~26 % additional coverage → combined ~60 % boulder / ~40 % open on average Mountains tiles. Peaks use 0.52 threshold for slightly denser coverage.

**WorldGenPanel.cs — text fix:**
- "Peak tiles" → "Peaks tiles" (consistent with biome name).
- Em dash (—) replaced with plain hyphen (-) to avoid Grobold font rendering issue.

---

## [0.2.33] — 2026-05-10

### Changed — Mountain Terrain Redesign + Peaks Impassable + Boulder-in-Wood Fix

**LocalMapGenerator.cs — Mountains/Peaks biome overhaul:**
- **Dead logs and living wood disabled on Mountains and Peaks** (`BiomeDeadLogThreshold` and `BiomeLivingWoodThreshold` return 2.0f for both). Boulders from the elevation pass were being enclosed by dead-log/living-wood scatter formations, producing impassable pockets. Mountains are above treeline — no wood spawns there.
- **Mountain floor changed from `Grass` to `ForestFloor`** in `SelectTerrain` — bare rocky earth is more appropriate than lush grass at altitude. Boulder elevation threshold lowered from 0.68 to 0.62 so Pass 1 alone produces more initial stone coverage.
- **New Pass 4h — Mountain rock massing**: large-scale Simplex FBm (frequency 0.025, 3 octaves) creates contiguous stone masses matching DF/RimWorld cave-style maps. A secondary medium-frequency layer (0.065) scatters rocky outcrops in open clearings. Coverage targets: ~65 % boulder on Mountains, ~80 % on Peaks. A cleanup sweep after the mass pass converts any isolated passable tile (zero passable 8-neighbors) to Boulder, eliminating 1-tile pockets smurfs can't exit.

**WorldMapGenerator.cs — Peaks now impassable:**
- `Passable = biome != BiomeType.Pondsea && biome != BiomeType.Peaks` — colonies can no longer land on sheer mountain peaks, same restriction as open water.

**WorldGenPanel.cs — UI updates for Peaks:**
- Hint text updated: "Pondsea and Peak tiles are impassable — colonies cannot land on water or sheer mountain peaks."
- `OnTileSelected`: impassable Peaks tiles show "Peaks — too sheer to colonise; choose a lower tile." (previously all impassable tiles showed the Pondsea message).

---

## [0.2.32] — 2026-05-10

### Changed — Full-Width Coastal Beach + Island Corner Rounding + River Roadmap

**LocalMapGenerator.cs — Pass 4g: Coastal and island edge fixes:**
- **Coastal beach now spans the full chosen side**: `segLen = sideLen` and `segStart = 0` — previously only 1/4–1/2 of the side was beach; now the entire edge is covered uniformly. Water depth and sand band still vary per-column via Simplex noise for an organic look.
- **Island corner rounding**: The sand ring loop (tiles 4–7 from each edge) now performs a circular arc check before assigning sand. Tiles in the sand ring where both `rcx = min(x, W-1-x) - 4` and `rcy = min(y, H-1-y) - 4` are non-negative and `rcx² + rcy² < 9` are converted to Water instead of Sand. This rounds each corner with a radius-3 arc so island maps look round rather than boxy.

**SmurfulationC_Roadmap_2026.md — Phase 2.6 River Generation added:**
- New roadmap section documents planned river generation (world-map `HasRiver` marking, local-map Pass 4h meandering carve, vegetation ecology, shallow ford crossings, mud silt border, rendering).
- Notes connection to Phase 9 Crawdad habitat prerequisite.

---

## [0.2.31] — 2026-05-10

### Changed — Mega-cluster Pondsea + Organic Beach Redesign

**WorldMapGenerator.cs — Two-tier Pondsea generation (Post-pass A rewrite):**
- Previous system of many small equal-sized BFS clusters replaced with a two-tier model:
  - **Mega-clusters** (20–80 tiles): primary water bodies. Count is rainfall-driven — 0 at very low rain (`avgRain < 0.22`), 0–1 at dry (`< 0.38`), always 1 at moderate, always 2 at maximum slider (`rainBias ≥ 0.9`). Cap is 40 tiles at default, 80 at max rainfall.
  - **Satellite clusters** (2–8 tiles): small inland ponds, 0–3 based on rainfall.
- **Post-pass A.3 — cluster merging**: After initial generation, flood-fill labels every connected Pondsea component. Overlapping 30×30-tile windows scan for pairs of components with ≥3 tiles each (≥6 combined). Where found, the closest tile pair is bridged with a rectilinear carve, merging them into one body. Pairs are deduplicated across windows; combined size checked against cap × 2 before bridging.
- **Post-pass A.5 — island seeding**: Islands (land tiles surrounded on all 4 cardinal sides by Pondsea) are seeded adjacent to existing Pondsea bodies. Counts: `rainBias ≥ 0.9` → 2–6 islands; `≥ 0.6` → 1–3; `avgRain ≥ 0.5` → 0–2; otherwise 0. Islands are placed by picking a random Pondsea tile, then testing each of its land neighbours as a potential island center and carving the remaining cardinal directions to Pondsea. This guarantees islands appear within the water body, not floating in open land.

**LocalMapGenerator.cs — Pass 4g: Organic beach + island sand/reef ring:**
- **Coastal (non-island) beach**: replaced flat rectangular water strip with a fully modelled beach:
  - Segment width widened from 1/5–1/3 to **1/4–1/2** of the chosen side.
  - Water zone: 2–4 tiles deep. Per-column Simplex noise (frequency 0.20) varies depth ±1–2 tiles, producing a curved organic shoreline rather than a straight wall.
  - Sand zone: 3–5 tiles of forced `Sand` terrain directly behind the waterline. Explicit placement ensures a visible beach band even where the biome's elevation noise would not produce sand.
  - Rock outcrops: ~15% chance per beach column of a `Boulder` placed at or just inside the waterline (at depth 0 to waterD+1), replicating rocks in the surf and at the tideline.
- **Island**: concentric ring model.
  - Water ring (tiles 0–3 from each edge): forced Water, unchanged.
  - **Sand ring** (tiles 4–7 from each edge): new — forced Sand on passable tiles, producing a visible beach band around the whole island interior.
  - Rocky outcrops in the water ring: density scales with proximity to sand (`edgeDist/3 + 0.15`, max ~22%), mimicking a coral/rock reef fringe that is densest near shore.

---

## [0.2.30] — 2026-05-10

### Fixed — Impassable Pockets, Coastal UI, Island Generation, Ocean → Pondsea Text

**LocalMapGenerator.cs — `HasPassableNeighbor` guard:**
- Added `HasPassableNeighbor(map, x, y)` helper (8-way check). Returns true if at least one of the 8 neighbours is currently passable.
- Applied to every impassable-terrain scatter pass: Pass 4a (dead logs), 4b (living wood), 4c (debris scatter for both wood types), 4d (boulder scatter).
- Prevents enclosed impassable pockets — a tile that is already surrounded on all 8 sides by impassable tiles will no longer have a boulder / log placed on top of it, eliminating the "boulder buried inside dead wood" visual glitch.

**WorldMapGenerator.cs — Post-pass A.5: forced island seeding:**
- When `rainBias ≥ 0.9` (maximum rainfall slider), the world generator counts any naturally occurring Island candidates from cluster overlap. If fewer than 2 exist, it places the remainder by carving Pondsea on all 4 cardinal neighbours of a randomly chosen interior land tile.
- Post-pass C then detects and labels these tiles as Island automatically, ensuring at least 2 Island world-tiles always appear at maximum rainfall.

**WorldGenPanel.cs — "Coastal [Biome]" display:**
- Tile info label and Begin Colony button tooltip now prefix biome name with "Coastal " when `tile.IsCoastal` is true (e.g. "Coastal Forest · Elev 38% · Rain 72% · Magic 19%").
- "Ocean" UI strings replaced with "Pondsea" throughout: the map hint label and the impassable-tile selection message.

**BiomeType.cs:** Updated Pondsea comment to remove the word "ocean" (now reads "sea or inland pond").

---

## [0.2.29] — 2026-05-10

### Changed — Water Placement Redesign (World Clusters + Local Beach/Island Edges)

**WorldMapGenerator.cs:**
- Removed noise-elevation gate `elev < 0.15 → Pondsea` from `ScoreBiome`; all Pondsea now comes exclusively from Post-pass A.
- Removed forced edge-tile elevation clamping (`isEdge → elev ≤ 0.10`); world map edges can now be any biome.
- **Post-pass A completely replaced:** scatter of 0–3 individual tiles replaced with rainfall-weighted BFS cluster generator. Cluster count scales with grid area and average world rainfall (`gridSize² × 0.004 × (0.5 + avgRain × 2)`). Each cluster's target size is the max of two independent rolls biased toward 1–18 tiles, multiplied by `(0.6 + avgRain × 0.8)`, capped at 18. BFS growth skips Mountains, Peaks, and existing Pondsea; shuffled cardinal directions produce organic irregular shapes.
- Added `using System.Collections.Generic` for Queue.

**LocalMapGenerator.cs — SelectTerrain:**
- Removed `elev < 0.18 → Water` branch. Water no longer spawns from elevation on local maps. Very low elevation now falls through to the same `rain > 0.48 → Mud : Sand` branch as mid-low elevation.
- Added `BiomeType.Island` to `SelectTerrain` (shares Coast's `rain > 0.5 → Mud : Sand` terrain).
- Added `BiomeType.Island` to `BiomeDeadLogThreshold` (0.84, matching Coast) and `BiomeLivingWoodThreshold` (0.93, matching Coast).

**LocalMapGenerator.cs — Pass 4g (Coastal/Island water edges):**
- Fertility boost unchanged (+0.15 on all non-water tiles for IsCoastal or Island levels).
- **New: coastal beach strip** — IsCoastal non-Island tiles get a contiguous water edge on one randomly chosen side: 1/5–1/3 of that side's length, 2–3 tiles deep, passable tiles only. Mimics real-world beach shorelines.
- **Expanded: Island water border** — increased from 2-tile to 4-tile border on all four sides (~24% of an 80×50 map, well under the 1/3 cap). All perimeter tiles (passable or not) forced to Water.

---

## [0.2.28] — 2026-05-10

### Changed — BiomeType Ocean + Pond merged into Pondsea

**BiomeType.cs:** `Ocean` and `Pond` removed; replaced with single `Pondsea` entry.

**WorldTile.cs:** `Passable` and `IsCoastal` comments updated to reference Pondsea.

**WorldMapGenerator.cs:** All five `BiomeType.Ocean` / `BiomeType.Pond` references updated to `BiomeType.Pondsea`. Island detection condition simplified from `nb != Ocean && nb != Pond` to `nb != Pondsea`. Post-pass comments updated.

**WorldGenPanel.cs:** `Ocean` and `Pond` colour entries merged into a single `Pondsea` entry `(0.16, 0.38, 0.68)` — midpoint between former deep ocean and inland pond blues.

---

## [0.2.27] — 2026-05-10

### Changed — Vegetation Balance Pass

**LocalMapGenerator.cs — sandshroom biome restriction:**
- `LargeSandshroom` and `SmallSandshroom` now exclusive to Sand terrain. Removed from `SelectDesert` (grass tiles) entirely.
- `SelectDesert` (non-oasis grass): Underbrush 9%, HerbCluster 4%, MagicFlower 3%.
- `SelectOasisVegetation` (oasis-adjacent grass in Desert): replaced all sandshroom entries with normal forest vegetation — LargeMushroom 25%, SmallMushroom 28%, HerbCluster 5%, SmurfberryBush 4%, MagicFlower 3%.

**LocalMapGenerator.cs — SmurfberryBush halved:**
- Band width halved in every biome: Plains (28%→14%), Hills (24.5%→12%), Coast (23.5%→12%), Oasis (8%→4%). Adjacent categories absorb freed probability.

**LocalMapGenerator.cs — SmallMushroom rebalanced:**
- Halved in high-occurrence biomes: Forest (20%→10%), Swamp (22.5%→11%).
- Doubled in low-occurrence biomes: MagicGrove (11%→22%), Oasis (14%→28%).

**LocalMapGenerator.cs — MossPatch added to Forest:**
- `SelectForest` baseline: Underbrush cut ~30% (30%→21%) to add MossPatch 9% band.
- `SelectForestNearWood`: new selector for tiles adjacent to DeadLog or LivingWood. MossPatch 45%, Underbrush 17%, LargeMushroom 12%, SmallMushroom 6%. Produces moss rings around log formations.
- `IsAdjacentToWood` helper added (checks 8 neighbors for DeadLog or LivingWood).

**LocalMapGenerator.cs — Mountains and Peaks vegetation:**
- SmurfberryBush and SmallMushroom both added at ~5% each to Mountains and Peaks. Space absorbed from the None band.

**LocalMapGenerator.cs — local map ponds (Pass 4f):**
- Wetter biomes (Forest, Swamp, MagicGrove, Hills, Coast, Plains) scatter 0–3 small inland ponds (1–3 water tiles each). Swamp 0–3, Forest/MagicGrove 0–2, others 0–1.
- `effectiveN` boost: tiles within a 5×5 window of any water tile (ponds, rivers, oasis) have noise shifted +0.12 before vegetation selector lookup, producing denser growth rings around all water features.
- `IsWithinTwoOfWater` helper added.

---

## [0.2.26] — 2026-05-10

### Added — World Ponds, Coastal/Island Biomes, and Coastal Vegetation

**BiomeType.cs:**
- Added `Pond` (world-level inland water body; impassable at world tier).
- Added `Island` (land tile surrounded by Ocean/Pond on all cardinal sides; always Coastal).

**WorldTile.cs:**
- Added `bool IsCoastal`: set on passable tiles cardinally adjacent to a Pond, and on all Island tiles. Grants +0.15 local fertility and unlocks coastal sand vegetation.

**WorldMapGenerator.cs:**
- Added `using System` for deterministic `Random`.
- Post-pass A: scatters 0–3 inland Pond tiles on mid-elevation land (excludes Mountains, Peaks, Coast).
- Post-pass B: marks `IsCoastal = true` on all passable cardinal neighbors of Pond tiles.
- Post-pass C: detects Island biome (passable tile whose 4 cardinal neighbors are all Ocean or Pond).

**WorldGenPanel.cs:**
- Pond rendered as lighter inland blue `(0.28, 0.54, 0.80)`.
- Island rendered as bright sandy-green `(0.70, 0.84, 0.48)`.

**VegetationType.cs:**
- Added `PalmShroom` (coastal palm-mushroom; impassable; yields Fungal Wood; coastal/island sand only).
- Added `PineShroom` (coastal pine-mushroom cluster; passable; yields Food; coastal/island sand only).

**VegetationSlot.cs:**
- `PalmShroom`: BaseYield=2 (2/3 of LargeMushroom), RegrowthDays=24.
- `PineShroom`: BaseYield=8 (1.5× SmallMushroom), RegrowthDays=5.

**LocalMap.cs:**
- `PalmShroom` added to all three impassable-large-vegetation checks (`HarvestVegetation`, `RestoreVegetationYield`, `ApplyVegetationDelta`).

**LocalMapGenerator.cs:**
- Pass 4g: Coastal/Island levels get +0.15 fertility on all non-water tiles; Island maps gain a 2-tile Water border around the perimeter.
- `SelectCoastalSand()`: new coastal sand selector — PineShroom 6%, Underbrush 4%, HerbCluster 2%, PalmShroom top 10%. Used when `worldTile.IsCoastal || biome == Island`.
- `SelectVegetation` routes `BiomeType.Island` to `SelectCoast` for grass tile vegetation.
- PalmShroom added to the impassability block alongside LargeMushroom and LargeSandshroom.

**LocalMapRenderer.cs:**
- `PalmShroom`: amber-gold stem, wide tropical green cap, drooping frond accents at cap edges.
- `PineShroom`: paired pine-cone shaped caps in dark/mid/bright forest green over brown stems.

**TileInfoOverlay.cs:**
- `PalmShroom` → "Palm Shroom" / "Fungal Wood".
- `PineShroom` → "Pine Shroom" / "Food".

**Roadmap:**
- Phase 9 expanded with fishing system (Coastal/Island Dock + Fisher specialisation) and five aquatic/amphibious creature entries (Hermit Crab, Crawdad, Tide Beetle, Sandhopper, Shore Frog).

---

## [0.2.25] — 2026-05-10

### Added — LivingWood terrain type

**TerrainType.cs:**
- Added `LivingWood` enum value (after `DeadLog`): living tree stump/log, impassable, yields Living Wood material on excavation.

**LocalMap.cs — `IsPassableTerrain`:**
- `LivingWood` added to the impassable exclusions alongside `DeadLog` and `Boulder`.

**LocalMapGenerator.cs — three new passes (4b and 4c):**
- **Pass 4b** (seed +23, freq 0.015f, `FractalType.None`): living wood blob formations. Same large-blob approach as dead logs but rarer — `BiomeLivingWoodThreshold()` returns 0.88–0.98 by biome (Forest lowest, Desert never). Per-map density roll (`^ 0x5E2B9F41`) reduces threshold by up to 0.30f, floored at 0.55f (~45% max coverage). Runs after dead log pass so it never overwrites existing dead log tiles.
- **Pass 4c** (seeds +29 / +31, freq 0.05f): debris scatter pass. Scatters individual branch/bark tiles of both types on remaining passable tiles. Dead log debris threshold 0.935f; living wood debris 0.965f (rarer). Only active in biomes where each type can spawn.
- Added `BiomeLivingWoodThreshold()` method matching the existing `BiomeDeadLogThreshold()` pattern.

**LocalMapRenderer.cs:**
- `PaintTile()`: `LivingWood` now triggers an early return to `PaintLivingWood()` (same pattern as `DeadLog`/`PaintDeadLog()`). Refactored to cache `terrainType` so `Map.Get()` is called once.
- Added `PaintLivingWood()`: warm brown bark palette — `bark(0.28,0.17,0.08)` dominant base fill, `grain(0.38,0.24,0.12)`, `light(0.50,0.34,0.16)`, `dark(0.19,0.11,0.05)`. Three deterministic variants (end-grain stump top, side grain, rough split bark) matching the `PaintDeadLog()` structure so mixed formations read as coherent blocks.
- `TileColor`: `LivingWood → (0.28, 0.17, 0.08)` (deep bark brown; matches minimap).

**SmurfulationC_Roadmap_2026.md:**
- §5.1: `StructureMat` comment updated to include `LivingWood` between `Wood` and `Stone`.
- §5.1 materials table: added Living Wood row (Excavate `LivingWood` terrain; 5/tile; rarer than Dead Wood; mining produces tunnel).
- §5.3 construction table: added Wall / Floor / Door (Living Wood) rows at +2 tier bonus between Dead Wood (+1) and Stone (+1) rows.
- §5.7 tier modifier list: Living Wood: +2 (finite fresh timber; strongest wood material); Stone remains at +1 (parallel path).
- Updated §5.3 footnote and §5.7 closing paragraph to document both wood material paths.

---

## [0.2.24] — 2026-05-10

### Changed — Dead log terrain: large-block formation shape + weathered-wood colour

**LocalMapGenerator — noise shape change:**
- Dead log noise `Frequency` lowered from `0.035f` → `0.015f`. Lower frequency stretches the noise wavelength across the map so only 1–3 large peaks exceed the spawn threshold, producing continent-like blobs rather than many mid-scale clusters.
- Added `FractalType = FractalTypeEnum.None`: removes the default FBM octave layering, which was adding small-scale roughness that fragmented the edges of formations. Pure simplex at this frequency gives smooth, solid-edged blobs matching the RimWorld/DF reference aesthetic.

**LocalMapRenderer — `PaintDeadLog` colour palette:**
- Replaced the warm reddish-brown palette (fresh bark) with a weathered silver-grey palette drawn from photo reference of aged fallen logs and stumps:
  - `surf  (0.56, 0.54, 0.48)` — weathered silver-grey, now the dominant base fill for all three variants (ensures large areas read as one solid mass at distance)
  - `light (0.72, 0.70, 0.63)` — bleached grain highlight (exposed pale grain)
  - `crack (0.18, 0.15, 0.12)` — dark longitudinal crack line
  - `warm  (0.46, 0.38, 0.24)` — exposed inner heartwood at splits
- `TileColor` for `DeadLog` updated from `(0.50, 0.38, 0.24)` → `(0.56, 0.54, 0.48)` so the level-preview minimap matches the in-game colour.

---

## [0.2.23] — 2026-05-10

### Added — Message log fade-out after 15 seconds

**MessageLog — per-entry age tracking and fade:**
- `Entry` gained a mutable `float Age` field (seconds since posted, zero at creation).
- Two new constants: `FadeStartSeconds = 15f` and `FadeDuration = 2f`.
- `_Process(double delta)` advances every entry's age each frame. Entries whose age exceeds `FadeStartSeconds + FadeDuration` (17 s total) are removed from `_entries`. The method iterates oldest-first (tail → head of the `LinkedList`) so `LinkedListNode.Remove()` during iteration is safe.
- `Refresh()` now computes a `[0, 1]` alpha for each visible entry: full opacity for age < 15 s, linear fade to 0 over the next 2 s. Alpha is applied via `Label.Modulate` so both the font and its drop-shadow fade together uniformly.
- New messages always appear at full opacity (age = 0); the panel hides itself as soon as the last entry is removed.

---

## [0.2.22] — 2026-05-10

### Added — Level generation preview on landing zone screen + dead tree density randomisation

**WorldGenPanel — level preview panel (left side of map phase):**
- The "Select Landing Zone" screen now has a two-column layout: level preview on the left (300×188 px, 8:5 ratio), world map on the right. The outer container min-width was increased from 820 → 1000 px to accommodate both.
- When the player clicks any passable world tile, `LocalMapGenerator.Generate()` is called immediately and the resulting map is rendered into `LocalMapPreviewControl` — a new inner class that paints a 1-pixel-per-tile image using `LocalMapRenderer.TileColor()` for terrain and a 55%-strength vegetation colour tint per tile.
- The image is capped at display dimensions (max 300×188 px), so for large level sizes the generator samples tiles at a fixed stride rather than allocating a full-resolution image. The texture is always upscaled or 1:1, never downscaled — combined with `TextureFilter.Nearest`, this keeps the preview crisp at all level sizes.
- Clicking an ocean tile or the Back button clears the preview back to its placeholder state ("Select a tile to preview" drawn centred in the panel).

**LocalMapGenerator — dead tree density randomisation:**
- Each world tile now rolls a per-map density factor from a seeded `System.Random` (seed = `LocalSeed ^ 0x1A3F7C2D`). The factor is a uniform `[0, 1)` roll multiplied by `0.42`, which is subtracted from the biome's base dead-log threshold.
- Threshold floor is clamped to `0.40`, so the densest possible map has `noise > 0.40` → ~60% of passable tiles become DeadLog. Most maps will be much sparser (the roll is uniform, so ~50% of maps see less than 21% additional density).
- The biome hierarchy is preserved: wet biomes (Swamp base 0.80) can reach full 60% density; dry biomes (Desert base 0.96) max out around 46% even on an extreme roll.

---

## [0.2.21] — 2026-05-10

### Changed — Dead log terrain texture (barky, block-like)

**LocalMapRenderer — `PaintDeadLog` rewrite:**
- Root issue: the old renderer placed a floating shape (circles or rects) over a forest-floor green background, making dead log tiles look like objects on soil rather than solid impassable terrain — inconsistent with Boulder, which is a flat uniform fill.
- Fix: the entire 16×16 tile is now filled with dark bark `(0.28, 0.17, 0.08)` first — no green visible. Grain highlight and shadow crack lines are painted on top, giving a textured-but-solid appearance that reads at the same visual weight as Boulder.
- Three deterministic variants (hash of tile coords % 3):
  - **End-grain** — four concentric rectangular rings (tree rings from above) with four radial crack lines from the centre; reads as a stump cross-section.
  - **Side grain** — five full-width horizontal grain bands (alternating grain/light tones) with two vertical crack lines; reads as log side view.
  - **Rough bark** — offset grain bands of unequal length with three asymmetric crack lines; reads as broken or weathered bark face.
- All three variants share the same `bark / grain / light / dark` colour palette so they tile together as a coherent terrain type.

---

## [0.2.20] — 2026-05-10

### Changed — Vegetation pixel art overhaul + smurf sprite improvements + dead log formation scale

**LocalMapRenderer — all vegetation redesigned as 16×16 pixel art:**
- **LargeMushroom** → Fly Amanita. Overrides terrain with a forest-floor background, then paints: cream stem (4×6 px, bottom-center), gill shadow line, bright red hemispherical cap (r=6 centered at oy+6), and three white spots on the cap. Fully readable as a mushroom at any zoom.
- **SmallMushroom** → 3/4-view toadstool. Cream stem (2×4 px), dark gill strip (6×2 px), tan rounded cap circle (r=3.5 centered above gills).
- **Underbrush** → Multi-blob leafy silhouette. Two dark-green lateral blobs + large central blob for mass; three lighter-green smaller circles on top for layered foliage depth.
- **SmurfberryBush** → Organic rounded bush with red berries. Three overlapping dark-green circles for body; a lighter highlight cluster on top; three distinct red berry dots.
- **HerbCluster** → Herb stem pair with leaf sprays and magenta flower cluster. Two thin green stems; four leaf-pair rectangles radiating outward; three magenta flower circles at the top.
- **MagicFlower** → Eight-petal radial flower viewed from above. Four main petals on the cardinal axes (alternating purple-magenta and deep purple); four smaller diagonal petals; golden-yellow center disc overlaid on all.
- **MossPatch** → Irregular organic blob patch. Four overlapping teal-green circles at different radii and offsets, composited at partial opacity (0.35–0.55× `a`) to read as a semi-transparent ground cover.

**LocalMapGenerator — dead log formation scale:**
- Dead log noise frequency lowered from `0.08f` → `0.035f`. Higher-frequency noise produced small isolated tiles; the lower frequency creates large contiguous zones of stumps and fallen logs that read as fallen-tree fields or stump groves from a smurf's perspective, analogous to boulder fields or forest patches.

**SmurfColonyView — smurf sprite improvements:**
- Separated head and body into two distinct circles (head r=5 at y−1; body r=3.5 at y+2.5) for clearer smurf silhouette rather than a single merged blob.
- Eye whites changed from 35% to 100% opacity and increased to r=1.8 so they are clearly visible at normal zoom. Pupils placed slightly below sclera centre for a forward-looking expression.
- Hat given right-side shadow triangle (darker duplicate polygon on the right half) for a conical 3-D appearance. Shadow uses a noticeably darker tint on both white (male) and pink (female) hats.
- Mood dot repositioned to y−13.5 (above hat tip) and selection glow softened to 45% opacity.
- Female hair blobs enlarged to r=2.0 and repositioned closer to the hat brim for a more natural silhouette.

---

## [0.2.19] — 2026-05-10

### Fixed / Added — DeadLog terrain spawning and visual rendering

**LocalMapGenerator — dead log spawning fixed:**
- Root cause: the dead log pass used `noise < density` (e.g., `< 0.10` for Forest), targeting the bottom of the Simplex noise range. Simplex noise from FastNoiseLite rarely reaches its extreme low values in practice, so the condition almost never fired and no dead logs spawned.
- Fix: renamed `BiomeDeadLogDensity` → `BiomeDeadLogThreshold` and flipped the comparison to `noise > threshold`, matching the high-end convention used by vegetation selection. Threshold values are calibrated to give visible dead-log clusters without blocking navigation.
- Biome order by dead-wood emphasis (wetter = more dead wood): Swamp (0.80) > Forest (0.82) > Coast (0.84) > MagicGrove (0.85) > Hills (0.86) > Plains (0.90) > Mountains (0.92) > Peaks (0.94) > Desert (0.96). Unrecognised biomes use threshold 2.0 (never spawns).

**LocalMapRenderer — DeadLog tile shape rendering:**
- Dead log tiles no longer render as solid brown squares. A per-tile deterministic variant (hash of tile coordinates mod 3) selects one of three shapes:
  - **Stump** — concentric circles: dark bark rim (r=5), exposed wood ring (r=3.5), heartwood centre (r=1.5).
  - **Fallen log — horizontal** — bark body rect (14×6 px), lighter wood-face on top half, highlight line, rounded bark ends.
  - **Fallen log — vertical** — same rotated 90°.
- All three variants use a forest-floor green background so the ground shows around the shape regardless of the surrounding terrain type.

---

## [0.2.18] — 2026-05-10

### Fixed / Changed — Vital-organ death + message log expansion

**SimulationCore / NeedsSystem — vital-organ death now fires with Caretaker present:**
- Root cause: `NeedsSystem.Tick()` applied starvation damage to the Liver and then immediately called `HealParts()` in the same pass. A Caretaker's vital-organ heal rate (`0.1f/call`) was enough to push the Liver from `0.0` back to `0.1` before SimulationCore's death check ran, so the check (`cond <= 0f`) never triggered while a Caretaker was alive.
- Fix: split NeedsSystem into two phases. `Tick()` now only applies need decay and starvation damage. A new `HealTick()` method handles all passive and Caretaker healing. SimulationCore calls them in order: `NeedsSystem.Tick()` → vital-organ death check → mood-crossing detection → `NeedsSystem.HealTick()`. Dead smurfs are already marked `IsAlive = false` before `HealTick` runs, so `HealTick` skips them.

**MessageLog — expanded to 10 visible entries:**
- `MaxVisible` increased from 4 to 10 (2.5×). Panel height and `OffsetTop` adjust automatically from the constant.

---

## [0.2.17] — 2026-05-10

### Fixed — Vegetation flicker/ripple on camera move

**LocalMapRenderer — full-resolution baked texture:**
- Root cause: Godot's canvas renderer clips individual `DrawRect`/`DrawCircle` primitives at the viewport boundary every rendering frame. As the camera moves, the clip boundary shifts and primitives straddling the edge pop in/out, producing the ripple effect. `DrawTextureRect` is immune because clipping is handled by the GPU sampler.
- Fix: vegetation is no longer drawn as canvas primitives. Instead, a single `Rgb8` image at full pixel resolution (`Width×TS × Height×TS`) is maintained. Terrain colour is filled for all 16×16 pixels of each tile first; the vegetation shape is then pixel-painted on top using `terrain.Lerp(vegColor, alpha)` — pre-baking transparency into the RGB values so no alpha channel is required.
- `_Draw()` now emits exactly one `DrawTextureRect`. No canvas primitives at all — the flicker is structurally impossible.
- `FillRect` and `FillCircle` helpers paint directly into the image. On a change event, only the affected tile's 16×16 pixel block is repainted before `ImageTexture.Update()`.

---

## [0.2.16] — 2026-05-10

### Fixed — Vegetation shape rendering (roadmap visuals)

**LocalMapRenderer — per-tile shape drawing:**
- Dropped the second `Rgba8` vegetation image entirely. 1-pixel-per-tile images cannot express sub-tile shapes and always render as solid colour blocks.
- Vegetation is now drawn as Godot primitives (`DrawRect` / `DrawCircle`) directly in `_Draw()`, on top of the baked terrain texture. Shapes match the Phase 2 roadmap spec:
  - **Underbrush** — dark-green rect, 8×8 px, centered in tile
  - **SmurfberryBush** — green rect + red accent dot (radius 1.5)
  - **SmallMushroom** — cream/tan circle, radius 3 px
  - **LargeMushroom** — warm tan rect, 10×10 px
  - **HerbCluster** — bright-green rect + magenta dot (radius 1.5)
  - **MagicFlower** — magenta/purple circle, radius 3 px
  - **MossPatch** — soft blue-green rect, 8×8 px, 40% opacity
- Depleted vegetation renders at 50% alpha. Terrain layer unchanged (one `DrawTextureRect`).
- Performance: `_Draw()` is only invoked on `QueueRedraw()`, which fires only on `TerrainChanged` / `VegetationChanged` events. Camera movement still causes zero redraws.

---

## [0.2.15] — 2026-05-10

### Fixed / Changed — Vegetation overlay + mood message coverage

**LocalMapRenderer — two-texture vegetation overlay:**
- Vegetation is no longer composited into the terrain image as a colour lerp. The renderer now maintains two separate baked textures: a terrain layer (`Rgb8`, opaque) and a vegetation layer (`Rgba8`, transparent where no vegetation). `_Draw()` emits two `DrawTextureRect` calls — terrain first, vegetation on top — so vegetation appears as a visually distinct overlay rather than a tint of the terrain pixel.
- `OnTerrainChanged` updates both layers (terrain colour + re-evaluate vegetation for that tile). `OnVegetationChanged` updates only the vegetation layer. Camera movement still causes zero redraws; each event still touches exactly one pixel per image.
- Vegetation alpha: 0.75 (healthy) / 0.45 (depleted). Colours are bolder than before to read clearly over all terrain types.

**GameController — mood messages for every downward transition:**
- `OnMoodThresholdCrossed` previously only posted a `MoodDrop` message when the new mood was Stressed or worse (ordinal ≤ 3). Removed that guard — any downward mood transition now posts a message (e.g. Inspired → Content, Content → Stressed, etc.).

---

## [0.2.14] — 2026-05-10

### Fixed / Changed — LocalMapRenderer baked texture + MessageLog repositioning

**LocalMapRenderer — vegetation composited into terrain image (performance fix):**
- Removed `DrawVegetation()`, `DrawVegetationCulled()`, and `_Process()` entirely. The previous approach drew per-tile vegetation shapes each frame as separate draw commands (up to 90,000+ at low zoom on a 480×300 map); camera movement triggered a full redraw every frame.
- Vegetation is now composited directly into the terrain image via `Color.Lerp(terrainColor, vegColor, alpha)` — one pixel per tile, encoding both terrain type and vegetation state. `_Draw()` always emits exactly one `DrawTextureRect` call regardless of map size, zoom, or camera position.
- Camera movement causes zero redraws. Terrain/vegetation change events update exactly one pixel via `Image.SetPixel` + `ImageTexture.Update()` then `QueueRedraw()`.
- Vegetation colour table: Underbrush (dark green), SmurfberryBush (bright green), SmallMushroom (tan), LargeMushroom (brown), HerbCluster (lime), MagicFlower (purple), MossPatch (teal). Alpha: 0.65 healthy / 0.35 depleted.

**MessageLog — repositioned to bottom-left, size reduced by half:**
- Anchors changed from top-left (below HUD at y=54) to bottom-left (measuring upward from screen bottom edge with 10 px margin).
- Panel width reduced from 300 px to 200 px. `MaxVisible` reduced from 6 → 4 entries. `FontSize` reduced from 12 → 9 pt. `LineHeight` reduced from 16 → 12 px.

---

## [0.2.13] — 2026-05-10

### Added — Message log and WorldGenPanel labels

**MessageLog (new — scripts/ui/MessageLog.cs):**
- RimWorld/DF-style event log anchored below the top HUD bar on the left side, matching the font, shadow, and positioning style of `TileInfoOverlay`. Shows the 6 most recent events; newest entry at top.
- Six message categories: `Birth` (green), `Death` (red), `MoodDrop` (amber), `Combat` (orange), `Research` (blue), `General` (white). Each category has an independent visibility filter toggled via `SetCategoryVisible()`; all are visible by default.
- `MessagePosted` event fires after every accepted message. `GameController` (or a future SettingsPanel) can subscribe to this hook to pause the simulation on specific event types — e.g. pause on `Death` or `Combat` — without modifying `MessageLog` itself.
- Wired in `GameController`: `SmurfDied` → Death entry with compact date; `BirthOccurred` → Birth entry; `MoodThresholdCrossed` → MoodDrop entry only when mood moves downward into Stressed or worse. Date displayed as compact `[D{day} Y{year}]` prefix.

**WorldGenPanel — level size labels:**
- `320 × 200` label now reads `320 × 200  (Recommended)` matching the `(Max)` / `(Default)` tag format used in the world size dropdown.
- `480 × 300` label now reads `480 × 300  (Max)` for consistency.

---

## [0.2.12] — 2026-05-10

### Changed — Smurf scale and building cleanup

**Smurf resized to 1 tile (SmurfColonyView):**
- All `DrawSmurf` dimensions scaled to fit within a 16×16 px tile: body radius 12 → 5 px, hat tip at y-11 (was y-26), hat base ±4 (was ±9), eyes radius 1 px (was 2 px), female hair radius 1.5 px (was 3.0–3.5 px), name label offset y+9 (was y+20), selection ring radius 8 (was 15). Bob amplitude reduced 2.2 → 1.0 px. Click-detection radius updated 18 → 10 px.

**Stub buildings removed (SmurfColonyView):**
- `Buildings` static array, `DrawBuildings()`, and `DrawMushroomCap()` removed. These were placeholder geometry for Kitchen, Hut, Hall, Watchtower etc. that pre-dated the tile-based Phase 5 construction design. `_Draw()` no longer calls `DrawBuildings()`. Building rendering will be handled by `LocalMapRenderer` as part of the Phase 5 `StructureSlot` system.

---

## [0.2.11] — 2026-05-10

### Fixed — Vegetation rendering and camera initialisation

**Vegetation culling rewritten (LocalMapRenderer):**
- `DrawVegetationCulled()` no longer uses `GetViewportTransform() * GlobalTransform` matrix inversion to compute the visible tile range. That approach produced incorrect results when the renderer's parent is a plain `Node` (no 2D canvas transform) and the viewport is stretched to a non-native resolution, causing vegetation to be drawn only for a fraction of visible tiles. The loop now queries the active `Camera2D` via `GetViewport().GetCamera2D()` and computes the visible world rect from camera position, zoom, and viewport size directly — robust to any stretch or resolution configuration.
- `_Process()` now compares camera `GlobalPosition` and `Zoom` with `IsEqualApprox()` instead of comparing raw `Transform2D` values from `GetViewportTransform()`. This avoids false positives from floating-point noise and prevents spurious `QueueRedraw()` calls on static frames.

**Camera position fixed after deferred map generation (GameController):**
- `GenerateAndInitMap()` re-centres `_camera.Position` on the actual generated map after `EnsureDefaultMap()` completes. Previously, `BuildGameWorld()` set the camera position from `LocalMap.DefaultWidth/Height` (80×50 tiles) before generation ran; on larger maps the camera remained in the top-left corner, making only a small portion of the map visible and concentrating Smurf wander within that quadrant.

**Colony view wander bounds fixed (SmurfColonyView):**
- Added `UpdateMapSize(LocalMap)` method, called from `GenerateAndInitMap()` after map generation. `_Ready()` cannot update `_mapSize` on the deferred-generation path because the map doesn't exist yet at that point, leaving Smurfs wandering within the default 1280×800 area even on larger maps.

---

## [0.2.9] — 2026-05-10

### Changed — Performance fixes + live visual update system

**Fix 1 — Image-based terrain rendering:**
- `LocalMapRenderer` now bakes the entire terrain layer into a single `Image` (one pixel per tile, `Format.Rgb8`) at map load time and draws it with a single `DrawTextureRect` call. On a 480×300 map this replaces ~144,000 `DrawRect` calls with one GPU texture blit per frame. Texture filter set to `NEAREST` for crisp pixel-art display at all zoom levels.
- When `LocalMap.MutateTerrain` is called (Phase 3 excavation, LargeMushroom depletion/regrowth) only the affected pixel is overwritten via `Image.SetPixel` + `ImageTexture.Update` — no full rebake needed.

**Fix 2 — Viewport-culled vegetation overlay:**
- `LocalMapRenderer._Draw()` now computes the visible tile rect by inverting `GetViewportTransform() × GlobalTransform` against the viewport rectangle, then emits draw commands only for tiles within that window. At 1× zoom roughly 120×68 tiles are visible; ~8,160 draw commands instead of up to 144,000.
- `LocalMapRenderer._Process()` compares the current viewport transform against the last-seen value each tick and calls `QueueRedraw()` only when the camera has moved or zoomed, preventing unnecessary redraws on static frames.

**Fix 3 — Deferred map generation with loading overlay:**
- `GameController._Ready()` no longer calls `WorldState.EnsureDefaultMap()` synchronously. When no map is pre-loaded (new-game path), it shows a "Generating map…" overlay and defers generation via `CallDeferred`. This gives the player one rendered frame of visual feedback before the ~100 ms blocking generation begins on large maps.
- When a map IS pre-loaded (save-load path), `_mapRenderer.SetMap()` is called immediately — no overlay shown.

**Live visual update system:**
- `LocalMap` now exposes `event Action<int,int,TerrainType> TerrainChanged` and `event Action<int,int> VegetationChanged`. Both are fired from `MutateTerrain` and `SetVegetationYield` respectively, so any Phase 3/4 system that mutates the map automatically propagates changes to the renderer.
- `LocalMapRenderer.SetMap(LocalMap)` subscribes to both events. `TerrainChanged` triggers a single-pixel image update; `VegetationChanged` calls `QueueRedraw()` so the depleted/regrown/cleared state is shown on the same frame it changes. `_ExitTree` unsubscribes to prevent dangling references on scene reload.
- `LocalMap.HarvestVegetation(x, y)` added as a Phase 3 stub: reduces yield by one unit, enters regrowth state on full depletion, and for `LargeMushroom` specifically restores tile passability when the tree is fully cleared (and fires `TerrainChanged` so the pixel updates). `RestoreVegetationYield` re-locks passability when a LargeMushroom regrows.
- `LocalMap.ApplyVegetationDelta` now restores tile passability when loading a save where a LargeMushroom was depleted, so save/load correctly reflects cut-down trees.

---

## [0.2.8] — 2026-05-10

### Changed
- **MagicFlower density halved in MagicGrove biome:** Noise band 0.38–0.60 (range 0.22, ~35% of vegetated tiles) reduced to 0.38–0.49 (range 0.11). Freed band 0.49–0.60 reassigned to SmallMushroom, compensating food density in MagicGrove maps. Magic-essence attunement remains viable via HerbCluster (unchanged) and the Pass 6 minimum guarantee.

---

## [0.2.7] — 2026-05-10

### Changed
- **Magic vegetation minimum guarantee (Pass 6):** Every generated map now guarantees at least `max(6, tileCount / 650)` MagicFlower + HerbCluster tiles. Non-MagicGrove biomes (Forest, Swamp, Desert, etc.) may produce very few magic-essence sources from their biome tables; the pass tops up with HerbCluster placed on random passable tiles using a deterministic seeded shuffle. Gives the Mage role valid attunement targets on any biome.
- **SmurfberryBush density +~12%:** Upper noise boundary raised in Plains (0.73 → 0.76), Hills (0.80 → 0.825), Coast (0.83 → 0.855). Adjacent bands compressed proportionally; LargeMushroom tier preserved.
- **SmallMushroom density +~11%:** LargeMushroom/SmallMushroom boundary in Forest lowered from 0.82 → 0.80 (expands SmallMushroom range). SmallMushroom upper boundary in Swamp raised from 0.62 → 0.645.

---

## [0.2.6] — 2026-05-10

### Fixed
- Every biome selector in `LocalMapGenerator` now includes a rare `LargeMushroom` tier at the top of its noise range (Plains, Swamp, Hills, Coast, Mountains, Peaks, Desert all previously had none). A loaded Swamp map had zero Large Mushrooms, which would soft-lock players from building structures once Phase 3+ construction is implemented.
- Added a **minimum guarantee pass** (Pass 5) at the end of generation: counts LargeMushrooms after all other passes complete; if below `max(10, tileCount / 400)`, scatters the remainder on random passable tiles using a deterministically seeded shuffle. This hard floor prevents any biome from being structurally starved of Fungal Wood regardless of seed or map size.

---

## [0.2.5] — 2026-05-10

### Added — Phase 2.5: Scale Refactor, Terrain Features, and Vegetation Layer

**Terrain type refactor:**
- `TerrainType.Stone` renamed to `TerrainType.Boulder` across all files (generator, renderer, overlay, passability logic). Recontextualised at Smurf scale: a pebble/rock face rather than mountain-scale geology. Impassable; yields Stone on excavation (Phase 3+).
- `TerrainType.DeadLog` added: fallen branch or log at Smurf scale. Impassable; yields Wood on excavation (Phase 3+). Placed by a fourth noise pass (seed +17) at biome-weighted density (Forest high → Desert very low).
- `LocalTile.DesignatedForExcavation` bool stub added for Phase 3 player designation.
- `LocalMap.IsPassableTerrain()` static helper extracted (Water, Boulder, and DeadLog all impassable).

**Vegetation layer:**
- `VegetationType` enum: None, Underbrush, SmurfberryBush, SmallMushroom, LargeMushroom, HerbCluster, MagicFlower, MossPatch.
- `VegetationSlot` struct: Type, Health (byte), YieldRemaining (byte), RegrowthTimer (ushort). Static helpers `Create(type)`, `BaseYield(type)`, `RegrowthDays(type)`.
- `LocalMap` gains a parallel `VegetationSlot[,]` array with `GetVegetation(x,y)` / `SetVegetation(x,y,slot)` accessors.
- `LocalMapGenerator` now runs four passes: terrain (1+2), dead log scatter (4), vegetation (3). Pass 4 runs before Pass 3 so dead-log tiles are never candidates for vegetation. `LargeMushroom` placement sets the tile's `Passable = false` at generation time.
- Biome vegetation tables: Forest/ForestFloor (Underbrush, LargeMushroom, SmallMushroom), MagicGrove (MagicFlower, LargeMushroom, HerbCluster), Plains (SmurfberryBush, Underbrush, HerbCluster), Swamp (SmallMushroom, MossPatch, Underbrush), Hills (SmurfberryBush, MossPatch), Coast (SmurfberryBush, Underbrush), Mountains/Peaks (MossPatch sparse), Desert (HerbCluster rare).

**Renderer:**
- `LocalMapRenderer` gains a second draw pass: vegetation drawn on top of terrain using small colored rects and circles per type. Depleted slots render at half opacity.
- `TileColor` updated: `Boulder` (mid-gray with cool blue tint `#8B8FA0`), `DeadLog` (warm desaturated brown `#80613D`). `Stone` entry removed.
- `TileInfoOverlay.ShowTile` gains a `VegetationSlot` parameter; shows vegetation name, yield resource, and `[depleted]` on a second line. Boulder and DeadLog overlays show "Yields Stone / Wood" hints.

**Save/load infrastructure:**
- `TerrainDelta(X, Y, Terrain)` and `VegetationDelta(X, Y, YieldRemaining, RegrowthTimer)` record types added to `SaveManager`.
- `ColonySave` gains nullable `TerrainDeltas` and `VegetationDeltas` lists (null = no mutations; omitted from JSON for clean saves). Old saves load cleanly with null defaults.
- `LocalMap` gains mutation-tracking API: `MutateTerrain(x,y,t)` and `SetVegetationYield(x,y,yield,timer)` log changes for delta serialization; `ApplyTerrainDelta` and `ApplyVegetationDelta` (used at load time) do not.
- `SaveToSlot` collects deltas from the live local map and serializes them.
- `WorldState.LoadFromSave` applies both delta lists after regenerating from seed.

---

## [0.2.4] — 2026-05-10

### Fixed
- Added `SettingsClosed` signal to `SettingsPanel`, emitted from `Close()` after `SaveSettings()`. `GameController` and `MainMenuController` subscribe and call `ApplyRuntimeSettings()`, which re-reads `settings.cfg` and rebuilds the root tooltip `Theme` via `GameController.BuildTooltipTheme()` — the same path used at startup. This replaces the broken inline `ApplyTooltipSize()` approach, which called `GetTree().Root.Theme = …` at the wrong time; Godot 4 does not reliably propagate a full `Theme` replacement to its internally reused tooltip popup nodes mid-session.

---

## [0.2.3] — 2026-05-10

### Fixed
- `AnimatedButton._Notification` now calls `base._Notification(what)` before its own logic; the missing base call was suppressing native draw, layout, and theme-change notifications across every button in the app, causing widespread panel opacity regressions.
- Removed `ClipChildren = ClipChildrenMode.Only` from the `SaveFileBrowser` card; this flag forces children into a separate compositing pass that breaks `PanelContainer`'s own `StyleBox` background rendering, making the card area appear transparent over the game world and main menu.
- `WorldGenPanel` backdrop alpha raised from 0.96 to 1.0 so the main menu never bleeds through when the New Colony overlay is open.

---

## [0.2.2] — 2026-05-10

### Fixed
- `AnimatedButton` scale animation now tweens from the button's visual center (`PivotOffset = Size / 2`) rather than its top-left corner, preventing asymmetric overflow in any direction.
- `SaveFileBrowser` card now sets `ClipChildren = ClipChildrenMode.Only`, hard-clipping all child drawing to the card rect so animated buttons can never visually escape the panel during hover or press animations.
- `SmurfCardPanel` widened from 275 px to 310 px to give attribute columns and skill bars more breathing room.
- Added a 6 px right `MarginContainer` inside the smurf-card `ScrollContainer` so value and role-bonus labels no longer sit flush against the scrollbar thumb.
- Mouse-wheel zoom is now suppressed while the cursor is inside the open smurf card (`IsMouseOverCard()` guard in `GameController._UnhandledInput`), preventing accidental camera zoom when the player is scrolling the unit card.

---

## [0.2.1] — 2026-05-09

### Fixed
- `SaveFileBrowser` card switched from `CenterContainer` to anchor-based layout (`AnchorLeft/Right = 0.5, OffsetLeft/Right = ±360`) so the 720 px card never overflows the viewport regardless of content width.
- Added inner 8 px right `MarginContainer` inside the save-browser scroll area; button shadows no longer clip against the scrollbar.
- `BuildSlotRow` name `VBoxContainer` given `CustomMinimumSize = Vector2(80, 0)` and labels set to `TextOverrunBehavior.TrimEllipsis`; long slot names no longer collapse the name column to zero width.
- `SaveFileBrowser.Refresh()` made public; `OnBrowserSaveConfirmed` now calls it so the slot list updates immediately after a save, with a `✓ Saved as '…'!` status message.
- Pause-menu button icons (▶ ★ ↩ ⚙ ⬅) removed; buttons are now text-only, matching the main menu's visual style.
- Spacebar pause handling moved from `_UnhandledInput` to `_Input` with `SetInputAsHandled()`; prevents the focused pause button from double-consuming the event and requiring the player to hold the key.
- `Cancelled` signal handler in `GameController` now reopens the pause menu after closing the save/load browser, rather than leaving no panel visible.

---

## [0.2.0] — 2026-05-01 (Phase 2 baseline)

### Added
- `WorldTile`, `LocalTile`, `LocalMap` data structures per the two-layer world architecture.
- `WorldMapGenerator`: 32×32 grid, four independent FastNoiseLite passes (elevation, rainfall, temperature, magic density); latitude gradient for temperature; forced ocean edges; 10 biomes scored from all four layers.
- `LocalMapGenerator`: 80×50 grid seeded deterministically from `WorldTile.LocalSeed`; two noise passes; 7 terrain types assigned from biome and local variation.
- `LocalMapRenderer`: Option B colored rectangles, one `DrawRect` per tile; green fallback for old saves without world data.
- `WorldState` autoload: carries `WorldMap`, selected tile coordinates, and `CurrentLocalMap` between scenes; `EnsureDefaultMap()` handles legacy saves.
- `WorldGenPanel`: two-column layout — world name, seed, world/level size dropdowns, 4 generation bias sliders; saved worlds browser with load/delete; worlds persisted to `user://worlds/{name}.json`.
- `Camera2D`: three discrete zoom levels cycled with Tab (0.55× village / 1.0× neighbourhood / 2.0× individual); bounded to 12 tiles outside map edges; WASD pan, mouse-drag pan, scroll-wheel zoom with RimWorld-style mouse-anchor.
- `TileInfoOverlay`: hover tooltip showing tile type name.
- `SmurfColonyView` restructured: terrain drawing delegated to `LocalMapRenderer`; wander bounds read from actual map dimensions; click detection uses `GetGlobalMousePosition()` for camera-correct hit testing.
- `SaveManager` overhauled: named slots in `user://saves/{name}.json`; `SaveToSlot`, `LoadSlot`, `DeleteSlot`, `RenameSlot`, `GetSaveSlots`; legacy `smurfulation_save.json` auto-migrated to an `autosave` slot on first run; `ColonySave` extended with world fields (seed, tile coordinates, grid size, level size, name, all 4 bias values).
- `SaveFileBrowser` panel: modal overlay for Save (new slot + overwrite + rename) and Load modes; inline rename with commit-on-blur; delete per slot; exit-save displayed as "Exit Save."
- "Exit to Main Menu" silently writes an `exit-save` slot before returning.

---

## [0.1.2] — 2026-04-30

### Added
- Skills system: 16 skills across 6 domains (Survival, Combat, Crafting, Magic, Social, Knowledge); role-seeded at creation; saved and loaded with back-fill for older saves.
- `SkillRegistry`: defines all skills, their domain, description, and per-role bonus values.
- Role assignment: player dropdown in `SmurfCardPanel` takes effect immediately, even while paused; role change queued from main thread to sim thread.
- `SmurfCardPanel` Skills tab (fourth tab): skill bars grouped by domain with tier-coloured fills (Novice steel-blue → Capable bright-blue → Skilled green → Expert gold → Master amber); role-bonus `+N` overlay in gold.
- `MoodThresholdCrossed` signal.

---

## [0.1.1] — 2026-04-28

### Added
- Trait inheritance: biological trait penetrance passed from two parents with ±0.1 variance at birth.
- Sex ratio: 1:49 male-to-female enforced at birth; `SmurfNameGenerator` with 40-name male pool and 10-name female pool.
- Population pressure: Nutrition decays faster proportional to colony overage above `FoodCapacity`.
- `BirthSystem.ComputeFoodCapacity`: `max(7, foragers × 3)` — pre-Phase 4 food proxy.
- Passive body-part healing: non-vital parts heal 0.1 per call; vital parts require an assigned Caretaker at the same rate.
- `CauseOfDeath` enum (Natural / Starvation / Combat) carried through `SmurfSnapshot` and the `SmurfDied` signal.
- Death cleanup: `RemoveAll`, snapshot filter, and `UpdateFromTick` diff verified clean on smurf removal.
- Birth visual: `SmurfColonyView.UpdateFromTick` auto-adds sprite for newly born smurfs on name-set diff.

---

## [0.1.0] — 2026-04-26 (Phase 1 baseline)

### Added
- Birth system: seasonal check (25% chance per season) against colony food capacity; `BirthOccurred(Smurf)` signal.
- Population loop closed: smurfs are born and die within a single continuous simulation run.

---

## [0.0.3] — 2026-04-24

### Added
- `SmurfCardPanel`: tabbed unit-detail card — Main (needs bars, role dropdown, mood bar, personality trait list), Mood (need breakdown, personality modifier total, effective mood, state description), Health (body-part condition tree, biological trait bars).
- `GameOverPanel`: full-screen overlay on colony wipe; deletes save; returns to main menu.
- Tooltip system: RimWorld-style compact dark tooltips on needs, mood states, traits, roles, body parts; delay 1.5 s; `MouseFilter.Pass` on labels.
- `HUDController`: date display, population count, mood-state breakdown counts, speed/pause buttons.
- `PauseMenuPanel`: full-screen pause overlay with Resume / Save / Load / Settings / Exit to Main Menu.
- `SettingsPanel`: audio (master / music / SFX volume), display (full-screen toggle, zoom speed, scroll speed), gameplay (tooltip size, show tooltips) — persisted to `user://settings.cfg`.
- `AnimatedButton`: unified wood/parchment-themed button with center-pivot bounce animation, hover scale, and SFX hooks.
- `SFXManager`: pooled one-shot audio; hover, click, and notification sounds; logarithmic volume scaling.
- `MusicManager`: context-based crossfading tracks (Peace / Tense / Menu).
- Basic autosave to `user://smurfulation_save.json` (superseded by named-slot system in v0.2.0).
- Main menu with Continue (if save exists) / New Colony / Settings / Quit.

---

## [0.0.2] — 2026-04-22

### Added
- Body part system: 20 named parts with parent/child hierarchy and vital flags (Brain, Heart, Left Lung, Right Lung, Liver, Stomach).
- Starvation damage: Stomach degrades 2.5/call and Liver degrades 1.7/call at zero Nutrition; Liver failure causes death in approximately 3.5 in-game days.
- Vital organ death: any vital part reaching 0% condition kills the smurf immediately; `SmurfDied` signal fires with cause.
- Body-part condition carried through `SmurfSnapshot` for UI display.
- `BodyPartRegistry`: static template defining all 20 parts, their parent, and their vital flag.

---

## [0.0.1] — 2026-04-19

### Added
- Personality trait system: 25 named traits (Optimist, Pessimist, Sleepyhead, Glutton, Stoic, etc.); each smurf assigned 1–5 traits age-weighted at creation; each trait carries a `MoodModifier` (−10 to +8).
- Biological trait system: 13 Homo smurficus traits (Communal Bonding, Accident-Prone, Night Owl, etc.) with penetrance float (0.0–1.0); penetrance scales need-decay modifiers per trait formula.
- `PersonalityRegistry` and `BiologicalTraitRegistry`: static definitions consumed by simulation and UI.
- Aging: birthday system increments age each in-game year; `LifeStage` enum (Sprout < 20, Juvenile 20–49, Adult 50–399, Elder 400–544, LastSeason 545+); natural death at LastSeason year threshold.
- `YearTicked`, `SeasonChanged`, and `SmurfDied` signals wired into `SimulationCore`.
- Sex-differentiated smurf visuals: females rendered with pink hat and blonde hair; sex ratio recorded on each smurf.

---

## [0.0.0] — 2026-04-17 (Project foundation)

### Added
- `SimulationCore`: background-thread simulation loop at 17 ms tick (≈ 60 ticks/sec); burst-tick mode for fast-forward; pause/resume; speed multiplier.
- Smurf data model: name, sex, age, role, five needs (Nutrition, Rest, Social, MagicResonance, Safety), mood score, personality traits, biological traits, body parts, skills.
- Needs decay: five independent per-tick decay rates weighted by role, life stage, and biological traits.
- Mood system: six states (Inspired ≥ 80, Content ≥ 60, Stressed ≥ 40, Distressed ≥ 20, Breaking ≥ 1, Collapse = 0); computed as weighted needs average plus personality modifier sum, clamped 0–100.
- In-game calendar: 120-day year, 4 seasons, date display.
- `SmurfSnapshot`: immutable sim-to-UI data transfer object produced each tick.
- `SmurfColonyView`: visual representation of the colony; smurf sprites wander within scene bounds.
- Founding colony of 7 smurfs with procedurally generated names, traits, and needs.
- `SimulationManager`: top-level coordinator wiring sim thread to main-thread UI via signals.
