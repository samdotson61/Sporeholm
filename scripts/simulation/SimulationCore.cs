using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using SmurfulationC.Simulation.Systems;
using SmurfulationC.World;

namespace SmurfulationC.Simulation
{
	// Owns and drives the full simulation loop on a dedicated background thread.
	//
	// Threading model:
	//   - The simulation thread is the sole writer of Smurf state.
	//   - The main (Godot) thread is the sole reader of SimulationSnapshot records.
	//   - Communication happens only through thread-safe queues (Snapshots and the
	//     Pending* event queues below).
	//   - _smurfLock guards the smurf list against concurrent Add/Remove while the
	//     simulation copies it at the start of each tick.
	public sealed class SimulationCore : IDisposable
	{
		public SimulationClock Clock { get; } = new();

		private SimulationDate _date = SimulationDate.Zero;
		public SimulationDate Date => _date; // read from any thread (struct copy = safe)

		private readonly List<Smurf> _smurfs = new();
		private readonly object _smurfLock = new();

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
		// every smurf stays Hot, which matches initial-load behaviour.
		public Godot.Vector2 CameraFollow;

		// Global tick counter for LOD phase modulo. Increments inside Tick().
		// Sim thread is the sole writer; BehaviorSystem reads.
		public long GlobalTick;

		// (year) — fires once per in-game year
		public ConcurrentQueue<int> PendingYearEvents { get; } = new();

		// (newSeason, previousSeason) — fires 4 times per in-game year
		public ConcurrentQueue<(Season NewSeason, Season PrevSeason)> PendingSeasonEvents { get; } = new();

		// Full snapshot of the smurf at the moment of death
		public ConcurrentQueue<SmurfSnapshot> PendingDeaths { get; } = new();

		// (snapshot, from, to) — fires when a smurf crosses a mood threshold
		public ConcurrentQueue<(SmurfSnapshot Snap, MoodState From, MoodState To)> PendingMoodCrossings { get; } = new();

		// Full snapshot of the smurf at the moment of birth
		public ConcurrentQueue<SmurfSnapshot> PendingBirths { get; } = new();

		// v0.3.47 (Phase 4 sub-B) — wandering-in arrivals. WanderingInSystem
		// enqueues; SimulationManager drains and re-emits to the AlertsPane
		// as a "Wanderer joined" notification. For sub-B the prompt
		// auto-accepts; Phase 8's storyteller will gain Accept/Decline UI.
		public ConcurrentQueue<SmurfSnapshot> PendingWanderers { get; } = new();

		private readonly Random _rng = new();

		// ── Role-change command queue ─────────────────────────────────────────────
		// Main thread enqueues; sim thread applies at the start of the next tick.
		private readonly ConcurrentQueue<(string Name, string NewRole)> _pendingRoleChanges = new();

		public void QueueRoleChange(string smurfName, string newRole) =>
			_pendingRoleChanges.Enqueue((smurfName, newRole));

		// ── Phase 3 — Behavior wiring ─────────────────────────────────────────────
		// Colony resource ledger, owned by SimulationCore. Sim thread mutates;
		// main thread can read via Snapshot() (copy semantics) for HUD display.
		public ColonyResources Resources { get; } = new();

		// LocalMap reference assigned by SimulationManager once the world map is
		// loaded. Sim thread reads + mutates via LocalMap's existing APIs.
		public LocalMap? Map { get; set; }

		// Player-order queue (Roadmap §3.9). Main thread enqueues right-click
		// move orders; BehaviorSystem.Tick drains them. ConcurrentQueue → no lock.
		public ConcurrentQueue<PlayerOrder> PendingPlayerOrders { get; } = new();

		public void QueuePlayerOrder(string smurfName, Vector2 target) =>
			PendingPlayerOrders.Enqueue(new PlayerOrder(smurfName, target));

		// v0.4.3 — right-click "pick up this item" orders. The smurf walks
		// to the tile, picks up whatever item is there, then routes to
		// the colony delivery point — i.e. a Haul cycle anchored by
		// the player's target instead of the nearest unreserved item.
		// (SmurfName, ItemTilePixel)
		public ConcurrentQueue<(string SmurfName, Vector2 ItemTile)> PendingPickUps { get; } = new();

		// v0.4.3 — generic sim-thread command queue. Used by the
		// Inventory tab on the smurf card for equip / unequip / drop
		// actions that must mutate Smurf.Equipped* + Inventory under
		// the sim thread's exclusive write rules.
		public ConcurrentQueue<System.Action> PendingCommands { get; } = new();

