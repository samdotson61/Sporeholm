using System;
using System.Collections.Generic;
using Godot;

namespace Sporeholm.Simulation
{
	public enum LifeStage { Sprout, Juvenile, Adult, Elder, LastSeason }
	public enum Sex { Male, Female }
	public enum MoodState { Inspired, Content, Stressed, Distressed, Breaking, Collapse }
	public enum CauseOfDeath { Natural, Starvation, Combat, Dev, BloodLoss }

	// v0.5.63 — Cap-and-stem geometry visual identity per shroomport.md §4.2.
	// Replaces the pre-rename humanoid-pawn identity (skin/hair). Each
	// Shroomp rolls these fields at creation; ShroompColonyView.BakeShroompSprite
	// keys its sprite cache on (CapShape, CapColour bucket, StemColour bucket,
	// Sex, Mood) and draws a procedural cap-and-stem pixel art.
	public enum CapShape   : byte { Round, Convex, Flat, Depressed, Pointed, Spotted }
	public enum CapTexture : byte { Smooth, Scaly, Pitted }
	public enum StemBuild  : byte { Stocky, Average, Slim }

	// Core shroomp data model. Lives exclusively on the simulation thread.
	// The main thread only reads from ShroompSnapshot, never from this class directly.
	public class Shroomp
	{
		public Guid Id { get; } = Guid.NewGuid();
		public string Name { get; set; } = string.Empty;
		public int AgeInYears { get; set; }
		public Sex Sex { get; set; } = Sex.Male;
		public string Role { get; set; } = "Unassigned";
		public bool IsAlive { get; set; } = true;

		// Needs: 0 (critical) to 100 (fully satisfied)
		public float Nutrition { get; set; } = 100f;
		public float Rest { get; set; } = 100f;
		public float Social { get; set; } = 100f;
		public float MagicResonance { get; set; } = 100f;
		public float Safety { get; set; } = 100f;
		// v0.4.63 (G4 from rimport.md) — Joy / Recreation as the sixth need.
		// Decays slowly during all activity; restored by idle tasks
		// (Wander/Loiter/Observe/Converse/Meditate/VisitFavorite, all of
		// which already exist in BehaviorSystem). Mirrors RimWorld's Joy
		// need: a colony that works without break for too long sees
		// mood drift down via low-Joy thoughts ("DemoralizedDrudge").
		// Decay rate is the slowest of the five existing needs because
		// idle tasks already fire opportunistically — Joy ticks down
		// slow enough that a shroomp with a few minutes of idle activity
		// per day stays topped up.
		public float Joy { get; set; } = 100f;

		public float MoodScore    { get; set; } = 100f;
		// Raw score from needs only (before personality modifiers).
		public float MoodRaw      { get; set; } = 100f;
		// Sum of personality trait mood modifiers applied this tick.
		public float MoodModifier { get; set; } = 0f;

		public MoodState MoodState => MoodScore switch
		{
			>= 80f => MoodState.Inspired,
			>= 60f => MoodState.Content,
			>= 40f => MoodState.Stressed,
			>= 20f => MoodState.Distressed,
			> 0f   => MoodState.Breaking,
			_      => MoodState.Collapse
		};

		public LifeStage LifeStage => AgeInYears switch
		{
			< 20  => LifeStage.Sprout,
			< 50  => LifeStage.Juvenile,
			< 400 => LifeStage.Adult,
			< 545 => LifeStage.Elder,
			_     => LifeStage.LastSeason
		};

		// Day of year (0–119) on which this shroomp ages by 1 each year.
		// 120 days per year: 4 seasons × 30 days. Assigned randomly at creation.
		public int BirthdayDayOfYear { get; set; } = 0;

		// Set at the moment IsAlive becomes false. Null while alive.
		public CauseOfDeath? CauseOfDeath { get; set; } = null;

		// Tracks the mood state from the previous MoodSystem pass for threshold detection.
		public MoodState PreviousMoodState { get; set; } = MoodState.Content;

		// v0.5.84t — starvation transition tracker. True while Nutrition < 20.
		// Used by SimulationCore to emit a one-shot PendingStarvationStarts
		// event on the rising edge (false → true). Cleared when Nutrition
		// rises back above the threshold so a recovered-then-starved pawn
		// alerts again. Sam: "message box should indicate when shroomps are
		// starving."
		public bool WasStarving { get; set; } = false;

		// v0.5.84t — Pacifist trait. RimWorld parity: Pawn.story.WorkTags has
		// the Violent flag, and Trait_NonViolent disables it — pacifists
		// refuse to auto-equip weapons and won't draft for combat. Rolled at
		// shroomp gen at ~8% incidence (RimWorld's NonViolent rate). Cleared
		// to false by default so old saves load as non-pacifist. EquipmentSystem
		// reads this in AutoEquipBetterWeapon to early-return.
		// Sam: "unless they're a pacifist."
		public bool IsPacifist { get; set; } = false;

		// Biological trait penetrance: 0.0 (suppressed) to 1.0 (fully expressed)
		public Dictionary<string, float> Traits { get; } = new();

		// Skills: 0–20  (0 = untrained, 20 = master)
		public Dictionary<string, int> Skills { get; } = new();

