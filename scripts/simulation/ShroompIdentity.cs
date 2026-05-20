using System;
using Godot;

namespace Sporeholm.Simulation
{
    // v0.5.63 — Per-Shroomp visual identity roller. Sets CapShape /
    // CapTexture / StemBuild / CapColour / StemColour / PorePadColour
    // at Shroomp creation (Scenario founding-7, BirthSystem children,
    // wanderer-in arrivals). Sprite cache keys on the bucketed colours
    // + cap shape + sex + mood — bucketing keeps the sprite pool bounded
    // (~84 base sprites worst case) while still giving every Shroomp
    // visible per-individual variation.
    //
    // Palette tuned for "fungal" look:
    //   Caps: cream / tan / russet / red-brown / dark-brown / spotted
    //   Stems: pale cream / tan / light-brown (always lighter than cap)
    //   Pore-pads: cream / yellow / olive (cap underside)
    //
    // Sex-distinct nudge: Female (Spore-Mothers in lore) get a brighter
    // pore-pad bias — subtle but visible from below / in portraits.
    public static class ShroompIdentity
    {
        // Cap palette — six base hues. Sprite baker buckets by index when
        // caching so per-Shroomp colour variation reads through to the
        // sprite without exploding the cache.
        private static readonly Color[] CapColours =
        {
            new(0.93f, 0.86f, 0.72f),   // cream
            new(0.78f, 0.62f, 0.42f),   // tan
            new(0.55f, 0.32f, 0.18f),   // russet
            new(0.62f, 0.20f, 0.18f),   // red-brown
            new(0.36f, 0.22f, 0.14f),   // dark-brown
            new(0.82f, 0.55f, 0.30f),   // amber-orange
        };

        // Stem palette — always lighter / paler than the cap. Bucketed
        // independently of cap to allow contrast variation.
        private static readonly Color[] StemColours =
        {
            new(0.95f, 0.92f, 0.80f),   // cream-white
            new(0.86f, 0.78f, 0.62f),   // pale-tan
            new(0.72f, 0.62f, 0.48f),   // light-brown
        };

        // Pore-pad palette — cap underside colour. Visible in the
        // sprite as a small ring of dots under the cap edge.
        private static readonly Color[] PorePadColours =
        {
            new(0.92f, 0.85f, 0.60f),   // cream
            new(0.92f, 0.80f, 0.30f),   // yellow
            new(0.62f, 0.62f, 0.30f),   // olive
        };

        // Female Spore-Mother bias: 70 % chance of yellow/olive pore-pad
        // (the brighter end of the palette). Males default uniform-random.
        private static readonly Color[] PorePadColoursFemale =
        {
            new(0.92f, 0.80f, 0.30f),   // yellow
            new(0.62f, 0.62f, 0.30f),   // olive
            new(0.96f, 0.90f, 0.40f),   // bright cream-yellow
        };

        public static void Roll(Shroomp s, Random rng)
        {
            s.CapShape = (CapShape)rng.Next(6);
            s.CapTexture = (CapTexture)rng.Next(3);
            s.StemBuild = (StemBuild)rng.Next(3);
            s.CapColour = CapColours[rng.Next(CapColours.Length)];
            s.StemColour = StemColours[rng.Next(StemColours.Length)];
            if (s.Sex == Sex.Female && rng.NextDouble() < 0.70)
                s.PorePadColour = PorePadColoursFemale[rng.Next(PorePadColoursFemale.Length)];
            else
                s.PorePadColour = PorePadColours[rng.Next(PorePadColours.Length)];
        }
    }
}