		public void PostMainThreadCommand(System.Action cmd)
		{
			if (cmd != null) PendingCommands.Enqueue(cmd);
		}

		// v0.3.24 — combat order queue (Phase 8 stub). Drained from
		// BehaviorSystem.Tick alongside PendingPlayerOrders: each entry sets
		// the named smurf's CombatTargetName (null = clear). The behavior
		// system does not yet act on this — only the visual layer reads it.
		public ConcurrentQueue<(string SmurfName, string? TargetName)> PendingCombatOrders { get; } = new();

		public void QueueCombatOrder(string smurfName, string? targetName) =>
			PendingCombatOrders.Enqueue((smurfName, targetName));

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

		public void AddSmurf(Smurf smurf)
		{
			lock (_smurfLock) _smurfs.Add(smurf);
		}

		public void RemoveSmurf(Smurf smurf)
		{
			lock (_smurfLock) _smurfs.Remove(smurf);
		}

		// Thread-safe snapshot of all smurfs (alive + dead). Used by
		// SimulationManager to seed sim positions before the sim thread starts
		// and after the LocalMap is rebound. Returns a copy so callers can
		// safely iterate without holding the lock.
		public IReadOnlyList<Smurf> AllSmurfs()
		{
			lock (_smurfLock) return new List<Smurf>(_smurfs);
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
						lock (_smurfLock)
						{
							var target = _smurfs.Find(s => s.Name == rc.Name);
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
						// from the queue (or never read). At 1000 smurfs with
						// the struct-snapshot from v0.3.36 each push is much
						// cheaper, but skipping the intermediate ones still
						// saves the per-snapshot dict allocation and the
						// list copy under the smurf-lock.
						bool isLast = (ran + 1 >= maxBatch)
							|| (sw.Elapsed.TotalMilliseconds < nextTickMs + intervalMs);
						// v0.4.18 — catch-all exception barrier. The
						// player reported "smurfs stop and do not move
						// again after a few minutes"; the most likely
						// cause is an unhandled exception inside Tick
						// killing the sim thread silently (Run exits the
						// while loop, `_running` stays true, but the OS
						// thread is dead). Smurf state freezes, the
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
			// v0.4.3 — drain UI-thread commands (equip / unequip / drop)
			// before anything reads Smurf or Inventory state so the
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
				PendingPlayerOrders.Enqueue(new PlayerOrder(pu.SmurfName, pu.ItemTile));

			// 1. Apply queued role changes before snapshotting.
			while (_pendingRoleChanges.TryDequeue(out var rc))
			{
				lock (_smurfLock)
				{
					var target = _smurfs.Find(s => s.Name == rc.Name);
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

			// 2. Remove smurfs that died in a previous tick.
			lock (_smurfLock) _smurfs.RemoveAll(s => !s.IsAlive);

			// 3. Snapshot the living smurf list (holds lock as briefly as possible).
			List<Smurf> working;
			lock (_smurfLock) working = new List<Smurf>(_smurfs);

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
					// every living smurf at day boundary. Independent of
					// the per-smurf birthday check below; XP saturation
					// resets for all colonists regardless of birthday.
					SmurfulationC.Simulation.SkillRegistry.ResetDailyXp(s);

					if (s.BirthdayDayOfYear != newDayOfYear) continue;

					AgingSystem.AdvanceAge(s);

					if (AgingSystem.HasExpired(s))
					{
						// v0.4.60 — single canonical kill path.
						KillSmurf(s, CauseOfDeath.Natural);
					}
				}

				// Fire year/season events after processing deaths so Godot signals
				// carry an accurate colony state.
				if (yearBoundary)
					PendingYearEvents.Enqueue(_date.Year);

				if (seasonBoundary)
				{
					PendingSeasonEvents.Enqueue((_date.Season, prevDate.Season));

					// Birth attempt once per season — adds new smurf if conditions allow.
					// v0.3.47 — pass the current colony food total so the
					// Phase 4 food-stockpile gate can suspend births when
					// reserves are too low.
					int foodTotal = Resources.Inventory.TotalByKind(
						SmurfulationC.Simulation.Items.ItemKind.Food);
					var newborn = BirthSystem.TryBirth(working, _rng, foodTotal);
					if (newborn != null)
					{
						lock (_smurfLock) _smurfs.Add(newborn);
						PendingBirths.Enqueue(new SmurfSnapshot(newborn));
					}

					// v0.3.47 — wandering-in event. Tapered incidence by
					// colony size: ~3/year small colonies (≤ 30), ~1/year
					// medium (30-100), ~0 beyond. One roll per season.
					var wanderer = WanderingInSystem.TryWanderer(working, _rng, foodTotal, GlobalTick);
					if (wanderer != null)
					{
						lock (_smurfLock) _smurfs.Add(wanderer);
						PendingWanderers.Enqueue(new SmurfSnapshot(wanderer));
					}
				}
			}

			// 6. Run simulation systems once per real second (every 60 ticks at 1×).
			if (++_ticksSinceSimUpdate >= SimSystemInterval)
			{
				_ticksSinceSimUpdate = 0;
				int foodCap = BirthSystem.ComputeFoodCapacity(working);
				NeedsSystem.Tick(working, foodCap);   // need decay + starvation damage
				// v0.3.43 — thoughts tick down before MoodSystem reads the
				// resulting MoodFromThoughts. Order matters: ThoughtSystem
				// updates the per-smurf thought-mood sum, then MoodSystem
				// folds it into the final MoodScore.
				ThoughtSystem.Tick(working);
				MoodSystem.Tick(working);

				// Vital organ failure: any vital body part at 0% kills the smurf.
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
							KillSmurf(s, cause);
							break;
						}
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
					PendingMoodCrossings.Enqueue((new SmurfSnapshot(s), prev, newMood));
				}

				// Healing phase runs last — dead smurfs are already marked so HealTick
				// skips them, and vital organs that just hit 0 are not healed back above zero.
				NeedsSystem.HealTick(working);
			}

