using Godot;
using System.Collections.Generic;
using SmurfulationC.World;

// v0.3.24 — short-lived visual confirmation that every player order was
// received. Three feedback types:
//
//   • Designation commit: a soft flash overlaid on each tile in the
//     just-committed rectangle (fades over ~0.5 sec).
//   • Move order: an expanding cyan ring at the target tile (~0.6 sec).
//   • Combat order: an expanding red ring at the target tile (~0.6 sec).
//
// All entries are tracked in `_pulses` with a TTL; the node ticks them down
// in `_Process` and draws all live ones in `_Draw`. Audio cues are planned
// in a later phase (the user said "audio later on").
public partial class OrderFeedbackOverlay : Node2D
{
    // v0.3.38 — added TileFlashChop / TileFlashCut for the new orders.
    // v0.5.1 — TileFlashStockpile (zone painter) + RingForbid / RingAllow.
    private enum Kind { TileFlashGather, TileFlashExcavate, TileFlashRemove,
                        TileFlashChop, TileFlashCut, RingMove, RingCombat,
                        // v0.4.3 — feedback variants for new context-aware
                        // right-click actions. PickUp = yellow ring around
                        // the targeted item tile; ForceCraft = brown
                        // workbench ring (Phase 5 stub).
                        RingPickUp, RingForceCraft,
                        // v0.4.12 — Haul drag flash (yellow-gold rect).
                        TileFlashHaul,
                        // v0.5.1 — Phase 5A feedback. Stockpile rect = soft
                        // yellow matching the StockpileOverlay tint; Forbid /
                        // Allow rings = red / green X-style flash on the
                        // toggled tile.
                        TileFlashStockpile, RingForbid, RingAllow }

    private struct Pulse
    {
        public Kind   Kind;
        public Rect2  Rect;      // tile-flash uses Rect; ring uses pos+size for origin
        public float  Age;       // seconds since start
        public float  Lifetime;  // total lifetime in seconds
    }

    private readonly List<Pulse> _pulses = new();

    public override void _Ready() { TextureFilter = TextureFilterEnum.Nearest; }

    // Public API used by GameController on each kind of order ──────────────────

    public void FlashDesignationRect(SmurfulationC.UI.DesignationTool tool,
        int x0, int y0, int x1, int y1)
    {
        int ts = LocalMap.TileSize;
        int xMin = Mathf.Min(x0, x1), xMax = Mathf.Max(x0, x1);
        int yMin = Mathf.Min(y0, y1), yMax = Mathf.Max(y0, y1);
        var rect = new Rect2(xMin * ts, yMin * ts,
                             (xMax - xMin + 1) * ts, (yMax - yMin + 1) * ts);
        var kind = tool switch
        {
            SmurfulationC.UI.DesignationTool.Gather    => Kind.TileFlashGather,
            SmurfulationC.UI.DesignationTool.Excavate  => Kind.TileFlashExcavate,
            SmurfulationC.UI.DesignationTool.ChopWood  => Kind.TileFlashChop,
            SmurfulationC.UI.DesignationTool.Cut       => Kind.TileFlashCut,
            SmurfulationC.UI.DesignationTool.Haul      => Kind.TileFlashHaul,
            SmurfulationC.UI.DesignationTool.Stockpile => Kind.TileFlashStockpile,   // v0.5.1
            SmurfulationC.UI.DesignationTool.Remove    => Kind.TileFlashRemove,
            _                                          => Kind.TileFlashGather,
        };
        _pulses.Add(new Pulse { Kind = kind, Rect = rect, Age = 0f, Lifetime = 0.50f });
        QueueRedraw();
    }

    public void RingMove(Vector2 worldPos) =>
        AddRing(Kind.RingMove, worldPos);

    public void RingCombat(Vector2 worldPos) =>
        AddRing(Kind.RingCombat, worldPos);

    public void RingPickUp(Vector2 worldPos) =>
        AddRing(Kind.RingPickUp, worldPos);

    public void RingForceCraft(Vector2 worldPos) =>
        AddRing(Kind.RingForceCraft, worldPos);

    // v0.5.1 — Phase 5A right-click Forbid / Allow toggle feedback.
    public void RingForbid(Vector2 worldPos) =>
        AddRing(Kind.RingForbid, worldPos);

    public void RingAllow(Vector2 worldPos) =>
        AddRing(Kind.RingAllow, worldPos);

