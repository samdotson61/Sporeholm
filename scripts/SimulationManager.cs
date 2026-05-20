using Godot;
using System;
using System.Collections.Generic;
using Sporeholm.Simulation;
using Sporeholm.Simulation.Systems;
using Sporeholm.World;

namespace Sporeholm
{
	// Root Godot node. Owns SimulationCore and bridges the background
	// simulation thread to the scene tree.
	public partial class SimulationManager : Node
	{
		private readonly SimulationCore _core = new();
		private readonly Random _rng = new();

		[Export] public float SpeedMultiplier { get; set; } = 1.0f;
		[Export] public bool Paused { get; set; } = false;

		// ── Godot signals (emitted on the main thread from _Process) ──────────────

		[Signal] public delegate void TickCompletedEventHandler(string date, int population, int inspired, int distressed);

		// Year advanced — fires once per in-game year.
		[Signal] public delegate void YearTickedEventHandler(int year);

		// Season changed — fires 4 times per in-game year.
		[Signal] public delegate void SeasonChangedEventHandler(string newSeason, string prevSeason);

		// A shroomp died — causeOfDeath is "Natural", "Starvation", or "Combat".
		[Signal] public delegate void ShroompDiedEventHandler(string name, int ageAtDeath, string causeOfDeath);

		// A new shroomp was born.
		[Signal] public delegate void BirthOccurredEventHandler(string name, string sex);
		// v0.3.47 — wandering-in arrival. UI consumes this in AlertsPane.
		[Signal] public delegate void WandererArrivedEventHandler(string name, string sex, string role, int age);

		// A shroomp crossed a mood threshold in either direction.
		[Signal] public delegate void MoodThresholdCrossedEventHandler(string name, string fromState, string toState);

		// v0.5.84t — one-shot starvation alert (rising edge of Nutrition < 20).
		// GameController posts a MessageLog entry under Category.Starving.
		[Signal] public delegate void StarvationStartedEventHandler(string name);

		// ── State ─────────────────────────────────────────────────────────────────

		private SimulationSnapshot? _lastSnapshot;
		public SimulationSnapshot? GetLastSnapshot() => _lastSnapshot;

		// v0.5.81 — public drain entry-point so GameController.StartSim can
		// force the initial snapshot through after connecting the
		// TickCompleted signal handler. Pre-v0.5.81b the initial snapshot
		// PushSnapshot from SimulationCore.Start landed in the queue
		// BEFORE GameController.Connect wired OnTick, and the first
		// _Process drain happened in the same frame — the timing race
		// meant the first emit could fire to no listener. Calling this
		// explicitly after Connect closes the race.
		public void DrainSnapshotsAndEmit()
		{
			SimulationSnapshot? latest = null;
			while (_core.Snapshots.TryDequeue(out var snap))
				latest = snap;

			if (latest is not null)
			{
				_lastSnapshot = latest;

				int inspired = 0, distressed = 0;
				foreach (var s in latest.Shroomps)
				{
					if (s.MoodState == MoodState.Inspired) inspired++;
					if (s.MoodState is MoodState.Distressed or MoodState.Breaking or MoodState.Collapse) distressed++;
				}

				EmitSignal(SignalName.TickCompleted,
					latest.Date.ToString(),
					latest.Shroomps.Count,
					inspired,
					distressed);
			}
		}

		// ── Lifecycle ──────────────────────────────────────────────────────────────

		public override void _Ready()
		{
			SeedColony();
			SeedSimPositions();
			// Hand the loaded LocalMap (if any) to the sim thread so BehaviorSystem
			// can read terrain + vegetation. WorldState owns the map; the sim core
			// just reads from it (and uses the existing thread-safe Mutate / Harvest
			// APIs when behavior writes back).
			_core.Map = WorldState.Instance?.CurrentLocalMap;
			// v0.4.36 — also bind to ColonyResources so HUD totals can sum
			// on-map stockpiles. Replaces the v0.4.30 dual-write that
			// overflowed the colony pool.
			_core.Resources.Map = _core.Map;
			_core.Start();
		}

		// Called from GameController after the LocalMap is generated, in case the
		// map wasn't ready when SimulationManager._Ready ran.
		public void BindLocalMap(LocalMap map)
		{
			_core.Map = map;
			_core.Resources.Map = map;   // v0.4.36 — HUD ground-stockpile reads
			SeedSimPositions();
		}

		// Assigns each shroomp a starting SimPos drawn from the map's spawn cluster
		// (BFS from centre). Without this the BehaviorSystem would treat SimPos as
		// Vector2.Zero and immediately try to walk to the map origin (top-left).
		// Cluster size scales with actual shroomp count (was hard-capped at 16,
		// which produced visible stacking on scenarios with 17+ shroomps and meant
		// large colonies that hadn't been seeded yet rendered at (0, 0) until
		// the BehaviorSystem's first move tick).
		private void SeedSimPositions()
		{
			var map = WorldState.Instance?.CurrentLocalMap;
			if (map == null) return;
			var shroomps = _core.AllShroomps();
			if (shroomps.Count == 0) return;

			// v0.3.35 — only seed shroomps whose SimPos is still Vector2.Zero
			// (i.e. brand-new colonies). Save-loaded shroomps have their SimPos
			// restored by LoadFromSave; without this guard, SeedSimPositions
			// would clobber the restored position with the spawn cluster
			// centre — which is the bug the player saw: "shroomps teleport to
			// the centre when the game is unpaused after a save reload".
			var needsSeed = new List<Shroomp>();
			foreach (var s in shroomps)
				if (s.SimPos == Vector2.Zero) needsSeed.Add(s);
			if (needsSeed.Count == 0) return;

			// Always set SimSpeed (role-derived, not saved) for every shroomp
			// — both new and restored — so loaded shroomps use the right speed.
			foreach (var s in shroomps)
				if (s.SimSpeed <= 0f) s.SimSpeed = SpeedForRole(s.Role);

			// Request 1.5× as many cluster tiles as needSeed count so jitter
			// has room to spread the new arrivals out a bit.
			int want = System.Math.Max(8, (int)(needsSeed.Count * 1.5));
			var spawn = map.FindSpawnCluster(want);
			if (spawn.Count == 0) return;

			int i = 0;
			foreach (var s in needsSeed)
			{
				var tile = spawn[i % spawn.Count];
				float jx = (float)_rng.NextDouble() * 0.6f - 0.3f;
				float jy = (float)_rng.NextDouble() * 0.6f - 0.3f;
				s.SimPos    = new Vector2(
					(tile.X + 0.5f + jx) * LocalMap.TileSize,
					(tile.Y + 0.5f + jy) * LocalMap.TileSize);
				s.SimTarget = s.SimPos;
				s.SimSpeed  = SpeedForRole(s.Role);
				i++;
			}
		}

		// Roadmap §3.7 role speed multipliers (base = 32 px/s).
		private static float SpeedForRole(string role) => role switch
		{
			"Guardian"  => 32f * 1.20f,
			"Forager"   => 32f * 1.10f,
			"Crafter"   => 32f * 1.00f,
			"Elder"     => 32f * 0.85f,
			_           => 32f,
		};

		public override void _Process(double delta)
		{
			_core.Clock.SpeedMultiplier = SpeedMultiplier;
			_core.Clock.Paused = Paused;
			DrainSnapshotsAndEmit();

			// Drain cross-thread event queues and re-emit as Godot signals.

			while (_core.PendingYearEvents.TryDequeue(out int year))
				EmitSignal(SignalName.YearTicked, year);

			while (_core.PendingSeasonEvents.TryDequeue(out var sc))
				EmitSignal(SignalName.SeasonChanged, sc.NewSeason.ToString(), sc.PrevSeason.ToString());

			while (_core.PendingDeaths.TryDequeue(out var dead))
			{
				string cause = dead.CauseOfDeath?.ToString() ?? "Natural";
				GD.Print($"[Sim] {dead.Name} died at age {dead.AgeInYears} ({cause}).");
				EmitSignal(SignalName.ShroompDied, dead.Name, dead.AgeInYears, cause);
			}

			while (_core.PendingBirths.TryDequeue(out var born))
			{
				GD.Print($"[Sim] {born.Name} ({born.Sex}) was born!");
				EmitSignal(SignalName.BirthOccurred, born.Name, born.Sex.ToString());
			}

			while (_core.PendingWanderers.TryDequeue(out var wand))
			{
				GD.Print($"[Sim] {wand.Name} ({wand.Sex}, {wand.Role}, age {wand.AgeInYears}) joined the colony.");
				EmitSignal(SignalName.WandererArrived,
					wand.Name, wand.Sex.ToString(), wand.Role, wand.AgeInYears);
			}

			while (_core.PendingMoodCrossings.TryDequeue(out var mc))
				EmitSignal(SignalName.MoodThresholdCrossed, mc.Snap.Name, mc.From.ToString(), mc.To.ToString());

			// v0.5.84t — drain starvation rising-edges.
			while (_core.PendingStarvationStarts.TryDequeue(out var starv))
				EmitSignal(SignalName.StarvationStarted, starv.Name);
		}

		public override void _ExitTree() => _core.Dispose();

		// ── Public API ─────────────────────────────────────────────────────────────

		public void SetSpeed(float multiplier) => SpeedMultiplier = multiplier;
		public void TogglePause() => Paused = !Paused;
		public SimulationDate GetCurrentDate() => _core.Date;

		// Queues a role change to be applied on the simulation thread's next tick.
		public void RequestRoleChange(string shroompName, string newRole) =>
			_core.QueueRoleChange(shroompName, newRole);

		// Roadmap §3.9 — issues a "Move to" player order. The sim thread picks
		// this up on its next BehaviorSystem.Tick and overrides the named
		// shroomp's current task with a non-interruptible PlayerOrder.
		public void RequestPlayerMoveOrder(string shroompName, Vector2 worldPosPixels) =>
			_core.QueuePlayerOrder(shroompName, worldPosPixels);