			// 7. Phase 3 — Behavior. Movement + task evaluation every tick at fixed
			//    dt = base interval. Player orders, critical needs, and role tasks
			//    drive the smurfs' SimPos / SimTarget across the local map.
			//    Mutating CurrentTask / SimPos here is safe because the sim thread
			//    is the sole writer; the next PushSnapshot() captures the result.
			var queue = new Queue<PlayerOrder>();
			while (PendingPlayerOrders.TryDequeue(out var po)) queue.Enqueue(po);

			// v0.3.24 — drain combat-order queue, set the flag on each named
			// smurf. No behavior plumbing yet — this is just so the visual
			// sword icon turns on/off in response to right-click on an enemy.
			while (PendingCombatOrders.TryDequeue(out var co))
			{
				foreach (var s in working)
				{
					if (s.Name == co.SmurfName) { s.CombatTargetName = co.TargetName; break; }
				}
			}

			float dt = SimulationClock.BaseTickIntervalMs / 1000f;
			// v0.3.39 (O-H.2) — increment the global tick counter and pass
			// to BehaviorSystem so its per-smurf LOD skip can compare
			// (s.TickPhase, s.TickSlot) against the current tick modulo.
			// Also reassigns phases periodically based on camera distance.
			GlobalTick++;
			// v0.3.40 — reassignment cadence 32 → 16 ticks (≈ 0.27 sec at 1×).
			// Faster reassign means the camera can scroll over the colony
			// without leaving cold-banded smurfs visible for long (smurfs
			// entering frame are reclassified to Hot within a quarter
			// second). Phase assignment is a single distance check per
			// smurf — cheap even at 1000 smurfs.
			if ((GlobalTick & 15) == 0)
				BehaviorSystem.AssignTickPhases(working, CameraFollow);
			BehaviorSystem.Tick(working, Map, Resources, queue, _rng, dt, GlobalTick);

			// 8. Push snapshot — only alive smurfs are included (filtered in constructor).
			// v0.3.36 — skip during catch-up batch's intermediate ticks.
			if (pushSnapshot) PushSnapshot();
		}

		private void PushSnapshot()
		{
			List<Smurf> snap;
			lock (_smurfLock) snap = new List<Smurf>(_smurfs);
			Snapshots.Enqueue(new SimulationSnapshot(_date, snap));
			while (Snapshots.Count > MaxQueueDepth)
				Snapshots.TryDequeue(out _);
		}

		public void Dispose() => Stop();

