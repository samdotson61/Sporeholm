using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation;
using Sporeholm.Simulation.Entities;
using Sporeholm.World;

namespace Sporeholm.UI
{
    // v0.6.0 (Phase 6) — wildlife renderer. Mirrors ShroompColonyView's
    // architecture: one MultiMesh per species, sprites baked at startup,
    // per-instance transforms updated from each snapshot push.
    //
    // Per-frame cost: ~50 entities × per-instance Transform2D write each
    // ~150 ns = ~7.5 µs total. Negligible against the shroomp pipeline.
    //
    // Sprite art: each EntityKind has a dedicated bake method
    // (BakeXxxSprite) drawing a 16×16 (or 20×20 for larger species)
    // procedural pixel-art creature. Painters live in
    // EntityColonyView.Sprites.cs so this file stays focused on the
    // MMI lifecycle + per-frame loop.
    public partial class EntityColonyView : Node2D
    {
        // v0.6.2 — click selection. Mirrors ShroompColonyView.ShroompClicked
        // → ShroompCardPanel flow; GameController routes this signal to
        // EntityCardPanel.Show on click.
        [Signal] public delegate void EntityClickedEventHandler(string entityIdAsString);

        private const int SpriteW = 20;
        private const int SpriteH = 20;
        private const int MaxPerKind = 32;   // soft cap; matches EntityRegistry PopulationCap max

        private readonly Dictionary<EntityKind, MultiMeshInstance2D> _mmis = new();
        private readonly Dictionary<EntityKind, ImageTexture>        _sprites = new();

        // v0.6.2 — last-snapshot entity list cached so GetEntityIdAt can
        // do per-frame hit-testing without spawning a fresh sim snapshot.
        // Refreshed in UpdateFromSnapshot.
        private System.Collections.Generic.IReadOnlyList<EntitySnapshot>? _lastSnap;

        // v0.6.2u — selection bracket id. Mirrors ShroompColonyView's per-shroomp
        // Selected flag but lives at the colony-view level since EntitySnapshot
        // is a value-type record (no mutable Selected field). GameController
        // sets this when an entity is clicked + Card opened; clears it when
        // the card closes or the selected entity despawns. _Draw walks the
        // last snapshot each frame and renders white corner brackets around
        // the selected entity's SimPos — reuses the same DrawSelectionBrackets
        // helper as the shroomp / tile-properties selection indicators.
        private System.Guid? _selectedId;

        public override void _Ready()
        {
            TextureFilter = TextureFilterEnum.Nearest;
            // v0.6.0 — z=0 same as shroomp colony view + tree-order render
            // resolution. GameController adds this overlay BEFORE the shroomp
            // colony view so shroomps render on top of entities when they
            // overlap (shroomps are the protagonists; player needs them
            // visible even when standing on top of a stationary creature).
            ZIndex = 0;
            var quad = new QuadMesh { Size = new Vector2(SpriteW, SpriteH) };
            foreach (var def in EntityRegistry.All)
            {
                var tex = BakeSpriteFor(def.Kind);
                _sprites[def.Kind] = tex;
                var mmi = CreateMmi(quad, tex);
                _mmis[def.Kind] = mmi;
                AddChild(mmi);
            }
        }

        private MultiMeshInstance2D CreateMmi(QuadMesh quad, ImageTexture tex)
        {
            var mm = new MultiMesh
            {
                Mesh = quad,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
                UseColors       = true,
                InstanceCount   = MaxPerKind,
            };
            return new MultiMeshInstance2D
            {
                Multimesh = mm,
                Texture   = tex,
                ZIndex    = 0,
                TextureFilter = TextureFilterEnum.Nearest,
            };
        }