		// v0.4.62 (G3 from rimport.md) — RimWorld-style skill XP. Each skill
		// accumulates XP on relevant work; when XP crosses
		// `SkillRegistry.LevelThreshold(level)` the level increments and
		// the excess carries over. SkillsXp is the "XP within current
		// level" tally; SkillsXpToday is the daily-cap window cleared by
		// SimulationCore on dayBoundary. RimWorld constants matched:
		// daily cap 4000 XP, beyond which gains scale by 0.2 ×
		// (saturation factor).
		//
		// Both dicts are populated lazily by `SkillRegistry.GainXp` —
		// pre-existing shroomps (loaded from save before this version)
		// get fresh entries on first work tick. No save migration
		// needed; missing keys simply default to 0.
		public Dictionary<string, float> SkillsXp { get; } = new();
		public Dictionary<string, float> SkillsXpToday { get; } = new();

		// Personality traits — 2-3 string names from PersonalityRegistry.
		public List<string> Personality { get; set; } = new();

		// v0.4.64 (G6 from rimport.md) — backstory strings displayed on the
		// shroomp card. Childhood applies to every shroomp; Adulthood only fires
		// for shroomps old enough to have had one (Juvenile+). RimWorld uses
		// these as both narrative texture AND a skill-modifier source —
		// `BackstoryRegistry.ApplyTo` bumps starting skill levels at
		// generation time. Value is a key from BackstoryRegistry; null /
		// empty string = no backstory assigned (legacy save shroomps).
		public string Childhood { get; set; } = string.Empty;
		public string Adulthood { get; set; } = string.Empty;

		// Body part condition: part name → 0–100. Populated from BodyPartRegistry.
		public Dictionary<string, float> BodyParts { get; set; } = new();

		// v0.5.82 — RimWorld-parity pawn-blocked-path cooldown. Set to
		// the current sim tick by BehaviorSystem.RecordPathPawnBlockage
		// whenever a freshly-computed A* path contains at least one
		// pawn-occupied tile. The stuck-detection re-path branch then
		// refuses to re-plan again for PawnBlockedRepathCooldown ticks
		// (~240 = 4 in-game seconds at 1×), mirroring RimWorld's
		// Pawn_PathFollower.BestPathHadPawnsInTheWayRecently anti-jitter
		// gate. Without the cooldown, a shroomp stuck behind a cluster
		// repath-loops every StuckRePathTicks (8) and never gets the
		// occupancy snapshot a chance to disperse. Default long.MinValue
		// so the cooldown is inactive at colony start.
		public long IsLastPawnBlockedPathTick_BackingField = long.MinValue;
		public long LastPawnBlockedPathTick
		{
			get => IsLastPawnBlockedPathTick_BackingField;
			set => IsLastPawnBlockedPathTick_BackingField = value;
		}

		// v0.5.79 — RimWorld-parity "Downed" state. Pawn is incapacitated
		// (lying on the ground, can't act) but not yet dead. Set when the
		// weighted-average health drops below the down threshold (default
		// 30 % = 70 damage taken). Cleared when health recovers above the
		// stand-back-up threshold (down + 10 % hysteresis to prevent
		// flicker). See BehaviorSystem.UpdateDownedState. Renderer rotates
		// the sprite horizontal like a sleeper but with a darker tint.
		public bool IsDowned { get; set; } = false;

		// v0.5.79 — weighted-average health across all body parts. Mirrors
		// the ShroompCardPanel "Health %" computation (vital parts ×3,
		// non-vital ×1). Returns 100 if no body parts populated yet (fresh
		// shroomp).
		public float ComputeHealthPercent()
		{
			float weightedSum = 0f, totalWeight = 0f;
			foreach (var def in Sporeholm.Simulation.BodyPartRegistry.Template)
			{
				if (!BodyParts.TryGetValue(def.Name, out float cond)) continue;
				float w = def.Vital ? 3f : 1f;
				weightedSum += cond * w;
				totalWeight += w;
			}
			return totalWeight > 0f ? weightedSum / totalWeight : 100f;
		}

		// v0.5.81 — Bleeding system foundation for Phase 7 combat. Per-tick
		// BleedRate is recomputed from the sum of "open wound" severity on
		// each damaged body part (parts under 50 % condition contribute
		// proportionally). Accumulated BloodLoss kills the shroomp once it
		// hits 100. Slow regen (~5 min to full recovery) when no parts are
		// bleeding. Sam: "Ensure bleeding is implemented with appropriate
		// graphic and slowed movement for injuries. This is in preparation
		// for later full implementation in phase 7."
		public float BloodLoss { get; set; } = 0f;  // 0 = full blood, 100 = dead from blood loss
		public float BleedRate { get; set; } = 0f;  // per-second accumulation rate

		// Update BleedRate from current body-part conditions. Vital parts
		// bleed faster than non-vital. Called once per sim tick by
		// SimulationCore (alongside the existing vital-part-zero death
		// check). Pure function — no side effects beyond writing BleedRate.
		public void RecomputeBleedRate()
		{
			float rate = 0f;
			foreach (var def in Sporeholm.Simulation.BodyPartRegistry.Template)
			{
				if (!BodyParts.TryGetValue(def.Name, out float cond)) continue;
				if (cond >= 50f) continue;   // healthy enough to not bleed
				float severity = 50f - cond;   // 0…50
				float partWeight = def.Vital ? 0.0012f : 0.0006f;
				rate += severity * partWeight;
			}
			BleedRate = rate;
		}

