using Godot;
using Sporeholm.Simulation.Entities;

namespace Sporeholm.UI
{
    // v0.6.0 (Phase 6) — procedural pixel-art sprite bakery for the
    // 15 EntityKind species. Mirrors the boletus-shroomp painter
    // pattern: 20×20 RGBA8 image painted from per-pixel coordinate maths
    // (no asset files). Wrapped in ImageTexture so the MMI consumes it
    // like any other texture. Each painter centred on cx=10, cy=10.
    //
    // Sprite design notes per species (drawn from §6.2 + animalspot.net
    // refs Sam keeps in the design-doc tree):
    //   Glowbunny  — pale fur + soft green glow halo (bioluminescent)
    //   Shroomgoat — chunky body + small horns + spore-wool fluff
    //   Shroomalo  — round friendly hamster body + mushroom-cap tuft
    //   Mouse      — small grey teardrop body + long tail + pink ears
    //   Ladybug    — red dome + black spots + tiny legs
    //   HermitCrab — coiled shell body + two claws + skinny legs
    //   Squirrel   — bushy tail + small body + acorn-tone fur
    //   Bonecrest  — chitin-armoured beetle + ridged back spine
    //   ForestBoar — long brown body + tusks + hoof feet
    //   CaveLizard — wedge head + long body + scaled tail
    //   AntSoldier — three-segment black body + curved mandibles
    //   WaspRenegade — striped yellow/black body + transparent wings
    //   Snake      — coiled S-body + scaled banding
    //   Wolf       — long grey body + pointed snout + bushy tail
    //   MagicWisp  — translucent floating orb + sparkle ring
    public partial class EntityColonyView : Node2D
    {
        private static ImageTexture BakeSpriteFor(EntityKind kind)
        {
            var img = Image.CreateEmpty(20, 20, false, Image.Format.Rgba8);
            img.Fill(new Color(0, 0, 0, 0));
            switch (kind)
            {
                case EntityKind.Glowbunny:       PaintGlowbunny(img);       break;
                case EntityKind.Shroomgoat:      PaintShroomgoat(img);      break;
                case EntityKind.Shroomalo:       PaintShroomalo(img);       break;
                case EntityKind.Mouse:           PaintMouse(img);           break;
                case EntityKind.Ladybug:         PaintLadybug(img);         break;
                case EntityKind.HermitCrab:      PaintHermitCrab(img);      break;
                case EntityKind.Squirrel:        PaintSquirrel(img);        break;
                case EntityKind.BonecrestBeetle: PaintBonecrestBeetle(img); break;
                case EntityKind.ForestBoar:      PaintForestBoar(img);      break;
                case EntityKind.CaveLizard:      PaintCaveLizard(img);      break;
                case EntityKind.AntSoldier:      PaintAntSoldier(img);      break;
                case EntityKind.WaspRenegade:    PaintWaspRenegade(img);    break;
                case EntityKind.Snake:           PaintSnake(img);           break;
                case EntityKind.Wolf:            PaintWolf(img);            break;
                case EntityKind.MagicWisp:       PaintMagicWisp(img);       break;
                default:                         PaintFallback(img);        break;
            }
            return ImageTexture.CreateFromImage(img);
        }

        // ── Painters ──────────────────────────────────────────────────
        // Each painter places pixels on a 20×20 canvas. Centre is (10, 10).
        // Conventions: warm colours for friendlies; cold/sharp tones for
        // hostiles; muted earth tones for neutrals.

        private static void PaintGlowbunny(Image img)
        {
            // Pale cream body with green glow halo
            var glow = new Color(0.55f, 0.95f, 0.70f, 0.35f);
            var body = new Color(0.92f, 0.90f, 0.80f);
            var ear  = new Color(0.85f, 0.75f, 0.70f);
            var eye  = new Color(0.10f, 0.08f, 0.06f);
            // Glow halo
            FillCircle(img, 10, 11, 8, glow);
            // Body — fat oval
            FillEllipse(img, 10, 12, 5, 4, body);
            // Head
            FillCircle(img, 10, 8, 3, body);
            // Long ears
            FillRect(img, 8, 4, 1, 3, ear);
            FillRect(img, 11, 4, 1, 3, ear);
            // Eye dot
            img.SetPixel(9, 8, eye);
            img.SetPixel(11, 8, eye);
            // Cottontail
            img.SetPixel(14, 12, new Color(1f, 1f, 1f));
        }

