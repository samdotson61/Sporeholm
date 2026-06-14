namespace Sporeholm.Simulation.Combat
{
    // v0.7.1 (Phase 7 §7.7) — a persistent wound on a body part. This is the
    // MEDICAL / pain layer; it sits ALONGSIDE the condition-based bleed / down /
    // death mechanics rather than replacing them:
    //   • A wound contributes Pain (which penalises combat + can knock a
    //     colonist unconscious).
    //   • A wound is the thing a Healer tends; tending restores the underlying
    //     body-part condition (which is what actually stops the bleeding) and
    //     marks the wound tended so it heals fast and stops hurting.
    //   • HediffSystem heals wounds over time (faster when tended), restoring
    //     part condition as they close, and prunes them at zero severity.
    public sealed class Hediff
    {
        public string      BodyPart = "";
        public CombatWound  Type;
        public float        Severity;   // 0..100, decays toward 0 as it heals
        public bool         Tended;     // a Healer has treated it → fast heal, ~no pain

        public Hediff() { }

        public Hediff(string bodyPart, CombatWound type, float severity)
        {
            BodyPart = bodyPart;
            Type = type;
            Severity = severity;
        }

        // Per-wound pain contribution (summed into Shroomp.ComputePain, 0..100).
        // Tended wounds hurt far less. Fractures / mangles / concussions hurt
        // most; bruises least.
        public float PainContribution()
        {
            float w = Type switch
            {
                CombatWound.Bruise     => 0.004f,
                CombatWound.Cut        => 0.006f,
                CombatWound.Puncture   => 0.008f,
                CombatWound.Fracture   => 0.010f,
                CombatWound.Mangle     => 0.012f,
                CombatWound.Sever      => 0.010f,
                CombatWound.Concussion => 0.012f,
                _                      => 0.004f,
            };
            if (Tended) w *= 0.25f;
            return Severity * w;
        }

        // Natural per-in-game-second heal rate for this wound type (before the
        // tended multiplier). Fractures + severe trauma close slowly.
        public float NaturalHealPerSec()
        {
            return Type switch
            {
                CombatWound.Bruise     => 0.35f,
                CombatWound.Cut        => 0.20f,
                CombatWound.Puncture   => 0.16f,
                CombatWound.Concussion => 0.18f,
                CombatWound.Fracture   => 0.08f,
                CombatWound.Mangle     => 0.05f,
                CombatWound.Sever      => 0.04f,
                _                      => 0.20f,
            };
        }
    }
}