		// v0.5.81 — Moving capacity derived from leg + foot body-part
		// conditions. Multiplier applied to walking speed in
		// BehaviorSystem.MoveOneTick. Both legs destroyed = ~0 movement
		// (downed via the Health threshold long before this hits 0). A
		// single damaged leg drops capacity to ~0.7×. Healthy = 1.0×.
		// Mirrors RimWorld's Moving capacity calculation at a simpler
		// granularity (no per-tier disables, no manipulation gating).
		public float ComputeMovingCapacity()
		{
			float legProduct = 1f;
			int   legParts   = 0;
			foreach (var def in Sporeholm.Simulation.BodyPartRegistry.Template)
			{
				bool isLeg = def.Name.Contains("Leg") || def.Name.Contains("Foot");
				if (!isLeg) continue;
				if (!BodyParts.TryGetValue(def.Name, out float cond)) continue;
				legParts++;
				legProduct *= System.Math.Max(0f, cond) / 100f;
			}
			if (legParts == 0) return 1f;
			// Take the geometric mean across leg/foot parts so one shredded
			// leg drops to ~0.5, both shredded → near-zero.
			return MathF.Pow(legProduct, 1f / legParts);
		}

		// v0.5.84r — Athletics-derived stat stubs. Sam: "a tiny increase
		// in carry capacity/movement speed for each level... [and] disease
		// resistance (Stub)." Move speed multiplier is applied directly
		// in BehaviorSystem.MoveOneTick. These two computed properties
		// stub the carry-capacity + disease-resistance bonuses for the
		// future Haul-stack-size + Phase 12 disease systems; both are
		// already used (carry capacity stub returns 1 extra slot per 5
		// Athletics levels; disease resistance returns a percentage that
		// nothing consults yet but is the contract the disease system
		// will read).
		public int CarryCapacityBonus
		{
			get
			{
				if (!Skills.TryGetValue("Athletics", out int lvl)) return 0;
				return lvl / 5;   // lvl 0-4 = +0, lvl 5-9 = +1, ..., lvl 20 = +4
			}
		}

		public float DiseaseResistance
		{
			get
			{
				if (!Skills.TryGetValue("Athletics", out int lvl)) return 0f;
				return lvl * 0.02f;   // lvl 0 = 0%, lvl 20 = 40% — stub multiplier for Phase 12 disease checks
			}
		}

		// ── Phase 3 — Behavior System ────────────────────────────────────────
		// Authoritative position lives on the sim thread (Roadmap §3.1). The main
		// thread reads SimPos via ShroompSnapshot and lerps the visual avatar toward
		// it each frame, so movement is sim-driven instead of cosmetic wander.

		// World-pixel position (LocalMap tile size × tile coords). Zero is treated
		// as "uninitialised" by the spawn pass.
		public Vector2 SimPos    { get; set; } = Vector2.Zero;
		public Vector2 SimTarget { get; set; } = Vector2.Zero;

		// Movement speed in pixels/second, scaled by role per Roadmap §3.7.
		public float   SimSpeed  { get; set; } = 32f;

		// Current task driving this shroomp's behaviour. Null = idle.
		// v0.3.36 — BehaviorTask is now a record struct, so CurrentTask is
		// `Nullable<BehaviorTask>`. Null checks (`s.CurrentTask == null`)
		// still work; field reads use `.Value.X` or pattern matching.
		public BehaviorTask? CurrentTask { get; set; }

		// v0.3.22 — pathfinding hook for Phase 4. The A* planner will populate
		// this queue with intermediate tile-centre waypoints; per-tick movement
		// consumes the head when the shroomp reaches it. Empty list means "no
		// path computed" — Phase 3 falls back to local greedy steering, which
		// is sufficient for short routes in open terrain.
		public List<Vector2> PathWaypoints { get; set; } = new();

		// v0.3.22 — stuck-detection state. MovementSystem compares SimPos to
		// PrevSimPos each tick; a near-zero delta increments StuckTicks. When
		// StuckTicks crosses a threshold the current task is cleared so the
		// shroomp re-evaluates instead of pingponging against the same obstacle.
		public Vector2 PrevSimPos { get; set; } = Vector2.Zero;
		public int     StuckTicks { get; set; } = 0;

		// v0.5.84t — tile-progress stuck check. Pre-v0.5.84t the stuck
		// detector compared `(SimPos - PrevSimPos).Length() < ArrivalEpsilon`,
		// which is a 0.5-pixel threshold. A pawn micro-jittering 0.6 px/tick
		// against a wall (e.g. crowdedFallback nudging primary into an
		// impassable target before the v0.5.84t IsClimbOverUseful + Resolve
		// WalkTarget fixes) cleared the threshold every tick — StuckTicks
		// never accumulated, no re-path ever fired. The new check also
		// requires crossing a tile boundary: if LastProgressTileIdx hasn't
		// changed in N ticks, count as stuck regardless of pixel motion.
		// Updated by MoveOneTick whenever the shroomp enters a new tile.
		public int     LastProgressTileIdx { get; set; } = -1;

