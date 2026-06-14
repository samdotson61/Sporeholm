using System.Collections.Generic;

namespace Sporeholm.Simulation.Systems
{
    // v0.7.1 (Phase 7 §7.7) — heals wounds over time, restoring the underlying
    // body-part condition as they close, decays venom, and prunes healed wounds.
    // Runs once per in-game second from SimulationCore, just before the existing
    // bleed tick (so the bleed recompute sees the freshly-healed condition).
    // Sim-thread only. Venom contributes to BleedRate inside RecomputeBleedRate,
    // so it doesn't need to be applied here beyond decaying the load.
    public static class HediffSystem
    {
        private const float TendHealMultiplier   = 6f;    // tended wounds close ~6× faster
        private const float ConditionPerSeverity = 1.0f;  // part condition restored per severity-pt healed
        private const float VenomDecayPerSec      = 1.2f;  // active venom load fades

        public static void Tick(IReadOnlyList<Shroomp> shroomps)
        {
            for (int i = 0; i < shroomps.Count; i++)
            {
                var s = shroomps[i];
                if (!s.IsAlive) continue;

                if (s.Hediffs.Count > 0)
                {
                    for (int h = s.Hediffs.Count - 1; h >= 0; h--)
                    {
                        var w = s.Hediffs[h];
                        float heal = w.NaturalHealPerSec();
                        if (w.Tended) heal *= TendHealMultiplier;
                        w.Severity -= heal;
                        // Restore the underlying part condition as the wound closes
                        // (this is what ultimately ends the bleed for that part).
                        // Guard cond > 0: a destroyed / severed part does NOT
                        // regenerate naturally (matches NeedsSystem.HealParts) — so
                        // a severed limb stays gone instead of slowly regrowing.
                        if (s.BodyParts.TryGetValue(w.BodyPart, out float cond) && cond > 0f)
                            s.BodyParts[w.BodyPart] = System.Math.Min(100f, cond + heal * ConditionPerSeverity);
                        if (w.Severity <= 0f) s.Hediffs.RemoveAt(h);
                    }
                }

                if (s.Venom > 0f)
                    s.Venom = System.Math.Max(0f, s.Venom - VenomDecayPerSec);
            }
        }
    }
}
