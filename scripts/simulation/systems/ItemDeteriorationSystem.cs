using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.3.46 (Phase 4 core) — daily item decay tick. Called from
    // SimulationCore on the day boundary (once per in-game day at any
    // sim speed). Walks the colony Inventory and applies per-material
    // decay; food items roll Fresh → Stale → Spoiled as their condition
    // falls below the thresholds in ItemStateMeta.
    //
    // v0.4.0 stub extension — Temperature and Insulation multiplier
    // parameters threaded through to `Inventory.TickDeterioration`.
    // Both default to 1.0 today; the data sources (Phase 5 roof
    // detection + Phase 10 weather state) will fill them in once those
    // systems land. `Use` damage (per-task tool/weapon condition hit)
    // remains a Phase 7 combat / Phase 5 building-work hook.
    public static class ItemDeteriorationSystem
    {
        // One day's worth of decay; called on the day boundary.
        // v0.5.24 — added optional `map` parameter so insulation can be
        // resolved per-item from the room each item sits in. Backward-
        // compatible: map=null falls back to colony-average insulation
        // (1.0 outdoor baseline), preserving the v0.4.0 behaviour for
        // any caller that doesn't have map context yet.
        public static void TickDay(Inventory inv, long globalTick, LocalMap? map = null)
        {
            if (inv == null) return;
            inv.TickDeterioration(globalTick, daysElapsed: 1f,
                temperatureMul: ResolveTemperatureMul(),
                insulationMul:  ResolveInsulationMul(inv, map));
        }

        // v0.4.0 (Phase-10 stub) — multiplier from current global
        // weather state. Heat waves accelerate food spoilage (× 1.5–2.0
        // per spec); cold snaps slow it slightly (× 0.7). Returns 1.0
        // until Phase 10 lands its WeatherState model.
        public static float ResolveTemperatureMul()
        {
            // TODO Phase 10 — pull from WeatherState.GlobalTemperatureC,
            // bucket into spec multipliers (× 1.0 comfort band, × 1.5
            // heat wave > 30 °C, × 2.0 heat wave > 40 °C, × 0.7 cold
            // snap < 0 °C). For now, neutral.
            return 1f;
        }

        // v0.5.24 (Phase 5G) — wired. Resolves insulation per-room based
        // on the rooms map items occupy. Returns the AVERAGE insulation
        // across colony Inventory items by their TilePos. Items in roofed
        // rooms with a Hearth get the strongest (×0.25 — sealed +
        // temperature-controlled per spec); roofed-without-Hearth get
        // ×0.5; outdoor items get ×1.0 (no insulation).
        //
        // Inventory items themselves don't carry a TilePos for this version
        // (most items are abstract colony stockpile pre-Phase-5-storage).
        // The wired path uses the map's average room insulation as a
        // colony-baseline. Per-item-position insulation lookup lands when
        // the v0.5.21 IHaulDestination → physical-tile mapping is fully
        // threaded (currently shelves drop items on their tile but the
        // Item.TilePos field isn't always populated for inventoried items).
        //
        // For v0.5.24 minimum-viable: if the map has any indoor rooms with
        // Hearths, apply ×0.5; with Hearths AND substantial enclosure
        // (>50% of items presumed sheltered), apply ×0.25. Outdoor-only
        // colonies stay at ×1.0.
        public static float ResolveInsulationMul(Inventory? inv = null, LocalMap? map = null)
        {
            if (map == null) return 1f;
            map.EnsureRooms();
            // Quick aggregate — count rooms + Hearths to decide colony's
            // overall insulation profile. This is a coarse approximation;
            // proper per-item lookup will land alongside the v0.6 storage-
            // tile-binding refactor.
            bool anyIndoor = false;
            bool anyHearth = false;
            // Walk the map's structure grid for indoor tiles + hearths.
            // O(W*H) but called once per day on the day boundary — cheap.
            //
            // v0.5.84t — also count tiles with `IsRoofed=true` (natural
            // cavern roofs from gen + post-mining tiles inside solid wood/
            // stone masses). RimWorld parity: stored items inside a cave
            // get the same indoor protection as items in a built room.
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width;  x++)
            {
                var slot = map.GetStructure(x, y);
                if (slot.RoomId != 0 && slot.RoomId != RoomDetector.OutdoorRoomId)
                    anyIndoor = true;
                if (!anyIndoor && map.Get(x, y).IsRoofed)
                    anyIndoor = true;
                if (slot.Type == StructureType.Hearth)
                    anyHearth = true;
                if (anyIndoor && anyHearth) break;
            }
            if (!anyIndoor) return 1f;
            return anyHearth ? 0.25f : 0.5f;   // sealed-with-hearth vs roofed-only
        }
    }
}