		// v0.5.84t — per-tick mining progress. RimWorld parity: GatherMaterial
		// now accumulates work over multiple ticks (Boulder = 200 work units,
		// DeadLog = 150, LivingWood = 200, Skeleton = 100) at a rate of
		// 10 × MiningSpeedFactor(skill) × ToolBonus per tick. Resets when
		// the shroomp targets a different tile or abandons the task.
		// Activates the dormant SkillCurve.MiningSpeedFactor curve (0.04
		// at lvl 0 → 2.44 at lvl 20 — ~60× spread) + the v0.5.84t tool
		// bonus (1.30× × Quality 0.90-1.50). Result: a lvl-0 bare-handed
		// miner takes ~500 ticks to break a boulder; a lvl-20 master with
		// a Masterwork Pick clears it in ~5 ticks.
		public int     GatherProgress      { get; set; } = 0;
		public int     GatherTargetTileX   { get; set; } = -1;
		public int     GatherTargetTileY   { get; set; } = -1;

		// v0.4.17 — single-shot guard for the mid-stuck re-pathfind attempt.
		// Set to true when BehaviorSystem triggers a halfway re-pathfind
		// against a stale path; cleared whenever StuckTicks resets to 0
		// (shroomp made progress, or a new task was assigned). Prevents the
		// re-pathfind from firing on every tick once the threshold is met.
		public bool    RePathTried { get; set; } = false;

		// v0.4.29 — DF-style "lie down so the other guy can climb over"
		// counter. When > 0 the shroomp is yielding for that many sim
		// ticks: skipped by MoveOneTick (no movement attempt), excluded
		// from the per-tick occupancy grid (so neighbours can step
		// freely onto its tile and cross over). Decremented in
		// MoveOneTick. Triggered on a *blocking* shroomp when another
		// shroomp has been stuck behind them in a single-tile choke-point
		// long enough to need the swap. Lore-friendly resolution for
		// the narrow-tunnel jam that the per-tile claim cascade alone
		// couldn't unblock once shroomps were already inside.
		public int     YieldingTicks { get; set; } = 0;

		// v0.4.57 — post-abandonment cooldown that suppresses designation
		// task selection. RimWorld-equivalent: `Pawn_JobTracker`'s
		// `jobsGivenRecentTicks` 10-jobs-in-10-ticks spam-guard kicks the
		// pawn into a forced idle when the job system is thrashing. We
		// can't run that exact mechanism (we don't reissue per-tick), but
		// the underlying problem is the same — a shroomp that just gave up
		// on a designation will, on the very next SelectTask, re-pick the
		// nearest reachable designation, which is almost always the SAME
		// one it just abandoned. The cooldown forces a short wander/idle
		// window after abandonment so the cluster breathes — by the time
		// the shroomp re-evaluates, some other shroomp has either claimed and
		// cleared the work, or moved away enough that a different
		// designation is closer.
		//
		// v0.4.59 — halved to 60 ticks (~1 s at 1×) from 120 (~2 s).
		// Sam: "Decrease amount of time to retry actions." With v0.4.58
		// A* crowd avoidance dispersing paths strategically, the
		// cooldown's role is reduced; 1 s of forced wander is enough to
		// physically displace the shroomp to a new position.
		// Decremented in BehaviorSystem.Tick. 0 = no cooldown, eligible
		// for designation tasks; > 0 = forced into idle/wander tier 3.
		public int     DesignationCooldownTicks { get; set; } = 0;

		// v0.5.4 — RimWorld-style "JobSearchSuppressTime" debounce. Set
		// after SelectTask returns an idle task (no work currently
		// reachable for this shroomp). Suppresses the `workAvailable`
		// re-eval clause for ~60 ticks (~1 s at 1×) so an idle shroomp
		// commits to their chosen leisure activity instead of re-rolling
		// a new random idle every tick because designations exist
		// somewhere globally that aren't reachable / claimed / not
		// assignable to this shroomp. Sam: "A person won't stop and freeze,
		// jittering in place while they rapidly cycle between which
		// leisure activities they might do."
		//
		// Critical needs (life-threatening) and chained player orders
		// still bypass this — they're separate clauses in the
		// `needNewTask` gate. The cooldown only debounces the
		// "designations-exist-somewhere" polling.
		//
		// RimWorld parity: ThinkNode_JobGiver.tryGiveJob sets
		// JobSearchSuppressUntilTick after a null Job return so the
		// pawn doesn't ask the same JobGiver every tick when no work
		// is available.
		public int     WorkSearchCooldownTicks { get; set; } = 0;

		// v0.5.84g — path-fail cooldown. Set to 30 (~0.5 s at 1×) after
		// any A* failure. While > 0, BehaviorSystem suppresses needNewTask
		// in the CurrentTask=null branch (life-threat overrides). Caps the
		// A*-recall rate per pawn under failure conditions: pre-v0.5.84a
		// the stuck-detector threw 36 ticks of grace before re-pick;
		// v0.5.84a immediate-drop-on-fail correctly stopped wall-walking
		// but let needNewTask fire every tick when CurrentTask was null,
		// re-calling A* at 60 Hz on the same chokepoint failure — at 50
		// pop with the v0.5.84f MaxNodes=4096 bump it ground the sim
		// thread to a halt in playtest. This cooldown reinstates a
		// throttle without re-introducing wall-walking.
		public int     PathFailCooldownTicks { get; set; } = 0;

		// v0.5.5 — multi-hop Wander state. "Take a walk" should mean a
		// real walk (2-4 destinations in sequence) rather than a single
		// short hop. Set by NewWanderTask; decremented and chained in
		// ApplyTaskEffect's Wander case. Zero = no more hops, shroomp
		// finishes the current destination's linger normally and the
		// idle activity ends. Sam: "a shroomp should actually take a short
		// walk and finish it when 'taking a walk'."
		public int     WanderHopsRemaining { get; set; } = 0;

