using System;

namespace SmurfulationC.Simulation
{
    // v0.4.4 — DF-style handedness trait. Rolled per-smurf at creation
    // (Birth / scenario / wanderer-in) with a 90 / 10 right / left
    // split — close enough to the human population baseline and to the
    // Smurfs-canon "Brainy / Hefty / Vanity are all clearly right-
    // handed in the comics" pattern. The auto-equip pipeline puts
    // tools and weapons in the dominant hand by default; the off-hand
    // stays free for shields + dual-wield (Phase 7 combat).
    public enum Handedness : byte
    {
        Right = 0,
        Left  = 1,
    }

    public static class HandednessMeta
    {
        // ~10 % southpaws — close to real-world prevalence (~11 %) and
        // mirrors RimWorld / DF flavor where it occasionally shows.
        public static Handedness Roll(Random rng) =>
            rng.NextDouble() < 0.10 ? Handedness.Left : Handedness.Right;

        public static SmurfulationC.Simulation.Items.EquipSlot DominantHand(Handedness h) =>
            h == Handedness.Left
                ? SmurfulationC.Simulation.Items.EquipSlot.LeftHand
                : SmurfulationC.Simulation.Items.EquipSlot.RightHand;

        public static SmurfulationC.Simulation.Items.EquipSlot OffHand(Handedness h) =>
            h == Handedness.Left
                ? SmurfulationC.Simulation.Items.EquipSlot.RightHand
                : SmurfulationC.Simulation.Items.EquipSlot.LeftHand;
    }
}
