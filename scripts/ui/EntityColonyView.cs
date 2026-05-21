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
        private const int SpriteW = 20;
        private const int SpriteH = 20;
        private const int MaxPerKind = 32;   // soft cap; matches EntityRegistry PopulationCap max

        private readonly Dictionary<EntityKind, MultiMeshInstance2D> _mmis = new();
        private readonly Dictionary<EntityKind, ImageTexture>        _sprites = new();

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
                // species at a glance. Phase 9 husbandry adds the same
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
    }
}
