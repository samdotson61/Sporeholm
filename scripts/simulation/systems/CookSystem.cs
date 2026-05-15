using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

namespace SmurfulationC.Simulation.Systems
{
	// v0.4.0 (Phase-5 stub) — Cook task scaffolding.
	//
	// Full Cook work loop per the Phase 4 spec (food taxonomy →
	// Raw / Prepared / Preserved) and Phase 5 Kitchen building:
	//   1. Player places a Kitchen building (Phase 5) and queues a
	//      meal recipe (e.g. "Mushroom stew = 1 Large Mushroom +
	//      1 Herb Cluster" produces a 25-nutrition prepared meal
	//      that emits a +5 "Ate fine meal" thought).
	//   2. A Cook-prio smurf hauls the required raw items to the
	//      Kitchen (via Haul, also stubbed) and works the recipe
	//      for N sim ticks scaled by their Cook skill.
	//   3. On completion the raw items are consumed and a new
	//      prepared item is added to the colony inventory; the
	//      Cook gains Cook skill experience.
	//
	// Sub-A landed the food item taxonomy hook (FoodFromVegetation /
	// ItemRegistry's BaseNutrition column). The cooked-meal SubType
	// entries ("MushroomStew", "BerryTart", "HerbTea") will be
	// registered in `ItemRegistry` at the same time Phase 5's
	// Kitchen building lands.
	public static class CookSystem
	{
		// Returns the next Cook target for this smurf, or null when
		// no Kitchen has an active recipe + raw ingredients available.
		//
		// Phase 5 will fill this with:
		//   - Iterate Kitchen buildings owned by the colony.
		//   - For each, check the recipe queue + the local inventory
		//     for required raw items.
		//   - Pick the highest-priority queued recipe.
		public static BehaviorTask? SelectCookTarget(Smurf s, LocalMap? map, ColonyResources r)
		{
			return null;
		}

		// Applies the cook completion. Phase 5 will:
		//   - Consume the recipe's raw ingredients from the Kitchen's
		//     adjacent stockpile.
		//   - Add the resulting prepared item with quality rolled
		//     from the cook's Cook skill (RollQuality(skillLevel)).
		//   - Award Cook skill experience.
		//   - Emit a "Cooked a meal" Accomplished thought on the cook.
		public static void Apply(Smurf s, BehaviorTask t, LocalMap? map, ColonyResources r)
		{
			// No-op — see class comment.
		}
	}
}
