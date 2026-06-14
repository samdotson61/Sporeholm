using Godot;
using System.Collections.Generic;
using Sporeholm.Simulation.Combat;

// v0.7.0 (Phase 7 §7.8b) — combat reads. Floating damage numbers + species-
// coloured blood spatter so the player sees a hit land without opening the log.
// Modelled on OrderFeedbackOverlay: a list of timed effects ticked in _Process
// and drawn in _Draw. Lives in world space (added as a Node2D child of the
// GameController under the camera), so positions are world pixels. Fed by
// GameController.OnTick draining SimulationManager.DrainCombatEvents().
public partial class CombatFeedbackOverlay : Node2D
{
    private enum FxKind { Floater, Blood }

    private struct Fx
    {
        public FxKind Kind;
        public Vector2 Pos;
        public float   Age;
        public float   Lifetime;
        public string  Text;     // floaters
        public Color   Color;
        public float   Size;     // floater font scale / blood splat radius
        public Vector2 Drift;    // floater rise direction
    }

    private readonly List<Fx> _fx = new();
    private const int MaxFx = 400;
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        _rng.Randomize();
    }

    // ── Public API (called from GameController) ───────────────────────────────

    public void SpawnFloater(Vector2 worldPos, float amount, CombatOutcome outcome)
    {
        (string text, Color col, float size) = outcome switch
        {
            CombatOutcome.Miss  => ("MISS",  new Color(0.78f, 0.78f, 0.78f), 1.0f),
            CombatOutcome.Dodge => ("DODGE", new Color(0.72f, 0.86f, 0.98f), 1.0f),
            CombatOutcome.Block => ("BLOCK", new Color(0.96f, 0.82f, 0.35f), 1.0f),
            CombatOutcome.Crit  => ($"-{Mathf.RoundToInt(amount)}", new Color(1.00f, 0.36f, 0.24f), 1.5f),
            CombatOutcome.Kill  => ($"-{Mathf.RoundToInt(amount)}", new Color(1.00f, 0.24f, 0.24f), 1.5f),
            _                   => ($"-{Mathf.RoundToInt(amount)}", new Color(1.00f, 0.58f, 0.46f), 1.0f),
        };
        _fx.Add(new Fx
        {
            Kind = FxKind.Floater, Pos = worldPos, Age = 0f, Lifetime = 0.75f,
            Text = text, Color = col, Size = size,
            Drift = new Vector2(_rng.RandfRange(-3f, 3f), -16f),
        });
        Cap();
        QueueRedraw();
    }

    public void SpawnBlood(Vector2 worldPos, uint rgba)
    {
        var col = ColorFromRgba(rgba);
        var jitter = new Vector2(_rng.RandfRange(-4f, 4f), _rng.RandfRange(-4f, 4f));
        _fx.Add(new Fx
        {
            Kind = FxKind.Blood, Pos = worldPos + jitter, Age = 0f, Lifetime = 14f,
            Color = col, Size = _rng.RandfRange(2.0f, 3.5f),
        });
        Cap();
        QueueRedraw();
    }

    private void Cap()
    {
        // Drop the oldest effects if we exceed the cap (heavy-combat backstop).
        while (_fx.Count > MaxFx) _fx.RemoveAt(0);
    }

    private static Color ColorFromRgba(uint rgba) => new Color(
        ((rgba >> 24) & 0xFF) / 255f,
        ((rgba >> 16) & 0xFF) / 255f,
        ((rgba >> 8)  & 0xFF) / 255f,
        (rgba & 0xFF) / 255f);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_fx.Count == 0) return;
        float dt = (float)delta;
        bool dirty = false;   // redraw only when something actually changed visually
        for (int i = _fx.Count - 1; i >= 0; i--)
        {
            var f = _fx[i];
            f.Age += dt;
            if (f.Age >= f.Lifetime) { _fx.RemoveAt(i); dirty = true; continue; }
            _fx[i] = f;
            // Floaters always move; a blood decal is static until its fade window,
            // so a fight's worth of decals doesn't force a redraw every frame.
            if (f.Kind == FxKind.Floater || f.Age > f.Lifetime * 0.75f) dirty = true;
        }
        if (dirty) QueueRedraw();
    }

    public override void _Draw()
    {
        Font font = ThemeDB.FallbackFont;
        foreach (var f in _fx)
        {
            float t = f.Age / f.Lifetime;       // 0 → 1
            float invT = 1f - t;
            switch (f.Kind)
            {
                case FxKind.Blood:
                {
                    // Blood stays mostly opaque then fades in the final quarter.
                    float a = Mathf.Min(1f, invT * 4f) * 0.85f;
                    var c = new Color(f.Color.R, f.Color.G, f.Color.B, a);
                    DrawCircle(f.Pos, f.Size, c);
                    DrawCircle(f.Pos + new Vector2(f.Size * 1.4f, f.Size * 0.6f), f.Size * 0.5f, c);
                    DrawCircle(f.Pos - new Vector2(f.Size * 1.2f, f.Size * 0.4f), f.Size * 0.45f, c);
                    break;
                }
                case FxKind.Floater:
                {
                    int fontSize = Mathf.RoundToInt(10f * f.Size);
                    Vector2 p = f.Pos + f.Drift * t + new Vector2(-24f, -10f);
                    // soft shadow for legibility over any terrain; 48px box fits
                    // the longest label (DODGE/BLOCK) centred on the defender.
                    DrawString(font, p + new Vector2(1f, 1f), f.Text,
                        HorizontalAlignment.Center, 48f, fontSize,
                        new Color(0f, 0f, 0f, 0.55f * invT));
                    DrawString(font, p, f.Text,
                        HorizontalAlignment.Center, 48f, fontSize,
                        new Color(f.Color.R, f.Color.G, f.Color.B, invT));
                    break;
                }
            }
        }
    }
}
