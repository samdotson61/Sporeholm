using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Sporeholm.Simulation.Systems;
using Sporeholm.World;

namespace Sporeholm.Simulation
{
	// Owns and drives the full simulation loop on a dedicated background thread.
	//
	// Threading model:
	//   - The simulation thread is the sole writer of Shroomp state.
	//   - The main (Godot) thread is the sole reader of SimulationSnapshot records.
	//   - Communication happens only through thread-safe queues (Snapshots and the
	//     Pending* event queues below).
	//   - _shroompLock guards the shroomp list against concurrent Add/Remove while the
	//     simulation copies it at the start of each tick.
	public sealed class SimulationCore : IDisposable
	{
		public SimulationClock Clock { get; } = new();

		private SimulationDate _date = SimulationDate.Zero;
		public SimulationDate Date => _date; // read from any thread (struct copy = safe)

		private readonly List<Shroomp> _shroomps = new();
		private readonly object _shroompLock = new();

		// v0.6.0 (Phase 6) — entity (creature) authoritative state. Sim
		// thread is sole writer; snapshots round-trip a read-only copy to
		// the renderer each tick. Held under a separate lock to keep entity
		// add/remove from contending with shroomp scans.
		private readonly List<Sporeholm.Simulation.Entities.Entity> _entities = new();
		private readonly object _entityLock = new();

		public IReadOnlyList<Sporeholm.Simulation.Entities.Entity> AllEntities()
		{
			lock (_entityLock) return new List<Sporeholm.Simulation.Entities.Entity>(_entities);
		}

		public void AddEntity(Sporeholm.Simulation.Entities.Entity e)
		{
			lock (_entityLock) _entities.Add(e);
		}

		public void AddEntities(System.Collections.Generic.IEnumerable<Sporeholm.Simulation.Entities.Entity> es)
		{
			lock (_entityLock) _entities.AddRange(es);
		}

		// Capped snapshot queue. The main thread drains this each frame and
		// uses only the latest entry; older entries are discarded automatically.
		public ConcurrentQueue<SimulationSnapshot> Snapshots { get; } = new();
		private const int MaxQueueDepth = 8;

		// ── Cross-thread event queues ─────────────────────────────────────────────
		// The sim thread enqueues; SimulationManager drains these in _Process() and
		// re-emits them as Godot signals on the main thread.

		// v0.3.39 (O-H.2) — camera-follow point read by the LOD-phase
		// assignment routine. Main thread writes (GameController per frame);
		// sim thread reads. Vector2 is a 2-float struct; reads/writes may
		// tear on x86/x64 but worst case is one tick of slightly-stale
		// camera position, which is invisible (LOD bands are 320+ px wide).
		// Default of Zero means "no camera yet" — until the first write
		// every shroomp stays Hot, which matches initial-load behaviour.
		public Godot.Vector2 CameraFollow;

		// Global tick counter for LOD phase modulo. Increments inside Tick().
		// Sim thread is the sole writer; BehaviorSystem reads.
		public long GlobalTick;

		// v0.5.84 — diagnostic perf counters for the dev-panel perf section.
		// Sim thread writes, dev panel reads (benign race; deltas-between-polls).
		// Microseconds, accumulated lifetime, divided per-tick at display time.
		public long PerfTicksRun;
		public long PerfTotalTickMicros;
		public long PerfBehaviorMicros;
		public long PerfNeedsMicros;

		// (year) — fires once per in-game year
		public ConcurrentQueue<int> PendingYearEvents { get; } = new();

		// (newSeason, previousSeason) — fires 4 times per in-game year
		public ConcurrentQueue<(Season NewSeason, Season PrevSeason)> PendingSeasonEvents { get; } = new();

		// Full snapshot of the shroomp at the moment of death
		public ConcurrentQueue<ShroompSnapshot> PendingDeaths { get; } = new();

		// (snapshot, from, to) — fires when a shroomp crosses a mood threshold
		public ConcurrentQueue<(ShroompSnapshot Snap, MoodState From, MoodState To)> PendingMoodCrossings { get; } = new();

		// v0.5.84t — one-shot starvation alerts. Enqueued on the rising
		// edge of WasStarving (Nutrition crossing below 20). SimulationManager
		// drains + emits StarvationStarted; GameController posts a message
		// to MessageLog under the new Starving category.
		public ConcurrentQueue<ShroompSnapshot> PendingStarvationStarts { get; } = new();