		// v0.5.60 — JoyTolerance per idle-activity (RimWorld parity). Pawns
		// who do the same recreation activity repeatedly get diminishing
		// joy from it. Tracked as a 0-1 saturation per idle TaskType:
		// 0 = fresh, +Joy at full rate; 1 = burned out, +Joy at near-zero.
		// Ticks UP during the matching activity, decays slowly during ANY
		// other activity. Drives both idle-weight selection and joy-gain
		// scaling so colonies naturally cycle through Meditate / Loiter /
		// Observe / Wander / Converse instead of locking onto one.
		public System.Collections.Generic.Dictionary<TaskType, float> JoyTolerance { get; } = new();

		// v0.5.60 — per-(other-shroomp-name) cooldown for interactions so a
		// pair doesn't fire Chitchat every tick they're standing next to
		// each other. Keys are partner.Name; values are the sim tick at
		// which the cooldown expires. InteractionTracker checks + writes.
		public System.Collections.Generic.Dictionary<string, long> InteractionCooldowns { get; } = new();

		// v0.5.57 — RimWorld-parity physical haul-to-site for the Build task.
		// When a constructor takes a Build task and the blueprint still needs
		// materials, the task target is redirected to the nearest matching
		// material stack (the SOURCE). BuildSiteTileX/Y remembers the
		// blueprint coordinates while the shroomp is en route to the source —
		// once materials are picked up the task target swaps back to the
		// blueprint. Cleared when the shroomp actually arrives at the blueprint
		// to begin depositing / framing. -1 means "no in-flight haul-to-site
		// errand; the task target IS the blueprint."
		public int     BuildSiteTileX { get; set; } = -1;
		public int     BuildSiteTileY { get; set; } = -1;

		// v0.5.20 (Phase 5C — rimport.md N6) — per-shroomp allowed-area
		// bitmap. RimWorld's "Allowed Area" pattern. When non-null, the
		// shroomp will not pick work tasks whose target tile is outside
		// the painted area. Pathfinder may still route through outside
		// tiles (faster paths matter), but task selection respects it.
		// Null = no restriction (the colony default — every shroomp can
		// work anywhere).
		//
		// Stored as a flat bool[Width*Height] so per-tile lookup is O(1)
		// and array allocation is one shot per shroomp when first painted.
		// Roughly 4 KB per shroomp at 80×50 default; 64 KB at 480×300 max
		// playable. At 250 shroomps cap (project_scope_population.md) that's
		// 1 MB worst case for the bitmap layer — well within budget.
		// v0.5.25 — legacy per-shroomp allowed-area bitmap. Deprecated as of
		// v0.5.44 when the colony-shared NamedAreas system landed; field
		// retained for save-load back-compat but BehaviorSystem now reads
		// AssignedAreaName + LocalMap.GetAreaCells instead.
		public bool[]? AllowedArea { get; set; } = null;
		public int     AllowedAreaWidth { get; set; } = 0;   // for safe index recompute on map change

		// v0.5.44 — RimWorld-parity Areas system. Each shroomp may be
		// assigned to a single named area (e.g. "Home"). Null = unrestricted.
		// When non-null, BehaviorSystem.IsTileInAllowedArea gates designation
		// work by `LocalMap.GetAreaCells(AssignedAreaName)` — only tiles
		// flagged true in that area's bitmap are valid work targets.
		// Matches RimWorld's per-pawn Allowed Area dropdown in the Assign
		// tab.
		public string? AssignedAreaName { get; set; } = null;

		// v0.5.11 — distance-not-decreasing stuck detector. RimWorld
		// pawns re-path when they're not making progress toward their
		// goal — regardless of whether they're physically moving. Our
		// existing StuckTicks (immobility-based) misses the case where
		// the shroomp IS moving (sideways at a corner) but isn't getting
		// any closer to the next path waypoint. Tracks the smallest
		// distance² ever achieved to the current walk target; reset when
		// the walk target changes (waypoint pops or task replaces).
		// Sam: "Shroomps still get stuck on corners of weird formations
		// from time to time."
		public float   MinSqrDistanceToWalkTarget { get; set; } = float.MaxValue;
		public int     NoProgressTicks            { get; set; } = 0;
		public int     LastWalkTargetTileX        { get; set; } = -1;
		public int     LastWalkTargetTileY        { get; set; } = -1;
		// Separate from RePathTried (which is reset whenever the shroomp
		// is moving, line 1492 — the immobility detector's reset
		// behavior). Progress-based re-path needs its own one-shot
		// budget that survives sideways oscillation.
		public bool    ProgressRePathTried        { get; set; } = false;

		// v0.4.19 — task-failure recovery state. Tasks that complete
		// without producing any output (Haul pickup target missing,
		// designation already cleared by another shroomp, slot depleted
		// before the shroomp arrived) used to chain together silently: the
		// shroomp would deliver, fail-the-next-haul, deliver, fail again,
		// and visibly cluster around the delivery point making no
		// progress. `TaskDidWork` is set by each `ApplyTaskEffect` case
		// when it actually produces output (item drop, terrain mutation,
		// inventory deposit); when a task completes (CurrentTask becomes
		// null) BehaviorSystem checks this flag — true resets
		// `ConsecutiveTaskFailures` to 0, false increments it. At
		// `TaskFailureForceWander` (3 in a row) the shroomp is forced to
		// take a Wander task with double linger to break the loop.
		public bool TaskDidWork              { get; set; } = false;
		public int  ConsecutiveTaskFailures  { get; set; } = 0;