        private static void PaintShroomgoat(Image img)
        {
            var body  = new Color(0.85f, 0.78f, 0.70f);
            var wool  = new Color(0.95f, 0.92f, 0.88f);
            var horn  = new Color(0.40f, 0.30f, 0.22f);
            var hoof  = new Color(0.15f, 0.12f, 0.10f);
            var eye   = new Color(0.10f, 0.08f, 0.06f);
            // Wool fluff body
            FillEllipse(img, 10, 12, 6, 4, wool);
            // Head
            FillCircle(img, 14, 9, 3, body);
            // Horns
            img.SetPixel(13, 6, horn);
            img.SetPixel(15, 6, horn);
            img.SetPixel(12, 7, horn);
            img.SetPixel(16, 7, horn);
            // Eye + nose
            img.SetPixel(15, 9, eye);
            img.SetPixel(16, 10, eye);
            // Legs
            img.SetPixel(7,  16, hoof); img.SetPixel(7,  17, hoof);
            img.SetPixel(9,  16, hoof); img.SetPixel(9,  17, hoof);
            img.SetPixel(11, 16, hoof); img.SetPixel(11, 17, hoof);
            img.SetPixel(13, 16, hoof); img.SetPixel(13, 17, hoof);
        }

        private static void PaintShroomalo(Image img)
        {
            // Round friendly hamster body with a mushroom-cap tuft on the head.
            var body = new Color(0.92f, 0.80f, 0.62f);
            var cap  = new Color(0.85f, 0.45f, 0.40f);   // soft red cap
            var capDot = new Color(0.98f, 0.95f, 0.90f);
            var eye  = new Color(0.10f, 0.08f, 0.06f);
            var foot = new Color(0.78f, 0.65f, 0.48f);
            FillEllipse(img, 10, 12, 6, 5, body);
            // Cheeks
            FillCircle(img, 6,  12, 2, body);
            FillCircle(img, 14, 12, 2, body);
            // Head + mushroom tuft
            FillCircle(img, 10, 8, 3, body);
            FillEllipse(img, 10, 5, 4, 2, cap);
            img.SetPixel(8,  5, capDot);
            img.SetPixel(11, 4, capDot);
            // Eyes (friendly)
            img.SetPixel(8,  9, eye);
            img.SetPixel(12, 9, eye);
            // Tiny feet
            img.SetPixel(7,  17, foot);
            img.SetPixel(13, 17, foot);
        }

        private static void PaintMouse(Image img)
        {
            var body = new Color(0.55f, 0.50f, 0.48f);
            var pink = new Color(0.92f, 0.65f, 0.65f);
            var eye  = new Color(0.10f, 0.08f, 0.06f);
            var tail = new Color(0.65f, 0.55f, 0.50f);
            FillEllipse(img, 10, 12, 4, 3, body);
            // Head
            FillCircle(img, 13, 11, 2, body);
            // Ears
            FillCircle(img, 12, 9, 1, pink);
            FillCircle(img, 14, 9, 1, pink);
            // Tail
            img.SetPixel(6, 12, tail); img.SetPixel(5, 13, tail);
            img.SetPixel(4, 13, tail); img.SetPixel(3, 12, tail);
            // Eye + nose
            img.SetPixel(14, 11, eye);
            img.SetPixel(15, 11, pink);
        }

        private static void PaintLadybug(Image img)
        {
            var shell = new Color(0.85f, 0.20f, 0.20f);
            var dark  = new Color(0.10f, 0.08f, 0.06f);
            var leg   = new Color(0.05f, 0.04f, 0.04f);
            // Dome shell
            FillCircle(img, 10, 11, 5, shell);
            // Mid line + spots
            for (int x = 5; x <= 15; x++) img.SetPixel(x, 11, dark);
            img.SetPixel(7,  9, dark);
            img.SetPixel(13, 9, dark);
            img.SetPixel(7,  13, dark);
            img.SetPixel(13, 13, dark);
            // Head bump
            FillCircle(img, 10, 6, 2, dark);
            // Legs
            img.SetPixel(5,  11, leg); img.SetPixel(15, 11, leg);
            img.SetPixel(6,  14, leg); img.SetPixel(14, 14, leg);
            img.SetPixel(6,  8, leg);  img.SetPixel(14, 8, leg);
        }

