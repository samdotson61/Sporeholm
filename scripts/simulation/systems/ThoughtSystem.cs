using System;
using System.Collections.Generic;
using SmurfulationC.Simulation;

namespace SmurfulationC.Simulation.Systems
{
    // v0.3.43 — Thoughts tick-down and rolling mood-contribution recompute.
    //
    // Runs every SimSystemInterval ticks (once per real second at 1×), the
    // same cadence as NeedsSystem and MoodSystem. Two responsibilities:
    //
    //   1. Decrement TicksRemaining on every active thought by the system
    //      interval. Expired thoughts (TicksRemaining <= 0) clear their
    //      slot; the ring's "oldest entry has the smallest TTL" invariant
    //      naturally puts new thoughts into emptied slots first.
    //
    //   2. Recompute Smurf.MoodFromThoughts — the per-smurf cached sum of
    //      live thought MoodOffsets that MoodSystem folds into MoodScore.
    //      The sum is clamped to ±50 so a streak of similar thoughts can't
    //      pin the mood at one extreme indefinitely.
    //
    // The system only walks smurfs whose ThoughtsDirty flag is set OR whose
    // earliest thought is about to expire on this tick. In practice fewer
    // than 1 % of the colony has live thoughts changing on any given tick,
    // so this stays cheap even at the planned 1000-smurf scale.
    public static class ThoughtSystem
    {
        private const int  TicksPerSystemPass = 60;   // matches SimSystemInterval
        private const float ThoughtMoodClamp  = 50f;

        public static void Tick(IReadOnlyList<Smurf> smurfs)
        {
            foreach (var s in smurfs)
            {
                if (!s.IsAlive) continue;
                if (s.Thoughts == null) continue;

                bool anyChange = s.ThoughtsDirty;
                bool anyActive = false;

                for (int i = 0; i < s.Thoughts.Length; i++)
                {
                    ref var t = ref s.Thoughts[i];
                    if (t.TicksRemaining <= 0) continue;
                    t.TicksRemaining -= TicksPerSystemPass;
                    if (t.TicksRemaining <= 0)
                    {
                        t = default;
                        anyChange = true;
                    }
                    else
                    {
                        anyActive = true;
                    }
                }

                if (anyChange || s.ThoughtsDirty)
                {
                    float sum = 0f;
                    for (int i = 0; i < s.Thoughts.Length; i++)
                    {
                        if (s.Thoughts[i].TicksRemaining > 0)
                            sum += s.Thoughts[i].MoodOffset;
                    }
                    if (sum >  ThoughtMoodClamp) sum =  ThoughtMoodClamp;
                    if (sum < -ThoughtMoodClamp) sum = -ThoughtMoodClamp;

                    // Invalidate MoodSystem's needs-cache so the next
                    // MoodSystem.Tick pass recomputes MoodScore with the
                    // new MoodFromThoughts blended in. Without this, a
                    // smurf with stable needs but a freshly added thought
                    // would not see the mood change until a need wiggled
                    // past the v0.3.36 epsilon.
                    if (Math.Abs(s.MoodFromThoughts - sum) > 0.05f)
                        s.MoodCacheNutrition = float.NaN;

                    s.MoodFromThoughts = sum;
                    s.ThoughtsDirty    = false;
                }
                else if (!anyActive && s.MoodFromThoughts != 0f)
                {
                    s.MoodFromThoughts = 0f;
                    s.MoodCacheNutrition = float.NaN;
                }
            }
        }
    }
}
