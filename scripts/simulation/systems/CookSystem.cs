using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
	// v0.5.22 (Phase 5E — rimport.md N4) — Cook work loop.
	//
	// Pre-v0.5.22 this was a Phase 5 stub returning null. v0.5.22 wires
	// the minimum-viable "Crafter cooks at workbench" loop:
	//
	//   SelectCookTarget — find the nearest built Workbench reachable
	//     from the shroomp, return a Cook task targeting it. Only fires
	//     when (a) a Workbench exists, (b) the colony has at least one
	//     raw Plant-family Food item available, and (c) the prospective
	//     Cook has Cooking skill > 0 (anyone can cook badly, but Crafter
	//     role + cooking skill produces faster + higher-quality output).
	//
	//   Apply — Crafter has arrived at the Workbench. Consume one raw
	//     Food stack from the colony inventory, produce a PreparedMeal
	//     (the v0.5.22 cooked-food sub-type) with quality rolled from
	//     the cook's Cooking skill, drop it on the workbench tile so
	//     it joins the haul flow naturally. Grant Cooking XP per spec
	//     (~0.11 XP per work tick — RimWorld value, scaled to 80 XP per
	//     completion).
	//
	// v0.5.22 simplifications (deferred to v0.5.23+):
	//   • No explicit Bill_Production model. Auto-cook fires whenever
	//     the conditions hold; a per-Workbench bill queue (RepeatN /
	//     Forever / TargetN modes per RimWorld) lands in v0.5.23 polish.
	//   • Single recipe — any raw Plant-family Food cooks to PreparedMeal.
	//     Specific recipes (Mushroom Stew, Berry Tart, Herb Tea) land
	//     when Phase 6 crafting tier registers them.
	//   • Adjacent-Hearth bonus stub: present in concept (cooking at a
	//     Workbench adjacent to a Hearth gets +1 quality tier) but the
	//     check fires only after v0.5.24 ships Hearth-as-real-furniture.
	public static class CookSystem
	{
		public static BehaviorTask? SelectCookTarget(Shroomp s, LocalMap? map, ColonyResources r)
		{
			if (map == null || r == null) return null;
			// Need at least one raw Food (Plant family) in colony inventory
			// to consume. PreparedMeal is also Plant family — exclude it
			// so cooks don't endlessly re-cook their own output.
			if (!HasRawFoodAvailable(r)) return null;
			// Find nearest reachable Workbench.
			var wb = FindNearestWorkbench(s.SimPos, map);
			if (!wb.HasValue) return null;
			var (tx, ty) = wb.Value;
			var px = new Vector2(
				tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
				ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
			return new BehaviorTask(TaskType.Cook, px, 50f,
				interruptible: true,
				tileX: tx, tileY: ty);
		}

		public static void Apply(Shroomp s, BehaviorTask t, LocalMap? map, ColonyResources r)
		{
			if (map == null || r == null) return;
			// Verify workbench still present at target (could've been demolished).
			if (t.TargetTileX < 0 || t.TargetTileY < 0) return;
			var slot = map.GetStructure(t.TargetTileX, t.TargetTileY);
			if (slot.Type != StructureType.Workbench) return;
			// v0.5.84t — auto-cook tracks the v0.5.84t CookMeal recipe:
			// 4 raw Food of any family → 1 PreparedMeal. Use the source
			// stack the player has the MOST of as the visual material so
			// the meal stack reads as "berry stew" / "mushroom stew" rather
			// than always-Plant. RawFood = any Food stack that isn't already
			// a PreparedMeal.
			int totalRaw = r.Inventory.TotalByKind(ItemKind.Food)
			             - r.Inventory.TotalBySubType(ItemKind.Food, "PreparedMeal");
			if (totalRaw < 4) return;
			var raw = r.Inventory.FindFirst(ItemKind.Food, "PreparedMeal");
			if (raw == null) return;
			string sourceMat = raw.Material.SubType;
			// Consume 4 of any non-PreparedMeal Food (smallest stacks first,
			// matching ConsumeByKind's pattern). We can't use ConsumeByKind
			// directly because it would also pull PreparedMeal stacks — so
			// we walk the inventory once with an exclude.
			int needed = 4;
			while (needed > 0)
			{
				var stack = r.Inventory.FindFirst(ItemKind.Food, "PreparedMeal");
				if (stack == null) break;
				int take = System.Math.Min(needed, stack.Quantity);
				r.Inventory.Consume(stack, take);
				needed -= take;
			}
			// Produce PreparedMeal. Quality from cooking skill (ItemFactory
			// reads the level for its quality bell curve internally).
			var rng = new System.Random();
			var meal = ItemFactory.Create(
				ItemKind.Food, "PreparedMeal",
				new MaterialKey("Plant", sourceMat),
				rng,
				0,
				skillLevel: 5,   // baseline; per-shroomp skill picked up by ItemFactory once threaded
				quantity: 1);
			meal.TilePos = new Vector2(
				t.TargetTileX * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
				t.TargetTileY * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
			map.DropItem(meal);
			// v0.5.84r — cooking awards Crafting XP (Cooking was never a real skill;
			// the call was a silent no-op pre-restructure). Crafting now covers
			// workbench-driven production including meals.
			Sporeholm.Simulation.SkillRegistry.GainXp(s, "Crafting", 80f);
			s.TaskDidWork = true;
		}

		private static bool HasRawFoodAvailable(ColonyResources r)
		{
			// v0.5.84t — auto-cook needs 4 raw Food (any family, excluding
			// already-prepared meals) to match the CookMeal recipe.
			int rawTotal = r.Inventory.TotalByKind(ItemKind.Food)
			             - r.Inventory.TotalBySubType(ItemKind.Food, "PreparedMeal");
			return rawTotal >= 4;
		}

		// v0.5.22 — find nearest built Workbench. Walks the structure grid
		// linearly; acceptable at typical scales. A per-workbench HashSet
		// can land later if workbench-heavy bases need it.
		private static (int X, int Y)? FindNearestWorkbench(Vector2 fromPx, LocalMap map)
		{
			int fx = (int)(fromPx.X / LocalMap.TileSize);
			int fy = (int)(fromPx.Y / LocalMap.TileSize);
			int best = int.MaxValue;
			(int X, int Y)? winner = null;
			for (int y = 0; y < map.Height; y++)
			for (int x = 0; x < map.Width;  x++)
			{
				if (map.GetStructure(x, y).Type != StructureType.Workbench) continue;
				int dx = x - fx, dy = y - fy;
				int d = dx * dx + dy * dy;
				if (d < best) { best = d; winner = (x, y); }
			}
			return winner;
		}
	}
}
