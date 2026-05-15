using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SmurfulationC.Simulation;
using SmurfulationC.World;

// Node2D that renders smurfs and stub buildings procedurally on top of the tile map.
// Terrain is now drawn by LocalMapRenderer; this class draws only entities.
public partial class SmurfColonyView : Node2D
{
    [Signal] public delegate void SmurfClickedEventHandler(string name);

    // ── Internal wander state ──────────────────────────────────────────────────

    private sealed class VisualSmurf
    {
        public string   Name     = "";
        public string   Role     = "";
        public Sex      Sex      = Sex.Male;
        public string   MoodStr  = "Content";
        public MoodState Mood    = MoodState.Content;
        public float Nutrition = 80, Rest = 80, Social = 80, Magic = 80, Safety = 90;
        public float MoodScore = 70;
        public Vector2 Pos;
        public Vector2 Target;
        public float   Speed;
        public bool    Selected;
        public float   BobTimer;
        // v0.3.24 — non-null when the sim has this smurf in combat. Visual
        // draws a sword glyph above the head while set.
        public string? CombatTargetName;

        // v0.4.2 — carry-visual + equipment overlay payload. CarriedKind /
        // CarriedSubType / CarriedMaterialFamily drive the in-hand icon
        // drawn next to the smurf while hauling. EquippedTool / Weapon /
        // Apparel populate the body-overlay tint (stub today — no
        // production path equips, so these stay null).
        public string? CarriedKind;
        public string? CarriedSubType;
        public string? CarriedMaterialFamily;
        public string? EquippedToolSubType;
        public string? EquippedWeaponSubType;
        public string? EquippedApparelSubType;

        // v0.4.4 — per-slot equipment payload + handedness. Renderer
        // walks Equipment to draw the hand-held tool on the correct
        // side (dominant vs off-hand) and to layer per-body-part
        // overlays as Phase 7 armor lands.
        public System.Collections.Generic.IReadOnlyDictionary<string,
            (string Kind, string SubType, string MaterialFamily, string MaterialSubType)>? Equipment;
        public Handedness Handedness;

        // v0.4.30 — DF-style yield (lying down to let other smurfs climb
        // over). Renderer squashes the sprite vertically while true.
        public bool IsYielding;
    }

    private readonly List<VisualSmurf>        _smurfs = new();
    private readonly RandomNumberGenerator    _rng    = new();

    // v0.4.22 — O(1) name → VisualSmurf lookup. The previous
    // `UpdateFromTick` did `_smurfs.Find(s => s.Name == snap.Name)`
    // per snapshot entry. At 250 smurfs × 60 Hz snapshot push that
    // was ~3.75 M name comparisons per second on the main thread +
    // a closure allocation per Find call — the single-thread spike
    // Sam's CPU performance graph showed. Maintained in lock-step
    // with `_smurfs`: every Add / Remove also updates this dict.
    private readonly Dictionary<string, VisualSmurf> _byName = new();
    // Scratch set for the snapshot-name → currently-present diff in
    // UpdateFromTick, so we don't allocate a new HashSet every push.
    private readonly HashSet<string> _scratchSnapNames = new();

    // Map bounds — updated from the actual local map size; smurfs wander within these.
    private Vector2 _mapSize = new(LocalMap.DefaultWidth  * LocalMap.TileSize,
                                    LocalMap.DefaultHeight * LocalMap.TileSize);

    public float SpeedMultiplier { get; set; } = 1f;
    public bool  Paused          { get; set; } = false;

    // v0.4.24 — MultiMeshInstance2D-based body rendering. Per the Godot
    // 2D rendering guide, MultiMesh is the canonical answer for "many
    // sprites with the same texture": ONE GPU draw call submits N
    // instances, the vertex shader applies each instance's transform on
    // the GPU. CPU work drops from N individual `DrawTextureRect`
    // interop crossings to N cheap `SetInstanceTransform2D` calls — and
    // the Compatibility renderer supports MultiMeshInstance2D natively.
    //
    // We keep 12 separate `MultiMeshInstance2D` children — one per
    // (mood × sex) variant — because each MultiMesh binds a single
    // texture. Within a variant, all smurfs batch into one GPU draw.
    // Worst case (all 6 moods × 2 sexes present in the visible set) is
    // 12 draw calls for the whole colony body pass, down from 250
    // individual `DrawTextureRect` commands.
    private MultiMeshInstance2D[]? _maleMmi;
    private MultiMeshInstance2D[]? _femaleMmi;
    private int[] _maleInstanceCount   = System.Array.Empty<int>();
    private int[] _femaleInstanceCount = System.Array.Empty<int>();
    private const int MaxInstancesPerVariant = 300;   // 250 target colony + headroom

    // v0.4.24 — extras (equipment, carry icon, combat indicator, name
    // label) must render *above* the body. Children render after the
    // parent's _Draw, so the body MMIs (children of this Node2D)
    // already draw on top of anything emitted by `SmurfColonyView._Draw`.
    // We add `_extrasNode` as the LAST child so its _Draw fires after
    // the MMIs — putting overlay icons on top of body sprites where
    // they belong.
    private SmurfExtrasNode? _extrasNode;

    public override void _Ready()
    {
        _rng.Randomize();
        // v0.4.20 — nearest-neighbour sampling for the pre-baked smurf
        // sprites. Matches the pixel-art aesthetic and skips bilinear
        // filtering at the GPU.
        TextureFilter = TextureFilterEnum.Nearest;
        var map = WorldState.Instance?.CurrentLocalMap;
        if (map != null)
            _mapSize = new Vector2(map.Width * LocalMap.TileSize, map.Height * LocalMap.TileSize);

        // v0.4.24 — bake the sprite cache, then build the MultiMesh
        // children. Order matters: MMI children added first → render
        // before `_extrasNode` (added last) so extras layer above body.
        EnsureSprites();
        SetupMultiMeshes();
        _extrasNode = new SmurfExtrasNode { Owner = this };
        AddChild(_extrasNode);
    }

    private void SetupMultiMeshes()
    {
        var quad = new QuadMesh { Size = new Vector2(SpriteW, SpriteH) };
        _maleMmi   = new MultiMeshInstance2D[_moodCount];
        _femaleMmi = new MultiMeshInstance2D[_moodCount];
        _maleInstanceCount   = new int[_moodCount];
        _femaleInstanceCount = new int[_moodCount];

        for (int mood = 0; mood < _moodCount; mood++)
        {
            _maleMmi  [mood] = CreateVariantMmi(quad, _maleSprites!  [mood]);
            _femaleMmi[mood] = CreateVariantMmi(quad, _femaleSprites![mood]);
        }
    }

