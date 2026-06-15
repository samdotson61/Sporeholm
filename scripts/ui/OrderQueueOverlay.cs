using Godot;
using System.Collections.Generic;
using Sporeholm.Simulation;

namespace Sporeholm.UI
{
    // v0.5.2 — RTS-style chain-order waypoint visualization. For every
    // currently-selected shroomp with at least one entry in their
    // MoveOrderQueue (shift+right-click queue), draws:
    //   • A faint cyan line from the shroomp's current position through
    //     each queued waypoint in order (shows route).
    //   • A small cyan dot at each queued waypoint (shows destinations).
    //   • A larger dot at the FIRST waypoint (the active "next" target).
    //
    // Only renders for selected shroomps — the player only cares about their
    // own active commands. Updates from the sim snapshot per tick (cheap
    // — typically 0-3 queued orders per shroomp, and only selected shroomps
    // are walked).
    public partial class OrderQueueOverlay : Node2D
    {
        // Plumbed in by GameController each frame via SetSelection +
        // SetSnapshot. Both are required for any drawing to happen.
        private HashSet<string>?      _selected;
        private SimulationSnapshot?   _snapshot;
        // v0.7.4 (#14) — live patrol route being built with the Patrol tool
        // (GameController._patrolRoute). Drawn in amber so it reads distinct from
        // the cyan chain-order queue. Null when not building a patrol.
        private List<Vector2>?        _patrolPreview;

        public void SetPatrolPreview(IReadOnlyList<Vector2>? route)
        {
            _patrolPreview = (route == null || route.Count == 0)
                ? null : new List<Vector2>(route);
            QueueRedraw();
        }

        public override void _Ready()
        {
            // Above the map / item / designation overlays but below the shroomp
            // colony view so the dots sit "behind" the shroomp sprites at the
            // route origin without obscuring them.
            ZIndex = 0;
        }

        public void SetSelection(HashSet<string> selected)
        {
            _selected = selected;
            QueueRedraw();
        }

        public void SetSnapshot(SimulationSnapshot? snap)
        {
            _snapshot = snap;
            QueueRedraw();
        }

        public override void _Draw()
        {
            DrawPatrolPreview();   // v0.7.4 (#14) — independent of selection/snapshot
            if (_selected == null || _selected.Count == 0) return;
            if (_snapshot == null) return;

            var lineColor = new Color(0.40f, 0.85f, 1.00f, 0.55f);   // cyan, semi-transparent
            var dotColor  = new Color(0.40f, 0.85f, 1.00f, 0.95f);
            var headColor = new Color(0.55f, 0.95f, 1.00f, 1.00f);   // brighter for "next" waypoint

            foreach (var shroomp in _snapshot.Shroomps)
            {
                if (!_selected.Contains(shroomp.Name)) continue;
                if (shroomp.MoveOrderQueue == null || shroomp.MoveOrderQueue.Count == 0) continue;

                // Route polyline: shroomp → first waypoint → second → ...
                Vector2 prev = shroomp.SimPos;
                for (int i = 0; i < shroomp.MoveOrderQueue.Count; i++)
                {
                    Vector2 wp = shroomp.MoveOrderQueue[i];
                    DrawLine(prev, wp, lineColor, 1.5f, antialiased: true);
                    prev = wp;
                }

                // Dots — first one is the brighter "next" target.
                for (int i = 0; i < shroomp.MoveOrderQueue.Count; i++)
                {
                    Vector2 wp = shroomp.MoveOrderQueue[i];
                    float r = (i == 0) ? 4.5f : 3.0f;
                    var c  = (i == 0) ? headColor : dotColor;
                    DrawCircle(wp, r, c);
                    // Thin outline for readability over varied terrain.
                    DrawArc(wp, r, 0f, Mathf.Tau, 16,
                        new Color(0f, 0f, 0f, 0.4f), 1f, antialiased: true);
                }
            }
        }

        // v0.7.4 (#14) — preview of the patrol route the player is currently
        // placing (amber loop + dots), so they can see the route before the
        // second click commits it. Closes the loop back to the first point.
        private void DrawPatrolPreview()
        {
            var r = _patrolPreview;
            if (r == null || r.Count == 0) return;
            var line = new Color(1.00f, 0.80f, 0.30f, 0.70f);   // amber
            var dot  = new Color(1.00f, 0.85f, 0.40f, 0.95f);
            for (int i = 0; i < r.Count - 1; i++)
                DrawLine(r[i], r[i + 1], line, 1.5f, antialiased: true);
            if (r.Count >= 2)
                DrawLine(r[r.Count - 1], r[0], line, 1.5f, antialiased: true);   // loop close
            for (int i = 0; i < r.Count; i++)
            {
                DrawCircle(r[i], 4.0f, dot);
                DrawArc(r[i], 4.0f, 0f, Mathf.Tau, 16,
                    new Color(0f, 0f, 0f, 0.4f), 1f, antialiased: true);
            }
        }
    }
}