		// Full snapshot of the shroomp at the moment of birth
		public ConcurrentQueue<ShroompSnapshot> PendingBirths { get; } = new();

		// v0.3.47 (Phase 4 sub-B) — wandering-in arrivals. WanderingInSystem
		// enqueues; SimulationManager drains and re-emits to the AlertsPane
		// as a "Wanderer joined" notification. For sub-B the prompt
		// auto-accepts; Phase 8's storyteller will gain Accept/Decline UI.
		public ConcurrentQueue<ShroompSnapshot> PendingWanderers { get; } = new();

		private readonly Random _rng = new();

		// ── Role-change command queue ─────────────────────────────────────────────
		// Main thread enqueues; sim thread applies at the start of the next tick.
		private readonly ConcurrentQueue<(string Name, string NewRole)> _pendingRoleChanges = new();

		public void QueueRoleChange(string shroompName, string newRole) =>
			_pendingRoleChanges.Enqueue((shroompName, newRole));

		// ── Phase 3 — Behavior wiring ─────────────────────────────────────────────
		// Colony resource ledger, owned by SimulationCore. Sim thread mutates;
		// main thread can read via Snapshot() (copy semantics) for HUD display.
		public ColonyResources Resources { get; } = new();

		// LocalMap reference assigned by SimulationManager once the world map is
		// loaded. Sim thread reads + mutates via LocalMap's existing APIs.
		// v0.5.61 — setter also wires `ReservationManager.Active` to the
		// map's Reservations instance so HaulSystem's static API can route
		// to the unified reservation store. Clearing Map (e.g. on game
		// load) sets Active back to null.
		private LocalMap? _map;
		public LocalMap? Map
		{
			get => _map;
			set
			{
				_map = value;
				ReservationManager.Active = value?.Reservations;
			}
		}

		// Player-order queue (Roadmap §3.9). Main thread enqueues right-click
		// move orders; BehaviorSystem.Tick drains them. ConcurrentQueue → no lock.
		public ConcurrentQueue<PlayerOrder> PendingPlayerOrders { get; } = new();

		public void QueuePlayerOrder(string shroompName, Vector2 target) =>
			PendingPlayerOrders.Enqueue(new PlayerOrder(shroompName, target));

		// v0.4.3 — right-click "pick up this item" orders. The shroomp walks
		// to the tile, picks up whatever item is there, then routes to
		// the colony delivery point — i.e. a Haul cycle anchored by
		// the player's target instead of the nearest unreserved item.
		// (ShroompName, ItemTilePixel)
		public ConcurrentQueue<(string ShroompName, Vector2 ItemTile)> PendingPickUps { get; } = new();

		// v0.4.3 — generic sim-thread command queue. Used by the
		// Inventory tab on the shroomp card for equip / unequip / drop
		// actions that must mutate Shroomp.Equipped* + Inventory under
		// the sim thread's exclusive write rules.
		public ConcurrentQueue<System.Action> PendingCommands { get; } = new();

		public void PostMainThreadCommand(System.Action cmd)
		{
			if (cmd != null) PendingCommands.Enqueue(cmd);
		}

		// v0.3.24 — combat order queue (Phase 8 stub). Drained from
		// BehaviorSystem.Tick alongside PendingPlayerOrders: each entry sets
		// the named shroomp's CombatTargetName (null = clear). The behavior
		// system does not yet act on this — only the visual layer reads it.
		public ConcurrentQueue<(string ShroompName, string? TargetName)> PendingCombatOrders { get; } = new();

		public void QueueCombatOrder(string shroompName, string? targetName) =>
			PendingCombatOrders.Enqueue((shroompName, targetName));

		// v0.3.39 (O-M.1) — designation write queue. Main thread enqueues
		// player drag-box edits; sim thread drains at the top of each
		// tick. Confines all designation-flag mutations to the sim thread,
		// closing the latent LocalTile read-modify-write race the
		// optimization report flagged.
		//
		// Kind encoding: bit-packed byte.
		//   0 = SetExcavation, 1 = SetGather, 2 = SetChopWood,
		//   3 = SetCut,        4 = ClearAll
		public enum DesignationOp : byte
		{
			SetExcavation,
			SetGather,
			SetChopWood,
			SetCut,
			ClearAll,
		}
		public readonly record struct DesignationCmd(DesignationOp Op, int X, int Y);
		public ConcurrentQueue<DesignationCmd> PendingDesignations { get; } = new();

		public void QueueDesignation(DesignationOp op, int x, int y) =>
			PendingDesignations.Enqueue(new DesignationCmd(op, x, y));

