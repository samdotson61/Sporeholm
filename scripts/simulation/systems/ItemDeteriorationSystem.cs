using SmurfulationC.Simulation.Items;

namespace SmurfulationC.Simulation.Systems
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
        public static void TickDay(Inventory inv, long globalTick)
        {
            if (inv == null) return;
            inv.TickDeterioration(globalTick, daysElapsed: 1f,
                temperatureMul: ResolveTemperatureMul(),
                insulationMul:  ResolveInsulationMul());
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

        // v0.4.0 (Phase-5 stub) — multiplier from item location's
        // insulation state. Indoors-roofed × 0.4, sealed-temperature-
        // controlled × 0.25, outdoors × 1.0 per spec. Returns 1.0
        // until Phase 5 lands the StructureSlot.HasRoof + RoomDetector
        // flood-fill pipeline. For now every item is treated as
        // "outdoors" — which matches reality (no buildings exist yet).
        public static float ResolveInsulationMul()
        {
            // TODO Phase 5 — once items can sit on tiles (Haul) and
            // tiles can be roofed (Phase 5.10), look up the tile's
            // RoomId / HasRoof state and apply the spec multiplier.
            return 1f;
        }
    }
}