		// v0.3.35 — short-term per-shroomp blacklist used by FindNearestExcavate /
		// FindNearestGather. v0.3.40 — extended from a single slot to a
		// small FIFO so consecutive stucks blacklist multiple distinct
		// tiles. Without this, a shroomp that gave up on T1 then gave up on
		// T2 would blacklist only T2 — T1 became eligible again immediately
		// and the shroomp cycled T1 ↔ T2 forever. Fixed-size 4-entry array
		// (Vector3-style packing: X, Y, TicksLeft per slot) is enough to
		// cover the practical "few stucks in a row" pattern without
		// per-shroomp List allocation. Slot is unused when TicksLeft = 0.
		public (int X, int Y, int TicksLeft)[] AvoidTiles { get; set; } =
			new (int, int, int)[4];

		// v0.3.36 (B.14) — mood-cache snapshot. MoodSystem.Tick reads this
		// before recomputing; if every input need changed by less than the
		// epsilon since the last recompute, the recompute is skipped. At
		// 1000 shroomps the mood-system pass would otherwise walk every shroomp
		// every tick through TraitRegistry.GetNeedDecayMod + the per-trait
		// modifier sum — meaningful CPU when only a handful of shroomps
		// actually have changing needs.
		public float MoodCacheNutrition { get; set; } = float.NaN;
		public float MoodCacheRest      { get; set; } = float.NaN;
		public float MoodCacheSocial    { get; set; } = float.NaN;
		public float MoodCacheMagic     { get; set; } = float.NaN;
		public float MoodCacheSafety    { get; set; } = float.NaN;
		public float MoodCacheJoy       { get; set; } = float.NaN;   // v0.4.63 (G4)

		// v0.3.39 (O-H.2) — LOD tick phase.
		//   0 = Hot (every tick — visible / on-camera).
		//   1 = Warm (every 3rd tick — within ~50 tiles of camera).
		//   2 = Cold (every 6th tick — everywhere else).
		// BehaviorSystem reads the global tick counter and skips shroomps
		// whose phase mod doesn't match. SimulationCore periodically
		// reassigns phases based on camera-distance to keep visible
		// shroomps hot. Combined with the phase-divided per-tick step
		// adjustment in MoveOneTick, warm/cold shroomps make the same
		// real-world progress per unit time as hot ones — just in
		// fewer, larger steps.
		public byte TickPhase { get; set; } = 0;
		// Sub-phase slot for fair distribution within warm/cold groups
		// — e.g. if 100 shroomps are warm, ~33 fire each tick rather than
		// all 100 at once. Assigned at the same time as TickPhase.
		public byte TickSlot { get; set; } = 0;

		// v0.3.24 — combat stub (Phase 9). When set, BehaviorSystem will route
		// the shroomp to attack this target instead of the autonomous task. For
		// now there is no enemy entity type, so this is data-plumbing only:
		// the visual layer reads it to draw a sword icon over the shroomp's
		// head, and the eventual Phase 9 combat system will fill in actions.
		public string? CombatTargetName { get; set; }

		// v0.3.43 — Thoughts (RimWorld-style temporal mood entries) and
		// Preferences (DF-style persistent likes/dislikes). See Thought.cs
		// and Preferences.cs for the data shapes. Together they replace
		// the "shroomp does Wander forever in idle" mechanic with one that
		// has memory of recent events and a personality-coloured opinion
		// on activities, foods, materials, and other shroomps.

		// Ring of active thoughts. Allocated lazily on first ThoughtRegistry.Add
		// so shroomps that never accumulate a thought (e.g. the dead) pay no
		// memory cost. Capacity = ThoughtRegistry.ThoughtCapacity (8).
		public Thought[]? Thoughts { get; set; }

		// Cached sum of MoodOffset over live thoughts, recomputed by
		// ThoughtSystem.Tick when something changes. MoodSystem reads this
		// when blending raw needs + personality + thought contribution into
		// the final MoodScore. Always in the range [-50, +50].
		public float MoodFromThoughts { get; set; } = 0f;

		// Set whenever a thought is added or removed; ThoughtSystem clears
		// it after recomputing MoodFromThoughts. Lets MoodSystem skip the
		// recompute when the thought layer didn't change.
		public bool ThoughtsDirty { get; set; } = false;

		// Persistent likes/dislikes plus runtime-built friends/enemies. Rolled
		// at shroomp creation (SimulationManager.SeedColony / BirthSystem /
		// ScenarioPanel) and carried for life. Drives priority weighting in
		// BehaviorSystem.SelectTask + colours the thoughts emitted on
		// completion.
		public Preferences Preferences { get; set; } = new();

		// v0.3.43 — idle-arrival linger. When a Tier-3 idle task arrives at
		// its target, the shroomp stays for this many sim ticks before re-
		// evaluating. Without it, the previous Wander-only loop instantly
		// picked the next destination on arrival, which produced the
		// jittering-in-place feel Sam called out. Set by NewIdleTask
		// constructors to a per-activity value (Observe ≫ Wander).
		public int IdleLingerTicks { get; set; } = 0;

