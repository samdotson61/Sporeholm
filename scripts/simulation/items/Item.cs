using System;
using Godot;

namespace Sporeholm.Simulation.Items
{
	// v0.3.46 (Phase 4 core) — concrete item instance / stack. Lives on
	// the sim thread; never reference-shared with the main thread (the
	// snapshot path copies item summaries by value into the resource
	// panel data structures).
	//
	// Stacking model: for stackable kinds (Food / Material / Magic /
	// TradeGood) identical (Kind, SubType, Material, Quality, State)
	// instances merge into a single Item with Quantity > 1. AvgCondition
	// and AvgBirthTick are weighted by the merged-in quantity so the
	// numbers stay representative — RimWorld's approach. Non-stackable
	// kinds (Tool / Weapon / Apparel / Furniture / Trinket) keep
	// Quantity = 1 forever because Condition diverges with use.
	//
	// OwnerShroompId / TilePos are reserved hooks for the Phase 5 hauling
	// + stockpile work. Today every item lives in the colony-global
	// Inventory with OwnerShroompId = null and TilePos = null.
	public sealed class Item
	{
		public Guid Id              { get; init; } = Guid.NewGuid();
		public ItemKind Kind        { get; init; }
		public string SubType       { get; init; } = "";
		public MaterialKey Material { get; set; }
		public Quality Quality      { get; set; } = Quality.Normal;
		public ItemState State      { get; set; } = ItemState.Fresh;

		// Average condition of items in this stack (or this single instance
		// for non-stackable kinds). Bounded by DurabilityCap. Falls over
		// time via ItemDeteriorationSystem; for tools it would also fall
		// per-use once Phase 4 hooks that up.
		public float AvgCondition   { get; set; } = 100f;
		public float DurabilityCap  { get; set; } = 100f;

		// SimulationCore.GlobalTick at item creation, weighted by quantity
		// when stacks merge. Used by the deterioration system to compute
		// "age" without a separate field — Age = GlobalTick - AvgBirthTick.
		public long  AvgBirthTick   { get; set; } = 0;

		// Stack count for stackable kinds; always 1 for non-stackable.
		public int   Quantity       { get; set; } = 1;

		public Guid?    OwnerShroompId { get; set; }
		public Vector2? TilePos      { get; set; }

		// v0.4.33 — biographical sidecar populated for ItemKind.Corpse and
		// null for everything else. Carries the dead shroomp's name / role /
		// cause-of-death / etc. so the hover overlay can show a proper
		// RimWorld-style obituary line over the corpse tile while the
		// body itself decays through the normal AvgCondition path.
		public CorpseData? CorpseInfo { get; set; }

		// v0.5.0 (Phase 5A — rimport N5) — RimWorld-style universal forbid
		// flag. Forbidden items are skipped by every haul / eat / equip
		// path, but they still occupy their tile and remain visible.
		// Player can right-click a forbidden item and toggle Allow to
		// make it haulable again.
		//
		// Auto-forbid policies (applied at drop time, see LocalMap.DropItem):
		//   • Corpses spawned outside the colony's home area auto-forbid
		//     (RimWorld default — colonists don't haul stranger corpses
		//     unless the player explicitly allows them).
		//   • Items dropped via PlayerOrder pickup don't auto-forbid (the
		//     player intentionally moved them).
		//   • Bones from corpse decay don't auto-forbid (already inside
		//     the colony's frame of reference once the body decayed).
		//
		// Forbid is per-item-instance, not per-tile. Five berries on one
		// tile can be individually forbidden.
		public bool IsForbidden { get; set; } = false;

		// Convenience: same-stack-eligibility check. Two items can merge
		// when they share (Kind, SubType, Material, Quality, State) AND
		// both belong to a stackable kind. Condition + age get averaged
		// rather than compared because forcing exact equality on float
		// condition would never merge anything.
		public bool CanStackWith(Item other)
		{
			if (other == null) return false;
			if (!ItemKindMeta.IsStackable(Kind)) return false;
			return Kind == other.Kind
				&& SubType  == other.SubType
				&& Material == other.Material
				&& Quality  == other.Quality
				&& State    == other.State;
		}

		// Folds `other` into this stack (sum quantities, average condition
		// + birth tick weighted by old/new quantity). Caller is responsible
		// for removing `other` from its container afterwards.
		public void Absorb(Item other)
		{
			float w1 = Quantity, w2 = other.Quantity;
			float wt = w1 + w2;
			if (wt > 0f)
			{
				AvgCondition  = (AvgCondition * w1 + other.AvgCondition * w2) / wt;
				AvgBirthTick  = (long)((AvgBirthTick * w1 + other.AvgBirthTick * w2) / wt);
			}
			Quantity += other.Quantity;
		}
	}
}