        private static void PaintHermitCrab(Image img)
        {
            var shell      = new Color(0.65f, 0.50f, 0.35f);
            var shellRing  = new Color(0.45f, 0.30f, 0.20f);
            var claw       = new Color(0.90f, 0.55f, 0.45f);
            var leg        = new Color(0.85f, 0.55f, 0.42f);
            var eye        = new Color(0.10f, 0.08f, 0.06f);
            // Spiral shell
            FillCircle(img, 11, 10, 5, shell);
            FillCircle(img, 11, 10, 3, shellRing);
            img.SetPixel(11, 10, shell);
            // Front body + claws
            FillEllipse(img, 6, 13, 2, 2, leg);
            img.SetPixel(4, 12, claw); img.SetPixel(4, 14, claw);
            img.SetPixel(3, 13, claw);
            // Eyestalks
            img.SetPixel(6, 11, leg);
            img.SetPixel(5, 10, eye);
            // Legs
            img.SetPixel(7, 16, leg); img.SetPixel(9, 16, leg);
            img.SetPixel(11, 16, leg); img.SetPixel(13, 16, leg);
        }

        private static void PaintSquirrel(Image img)
        {
            var fur  = new Color(0.55f, 0.35f, 0.20f);
            var tail = new Color(0.65f, 0.42f, 0.25f);
            var eye  = new Color(0.10f, 0.08f, 0.06f);
            var nose = new Color(0.20f, 0.12f, 0.10f);
            // Body
            FillEllipse(img, 8, 12, 3, 3, fur);
            // Head
            FillCircle(img, 11, 9, 3, fur);
            // Bushy tail
            FillEllipse(img, 4, 9, 2, 4, tail);
            FillCircle(img, 4, 6, 2, tail);
            // Ears
            img.SetPixel(10, 6, fur);
            img.SetPixel(12, 6, fur);
            // Eye + nose
            img.SetPixel(12, 9, eye);
            img.SetPixel(13, 10, nose);
            // Feet
            img.SetPixel(7, 16, fur); img.SetPixel(9, 16, fur);
        }

        private static void PaintBonecrestBeetle(Image img)
        {
            var chitin    = new Color(0.30f, 0.25f, 0.20f);
            var ridge     = new Color(0.85f, 0.80f, 0.70f);   // bone-coloured spinal ridge
            var leg       = new Color(0.15f, 0.12f, 0.10f);
            // Body — oval chitin
            FillEllipse(img, 10, 11, 6, 4, chitin);
            // Ridge spikes
            img.SetPixel(8,  8, ridge); img.SetPixel(10, 7, ridge); img.SetPixel(12, 8, ridge);
            img.SetPixel(9,  10, ridge); img.SetPixel(11, 10, ridge);
            // Head + mandibles
            FillCircle(img, 14, 11, 2, chitin);
            img.SetPixel(16, 10, ridge);
            img.SetPixel(16, 12, ridge);
            // Legs
            img.SetPixel(6,  15, leg); img.SetPixel(9, 15, leg); img.SetPixel(12, 15, leg);
            img.SetPixel(6,  16, leg); img.SetPixel(9, 16, leg); img.SetPixel(12, 16, leg);
        }

