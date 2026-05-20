using System.Collections.Generic;
using Godot;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.5.60 — RimWorld-parity per-tick interaction roll.
    //
    // Called once per shroomp per sim tick from BehaviorSystem.Tick.
    // Probabilistic: each eligible shroomp has a small per-tick chance of
    // firing an interaction with a nearby partner. The interaction is a
    // one-tick event (thought + opinion shift + small Social bump) that
    // happens DURING whatever task the pawn is doing — eating, walking,
    // even mid-Build-task. No state machine, no job hijacking.
    //
    // Eligibility filters:
    //   • Shroomp must be alive
    //   • Not yielding (lying down for traffic flow)
    //   • Not asleep (Sleep task in progress)
    //   • Not in critical-need preemption (life-threatening state)
    //
    // Target selection:
    //   • Within ProximityTilesSq (default 3 tiles Chebyshev)
    //   • Same eligibility filters as initiator
    //   • Not the same shroomp
    //   • Cooldown between this pair must have expired
    //
    // Interaction selection:
    //   • Weighted roll over InteractionRegistry.All
    //   • Slight is gated by initiator.Preferences.DislikesShroomp(target.Name)
    //     so it only fires between shroomps with negative existing opinion —
    //     a small but real RimWorld behaviour ("Abrasive" pawns aside)
    //
    // Outcome:
    //   • Both shroomps gain InteractionDef.SocialDelta to Social (clamped)
    //   • Opinion delta applied via Preferences (capped per-cooldown to
    //     prevent unbounded growth)
    //   • Thought emitted to both via ThoughtRegistry
    //   • Cooldown for the pair set to InteractionDef.Cooldown
    public static class InteractionTracker
    {
        // ~3-tile Chebyshev radius. Two shroomps sharing a corridor or a
        // dining table count as adjacent. Beyond ~3 tiles they're not
        // visibly proximate to the player.
        private const int ProximityTilesSq = 9;   // 3² Chebyshev → 9 tiles² distance²

        // Per-tick interaction probability for an eligible shroomp. ~1 %
        // per tick at 1× = ~1 interaction per shroomp per second. Most
        // interactions fail the proximity filter (no partner in range)
        // so the effective rate is much lower.
        private const float TickRollProbability = 0.01f;

        // Per-pair opinion delta cap. Prevents two shroomps becoming
        // best-friends-forever in one minute by chatting nonstop. Matches
        // RimWorld's OpinionMod.expireTicks (we use sim ticks here).
        // private const int  OpinionDeltaCap = 10;   // currently unused — kept for future per-pair cap if needed

        public static void Tick(Shroomp s, IReadOnlyList<Shroomp> shroomps,
            System.Random rng, long currentTick)
        {
            if (!s.IsAlive) return;
            if (s.YieldingTicks > 0) return;
            if (s.CurrentTask is { } ct && ct.Type == TaskType.Sleep) return;

            // Per-tick gate. Saves the proximity scan for ~99 % of ticks.
            if (rng.NextDouble() >= TickRollProbability) return;

            // Find nearest eligible partner (within proximity, off-cooldown).
            Shroomp? partner = FindNearbyPartner(s, shroomps, currentTick);
            if (partner == null) return;

            // Pick an interaction. Slight is gated by negative opinion.
            bool allowSlight = s.Preferences != null &&
                s.Preferences.DislikesShroomp(partner.Name);
            var def = InteractionRegistry.Pick(rng, allowSlight);
            if (def == null) return;

            Apply(s, partner, def, currentTick);
        }

        private static Shroomp? FindNearbyPartner(Shroomp s, IReadOnlyList<Shroomp> shroomps, long currentTick)
        {
            Shroomp? best = null;
            int bestD2 = int.MaxValue;
            int sTx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sTy = (int)(s.SimPos.Y / LocalMap.TileSize);
            for (int i = 0; i < shroomps.Count; i++)
            {
                var o = shroomps[i];
                if (o == s || !o.IsAlive) continue;
                if (o.YieldingTicks > 0) continue;
                if (o.CurrentTask is { } ot && ot.Type == TaskType.Sleep) continue;
                int oTx = (int)(o.SimPos.X / LocalMap.TileSize);
                int oTy = (int)(o.SimPos.Y / LocalMap.TileSize);
                int dx = oTx - sTx, dy = oTy - sTy;
                int d2 = dx * dx + dy * dy;
                if (d2 > ProximityTilesSq) continue;
                // Pair cooldown check.
                if (s.InteractionCooldowns.TryGetValue(o.Name, out long until)
                    && until > currentTick) continue;
                if (d2 < bestD2) { best = o; bestD2 = d2; }
            }
            return best;
        }

        private static void Apply(Shroomp initiator, Shroomp target, InteractionDef def, long currentTick)
        {
            // Social delta — clamped 0-100. Both gain (or lose for Slight).
            initiator.Social = Mathf.Clamp(initiator.Social + def.SocialDelta, 0f, 100f);
            target.Social    = Mathf.Clamp(target.Social    + def.SocialDelta, 0f, 100f);

            // Opinion delta — friendship/enmity threshold via Preferences.
            // Positive interactions can Befriend; negative can flip to dislike.
            if (def.OpinionDelta > 0)
            {
                // Repeated positive interactions cumulatively bump toward friendship.
                // Per-interaction chance based on the delta magnitude.
                if (System.MathF.Abs(def.OpinionDelta) >= 3 && initiator.Preferences != null)
                {
                    initiator.Preferences.Befriend(target.Name);
                    target.Preferences?.Befriend(initiator.Name);
                }
            }
            // Slight could escalate to enemy — keep it light for now: thought
            // penalty is the main effect, no auto-enmity yet (future polish
            // can add it when running negative-opinion totals exist).

            // Thought emission — ThoughtRegistry expects (shroomp, key, context?).
            ThoughtRegistry.Add(initiator, def.ThoughtKey, target.Name);
            ThoughtRegistry.Add(target,    def.ThoughtKey, initiator.Name);

            // Pair cooldown — both directions so neither immediately
            // re-rolls the same partner.
            long until = currentTick + def.Cooldown;
            initiator.InteractionCooldowns[target.Name] = until;
            target.InteractionCooldowns[initiator.Name] = until;
        }
    }
}
