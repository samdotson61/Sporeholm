using Godot;
using System.Collections.Generic;
using SmurfulationC.Simulation;

namespace SmurfulationC.UI
{
    // v0.5.2 — RTS-style chain-order waypoint visualization. For every
    // currently-selected smurf with at least one entry in their
    // MoveOrderQueue (shift+right-click queue), draws:
    //   • A faint cyan line from the smurf's current position through
    //     each queued waypoint in order (shows route).
    //   • A small cyan dot at each queued waypoint (shows destinations).
    //   • A larger dot at the FIRST waypoint (the active "next" target).
    //
    // Only renders for selected smurfs — the player only cares about their
    // own active commands. Updates from the sim snapshot per tick (cheap
    // — typically 0-3 queued orders per smurf, and only selected smurfs
    // are walked).
    public partial class OrderQueueOverlay : Node2D
    {
        // Plumbed in by GameController each frame via SetSelection +
        // SetSnapshot. Both are required for any drawing to happen.
        private HashSet<string>?      _selected;
        private SimulationSnapshot?   _snapshot;

        public override void _Ready()
        {
            // Above the map / item / designation overlays but below the smurf
            // colony view so the dots sit "behind" the smurf sprites at the
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
            if (_selected == null || _selected.Count == 0) return;
            if (_snapshot == null) return;

            var lineColor = new Color(0.40f, 0.85f, 1.00f, 0.55f);   // cyan, semi-transparent
            var dotColor  = new Color(0.40f, 0.85f, 1.00f, 0.95f);
            var headColor = new Color(0.55f, 0.95f, 1.00f, 1.00f);   // brighter for "next" waypoint

            foreach (var smurf in _snapshot.Smurfs)
            {
                if (!_selected.Contains(smurf.Name)) continue;
                if (smurf.MoveOrderQueue == null || smurf.MoveOrderQueue.Count == 0) continue;

                // Route polyline: smurf → first waypoint → second → ...
                Vector2 prev = smurf.SimPos;
                for (int i = 0; i < smurf.MoveOrderQueue.Count; i++)
                {
                    Vector2 wp = smurf.MoveOrderQueue[i];
                    DrawLine(prev, wp, lineColor, 1.5f, antialiased: true);
                    prev = wp;
                }

                // Dots — first one is the brighter "next" target.
                for (int i = 0; i < smurf.MoveOrderQueue.Count; i++)
                {
                    Vector2 wp = smurf.MoveOrderQueue[i];
                    float r = (i == 0) ? 4.5f : 3.0f;
                    var c  = (i == 0) ? headColor : dotColor;
                    DrawCircle(wp, r, c);
                    // Thin outline for readability over varied terrain.
                    DrawArc(wp, r, 0f, Mathf.Tau, 16,
                        new Color(0f, 0f, 0f, 0.4f), 1f, antialiased: true);
                }
            }
        }
    }
}
