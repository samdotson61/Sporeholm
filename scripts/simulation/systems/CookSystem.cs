using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
	// v0.5.22 (Phase 5E — rimport.md N4) — Cook work loop.
	// v0.6.2 (Phase 5.6 ship) — CookingTable preferred; Bonfire fallback at
	// 0.5× speed; Workbench no longer cooks (Cooking split off Crafting).
	//
	//   SelectCookTarget — find the nearest built CookingTable reachable
	//     from the shroomp; if none, fall back to the nearest Bonfire.
	//     Only fires when (a) a cook station exists, (b) the colony has
	//     at least one raw non-PreparedMeal Food item available, (c) the
	//     prospective Cook has Cooking skill ≥ 0 (anyone can cook badly).
	//
	//   Apply — Cook has arrived. Consume 4 raw Food stacks from the
	//     colony inventory (CookMeal recipe), produce a PreparedMeal
	//     with quality rolled from the cook's Cooking skill, drop it on
	//     the station tile so it joins the haul flow naturally. Bonfire
	//     work takes 2× as long as CookingTable work — the player feels
	//     the penalty in throughput.
	//
	// Original v0.5.22 simplifications still in force (Phase 5.5 bills
	// fill in the bill-queue model; specific recipes per Phase 6+):
	//   • Auto-cook fires whenever the conditions hold (no bill required).
	//     Phase 5.5 BillSystem provides the explicit bill queue for
	//     player-defined cooking jobs.
	//   • Single recipe — any raw Food cooks to PreparedMeal. Specific
	//     recipes (Mushroom Stew, Berry Tart) land when Phase 8 fleshes
	//     out the Cooking recipe catalogue.
	public static class CookSystem
	{
		// v0.6.2 (Phase 5.6) — Bonfire speed penalty when used as a cooking
		// fallback. 2.0× WorkTicks → 50% effective cook speed. Matches the
		// CookMeal/JuiceBerries RecipeDef.BonfireSpeedMul value.
		public const float BonfireSpeedMul = 2.0f;

		public static BehaviorTask? SelectCookTarget(Shroomp s, LocalMap? map, ColonyResources r)
		{
			if (map == null || r == null) return null;
			// Need at least one raw Food in colony inventory to consume.
			// PreparedMeal excluded so cooks don't endlessly re-cook output.
			if (!HasRawFoodAvailable(r)) return null;
			// v0.6.2 (Phase 5.6) — Prefer CookingTable. Fall back to Bonfire.
			// Workbench no longer cooks (it's the Crafting station now).
			var cookTile = FindNearestCookStation(s.SimPos, map);
			if (!cookTile.HasValue) return null;
			var (tx, ty) = cookTile.Value;
			var px = new Vector2(
				tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
				ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
			return new BehaviorTask(TaskType.Cook, px, 50f,
				interruptible: true,
				tileX: tx, tileY: ty);
		}

		// CookMeal recipe WorkTicks (matches RecipeRegistry.cs entry). The
		// auto-cook uses the same value so the throughput feels identical
		// whether the player set up a bill or relied on auto-cook.
		public const int CookMealWorkTicks = 240;

		public static void Apply(Shroomp s, BehaviorTask t, LocalMap? map, ColonyResources r)
		{
			if (map == null || r == null) return;
			// Verify cook station still present at target (could've been demolished).
			if (t.TargetTileX < 0 || t.TargetTileY < 0) return;
			var slot = map.GetStructure(t.TargetTileX, t.TargetTileY);
			// v0.6.2 (Phase 5.6) — accept CookingTable OR Bonfire. Reject
			// Workbench (legacy auto-cook target, no longer valid).
			if (slot.Type != StructureType.CookingTable && slot.Type != StructureType.Bonfire)
			{
				s.CurrentTask = null;
				return;
			}
			// v0.6.2 audit Fix 2 — per-tick work accumulation so the Bonfire
			// fallback actually feels slower than a CookingTable. Pre-fix
			// the auto-cook produced the meal in a single Apply call,
			// bypassing the BonfireSpeedMul × 2.0 penalty BillSystem
			// already enforces for explicit cooking bills.
			//
			// Effective WorkTicks: 240 at CookingTable, 480 at Bonfire.
			// Per-tick advance scales with Cooking skill (lvl 0 = 0.5×,
			// lvl 20 = 1.5×) — matches BillSystem's curve so auto-cook
			// and bill-cook have identical pacing.
			int cookingSkill = s.Skills.TryGetValue("Cooking", out int v) ? v : 0;
			float speedMul = 0.5f + cookingSkill * 0.05f;
			int advance = System.Math.Max(1, (int)(1 * speedMul + 0.5f));
			s.TaskProgressTicks += advance;
			s.TaskDidWork = true;
			int effectiveWorkTicks = CookMealWorkTicks;
			if (slot.Type == StructureType.Bonfire)
				effectiveWorkTicks = (int)(CookMealWorkTicks * BonfireSpeedMul);
			if (s.TaskProgressTicks < effectiveWorkTicks) return;

			// v0.5.84t — auto-cook tracks the v0.5.84t CookMeal recipe:
			// 4 raw Food of any family → 1 PreparedMeal. RawFood = any Food
			// stack that isn't already a PreparedMeal. Re-check at completion
			// so an interrupted cook (food consumed by another task while
			// progress accumulated) doesn't crash — just drops the task.
			int totalRaw = r.Inventory.TotalByKind(ItemKind.Food)
			             - r.Inventory.TotalBySubType(ItemKind.Food, "PreparedMeal");
			if (totalRaw < 4) { s.CurrentTask = null; s.TaskProgressTicks = 0; return; }
			// FindFirst(ItemKind, params excludeSubTypes) returns the first
			// stack matching the kind with NONE of the listed subtypes.
			// Here: any Food that isn't a PreparedMeal — i.e. raw food.
			var raw = r.Inventory.FindFirst(ItemKind.Food, "PreparedMeal");
			if (raw == null) { s.CurrentTask = null; s.TaskProgressTicks = 0; return; }
			string sourceMat = raw.Material.SubType;
			// Consume 4 of any non-PreparedMeal Food.
			int needed = 4;
			while (needed > 0)
			{
				var stack = r.Inventory.FindFirst(ItemKind.Food, "PreparedMeal");
				if (stack == null) break;
				int take = System.Math.Min(needed, stack.Quantity);
				r.Inventory.Consume(stack, take);
				needed -= take;
			}
			// Produce PreparedMeal. Quality from cooking skill.
			var rng = new System.Random();
			var meal = ItemFactory.Create(
				ItemKind.Food, "PreparedMeal",
				new MaterialKey("Plant", sourceMat),
				rng,
				0,
				skillLevel: cookingSkill,
				quantity: 1);
			meal.TilePos = new Vector2(
				t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
				t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
			map.DropItem(meal);
			// v0.6.2 (Phase 5.6 ship) — cooking awards Cooking XP (its own skill again).
			Sporeholm.Simulation.SkillRegistry.GainXp(s, "Cooking", 80f);
			// Reset progress + clear task so SelectTask can fire the next
			// auto-cook (or another work task) on the next tick.
			s.TaskProgressTicks = 0;
			s.CurrentTask = null;
		}

		private static bool HasRawFoodAvailable(ColonyResources r)
		{
			// v0.5.84t — auto-cook needs 4 raw Food (any family, excluding
			// already-prepared meals) to match the CookMeal recipe.
			int rawTotal = r.Inventory.TotalByKind(ItemKind.Food)
			             - r.Inventory.TotalBySubType(ItemKind.Food, "PreparedMeal");
			return rawTotal >= 4;
		}

		// v0.6.2 (Phase 5.6) — find the nearest valid cook station. Prefers
		// any built CookingTable; falls back to the nearest built Bonfire if
		// no CookingTable exists on the map. Walks the structure grid
		// linearly; acceptable at typical scales. A per-station HashSet can
		// land later if kitchen-heavy bases need it.
		private static (int X, int Y)? FindNearestCookStation(Vector2 fromPx, LocalMap map)
		{
			int fx = (int)(fromPx.X / LocalMap.TileSize);
			int fy = (int)(fromPx.Y / LocalMap.TileSize);
			int bestCookingTable = int.MaxValue;
			(int X, int Y)? cookingTableWinner = null;
			int bestBonfire = int.MaxValue;
			(int X, int Y)? bonfireWinner = null;
			for (int y = 0; y < map.Height; y++)
			for (int x = 0; x < map.Width;  x++)
			{
				var t = map.GetStructure(x, y).Type;
				if (t != StructureType.CookingTable && t != StructureType.Bonfire) continue;
				int dx = x - fx, dy = y - fy;
				int d = dx * dx + dy * dy;
				if (t == StructureType.CookingTable && d < bestCookingTable)
				{
					bestCookingTable = d;
					cookingTableWinner = (x, y);
				}
				else if (t == StructureType.Bonfire && d < bestBonfire)
				{
					bestBonfire = d;
					bonfireWinner = (x, y);
				}
			}
			// Cooking Table wins regardless of distance — the player paid for
			// the proper station, the cook should use it even if there's a
			// closer Bonfire. (Future polish: also consider walk-distance
			// reachability, not just squared straight-line.)
			return cookingTableWinner ?? bonfireWinner;
		}
	}
}