        private static void PaintForestBoar(Image img)
        {
            var fur  = new Color(0.32f, 0.22f, 0.16f);
            var furL = new Color(0.45f, 0.30f, 0.22f);
            var hoof = new Color(0.10f, 0.08f, 0.06f);
            var tusk = new Color(0.95f, 0.92f, 0.85f);
            var eye  = new Color(0.10f, 0.08f, 0.06f);
            // Body
            FillEllipse(img, 9, 11, 6, 3, fur);
            FillEllipse(img, 9, 10, 6, 2, furL);
            // Head
            FillCircle(img, 15, 11, 2, fur);
            // Tusks
            img.SetPixel(17, 10, tusk);
            img.SetPixel(17, 12, tusk);
            // Eye
            img.SetPixel(16, 10, eye);
            // Legs
            img.SetPixel(5,  15, hoof); img.SetPixel(8,  15, hoof);
            img.SetPixel(11, 15, hoof); img.SetPixel(14, 15, hoof);
            img.SetPixel(5,  16, hoof); img.SetPixel(8,  16, hoof);
            img.SetPixel(11, 16, hoof); img.SetPixel(14, 16, hoof);
        }

        private static void PaintCaveLizard(Image img)
        {
            var scale = new Color(0.35f, 0.45f, 0.30f);
            var lite  = new Color(0.50f, 0.60f, 0.40f);
            var eye   = new Color(0.95f, 0.85f, 0.25f);
            // Long body
            FillEllipse(img, 10, 11, 7, 2, scale);
            // Belly highlight
            FillRect(img, 4, 12, 13, 1, lite);
            // Head wedge
            for (int dy = -1; dy <= 1; dy++)
                for (int x = 16; x <= 18; x++)
                    if ((x - 16) + System.Math.Abs(dy) <= 2) img.SetPixel(x, 11 + dy, scale);
            // Eye (yellow slit)
            img.SetPixel(17, 10, eye);
            // Tail
            img.SetPixel(2, 11, scale); img.SetPixel(1, 12, scale);
            // Legs
            img.SetPixel(6,  13, scale); img.SetPixel(13, 13, scale);
            img.SetPixel(6,  14, scale); img.SetPixel(13, 14, scale);
        }

        private static void PaintAntSoldier(Image img)
        {
            var body = new Color(0.20f, 0.12f, 0.08f);
            var mand = new Color(0.45f, 0.30f, 0.20f);
            var leg  = new Color(0.10f, 0.07f, 0.05f);
            // Three segments
            FillCircle(img, 7, 11, 2, body);
            FillCircle(img, 11, 11, 2, body);
            FillCircle(img, 14, 11, 2, body);
            // Mandibles
            img.SetPixel(16, 10, mand);
            img.SetPixel(16, 12, mand);
            img.SetPixel(17, 11, mand);
            // Legs
            img.SetPixel(8,  14, leg); img.SetPixel(8,  15, leg);
            img.SetPixel(11, 14, leg); img.SetPixel(11, 15, leg);
            img.SetPixel(14, 14, leg); img.SetPixel(14, 15, leg);
            img.SetPixel(8,   8, leg); img.SetPixel(11,  8, leg); img.SetPixel(14,  8, leg);
        }

        private static void PaintWaspRenegade(Image img)
        {
            var yellow = new Color(0.95f, 0.85f, 0.20f);
            var black  = new Color(0.15f, 0.10f, 0.08f);
            var wing   = new Color(0.85f, 0.90f, 0.95f, 0.55f);
            // Body with stripes
            for (int dx = -3; dx <= 3; dx++)
            {
                int x = 10 + dx;
                bool stripe = (dx + 3) % 2 == 0;
                img.SetPixel(x, 11, stripe ? yellow : black);
                img.SetPixel(x, 12, stripe ? yellow : black);
            }
            // Wings (transparent overlay)
            FillEllipse(img, 10, 8, 4, 2, wing);
            // Head
            FillCircle(img, 14, 11, 2, black);
            img.SetPixel(15, 11, yellow);
            // Stinger
            img.SetPixel(6, 11, black); img.SetPixel(5, 12, black);
        }