		// v0.5.1 — tracks whether an idle task's shroomp has reached its
		// destination yet. False at task creation; flipped true the first
		// tick MoveOneTick fires arrival on this task. The needNewTask
		// gate's lingerExpired check requires both `IdleArrived == true`
		// AND `IdleLingerTicks <= 0`, which means the linger countdown
		// only starts at arrival — fixing the v0.3.45 "total time-budget"
		// model that expired LingerWander (120 ticks ≈ 2 sec) before a
		// 14-tile Wander walk (~7 sec at base speed) could complete.
		// Without this flag, idle shroomps with longer destinations cycled
		// through every idle activity their personality could roll: pick
		// Wander, walk 2 sec, lingerExpired triggers, pick Loiter,
		// walk 2 sec, lingerExpired triggers, ... visibly thrashing
		// and pegging FPS through the per-tick SelectTask cost. Now an
		// idle task is committed for the full walk + linger duration.
		public bool IdleArrived { get; set; } = false;

		// v0.5.2 — RTS-style chain order queue. Shift + right-click on a
		// destination tile appends the position here instead of replacing
		// CurrentTask; non-shift right-click clears the queue and replaces
		// CurrentTask (StarCraft / Warcraft pattern). BehaviorSystem.Tick
		// pops the head onto a fresh PlayerOrder when CurrentTask becomes
		// null AND no critical-need override fires. The list lives on the
		// shroomp so cross-thread access is the same contract as
		// CurrentTask / PathWaypoints (sim thread reads + mutates; main
		// thread appends via PostMainThreadCommand). Each entry is a
		// pixel-space Vector2 — same coordinate system as PlayerOrder.Target.
		// Future combat orders will need a richer QueuedOrder type
		// (TaskType + target + optional targetId); v0.5.2 ships with Move
		// only since combat is Phase 7.
		public List<Godot.Vector2> MoveOrderQueue { get; } = new();

		// v0.3.47 (Phase 4 sub-B) — RimWorld-style per-shroomp work
		// priorities. Keyed by work-category string (Doctor / Mine /
		// PlantCut / Cook / Hunt / Haul / Clean / Research / etc.).
		// Value 0 = off (shroomp will never do this work); 1 = highest,
		// 4 = lowest. SelectTask reads these to gate Tier 2 evaluation;
		// JobsPanel lets the player edit them. Defaults seeded per role
		// in WorkPriorityDefaults.cs.
		public Dictionary<string, byte> WorkPriorities { get; set; } = new();

		// v0.4.30 — multi-item carry inventory (RimWorld pickup-while-
		// hauling model, replacing the v0.4.2 single carry slot). When a
		// shroomp is hauling, they pick up items into Inventory until they
		// hit CarryingCapacity OR run out of nearby haulable items, then
		// deliver everything at once. Cuts pathfinding load (one round
		// trip per N items, not N round trips) and matches the player's
		// real-world expectation that a shroomp carrying 35 items returns
		// once with the lot, not 35 times with one each.
		//
		// Each item's Quantity counts toward the load (so a single
		// 50-stack of berries occupies 50 of the shroomp's capacity).
		// Worn equipment is NOT in this list — it lives in `Equipment`
		// per body slot and never counts toward the carry budget.
		// Unworn equipment in Inventory DOES count (each Item @ Quantity 1).
		public List<Sporeholm.Simulation.Items.Item> Inventory { get; set; } =
			new List<Sporeholm.Simulation.Items.Item>();

		// Sum of Quantity across the carry inventory. Used by the haul
		// system to decide "am I full?" and by the unit card carry bar.
		public int CurrentCarriedCount
		{
			get
			{
				int total = 0;
				for (int i = 0; i < Inventory.Count; i++) total += Inventory[i].Quantity;
				return total;
			}
		}

		// v0.4.30 — backward-compat shim for v0.4.2 → v0.4.29 callers that
		// only knew about a single CarriedItem slot. Reads return the
		// most-recently-added inventory entry (matches the v0.4.2
		// renderer contract: "show the item in the shroomp's hand").
		// Writes match the original single-slot semantics: setting null
		// clears the entire inventory (caller's intent was "drop the
		// carry"); setting non-null appends. Real multi-item code paths
		// (HaulSystem v0.4.30+) should use Inventory directly.
		public Sporeholm.Simulation.Items.Item? CarriedItem
		{
			get => Inventory.Count > 0 ? Inventory[Inventory.Count - 1] : null;
			set
			{
				if (value == null) Inventory.Clear();
				else Inventory.Add(value);
			}
		}

