using System;
using System.Collections.Generic;
using Godot;

namespace Sporeholm.Simulation
{
    // v0.5.60 — RimWorld-parity social interactions as one-tick events.
    //
    // Pre-v0.5.60 social was only filled via TaskType.Converse — a
    // dedicated job that occupied the shroomp for ~5 seconds. v0.5.59
    // patched the chat-forever loop but the underlying model is still
    // "Converse-as-job." RimWorld's pattern is fundamentally different:
    // pawn interactions fire as EVENTS during whatever the pawn is
    // doing — eating, walking, working at the same workbench. The
    // interaction is one tick (a thought bubble appears, opinion
    // shifts, both pawns gain a small Social bump), then everyone
    // continues their tasks.
    //
    // This file defines the InteractionDef registry + a small set of
    // interaction kinds. The actual per-tick proximity-roll and
    // outcome dispatch live in InteractionTracker (called from
    // BehaviorSystem.Tick).
    //
    // Mushroom-flavor names anticipate the v0.5.61+ Shroomp rebrand:
    // Sporechat / KindSpore / Slight / DeepRoot. The flavor strings
    // (thought labels) read fine for either Shroomps or Shroomps.

    public enum InteractionKind : byte
    {
        Chitchat,   // base case — mild +Social, mild +opinion both ways
        KindWords,  // rarer — larger +opinion, small +mood
        Slight,     // rare — minor -opinion, small -mood (between strangers / disliked)
        DeepTalk,   // rarest — large +opinion both, friend-tier bond progression
    }

    public sealed class InteractionDef
    {
        public InteractionKind Kind { get; }
        public string ThoughtKey { get; }       // ThoughtRegistry key to apply on initiator + target
        public float SocialDelta { get; }       // +/- to both shroomps' Social need
        public int  OpinionDelta { get; }       // +/- to PrefsCache opinion (clamp at ±10 for now)
        public float Weight { get; }            // base probability weight in selection
        public int  Cooldown { get; }           // min ticks between two pawns interacting again

        public InteractionDef(InteractionKind kind, string thoughtKey,
            float socialDelta, int opinionDelta, float weight, int cooldown)
        {
            Kind = kind;
            ThoughtKey = thoughtKey;
            SocialDelta = socialDelta;
            OpinionDelta = opinionDelta;
            Weight = weight;
            Cooldown = cooldown;
        }
    }

    public static class InteractionRegistry
    {
        // Weighted pool. RimWorld uses MTB (mean time between) seconds for
        // each interaction; we use a simpler weight roll over the pool.
        // Numbers tuned so: Chitchat ~75 %, KindWords ~12 %, Slight ~8 %,
        // DeepTalk ~5 %. Slight is gated by opinion (only fires if
        // initiator already dislikes target — separate check in the
        // tracker).
        public static readonly IReadOnlyList<InteractionDef> All = new[]
        {
            new InteractionDef(InteractionKind.Chitchat,  "Chitchat",   +2f,  +1,  75f, cooldown: 300),
            new InteractionDef(InteractionKind.KindWords, "KindWords",  +3f,  +3,  12f, cooldown: 600),
            new InteractionDef(InteractionKind.Slight,    "Slight",     -1f,  -3,   8f, cooldown: 600),
            new InteractionDef(InteractionKind.DeepTalk,  "DeepTalk",   +5f,  +5,   5f, cooldown: 1200),
        };

        // Pick a random interaction weighted by Weight. Slight is filtered
        // out unless the initiator dislikes the target (matches RimWorld
        // pattern where Abrasive/Misanthropic traits skew toward Slight
        // / Insult against pawns with bad opinion). For simplicity v0.5.60
        // doesn't check personality; future polish can weight Slight up
        // for "Pessimist" / "Grumbler" traits.
        public static InteractionDef? Pick(Random rng, bool allowSlight)
        {
            float total = 0f;
            foreach (var d in All)
            {
                if (d.Kind == InteractionKind.Slight && !allowSlight) continue;
                total += d.Weight;
            }
            if (total <= 0f) return null;
            float roll = (float)(rng.NextDouble() * total);
            foreach (var d in All)
            {
                if (d.Kind == InteractionKind.Slight && !allowSlight) continue;
                roll -= d.Weight;
                if (roll <= 0f) return d;
            }
            return All[0];   // fallback (rounding-error safety)
        }
    }
}
