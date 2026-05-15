using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SmurfulationC.Simulation
{
	// Immutable point-in-time snapshot pushed to the main thread after each tick.
	// The main/render thread reads only from these — never from Smurf directly.
	// Only alive smurfs are included; dead smurfs are removed before enqueueing.
	//
	// v0.3.36 — added `SmurfsByName` index so the unit card, roster, and
	// GameController.OnTick can find a snapshot in O(1) instead of LINQ
	// `FirstOrDefault` (linear scan, O(N) per lookup). With 1000 smurfs in
	// the planned target colony size that's a 1000× per-lookup speedup.
    public sealed class SimulationSnapshot
    {
        public SimulationDate Date { get; }
        public IReadOnlyList<SmurfSnapshot> Smurfs { get; }
        public IReadOnlyDictionary<string, SmurfSnapshot> SmurfsByName { get; }

        public SimulationSnapshot(SimulationDate date, IEnumerable<Smurf> smurfs)
        {
            Date = date;
            var list = new List<SmurfSnapshot>();
            var dict = new Dictionary<string, SmurfSnapshot>();
            foreach (var s in smurfs)
            {
                if (!s.IsAlive) continue;
                var snap = new SmurfSnapshot(s);
                list.Add(snap);
                dict[s.Name] = snap;
            }
            Smurfs       = list;
            SmurfsByName = dict;
        }

        public SimulationSnapshot(SimulationDate date, IReadOnlyList<SmurfSnapshot> smurfs)
        {
            Date   = date;
            Smurfs = smurfs;
            var dict = new Dictionary<string, SmurfSnapshot>(smurfs.Count);
            foreach (var s in smurfs) dict[s.Name] = s;
            SmurfsByName = dict;
        }
    }

    // v0.3.36 — converted from `sealed record` (reference type) to
    // `readonly record struct` (value type). The previous constructor did
    // four defensive `new Dictionary(s.X)` / `new List(s.Y)` copies per
    // smurf per snapshot — at the planned 1000-smurf colony size that
    // would be 240,000 heap allocations per second from snapshot creation
    // alone. As a struct, the snapshot lives inline in `SimulationSnapshot.Smurfs`
    // and the collections are passed *by reference*. Safe because:
    //   • Traits / Skills / Personality are write-once at smurf creation /
    //     load — sim thread never adds or removes entries after that.
    //   • BodyParts has a fixed key set (from BodyPartRegistry) populated
    //     at creation. Values are mutated by NeedsSystem.HealTick but never
	//     change the dictionary's internal structure. Reads from main
	//     thread might see slightly stale values; same trade-off as the
	//     atomically-readable float fields like Nutrition.
	public readonly record struct SmurfSnapshot(
		Guid                              Id,
		string                            Name,
		int                               AgeInYears,
		Sex                               Sex,
		string                            Role,
		float                             Nutrition,
		float                             Rest,
		float                             Social,
		float                             MagicResonance,
		float                             Safety,
		float                             MoodScore,
		float                             MoodRaw,
		float                             MoodModifier,
		MoodState                         MoodState,
		LifeStage                         LifeStage,
		bool                              IsAlive,
		int                               BirthdayDayOfYear,
		CauseOfDeath?                     CauseOfDeath,
		IReadOnlyDictionary<string, float> Traits,
		IReadOnlyDictionary<string, int>   Skills,
		IReadOnlyList<string>              Personality,
		IReadOnlyDictionary<string, float> BodyParts,
		// Phase 3 — Behavior System
		Vector2                            SimPos,
		Vector2                            SimTarget,
		TaskType                           CurrentTask,
		// v0.3.24 — combat stub (Phase 8 fill-in). Non-null name = smurf is
		// in combat with that target; visual layer draws a sword icon.
		string?                            CombatTargetName,
		// v0.4.2 — carry-visual + equipment overlay payload. Snapshot
		// carries lightweight (Kind, SubType, MaterialFamily) tuples
		// because the renderer only needs to pick an icon — full Item
		// references would tie the visual to sim-thread state.
		string?                            CarriedKind,
		string?                            CarriedSubType,
		string?                            CarriedMaterialFamily,
		// v0.4.4 — per-body-part equipment payload. Dict keyed by
		// EquipSlot string with (Kind, SubType, MaterialFamily,
		// MaterialSubType) tuple value so the unit card + renderer can
		// look up each slot's contents without referencing Items
		// directly. Plus the smurf's Handedness so renderer knows
		// which hand glyph to draw a held tool in.
		IReadOnlyDictionary<string, (string Kind, string SubType, string MaterialFamily, string MaterialSubType)> Equipment,
		Handedness                         Handedness,
		// v0.4.30 — true while the smurf is lying down to let another smurf
		// climb over them (DF-style yield). Renderer squashes the sprite
		// vertically as visual feedback so the player can see who's prone.
		bool                               IsYielding,
		// v0.4.30 — multi-item carry. CarryingCapacity is computed from
		// life stage + traits (5–75); CurrentCarriedCount is the sum of
		// Quantity across the smurf's Inventory. Unit card draws a bar
		// from the two; HaulSystem caps pickups against the same.
		int                                CarryingCapacity,
		int                                CurrentCarriedCount,
		// v0.4.30 — full inventory display for the unit card. Lightweight
		// tuples (Kind, SubType, Material.Family, Quantity) so the UI can
		// list every stack without holding a sim-thread Item reference.
		// Empty list when carrying nothing.
		IReadOnlyList<(string Kind, string SubType, string MaterialFamily, int Quantity)> InventoryItems,
		// v0.4.31 — recent thoughts (RimWorld-style). Each entry pre-resolves
		// the registry headline so the UI can render without a registry
		// lookup. Sorted most-recent-first (largest TicksRemaining first
		// approximates "most recently added or most slowly decaying"). Empty
		// when nothing has affected the smurf's mood lately. MoodFromThoughts
		// is the live sum (clamped ±50) — surfaces the "thoughts contributed
		// +X to mood" line in the breakdown.
		IReadOnlyList<(string Key, string Headline, float MoodOffset, int TicksRemaining, string Context)> Thoughts,
		float                              MoodFromThoughts,
		// v0.5.2 — RTS chain order queue snapshot. Pixel-space targets the
		// player shift+right-clicked while this smurf was selected. The
		// per-tick render reads this for the OrderQueueOverlay (small
		// dots + connecting line for the selected smurf only). Empty when
		// no chained orders are pending.
		IReadOnlyList<Godot.Vector2>       MoveOrderQueue,
		// v0.5.5 — partner name when the smurf is mid-conversation
		// (CurrentTask is Converse and TargetId is set). Renderer +
		// unit-card surface "Talking to X" so the player can see the
		// social interaction without inferring it from positions. Null
		// when not conversing.
		string?                            ChatPartnerName,
		// v0.5.5 — remaining hops in a multi-leg "take a walk". Zero
		// when the smurf isn't wandering or is on the final leg. Used
		// by the unit card to surface "Wandering (3 of 4)" so the
		// player can tell long walks apart from short hops.
		int                                WanderHopsRemaining)
	{
		public SmurfSnapshot(Smurf s) : this(
			s.Id, s.Name, s.AgeInYears, s.Sex, s.Role,
			s.Nutrition, s.Rest, s.Social, s.MagicResonance, s.Safety,
			s.MoodScore, s.MoodRaw, s.MoodModifier, s.MoodState, s.LifeStage, s.IsAlive,
			s.BirthdayDayOfYear, s.CauseOfDeath,
			// v0.3.36 — share by reference. See SimulationSnapshot doc above.
			s.Traits, s.Skills, s.Personality, s.BodyParts,
			s.SimPos, s.SimTarget,
			s.CurrentTask?.Type ?? TaskType.None,
			s.CombatTargetName,
			s.CarriedItem?.Kind.ToString(),
			s.CarriedItem?.SubType,
			s.CarriedItem?.Material.Family,
			SnapshotEquipment(s),
			s.Handedness,
			s.YieldingTicks > 0,
			s.CarryingCapacity,
			s.CurrentCarriedCount,
			SnapshotInventory(s),
			SnapshotThoughts(s),
			s.MoodFromThoughts,
			SnapshotMoveOrderQueue(s),
			// v0.5.5 — chat partner name (only when actively conversing).
			s.CurrentTask is { Type: TaskType.Converse } ct ? ct.TargetId : null,
			s.WanderHopsRemaining) { }

		// v0.5.2 — copy the chain-order queue into a snapshot list. Sim thread
		// is the sole writer (via PostMainThreadCommand on append + the
		// BehaviorSystem queue-consumption pop), so a defensive copy here
		// is safe against concurrent UI reads. Empty queue returns the
		// pre-allocated empty array, no allocation in the common case.
		private static readonly IReadOnlyList<Godot.Vector2> _emptyMoveQueue =
			System.Array.Empty<Godot.Vector2>();
		private static IReadOnlyList<Godot.Vector2> SnapshotMoveOrderQueue(Smurf s)
		{
			if (s.MoveOrderQueue == null || s.MoveOrderQueue.Count == 0) return _emptyMoveQueue;
			var copy = new Godot.Vector2[s.MoveOrderQueue.Count];
			for (int i = 0; i < copy.Length; i++) copy[i] = s.MoveOrderQueue[i];
			return copy;
		}

		private static readonly IReadOnlyList<(string, string, float, int, string)> _emptyThoughts =
			System.Array.Empty<(string, string, float, int, string)>();

		private static IReadOnlyList<(string, string, float, int, string)> SnapshotThoughts(Smurf s)
		{
			if (s.Thoughts == null) return _emptyThoughts;
			var list = new List<(string, string, float, int, string)>(s.Thoughts.Length);
			for (int i = 0; i < s.Thoughts.Length; i++)
			{
				var t = s.Thoughts[i];
				if (!t.IsActive) continue;
				string headline = ThoughtRegistry.TryGet(t.Key, out var def) ? def.Headline : t.Key;
				list.Add((t.Key, headline, t.MoodOffset, t.TicksRemaining, t.Context ?? ""));
			}
			// Sort by TicksRemaining descending so the longest-lived
			// (typically most-recently-added or most-impactful) thoughts
			// land at the top of the unit-card list.
			list.Sort((a, b) => b.Item4.CompareTo(a.Item4));
			return list;
		}

		private static readonly IReadOnlyList<(string, string, string, int)> _emptyInv =
			System.Array.Empty<(string, string, string, int)>();

		private static IReadOnlyList<(string, string, string, int)> SnapshotInventory(Smurf s)
		{
			if (s.Inventory == null || s.Inventory.Count == 0) return _emptyInv;
			var list = new List<(string, string, string, int)>(s.Inventory.Count);
			for (int i = 0; i < s.Inventory.Count; i++)
			{
				var it = s.Inventory[i];
				list.Add((it.Kind.ToString(), it.SubType, it.Material.Family, it.Quantity));
			}
			return list;
		}

		private static IReadOnlyDictionary<string, (string, string, string, string)> SnapshotEquipment(Smurf s)
		{
			if (s.Equipment == null || s.Equipment.Count == 0)
				return _emptyEquip;
			var dict = new Dictionary<string, (string, string, string, string)>(s.Equipment.Count);
			foreach (var (slot, item) in s.Equipment)
			{
				dict[slot.ToString()] = (
					item.Kind.ToString(),
					item.SubType,
					item.Material.Family,
					item.Material.SubType);
			}
			return dict;
		}

		private static readonly Dictionary<string, (string, string, string, string)> _emptyEquip = new();

		// v0.4.4 — legacy accessor compatibility. Old code that read
		// EquippedToolSubType / EquippedWeaponSubType / EquippedApparelSubType
		// keeps working — these properties resolve from the new Equipment
		// dict using the dominant-hand lookup for tools / weapons and the
		// Torso slot for apparel.
		public string? EquippedToolSubType
		{
			get
			{
				var dom = HandednessMeta.DominantHand(Handedness).ToString();
				var off = HandednessMeta.OffHand(Handedness).ToString();
				if (Equipment.TryGetValue(dom, out var d) && d.Kind == "Tool") return d.SubType;
				if (Equipment.TryGetValue(off, out var o) && o.Kind == "Tool") return o.SubType;
				return null;
			}
		}
		public string? EquippedWeaponSubType
		{
			get
			{
				var dom = HandednessMeta.DominantHand(Handedness).ToString();
				var off = HandednessMeta.OffHand(Handedness).ToString();
				if (Equipment.TryGetValue(dom, out var d) && d.Kind == "Weapon") return d.SubType;
				if (Equipment.TryGetValue(off, out var o) && o.Kind == "Weapon") return o.SubType;
				return null;
			}
		}
		public string? EquippedApparelSubType =>
			Equipment.TryGetValue("Torso", out var t) ? t.SubType : null;
	}
}
