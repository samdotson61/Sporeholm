using System.Collections.Generic;

namespace Sporeholm.Simulation
{
	// v0.3.43 — Thoughts (RimWorld-style temporal mood entries).
	//
	// Where mood comes from in v0.3.42 was a pure linear blend of the five
	// needs plus the fixed personality modifier. That makes the colony feel
	// mechanical: a shroomp with full needs is always Inspired, a starved
	// shroomp is always Distressed, and nothing in between leaves a mark.
	//
	// Thoughts add an *event memory* layer on top. When a shroomp completes a
	// task, witnesses something, or has a social interaction, the event
	// emits a Thought — a small signed mood offset that decays over a fixed
	// number of ticks. While the thought is live, MoodSystem folds its
	// offset into MoodScore so the shroomp's mood reflects what they've been
	// doing, not just whether their needs are topped up.
	//
	// Catalogue lives in ThoughtRegistry; per-shroomp state lives in
	// Shroomp.Thoughts as a small ring of active entries. Capacity is fixed
	// so the per-tick walk stays O(1) per shroomp even at the 1000-shroomp
	// target colony size; the oldest entry is overwritten when a new
	// thought needs a slot.
	public enum ThoughtCategory : byte
	{
		Meal, Sleep, Work, Social, Aesthetic, Trauma, Magic, Comfort, Idle,
	}

	public readonly record struct ThoughtDef(
		string          Key,
		string          Headline,        // displayed in unit-card thoughts list
		float           MoodOffset,      // signed; summed into MoodScore while active
		int             DurationTicks,   // ticks (60/sec at 1×) until the thought expires
		ThoughtCategory Category);

	// A live thought instance on a shroomp. The key indexes ThoughtRegistry
	// to recover the headline / category / max duration; only the remaining
	// TTL and a context string (e.g. who or what caused it) live per-shroomp.
	public struct Thought
	{
		public string Key;
		public int    TicksRemaining;
		public float  MoodOffset;        // copied from def so we don't dict-lookup per tick
		public string Context;           // optional: shroomp name, item, location

		public bool IsActive => TicksRemaining > 0 && !string.IsNullOrEmpty(Key);
	}

