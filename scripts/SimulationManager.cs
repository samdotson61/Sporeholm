using Godot;
using System;
using System.Collections.Generic;
using SmurfulationC.Simulation;
using SmurfulationC.Simulation.Systems;
using SmurfulationC.World;

namespace SmurfulationC
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

		// A smurf died — causeOfDeath is "Natural", "Starvation", or "Combat".
		[Signal] public delegate void SmurfDiedEventHandler(string name, int ageAtDeath, string causeOfDeath);

		// A new smurf was born.
		[Signal] public delegate void BirthOccurredEventHandler(string name, string sex);
		// v0.3.47 — wandering-in arrival. UI consumes this in AlertsPane.
		[Signal] public delegate void WandererArrivedEventHandler(string name, string sex, string role, int age);

		// A smurf crossed a mood threshold in either direction.
		[Signal] public delegate void MoodThresholdCrossedEventHandler(string name, string fromState, string toState);

		// ── State ─────────────────────────────────────────────────────────────────

		private SimulationSnapshot? _lastSnapshot;
		public SimulationSnapshot? GetLastSnapshot() => _lastSnapshot;

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

		// Assigns each smurf a starting SimPos drawn from the map's spawn cluster
		// (BFS from centre). Without this the BehaviorSystem would treat SimPos as
		// Vector2.Zero and immediately try to walk to the map origin (top-left).
		// Cluster size scales with actual smurf count (was hard-capped at 16,
		// which produced visible stacking on scenarios with 17+ smurfs and meant
		// large colonies that hadn't been seeded yet rendered at (0, 0) until
		// the BehaviorSystem's first move tick).
		private void SeedSimPositions()
		{
			var map = WorldState.Instance?.CurrentLocalMap;
			if (map == null) return;
			var smurfs = _core.AllSmurfs();
			if (smurfs.Count == 0) return;

			// v0.3.35 — only seed smurfs whose SimPos is still Vector2.Zero
			// (i.e. brand-new colonies). Save-loaded smurfs have their SimPos
			// restored by LoadFromSave; without this guard, SeedSimPositions
			// would clobber the restored position with the spawn cluster
			// centre — which is the bug the player saw: "smurfs teleport to
			// the centre when the game is unpaused after a save reload".
			var needsSeed = new List<Smurf>();
			foreach (var s in smurfs)
				if (s.SimPos == Vector2.Zero) needsSeed.Add(s);
			if (needsSeed.Count == 0) return;

			// Always set SimSpeed (role-derived, not saved) for every smurf
			// — both new and restored — so loaded smurfs use the right speed.
			foreach (var s in smurfs)
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

			// Drain the snapshot queue — keep only the latest.
			SimulationSnapshot? latest = null;
			while (_core.Snapshots.TryDequeue(out var snap))
				latest = snap;

			if (latest is not null)
			{
				_lastSnapshot = latest;

				int inspired = 0, distressed = 0;
				foreach (var s in latest.Smurfs)
				{
					if (s.MoodState == MoodState.Inspired) inspired++;
					if (s.MoodState is MoodState.Distressed or MoodState.Breaking or MoodState.Collapse) distressed++;
				}

				EmitSignal(SignalName.TickCompleted,
					latest.Date.ToString(),
					latest.Smurfs.Count,
					inspired,
					distressed);
			}

			// Drain cross-thread event queues and re-emit as Godot signals.

			while (_core.PendingYearEvents.TryDequeue(out int year))
				EmitSignal(SignalName.YearTicked, year);

			while (_core.PendingSeasonEvents.TryDequeue(out var sc))
				EmitSignal(SignalName.SeasonChanged, sc.NewSeason.ToString(), sc.PrevSeason.ToString());

			while (_core.PendingDeaths.TryDequeue(out var dead))
			{
				string cause = dead.CauseOfDeath?.ToString() ?? "Natural";
				GD.Print($"[Sim] {dead.Name} died at age {dead.AgeInYears} ({cause}).");
				EmitSignal(SignalName.SmurfDied, dead.Name, dead.AgeInYears, cause);
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
		}

		public override void _ExitTree() => _core.Dispose();

		// ── Public API ─────────────────────────────────────────────────────────────

		public void SetSpeed(float multiplier) => SpeedMultiplier = multiplier;
		public void TogglePause() => Paused = !Paused;
		public SimulationDate GetCurrentDate() => _core.Date;

		// Queues a role change to be applied on the simulation thread's next tick.
		public void RequestRoleChange(string smurfName, string newRole) =>
			_core.QueueRoleChange(smurfName, newRole);

		// Roadmap §3.9 — issues a "Move to" player order. The sim thread picks
		// this up on its next BehaviorSystem.Tick and overrides the named
		// smurf's current task with a non-interruptible PlayerOrder.
		public void RequestPlayerMoveOrder(string smurfName, Vector2 worldPosPixels) =>
			_core.QueuePlayerOrder(smurfName, worldPosPixels);

		// v0.3.24 — multi-smurf move order. Each smurf in the list is issued
		// the same move target, with a small radial offset so they don't all
		// pile on the exact same pixel. Used by the RTS-style box-select on
		// right-click.
		// v0.5.2 — also clears each smurf's MoveOrderQueue so a non-shift
		// right-click cancels any chained orders the player previously
		// queued. RTS standard.
		public void RequestPlayerMoveOrderGroup(System.Collections.Generic.IEnumerable<string> smurfNames,
			Vector2 worldPosPixels)
		{
			int i = 0;
			int count = 0;
			foreach (var _ in smurfNames) count++;
			if (count == 0) return;
			foreach (var name in smurfNames)
			{
				// Ring offsets around target — 12 px radius spreads up to ~6
				// smurfs comfortably; larger groups stack with minor overlap.
				float ang = (float)(i * 2.0 * System.Math.PI / System.Math.Max(count, 1));
				var offset = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 12f;
				var target = worldPosPixels + offset;
				// v0.5.2 — clear chain queue on plain right-click. Routed
				// through the sim-thread command queue so the read+clear
				// is atomic with BehaviorSystem.Tick's own queue
				// consumption. Without this, an in-flight chain order
				// could fire after the player issued a fresh single-click
				// move (visible "smurf detours back to the old chain
				// destination" surprise).
				string capturedName = name;
				_core.PostMainThreadCommand(() =>
				{
					var s = DevFindSmurfByName(capturedName);
					s?.MoveOrderQueue.Clear();
				});
				_core.QueuePlayerOrder(name, target);
				i++;
			}
		}

		// v0.5.2 — RTS chain-order variant. Shift+right-click appends the
		// destination to each smurf's MoveOrderQueue instead of replacing
		// CurrentTask. BehaviorSystem.Tick pops the queue head onto a
		// fresh PlayerOrder when the previous task completes (CurrentTask
		// becomes null) AND no critical-need override fires. The first
		// chained order may still pop immediately if the smurf is currently
		// idle / between tasks — that's correct (chain start).
		//
		// Multi-smurf with radial offsets matches the non-queued group API
		// so a box-selected squad chain-orders to coherent ring positions.
		public void RequestPlayerMoveOrderGroupQueued(System.Collections.Generic.IEnumerable<string> smurfNames,
			Vector2 worldPosPixels)
		{
			int i = 0;
			int count = 0;
			foreach (var _ in smurfNames) count++;
			if (count == 0) return;
			foreach (var name in smurfNames)
			{
				float ang = (float)(i * 2.0 * System.Math.PI / System.Math.Max(count, 1));
				var offset = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 12f;
				var target = worldPosPixels + offset;
				string capturedName = name;
				_core.PostMainThreadCommand(() =>
				{
					var s = DevFindSmurfByName(capturedName);
					s?.MoveOrderQueue.Add(target);
				});
				i++;
			}
		}

		// v0.3.24 — combat order stub. Phase 8 will wire the BehaviorSystem
		// to read CombatTargetName and route an attack task. For now this
		// only sets the flag so the visual layer draws a sword icon over the
		// smurf — it's deliberately data-plumbing-only until enemies exist.
		public void RequestCombatOrder(string smurfName, string targetName)
		{
			_core.QueueCombatOrder(smurfName, targetName);
		}

		public void ClearCombatOrder(string smurfName)
		{
			_core.QueueCombatOrder(smurfName, null);
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
		public void DesignateRect(SmurfulationC.UI.DesignationTool tool,
			int x0, int y0, int x1, int y1)
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
			if (tool == SmurfulationC.UI.DesignationTool.Haul)
			{
				foreach (var (tx, ty, items) in map.EnumerateDroppedItems())
				{
					if (tx < xMin || tx > xMax || ty < yMin || ty > yMax) continue;
					foreach (var it in items)
						SmurfulationC.Simulation.Systems.HaulSystem.MarkPriority(it.Id);
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
					case SmurfulationC.UI.DesignationTool.Excavate:
						map.SetExcavationDesignation(x, y, true);
						break;
					case SmurfulationC.UI.DesignationTool.Gather:
						map.SetGatherDesignation(x, y, true);
						break;
					case SmurfulationC.UI.DesignationTool.ChopWood:
						map.SetChopWoodDesignation(x, y, true);
						break;
					case SmurfulationC.UI.DesignationTool.Cut:
						map.SetCutDesignation(x, y, true);
						break;
					case SmurfulationC.UI.DesignationTool.Remove:
						// v0.5.0 — Remove brush also clears stockpile membership.
						map.ClearDesignationsAt(x, y);
						map.ClearStockpileCell(x, y);
						break;
					case SmurfulationC.UI.DesignationTool.Stockpile:
						extendId = map.SetStockpileCell(x, y, extendId);
						break;
				}
			}
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
		public SmurfulationC.Simulation.Items.InventoryRow[] GetInventorySnapshot() =>
			_core?.Resources?.Inventory?.Snapshot()
				?? System.Array.Empty<SmurfulationC.Simulation.Items.InventoryRow>();

		// v0.3.47 — current sim-thread tick counter. Save format uses this
		// to compute item AgeInTicks at save time so reload can re-anchor
		// to the new GlobalTick without losing the spoilage clock.
		public long GetGlobalTick() => _core?.GlobalTick ?? 0;

		// v0.4.7 (bugreport B-1) — per-smurf "save extras" snapshot for
		// the equipment/handedness/carried/preferences/thoughts save
		// fields. Each entry carries the full fidelity of the live
		// state so save round-trip is loss-less. Walks under the smurf
		// lock; safe to call from the save path (main thread).
		public System.Collections.Generic.Dictionary<string, SmurfSaveExtras>
			GetSmurfSaveExtras(long globalTick)
		{
			var result = new System.Collections.Generic.Dictionary<string, SmurfSaveExtras>();
			if (_core == null) return result;
			foreach (var s in _core.AllSmurfs())
			{
				if (!s.IsAlive) continue;
				result[s.Name] = new SmurfSaveExtras
				{
					Handedness  = s.Handedness.ToString(),
					Equipment   = SnapshotSmurfEquipment(s, globalTick),
					CarriedItem = s.CarriedItem != null
						? SnapshotItem(s.CarriedItem, globalTick) : null,
					Preferences = SnapshotPreferences(s.Preferences),
					Thoughts    = SnapshotThoughts(s.Thoughts),
				};
			}
			return result;
		}

		// Per-Smurf save extras returned by GetSmurfSaveExtras. Shape
		// matches the save-record fields so SaveManager.BuildSmurfList
		// can copy directly across.
		public sealed class SmurfSaveExtras
		{
			public string                                  Handedness   = "Right";
			public System.Collections.Generic.List<SaveManager.EquipmentSaveData>? Equipment    = null;
			public SaveManager.ItemSaveData?               CarriedItem  = null;
			public SaveManager.PreferencesSaveData?        Preferences  = null;
			public System.Collections.Generic.List<SaveManager.ThoughtSaveData>?   Thoughts     = null;
		}

		// v0.4.7 — snapshot a single Item into the save record. Mirrors
		// the inventory-save path in SaveToSlot.
		// v0.4.35 — corpse-kind items also serialise their CorpseInfo
		// sidecar so reload restores the dead smurf's name / cause /
		// personality on the obituary line. Non-corpse items leave the
		// `Corpse` init property at its null default.
		private static SaveManager.ItemSaveData SnapshotItem(
			SmurfulationC.Simulation.Items.Item it, long globalTick)
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
			SnapshotSmurfEquipment(Smurf s, long globalTick)
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
			SmurfulationC.Simulation.Preferences? p)
		{
			if (p == null) return null;
			return new SaveManager.PreferencesSaveData
			{
				LikedItems         = new System.Collections.Generic.List<string>(p.LikedItems),
				DislikedItems      = new System.Collections.Generic.List<string>(p.DislikedItems),
				LikedActivities    = new System.Collections.Generic.List<string>(p.LikedActivities),
				DislikedActivities = new System.Collections.Generic.List<string>(p.DislikedActivities),
				LikedSmurfs        = new System.Collections.Generic.List<string>(p.LikedSmurfs),
				DislikedSmurfs     = new System.Collections.Generic.List<string>(p.DislikedSmurfs),
			};
		}

		private static System.Collections.Generic.List<SaveManager.ThoughtSaveData>?
			SnapshotThoughts(SmurfulationC.Simulation.Thought[]? ring)
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

		// v0.3.47 — per-smurf work priorities snapshot for the Jobs tab
		// save path. Walks the live smurf list under the sim lock and
		// shallow-copies each WorkPriorities dict.
		public System.Collections.Generic.Dictionary<string,
			System.Collections.Generic.Dictionary<string, byte>> GetWorkPrioritiesSnapshot()
		{
			var result = new System.Collections.Generic.Dictionary<string,
				System.Collections.Generic.Dictionary<string, byte>>();
			if (_core == null) return result;
			foreach (var s in _core.AllSmurfs())
			{
				if (s.WorkPriorities == null || s.WorkPriorities.Count == 0) continue;
				result[s.Name] = new System.Collections.Generic.Dictionary<string, byte>(s.WorkPriorities);
			}
			return result;
		}

		// v0.4.3 — equip / unequip / drop wired from the Inventory tab on
		// the smurf card. Each call posts a delegate onto the sim
		// thread's pending command queue; the sim drains them at the
		// top of Tick. Direct mutation of Smurf.Equipped* + Inventory
		// from the UI thread would race with BehaviorSystem reads, so
		// we queue.
		// v0.4.4 — equip target now identifies the specific EquipSlot
		// the player picked in the Inventory tab (Head, Torso, LeftHand,
		// RightHand, LeftFoot, RightFoot, …). The card sends a slot
		// hint of "auto" for hand items, in which case we resolve to
		// the smurf's dominant hand. Empty slot string falls back to
		// the body-class default (matches v0.4.3 behaviour).
		public void RequestEquip(string smurfName, string subType, string materialFamily, string materialSubType, string slotHint = "auto")
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindSmurf(smurfName); if (s == null) return;
				var item = TakeFromColonyInventory(subType, materialFamily, materialSubType);
				if (item == null) return;

				var def = SmurfulationC.Simulation.Items.ItemRegistry.Get(item.Kind, item.SubType);
				// v0.4.7 (bugreport B-4) — bounce back to inventory if the
				// item's sub-type isn't in the registry. Previously the
				// item was consumed from inventory but never returned,
				// silently leaking on registry mismatches.
				if (def == null) { _core.Resources.Inventory.Add(item); return; }
				var slot = ResolveEquipSlot(s, def, slotHint);
				if (slot == SmurfulationC.Simulation.Items.EquipSlot.None)
				{
					// Item isn't slot-equipable — bounce back to inventory.
					_core.Resources.Inventory.Add(item);
					return;
				}

				if (s.Equipment.TryGetValue(slot, out var displaced))
				{
					displaced.OwnerSmurfId = null;
					_core.Resources.Inventory.Add(displaced);
				}
				item.OwnerSmurfId = s.Id;
				s.Equipment[slot] = item;
			});
		}

		// Resolves the target slot for a (smurf, item, hint) triple.
		// hint = "auto" defers to body-class + handedness; hint =
		// "LeftHand"/"RightHand"/etc. forces that slot.
		private static SmurfulationC.Simulation.Items.EquipSlot ResolveEquipSlot(
			Smurf s, SmurfulationC.Simulation.Items.ItemSubTypeDef def, string hint)
		{
			if (System.Enum.TryParse<SmurfulationC.Simulation.Items.EquipSlot>(hint, out var explicitSlot)
				&& explicitSlot != SmurfulationC.Simulation.Items.EquipSlot.None)
				return explicitSlot;
			var slots = SmurfulationC.Simulation.Items.EquipSlotMeta.SlotsFor(def.BodyClass);
			if (slots.Length == 0) return SmurfulationC.Simulation.Items.EquipSlot.None;
			if (slots.Length == 1) return slots[0];
			// Hand / Foot / Arm / Leg classes — pick the dominant side
			// (or, for foot/arm/leg, fill the empty side first).
			if (def.BodyClass == SmurfulationC.Simulation.Items.EquipSlotMeta.BodyClass.Hand)
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
		public void RequestUnequip(string smurfName, string slot)
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindSmurf(smurfName); if (s == null) return;
				if (System.Enum.TryParse<SmurfulationC.Simulation.Items.EquipSlot>(slot, out var es)
					&& s.Equipment.TryGetValue(es, out var current))
				{
					s.Equipment.Remove(es);
					current.OwnerSmurfId = null;
					_core.Resources.Inventory.Add(current);
				}
			});
		}

		public void RequestDropEquipped(string smurfName, string slot)
		{
			_core?.PostMainThreadCommand(() =>
			{
				var s = FindSmurf(smurfName); if (s == null) return;
				SmurfulationC.Simulation.Items.Item? current = null;
				if (slot == "Carried")
				{
					current = s.CarriedItem;
					s.CarriedItem = null;
				}
				else if (System.Enum.TryParse<SmurfulationC.Simulation.Items.EquipSlot>(slot, out var es)
					&& s.Equipment.TryGetValue(es, out var eq))
				{
					current = eq;
					s.Equipment.Remove(es);
				}
				if (current == null) return;
				current.OwnerSmurfId = null;
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
		// item into the smurf's CarriedItem and then routes to the
		// colony delivery point.
		public void RequestPickUp(string smurfName, Godot.Vector2 itemTile)
		{
			_core?.PendingPickUps?.Enqueue((smurfName, itemTile));
		}

		private Smurf? FindSmurf(string name)
		{
			if (_core == null) return null;
			foreach (var s in _core.AllSmurfs()) if (s.Name == name) return s;
			return null;
		}

		// Pops a matching item out of the colony inventory by
		// (SubType, MaterialFamily, MaterialSubType). Returns null if
		// no match.
		private SmurfulationC.Simulation.Items.Item? TakeFromColonyInventory(
			string subType, string materialFamily, string materialSubType)
		{
			if (_core == null) return null;
			var inv = _core.Resources.Inventory;
			var rows = inv.Items;
			SmurfulationC.Simulation.Items.Item? match = null;
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
				var single = new SmurfulationC.Simulation.Items.Item
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

		// v0.4.4 — EquipOnSmurf helper removed. Equip is now handled
		// inline in RequestEquip via ResolveEquipSlot, which knows
		// about handedness + paired slots.

		// v0.3.47 — apply a per-smurf work priorities edit from the Jobs
		// tab UI. Called on the main thread; locks via AllSmurfs internally.
		public void SetWorkPriority(string smurfName, string category, byte priority)
		{
			if (_core == null) return;
			foreach (var s in _core.AllSmurfs())
			{
				if (s.Name != smurfName) continue;
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
			if (scenario != null && scenario.Smurfs.Count > 0)
			{
				foreach (var t in scenario.Smurfs)
					AddSmurfFromTemplate(t);
				WorldState.Instance!.PendingScenario = null;
				return;
			}

			// Legacy quick-start: founding seven. Skill seeds from Roadmap §2.5.
			AddSmurf("Papa",      542, "Elder",     Sex.Male);
			AddSmurf("Brainy",    98,  "Scholar",   Sex.Male);
			AddSmurf("Hefty",     75,  "Guardian",  Sex.Male);
			AddSmurf("Smurfette", 22,  "Caretaker", Sex.Female);
			AddSmurf("Clumsy",    45,  "Forager",   Sex.Male);
			AddSmurf("Handy",     61,  "Crafter",   Sex.Male);
			AddSmurf("Grouchy",   83,  "Forager",   Sex.Male);
		}

		// Build a Smurf from a ScenarioPanel template. Unlike the legacy
		// AddSmurf path, personality may be pre-set by the player; we only
		// assign random personality if the template's list is empty.
		// Biological traits still roll randomly — they're penetrance values,
		// not a player-facing checkbox per the §0.x trait design.
		private void AddSmurfFromTemplate(SmurfTemplate t)
		{
			var s = new Smurf
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
			// on the smurf card before confirming.
			s.Preferences = t.Preferences ?? PreferenceRegistry.Assign(_rng, s.Personality);
			WorkPriorityDefaults.ApplyRoleDefaults(s);
			s.Handedness = HandednessMeta.Roll(_rng);
			_core.AddSmurf(s);

			// v0.3.47 (Phase 4 sub-B) — deposit scenario-rolled starting
			// kit into the colony inventory. Each smurf's items merge into
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

		private void AddSmurf(string name, int age, string role, Sex sex)
		{
			var s = new Smurf
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
			_core.AddSmurf(s);
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

			foreach (var sd in save.Smurfs)
			{
				var s = new Smurf
				{
					Name              = sd.Name,
					AgeInYears        = sd.Age,
					Role              = sd.Role,
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
				// SmurfSaveData in a future patch when persistence is
				// hooked up. For now newborns + scenario smurfs carry
				// preferences in-memory only.
				s.Preferences = PreferenceRegistry.Assign(_rng, s.Personality);

				// Restore body parts; create healthy if save predates this system.
				if (sd.BodyParts != null && sd.BodyParts.Count > 0)
				{
					s.BodyParts = new Dictionary<string, float>(sd.BodyParts);
					foreach (var def in BodyPartRegistry.Template)
						if (!s.BodyParts.ContainsKey(def.Name))
							s.BodyParts[def.Name] = 100f;
				}
				else
					s.BodyParts = BodyPartRegistry.CreateHealthy();

				// v0.3.35 — restore SimPos / SimTarget / SimSpeed from the
				// save so the smurf re-enters the sim at its saved tile, not
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
						if (!System.Enum.TryParse<SmurfulationC.Simulation.Items.EquipSlot>(entry.Slot, out var slot))
							continue;
						var item = RehydrateItem(entry.Item, now);
						if (item == null) continue;
						item.OwnerSmurfId = s.Id;
						s.Equipment[slot] = item;
					}
				}

				// v0.4.7 — restore carried haul item. Setting TilePos
				// null so the renderer treats it as "in hand" rather
				// than dropped.
				if (sd.CarriedItem != null)
				{
					long now = _core.GlobalTick;
					var item = RehydrateItem(sd.CarriedItem, now);
					if (item != null)
					{
						item.OwnerSmurfId = s.Id;
						item.TilePos = null;
						s.CarriedItem = item;
					}
				}

				// v0.4.7 — restore preferences (DF-style likes/dislikes
				// + runtime social affinity). Re-roll fresh if the save
				// predates this system.
				if (sd.Preferences != null)
				{
					s.Preferences = new SmurfulationC.Simulation.Preferences
					{
						LikedItems         = new List<string>(sd.Preferences.LikedItems),
						DislikedItems      = new List<string>(sd.Preferences.DislikedItems),
						LikedActivities    = new List<string>(sd.Preferences.LikedActivities),
						DislikedActivities = new List<string>(sd.Preferences.DislikedActivities),
						LikedSmurfs        = new List<string>(sd.Preferences.LikedSmurfs),
						DislikedSmurfs     = new List<string>(sd.Preferences.DislikedSmurfs),
					};
				}

				// v0.4.7 — restore active thoughts into a fresh 8-slot
				// ring; saved entries fill from slot 0 outward, the
				// rest stay default (empty Key + TicksRemaining 0).
				if (sd.Thoughts != null && sd.Thoughts.Count > 0)
				{
					var ring = new SmurfulationC.Simulation.Thought[SmurfulationC.Simulation.ThoughtRegistry.ThoughtCapacity];
					int j = 0;
					foreach (var rec in sd.Thoughts)
					{
						if (j >= ring.Length) break;
						ring[j++] = new SmurfulationC.Simulation.Thought
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

				_core.AddSmurf(s);
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
							rec.X * SmurfulationC.World.LocalMap.TileSize + SmurfulationC.World.LocalMap.TileSize * 0.5f,
							rec.Y * SmurfulationC.World.LocalMap.TileSize + SmurfulationC.World.LocalMap.TileSize * 0.5f);
						map.DropItem(item);
					}
				}
			}
		}

		// v0.4.7 (bugreport B-1) — shared helper used by every Item
		// restore path (colony inventory, smurf equipment, smurf
		// carried, dropped items on the map). Returns null on enum
		// parse failure (sub-type / quality / state from a future
		// version we don't understand) so callers can skip cleanly.
		// ── Developer Mode actions (v0.4.32) ──────────────────────────────────
		//
		// RimWorld-style dev hooks called from `DevPanel` when DevMode.IsEnabled.
		// All methods are no-ops when the named smurf doesn't exist; spawn /
		// item-drop variants are no-ops when the map isn't bound yet. Each
		// mutates sim-thread state directly under SimulationCore's smurf lock
		// — main-thread races against an in-progress tick are bounded to
		// single-field reads/writes (cheap and tolerable for dev tools).

		public Smurf? DevFindSmurfByName(string name)
		{
			foreach (var s in _core.AllSmurfs())
				if (s.Name == name) return s;
			return null;
		}

		public bool DevKillSmurf(string name)
		{
			var s = DevFindSmurfByName(name);
			if (s == null || !s.IsAlive || _core == null) return false;
			// v0.4.60 — route through SimulationCore.KillSmurf so the
			// dev-kill goes through the canonical kill pipeline (same as
			// natural death from aging or vital-organ failure). Mirrors
			// RimWorld's pattern where dev-mode "Kill" applies synthetic
			// damage that hits the same Pawn.Kill() entry point — no
			// separate code path, no missed cleanup.
			//
			// Pre-v0.4.60 the bug: this method flipped IsAlive=false on
			// the main thread directly. The next sim tick's
			// `_smurfs.RemoveAll(s => !s.IsAlive)` (SimulationCore.cs
			// ~L335) ran BEFORE the per-smurf working-list loop, so the
			// dead smurf was filtered out of the iteration → DropCorpseGear
			// never ran → no corpse, no gear drop, smurf just disappeared.
			// Routing through PostMainThreadCommand puts the kill on the
			// sim thread INSIDE the same tick, BEFORE the next-tick
			// removal sweep, so the corpse + gear spawn correctly.
			_core.PostMainThreadCommand(() => _core.KillSmurf(s, CauseOfDeath.Dev));
			return true;
		}

		public bool DevFillNeeds(string name)
		{
			var s = DevFindSmurfByName(name);
			if (s == null) return false;
			s.Nutrition = s.Rest = s.Social = s.MagicResonance = s.Safety = 100f;
			return true;
		}

		public bool DevDrainNeeds(string name)
		{
			var s = DevFindSmurfByName(name);
			if (s == null) return false;
			// v0.4.55 — drain to 0 (was 5f). Sam: "Drain Needs should
			// drain needs all the way to zero so that I can actually test
			// things like eating, sleeping, and socializing." A floor
			// of 5 left the smurf above the starvation damage line
			// (NeedsSystem.cs ~line 66 → Stomach/Liver damage at
			// Nutrition <= 0) so the most interesting failure modes were
			// untestable from the dev panel.
			s.Nutrition = s.Rest = s.Social = s.MagicResonance = s.Safety = 0f;
			return true;
		}

		public bool DevAddThought(string name, string thoughtKey)
		{
			var s = DevFindSmurfByName(name);
			if (s == null) return false;
			SmurfulationC.Simulation.ThoughtRegistry.Add(s, thoughtKey, context: "dev");
			return true;
		}

		public bool DevForceYield(string name, int ticks = 60)
		{
			var s = DevFindSmurfByName(name);
			if (s == null) return false;
			s.YieldingTicks = ticks;
			return true;
		}

		// Spawns a stack of one item type at the requested tile via map.DropItem
		// (so the v0.4.30 stack-cap + single-type-per-tile rules + overflow
		// spiral all run as in real gameplay). Returns the actual landing
		// tile after overflow, or null if the map isn't bound.
		public (int X, int Y)? DevSpawnItem(int tx, int ty,
			SmurfulationC.Simulation.Items.ItemKind kind,
			string subType, string materialFamily, string materialSubType,
			int quantity, SmurfulationC.Simulation.Items.Quality quality
				= SmurfulationC.Simulation.Items.Quality.Normal)
		{
			var map = WorldState.Instance?.CurrentLocalMap;
			if (map == null || quantity <= 0) return null;
			var pos = new Vector2(
				tx * SmurfulationC.World.LocalMap.TileSize + SmurfulationC.World.LocalMap.TileSize * 0.5f,
				ty * SmurfulationC.World.LocalMap.TileSize + SmurfulationC.World.LocalMap.TileSize * 0.5f);
			var matKey = new SmurfulationC.Simulation.Items.MaterialKey(materialFamily, materialSubType);
			var item = SmurfulationC.Simulation.Items.ItemFactory.Create(
				kind, subType, matKey, _rng, _core.GlobalTick, skillLevel: 0, quantity: quantity);
			item.Quality = quality;
			item.TilePos = pos;
			map.DropItem(item);
			// item.TilePos may have been re-pointed by overflow.
			if (item.TilePos.HasValue)
				return ((int)(item.TilePos.Value.X / SmurfulationC.World.LocalMap.TileSize),
						(int)(item.TilePos.Value.Y / SmurfulationC.World.LocalMap.TileSize));
			return (tx, ty);
		}

		// Spawns a brand-new adult smurf near the requested pixel with random
		// traits + personality + role. Uses the same registries as BirthSystem
		// so the spawned smurf is indistinguishable from a colony-born one
		// once placed.
		public bool DevSpawnSmurf(Vector2 pixel, string role = "Unassigned")
		{
			var existing = _core.AllSmurfs();
			var living   = new List<Smurf>(existing.Count);
			foreach (var s in existing) if (s.IsAlive) living.Add(s);
			var sex  = _rng.Next(49) == 0 ? Sex.Female : Sex.Male;
			var name = SmurfNameGenerator.Generate(living.ConvertAll<string>(s => s.Name), _rng, sex);
			var fresh = new Smurf
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
			SmurfulationC.Simulation.TraitRegistry.AssignDawnEraTraits(fresh, _rng);
			fresh.Personality = SmurfulationC.Simulation.PersonalityRegistry.Assign(_rng, fresh.AgeInYears);
			fresh.BodyParts   = SmurfulationC.Simulation.BodyPartRegistry.CreateHealthy();
			fresh.Preferences = SmurfulationC.Simulation.PreferenceRegistry.Assign(_rng, fresh.Personality);
			SmurfulationC.Simulation.SkillRegistry.Distribute(fresh, _rng);
			SmurfulationC.Simulation.WorkPriorityDefaults.ApplyRoleDefaults(fresh);
			fresh.Handedness  = SmurfulationC.Simulation.HandednessMeta.Roll(_rng);
			fresh.SimPos      = pixel;
			fresh.SimTarget   = pixel;
			fresh.SimSpeed    = 1.2f;
			_core.AddSmurf(fresh);
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

		private static SmurfulationC.Simulation.Items.Item? RehydrateItem(
			SaveManager.ItemSaveData rec, long globalTick)
		{
			if (!System.Enum.TryParse<SmurfulationC.Simulation.Items.ItemKind>(rec.Kind, out var kind)) return null;
			if (!System.Enum.TryParse<SmurfulationC.Simulation.Items.Quality>(rec.Quality, out var qual))
				qual = SmurfulationC.Simulation.Items.Quality.Normal;
			if (!System.Enum.TryParse<SmurfulationC.Simulation.Items.ItemState>(rec.State, out var state))
				state = SmurfulationC.Simulation.Items.ItemState.Fresh;
			var item = new SmurfulationC.Simulation.Items.Item
			{
				Kind          = kind,
				SubType       = rec.SubType,
				Material      = new SmurfulationC.Simulation.Items.MaterialKey(rec.MaterialFamily, rec.MaterialSubType),
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
				item.CorpseInfo = new SmurfulationC.Simulation.Items.CorpseData(
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