		// v0.3.24 — multi-shroomp move order. Each shroomp in the list is issued
		// the same move target, with a small radial offset so they don't all
		// pile on the exact same pixel. Used by the RTS-style box-select on
		// right-click.
		// v0.5.2 — also clears each shroomp's MoveOrderQueue so a non-shift
		// right-click cancels any chained orders the player previously
		// queued. RTS standard.
		// v0.5.25 (Phase 5C — rimport.md N6) — paint a tile rectangle as
		// allowed-area for the named shroomp. Lazy-allocates the per-shroomp
		// `AllowedArea` bitmap on first paint. Routes through the sim-
		// thread command queue (matches the v0.4.55+ pattern for player
		// mutations on shroomp state). Caller (GameController) provides the
		// currently-selected shroomp name; if no shroomp is selected, the
		// paint is a no-op.
		public void PaintAllowedArea(string shroompName, int xMin, int yMin, int xMax, int yMax, bool allow)
		{
			var map = _core?.Map;
			if (map == null || string.IsNullOrEmpty(shroompName)) return;
			_core!.PostMainThreadCommand(() =>
			{
				var s = DevFindShroompByName(shroompName);
				if (s == null) return;
				int W = map.Width, H = map.Height;
				if (s.AllowedArea == null || s.AllowedAreaWidth != W)
				{
					// First paint allocates the full bitmap. Default
					// behaviour pre-paint was "no restriction" (null), so
					// to preserve that semantic on first paint we INITIALISE
					// to all-allowed (true) and let this paint flip the
					// rect to disallowed if `allow=false`. Otherwise the
					// player's first paint would forbid every tile NOT in
					// the rect — counter-intuitive.
					s.AllowedArea = new bool[W * H];
					for (int i = 0; i < s.AllowedArea.Length; i++) s.AllowedArea[i] = true;
					s.AllowedAreaWidth = W;
				}
				for (int y = yMin; y <= yMax; y++)
				for (int x = xMin; x <= xMax; x++)
				{
					if ((uint)x >= (uint)W || (uint)y >= (uint)H) continue;
					s.AllowedArea[y * W + x] = allow;
				}
			});
		}

		// v0.5.44 — colony-shared named-area painter. Replaces v0.5.25's
		// PaintAllowedArea (per-shroomp bitmap) as the canonical painter for
		// the Areas tab. Routes through the sim thread to mutate the map's
		// _namedAreas dict safely. The "Home" area is auto-created on
		// LocalMap construction; other names can be painted into existence
		// by passing a fresh name here (lazy-allocated to all-false).
		public void PaintAreaCells(string areaName, int xMin, int yMin, int xMax, int yMax, bool allow)
		{
			var map = _core?.Map;
			if (map == null || string.IsNullOrEmpty(areaName)) return;
			_core!.PostMainThreadCommand(() =>
			{
				map.PaintAreaRect(areaName, xMin, yMin, xMax, yMax, allow);
			});
		}

		// v0.5.44 — set the per-shroomp area assignment. Null = unrestricted
		// (shroomp can work anywhere). Non-null = restrict to the named area.
		// Used by the AreasPanel per-shroomp dropdown.
		public void SetShroompAssignedArea(string shroompName, string? areaName)
		{
			if (string.IsNullOrEmpty(shroompName)) return;
			_core?.PostMainThreadCommand(() =>
			{
				var s = DevFindShroompByName(shroompName);
				if (s == null) return;
				s.AssignedAreaName = string.IsNullOrEmpty(areaName) ? null : areaName;
			});
		}

		// v0.5.44 — snapshot of (name, area) tuples for the AreasPanel UI.
		// Pulls from the latest snapshot so the panel can render the
		// current assignment per shroomp without touching the sim thread.
		public System.Collections.Generic.IReadOnlyList<(string Name, string? AreaName)> SnapshotShroompAreaAssignments()
		{
			var list = new System.Collections.Generic.List<(string, string?)>();
			var snap = _lastSnapshot;
			if (snap == null) return list;
			foreach (var s in snap.Shroomps)
				list.Add((s.Name, s.AssignedAreaName));
			return list;
		}

		// v0.5.44 — snapshot of area names known to the map. AreasPanel
		// drives its dropdown options from this.
		public System.Collections.Generic.IReadOnlyList<string> SnapshotAreaNames()
		{
			var map = _core?.Map;
			if (map == null) return System.Array.Empty<string>();
			return map.AreaNames();
		}

		// v0.5.45 — multi-area lifecycle. The AreasPanel "Create" /
		// "Rename" / "Delete" buttons route here. Each returns true on
		// success so the UI can flash an error toast on failure
		// (duplicate name, missing target, etc.). All three mutate the
		// map's _namedAreas dict and run on the sim thread for safety.
		public bool CreateArea(string name)
		{
			var map = _core?.Map;
			if (map == null) return false;
			return map.CreateArea(name);
		}

		// Renames the area in place AND sweeps every alive shroomp so that
		// any AssignedAreaName matching the old name follows. Without the
		// sweep, renamed areas would orphan their assigned shroomps (their
		// AssignedAreaName would point at a missing key → BehaviorSystem
		// would fall back to "no restriction" via the legacy bitmap path).
		public bool RenameArea(string oldName, string newName)
		{
			var map = _core?.Map;
			if (map == null) return false;
			if (!map.RenameArea(oldName, newName)) return false;
			_core!.PostMainThreadCommand(() =>
			{
				foreach (var s in _core.AllShroomps())
					if (s.AssignedAreaName == oldName)
						s.AssignedAreaName = newName;
			});
			return true;
		}

		// Deletes the area + un-assigns any shroomps whose AssignedAreaName
		// matched the deleted area. They revert to "Unrestricted" which
		// is the safe default. Home isn't special-cased — if the player
		// deletes Home, they can recreate it via Create.
		public bool DeleteArea(string name)
		{
			var map = _core?.Map;
			if (map == null) return false;
			if (!map.DeleteArea(name)) return false;
			_core!.PostMainThreadCommand(() =>
			{
				foreach (var s in _core.AllShroomps())
					if (s.AssignedAreaName == name)
						s.AssignedAreaName = null;
			});
			return true;
		}

		public void RequestPlayerMoveOrderGroup(System.Collections.Generic.IEnumerable<string> shroompNames,
			Vector2 worldPosPixels)
		{
			int i = 0;
			int count = 0;
			foreach (var _ in shroompNames) count++;
			if (count == 0) return;
			foreach (var name in shroompNames)
			{
				// Ring offsets around target — 12 px radius spreads up to ~6
				// shroomps comfortably; larger groups stack with minor overlap.
				float ang = (float)(i * 2.0 * System.Math.PI / System.Math.Max(count, 1));
				var offset = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 12f;
				var target = worldPosPixels + offset;
				// v0.5.2 — clear chain queue on plain right-click. Routed
				// through the sim-thread command queue so the read+clear
				// is atomic with BehaviorSystem.Tick's own queue
				// consumption. Without this, an in-flight chain order
				// could fire after the player issued a fresh single-click
				// move (visible "shroomp detours back to the old chain
				// destination" surprise).
				string capturedName = name;
				_core.PostMainThreadCommand(() =>
				{
					var s = DevFindShroompByName(capturedName);
					s?.MoveOrderQueue.Clear();
				});
				_core.QueuePlayerOrder(name, target);
				i++;
			}
		}

		// v0.5.2 — RTS chain-order variant. Shift+right-click appends the
		// destination to each shroomp's MoveOrderQueue instead of replacing
		// CurrentTask. BehaviorSystem.Tick pops the queue head onto a
		// fresh PlayerOrder when the previous task completes (CurrentTask
		// becomes null) AND no critical-need override fires. The first
		// chained order may still pop immediately if the shroomp is currently
		// idle / between tasks — that's correct (chain start).
		//
		// Multi-shroomp with radial offsets matches the non-queued group API
		// so a box-selected squad chain-orders to coherent ring positions.
		public void RequestPlayerMoveOrderGroupQueued(System.Collections.Generic.IEnumerable<string> shroompNames,
			Vector2 worldPosPixels)
		{
			int i = 0;
			int count = 0;
			foreach (var _ in shroompNames) count++;
			if (count == 0) return;
			foreach (var name in shroompNames)
			{
				float ang = (float)(i * 2.0 * System.Math.PI / System.Math.Max(count, 1));
				var offset = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 12f;
				var target = worldPosPixels + offset;
				string capturedName = name;
				_core.PostMainThreadCommand(() =>
				{
					var s = DevFindShroompByName(capturedName);
					s?.MoveOrderQueue.Add(target);
				});
				i++;
			}
		}

		// v0.3.24 — combat order stub. Phase 8 will wire the BehaviorSystem
		// to read CombatTargetName and route an attack task. For now this
		// only sets the flag so the visual layer draws a sword icon over the
		// shroomp — it's deliberately data-plumbing-only until enemies exist.
		public void RequestCombatOrder(string shroompName, string targetName)
		{
			_core.QueueCombatOrder(shroompName, targetName);
		}

		public void ClearCombatOrder(string shroompName)
		{
			_core.QueueCombatOrder(shroompName, null);
		}

		// v0.3.39 (O-H.2) — main-thread → sim-thread camera-follow push.
		// GameController calls this once per frame; the sim thread reads
		// the field when assigning LOD tick phases (~every 32 sim ticks).
		// Race-tolerant: a one-tick-stale camera position is invisible at
		// the 320-px-wide LOD band granularity.
		public void SetCameraFollow(Godot.Vector2 worldPos)
		{
			_core.CameraFollow = worldPos;
		}

		// Roadmap §3.21 — designation API (drag-box from DesignationToolbar).
		// All three calls are idempotent: the map's setter checks `changed` before
		// firing DesignationChanged, so repeating the same call over the same
		// tile during a drag is free.

		public void SetExcavationDesignation(int tileX, int tileY, bool on)
		{
			_core.Map?.SetExcavationDesignation(tileX, tileY, on);
		}

		public void SetGatherDesignation(int tileX, int tileY, bool on)
		{
			_core.Map?.SetGatherDesignation(tileX, tileY, on);
		}

		public void ClearDesignationsAt(int tileX, int tileY)
		{
			_core.Map?.ClearDesignationsAt(tileX, tileY);
		}