        // v0.6.0 — render entities into per-kind MultiMesh instances.
        // Called by GameController each snapshot tick. Two-pass: first
        // clear visible counts per kind, then walk the snapshot and
        // bump the per-kind index as each entity is emitted.
        public void UpdateFromSnapshot(IReadOnlyList<EntitySnapshot> entities)
        {
            _lastSnap = entities;   // v0.6.2 — cache for hit-testing
            // Per-kind counters
            var counts = new Dictionary<EntityKind, int>(EntityRegistry.All.Count);
            foreach (var def in EntityRegistry.All) counts[def.Kind] = 0;

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (!_mmis.TryGetValue(e.Kind, out var mmi)) continue;
                int idx = counts[e.Kind];
                if (idx >= MaxPerKind) continue;
                var origin = e.SimPos;
                // Fading the alpha on wounded entities gives a quick
                // visual hint that something is hurt without an HP bar.
                float healthFrac = e.MaxHealth > 0f ? Mathf.Clamp(e.Health / e.MaxHealth, 0f, 1f) : 1f;
                float alpha      = 0.40f + 0.60f * healthFrac;
                // Tamed entities get a soft warm tint so the player can
                // tell their pets apart from wild specimens of the same
                // species at a glance. Phase 8 husbandry adds the same
                // marker in the Animals tab.
                Color tint = e.IsTamed
                    ? new Color(1.10f, 1.00f, 0.85f, alpha)
                    : new Color(1.00f, 1.00f, 1.00f, alpha);
                mmi.Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
                mmi.Multimesh.SetInstanceColor(idx, tint);
                counts[e.Kind] = idx + 1;
            }

            // Publish visible counts
            foreach (var def in EntityRegistry.All)
            {
                _mmis[def.Kind].Multimesh.VisibleInstanceCount = counts[def.Kind];
            }
        }

        // v0.6.2 — hit-test for click selection. Returns the EntitySnapshot
        // whose SimPos is within `radius` px of the given world position;
        // null if nothing matches. Mirrors ShroompColonyView.GetShroompNameAt.
        // The radius defaults to a half-tile (8 px) to match the visual
        // body size of most species — Wolf / Forest Boar (BodyRadius 9) are
        // forgivingly hit-testable at 12 px; the smallest species (Mouse
        // / Ant Soldier at 4) need clicks closer to centre but the per-
        // species BodyRadiusPx in EntityDef sets the actual gate.
        public EntitySnapshot? GetEntitySnapAt(Vector2 worldPos, float fallbackRadius = 10f)
        {
            if (_lastSnap == null) return null;
            EntitySnapshot? best = null;
            float bestDist2 = float.MaxValue;
            for (int i = 0; i < _lastSnap.Count; i++)
            {
                var e = _lastSnap[i];
                var def = EntityRegistry.Get(e.Kind);
                float r = Mathf.Max(def.BodyRadiusPx, fallbackRadius);
                float dx = e.SimPos.X - worldPos.X;
                float dy = e.SimPos.Y - worldPos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > r * r) continue;
                // Closest entity wins (handles overlapping creatures correctly).
                if (d2 < bestDist2) { bestDist2 = d2; best = e; }
            }
            return best;
        }

        // Convenience: fire the EntityClicked signal with the given id.
        // GameController calls this after GetEntitySnapAt returns a hit.
        public void EmitEntityClicked(System.Guid id) =>
            EmitSignal(SignalName.EntityClicked, id.ToString());

        // v0.6.2u — selection bracket plumbing. GameController calls
        // SetSelection on click and ClearSelection on EntityCardPanel close.
        // _Draw runs every frame and paints the same white corner brackets
        // ShroompColonyView uses for selected shroomps + SelectionOverlay
        // uses for tile-properties selections, so the visual treatment
        // stays consistent across the three click-inspector flows.
        public void SetSelection(System.Guid id)
        {
            _selectedId = id;
            QueueRedraw();
        }

        public void ClearSelection()
        {
            if (_selectedId == null) return;
            _selectedId = null;
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            // Brackets sit on the selected entity's SimPos which moves as
            // the entity wanders. Repaint each frame while a selection is
            // active so the brackets track the entity's motion.
            if (_selectedId != null) QueueRedraw();
        }

        public override void _Draw()
        {
            if (_selectedId == null || _lastSnap == null) return;
            for (int i = 0; i < _lastSnap.Count; i++)
            {
                var e = _lastSnap[i];
                if (e.Id != _selectedId.Value) continue;
                // Frame the entity sprite with brackets sized to the species'
                // body radius. Slight vertical offset centres the brackets on
                // the sprite (entities render with sprite centre at SimPos).
                var def = EntityRegistry.Get(e.Kind);
                float r = Mathf.Max(def.BodyRadiusPx, 9f) + 2f;
                var rect = new Rect2(e.SimPos.X - r, e.SimPos.Y - r, r * 2f, r * 2f);
                ShroompColonyView.DrawSelectionBrackets(this, rect);
                return;
            }
            // Selected entity not found in the latest snapshot — auto-clear.
            _selectedId = null;
        }
    }
}