		// v0.4.60 — canonical kill pipeline. Mirrors RimWorld's `Pawn.Kill()`
		// pattern: a single entry point that flips the Dead flag, drops
		// loose gear at the death tile, spawns the corpse Item, and
		// enqueues the PendingDeaths event. Call from sim thread only —
		// reads/writes Smurf state and Map state under the same lock
		// regime as the rest of Tick(). DevPanel routes through
		// PostMainThreadCommand to land on the sim thread.
		//
		// Idempotent: a second call on an already-dead smurf no-ops, so
		// natural-death paths and Dev kill can race without double-spawning
		// the corpse.
		//
		// Order matches the RimWorld decompile (Verse.Pawn.Kill ~L2197):
		//   1. Capture position (implicit — Smurf.SimPos is read inside
		//      DropCorpseGear before any state mutation could move it)
		//   2. Set CauseOfDeath
		//   3. DropCorpseGear: inventory drop → equipment drop → corpse
		//      spawn → witness-thought broadcast (Map.DropItem reads
		//      live SimPos)
		//   4. Set IsAlive = false (the dead-flag transition that
		//      _smurfs.RemoveAll picks up at the next tick boundary)
		//   5. Enqueue PendingDeaths so the SimulationManager death
		//      signal fires for UI updates (message log, achievement
		//      hooks, mood broadcasts).
		//
		// The flag flip happens AFTER DropCorpseGear so any code path
		// inside DropCorpseGear that filters on IsAlive (e.g. the
		// witness-thought loop's `other.IsAlive` check) doesn't accidentally
		// skip the dying smurf — though that smurf is `other == s` filtered
		// anyway, the ordering keeps the invariant simple.
		public void KillSmurf(Smurf s, CauseOfDeath cause)
		{
			if (s == null || !s.IsAlive) return;
			s.CauseOfDeath = cause;
			DropCorpseGear(s);
			s.IsAlive = false;
			PendingDeaths.Enqueue(new SmurfSnapshot(s));
		}

		// v0.4.7 (bugreport B-2) — DF / RimWorld convention: when a smurf
		// dies, every carried + equipped item drops on the death tile.
		// Previously these items were silently GC'd along with the dead
		// Smurf object — a Guardian carrying a Masterwork Spear would
		// lose the spear entirely on death. Now the player can recover
		// the gear from the corpse's tile.
		private void DropCorpseGear(Smurf s)
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
					it.OwnerSmurfId = null;
					it.TilePos = dropPos;
					Map.DropItem(it);
				}
				s.Inventory.Clear();
			}
			if (s.Equipment != null && s.Equipment.Count > 0)
			{
				foreach (var (_, item) in s.Equipment)
				{
					item.OwnerSmurfId = null;
					item.TilePos = dropPos;
					Map.DropItem(item);
				}
				s.Equipment.Clear();
			}

			// v0.4.33 — also spawn a Corpse item carrying the dead smurf's
			// biographical sidecar (CorpseData). The standard
			// AvgCondition-decay path handles rot over ~7 in-game days
			// (LocalMap.TickCorpseDecay walks dropped items daily and
			// removes Corpse entries that hit 0). Personality list is
			// copied (not shared) because Smurf.Personality may be
			// mutated by future personality-evolution work — the corpse's
			// is a fixed snapshot at time of death.
			var personalityCopy = s.Personality != null
				? new System.Collections.Generic.List<string>(s.Personality)
				: new System.Collections.Generic.List<string>(0);
			var corpse = new SmurfulationC.Simulation.Items.Item
			{
				Kind          = SmurfulationC.Simulation.Items.ItemKind.Corpse,
				SubType       = "SmurfBody",
				Material      = new SmurfulationC.Simulation.Items.MaterialKey("Flesh", "Smurf"),
				Quality       = SmurfulationC.Simulation.Items.Quality.Normal,
				State         = SmurfulationC.Simulation.Items.ItemState.Fresh,
				Quantity      = 1,
				AvgCondition  = 100f,
				DurabilityCap = 100f,
				AvgBirthTick  = GlobalTick,
				TilePos       = dropPos,
				CorpseInfo    = new SmurfulationC.Simulation.Items.CorpseData(
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
			// smurf within ~10 tiles of the body. The thought def has
			// been live since v0.3.43 but nothing was triggering it.
			// Distance is squared-px (no sqrt). AllSmurfs() takes a
			// snapshot under _smurfLock so the iteration is safe.
			const float WitnessRadiusPx = 10f * SmurfulationC.World.LocalMap.TileSize;
			float wr2 = WitnessRadiusPx * WitnessRadiusPx;
			var living = AllSmurfs();
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