		// Box-area helper — clamps the rect to map bounds and calls the
		// per-tile setter for each cell. The setter handles validity
		// (Boulder-only for Excavate, food-veg-only for Gather), so invalid
		// tiles in the box are silently skipped.
		public void DesignateRect(Sporeholm.UI.DesignationTool tool,
			int x0, int y0, int x1, int y1,
			Sporeholm.World.StructureMat? buildMaterial = null)
		{
			var map = _core.Map;
			if (map == null) return;
			int xMin = System.Math.Min(x0, x1);
			int xMax = System.Math.Max(x0, x1);
			int yMin = System.Math.Min(y0, y1);
			int yMax = System.Math.Max(y0, y1);

			// v0.4.12 — Haul order is item-keyed, not tile-keyed. Walk
			// every dropped item with TilePos inside the rect and mark
			// it for priority haul; HaulSystem.SelectHaulTarget will
			// pick these up before falling back to the radius-bounded
			// auto-haul scan. Doesn't go through the QueueDesignation
			// path because the LocalTile designation flags aren't
			// involved.
			if (tool == Sporeholm.UI.DesignationTool.Haul)
			{
				foreach (var (tx, ty, items) in map.EnumerateDroppedItems())
				{
					if (tx < xMin || tx > xMax || ty < yMin || ty > yMax) continue;
					foreach (var it in items)
						Sporeholm.Simulation.Systems.HaulSystem.MarkPriority(it.Id);
				}
				return;
			}

			// v0.4.27 — apply designations directly on the main thread
			// instead of enqueueing for the sim to drain on its next
			// Tick. Sam's gameplay requirement: "designations should
			// appear immediately, not load in over time". The sim-thread
			// queue (added in v0.3.39 to close a read-modify-write race
			// on the designation flags) gated the visual update behind
			// at least one sim tick + one main-thread `_Process` cycle —
			// noticeable at the player's 11 FPS perf-target frame rate.
			//
			// `LocalMap.SetXxxDesignation` already takes `_designationsLock`
			// internally, so the race v0.3.39 was guarding against stays
			// closed: every writer (sim ApplyTaskEffect, the auto-haul
			// path, this main-thread DesignateRect) serialises through
			// the same lock. Sim's `FindNearestX` reads under the same
			// lock too, so brief lock contention is the only cost —
			// vastly preferable to the multi-tick visual lag.
			//
			// `DesignationChanged` events fire from the SetXxx call
			// (already thread-safe), and the overlay's `_dirty` flag
			// becomes immediately visible to the main thread that
			// initiated the call.
			// v0.5.0 (Phase 5A — rimport N1) — Stockpile painter. Single
			// drag-rect creates / extends one stockpile zone. The first
			// painted cell creates the zone; subsequent cells in the same
			// rect extend it. Re-paint over an existing-zone cell is
			// idempotent (silently kept).
			int extendId = 0;
			for (int y = yMin; y <= yMax; y++)
			for (int x = xMin; x <= xMax; x++)
			{
				if (!map.InBounds(x, y)) continue;
				switch (tool)
				{
					case Sporeholm.UI.DesignationTool.Excavate:
						map.SetExcavationDesignation(x, y, true);
						break;
					case Sporeholm.UI.DesignationTool.Gather:
						map.SetGatherDesignation(x, y, true);
						break;
					case Sporeholm.UI.DesignationTool.ChopWood:
						map.SetChopWoodDesignation(x, y, true);
						break;
					case Sporeholm.UI.DesignationTool.Cut:
						map.SetCutDesignation(x, y, true);
						break;
					case Sporeholm.UI.DesignationTool.Remove:
						// v0.5.0 — Remove brush also clears stockpile membership.
						map.ClearDesignationsAt(x, y);
						map.ClearStockpileCell(x, y);
						break;
					case Sporeholm.UI.DesignationTool.Stockpile:
						extendId = map.SetStockpileCell(x, y, extendId);
						break;
					// v0.5.19 (Phase 5B) — construction blueprint painters.
					// Wall blueprints require the tile to be passable
					// terrain (no walls on water / boulders / existing
					// walls). Floor blueprints same. Material defaults to
					// Stone — the BehaviorSystem.Build task can swap to
					// Wood if the colony has more wood than stone at task
					// assignment time.
					case Sporeholm.UI.DesignationTool.BuildWall:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.WallPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.Stone));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					case Sporeholm.UI.DesignationTool.BuildFloor:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.FloorPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.Stone));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.20 (Phase 5C) — Door blueprint.
					case Sporeholm.UI.DesignationTool.BuildDoor:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.DoorPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.21 (Phase 5D) — Shelf blueprint (storage furniture).
					case Sporeholm.UI.DesignationTool.BuildShelf:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.ShelfPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.22 (Phase 5E) — Workbench blueprint.
					case Sporeholm.UI.DesignationTool.BuildWorkbench:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.WorkbenchPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.24 (Phase 5G) — Hearth blueprint (heat source).
					case Sporeholm.UI.DesignationTool.BuildHearth:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.HearthPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.Stone));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.35 (Phase 5 arc) — Bed blueprint.
					case Sporeholm.UI.DesignationTool.BuildBed:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.BedPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.36 (Phase 5 arc) — Joy furniture blueprints.
					case Sporeholm.UI.DesignationTool.BuildMeditationShrine:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.MeditationShrinePlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					case Sporeholm.UI.DesignationTool.BuildShroomBoard:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.ShroomBoardPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					case Sporeholm.UI.DesignationTool.BuildGossipBench:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.GossipBenchPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.37 (Phase 5 arc) — Table blueprint.
					case Sporeholm.UI.DesignationTool.BuildTable:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.TablePlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					// v0.5.84t — Torch blueprint. Cheap floor-tile light source +
					// small heat (Room.TorchCount × +2°C). Built from wood;
					// future "wood + grass" recipe extension would need a
					// multi-ingredient BuildMaterialCost refactor.
					case Sporeholm.UI.DesignationTool.BuildTorch:
						if (CanPlaceBlueprint(map, x, y))
						{
							map.SetStructure(x, y,
								Sporeholm.World.StructureSlot.Blueprint(
									Sporeholm.World.StructureType.TorchPlanned,
									buildMaterial ?? Sporeholm.World.StructureMat.DeadWood));
							AutoAddPrepDesignations(map, x, y);
						}
						break;
					case Sporeholm.UI.DesignationTool.Demolish:
						// v0.5.20 — refund 50 % of original cost when
						// demolishing a built structure (RimWorld pattern).
						// v0.5.34 — refund logic now reads MaterialsDelivered
						// so mid-delivery cancels recover the delivered units.
						// Pending blueprints with no delivery get full clear
						// (nothing was consumed); frames + builds refund
						// 50 % of delivered.
						if (map.HasStructure(x, y))
						{
							var dStruct = map.GetStructure(x, y);
							bool hasInvestment = dStruct.IsBuilt
								|| (dStruct.IsBlueprint && (dStruct.MaterialsDelivered > 0 || dStruct.BuildProgress > 0));
							if (hasInvestment)
							{
								int delivered = dStruct.IsBuilt
									? Sporeholm.World.StructureSlot.BuildMaterialCost(dStruct.Type)
									: dStruct.MaterialsDelivered;
								int refundAmount = System.Math.Max(1, delivered / 2);
								// Drop the refund as an item on the demolished
								// tile so it joins the haul flow naturally.
								_core?.PostMainThreadCommand(() =>
								{
									if (_core?.Resources == null) return;
									// v0.5.32 — refund family resolved through the central
									// helper so wood sub-materials (FungalWood / LivingWood /
									// etc.) refund into the colony's generic Wood pool.
									string family = Sporeholm.World.StructureMatMeta.ConsumeFamily(dStruct.Material);
									string subType = family == "Wood" ? "WoodLog" : "StoneBlock";
									string matSubType = family == "Wood" ? "DeadWood" : "Granite";
									var refundItem = Sporeholm.Simulation.Items.ItemFactory.Create(
										Sporeholm.Simulation.Items.ItemKind.Material, subType,
										new Sporeholm.Simulation.Items.MaterialKey(family, matSubType),
										new System.Random(),
										0,
										skillLevel: 0,
										quantity: refundAmount);
									refundItem.TilePos = new Godot.Vector2(
										x * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f,
										y * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f);
									map.DropItem(refundItem);
								});
							}
							map.SetStructure(x, y, Sporeholm.World.StructureSlot.Empty);
						}
						break;
				}
			}
		}

		// v0.5.31 — relaxed blueprint placement gate. Old check rejected any
		// non-passable tile, which blocked the player from queueing a wall
		// on top of a boulder / dead log / vegetation. RimWorld lets you
		// place a blueprint anywhere reasonable; constructors handle the
		// clearing as part of the build job. This helper now only rejects
		// Water (no walls on rivers) and tiles already occupied by another
		// structure. Vegetation and impassable terrain are allowed —
		// AutoAddPrepDesignations spawns the cleanup designations the
		// constructor (or any spare worker) will use to clear the tile
		// before the build proceeds.
		private static bool CanPlaceBlueprint(Sporeholm.World.LocalMap map, int x, int y)
		{
			if (!map.InBounds(x, y)) return false;
			var terrain = map.Get(x, y).Terrain;
			if (terrain == Sporeholm.World.TerrainType.Water) return false;   // never on rivers
			// v0.5.84c — allow furniture/walls/doors on top of an existing
			// Floor (and floor on top of existing furniture/walls/doors)
			// for the floor-under-everything stacking model. Sam:
			// "Furniture and its blueprints should also appear/be able to
			// be built on top of flooring for gameplay purposes." Three
			// rules:
			//   1. Empty slot → place freely (existing).
			//   2. Slot holds Floor → any non-floor blueprint is allowed;
			//      a second Floor blueprint is rejected (already floored).
			//   3. Slot holds non-Floor → a Floor blueprint is allowed
			//      (the floor will paint underneath when built); a non-
			//      floor blueprint is rejected (one wall/furniture/door
			//      per tile still).
			// Currently we accept any non-empty slot for any blueprint
			// type because the placement caller (the build orders for
			// each StructureType) targets a specific kind — the per-kind
			// build-order site filters further. This relaxed check just
			// stops blocking the stack-compatible combinations.
			if (!map.HasStructure(x, y)) return true;
			// Slot occupied — only accept if the existing thing is a
			// Floor (so a furniture blueprint can stack onto it). The
			// reverse case (placing Floor onto existing furniture) is
			// also valid but the BlueprintForType dispatch picks the
			// blueprint variant; we don't see that here, so allow any
			// secondary placement when one of the two is a floor.
			var existing = map.GetStructure(x, y);
			if (existing.Type == Sporeholm.World.StructureType.Floor
			    || existing.Type == Sporeholm.World.StructureType.FloorPlanned)
				return true;   // existing floor accepts a furniture/wall blueprint on top
			// Existing non-floor structure; reject for now (the floor-
			// under-existing-furniture case is a future polish — adding
			// it requires inverse blueprint semantics that aren't shipped
			// in this pass).
			return false;
		}

		// v0.5.31 — when a blueprint is placed on an obstructed tile, spawn
		// the appropriate clearing designation so the cleanup work joins
		// the colony's normal task pool. Three obstruction cases:
		//
		//   1. Impassable terrain (Boulder / DeadLog / LivingWood / Skeleton)
		//      → Excavate designation. Any miner with priority 1-4 picks
		//        it up; if none has Mine priority, the Build branch in
		//        BehaviorSystem.SelectTask redirects the Crafter to do
		//        the excavation themselves (bypasses the Mine priority
		//        gate because clearing is a sub-step of construction).
		//
		//   2. Non-depleted vegetation (trees / bushes / herbs / underbrush)
		//      → Cut designation. CutVegetation in BehaviorSystem already
		//        handles tree-class shrooms (FullyDeplete + flip
		//        passability) and decoration (ClearVegetation outright);
		//        depleted stumps require no further work since
		//        FullyDepleteVegetation has flipped them to passable.
		//
		//   3. Already build-ready (passable + no vegetation) — no-op.
		//
		// The designations are additive: the player can also place them
		// manually with the same effect. Removing a blueprint mid-clear
		// does NOT clear the prep designations — they remain so the
		// material harvest still happens (matches RimWorld where cancelling
		// a build doesn't undo a tree-chop designation that was added by
		// the build).
		private static void AutoAddPrepDesignations(Sporeholm.World.LocalMap map, int x, int y)
		{
			var terrain = map.Get(x, y).Terrain;
			bool terrainBlocking =
				terrain == Sporeholm.World.TerrainType.Boulder    ||
				terrain == Sporeholm.World.TerrainType.DeadLog    ||
				terrain == Sporeholm.World.TerrainType.LivingWood ||
				terrain == Sporeholm.World.TerrainType.Skeleton;
			if (terrainBlocking)
				map.SetExcavationDesignation(x, y, true);

			var veg = map.GetVegetation(x, y);
			if (veg.IsPresent && !veg.IsDepleted)
				map.SetCutDesignation(x, y, true);
		}

		// v0.5.31 — query used by BehaviorSystem.SelectTask Build branch
		// to decide whether a blueprint tile is ready for the Build task
		// or still needs clearing. Mirrors AutoAddPrepDesignations'
		// obstruction checks. Exposed as static so it can be called from
		// both this manager and BehaviorSystem without a back-reference.
		public static bool IsBlueprintBuildReady(Sporeholm.World.LocalMap map, int x, int y)
		{
			if (!map.InBounds(x, y)) return false;
			var terrain = map.Get(x, y).Terrain;
			if (terrain == Sporeholm.World.TerrainType.Boulder    ||
				terrain == Sporeholm.World.TerrainType.DeadLog    ||
				terrain == Sporeholm.World.TerrainType.LivingWood ||
				terrain == Sporeholm.World.TerrainType.Skeleton)
				return false;
			var veg = map.GetVegetation(x, y);
			if (veg.IsPresent && !veg.IsDepleted) return false;
			return true;
		}

		// v0.5.0 (Phase 5A — rimport N5) — universal Forbid/Allow toggle on
		// every item at a tile. Routes through PostMainThreadCommand so the
		// write happens on the sim thread (matches v0.4.55's pattern for
		// other player-driven mutations on Item state). Items already in
		// the requested state are left alone.
		public void SetForbiddenOnTile(int tx, int ty, bool forbid)
		{
			var map = _core?.Map;
			if (map == null) return;
			_core!.PostMainThreadCommand(() =>
			{
				var items = map.GetItemsOnTile(tx, ty);
				for (int i = 0; i < items.Count; i++)
				{
					if (items[i].IsForbidden != forbid)
						items[i].IsForbidden = forbid;
				}
				map.NotifyItemsChanged(tx, ty);
			});
		}

		// Read-only view of the colony resource ledger for HUD display.
		public ColonyResources GetResourcesSnapshot() => _core.Resources.Snapshot();

		// v0.3.46 (Phase 4) — main-thread snapshot of the colony inventory
		// as a flat array of value-type rows. Locks internally; safe to
		// call at HUD refresh rate. Returns an empty array if the sim
		// thread hasn't constructed Resources yet (early boot).
		public Sporeholm.Simulation.Items.InventoryRow[] GetInventorySnapshot() =>
			_core?.Resources?.Inventory?.Snapshot()
				?? System.Array.Empty<Sporeholm.Simulation.Items.InventoryRow>();

		// v0.3.47 — current sim-thread tick counter. Save format uses this
		// to compute item AgeInTicks at save time so reload can re-anchor
		// to the new GlobalTick without losing the spoilage clock.
		public long GetGlobalTick() => _core?.GlobalTick ?? 0;

		// v0.4.7 (bugreport B-1) — per-shroomp "save extras" snapshot for
		// the equipment/handedness/carried/preferences/thoughts save
		// fields. Each entry carries the full fidelity of the live
		// state so save round-trip is loss-less. Walks under the shroomp
		// lock; safe to call from the save path (main thread).
		public System.Collections.Generic.Dictionary<string, ShroompSaveExtras>
			GetShroompSaveExtras(long globalTick)
		{
			var result = new System.Collections.Generic.Dictionary<string, ShroompSaveExtras>();
			if (_core == null) return result;
			foreach (var s in _core.AllShroomps())
			{
				if (!s.IsAlive) continue;
				// v0.5.73 — full Inventory snapshot (was: only the topmost
				// stack via CarriedItem getter, which lost everything below
				// it for haulers carrying >1 stack).
				System.Collections.Generic.List<SaveManager.ItemSaveData>? invList = null;
				if (s.Inventory != null && s.Inventory.Count > 0)
				{
					invList = new System.Collections.Generic.List<SaveManager.ItemSaveData>(s.Inventory.Count);
					foreach (var it in s.Inventory)
						invList.Add(SnapshotItem(it, globalTick));
				}
				result[s.Name] = new ShroompSaveExtras
				{
					Handedness  = s.Handedness.ToString(),
					Equipment   = SnapshotShroompEquipment(s, globalTick),
					CarriedItem = s.CarriedItem != null
						? SnapshotItem(s.CarriedItem, globalTick) : null,
					Inventory   = invList,
					Preferences = SnapshotPreferences(s.Preferences),
					Thoughts    = SnapshotThoughts(s.Thoughts),
				};
			}
			return result;
		}

		// Per-Shroomp save extras returned by GetShroompSaveExtras. Shape
		// matches the save-record fields so SaveManager.BuildShroompList
		// can copy directly across.
		public sealed class ShroompSaveExtras
		{
			public string                                  Handedness   = "Right";
			public System.Collections.Generic.List<SaveManager.EquipmentSaveData>? Equipment    = null;
			public SaveManager.ItemSaveData?               CarriedItem  = null;
			// v0.5.73 — full Shroomp.Inventory snapshot. Pre-v0.5.73 only
			// the topmost stack (CarriedItem getter) was saved; a hauler
			// with multiple stacks lost the rest. Loaders read Inventory
			// first; CarriedItem stays for older saves.
			public System.Collections.Generic.List<SaveManager.ItemSaveData>?      Inventory    = null;
			public SaveManager.PreferencesSaveData?        Preferences  = null;
			public System.Collections.Generic.List<SaveManager.ThoughtSaveData>?   Thoughts     = null;
		}

		// v0.4.7 — snapshot a single Item into the save record. Mirrors
		// the inventory-save path in SaveToSlot.
		// v0.4.35 — corpse-kind items also serialise their CorpseInfo
		// sidecar so reload restores the dead shroomp's name / cause /
		// personality on the obituary line. Non-corpse items leave the
		// `Corpse` init property at its null default.
		private static SaveManager.ItemSaveData SnapshotItem(
			Sporeholm.Simulation.Items.Item it, long globalTick)
		{
			long age = System.Math.Max(0, globalTick - it.AvgBirthTick);
			var rec = new SaveManager.ItemSaveData(
				it.Kind.ToString(), it.SubType,
				it.Material.Family, it.Material.SubType,
				it.Quality.ToString(), it.State.ToString(),
				it.Quantity, it.AvgCondition, it.DurabilityCap,
				age);
			if (it.CorpseInfo != null)
			{
				var c = it.CorpseInfo;
				rec = rec with
				{
					Corpse = new SaveManager.CorpseSaveData(
						Name:          c.Name,
						AgeYears:      c.AgeYears,
						Sex:           c.Sex.ToString(),
						Role:          c.Role,
						Cause:         c.Cause.ToString(),
						DeathAgeTicks: System.Math.Max(0, globalTick - c.DeathTick),
						Personality:   new System.Collections.Generic.List<string>(c.Personality),
						Handedness:    c.Handedness.ToString()),
				};
			}
			return rec;
		}

		private static System.Collections.Generic.List<SaveManager.EquipmentSaveData>?
			SnapshotShroompEquipment(Shroomp s, long globalTick)
		{
			if (s.Equipment == null || s.Equipment.Count == 0) return null;
			var list = new System.Collections.Generic.List<SaveManager.EquipmentSaveData>(s.Equipment.Count);
			foreach (var (slot, item) in s.Equipment)
			{
				list.Add(new SaveManager.EquipmentSaveData(slot.ToString(), SnapshotItem(item, globalTick)));
			}
			return list;
		}

		private static SaveManager.PreferencesSaveData? SnapshotPreferences(
			Sporeholm.Simulation.Preferences? p)
		{
			if (p == null) return null;
			return new SaveManager.PreferencesSaveData
			{
				LikedItems         = new System.Collections.Generic.List<string>(p.LikedItems),
				DislikedItems      = new System.Collections.Generic.List<string>(p.DislikedItems),
				LikedActivities    = new System.Collections.Generic.List<string>(p.LikedActivities),
				DislikedActivities = new System.Collections.Generic.List<string>(p.DislikedActivities),
				LikedShroomps        = new System.Collections.Generic.List<string>(p.LikedShroomps),
				DislikedShroomps     = new System.Collections.Generic.List<string>(p.DislikedShroomps),
			};
		}

		private static System.Collections.Generic.List<SaveManager.ThoughtSaveData>?
			SnapshotThoughts(Sporeholm.Simulation.Thought[]? ring)
		{
			if (ring == null) return null;
			var list = new System.Collections.Generic.List<SaveManager.ThoughtSaveData>();
			for (int i = 0; i < ring.Length; i++)
			{
				var t = ring[i];
				if (t.TicksRemaining <= 0 || string.IsNullOrEmpty(t.Key)) continue;
				list.Add(new SaveManager.ThoughtSaveData(
					t.Key, t.TicksRemaining, t.MoodOffset, t.Context ?? ""));
			}
			return list.Count > 0 ? list : null;
		}

		// v0.4.7 — main-thread snapshot of the LocalMap's dropped items
		// for save. Just forwards to the locked LocalMap.SnapshotDroppedItems
		// + transforms to save records.
		public System.Collections.Generic.List<SaveManager.DroppedItemSaveData>?
			GetDroppedItemsSnapshot(long globalTick)
		{
			var map = WorldState.Instance?.CurrentLocalMap;
			if (map == null) return null;
			var raw = map.SnapshotDroppedItems();
			if (raw.Count == 0) return null;
			var result = new System.Collections.Generic.List<SaveManager.DroppedItemSaveData>(raw.Count);
			foreach (var (x, y, it) in raw)
			{
				result.Add(new SaveManager.DroppedItemSaveData(x, y, SnapshotItem(it, globalTick)));
			}
			return result;
		}

		// v0.3.47 — per-shroomp work priorities snapshot for the Jobs tab
		// save path. Walks the live shroomp list under the sim lock and
		// shallow-copies each WorkPriorities dict.
		public System.Collections.Generic.Dictionary<string,
			System.Collections.Generic.Dictionary<string, byte>> GetWorkPrioritiesSnapshot()
		{
			var result = new System.Collections.Generic.Dictionary<string,
				System.Collections.Generic.Dictionary<string, byte>>();
			if (_core == null) return result;
			foreach (var s in _core.AllShroomps())
			{
				if (s.WorkPriorities == null || s.WorkPriorities.Count == 0) continue;
				result[s.Name] = new System.Collections.Generic.Dictionary<string, byte>(s.WorkPriorities);
			}
			return result;
		}

		// v0.4.3 — equip / unequip / drop wired from the Inventory tab on
		// the shroomp card. Each call posts a delegate onto the sim
		// thread's pending command queue; the sim drains them at the
		// top of Tick. Direct mutation of Shroomp.Equipped* + Inventory
		// from the UI thread would race with BehaviorSystem reads, so
		// we queue.
		// v0.4.4 — equip target now identifies the specific EquipSlot
		// the player picked in the Inventory tab (Head, Torso, LeftHand,
		// RightHand, LeftFoot, RightFoot, …). The card sends a slot
		// hint of "auto" for hand items, in which case we resolve to
		// the shroomp's dominant hand. Empty slot string falls back to
		// the body-class default (matches v0.4.3 behaviour).
		public void RequestEquip(string shroompName, string subType, string materialFamily, string materialSubType, string slotHint = "auto")
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindShroomp(shroompName); if (s == null) return;
				var item = TakeFromColonyInventory(subType, materialFamily, materialSubType);
				if (item == null) return;

				var def = Sporeholm.Simulation.Items.ItemRegistry.Get(item.Kind, item.SubType);
				// v0.4.7 (bugreport B-4) — bounce back to inventory if the
				// item's sub-type isn't in the registry. Previously the
				// item was consumed from inventory but never returned,
				// silently leaking on registry mismatches.
				if (def == null) { _core.Resources.Inventory.Add(item); return; }
				var slot = ResolveEquipSlot(s, def, slotHint);
				if (slot == Sporeholm.Simulation.Items.EquipSlot.None)
				{
					// Item isn't slot-equipable — bounce back to inventory.
					_core.Resources.Inventory.Add(item);
					return;
				}

				if (s.Equipment.TryGetValue(slot, out var displaced))
				{
					displaced.OwnerShroompId = null;
					_core.Resources.Inventory.Add(displaced);
				}
				item.OwnerShroompId = s.Id;
				s.Equipment[slot] = item;
			});
		}

		// Resolves the target slot for a (shroomp, item, hint) triple.
		// hint = "auto" defers to body-class + handedness; hint =
		// "LeftHand"/"RightHand"/etc. forces that slot.
		private static Sporeholm.Simulation.Items.EquipSlot ResolveEquipSlot(
			Shroomp s, Sporeholm.Simulation.Items.ItemSubTypeDef def, string hint)
		{
			if (System.Enum.TryParse<Sporeholm.Simulation.Items.EquipSlot>(hint, out var explicitSlot)
				&& explicitSlot != Sporeholm.Simulation.Items.EquipSlot.None)
				return explicitSlot;
			var slots = Sporeholm.Simulation.Items.EquipSlotMeta.SlotsFor(def.BodyClass);
			if (slots.Length == 0) return Sporeholm.Simulation.Items.EquipSlot.None;
			if (slots.Length == 1) return slots[0];
			// Hand / Foot / Arm / Leg classes — pick the dominant side
			// (or, for foot/arm/leg, fill the empty side first).
			if (def.BodyClass == Sporeholm.Simulation.Items.EquipSlotMeta.BodyClass.Hand)
			{
				var dom = HandednessMeta.DominantHand(s.Handedness);
				if (!s.Equipment.ContainsKey(dom)) return dom;
				var off = HandednessMeta.OffHand(s.Handedness);
				if (!s.Equipment.ContainsKey(off)) return off;
				return dom;   // both occupied → replace dominant
			}
			// Paired non-hand slots — fill empty side first.
			foreach (var sl in slots) if (!s.Equipment.ContainsKey(sl)) return sl;
			return slots[0];
		}

		// v0.4.4 — slot string is now the EquipSlot enum name
		// ("Head", "Torso", "LeftHand", "RightHand", "LeftFoot", …) or
		// "Carried" for the haul-carry slot.
		public void RequestUnequip(string shroompName, string slot)
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindShroomp(shroompName); if (s == null) return;
				if (System.Enum.TryParse<Sporeholm.Simulation.Items.EquipSlot>(slot, out var es)
					&& s.Equipment.TryGetValue(es, out var current))
				{
					s.Equipment.Remove(es);
					current.OwnerShroompId = null;
					_core.Resources.Inventory.Add(current);
				}
			});
		}

		public void RequestDropEquipped(string shroompName, string slot)
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindShroomp(shroompName); if (s == null) return;
				Sporeholm.Simulation.Items.Item? current = null;
				if (slot == "Carried")
				{
					current = s.CarriedItem;
					s.CarriedItem = null;
				}
				else if (System.Enum.TryParse<Sporeholm.Simulation.Items.EquipSlot>(slot, out var es)
					&& s.Equipment.TryGetValue(es, out var eq))
				{
					current = eq;
					s.Equipment.Remove(es);
				}
				if (current == null) return;
				current.OwnerShroompId = null;
				// v0.4.7 (bugreport B-6) — if the local map isn't bound
				// (scene transition / exit-to-menu race), the item would
				// orphan silently. Bounce back to the colony inventory
				// as a fallback so nothing leaks.
				var map = WorldState.Instance?.CurrentLocalMap;
				if (map == null)
				{
					_core.Resources.Inventory.Add(current);
					return;
				}
				current.TilePos = s.SimPos;
				map.DropItem(current);
			});
		}

		// v0.4.3 — right-click "pick up this item" order. Queues a
		// BehaviorSystem.PlayerOrder pointing at the item's tile;
		// BehaviorSystem treats arrival as a Haul-pickup that puts the
		// item into the shroomp's CarriedItem and then routes to the
		// colony delivery point.
		public void RequestPickUp(string shroompName, Godot.Vector2 itemTile)
		{
			_core?.PendingPickUps?.Enqueue((shroompName, itemTile));
		}

		private Shroomp? FindShroomp(string name)
		{
			if (_core == null) return null;
			foreach (var s in _core.AllShroomps()) if (s.Name == name) return s;
			return null;
		}

		// Pops a matching item out of the colony inventory by
		// (SubType, MaterialFamily, MaterialSubType). Returns null if
		// no match.
		private Sporeholm.Simulation.Items.Item? TakeFromColonyInventory(
			string subType, string materialFamily, string materialSubType)
		{
			if (_core == null) return null;
			var inv = _core.Resources.Inventory;
			var rows = inv.Items;
			Sporeholm.Simulation.Items.Item? match = null;
			foreach (var it in rows)
			{
				if (it.SubType != subType) continue;
				if (it.Material.Family != materialFamily) continue;
				if (it.Material.SubType != materialSubType) continue;
				match = it; break;
			}
			if (match == null) return null;

			// Split one unit off a stack — that's what we equip; the
			// rest stays in inventory.
			if (match.Quantity > 1)
			{
				var single = new Sporeholm.Simulation.Items.Item
				{
					Kind          = match.Kind,
					SubType       = match.SubType,
					Material      = match.Material,
					Quality       = match.Quality,
					State         = match.State,
					AvgCondition  = match.AvgCondition,
					DurabilityCap = match.DurabilityCap,
					AvgBirthTick  = match.AvgBirthTick,
					Quantity      = 1,
				};
				inv.Consume(match, 1);
				return single;
			}
			inv.Consume(match, 1);
			return match;
		}

		// v0.4.4 — EquipOnShroomp helper removed. Equip is now handled
		// inline in RequestEquip via ResolveEquipSlot, which knows
		// about handedness + paired slots.

		// v0.3.47 — apply a per-shroomp work priorities edit from the Jobs
		// tab UI. Called on the main thread; locks via AllShroomps internally.
		public void SetWorkPriority(string shroompName, string category, byte priority)
		{
			if (_core == null) return;
			foreach (var s in _core.AllShroomps())
			{
				if (s.Name != shroompName) continue;
				if (s.WorkPriorities == null) s.WorkPriorities = new();
				s.WorkPriorities[category] = priority;
				return;
			}
		}

		// ── Colony seeding ─────────────────────────────────────────────────────────

		private void SeedColony()
		{
			if (SaveManager.Instance?.HasSave == true &&
				SaveManager.Instance.CurrentSave is { } save)
			{
				LoadFromSave(save);
				return;
			}

			// Scenario seeding (configured on ScenarioPanel before this scene
			// loaded). The pending scenario is consumed once and cleared so a
			// subsequent quick-start without revisiting the scenario screen
			// falls back to the legacy founding seven.
			var scenario = WorldState.Instance?.PendingScenario;
			if (scenario != null && scenario.Shroomps.Count > 0)
			{
				foreach (var t in scenario.Shroomps)
					AddShroompFromTemplate(t);
				WorldState.Instance!.PendingScenario = null;
				return;
			}

			// Legacy quick-start: founding seven. Skill seeds from Roadmap §2.5.
			AddShroomp("Papa",      542, "Elder",     Sex.Male);
			AddShroomp("Brainy",    98,  "Scholar",   Sex.Male);
			AddShroomp("Hefty",     75,  "Guardian",  Sex.Male);
			AddShroomp("SporeMother", 22,  "Caretaker", Sex.Female);
			AddShroomp("Clumsy",    45,  "Forager",   Sex.Male);
			AddShroomp("Handy",     61,  "Crafter",   Sex.Male);
			AddShroomp("Grouchy",   83,  "Forager",   Sex.Male);
		}

		// Build a Shroomp from a ScenarioPanel template. Unlike the legacy
		// AddShroomp path, personality may be pre-set by the player; we only
		// assign random personality if the template's list is empty.
		// Biological traits still roll randomly — they're penetrance values,
		// not a player-facing checkbox per the §0.x trait design.
		private void AddShroompFromTemplate(ShroompTemplate t)
		{
			var s = new Shroomp
			{
				Name              = t.Name,
				AgeInYears        = t.Age,
				Role              = t.Role,
				Sex               = t.Sex,
				BirthdayDayOfYear = _rng.Next(0, 120),
			};
			TraitRegistry.AssignDawnEraTraits(s, _rng);
			SkillRegistry.Distribute(s, _rng);
			// v0.4.64 (G6) — assign backstory AFTER skill distribute so the
			// childhood/adulthood bumps layer on top of the budget roll.
			BackstoryRegistry.AssignAndApply(s, _rng);
			s.Personality = t.Personality.Count > 0
				? new List<string>(t.Personality)
				: PersonalityRegistry.Assign(_rng, t.Age);
			s.BodyParts   = BodyPartRegistry.CreateHealthy();
			// v0.3.43 — scenario-rolled preferences if the template carries
			// them; otherwise fresh roll. ScenarioPanel populates t.Preferences
			// during scenario setup so the player sees the preference summary
			// on the shroomp card before confirming.
			s.Preferences = t.Preferences ?? PreferenceRegistry.Assign(_rng, s.Personality);
			WorkPriorityDefaults.ApplyRoleDefaults(s);
			s.Handedness = HandednessMeta.Roll(_rng);
			ShroompIdentity.Roll(s, _rng);   // v0.5.63 cap-and-stem identity
			_core.AddShroomp(s);

			// v0.3.47 (Phase 4 sub-B) — deposit scenario-rolled starting
			// kit into the colony inventory. Each shroomp's items merge into
			// the shared pool via Inventory.Add's stacking rules.
			if (t.StartingItems != null)
			{
				foreach (var item in t.StartingItems)
				{
					// Re-stamp birth tick to the current sim tick so the
					// spoilage clock starts fresh on game start (items
					// were rolled with tick = 0 in the scenario screen).
					item.AvgBirthTick = _core.GlobalTick;
					_core.Resources.Inventory.Add(item);
				}
			}
		}

		private void AddShroomp(string name, int age, string role, Sex sex)
		{
			var s = new Shroomp
			{
				Name       = name,
				AgeInYears = age,
				Role       = role,
				Sex        = sex,
				BirthdayDayOfYear = _rng.Next(0, 120),
			};
			TraitRegistry.AssignDawnEraTraits(s, _rng);
			SkillRegistry.Distribute(s, _rng);
			// v0.4.64 (G6) — assign backstory AFTER skill distribute.
			BackstoryRegistry.AssignAndApply(s, _rng);
			s.Personality = PersonalityRegistry.Assign(_rng, s.AgeInYears);
			s.BodyParts   = BodyPartRegistry.CreateHealthy();
			s.Preferences = PreferenceRegistry.Assign(_rng, s.Personality);
			WorkPriorityDefaults.ApplyRoleDefaults(s);
			s.Handedness = HandednessMeta.Roll(_rng);
			ShroompIdentity.Roll(s, _rng);   // v0.5.63 cap-and-stem identity
			_core.AddShroomp(s);
		}

		private void LoadFromSave(SaveManager.ColonySave save)
		{
			if (System.Enum.TryParse<Season>(save.Season, out var season))
			{
				_core.SetStartDate(new SimulationDate
				{
					Year   = save.Year,
					Season = season,
					Day    = save.Day,
					Hour   = 6,
				});
			}

			foreach (var sd in save.Shroomps)
			{
				// v0.5.84t — legacy role migration. The "Mage" role was renamed
				// to "Sage" in v0.5.84t; old saves' shroomps come back with
				// Role="Mage" and would silently fall out of all the role-
				// keyed dicts (SkillRegistry / WorkPriorityDefaults / Needs
				// decay table). Map forward so old colonies keep their
				// Sage-equivalent behaviour.
				string migratedRole = sd.Role == "Mage" ? "Sage" : sd.Role;
				var s = new Shroomp
				{
					Name              = sd.Name,
					AgeInYears        = sd.Age,
					Role              = migratedRole,
					Sex               = sd.Sex == "Female" ? Sex.Female : Sex.Male,
					Nutrition         = sd.Nutrition,
					Rest              = sd.Rest,
					Social            = sd.Social,
					MagicResonance    = sd.MagicResonance,
					Safety            = sd.Safety,
					BirthdayDayOfYear = sd.BirthdayDayOfYear,
				};

				// Restore traits; assign fresh if save predates this system.
				if (sd.Traits != null && sd.Traits.Count > 0)
				{
					foreach (var (k, v) in sd.Traits) s.Traits[k] = v;
					// v0.5.84t — migrate pre-rename scientific-Latin trait keys
					// (MagicalAptitude / Miniaturization / HaemocyaninMetabolism
					// / etc.) onto the new mushroom-themed keys BEFORE the
					// back-fill so accumulated penetrance carries forward
					// rather than being overwritten with a fresh Dawn-Era roll.
					TraitRegistry.MigrateLegacyTraitNames(s);
					// Back-fill any traits added to the registry after this save was made.
					foreach (var def in TraitRegistry.All)
						if (!s.Traits.ContainsKey(def.Name))
							s.Traits[def.Name] = (float)(def.DawnFloor + _rng.NextDouble() * (def.DawnCeiling - def.DawnFloor));
				}
				else
					TraitRegistry.AssignDawnEraTraits(s, _rng);

				// Restore skills; seed fresh if save predates this system.
				if (sd.Skills != null && sd.Skills.Count > 0)
				{
					foreach (var (k, v) in sd.Skills) s.Skills[k] = v;
					// v0.5.84r — migrate pre-restructure skill keys (Foraging→
					// Botany, Arcane/Ritual/Lore→Magic, Empathy/Leadership→
					// Social, Medicine→Healing, Research→Study, Cooking→Crafting).
					// Applied BEFORE the missing-skills back-fill so an old
					// save's accumulated XP gets folded into the new merged
					// skill rather than ignored.
					SkillRegistry.MigrateLegacySkillKeys(s.Skills, s.SkillsXp, s.SkillsXpToday);
					// Back-fill any skills added to the registry after this save was made.
					foreach (var def in SkillRegistry.All)
						if (!s.Skills.ContainsKey(def.Name))
							s.Skills[def.Name] = 0;
				}
				else
					SkillRegistry.Distribute(s, _rng);

				// Restore personality; assign fresh if save predates this system.
				if (sd.Personality != null && sd.Personality.Count > 0)
					s.Personality = new List<string>(sd.Personality);
				else
					s.Personality = PersonalityRegistry.Assign(_rng, s.AgeInYears);

				// v0.3.43 — preferences. Saves predating this system get a
				// fresh roll on load; in-place preferences will be added to
				// ShroompSaveData in a future patch when persistence is
				// hooked up. For now newborns + scenario shroomps carry
				// preferences in-memory only.
				s.Preferences = PreferenceRegistry.Assign(_rng, s.Personality);

				// Restore body parts; create healthy if save predates this system.
				if (sd.BodyParts != null && sd.BodyParts.Count > 0)
				{
					s.BodyParts = new Dictionary<string, float>(sd.BodyParts);
					// v0.5.84q — migrate pre-rename keys (Head → Cap,
					// Torso → Stalk, Lung → Gill, Nose → Spore Vent,
					// Liver → Filter) before the missing-part fill pass
					// so a v0.5.83 save loads with its body-part values
					// preserved rather than reset to 100.
					BodyPartRegistry.MigrateLegacyNames(s.BodyParts);
					foreach (var def in BodyPartRegistry.Template)
						if (!s.BodyParts.ContainsKey(def.Name))
							s.BodyParts[def.Name] = 100f;
				}
				else
					s.BodyParts = BodyPartRegistry.CreateHealthy();

				// v0.5.81 — restore bleeding reservoir. Default 0 on saves
				// predating Phase 7 prep (the field's nullable-with-default
				// init means old saves deserialise to 0 cleanly).
				s.BloodLoss = sd.BloodLoss;

				// v0.3.35 — restore SimPos / SimTarget / SimSpeed from the
				// save so the shroomp re-enters the sim at its saved tile, not
				// at Vector2.Zero (which SeedSimPositions would then clobber
				// with the spawn-cluster centre, teleporting the colony to
				// the middle of the map when the player unpaused). Saves
				// predating these fields wrote 0 for both — guarded so we
				// don't restore a zero position that would re-trigger the
				// spawn-cluster fallback path.
				if (sd.PosX != 0f || sd.PosY != 0f)
				{
					s.SimPos    = new Vector2(sd.PosX,    sd.PosY);
					s.SimTarget = sd.TargetX != 0f || sd.TargetY != 0f
						? new Vector2(sd.TargetX, sd.TargetY)
						: s.SimPos;
				}
				s.SimSpeed = SpeedForRole(s.Role);

				// v0.3.47 (Phase 4 sub-B) — restore work priorities. Saves
				// predating this field fall through to the role defaults.
				if (save.WorkPriorities != null
					&& save.WorkPriorities.TryGetValue(s.Name, out var prios))
				{
					s.WorkPriorities = new Dictionary<string, byte>(prios);
					// v0.5.84r — migrate legacy category keys
					// (Doctor → Healer, Research → Study) on save load
					// so old colonies keep their priority settings.
					WorkPriorityDefaults.MigrateLegacyCategoryKeys(s.WorkPriorities);
				}
				WorkPriorityDefaults.ApplyRoleDefaults(s);

				// v0.4.7 (bugreport B-1) — restore handedness if saved,
				// else roll fresh. Equipment / Carried / Preferences /
				// Thoughts restored below from the corresponding fields.
				if (sd.Handedness != null
					&& System.Enum.TryParse<Handedness>(sd.Handedness, out var h))
					s.Handedness = h;
				else
					s.Handedness = HandednessMeta.Roll(_rng);

				// v0.4.7 — restore equipment dict. Falls back to empty
				// dict when not present; auto-equip on next task will
				// repopulate from inventory.
				if (sd.Equipment != null && sd.Equipment.Count > 0)
				{
					long now = _core.GlobalTick;
					foreach (var entry in sd.Equipment)
					{
						if (!System.Enum.TryParse<Sporeholm.Simulation.Items.EquipSlot>(entry.Slot, out var slot))
							continue;
						var item = RehydrateItem(entry.Item, now);
						if (item == null) continue;
						item.OwnerShroompId = s.Id;
						s.Equipment[slot] = item;
					}
				}

				// v0.5.73 — full Inventory restore takes precedence; falls
				// back to the legacy single CarriedItem for saves predating
				// the full-list field. TilePos=null marks "in hand" so the
				// item renderer doesn't treat it as a dropped pile.
				if (sd.Inventory != null && sd.Inventory.Count > 0)
				{
					long now = _core.GlobalTick;
					s.Inventory.Clear();
					foreach (var rec in sd.Inventory)
					{
						var item = RehydrateItem(rec, now);
						if (item == null) continue;
						item.OwnerShroompId = s.Id;
						item.TilePos = null;
						s.Inventory.Add(item);
					}
				}
				else if (sd.CarriedItem != null)
				{
					// v0.4.7 legacy path — single carried item from old saves.
					long now = _core.GlobalTick;
					var item = RehydrateItem(sd.CarriedItem, now);
					if (item != null)
					{
						item.OwnerShroompId = s.Id;
						item.TilePos = null;
						s.CarriedItem = item;
					}
				}

				// v0.4.7 — restore preferences (DF-style likes/dislikes
				// + runtime social affinity). Re-roll fresh if the save
				// predates this system.
				if (sd.Preferences != null)
				{
					s.Preferences = new Sporeholm.Simulation.Preferences
					{
						LikedItems         = new List<string>(sd.Preferences.LikedItems),
						DislikedItems      = new List<string>(sd.Preferences.DislikedItems),
						LikedActivities    = new List<string>(sd.Preferences.LikedActivities),
						DislikedActivities = new List<string>(sd.Preferences.DislikedActivities),
						LikedShroomps        = new List<string>(sd.Preferences.LikedShroomps),
						DislikedShroomps     = new List<string>(sd.Preferences.DislikedShroomps),
					};
				}

				// v0.4.7 — restore active thoughts into a fresh 8-slot
				// ring; saved entries fill from slot 0 outward, the
				// rest stay default (empty Key + TicksRemaining 0).
				if (sd.Thoughts != null && sd.Thoughts.Count > 0)
				{
					var ring = new Sporeholm.Simulation.Thought[Sporeholm.Simulation.ThoughtRegistry.ThoughtCapacity];
					int j = 0;
					foreach (var rec in sd.Thoughts)
					{
						if (j >= ring.Length) break;
						ring[j++] = new Sporeholm.Simulation.Thought
						{
							Key            = rec.Key,
							TicksRemaining = rec.TicksRemaining,
							MoodOffset     = rec.MoodOffset,
							Context        = rec.Context ?? "",
						};
					}
					s.Thoughts = ring;
					s.ThoughtsDirty = true;   // recompute MoodFromThoughts on next ThoughtSystem.Tick
				}

				_core.AddShroomp(s);
			}

			// v0.3.47 (Phase 4 sub-B) — restore colony inventory. Each saved
			// item rebuilds with a fresh Guid; AgeInTicks roundtrips by
			// subtracting from the new GlobalTick so spoilage state stays
			// truthful across the save/load boundary.
			if (save.ColonyInventory != null && save.ColonyInventory.Count > 0)
			{
				long now = _core.GlobalTick;
				foreach (var rec in save.ColonyInventory)
				{
					var item = RehydrateItem(rec, now);
					if (item != null) _core.Resources.Inventory.Add(item);
				}
			}

			// v0.4.7 (bugreport B-1) — restore items dropped on the
			// map. Each rebuilds with a fresh Guid + restored TilePos
			// and gets dropped via LocalMap.DropItem so renderer +
			// hauler indexes pick them up correctly. The local map
			// must be bound before this point (it is — LoadFromSave
			// is called from SeedColony after BindLocalMap).
			if (save.DroppedItems != null && save.DroppedItems.Count > 0)
			{
				var map = WorldState.Instance?.CurrentLocalMap;
				if (map != null)
				{
					long now = _core.GlobalTick;
					foreach (var rec in save.DroppedItems)
					{
						var item = RehydrateItem(rec.Item, now);
						if (item == null) continue;
						item.TilePos = new Vector2(
							rec.X * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f,
							rec.Y * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f);
						map.DropItem(item);
					}
				}
			}
		}

		// v0.4.7 (bugreport B-1) — shared helper used by every Item
		// restore path (colony inventory, shroomp equipment, shroomp
		// carried, dropped items on the map). Returns null on enum
		// parse failure (sub-type / quality / state from a future
		// version we don't understand) so callers can skip cleanly.
		// ── Developer Mode actions (v0.4.32) ──────────────────────────────────
		//
		// RimWorld-style dev hooks called from `DevPanel` when DevMode.IsEnabled.
		// All methods are no-ops when the named shroomp doesn't exist; spawn /
		// item-drop variants are no-ops when the map isn't bound yet. Each
		// mutates sim-thread state directly under SimulationCore's shroomp lock
		// — main-thread races against an in-progress tick are bounded to
		// single-field reads/writes (cheap and tolerable for dev tools).

		public Shroomp? DevFindShroompByName(string name)
		{
			foreach (var s in _core.AllShroomps())
				if (s.Name == name) return s;
			return null;
		}

		public bool DevKillShroomp(string name)
		{
			var s = DevFindShroompByName(name);
			if (s == null || !s.IsAlive || _core == null) return false;
			// v0.4.60 — route through SimulationCore.KillShroomp so the
			// dev-kill goes through the canonical kill pipeline (same as
			// natural death from aging or vital-organ failure). Mirrors
			// RimWorld's pattern where dev-mode "Kill" applies synthetic
			// damage that hits the same Pawn.Kill() entry point — no
			// separate code path, no missed cleanup.
			//
			// Pre-v0.4.60 the bug: this method flipped IsAlive=false on
			// the main thread directly. The next sim tick's
			// `_shroomps.RemoveAll(s => !s.IsAlive)` (SimulationCore.cs
			// ~L335) ran BEFORE the per-shroomp working-list loop, so the
			// dead shroomp was filtered out of the iteration → DropCorpseGear
			// never ran → no corpse, no gear drop, shroomp just disappeared.
			// Routing through PostMainThreadCommand puts the kill on the
			// sim thread INSIDE the same tick, BEFORE the next-tick
			// removal sweep, so the corpse + gear spawn correctly.
			_core.PostMainThreadCommand(() => _core.KillShroomp(s, CauseOfDeath.Dev));
			return true;
		}

		public bool DevFillNeeds(string name)
		{
			var s = DevFindShroompByName(name);
			if (s == null) return false;
			s.Nutrition = s.Rest = s.Social = s.MagicResonance = s.Safety = 100f;
			return true;
		}

		public bool DevDrainNeeds(string name)
		{
			var s = DevFindShroompByName(name);
			if (s == null) return false;
			// v0.4.55 — drain to 0 (was 5f). Sam: "Drain Needs should
			// drain needs all the way to zero so that I can actually test
			// things like eating, sleeping, and socializing." A floor
			// of 5 left the shroomp above the starvation damage line
			// (NeedsSystem.cs ~line 66 → Stomach/Liver damage at
			// Nutrition <= 0) so the most interesting failure modes were
			// untestable from the dev panel.
			s.Nutrition = s.Rest = s.Social = s.MagicResonance = s.Safety = 0f;
			return true;
		}

		public bool DevAddThought(string name, string thoughtKey)
		{
			var s = DevFindShroompByName(name);
			if (s == null) return false;
			Sporeholm.Simulation.ThoughtRegistry.Add(s, thoughtKey, context: "dev");
			return true;
		}

		// v0.5.80 — damage the selected shroomp's body parts until they
		// cross the (trait-modified) Down threshold. Sam: "Dev panel
		// should have a button, not a slider, that damages the shroomp
		// until they are down." Iteratively shaves non-vital parts to 0
		// and vital parts down to a floor of 8 (so the shroomp goes Down
		// rather than dying from a destroyed vital — UpdateDownedState
		// on the next tick flips IsDowned=true). Safety cap of 60 passes
		// covers the worst case where every vital part is already low.
		public bool DevDamageToDown(string name)
		{
			var s = DevFindShroompByName(name);
			if (s == null || !s.IsAlive) return false;
			float threshold = Sporeholm.Simulation.Systems.BehaviorSystem.DownThresholdFor(s);
			// Aim a couple of % below threshold so UpdateDownedState's
			// strict-less-than comparison fires reliably.
			float targetHealth = threshold - 2f;
			if (targetHealth < 0f) targetHealth = 0f;

			var partKeys = new System.Collections.Generic.List<string>(s.BodyParts.Keys);
			int passes = 0;
			while (s.ComputeHealthPercent() > targetHealth && passes++ < 60)
			{
				foreach (var k in partKeys)
				{
					var def = System.Array.Find(
						Sporeholm.Simulation.BodyPartRegistry.Template,
						d => d.Name == k);
					float floor = (def != null && def.Vital) ? 8f : 0f;
					float cur   = s.BodyParts[k];
					if (cur > floor)
						s.BodyParts[k] = System.Math.Max(floor, cur - 5f);
				}
			}
			// IsDowned flips on the next BehaviorSystem tick via
			// UpdateDownedState. Don't set it directly here — let the
			// canonical state-machine path own the transition (also
			// fires the Downed thought).
			return true;
		}

		public bool DevForceYield(string name, int ticks = 60)
		{
			var s = DevFindShroompByName(name);
			if (s == null) return false;
			s.YieldingTicks = ticks;
			return true;
		}

		// Spawns a stack of one item type at the requested tile via map.DropItem
		// (so the v0.4.30 stack-cap + single-type-per-tile rules + overflow
		// spiral all run as in real gameplay). Returns the actual landing
		// tile after overflow, or null if the map isn't bound.
		public (int X, int Y)? DevSpawnItem(int tx, int ty,
			Sporeholm.Simulation.Items.ItemKind kind,
			string subType, string materialFamily, string materialSubType,
			int quantity, Sporeholm.Simulation.Items.Quality quality
				= Sporeholm.Simulation.Items.Quality.Normal)
		{
			var map = WorldState.Instance?.CurrentLocalMap;
			if (map == null || quantity <= 0) return null;
			var pos = new Vector2(
				tx * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f,
				ty * Sporeholm.World.LocalMap.TileSize + Sporeholm.World.LocalMap.TileSize * 0.5f);
			var matKey = new Sporeholm.Simulation.Items.MaterialKey(materialFamily, materialSubType);
			var item = Sporeholm.Simulation.Items.ItemFactory.Create(
				kind, subType, matKey, _rng, _core.GlobalTick, skillLevel: 0, quantity: quantity);
			item.Quality = quality;
			item.TilePos = pos;
			map.DropItem(item);
			// item.TilePos may have been re-pointed by overflow.
			if (item.TilePos.HasValue)
				return ((int)(item.TilePos.Value.X / Sporeholm.World.LocalMap.TileSize),
						(int)(item.TilePos.Value.Y / Sporeholm.World.LocalMap.TileSize));
			return (tx, ty);
		}

		// Spawns a brand-new adult shroomp near the requested pixel with random
		// traits + personality + role. Uses the same registries as BirthSystem
		// so the spawned shroomp is indistinguishable from a colony-born one
		// once placed.
		public bool DevSpawnShroomp(Vector2 pixel, string role = "Unassigned")
		{
			var existing = _core.AllShroomps();
			var living   = new List<Shroomp>(existing.Count);
			foreach (var s in existing) if (s.IsAlive) living.Add(s);
			var sex  = _rng.Next(49) == 0 ? Sex.Female : Sex.Male;
			var name = ShroompNameGenerator.Generate(living.ConvertAll<string>(s => s.Name), _rng, sex);
			var fresh = new Shroomp
			{
				Name              = name,
				AgeInYears        = 60 + _rng.Next(80),
				Sex               = sex,
				Role              = role,
				BirthdayDayOfYear = _rng.Next(0, 120),
				Nutrition         = 80f,
				Rest              = 80f,
				Social            = 80f,
				MagicResonance    = 75f,
				Safety            = 90f,
			};
			Sporeholm.Simulation.TraitRegistry.AssignDawnEraTraits(fresh, _rng);
			fresh.Personality = Sporeholm.Simulation.PersonalityRegistry.Assign(_rng, fresh.AgeInYears);
			fresh.BodyParts   = Sporeholm.Simulation.BodyPartRegistry.CreateHealthy();
			fresh.Preferences = Sporeholm.Simulation.PreferenceRegistry.Assign(_rng, fresh.Personality);
			Sporeholm.Simulation.SkillRegistry.Distribute(fresh, _rng);
			Sporeholm.Simulation.WorkPriorityDefaults.ApplyRoleDefaults(fresh);
			fresh.Handedness  = Sporeholm.Simulation.HandednessMeta.Roll(_rng);
			Sporeholm.Simulation.ShroompIdentity.Roll(fresh, _rng);   // v0.5.63
			fresh.SimPos      = pixel;
			fresh.SimTarget   = pixel;
			fresh.SimSpeed    = 1.2f;
			_core.AddShroomp(fresh);
			return true;
		}

		// Toggle force-pause from dev panel — wraps the existing Paused field
		// so the dev panel button text can mirror it (and the v0.4.27 main-
		// thread queue still picks up the change cleanly).
		public bool DevTogglePause()
		{
			Paused = !Paused;
			return Paused;
		}

		// v0.5.84 — perf snapshot for the dev-panel perf section. Reads the
		// sim-thread-incremented counters lifetime-cumulative; the dev panel
		// stores last-poll values and computes deltas between polls so all
		// reads are lock-free (benign race; a torn read shows briefly then
		// corrects on the next poll).
		public readonly record struct PerfCounters(
			long TicksRun, long TotalTickMicros, long BehaviorMicros, long NeedsMicros,
			long PfCalls, long PfExpansions, long PfSuccesses, long PfFailures,
			int LiveShroomps);

		public PerfCounters GetPerfCounters()
		{
			int live = 0;
			foreach (var s in _core.AllShroomps())
				if (s.IsAlive) live++;
			return new PerfCounters(
				TicksRun:        _core.PerfTicksRun,
				TotalTickMicros: _core.PerfTotalTickMicros,
				BehaviorMicros:  _core.PerfBehaviorMicros,
				NeedsMicros:     _core.PerfNeedsMicros,
				PfCalls:         Sporeholm.Simulation.Pathfinder.TotalCalls,
				PfExpansions:    Sporeholm.Simulation.Pathfinder.TotalExpansions,
				PfSuccesses:     Sporeholm.Simulation.Pathfinder.TotalSuccesses,
				PfFailures:      Sporeholm.Simulation.Pathfinder.TotalFailures,
				LiveShroomps:    live);
		}

		private static Sporeholm.Simulation.Items.Item? RehydrateItem(
			SaveManager.ItemSaveData rec, long globalTick)
		{
			if (!System.Enum.TryParse<Sporeholm.Simulation.Items.ItemKind>(rec.Kind, out var kind)) return null;
			if (!System.Enum.TryParse<Sporeholm.Simulation.Items.Quality>(rec.Quality, out var qual))
				qual = Sporeholm.Simulation.Items.Quality.Normal;
			if (!System.Enum.TryParse<Sporeholm.Simulation.Items.ItemState>(rec.State, out var state))
				state = Sporeholm.Simulation.Items.ItemState.Fresh;
			// v0.5.84t — legacy SubType migration. Pre-v0.5.84t items used
			// "ClothBolt" (renamed to "MossCloth") and "BerryWine" /
			// "BoneKnife" (replaced by "Knife" + material). Item dicts
			// keyed by SubType need to map the old name forward so old
			// saves don't lose stacks.
			string subType = rec.SubType switch
			{
				"ClothBolt" => "MossCloth",
				"BerryWine" => "BerryJuice",
				"BoneKnife" => "Knife",
				_           => rec.SubType,
			};
			var item = new Sporeholm.Simulation.Items.Item
			{
				Kind          = kind,
				SubType       = subType,
				Material      = new Sporeholm.Simulation.Items.MaterialKey(rec.MaterialFamily, rec.MaterialSubType),
				Quality       = qual,
				State         = state,
				Quantity      = rec.Quantity,
				AvgCondition  = rec.AvgCondition,
				DurabilityCap = rec.DurabilityCap,
				AvgBirthTick  = globalTick - rec.AgeInTicks,
			};
			// v0.4.35 — corpse sidecar rehydrate. Enums fall back to
			// sensible defaults on parse failure (Sex.Male, "Unassigned"
			// role, CauseOfDeath.Natural, Handedness.Right) so older
			// saves without these fields still load cleanly.
			if (rec.Corpse != null)
			{
				var cs = rec.Corpse;
				System.Enum.TryParse<Sex>(cs.Sex, out var corpseSex);
				if (!System.Enum.TryParse<CauseOfDeath>(cs.Cause, out var corpseCause))
					corpseCause = CauseOfDeath.Natural;
				System.Enum.TryParse<Handedness>(cs.Handedness, out var corpseHand);
				item.CorpseInfo = new Sporeholm.Simulation.Items.CorpseData(
					Name:        cs.Name,
					AgeYears:    cs.AgeYears,
					Sex:         corpseSex,
					Role:        cs.Role ?? "Unassigned",
					Cause:       corpseCause,
					DeathTick:   globalTick - cs.DeathAgeTicks,
					Personality: cs.Personality ?? new System.Collections.Generic.List<string>(0),
					Handedness:  corpseHand);
			}
			return item;
		}
	}
}