		// ── Thread lifecycle ──────────────────────────────────────────────────────

		private Thread? _thread;
		private volatile bool _running;

		public void SetStartDate(SimulationDate date) => _date = date;

		public void Start()
		{
			_running = true;
			// v0.5.82 — push an initial snapshot BEFORE the worker thread
			// spawns so the main-thread renderer has something to draw on
			// the very first frame after load. Pre-v0.5.82 the Run loop
			// only pushed snapshots from inside Tick (or on paused role-
			// change), so on a paused-on-load scene the renderer waited
			// until the player first unpaused before any shroomps appeared.
			// Sam: "Shroomps should appear on loading into the game and
			// not on the next unpaused tick." Safe to call directly here
			// — no worker thread exists yet, so no concurrent access to
			// _shroomps or Snapshots.
			PushSnapshot();
			_thread = new Thread(Run)
			{
				Name = "SimulationCore",
				IsBackground = true
			};
			_thread.Start();
		}

		public void Stop()
		{
			_running = false;
			_thread?.Join(3000);
		}

		public void AddShroomp(Shroomp shroomp)
		{
			lock (_shroompLock) _shroomps.Add(shroomp);
		}

		public void RemoveShroomp(Shroomp shroomp)
		{
			lock (_shroompLock) _shroomps.Remove(shroomp);
		}

		// Thread-safe snapshot of all shroomps (alive + dead). Used by
		// SimulationManager to seed sim positions before the sim thread starts
		// and after the LocalMap is rebound. Returns a copy so callers can
		// safely iterate without holding the lock.
		public IReadOnlyList<Shroomp> AllShroomps()
		{
			lock (_shroompLock) return new List<Shroomp>(_shroomps);
		}

		// ── Background thread ─────────────────────────────────────────────────────

		private void Run()
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			double nextTickMs = 0.0;

			while (_running)
			{
				if (Clock.Paused)
				{
					// Apply role changes even while paused so assignments take effect
					// immediately and the UI snapshot reflects them without resuming.
					bool anyChanges = false;
					while (_pendingRoleChanges.TryDequeue(out var rc))
					{
						lock (_shroompLock)
						{
							var target = _shroomps.Find(s => s.Name == rc.Name);
							if (target != null) { target.Role = rc.NewRole; anyChanges = true; }
						}
					}
					if (anyChanges) PushSnapshot();

					Thread.Sleep(50);
					nextTickMs = sw.Elapsed.TotalMilliseconds;
					continue;
				}

				double speed      = Clock.SpeedMultiplier;
				double intervalMs = SimulationClock.BaseTickIntervalMs / speed;
				double nowMs      = sw.Elapsed.TotalMilliseconds;

				if (nowMs >= nextTickMs)
				{
					int maxBatch = Math.Max(1, (int)(8.0 / Math.Max(intervalMs, 0.001)));
					int ran = 0;
					while (ran < maxBatch && _running && !Clock.Paused)
					{
						if (sw.Elapsed.TotalMilliseconds < nextTickMs) break;
						// v0.3.36 (B.15) — push the snapshot only on the LAST
						// tick of the batch. The visual layer can only render
						// the most recent snapshot anyway, so 10 ticks of
						// catch-up don't need 10 snapshot allocations + 10
						// list copies — the first 9 would just be dropped
						// from the queue (or never read). At 1000 shroomps with
						// the struct-snapshot from v0.3.36 each push is much
						// cheaper, but skipping the intermediate ones still
						// saves the per-snapshot dict allocation and the
						// list copy under the shroomp-lock.
						bool isLast = (ran + 1 >= maxBatch)
							|| (sw.Elapsed.TotalMilliseconds < nextTickMs + intervalMs);
						// v0.4.18 — catch-all exception barrier. The
						// player reported "shroomps stop and do not move
						// again after a few minutes"; the most likely
						// cause is an unhandled exception inside Tick
						// killing the sim thread silently (Run exits the
						// while loop, `_running` stays true, but the OS
						// thread is dead). Shroomp state freezes, the
						// snapshot queue stops draining. Logging the
						// exception and continuing lets the world keep
						// ticking; the underlying bug surfaces in the
						// editor console instead of presenting to the
						// player as "everyone stopped".
						try { Tick(pushSnapshot: isLast); }
						catch (System.Exception ex)
						{
							Godot.GD.PushError(
								$"[Sim] Tick threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
						}
						nextTickMs += intervalMs;
						ran++;
					}

					if (sw.Elapsed.TotalMilliseconds - nextTickMs > 200.0)
						nextTickMs = sw.Elapsed.TotalMilliseconds;
				}
				else
				{
					double sleepMs = nextTickMs - nowMs;
					Thread.Sleep(Math.Max(1, Math.Min(50, (int)sleepMs)));
				}
			}
		}