    private MultiMeshInstance2D CreateVariantMmi(Mesh mesh, Texture2D tex)
    {
        var mm = new MultiMesh
        {
            Mesh                 = mesh,
            TransformFormat      = MultiMesh.TransformFormatEnum.Transform2D,
            InstanceCount        = MaxInstancesPerVariant,
            VisibleInstanceCount = 0,
        };
        var mmi = new MultiMeshInstance2D
        {
            Multimesh     = mm,
            Texture       = tex,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        AddChild(mmi);
        return mmi;
    }

    // Child node that draws the "above-body" extras (equipment, carry,
    // combat indicator, name label). Added as the LAST child of
    // SmurfColonyView in _Ready, so its _Draw fires after the MMI body
    // pass.
    private partial class SmurfExtrasNode : Node2D
    {
        // v0.4.42 — `new` to acknowledge the Node.Owner hide. We're using
        // this field as our own typed back-reference to the parent view,
        // not the Godot editor-owner concept. CS0108 silenced.
        public new SmurfColonyView Owner = null!;
        public override void _Draw()
        {
            Owner?.DrawAllExtras();
        }
    }

    // Called from GameController after deferred map generation so Smurfs wander within
    // the actual generated map bounds rather than the default 1280×800 placeholder.
    public void UpdateMapSize(LocalMap map) =>
        _mapSize = new Vector2(map.Width * LocalMap.TileSize, map.Height * LocalMap.TileSize);

    public Dictionary<string, (Vector2 Pos, Vector2 Target)> GetPositions()
    {
        var dict = new Dictionary<string, (Vector2, Vector2)>(_smurfs.Count);
        foreach (var s in _smurfs)
            dict[s.Name] = (s.Pos, s.Target);
        return dict;
    }

    public void SeedSmurfs(List<(string name, string role)> roster,
        Dictionary<string, (Vector2 Pos, Vector2 Target)>? savedPositions = null)
    {
        _smurfs.Clear();
        _byName.Clear();   // v0.4.22 — keep dict in sync
        // Compute a passable spawn cluster near the map centre so the founding
        // colony reliably appears together on every level, never hidden inside a
        // rock wall or off the map. Cluster size grows with roster so there's
        // always enough room (×3 buffer leaves slack for visual jitter).
        var map    = WorldState.Instance?.CurrentLocalMap;
        var spawn  = map?.FindSpawnCluster(Math.Max(8, roster.Count * 3))
                     ?? new List<(int X, int Y)>();

        for (int i = 0; i < roster.Count; i++)
        {
            var (name, role) = roster[i];
            Vector2 pos, target;
            if (savedPositions?.TryGetValue(name, out var saved) == true
                && saved.Pos != Vector2.Zero)
            {
                pos    = saved.Pos;
                target = saved.Target != Vector2.Zero ? saved.Target : RandomPos();
            }
            else if (spawn.Count > 0)
            {
                var tile = spawn[i % spawn.Count];
                pos    = TileCentre(tile) + JitterOffset();
                target = pos;
            }
            else
            {
                pos    = RandomPos();
                target = RandomPos();
            }

            var vs = new VisualSmurf
            {
                Name     = name,
                Role     = role,
                Pos      = pos,
                Target   = target,
                Speed    = _rng.RandfRange(28f, 55f),
                BobTimer = _rng.RandfRange(0f, Mathf.Tau),
            };
            _smurfs.Add(vs);
            _byName[name] = vs;   // v0.4.22 — keep dict in sync
        }
    }

    // Converts a tile (x, y) to its world-space pixel centre.
    private static Vector2 TileCentre((int X, int Y) tile) =>
        new(tile.X * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
            tile.Y * LocalMap.TileSize + LocalMap.TileSize * 0.5f);

    // ±half-tile offset so clustered smurfs don't visually stack on identical pixels.
    private Vector2 JitterOffset() =>
        new(_rng.RandfRange(-LocalMap.TileSize * 0.4f, LocalMap.TileSize * 0.4f),
            _rng.RandfRange(-LocalMap.TileSize * 0.4f, LocalMap.TileSize * 0.4f));

    public void UpdateFromTick(IReadOnlyList<SmurfSnapshot> snaps)
    {
        // v0.4.22 — O(N) overall; was O(N²) via `_smurfs.Find` per snap.
        // Reuse the scratch set so no per-tick HashSet allocation.
        _scratchSnapNames.Clear();
        int snapCount = snaps.Count;
        for (int i = 0; i < snapCount; i++) _scratchSnapNames.Add(snaps[i].Name);

        // Remove visual smurfs whose names no longer appear in the
        // snapshot (deaths). Walk the list backward so we can RemoveAt
        // in place; sync the dictionary on each removal.
        for (int i = _smurfs.Count - 1; i >= 0; i--)
        {
            string name = _smurfs[i].Name;
            if (!_scratchSnapNames.Contains(name))
            {
                _byName.Remove(name);
                _smurfs.RemoveAt(i);
            }
        }

        for (int i = 0; i < snapCount; i++)
        {
            var snap = snaps[i];
            // O(1) Dictionary lookup replaces the O(N) List.Find. At 250
            // smurfs this drops a 62 500-comparison Find storm per snap
            // push to a 250-lookup walk.
            _byName.TryGetValue(snap.Name, out var vs);

            // Phase 3: authoritative position is on the sim thread. Pull SimPos
            // and SimTarget into VisualSmurf so _Process can lerp the visual
            // avatar toward it instead of autonomously wandering.

            if (vs == null)
            {
                // Pick the spawn position in priority order:
                //   1. The sim's authoritative SimPos when it's non-zero
                //      (SimulationManager.SeedSimPositions has run by then).
                //   2. The average of existing visual smurfs + jitter (births
                //      mid-game, so newborns appear inside the colony).
                //   3. A fresh BFS-from-centre spawn cluster tile (no existing
                //      visual neighbours yet — e.g. very first snapshot of a
                //      fresh game with non-trivial scenario rosters).
                //   4. RandomPos fallback (no map bound yet — should never hit).
                Vector2 spawnPos;
                if (snap.SimPos != Vector2.Zero)
                {
                    spawnPos = snap.SimPos;
                }
                else if (_smurfs.Count > 0)
                {
                    var avg = Vector2.Zero;
                    foreach (var existing in _smurfs) avg += existing.Pos;
                    avg /= _smurfs.Count;
                    spawnPos = avg + JitterOffset();
                }
                else
                {
                    var map   = WorldState.Instance?.CurrentLocalMap;
                    var seed  = map?.FindSpawnCluster(1);
                    spawnPos  = seed?.Count > 0 ? TileCentre(seed[0]) + JitterOffset() : RandomPos();
                }
                vs = new VisualSmurf
                {
                    Name     = snap.Name,
                    Role     = snap.Role,
                    Sex      = snap.Sex,
                    Pos      = spawnPos,
                    Target   = spawnPos,
                    Speed    = _rng.RandfRange(28f, 55f),
                    BobTimer = _rng.RandfRange(0f, Mathf.Tau),
                };
                _smurfs.Add(vs);
                _byName[snap.Name] = vs;   // v0.4.22 — keep dict in sync
            }

            vs.Nutrition = snap.Nutrition;
            vs.Rest      = snap.Rest;
            vs.Social    = snap.Social;
            vs.Magic     = snap.MagicResonance;
            vs.Safety    = snap.Safety;
            vs.MoodScore = snap.MoodScore;
            vs.Mood      = snap.MoodState;
            vs.MoodStr   = snap.MoodState.ToString();
            vs.Role      = snap.Role;
            vs.Sex       = snap.Sex;
            vs.CombatTargetName = snap.CombatTargetName;
            vs.CarriedKind            = snap.CarriedKind;
            vs.CarriedSubType         = snap.CarriedSubType;
            vs.CarriedMaterialFamily  = snap.CarriedMaterialFamily;
            vs.EquippedToolSubType    = snap.EquippedToolSubType;
            vs.EquippedWeaponSubType  = snap.EquippedWeaponSubType;
            vs.EquippedApparelSubType = snap.EquippedApparelSubType;
            vs.Equipment              = snap.Equipment;
            vs.Handedness             = snap.Handedness;
            vs.IsYielding             = snap.IsYielding;

            // Phase 3: sync visual target with sim. If the sim's reported SimPos
            // is non-zero (i.e., seeded), use it as the lerp destination.
            if (snap.SimPos != Vector2.Zero)
                vs.Target = snap.SimPos;
        }
        // v0.3.36 — even if Paused (so _Process won't tick), a fresh snapshot
        // arrived and the visual needs to redraw once to reflect any new
        // positions / mood colours / combat flags. Without this, role-change
        // updates while paused wouldn't repaint until the player unpaused.
        QueueRedraw();
        // v0.4.28 — when Paused, _Process returns early and never runs
        // UpdateMultiMeshInstances() / _extrasNode.QueueRedraw(). On a paused
        // game load (default scenario start) that left the body MultiMesh at
        // VisibleInstanceCount = 0 and the extras canvas blank, so smurfs
        // were invisible until the player hit play. Push the body buffers
        // and request an extras redraw so the colony renders on the first
        // snapshot regardless of pause state.
        if (Paused)
        {
            UpdateMultiMeshInstances();
            _extrasNode?.QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        // v0.3.36 (B.8 / N.5) — _Process used to QueueRedraw every frame
        // unconditionally. With the planned 1000-smurf colony, that's a full
        // colony draw every frame even when nothing moved (paused, smurfs
        // idle on their targets). Now we only redraw when something actually
        // changed. BobTimer animates while running, so the redraw still
        // fires every frame at 1×+ speed — but at pause we drop to zero
        // redraws/sec instead of 60.
        if (Paused)
        {
            // No animation, no movement, no redraw needed. Position/target
            // changes from UpdateFromTick (e.g. on save load) trigger a
            // one-time redraw via the explicit QueueRedraw at the end of
            // that method (no-op until added there).
            return;
        }

        {
            // v0.4.19 — visual animation scales 1:1 with `SpeedMultiplier`
            // so "2× speed" really means 2× visual animation, "10×" means
            // 10× bob + 10× lerp, etc. The previous version dampened
            // visuals with `Sqrt(SpeedMultiplier)` (so 10× sim only
            // produced ~3.16× visual) to avoid dizziness at the legacy
            // 100× ceiling. With the v0.4.19 speed buttons re-labelled to
            // 1× / 2× / 5× / 10× the ceiling is well within "comfortable
            // to watch" range and the sqrt was just lying about how fast
            // the world was actually running.
            float visualMul = Mathf.Max(SpeedMultiplier, 0.0001f);
            float dt = (float)delta * visualMul;
            // v0.3.40 — visual lerp WITH snap-threshold. Replaces v0.3.23's
            // pure-snap behaviour.
            //
            // v0.3.23 originally killed the visual lerp because a straight-
            // line lerp from old → new SimPos cut through impassable terrain
            // (the sim correctly routed around the rock, but the visual went
            // straight). At hot-tick rates (~0.5 px per sim tick) snapping
            // was indistinguishable from lerping so the trade-off was free.
            //
            // v0.3.39 introduced LOD ticking, which makes cold smurfs' SimPos
            // update every 6 ticks with ~3 px jumps. Snapping to that
            // produces visible "warping" — the smurf freezes for 5 frames
            // then teleports 3 px — especially during camera scroll when
            // the player sees cold smurfs entering frame.
            //
            // Solution: lerp at 200 px/s when the SimPos jump is small
            // enough that the lerp will cover it in <1 frame anyway (so
            // it's a no-op for hot smurfs), and snap unconditionally when
            // the jump is bigger than 32 px (SimPos rescue from the BFS in
            // MoveOneTick can move a smurf 8+ tiles instantly; lerping
            // through that would visibly drift through walls).
            const float SnapThreshold = 32f;
            const float LerpSpeed     = 200f;  // px/s

            foreach (var s in _smurfs)
            {
                s.BobTimer += dt * 2.4f;

                if (s.Target == Vector2.Zero)
                    continue;          // not yet seeded

                var diff = s.Target - s.Pos;
                float dist = diff.Length();
                if (dist <= 0.01f) continue;                      // already there
                if (dist >= SnapThreshold)
                {
                    s.Pos = s.Target;                              // big jump → snap
                }
                else
                {
                    float step = Mathf.Min(dist, LerpSpeed * dt);
                    s.Pos += diff.Normalized() * step;
                }
            }
        }
        // v0.4.24 — push positions into the MultiMesh instance buffers.
        // The body sprites are rendered by the MMI children automatically
        // once their transforms are set; only the parent + extras canvas
        // items still need explicit QueueRedraw.
        UpdateMultiMeshInstances();
        QueueRedraw();
        _extrasNode?.QueueRedraw();
    }

    // v0.4.24 — group visible smurfs by (sex × mood) variant and feed each
    // variant's MultiMesh a fresh set of instance transforms. One pass over
    // `_smurfs` total — O(N) — with O(1) per-smurf work (variant index +
    // transform compose + one `SetInstanceTransform2D` interop). The MMI
    // children render afterwards: 12 instanced GPU draw calls cover the
    // entire colony body, regardless of how many smurfs are visible.
    private void UpdateMultiMeshInstances()
    {
        if (_maleMmi == null || _femaleMmi == null) return;

        // Reset per-variant counters.
        for (int i = 0; i < _moodCount; i++)
        {
            _maleInstanceCount  [i] = 0;
            _femaleInstanceCount[i] = 0;
        }

        // Compute the visible-world rect once (camera-aware cull); off-screen
        // smurfs don't get an instance allocated, so the GPU draws only
        // what the viewport can see.
        var rect = ComputeVisibleWorldRect();

        // Anchor adjustment: QuadMesh is centred at its origin, so to put
        // the sprite's logical anchor (SpriteAnchorX, SpriteAnchorY) at
        // the smurf's world position, translate by
        // (SpriteW/2 − SpriteAnchorX, SpriteH/2 − SpriteAnchorY).
        float anchorAdjX = SpriteW * 0.5f - SpriteAnchorX;
        float anchorAdjY = SpriteH * 0.5f - SpriteAnchorY;

        // v0.4.30 — yielding (lying-down) smurfs render with a vertical
        // squash so the player can see who's prone. Y-scale 0.35 and a
        // re-derived Y-anchor offset that keeps the squashed sprite's feet
        // at the smurf's world position (so the SmurfPos still aligns with
        // the tile the lying smurf occupies). Bobbing is suppressed so
        // they look still rather than convulsing on the ground.
        const float YieldYScale = 0.35f;
        // Same derivation as anchorAdjY but with the post-flip anchor row
        // SCALED: mesh.bottom = smurf.Pos.Y + (SpriteH-1-SpriteAnchorY) * YieldYScale.
        // Solving for anchorAdjY: SpriteH*0.5*YieldYScale - (SpriteH-1-SpriteAnchorY)*YieldYScale.
        // For SpriteH=24, SpriteAnchorY=16, scale=0.35: 12*0.35 - 7*0.35 = 1.75. Negate
        // because mesh.center sits ABOVE the smurf position (Y down).
        const float YieldAnchorAdjY = -1.75f;

        foreach (var s in _smurfs)
        {
            float bob = s.IsYielding ? 0f : Mathf.Sin(s.BobTimer) * 1.0f;
            var pos = new Vector2(
                s.Pos.X + anchorAdjX,
                s.Pos.Y + (s.IsYielding ? YieldAnchorAdjY : anchorAdjY) + bob);
            if (!rect.HasPoint(s.Pos)) continue;

            int moodIdx = (int)s.Mood;
            if (moodIdx < 0 || moodIdx >= _moodCount) moodIdx = (int)MoodState.Content;

            // Per-instance Transform2D — explicit X-axis / Y-axis / origin
            // form so we can apply the per-smurf yield squash without
            // touching the QuadMesh size. MultiMesh accepts arbitrary 2D
            // transforms per instance, so 250 standing + a few yielding
            // smurfs still batch into the same GPU draw.
            Transform2D xform = s.IsYielding
                ? new Transform2D(new Vector2(1f, 0f), new Vector2(0f, YieldYScale), pos)
                : new Transform2D(0f, pos);

            if (s.Sex == Sex.Female)
            {
                int idx = _femaleInstanceCount[moodIdx];
                if (idx >= MaxInstancesPerVariant) continue;
                _femaleMmi[moodIdx].Multimesh.SetInstanceTransform2D(idx, xform);
                _femaleInstanceCount[moodIdx] = idx + 1;
            }
            else
            {
                int idx = _maleInstanceCount[moodIdx];
                if (idx >= MaxInstancesPerVariant) continue;
                _maleMmi[moodIdx].Multimesh.SetInstanceTransform2D(idx, xform);
                _maleInstanceCount[moodIdx] = idx + 1;
            }
        }

        // Flush visible-instance counts so the GPU only renders the
        // populated portion of each buffer.
        for (int i = 0; i < _moodCount; i++)
        {
            _maleMmi  [i].Multimesh.VisibleInstanceCount = _maleInstanceCount  [i];
            _femaleMmi[i].Multimesh.VisibleInstanceCount = _femaleInstanceCount[i];
        }
    }

    // v0.4.20 — visible-world rect computed once per _Draw and consulted
    // by the per-smurf culling check. Avoids drawing smurfs that fall
    // outside the camera viewport (cheap when zoomed in, where the
    // vast majority of a 250-smurf colony is off-screen).
    private Rect2 _visibleWorldRect;

    public override void _Draw()
    {
        // v0.4.24 — `_Draw` handles the "below-body" selection pass — the
        // body itself is rendered by the 12 MultiMeshInstance2D children
        // immediately after this method returns. v0.4.47 — replaced the
        // yellow glow circle with RimWorld-style white corner brackets so
        // selection is identifiable on any background colour and matches
        // the tile-selection indicator used by `SelectionOverlay`.
        _visibleWorldRect = ComputeVisibleWorldRect();
        foreach (var s in _smurfs)
        {
            if (!s.Selected) continue;
            float bob = Mathf.Sin(s.BobTimer) * 1.0f;
            var pos = s.Pos + new Vector2(0, bob);
            if (!_visibleWorldRect.HasPoint(pos)) continue;
            DrawSelectionBrackets(this, new Rect2(pos.X - 10f, pos.Y - 14f, 20f, 22f));
        }
    }

    // v0.4.47 — RimWorld-style selection indicator. Four white L-shaped
    // corner brackets framing the given rect, with a thin black 1-px
    // shadow offset so the marks read on light backgrounds (sand /
    // marble / etc.) as well as dark ones. Shared so the colony view
    // (smurfs) and `SelectionOverlay` (tile / item inspector) draw the
    // same visual treatment.
    internal static void DrawSelectionBrackets(Godot.CanvasItem canvas, Rect2 rect)
    {
        const float armLen     = 4f;   // length of each L arm in px
        const float thickness  = 1f;
        var white  = new Color(1f, 1f, 1f, 0.95f);
        var shadow = new Color(0f, 0f, 0f, 0.55f);
        float left   = rect.Position.X;
        float top    = rect.Position.Y;
        float right  = rect.Position.X + rect.Size.X;
        float bottom = rect.Position.Y + rect.Size.Y;

        // Each corner: 1-px horizontal arm + 1-px vertical arm forming an L
        // that opens AWAY from the rect (i.e. the arms point INTO the rect
        // from the corner). Shadow drawn 1 px down-right of the white so
        // the brackets pop on both dark and light terrain.
        void HArm(float x, float y) {
            canvas.DrawRect(new Rect2(x + 1f, y + 1f, armLen, thickness), shadow);
            canvas.DrawRect(new Rect2(x,      y,      armLen, thickness), white);
        }
        void VArm(float x, float y) {
            canvas.DrawRect(new Rect2(x + 1f, y + 1f, thickness, armLen), shadow);
            canvas.DrawRect(new Rect2(x,      y,      thickness, armLen), white);
        }
        // Top-left.    Arms extend right and down from (left, top).
        HArm(left,                  top);
        VArm(left,                  top);
        // Top-right.   Arms extend left and down from (right-1, top).
        HArm(right - armLen,        top);
        VArm(right - thickness,     top);
        // Bottom-left. Arms extend right and up from (left, bottom-1).
        HArm(left,                  bottom - thickness);
        VArm(left,                  bottom - armLen);
        // Bottom-right. Arms extend left and up from (right-1, bottom-1).
        HArm(right - armLen,        bottom - thickness);
        VArm(right - thickness,     bottom - armLen);
    }

    // v0.4.24 — above-body pass invoked from `_extrasNode._Draw`. Equipment
    // overlays sit on the smurf's hands / torso / head and must render
    // *above* the body so a held weapon doesn't disappear behind a body
    // sprite. Same for the carry icon, combat sword glyph, and the
    // selected-smurf name label.
    internal void DrawAllExtras()
    {
        _visibleWorldRect = ComputeVisibleWorldRect();
        foreach (var s in _smurfs)
            DrawSmurfExtras(s);
    }

    private void DrawSmurfExtras(VisualSmurf s)
    {
        float bob = Mathf.Sin(s.BobTimer) * 1.0f;
        var pos = s.Pos + new Vector2(0, bob);
        if (!_visibleWorldRect.HasPoint(pos)) return;

        // Combat indicator (sword glyph) — sparse: only smurfs in active
        // combat have it, almost always zero in idle gameplay.
        if (s.CombatTargetName != null)
        {
            var swordPos = pos + new Vector2(-3f, -19f);
            _extrasNode?.DrawString(ThemeDB.FallbackFont, swordPos, "⚔",
                HorizontalAlignment.Left, -1, 12,
                new Color(0.95f, 0.25f, 0.25f, 0.95f));
        }

        // Resolve carry slot first so the equipment overlay skips the
        // hand currently holding the haul-carry icon.
        string? carrySlot = (s.CarriedKind != null && s.CarriedSubType != null)
            ? ResolveCarrySlot(s) : null;

        // Equipment overlays (per slot). Sparse — most smurfs have no
        // equipment, so this is mostly a `null` early-out.
        if (s.Equipment != null)
        {
            foreach (var (slotName, payload) in s.Equipment)
            {
                if (slotName == carrySlot) continue;
                DrawEquipmentSlotOn(_extrasNode!, pos, slotName, payload.Kind, payload.SubType);
            }
        }

        if (carrySlot != null)
        {
            bool isLeft = carrySlot == "LeftHand";
            var handPos = pos + new Vector2(isLeft ? -5.5f : 5.5f, 2.5f);
            DrawCarriedIconOn(_extrasNode!, handPos, s.CarriedKind!, s.CarriedSubType!, s.CarriedMaterialFamily);
        }

        // v0.4.28 — name label above the hat (was 9 px below pos, which
        // overlapped the body and read as "label attached to feet").
        // Sprite top-of-hat is at pos.Y - 16; baseline at pos.Y - 19
        // puts the text two pixels above the hat tip with a clear gap.
        //
        // v0.4.28b — pre-measure the text width and shift x by -width/2
        // so the label is genuinely centred on the smurf. Godot's
        // DrawString silently ignores HorizontalAlignment.Center when the
        // `width` arg is -1 (no constraint) and falls back to left-anchor
        // — so the previous "Center, -1, 9" call was actually drawing
        // names left-anchored at pos.X, leaving every name visibly offset
        // to the right of its smurf.
        var nameFont = ThemeDB.FallbackFont;
        const int nameSize = 9;
        var nameMetrics = nameFont.GetStringSize(s.Name, HorizontalAlignment.Left, -1, nameSize);
        _extrasNode?.DrawString(nameFont,
            pos + new Vector2(-nameMetrics.X * 0.5f, -19f),
            s.Name, HorizontalAlignment.Left, -1, nameSize,
            new Color(0.95f, 0.92f, 0.72f, 0.85f));
    }

    private Rect2 ComputeVisibleWorldRect()
    {
        // Walk up to find the Camera2D (GameController parents one on the
        // viewport). Fall back to "whole map" if no camera is bound.
        var cam = GetViewport()?.GetCamera2D();
        if (cam == null) return new Rect2(Vector2.Zero, _mapSize);
        var vpSize = GetViewport()!.GetVisibleRect().Size;
        var zoom   = cam.Zoom;
        if (zoom.X <= 0.0001f || zoom.Y <= 0.0001f) zoom = Vector2.One;
        var worldSize   = new Vector2(vpSize.X / zoom.X, vpSize.Y / zoom.Y);
        var worldCentre = cam.GetScreenCenterPosition();
        // Grow the rect by one sprite worth on each side so smurfs whose
        // centre is just outside the viewport but whose hat tip would be
        // visible still get drawn.
        return new Rect2(worldCentre - worldSize * 0.5f, worldSize)
            .Grow(SpriteH);
    }

    // ── Drawing ────────────────────────────────────────────────────────────────

    // v0.4.24 — `DrawSmurf` removed. The body sprite is now rendered by
    // the 12 `MultiMeshInstance2D` children (GPU-instanced); the
    // remaining per-smurf canvas commands (selection glow, equipment,
    // carry, combat indicator, name label) live in `_Draw` (below-body
    // pass) and `DrawAllExtras` (above-body pass, via `_extrasNode`).

    // v0.4.5 — picks the EquipSlot the haul-carry occupies for visual
    // purposes. Mirrors the v0.4.4 hand-resolution logic in
    // SimulationManager.ResolveEquipSlot for Hand-class items:
    //   - Dominant hand if it's free.
    //   - Off-hand if the dominant is occupied.
    //   - Dominant hand as a fallback when both hands are occupied
    //     (the carry visually replaces the dominant equipment for
    //     the haul duration). The renderer suppresses the equipment
    //     overlay for whichever hand this returns so the icons don't
    //     stack.
    private static string ResolveCarrySlot(VisualSmurf s)
    {
        string dom = s.Handedness == Handedness.Left ? "LeftHand" : "RightHand";
        string off = s.Handedness == Handedness.Left ? "RightHand" : "LeftHand";
        bool domOccupied = s.Equipment != null && s.Equipment.ContainsKey(dom);
        bool offOccupied = s.Equipment != null && s.Equipment.ContainsKey(off);
        if (!domOccupied) return dom;
        if (!offOccupied) return off;
        return dom;   // both full → carry replaces dominant
    }

    // v0.4.4 — per-slot equipment overlay. Each slot draws a small
    // decoration at a slot-specific offset relative to the smurf
    // sprite. Hand slots draw a stub tool/weapon icon; Torso draws
    // a body-band tint; Head draws a hat overlay; Feet draw boot
    // dots. Colour palette per slot kind keeps the visual quick to
    // read at 1× zoom.
    // v0.4.24 — helpers now take a `CanvasItem ci` target so they can draw
    // onto the dedicated extras child node (rendered ABOVE the MultiMesh
    // body) instead of always recording to SmurfColonyView's own canvas
    // commands. The body sprite is rendered by the MMI children that sit
    // between SmurfColonyView and `_extrasNode` in the tree, so equipment,
    // tools, carry icons, and name labels need to live on `_extrasNode`
    // to appear on top of the body.
    private static void DrawEquipmentSlotOn(CanvasItem ci, Vector2 pos, string slotName, string kind, string subType)
    {
        switch (slotName)
        {
            case "Head":
                // Hat overlays the existing pointed cap with a band of
                // colour at the brim line.
                ci.DrawRect(new Rect2(pos.X - 4f, pos.Y - 5f, 8f, 1.5f),
                    new Color(0.85f, 0.55f, 0.30f, 0.90f));
                break;
            case "Torso":
                // Apparel band across the torso.
                ci.DrawRect(new Rect2(pos.X - 5f, pos.Y + 1f, 10f, 2f),
                    new Color(0.65f, 0.45f, 0.30f, 0.85f));
                break;
            case "LeftHand":
                DrawHandItemOn(ci, pos + new Vector2(-5.5f, 2.5f), kind, subType);
                break;
            case "RightHand":
                DrawHandItemOn(ci, pos + new Vector2(5.5f, 2.5f), kind, subType);
                break;
            case "LeftFoot":
                ci.DrawRect(new Rect2(pos.X - 4f, pos.Y + 6f, 3f, 2f),
                    new Color(0.45f, 0.30f, 0.20f, 0.95f));
                break;
            case "RightFoot":
                ci.DrawRect(new Rect2(pos.X + 1f, pos.Y + 6f, 3f, 2f),
                    new Color(0.45f, 0.30f, 0.20f, 0.95f));
                break;
            case "LeftArm":
                ci.DrawRect(new Rect2(pos.X - 6f, pos.Y + 0.5f, 2f, 4f),
                    new Color(0.55f, 0.40f, 0.25f, 0.85f));
                break;
            case "RightArm":
                ci.DrawRect(new Rect2(pos.X + 4f, pos.Y + 0.5f, 2f, 4f),
                    new Color(0.55f, 0.40f, 0.25f, 0.85f));
                break;
            case "LeftLeg":
                ci.DrawRect(new Rect2(pos.X - 3f, pos.Y + 4f, 2f, 3f),
                    new Color(0.50f, 0.35f, 0.20f, 0.85f));
                break;
            case "RightLeg":
                ci.DrawRect(new Rect2(pos.X + 1f, pos.Y + 4f, 2f, 3f),
                    new Color(0.50f, 0.35f, 0.20f, 0.85f));
                break;
        }
    }

    // Hand-held tool / weapon. Tool = small brown dot. Weapon = small
    // grey vertical bar to suggest a haft. Phase 7 combat will replace
    // with per-weapon glyphs.
    private static void DrawHandItemOn(CanvasItem ci, Vector2 at, string kind, string subType)
    {
        if (kind == "Weapon")
        {
            ci.DrawRect(new Rect2(at.X - 0.5f, at.Y - 2.5f, 1.5f, 5f),
                new Color(0.85f, 0.85f, 0.85f, 0.90f));
            ci.DrawRect(new Rect2(at.X - 1.0f, at.Y + 2.5f, 2.5f, 1f),
                new Color(0.55f, 0.40f, 0.25f, 0.95f));
        }
        else if (kind == "Tool")
        {
            ci.DrawCircle(at, 1.6f, new Color(0.65f, 0.45f, 0.20f, 0.95f));
            ci.DrawCircle(at, 1.6f, new Color(0.30f, 0.20f, 0.10f, 0.95f), filled: false, width: 0.6f);
        }
    }

    // v0.4.2 — hand-held item visual. Palette mirrors ItemDropOverlay
    // so a Smurfberry stack picked up off the ground reads as the
    // same colour while being carried. ~2.5 px radius — sits in the
    // smurf's outstretched-hand position relative to the body centre.
    private static void DrawCarriedIconOn(CanvasItem ci, Vector2 at, string kind, string subType, string? matFamily)
    {
        Color fill, edge;
        if (kind == "Food")
        {
            (fill, edge) = subType switch
            {
                "Smurfberry"    => (new Color(0.55f, 0.30f, 0.85f), new Color(0.20f, 0.10f, 0.40f)),
                "SmallMushroom" => (new Color(0.85f, 0.45f, 0.35f), new Color(0.40f, 0.18f, 0.15f)),
                "HerbCluster"   => (new Color(0.60f, 0.85f, 0.40f), new Color(0.20f, 0.40f, 0.15f)),
                "MagicBerry"    => (new Color(0.85f, 0.50f, 0.95f), new Color(0.40f, 0.18f, 0.50f)),
                _               => (new Color(0.85f, 0.65f, 0.30f), new Color(0.40f, 0.30f, 0.10f)),
            };
        }
        else if (kind == "Material")
        {
            (fill, edge) = matFamily switch
            {
                "Wood"  => (new Color(0.56f, 0.40f, 0.24f), new Color(0.25f, 0.18f, 0.10f)),
                "Stone" => (new Color(0.62f, 0.62f, 0.68f), new Color(0.30f, 0.30f, 0.34f)),
                "Plant" => (new Color(0.42f, 0.58f, 0.30f), new Color(0.18f, 0.26f, 0.12f)),
                _       => (new Color(0.55f, 0.45f, 0.30f), new Color(0.25f, 0.20f, 0.10f)),
            };
        }
        else if (kind == "Magic")
        {
            (fill, edge) = subType switch
            {
                "RawEssence"   => (new Color(0.55f, 0.85f, 1.00f), new Color(0.20f, 0.40f, 0.60f)),
                "CrystalShard" => (new Color(0.85f, 0.55f, 1.00f), new Color(0.40f, 0.20f, 0.50f)),
                _              => (new Color(0.65f, 0.85f, 1.00f), new Color(0.25f, 0.40f, 0.60f)),
            };
        }
        else
        {
            fill = new Color(0.80f, 0.80f, 0.80f);
            edge = new Color(0.30f, 0.30f, 0.30f);
        }

        if (kind == "Material")
        {
            ci.DrawRect(new Rect2(at.X - 1.5f, at.Y - 1.5f, 3f, 3f), fill);
            ci.DrawRect(new Rect2(at.X - 1.5f, at.Y - 1.5f, 3f, 3f), edge, false, 0.8f);
        }
        else
        {
            ci.DrawCircle(at, 2.2f, fill);
            ci.DrawArc(at, 2.2f, 0, Mathf.Tau, 8, edge, 0.8f, true);
        }
    }

    // ── Public API for GameController-driven input dispatch (v0.3.24) ─────────

    // Returns the name of the smurf whose visual position is within `radius` of
    // the given world-space pixel, or null. Used by GameController to detect
    // single-click smurf selection.
    public string? GetSmurfNameAt(Vector2 worldPos, float radius = 10f)
    {
        foreach (var s in _smurfs)
            if (s.Pos.DistanceTo(worldPos) <= radius) return s.Name;
        return null;
    }

    // Names of every smurf whose visual position is inside the given world-
    // space rectangle. Used by RTS-style box-select on left-drag.
    public System.Collections.Generic.List<string> GetSmurfNamesInRect(Rect2 worldRect)
    {
        var result = new System.Collections.Generic.List<string>();
        foreach (var s in _smurfs)
            if (worldRect.HasPoint(s.Pos)) result.Add(s.Name);
        return result;
    }

    // World-pixel position of a named smurf, or null if not found. Used by
    // SmurfRosterPanel's "double-click to zoom camera" path.
    public Vector2? GetSmurfPosition(string name)
    {
        foreach (var s in _smurfs)
            if (s.Name == name) return s.Pos;
        return null;
    }

    // Replaces the selection set on every VisualSmurf so the yellow ring
    // matches the GameController-side `_selectedSmurfs` HashSet.
    public void SetSelection(System.Collections.Generic.ICollection<string> selected)
    {
        foreach (var s in _smurfs)
            s.Selected = selected.Contains(s.Name);
    }

    // Returns the names of every alive smurf currently tracked. Used by the
    // Smurfs roster tab to populate its list.
    public System.Collections.Generic.List<string> GetAllSmurfNames()
    {
        var result = new System.Collections.Generic.List<string>(_smurfs.Count);
        foreach (var s in _smurfs) result.Add(s.Name);
        return result;
    }

    // Single-smurf selection emit hook kept for back-compat — `_UnhandledInput`
    // was previously firing it; GameController now calls it explicitly on
    // single-click detection, so the card panel still opens on smurf-click.
    public void EmitSmurfClicked(string name) =>
        EmitSignal(SignalName.SmurfClicked, name);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private Vector2 RandomPos() =>
        new(_rng.RandfRange(80f, _mapSize.X - 80f),
            _rng.RandfRange(80f, _mapSize.Y - 80f));

    private static Color MoodColor(MoodState mood) => mood switch
    {
        MoodState.Inspired   => new Color(0.30f, 0.60f, 1.00f),
        MoodState.Content    => new Color(0.25f, 0.50f, 0.90f),
        MoodState.Stressed   => new Color(0.50f, 0.45f, 0.80f),
        MoodState.Distressed => new Color(0.65f, 0.35f, 0.60f),
        MoodState.Breaking   => new Color(0.75f, 0.25f, 0.35f),
        MoodState.Collapse   => new Color(0.40f, 0.20f, 0.20f),
        _                    => new Color(0.30f, 0.55f, 0.90f),
    };

    private static Color MoodDotColor(MoodState mood) => mood switch
    {
        MoodState.Inspired   => Colors.LimeGreen,
        MoodState.Content    => Colors.YellowGreen,
        MoodState.Stressed   => Colors.Yellow,
        MoodState.Distressed => Colors.Orange,
        MoodState.Breaking   => Colors.OrangeRed,
        MoodState.Collapse   => Colors.Red,
        _                    => Colors.Gray,
    };

    // ── v0.4.20 — pre-baked sprite cache ───────────────────────────────────────
    //
    // The previous DrawSmurf path issued ~10 procedural primitive calls
    // (DrawCircle × ~7, DrawPolygon × 2, DrawString × 1) per smurf. At
    // 250 smurfs that's ~3 000 canvas commands plus 250 text-shaping
    // operations every redraw — and even when the sim is paused, Godot's
    // 2D renderer still re-walks the cached canvas commands every frame.
    // Per-smurf colour variation (mood × sex) broke draw-call batching,
    // so every command shipped on its own.
    //
    // Now: bake one `ImageTexture` per (mood × sex) variant at first
    // _Draw, then issue a single `DrawTexture` per smurf. With 12
    // variants total (6 mood states × 2 sexes), every smurf in the same
    // category batches into a single GPU draw call. The texture-mapped
    // quad is a trivial GPU primitive, far cheaper than the CPU-side
    // canvas-command churn the procedural path produced.
    //
    // Equipment overlays, carry icon, selection glow, combat indicator,
    // and name label stay as procedural draws — they're sparse (most
    // smurfs don't carry anything, only one is "selected" at a time)
    // and per-instance variable so baking variants would explode the
    // cache without proportional savings.
    private const int SpriteW       = 16;
    private const int SpriteH       = 24;
    private const int SpriteAnchorX = 8;
    private const int SpriteAnchorY = 16;   // smurf's logical pos lives at sprite-local (8, 16)

    private static ImageTexture[]? _maleSprites;
    private static ImageTexture[]? _femaleSprites;
    private static readonly int _moodCount = System.Enum.GetValues(typeof(MoodState)).Length;

    private static void EnsureSprites()
    {
        if (_maleSprites != null) return;
        _maleSprites   = new ImageTexture[_moodCount];
        _femaleSprites = new ImageTexture[_moodCount];
        foreach (MoodState mood in System.Enum.GetValues(typeof(MoodState)))
        {
            _maleSprites  [(int)mood] = BakeSmurfSprite(mood, Sex.Male);
            _femaleSprites[(int)mood] = BakeSmurfSprite(mood, Sex.Female);
        }
    }

    private static ImageTexture BakeSmurfSprite(MoodState mood, Sex sex)
    {
        var img = Image.CreateEmpty(SpriteW, SpriteH, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));   // transparent

        Color body  = MoodColor(mood);
        Color white = Colors.White;
        Color black = Colors.Black;
        Color hatCol = sex == Sex.Female ? new Color(1f, 0.55f, 0.75f) : Colors.White;

        // Translate the sprite-relative offsets that DrawSmurf used to use
        // (where `pos` was the smurf's logical anchor) into sprite-local
        // coordinates: spriteX = anchorX + offsetX, spriteY = anchorY + offsetY.
        int cx = SpriteAnchorX;
        int cy = SpriteAnchorY;

        // Hat triangle — drawn first so the head's pixels paint over the
        // bottom edge if they overlap (matches the old procedural order
        // where polygons drew underneath circles).
        FillTriangleOnImage(img,
            new Vector2(cx,           cy - 12),    // tip
            new Vector2(cx - 4,       cy - 5.5f),  // left base
            new Vector2(cx + 4.5f,    cy - 5.5f),  // right base
            hatCol);

        // Body (lower torso) + head (upper).
        FillCircleOnImage(img, cx,           cy + 3,      3.5f, body);
        FillCircleOnImage(img, cx,           cy - 1,      5.0f, body);

        // Eyes — white sclera, black pupils.
        FillCircleOnImage(img, cx - 2,       cy - 2,      1.8f, white);
        FillCircleOnImage(img, cx + 2,       cy - 2,      1.8f, white);
        FillCircleOnImage(img, cx - 2,       cy - 1,      1.0f, black);
        FillCircleOnImage(img, cx + 2,       cy - 1,      1.0f, black);

        // Female blonde hair under the hat brim.
        if (sex == Sex.Female)
        {
            var blonde = new Color(0.95f, 0.82f, 0.30f);
            FillCircleOnImage(img, cx - 5, cy - 3, 2.0f, blonde);
            FillCircleOnImage(img, cx - 4, cy - 1, 1.8f, blonde);
            FillCircleOnImage(img, cx + 5, cy - 3, 2.0f, blonde);
            FillCircleOnImage(img, cx + 4, cy - 1, 1.8f, blonde);
        }

        // Male hat mood-dot.
        if (sex != Sex.Female)
        {
            FillCircleOnImage(img, cx, cy - 13, 1.5f, MoodDotColor(mood));
        }

        // v0.4.28 — small dark feet at the very bottom of the sprite.
        // Without these the figure was a hat-on-head-with-bump shape that
        // could read as upside-down at low zoom (Sam's report). The dark
        // feet unambiguously anchor the figure as "head up, feet down"
        // and frame the white pants area between body and ground.
        var feetCol = new Color(0.10f, 0.08f, 0.16f, 1f);
        for (int fx = cx - 2; fx <= cx - 1; fx++)
        for (int fy = cy + 6; fy <= cy + 7; fy++)
            if ((uint)fx < (uint)SpriteW && (uint)fy < (uint)SpriteH)
                img.SetPixel(fx, fy, feetCol);
        for (int fx = cx + 1; fx <= cx + 2; fx++)
        for (int fy = cy + 6; fy <= cy + 7; fy++)
            if ((uint)fx < (uint)SpriteW && (uint)fy < (uint)SpriteH)
                img.SetPixel(fx, fy, feetCol);

        // v0.4.28b — Godot's QuadMesh inherits PrimitiveMesh's 3D Y-up
        // UV convention, which renders 2D textures Y-flipped relative to
        // Image's top-left-origin coordinate system. Without this flip
        // the smurf appeared upside down in-game (Sam's report). Flipping
        // the baked image cancels the rendering flip, so the figure
        // renders right-side up. Anchor offset (~1 px) is within margin
        // and invisible at gameplay zoom.
        img.FlipY();

        var tex = ImageTexture.CreateFromImage(img);
        return tex;
    }

    // Image-level scan-fill circle (cheap; runs once per variant at first draw).
    private static void FillCircleOnImage(Image img, int cx, int cy, float r, Color col)
    {
        int ir = (int)System.Math.Ceiling(r);
        float r2 = r * r;
        int w = img.GetWidth();
        int h = img.GetHeight();
        for (int dy = -ir; dy <= ir; dy++)
        {
            int py = cy + dy;
            if ((uint)py >= (uint)h) continue;
            float dy2 = dy * dy;
            if (dy2 > r2) continue;
            int halfW = (int)System.Math.Floor(System.Math.Sqrt(r2 - dy2));
            int xLo = System.Math.Max(0, cx - halfW);
            int xHi = System.Math.Min(w - 1, cx + halfW);
            for (int px = xLo; px <= xHi; px++)
                img.SetPixel(px, py, col);
        }
    }

    // Image-level scan-fill triangle (point-up isoceles for the hat).
    private static void FillTriangleOnImage(Image img, Vector2 a, Vector2 b, Vector2 c, Color col)
    {
        float yMin = System.Math.Min(a.Y, System.Math.Min(b.Y, c.Y));
        float yMax = System.Math.Max(a.Y, System.Math.Max(b.Y, c.Y));
        int   y0   = System.Math.Max(0, (int)System.Math.Floor(yMin));
        int   y1   = System.Math.Min(img.GetHeight() - 1, (int)System.Math.Ceiling(yMax));
        int   w    = img.GetWidth();
        for (int y = y0; y <= y1; y++)
        {
            // Find leftmost / rightmost x of the triangle at this y by
            // intersecting the three edges.
            float xLo = float.PositiveInfinity, xHi = float.NegativeInfinity;
            ProjectEdgeX(a, b, y, ref xLo, ref xHi);
            ProjectEdgeX(b, c, y, ref xLo, ref xHi);
            ProjectEdgeX(c, a, y, ref xLo, ref xHi);
            if (xLo > xHi) continue;
            int ix0 = System.Math.Max(0, (int)System.Math.Floor(xLo));
            int ix1 = System.Math.Min(w - 1, (int)System.Math.Ceiling(xHi));
            for (int x = ix0; x <= ix1; x++) img.SetPixel(x, y, col);
        }
    }

    private static void ProjectEdgeX(Vector2 p0, Vector2 p1, int y, ref float xLo, ref float xHi)
    {
        float y0 = p0.Y, y1 = p1.Y;
        if ((y < System.Math.Min(y0, y1)) || (y > System.Math.Max(y0, y1))) return;
        if (System.Math.Abs(y1 - y0) < 0.001f)
        {
            xLo = System.Math.Min(xLo, System.Math.Min(p0.X, p1.X));
            xHi = System.Math.Max(xHi, System.Math.Max(p0.X, p1.X));
            return;
        }
        float t = (y - y0) / (y1 - y0);
        float x = p0.X + (p1.X - p0.X) * t;
        if (x < xLo) xLo = x;
        if (x > xHi) xHi = x;
    }
}