		// v0.4.30 — DF/RimWorld-style per-shroomp carrying capacity. Range
		// 5 (sprout) to 75 (peak adult with Brawny + low CompactStature).
		// Computed from life stage + personality + biological traits.
		// Worn equipment doesn't count; unworn items in Inventory do.
		// Recomputed on each read — cheap (≤25 dict lookups), no caching
		// to invalidate on trait/personality change.
		public int CarryingCapacity
		{
			get
			{
				int baseCap = LifeStage switch
				{
					LifeStage.Sprout     => 5,
					LifeStage.Juvenile   => 18,
					LifeStage.Adult      => 50,
					LifeStage.Elder      => 35,
					LifeStage.LastSeason => 18,
					_                    => 30,
				};

				// Personality modifiers — biggest pulls from canon Shroomp
				// archetypes. Brawny is the headline buff; Sleepyhead /
				// Worrywart / Greedy Gut visibly drag the cap down.
				int modPersonality = 0;
				if (Personality != null)
				{
					for (int i = 0; i < Personality.Count; i++)
					{
						modPersonality += Personality[i] switch
						{
							"Brawny"           => +20,
							"Perfectionist"    => +5,
							"Stoic"            => +5,
							"Sleepyhead"       => -10,
							"Worrywart"        => -10,
							"Greedy Gut"       => -5,
							"Glutton"          => -5,
							"Pessimist"        => -5,
							"Thrill-Seeker"    => -5,
							"Accident-Prone"   => -3,
							_                  => 0,
						};
					}
				}

				// Biological trait modifiers — scaled by penetrance.
				// CompactStature (smaller truffle-like body) is the dominant
				// negative; WispyFrame (built for speed not load) is a softer
				// pull. PerennialMycelium / CopperHemolymph / RapidMetabolism
				// don't move the carry budget.
				// v0.5.84t — trait names renamed from Miniaturization /
				// StatureAgility to mushroom-themed CompactStature /
				// WispyFrame. Pre-v0.5.84t saves migrate via
				// TraitRegistry.MigrateLegacyTraitNames so this still finds
				// the right penetrance values after load.
				float modBio = 0f;
				if (Traits != null)
				{
					if (Traits.TryGetValue(TraitRegistry.CompactStature, out float pMin))
						modBio -= pMin * 15f;
					if (Traits.TryGetValue(TraitRegistry.WispyFrame, out float pAgi))
						modBio -= pAgi * 5f;
				}

				int cap = baseCap + modPersonality + (int)System.Math.Round(modBio);
				return System.Math.Clamp(cap, 5, 75);
			}
		}

		// v0.4.4 — DF-style per-body-part equipment. Keyed by EquipSlot
		// (Head / Torso / LeftArm / RightArm / LeftHand / RightHand /
		// LeftLeg / RightLeg / LeftFoot / RightFoot). Hand slots hold
		// tools, weapons, or shields; the rest hold layered apparel.
		// Auto-equip in BehaviorSystem reads `Handedness` to pick the
		// dominant hand for incoming tools / weapons.
		public Dictionary<Sporeholm.Simulation.Items.EquipSlot,
			Sporeholm.Simulation.Items.Item> Equipment { get; set; } =
				new Dictionary<Sporeholm.Simulation.Items.EquipSlot,
					Sporeholm.Simulation.Items.Item>();

		// v0.4.4 — handedness trait. Rolled at creation (Birth /
		// scenario / wanderer-in). Determines which hand auto-equip
		// fills first; the off-hand stays free for shields + dual-
		// wielding once Phase 7 combat lands.
		public Handedness Handedness { get; set; } = Handedness.Right;

		// v0.5.63 — Cap-and-stem visual identity (shroomport.md §4.2).
		// Per-Shroomp variation rolled at creation by ShroompIdentity.Roll.
		// Read by ShroompColonyView.BakeShroompSprite to generate pixel art
		// — sprite cache keys on (CapShape, CapColour bucket, StemColour
		// bucket, Sex, Mood) for ~84 baked sprites at the bucketed level.
		// Defaults are placeholders; constructors set real values.
		public CapShape   CapShape    { get; set; } = CapShape.Round;
		public CapTexture CapTexture  { get; set; } = CapTexture.Smooth;
		public StemBuild  StemBuild   { get; set; } = StemBuild.Average;
		public Color      CapColour   { get; set; } = new(0.55f, 0.32f, 0.18f);   // russet
		public Color      StemColour  { get; set; } = new(0.93f, 0.86f, 0.72f);   // cream
		public Color      PorePadColour { get; set; } = new(0.92f, 0.85f, 0.60f); // pore-pad cream

		// v0.4.3 → v0.4.4 — convenience accessors mirroring the old
		// single-slot fields. Returns the item in the dominant hand
		// (for tools / weapons) or in the Torso/Head/Feet slot. Used
		// by the snapshot to preserve the v0.4.2 visual rendering
		// contract while the per-slot detail lands. Setters bounce
		// any displaced item back to the caller's responsibility —
		// callers using these helpers should also Add() the displaced
		// item to ColonyResources.Inventory.
		public Sporeholm.Simulation.Items.Item? EquippedTool
		{
			get
			{
				var dom = HandednessMeta.DominantHand(Handedness);
				var off = HandednessMeta.OffHand(Handedness);
				if (Equipment.TryGetValue(dom, out var d) && d.Kind == Sporeholm.Simulation.Items.ItemKind.Tool) return d;
				if (Equipment.TryGetValue(off, out var o) && o.Kind == Sporeholm.Simulation.Items.ItemKind.Tool) return o;
				return null;
			}
		}
		public Sporeholm.Simulation.Items.Item? EquippedWeapon
		{
			get
			{
				var dom = HandednessMeta.DominantHand(Handedness);
				var off = HandednessMeta.OffHand(Handedness);
				if (Equipment.TryGetValue(dom, out var d) && d.Kind == Sporeholm.Simulation.Items.ItemKind.Weapon) return d;
				if (Equipment.TryGetValue(off, out var o) && o.Kind == Sporeholm.Simulation.Items.ItemKind.Weapon) return o;
				return null;
			}
		}
		public Sporeholm.Simulation.Items.Item? EquippedApparel =>
			Equipment.TryGetValue(Sporeholm.Simulation.Items.EquipSlot.Torso, out var t) ? t : null;
	}
}