		// ── Day/year boundary tracking ────────────────────────────────────────────

		// Day-of-year index: 0–119 (4 seasons × 30 days).
		private int _lastDayOfYear = 0; // matches SimulationDate.Zero (Spring, Day 1 = index 0)

		private static int DayOfYear(SimulationDate d) =>
			(int)d.Season * 30 + (d.Day - 1);

		// ── Tick loop ─────────────────────────────────────────────────────────────

		private const int SimSystemInterval = 60;
		private int _ticksSinceSimUpdate;

		private void Tick(bool pushSnapshot = true)
		{
			// v0.5.84 — wall-clock measurement for the dev-panel perf section.
			// Stopwatch.GetTimestamp is allocation-free and ns-resolution. Two
			// phase brackets (needs + behavior) and one outer bracket let the
			// panel show where tick time actually goes.
			long tTickStart = System.Diagnostics.Stopwatch.GetTimestamp();

			// v0.4.3 — drain UI-thread commands (equip / unequip / drop)
			// before anything reads Shroomp or Inventory state so the
			// edits land atomically inside one tick.
			while (PendingCommands.TryDequeue(out var cmd))
			{
				try { cmd?.Invoke(); }
				catch (System.Exception ex) { Godot.GD.PushWarning($"[Sim] Command failed: {ex.Message}"); }
			}

			// v0.4.3 — drain pick-up orders into PendingPlayerOrders so
			// they ride the existing player-order pathway. We use a
			// negative target-tile marker (Vector2 carries the pixel
			// position; BehaviorSystem detects an item at that tile and
			// converts to a Haul-pickup task on arrival).
			while (PendingPickUps.TryDequeue(out var pu))
				PendingPlayerOrders.Enqueue(new PlayerOrder(pu.ShroompName, pu.ItemTile));

			// 1. Apply queued role changes before snapshotting.
			while (_pendingRoleChanges.TryDequeue(out var rc))
			{
				lock (_shroompLock)
				{
					var target = _shroomps.Find(s => s.Name == rc.Name);
					if (target != null) target.Role = rc.NewRole;
				}
			}

			// v0.3.39 (O-M.1) — drain designation writes from the main
			// thread. Sim thread is now the sole writer to LocalTile's
			// designation flags + the indexed designation sets, closing
			// the read-modify-write race the optimization report flagged.
			while (Map != null && PendingDesignations.TryDequeue(out var dc))
			{
				switch (dc.Op)
				{
					case DesignationOp.SetExcavation: Map.SetExcavationDesignation(dc.X, dc.Y, true); break;
					case DesignationOp.SetGather:     Map.SetGatherDesignation    (dc.X, dc.Y, true); break;
					case DesignationOp.SetChopWood:   Map.SetChopWoodDesignation  (dc.X, dc.Y, true); break;
					case DesignationOp.SetCut:        Map.SetCutDesignation       (dc.X, dc.Y, true); break;
					case DesignationOp.ClearAll:      Map.ClearDesignationsAt     (dc.X, dc.Y);       break;
				}
			}

			// 2. Remove shroomps that died in a previous tick.
			lock (_shroompLock) _shroomps.RemoveAll(s => !s.IsAlive);

			// 3. Snapshot the living shroomp list (holds lock as briefly as possible).
			List<Shroomp> working;
			lock (_shroompLock) working = new List<Shroomp>(_shroomps);

			// 4. Advance the clock and detect boundary crossings.
			var prevDate   = _date;
			_date.AdvanceTick();

			int  newDayOfYear   = DayOfYear(_date);
			bool yearBoundary   = _date.Year   != prevDate.Year;
			bool seasonBoundary = _date.Season != prevDate.Season;
			bool dayBoundary    = newDayOfYear  != _lastDayOfYear || yearBoundary;

			// 5. Process day boundaries: individual birthdays and death checks.
			if (dayBoundary)
			{
				_lastDayOfYear = newDayOfYear;

				// v0.3.46 (Phase 4) — daily item decay. Food spoils,
				// tools degrade. One pass per in-game day; the tick rate
				// scales with sim speed automatically because dayBoundary
				// fires when the SimulationDate rolls a new day.
				ItemDeteriorationSystem.TickDay(Resources.Inventory, GlobalTick);

				// v0.4.33 — corpse rot. Walks the map's dropped items and
				// decays every Corpse-kind entry by ~14/day; bodies that
				// hit 0 condition are removed from the map. Cheap (linear
				// in dropped-tile count) and runs at most once per
				// in-game day.
				Map?.TickCorpseDecay(GlobalTick, daysElapsed: 1f);

				foreach (var s in working)
				{
					if (!s.IsAlive) continue;
					// v0.4.62 (G3) — reset the daily XP cap window for
					// every living shroomp at day boundary. Independent of
					// the per-shroomp birthday check below; XP saturation
					// resets for all colonists regardless of birthday.
					Sporeholm.Simulation.SkillRegistry.ResetDailyXp(s);

					if (s.BirthdayDayOfYear != newDayOfYear) continue;

					AgingSystem.AdvanceAge(s);

					if (AgingSystem.HasExpired(s))
					{
						// v0.4.60 — single canonical kill path.
						KillShroomp(s, CauseOfDeath.Natural);
					}
				}

				// Fire year/season events after processing deaths so Godot signals
				// carry an accurate colony state.
				if (yearBoundary)
					PendingYearEvents.Enqueue(_date.Year);

				if (seasonBoundary)
				{
					PendingSeasonEvents.Enqueue((_date.Season, prevDate.Season));

					// Birth attempt once per season — adds new shroomp if conditions allow.
					// v0.3.47 — pass the current colony food total so the
					// Phase 4 food-stockpile gate can suspend births when
					// reserves are too low.
					int foodTotal = Resources.Inventory.TotalByKind(
						Sporeholm.Simulation.Items.ItemKind.Food);
					var newborn = BirthSystem.TryBirth(working, _rng, foodTotal);
					if (newborn != null)
					{
						lock (_shroompLock) _shroomps.Add(newborn);
						PendingBirths.Enqueue(new ShroompSnapshot(newborn));
					}

					// v0.3.47 — wandering-in event. Tapered incidence by
					// colony size: ~3/year small colonies (≤ 30), ~1/year
					// medium (30-100), ~0 beyond. One roll per season.
					var wanderer = WanderingInSystem.TryWanderer(working, _rng, foodTotal, GlobalTick);
					if (wanderer != null)
					{
						lock (_shroompLock) _shroomps.Add(wanderer);
						PendingWanderers.Enqueue(new ShroompSnapshot(wanderer));
					}
				}
			}

			// 6. Run simulation systems once per real second (every 60 ticks at 1×).
			long tNeedsStart = System.Diagnostics.Stopwatch.GetTimestamp();
			if (++_ticksSinceSimUpdate >= SimSystemInterval)
			{
				_ticksSinceSimUpdate = 0;
				int foodCap = BirthSystem.ComputeFoodCapacity(working);
				NeedsSystem.Tick(working, foodCap);   // need decay + starvation damage
				// v0.3.43 — thoughts tick down before MoodSystem reads the
				// resulting MoodFromThoughts. Order matters: ThoughtSystem
				// updates the per-shroomp thought-mood sum, then MoodSystem
				// folds it into the final MoodScore.
				ThoughtSystem.Tick(working);
				MoodSystem.Tick(working);

				// Vital organ failure: any vital body part at 0% kills the shroomp.
				// Runs before NeedsSystem.HealTick so a Caretaker cannot mask a fatal organ.
				foreach (var s in working)
				{
					if (!s.IsAlive) continue;
					foreach (var def in BodyPartRegistry.Template)
					{
						if (!def.Vital) continue;
						if (s.BodyParts.TryGetValue(def.Name, out float cond) && cond <= 0f)
						{
							// v0.4.60 — single canonical kill path.
							var cause = s.Nutrition <= 0f
								? CauseOfDeath.Starvation
								: CauseOfDeath.Natural;
							KillShroomp(s, cause);
							break;
						}
					}
				}

				// v0.5.81 — Bleeding tick. Phase 7 prep: damaged body parts
				// produce a BleedRate; BloodLoss accumulates each tick and
				// kills the shroomp at 100. When no parts are bleeding, the
				// reservoir slowly regenerates so a tended-up shroomp
				// recovers within a few in-game minutes.
				// Block runs once per SimSystemInterval (60 ticks @ 1× =
				// one in-game second), same cadence as NeedsSystem.Tick.
				// Per-second rates calibrated so a single untreated severe
				// wound (cond=20, severity=30) on a non-vital part produces
				// ~0.018 / sec accumulation = ~93 minutes to bleed out —
				// slow but visible.
				const float bleedDt = 1f;
				foreach (var s in working)
				{
					if (!s.IsAlive) continue;
					s.RecomputeBleedRate();
					if (s.BleedRate > 0f)
					{
						s.BloodLoss = System.Math.Min(100f, s.BloodLoss + s.BleedRate * bleedDt);
						if (s.BloodLoss >= 100f)
						{
							KillShroomp(s, CauseOfDeath.BloodLoss);
							continue;
						}
					}
					else if (s.BloodLoss > 0f)
					{
						// No active wounds → slow blood-volume regen.
						// 0.05 / sec ≈ ~33 minutes for a full reservoir.
						s.BloodLoss = System.Math.Max(0f, s.BloodLoss - 0.05f * bleedDt);
					}
				}

				// Detect mood threshold crossings after MoodSystem updates MoodScore.
				foreach (var s in working)
				{
					if (!s.IsAlive) continue;
					var newMood = s.MoodState;
					if (newMood == s.PreviousMoodState) continue;
					var prev = s.PreviousMoodState;
					s.PreviousMoodState = newMood;
					PendingMoodCrossings.Enqueue((new ShroompSnapshot(s), prev, newMood));
				}

				// v0.5.84t — detect rising-edge starvation. Threshold 20
				// matches the BehaviorSystem MakeEat fallback (line ~2421
				// "if Nutrition < 20 then MakeEat"). One enqueue per
				// transition; the WasStarving flag holds until Nutrition
				// recovers above 25 (a 5-unit hysteresis so a recovering
				// pawn doesn't ping-pong on every tick around the threshold).
				const float StarvingEnter = 20f;
				const float StarvingExit  = 25f;
				foreach (var s in working)
				{
					if (!s.IsAlive) continue;
					if (s.Nutrition < StarvingEnter && !s.WasStarving)
					{
						s.WasStarving = true;
						PendingStarvationStarts.Enqueue(new ShroompSnapshot(s));
					}
					else if (s.Nutrition >= StarvingExit && s.WasStarving)
					{
						s.WasStarving = false;
					}
				}

				// Healing phase runs last — dead shroomps are already marked so HealTick
				// skips them, and vital organs that just hit 0 are not healed back above zero.
				NeedsSystem.HealTick(working);
			}
			PerfNeedsMicros += (System.Diagnostics.Stopwatch.GetTimestamp() - tNeedsStart)
				* 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

			// 7. Phase 3 — Behavior. Movement + task evaluation every tick at fixed
			//    dt = base interval. Player orders, critical needs, and role tasks
			//    drive the shroomps' SimPos / SimTarget across the local map.
			//    Mutating CurrentTask / SimPos here is safe because the sim thread
			//    is the sole writer; the next PushSnapshot() captures the result.
			var queue = new Queue<PlayerOrder>();
			while (PendingPlayerOrders.TryDequeue(out var po)) queue.Enqueue(po);

			// v0.3.24 — drain combat-order queue, set the flag on each named
			// shroomp. No behavior plumbing yet — this is just so the visual
			// sword icon turns on/off in response to right-click on an enemy.
			while (PendingCombatOrders.TryDequeue(out var co))
			{
				foreach (var s in working)
				{
					if (s.Name == co.ShroompName) { s.CombatTargetName = co.TargetName; break; }
				}
			}

			float dt = SimulationClock.BaseTickIntervalMs / 1000f;
			// v0.3.39 (O-H.2) — increment the global tick counter and pass
			// to BehaviorSystem so its per-shroomp LOD skip can compare
			// (s.TickPhase, s.TickSlot) against the current tick modulo.
			// Also reassigns phases periodically based on camera distance.
			GlobalTick++;
			// v0.3.40 — reassignment cadence 32 → 16 ticks (≈ 0.27 sec at 1×).
			// Faster reassign means the camera can scroll over the colony
			// without leaving cold-banded shroomps visible for long (shroomps
			// entering frame are reclassified to Hot within a quarter
			// second). Phase assignment is a single distance check per
			// shroomp — cheap even at 1000 shroomps.
			if ((GlobalTick & 15) == 0)
				BehaviorSystem.AssignTickPhases(working, CameraFollow);
			long tBehaviorStart = System.Diagnostics.Stopwatch.GetTimestamp();
			BehaviorSystem.Tick(working, Map, Resources, queue, _rng, dt, GlobalTick, _date.Hour);
			PerfBehaviorMicros += (System.Diagnostics.Stopwatch.GetTimestamp() - tBehaviorStart)
				* 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

			// v0.6.0 (Phase 6) — entity tick. Runs after BehaviorSystem
			// so hostile entities see shroomps with this tick's positions
			// before reacting. Dead entities are filtered here (Health <= 0
			// or State == Dead) so the next snapshot only carries the
			// living roster.
			if (Map != null)
			{
				List<Sporeholm.Simulation.Entities.Entity> entWork;
				lock (_entityLock)
				{
					_entities.RemoveAll(e => !e.IsAlive);
					entWork = new List<Sporeholm.Simulation.Entities.Entity>(_entities);
				}
				Sporeholm.Simulation.Systems.EntitySystem.Tick(entWork, working, Map, dt, _rng, (int)GlobalTick);
				// v0.6.0 — once per in-game day, refill ambient population.
				if (dayBoundary)
					Sporeholm.Simulation.Systems.EntitySpawnSystem.MaintainPopulation(
						(List<Sporeholm.Simulation.Entities.Entity>)entWork, Map, _rng);
				lock (_entityLock)
				{
					// The list is the same reference held inside the lock until
					// snapshot; no additional sync needed since we returned a
					// fresh copy. Just write back the maintained additions.
					_entities.Clear();
					_entities.AddRange(entWork);
				}
			}

			// 8. Push snapshot — only alive shroomps are included (filtered in constructor).
			// v0.3.36 — skip during catch-up batch's intermediate ticks.
			if (pushSnapshot) PushSnapshot();

			PerfTotalTickMicros += (System.Diagnostics.Stopwatch.GetTimestamp() - tTickStart)
				* 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
			PerfTicksRun++;
		}