    private void AddRing(Kind kind, Vector2 worldPos)
    {
        // Rect carries (origin, max-radius) in a compact form so _Draw can
        // animate the ring outward without an extra struct field.
        _pulses.Add(new Pulse
        {
            Kind     = kind,
            Rect     = new Rect2(worldPos, new Vector2(18f, 18f)),
            Age      = 0f,
            Lifetime = 0.60f,
        });
        QueueRedraw();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_pulses.Count == 0) return;
        float dt = (float)delta;
        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            p.Age += dt;
            if (p.Age >= p.Lifetime) { _pulses.RemoveAt(i); continue; }
            _pulses[i] = p;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var p in _pulses)
        {
            float t = p.Age / p.Lifetime;       // 0 → 1
            float invT = 1f - t;
            switch (p.Kind)
            {
                case Kind.TileFlashGather:
                    DrawRect(p.Rect, new Color(0.45f, 0.95f, 0.45f, 0.55f * invT), true);
                    DrawRect(p.Rect, new Color(0.45f, 1.00f, 0.45f, 0.85f * invT), false, 2f);
                    break;
                case Kind.TileFlashExcavate:
                    DrawRect(p.Rect, new Color(1.00f, 0.55f, 0.20f, 0.55f * invT), true);
                    DrawRect(p.Rect, new Color(1.00f, 0.65f, 0.20f, 0.85f * invT), false, 2f);
                    break;
                case Kind.TileFlashRemove:
                    DrawRect(p.Rect, new Color(1.00f, 0.30f, 0.30f, 0.55f * invT), true);
                    DrawRect(p.Rect, new Color(1.00f, 0.40f, 0.40f, 0.85f * invT), false, 2f);
                    break;
                // v0.3.38 — sienna flash for Chop Wood, teal-cyan for Cut.
                // Match the DesignationOverlay tile colour scheme so the
                // player connects the commit-flash to the persistent overlay.
                case Kind.TileFlashChop:
                    DrawRect(p.Rect, new Color(0.65f, 0.40f, 0.20f, 0.55f * invT), true);
                    DrawRect(p.Rect, new Color(0.85f, 0.55f, 0.30f, 0.85f * invT), false, 2f);
                    break;
                case Kind.TileFlashCut:
                    DrawRect(p.Rect, new Color(0.40f, 0.75f, 0.85f, 0.55f * invT), true);
                    DrawRect(p.Rect, new Color(0.55f, 0.95f, 1.00f, 0.85f * invT), false, 2f);
                    break;
                case Kind.TileFlashHaul:
                    // v0.4.12 — yellow-gold flash matches the RingPickUp
                    // colour so all "move-this-item" feedback shares a
                    // visual idiom.
                    DrawRect(p.Rect, new Color(1.00f, 0.85f, 0.30f, 0.40f * invT), true);
                    DrawRect(p.Rect, new Color(1.00f, 0.95f, 0.55f, 0.90f * invT), false, 2f);
                    break;
                case Kind.RingMove:
                {
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(0.40f, 0.85f, 1.00f, 0.95f * invT), 2.0f, antialiased: true);
                    break;
                }
                case Kind.RingCombat:
                {
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(1.00f, 0.30f, 0.30f, 0.95f * invT), 2.0f, antialiased: true);
                    break;
                }
                case Kind.RingPickUp:
                {
                    // v0.4.3 — yellow ring around the targeted item.
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(1.00f, 0.85f, 0.30f, 0.95f * invT), 2.0f, antialiased: true);
                    break;
                }
                case Kind.RingForceCraft:
                {
                    // v0.4.3 — warm-brown workbench ring (Phase 5 stub).
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(0.85f, 0.55f, 0.25f, 0.95f * invT), 2.0f, antialiased: true);
                    break;
                }
                // v0.5.1 — Phase 5A stockpile painter flash. Pale yellow
                // matches the StockpileOverlay tint so the player connects
                // the commit pulse to the persistent stockpile fill.
                case Kind.TileFlashStockpile:
                    DrawRect(p.Rect, new Color(1.00f, 0.92f, 0.35f, 0.45f * invT), true);
                    DrawRect(p.Rect, new Color(0.92f, 0.78f, 0.20f, 0.90f * invT), false, 2f);
                    break;
                // v0.5.1 — Phase 5A right-click Forbid: red expanding ring
                // with a thin diagonal slash.
                case Kind.RingForbid:
                {
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(1.00f, 0.30f, 0.30f, 0.95f * invT), 2.0f, antialiased: true);
                    // Slash to differentiate from RingCombat — a "no entry" feel.
                    var off = new Vector2(r * 0.7f, r * 0.7f);
                    DrawLine(p.Rect.Position - off, p.Rect.Position + off,
                             new Color(1.00f, 0.30f, 0.30f, 0.95f * invT), 2f, antialiased: true);
                    break;
                }
                // v0.5.1 — Phase 5A right-click Allow: green expanding ring
                // (the inverse of RingForbid). No slash; just a clean ring.
                case Kind.RingAllow:
                {
                    float r = Mathf.Lerp(4f, p.Rect.Size.X, t);
                    DrawArc(p.Rect.Position, r, 0f, Mathf.Tau, 24,
                            new Color(0.40f, 0.95f, 0.45f, 0.95f * invT), 2.0f, antialiased: true);
                    break;
                }
            }
        }
    }
}