        private static void PaintSnake(Image img)
        {
            var scale = new Color(0.30f, 0.55f, 0.30f);
            var lite  = new Color(0.55f, 0.75f, 0.40f);
            var dark  = new Color(0.20f, 0.40f, 0.20f);
            var eye   = new Color(0.85f, 0.20f, 0.20f);
            // S-curve body (rough hand-drawn coil)
            int[,] coil = {
                {4,11},{5,11},{6,12},{7,13},{8,13},{9,12},{10,11},{11,10},
                {12, 9},{13, 9},{14,10},{15,11},{16,11},
            };
            for (int i = 0; i < coil.GetLength(0); i++)
                img.SetPixel(coil[i,0], coil[i,1], scale);
            // Banding
            img.SetPixel(7, 13, lite); img.SetPixel(10, 11, lite); img.SetPixel(13, 9, lite);
            img.SetPixel(5, 11, dark); img.SetPixel(9, 12, dark); img.SetPixel(15, 11, dark);
            // Head
            img.SetPixel(17, 11, scale); img.SetPixel(17, 10, scale);
            img.SetPixel(18, 11, eye);
        }

        private static void PaintWolf(Image img)
        {
            var fur  = new Color(0.45f, 0.45f, 0.48f);
            var dark = new Color(0.25f, 0.25f, 0.28f);
            var eye  = new Color(0.95f, 0.85f, 0.25f);
            var nose = new Color(0.10f, 0.08f, 0.06f);
            // Body
            FillEllipse(img, 9, 11, 6, 3, fur);
            // Underbelly
            FillRect(img, 5, 12, 9, 1, dark);
            // Head + snout
            FillCircle(img, 15, 10, 2, fur);
            img.SetPixel(17, 11, fur);
            img.SetPixel(17, 10, nose);
            // Ears
            img.SetPixel(14, 7, fur);
            img.SetPixel(16, 7, fur);
            // Eye
            img.SetPixel(15, 10, eye);
            // Bushy tail
            FillCircle(img, 3, 11, 2, fur);
            img.SetPixel(2, 10, fur);
            // Legs
            img.SetPixel(6,  15, dark); img.SetPixel(9,  15, dark);
            img.SetPixel(12, 15, dark); img.SetPixel(15, 15, dark);
            img.SetPixel(6,  16, dark); img.SetPixel(9,  16, dark);
            img.SetPixel(12, 16, dark); img.SetPixel(15, 16, dark);
        }

        private static void PaintMagicWisp(Image img)
        {
            // Translucent floating orb with sparkle ring
            var coreBright = new Color(0.90f, 0.80f, 1.00f, 0.95f);
            var coreSoft   = new Color(0.65f, 0.55f, 0.95f, 0.55f);
            var halo       = new Color(0.45f, 0.35f, 0.85f, 0.25f);
            var sparkle    = new Color(1.00f, 1.00f, 0.95f, 0.90f);
            FillCircle(img, 10, 10, 6, halo);
            FillCircle(img, 10, 10, 4, coreSoft);
            FillCircle(img, 10, 10, 2, coreBright);
            // Sparkles
            img.SetPixel(4,  6,  sparkle);
            img.SetPixel(16, 7,  sparkle);
            img.SetPixel(13, 16, sparkle);
            img.SetPixel(5,  15, sparkle);
        }

        private static void PaintFallback(Image img)
        {
            // Magenta placeholder so any forgotten species is obvious in-game.
            FillCircle(img, 10, 10, 5, new Color(1f, 0f, 1f));
        }

        // ── Small drawing helpers ─────────────────────────────────────
        private static void FillRect(Image img, int x, int y, int w, int h, Color c)
        {
            for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= img.GetWidth() || py >= img.GetHeight()) continue;
                img.SetPixel(px, py, c);
            }
        }

        private static void FillCircle(Image img, int cx, int cy, int r, Color c)
        {
            int r2 = r * r;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int px = cx + dx, py = cy + dy;
                if (px < 0 || py < 0 || px >= img.GetWidth() || py >= img.GetHeight()) continue;
                img.SetPixel(px, py, c);
            }
        }

        private static void FillEllipse(Image img, int cx, int cy, int rx, int ry, Color c)
        {
            float rxf = rx, ryf = ry;
            for (int dy = -ry; dy <= ry; dy++)
            for (int dx = -rx; dx <= rx; dx++)
            {
                float nx = dx / rxf, ny = dy / ryf;
                if (nx * nx + ny * ny > 1f) continue;
                int px = cx + dx, py = cy + dy;
                if (px < 0 || py < 0 || px >= img.GetWidth() || py >= img.GetHeight()) continue;
                img.SetPixel(px, py, c);
            }
        }
    }
}