		private void PushSnapshot()
		{
			List<Shroomp> snap;
			lock (_shroompLock) snap = new List<Shroomp>(_shroomps);
			List<Sporeholm.Simulation.Entities.Entity> entSnap;
			lock (_entityLock) entSnap = new List<Sporeholm.Simulation.Entities.Entity>(_entities);
			// v0.6.0 (Phase 6) — entities flow into the snapshot alongside
			// shroomps so the renderer's per-frame loop only needs one read.
			Snapshots.Enqueue(new SimulationSnapshot(_date, snap, entSnap));
			while (Snapshots.Count > MaxQueueDepth)
				Snapshots.TryDequeue(out _);
		}

		public void Dispose() => Stop();

		// v0.4.60 — canonical kill pipeline. Mirrors RimWorld's `Pawn.Kill()`
		// pattern: a single entry point that flips the Dead flag, drops
		// loose gear at the death tile, spawns the corpse Item, and
		// enqueues the PendingDeaths event. Call from sim thread only —
		// reads/writes Shroomp state and Map state under the same lock
		// regime as the rest of Tick(). DevPanel routes through
		// PostMainThreadCommand to land on the sim thread.
		//
		// Idempotent: a second call on an already-dead shroomp no-ops, so
		// natural-death paths and Dev kill can race without double-spawning
		// the corpse.
		//
		// Order matches the RimWorld decompile (Verse.Pawn.Kill ~L2197):
		//   1. Capture position (implicit — Shroomp.SimPos is read inside
		//      DropCorpseGear before any state mutation could move it)
		//   2. Set CauseOfDeath
		//   3. DropCorpseGear: inventory drop → equipment drop → corpse
		//      spawn → witness-thought broadcast (Map.DropItem reads
		//      live SimPos)
		//   4. Set IsAlive = false (the dead-flag transition that
		//      _shroomps.RemoveAll picks up at the next tick boundary)
		//   5. Enqueue PendingDeaths so the SimulationManager death
		//      signal fires for UI updates (message log, achievement
		//      hooks, mood broadcasts).
		//
		// The flag flip happens AFTER DropCorpseGear so any code path
		// inside DropCorpseGear that filters on IsAlive (e.g. the
		// witness-thought loop's `other.IsAlive` check) doesn't accidentally
		// skip the dying shroomp — though that shroomp is `other == s` filtered
		// anyway, the ordering keeps the invariant simple.
		public void KillShroomp(Shroomp s, CauseOfDeath cause)
		{
			if (s == null || !s.IsAlive) return;
			s.CauseOfDeath = cause;
			DropCorpseGear(s);
			s.IsAlive = false;
			// v0.5.61 — release any reservations held by the dying shroomp.
			// Without this their claims on blueprints / items / beds /
			// medical slots would persist indefinitely and block other
			// shroomps from picking up the work. The unified ReservationManager
			// makes this a single sweep across all layers.
			ReservationManager.Active?.ClearStaleForClaimant(s.Id);
			PendingDeaths.Enqueue(new ShroompSnapshot(s));
		}