	public static class ThoughtRegistry
	{
		// Tick counts assume 60 ticks / real second at 1× speed, which is
		// also one in-game minute. 600 ticks ≈ 10 minutes of in-game time.
		public static readonly ThoughtDef[] All =
		{
			// ── Meals ──────────────────────────────────────────────────────
			// v0.4.61 (E2 from rimport.md) — TastyMeal reserved for Phase 5
			// Cook task output. Raw eating (capberry, mushroom, etc.) now
			// emits AteSimple (+1 mood) instead. Without this, every raw
			// berry triggered a "Had a tasty meal" thought, which inflated
			// baseline mood and meant Cook had nothing aspirational to add.
			new("TastyMeal",    "Had a tasty meal.",            +4f,   900, ThoughtCategory.Meal),
			new("AteSimple",    "Ate something simple.",        +1f,   600, ThoughtCategory.Meal),
			new("AteFavorite",  "Ate a favourite food.",        +8f,  1500, ThoughtCategory.Meal),
			new("AteDisliked",  "Forced down something gross.", -5f,   900, ThoughtCategory.Meal),
			new("AteHungry",    "Finally ate after going hungry.",+2f, 600, ThoughtCategory.Meal),
			new("Famished",     "Famished.",                    -6f,   300, ThoughtCategory.Meal),
			// v0.5.37 — RimWorld-parity table-eating mood. Emitted when a
			// shroomp eats with no table within 1 tile (8-neighbourhood).
			new("AteWithoutTable","Ate without a table.",       -3f,   600, ThoughtCategory.Meal),
			// v0.5.68 — emergency-eat thoughts. RimWorld parity: AteRottenFood
			// (-7 mood ~1 day), AteCorpse (-15 mood ~3 days, AteHumanlikeCorpse).
			// Shroomps reach for these only when starving (Nutrition < 25), so
			// the mood hit comes packaged with the relief of not dying — net
			// effect is "we lived but everyone's traumatised."
			new("AteSpoiled",   "Choked down rotten food.",     -7f,   900, ThoughtCategory.Meal),
			new("AteCorpse",    "Ate a corpse to survive.",    -15f,  3600, ThoughtCategory.Trauma),

			// ── Sleep ──────────────────────────────────────────────────────
			new("SleptOnGround","Slept on the bare ground.",    -4f,  1200, ThoughtCategory.Sleep),
			new("WellRested",   "Woke up refreshed.",           +3f,   900, ThoughtCategory.Sleep),

			// ── Health / injury (v0.5.79) ──────────────────────────────────
			new("Downed",       "Collapsed from injury.",      -10f,  1800, ThoughtCategory.Trauma),
			new("StoodBackUp",  "Pulled through and stood back up.", +4f,  900, ThoughtCategory.Trauma),

			// ── Work / accomplishment ──────────────────────────────────────
			new("WorkedFavorite","Did work I love.",            +4f,  1200, ThoughtCategory.Work),
			new("WorkedDisliked","Did work I hate.",            -3f,   900, ThoughtCategory.Work),
			new("Accomplished", "Felt accomplished.",           +3f,  1500, ThoughtCategory.Work),
			new("TaskAbandoned","Gave up on a task.",           -3f,   600, ThoughtCategory.Work),

			// ── Social ─────────────────────────────────────────────────────
			new("NiceChat",     "Had a nice chat.",             +3f,  1500, ThoughtCategory.Social),
			new("ChatWithFriend","Chatted with a friend.",      +6f,  2400, ThoughtCategory.Social),
			new("ChatWithEnemy","Forced to talk to someone I dislike.",-4f,1200,ThoughtCategory.Social),
			new("Alone",        "Spent the day alone.",         -2f,  1800, ThoughtCategory.Social),
			new("WitnessedDeath","Saw a friend die.",          -12f, 14400, ThoughtCategory.Trauma),
			// v0.5.60 — RimWorld-parity interactions-as-events. These fire
			// from InteractionTracker (proximity + MTB roll), independent of
			// the Converse task. Smaller mood deltas than the dedicated
			// NiceChat / ChatWithFriend (those still fire from Converse) —
			// these are micro-interactions that happen while shroomps are doing
			// other things.
			new("Chitchat",     "Exchanged a few words.",       +1f,   900, ThoughtCategory.Social),
			new("KindWords",    "Heard some kind words.",       +3f,  1500, ThoughtCategory.Social),
			new("Slight",       "Got a passive-aggressive jab.",-2f,  1200, ThoughtCategory.Social),
			new("DeepTalk",     "Had a deep conversation.",     +5f,  2700, ThoughtCategory.Social),

			// ── Magic ──────────────────────────────────────────────────────
			new("Attuned",      "Felt the magic flow.",         +5f,  1500, ThoughtCategory.Magic),

			// ── Comfort / safety / aesthetic ───────────────────────────────
			new("FoundSafety",  "Felt safe again.",             +2f,   900, ThoughtCategory.Comfort),
			new("Frightened",   "Got a fright.",                -3f,   600, ThoughtCategory.Trauma),

			// ── Idle pursuits ──────────────────────────────────────────────
			new("Daydreamed",   "Spent a moment daydreaming.",  +1f,   600, ThoughtCategory.Idle),
			new("Pondered",     "Pondered the world.",          +2f,   900, ThoughtCategory.Idle),
			new("VisitedSpot",  "Visited a favourite spot.",    +3f,  1200, ThoughtCategory.Idle),
			new("Wandered",     "Stretched the legs.",          +1f,   400, ThoughtCategory.Idle),

			// v0.5.14 (Phase 5C — rimport.md N18) — buried-treasure thought.
			// Fired by BehaviorSystem.GatherMaterial when an excavation
			// uncovers a tile flagged with HasBuriedTreasure. Higher mood
			// boost than VisitedSpot because the discovery is rare (~0-2
			// per map) and gameplay-meaningful (drops a Trinket).
			new("FoundTreasure","Uncovered ancient treasure!",  +8f,  2400, ThoughtCategory.Comfort),

			// v0.5.23 (Phase 5F — rimport.md G5) — Beauty thoughts. Fired
			// periodically when a shroomp is in a room with high / low
			// beauty score. RimWorld parity (Beauty: Pretty / Beauty: Ugly).
			new("BeautyPretty", "Felt at home in a beautiful space.", +3f, 900, ThoughtCategory.Comfort),
			new("BeautyUgly",   "Felt grim about ugly surroundings.",  -3f, 900, ThoughtCategory.Trauma),

			// v0.5.84t (Phase 5H — room-type-aware mood) — Bedroom comfort.
			// Fires on wake from a Bed inside a room that RoomDetector
			// classified as Bedroom (auto-inferred: room contains at least
			// one Bed). RimWorld parity ("Slept in bedroom"). Stacks with
			// WellRested.
			new("SleptInBedroom", "Slept comfortably in a private bedroom.", +2f, 1200, ThoughtCategory.Comfort),
		};

		private static readonly Dictionary<string, ThoughtDef> _byKey;

		static ThoughtRegistry()
		{
			_byKey = new Dictionary<string, ThoughtDef>(All.Length);
			foreach (var d in All) _byKey[d.Key] = d;
		}

		public static bool TryGet(string key, out ThoughtDef def) => _byKey.TryGetValue(key, out def);

		// Adds a thought to the shroomp's ring, overwriting the slot with the
		// smallest TicksRemaining (the oldest entry). If the key is already
		// present, refresh its TTL instead of double-stacking — RimWorld's
		// "same thought max 1 stack" rule keeps the per-shroomp state bounded.
		public static void Add(Shroomp s, string key, string context = "")
		{
			if (!_byKey.TryGetValue(key, out var def)) return;
			if (s.Thoughts == null) s.Thoughts = new Thought[ThoughtCapacity];

			int oldestIdx = 0;
			int oldestTtl = int.MaxValue;
			for (int i = 0; i < s.Thoughts.Length; i++)
			{
				if (s.Thoughts[i].Key == key)
				{
					// Refresh existing thought rather than duplicating.
					s.Thoughts[i].TicksRemaining = def.DurationTicks;
					s.Thoughts[i].MoodOffset     = def.MoodOffset;
					s.Thoughts[i].Context        = context;
					s.ThoughtsDirty = true;
					return;
				}
				int ttl = s.Thoughts[i].TicksRemaining;
				if (ttl < oldestTtl) { oldestTtl = ttl; oldestIdx = i; }
			}

			s.Thoughts[oldestIdx] = new Thought
			{
				Key            = key,
				TicksRemaining = def.DurationTicks,
				MoodOffset     = def.MoodOffset,
				Context        = context,
			};
			s.ThoughtsDirty = true;
		}

		// Ring capacity — 8 covers the realistic "last few notable events"
		// window without requiring a List allocation per shroomp.
		public const int ThoughtCapacity = 8;
	}
}