		// v0.4.7 (bugreport B-2) — DF / RimWorld convention: when a shroomp
		// dies, every carried + equipped item drops on the death tile.
		// Previously these items were silently GC'd along with the dead
		// Shroomp object — a Guardian carrying a Masterwork Spear would
		// lose the spear entirely on death. Now the player can recover
		// the gear from the corpse's tile.
		private void DropCorpseGear(Shroomp s)
		{
			if (Map == null) return;
			var dropPos = s.SimPos;
			// v0.4.30 — drop EVERY item in the carry inventory, not just
			// the most-recently-grabbed (the v0.4.2 single-slot
			// assumption). A multi-trip hauler killed mid-route could
			// have 30+ items on them; without the loop those items would
			// vanish into the dead body's reference.
			if (s.Inventory != null && s.Inventory.Count > 0)
			{
				for (int i = 0; i < s.Inventory.Count; i++)
				{
					var it = s.Inventory[i];
					it.OwnerShroompId = null;
					it.TilePos = dropPos;
					Map.DropItem(it);
				}
				s.Inventory.Clear();
			}
			if (s.Equipment != null && s.Equipment.Count > 0)
			{
				foreach (var (_, item) in s.Equipment)
				{
					item.OwnerShroompId = null;
					item.TilePos = dropPos;
					Map.DropItem(item);
				}
				s.Equipment.Clear();
			}

			// v0.4.33 — also spawn a Corpse item carrying the dead shroomp's
			// biographical sidecar (CorpseData). The standard
			// AvgCondition-decay path handles rot over ~7 in-game days
			// (LocalMap.TickCorpseDecay walks dropped items daily and
			// removes Corpse entries that hit 0). Personality list is
			// copied (not shared) because Shroomp.Personality may be
			// mutated by future personality-evolution work — the corpse's
			// is a fixed snapshot at time of death.
			var personalityCopy = s.Personality != null
				? new System.Collections.Generic.List<string>(s.Personality)
				: new System.Collections.Generic.List<string>(0);
			var corpse = new Sporeholm.Simulation.Items.Item
			{
				Kind          = Sporeholm.Simulation.Items.ItemKind.Corpse,
				SubType       = "ShroompBody",
				Material      = new Sporeholm.Simulation.Items.MaterialKey("Flesh", "Shroomp"),
				Quality       = Sporeholm.Simulation.Items.Quality.Normal,
				State         = Sporeholm.Simulation.Items.ItemState.Fresh,
				Quantity      = 1,
				AvgCondition  = 100f,
				DurabilityCap = 100f,
				AvgBirthTick  = GlobalTick,
				TilePos       = dropPos,
				CorpseInfo    = new Sporeholm.Simulation.Items.CorpseData(
					Name:        s.Name,
					AgeYears:    s.AgeInYears,
					Sex:         s.Sex,
					Role:        s.Role ?? "Unassigned",
					Cause:       s.CauseOfDeath ?? CauseOfDeath.Natural,
					DeathTick:   GlobalTick,
					Personality: personalityCopy,
					Handedness:  s.Handedness),
			};
			Map.DropItem(corpse);

			// v0.4.35 — broadcast WitnessedDeath thoughts to every living
			// shroomp within ~10 tiles of the body. The thought def has
			// been live since v0.3.43 but nothing was triggering it.
			// Distance is squared-px (no sqrt). AllShroomps() takes a
			// snapshot under _shroompLock so the iteration is safe.
			const float WitnessRadiusPx = 10f * Sporeholm.World.LocalMap.TileSize;
			float wr2 = WitnessRadiusPx * WitnessRadiusPx;
			var living = AllShroomps();
			for (int i = 0; i < living.Count; i++)
			{
				var other = living[i];
				if (!other.IsAlive || other == s) continue;
				float dx = other.SimPos.X - dropPos.X;
				float dy = other.SimPos.Y - dropPos.Y;
				if (dx * dx + dy * dy > wr2) continue;
				ThoughtRegistry.Add(other, "WitnessedDeath", context: s.Name);
			}
		}
	}
}
