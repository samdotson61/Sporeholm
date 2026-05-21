using Godot;
using Sporeholm.World;

namespace Sporeholm.UI
{
    // v0.5.19 (Phase 5B — Roadmap §5.1 + §5.2). Renders the per-tile
    // StructureSlot data: walls (impassable, dark grey/brown), floors
    // (passable, light wood/stone tint), and blueprint ghosts (translucent
    // white). Mirrors v0.5.0 StockpileOverlay's MMI-with-throttled-rebuild
    // pattern. Subscribes to LocalMap.StructureChanged for invalidation;
    // rebuilds at most every 200 ms.
    //
    // v0.5.19 ships with three sprite kinds — Wall, Floor, Blueprint —
    // each with its own MultiMeshInstance2D. v0.5.20+ extensions (Door,
    // Furniture, Workbench, etc.) add additional sprite kinds without
    // touching the dispatch logic; just add the kind to the StructureSlot
    // enum + the bake table here.
    public partial class StructureOverlay : Node2D
    {
        private LocalMap? _map;
        private const int TS = LocalMap.TileSize;

        // v0.5.84o — autotile wall MMIs. RimWorld-style "linked atlas":
        // 16 sprite variants per material based on a 4-bit cardinal-
        // neighbour mask (N=1, E=2, S=4, W=8). Each variant paints edge
        // caps only on sides that DON'T have a same-family wall neighbour.
        // A wall with all 4 neighbours (mask 15) is a pure-interior tile
        // with no caps; a solo wall (mask 0) gets caps on all 4 sides.
        // Pre-fix used one MMI per material (mask-blind); adjacent walls
        // blended via the v0.5.84d edge-trim approach but lone walls and
        // corner walls read identically to long-run walls. Now corners +
        // T-junctions + endcaps + pillars all render distinctly.
        private MultiMeshInstance2D[] _wallMmis       = new MultiMeshInstance2D[16];
        private MultiMeshInstance2D[] _wallFungalMmis = new MultiMeshInstance2D[16];
        private MultiMeshInstance2D[] _wallWoodMmis   = new MultiMeshInstance2D[16];
        private MultiMeshInstance2D _floorMmi     = null!;
        private MultiMeshInstance2D _floorFungalMmi = null!; // v0.5.70 — FungalWood floors (spore-pad sprite)
        private MultiMeshInstance2D _doorMmi      = null!;   // v0.5.20
        private MultiMeshInstance2D _doorFungalMmi = null!;  // v0.5.70 — FungalWood doors (cap-and-stem sprite)
        private MultiMeshInstance2D _shelfMmi     = null!;   // v0.5.21
        private MultiMeshInstance2D _workbenchMmi = null!;   // v0.5.25 (was missing)
        private MultiMeshInstance2D _bonfireMmi    = null!;   // v0.5.25 (was missing)
        private MultiMeshInstance2D _bedMmi       = null!;   // v0.5.35
        private MultiMeshInstance2D _shrineMmi    = null!;   // v0.5.36
        private MultiMeshInstance2D _boardMmi     = null!;   // v0.5.36
        private MultiMeshInstance2D _benchMmi     = null!;   // v0.5.36
        private MultiMeshInstance2D _tableMmi     = null!;   // v0.5.37
        private MultiMeshInstance2D _torchMmi     = null!;   // v0.5.84t
        private MultiMeshInstance2D _cookingTableMmi = null!; // v0.6.2 (Phase 5.6)
        private MultiMeshInstance2D _blueprintMmi = null!;
        // v0.6.2 — Demolish-as-task. Red X overlay drawn over any built
        // structure whose StructureSlot.MarkedForDemolition flag is set.
        // Lives in its own MMI so the demolish marker can stack on top of
        // the existing structure sprite (player still sees the wall/door/
        // furniture being marked, with a clear destruction indicator).
        private MultiMeshInstance2D _demolishMarkMmi = null!;
        // v0.5.84d/v0.5.84o — wood-family wall MMI array (16 autotile variants).
        // v0.5.84e — wood-family floor MMI. Sam: "Flooring made from wood
        // types/fungalwood should have subtle plank lines." Pre-fix all
        // floors used the flat `_floorMmi` regardless of material — wood
        // floors tinted brown read as solid color, not planks. New
        // dedicated plank sprite routed via material family.
        private MultiMeshInstance2D _floorWoodMmi = null!;
        private const int MaxInstances = 16000;

        private readonly System.Collections.Generic.HashSet<(int X, int Y)> _dirtyTiles = new();
        private readonly object _dirtyLock = new();
        private const double MinRefreshIntervalSec = 0.20;
        private double _timeSinceRefresh = 1.0;

        // v0.6.0 — door-opening animation cache. Populated by
        // RebuildInstances; one entry per rendered door (regular + fungal)
        // so UpdateDoorAnimation can update each door's MMI per-instance
        // Transform2D every frame without rebuilding the full structure
        // overlay. Per-door openness ∈ [0, 1] lerps toward 1.0 when a
        // shroomp is within DoorOpenRadiusPx of the door centre, else
        // toward 0. Door MMI scale.x = 1 − 0.78 × openness, so a fully
        // open door visually compresses to ~22 % width (looks like the
        // door has swung aside). Pure visual — has no effect on sim
        // pathing or room detection.
        private struct DoorRenderRef
        {
            public int TileKey;            // y * Width + x for openness lookup
            public Vector2 Centre;
            public MultiMeshInstance2D Mmi;
            public int InstanceIdx;
            public Color Tint;
        }
        private readonly System.Collections.Generic.List<DoorRenderRef> _doorRefs = new();
        // openness keyed by tile-linear-index (y * Width + x); persists across
        // rebuilds so the door state survives the 200 ms structure-overlay
        // throttle window without snapping closed.
        private readonly System.Collections.Generic.Dictionary<int, float> _doorOpenness = new();
        private const float DoorOpenRadiusPx = 14f;   // half a tile + a little
        private const float DoorOpenLerpUp   = 12f;   // per-second rate toward 1.0
        private const float DoorOpenLerpDown = 6f;    // per-second rate toward 0.0 (slower close = more visible)
        // v0.6.0 — supplied each frame by GameController so the door
        // animation can react to live shroomp positions without the
        // overlay needing its own SimulationManager handle.
        private System.Collections.Generic.IReadOnlyList<Sporeholm.Simulation.ShroompSnapshot>? _shroompsForDoorAnim;

        // v0.5.71 — sub-layer for floor MMIs only. Sits at relative
        // ZIndex=-1 so floors render BELOW StockpileOverlay (z=0). Sam
        // screenshot: brown floor squares covered the green stockpile
        // zone tint; the player needs to see which floored tiles are
        // part of which stockpile. Walls / doors / blueprints / furniture
        // stay at the StructureOverlay's z=0 so they keep rendering
        // ABOVE stockpile (which is what we want — a wall painted across
        // a stockpile area should occlude the tint, not the other way).
        // Pairs with GameController setting MapRenderer.ZIndex = -2 so
        // the terrain still sits BELOW floors.
        private Node2D _floorLayer = null!;

        public override void _Ready()
        {
            TextureFilter = TextureFilterEnum.Nearest;
            // v0.5.6 ZIndex parity (StockpileOverlay fix): same z=0 baseline,
            // ordered by tree position. GameController must add this overlay
            // AFTER MapRenderer + StockpileOverlay but BEFORE designations
            // / items / selection so structures sit on the floor under the
            // designation glyphs but over the map texture.
            ZIndex = 0;

            // v0.5.71 — child layer holding the floor MMIs only, at
            // relative z=-1 so floors sit under the StockpileOverlay
            // while walls/doors/furniture in this overlay stay above it.
            _floorLayer = new Node2D
            {
                Name          = "FloorSubLayer",
                ZIndex        = -1,
                ZAsRelative   = true,
                TextureFilter = TextureFilterEnum.Nearest,
            };
            AddChild(_floorLayer);

            var quad = new QuadMesh { Size = new Vector2(TS, TS) };
            // v0.5.84o — 16 autotile variants per wall family.
            for (int m = 0; m < 16; m++)
            {
                _wallMmis      [m] = CreateMmi(quad, BakeWallSpriteStone (m));
                _wallFungalMmis[m] = CreateMmi(quad, BakeWallSpriteFungal(m));
                _wallWoodMmis  [m] = CreateMmi(quad, BakeWallSpriteWood  (m));
            }
            _floorMmi       = CreateMmi(quad, BakeFloorSprite(),         _floorLayer);
            _floorFungalMmi = CreateMmi(quad, BakeFloorSpriteFungal(),   _floorLayer);   // v0.5.70
            _floorWoodMmi   = CreateMmi(quad, BakeFloorSpriteWood(),     _floorLayer);   // v0.5.84e
            _doorMmi        = CreateMmi(quad, BakeDoorSprite());          // v0.5.20
            _doorFungalMmi  = CreateMmi(quad, BakeDoorSpriteFungal());    // v0.5.70
            _shelfMmi       = CreateMmi(quad, BakeShelfSprite());         // v0.5.21
            _workbenchMmi   = CreateMmi(quad, BakeWorkbenchSprite());     // v0.5.25
            _bonfireMmi      = CreateMmi(quad, BakeBonfireSprite());        // v0.5.25
            _bedMmi         = CreateMmi(quad, BakeBedSprite());           // v0.5.35
            _shrineMmi      = CreateMmi(quad, BakeShrineSprite());        // v0.5.36
            _boardMmi       = CreateMmi(quad, BakeBoardSprite());         // v0.5.36
            _benchMmi       = CreateMmi(quad, BakeBenchSprite());         // v0.5.36
            _tableMmi       = CreateMmi(quad, BakeTableSprite());         // v0.5.37
            _torchMmi       = CreateMmi(quad, BakeTorchSprite());         // v0.5.84t
            _cookingTableMmi = CreateMmi(quad, BakeCookingTableSprite()); // v0.6.2 (Phase 5.6)
            _blueprintMmi   = CreateMmi(quad, BakeBlueprintSprite());
            _demolishMarkMmi = CreateMmi(quad, BakeDemolishMarkSprite()); // v0.6.2 — demolish-as-task
        }

        public void SetMap(LocalMap map)
        {
            if (_map != null) _map.StructureChanged -= OnStructureChanged;
            _map = map;
            _map.StructureChanged += OnStructureChanged;
            // Force a refresh on bind via the v0.4.56 sentinel pattern.
            lock (_dirtyLock) _dirtyTiles.Add((-1, -1));
            _timeSinceRefresh = MinRefreshIntervalSec;
        }

        public override void _ExitTree()
        {
            if (_map != null) _map.StructureChanged -= OnStructureChanged;
        }

        private void OnStructureChanged(int x, int y)
        {
            lock (_dirtyLock) _dirtyTiles.Add((x, y));
        }

        public override void _Process(double delta)
        {
            _timeSinceRefresh += delta;
            if (_map == null) return;
            // v0.6.0 — door open/close animation runs every frame, not on
            // the structure-rebuild throttle. Cheap (O(doors × shroomps);
            // typical map: ≤ 30 doors × ≤ 60 shroomps = ~1800 dist² ops).
            UpdateDoorAnimationTick(delta);
            bool hasDirty;
            lock (_dirtyLock) hasDirty = _dirtyTiles.Count > 0;
            if (!hasDirty) return;
            if (_timeSinceRefresh < MinRefreshIntervalSec) return;

            lock (_dirtyLock) _dirtyTiles.Clear();
            _timeSinceRefresh = 0;

            RebuildInstances();
        }

        // v0.6.0 — supplies the latest snapshot's shroomp positions for the
        // door-opening animation. GameController calls this every snapshot
        // tick. Reference held weakly (just the list); no copy.
        public void SetShroompPositionsForDoorAnim(
            System.Collections.Generic.IReadOnlyList<Sporeholm.Simulation.ShroompSnapshot> shroomps)
        {
            _shroompsForDoorAnim = shroomps;
        }

        // v0.6.0 — per-frame door animation tick. For each cached door,
        // compute target openness (1.0 if any shroomp is inside
        // DoorOpenRadiusPx of the door centre, else 0.0). Lerp current
        // openness toward target with a separate up-rate vs down-rate so the
        // door snaps open quickly when the shroomp arrives but closes
        // gracefully after they leave. Apply the openness as scale.x on the
        // door MMI's per-instance Transform2D — a fully-open door visually
        // compresses to ~22 % width with a small slide-aside translation so
        // it reads as "swung open" rather than "shrunken."
        private void UpdateDoorAnimationTick(double delta)
        {
            if (_doorRefs.Count == 0) return;
            float dt = (float)delta;
            var shroomps = _shroompsForDoorAnim;
            float r2 = DoorOpenRadiusPx * DoorOpenRadiusPx;
            for (int i = 0; i < _doorRefs.Count; i++)
            {
                var dref = _doorRefs[i];
                // Compute target openness from proximity. Cheap squared-dist
                // check; no sqrt.
                float target = 0f;
                if (shroomps != null)
                {
                    for (int j = 0; j < shroomps.Count; j++)
                    {
                        var p = shroomps[j].SimPos;
                        float dx = p.X - dref.Centre.X;
                        float dy = p.Y - dref.Centre.Y;
                        if (dx * dx + dy * dy <= r2) { target = 1f; break; }
                    }
                }
                _doorOpenness.TryGetValue(dref.TileKey, out float openness);
                float rate = target > openness ? DoorOpenLerpUp : DoorOpenLerpDown;
                openness += (target - openness) * Mathf.Clamp(rate * dt, 0f, 1f);
                if (openness < 0.001f) openness = 0f;
                _doorOpenness[dref.TileKey] = openness;

                // Apply visual: scale.x compresses, with a small slide-aside
                // offset so it reads as a swing-open. Keep the door body
                // centred at the same logical position when closed
                // (openness=0) so the cosmetic shift is purely the swing.
                float scaleX = 1f - 0.78f * openness;
                float slidePx = openness * (TS * 0.34f);   // shift right as it opens
                var origin = new Vector2(dref.Centre.X + slidePx, dref.Centre.Y);
                var xform = new Transform2D(0f, new Vector2(scaleX, 1f), 0f, origin);
                dref.Mmi.Multimesh.SetInstanceTransform2D(dref.InstanceIdx, xform);
            }
        }

        private void RebuildInstances()
        {
            if (_map == null) return;
            // v0.6.0 — reset door-animation cache. RebuildInstances is
            // the only place door MMI instance indices are assigned, so
            // wiping the cache here keeps it in sync. Tile-keyed openness
            // values in _doorOpenness persist (they're indexed by tile
            // coords not MMI instance index), so a door whose render index
            // shifts because another door was added/removed still keeps
            // its open animation phase.
            _doorRefs.Clear();
            // v0.5.84o — per-mask wall counters (16 per family). Other
            // counters unchanged.
            var wallStoneCounts  = new int[16];
            var wallFungalCounts = new int[16];
            var wallWoodCounts   = new int[16];
            int floorCount = 0, floorFungalCount = 0, floorWoodCount = 0,
                doorCount = 0, doorFungalCount = 0,
                shelfCount = 0,
                workbenchCount = 0, bonfireCount = 0, blueprintCount = 0,
                bedCount = 0, shrineCount = 0, boardCount = 0, benchCount = 0,
                tableCount = 0, torchCount = 0,
                cookingTableCount = 0,   // v0.6.2 (Phase 5.6)
                demolishMarkCount = 0;   // v0.6.2 — demolish-as-task
            for (int y = 0; y < _map.Height; y++)
            for (int x = 0; x < _map.Width;  x++)
            {
                var slot = _map.GetStructure(x, y);
                if (!slot.IsPresent) continue;
                var origin = new Vector2(x * TS + TS * 0.5f, y * TS + TS * 0.5f);
                // v0.5.84c — emit a floor instance BENEATH a non-Floor
                // structure when the slot carries HasFloorBeneath. Sam:
                // "Furniture and its blueprints should also appear/be
                // able to be built on top of flooring." The floor sub-
                // layer renders at relative z=-1, so this instance
                // automatically sits below the furniture/wall/door/
                // blueprint emitted in the main switch below.
                if (slot.HasFloorBeneath && slot.Type != StructureType.Floor
                    && slot.Type != StructureType.FloorPlanned)
                {
                    // v0.5.84e — three-way dispatch: fungal/wood/stone.
                    bool floorIsFungal = slot.FloorBeneath == StructureMat.FungalWood;
                    bool floorIsWood   = slot.FloorBeneath == StructureMat.DeadWood
                                      || slot.FloorBeneath == StructureMat.LivingWood
                                      || slot.FloorBeneath == StructureMat.Wood;
                    var floorTint = floorIsFungal
                        ? new Color(1f, 1f, 1f, 1f)
                        : MaterialTint(slot.FloorBeneath, alpha: 1f);
                    MultiMeshInstance2D floorMmi;
                    int idx;
                    if (floorIsFungal)   { floorMmi = _floorFungalMmi; idx = floorFungalCount; }
                    else if (floorIsWood) { floorMmi = _floorWoodMmi;   idx = floorWoodCount; }
                    else                  { floorMmi = _floorMmi;       idx = floorCount; }
                    if (idx < MaxInstances)
                    {
                        floorMmi.Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
                        floorMmi.Multimesh.SetInstanceColor(idx, floorTint);
                        if (floorIsFungal)      floorFungalCount++;
                        else if (floorIsWood)   floorWoodCount++;
                        else                    floorCount++;
                    }
                }
                // v0.5.33 — per-instance tint from StructureMat. Built
                // structures use the full-alpha tint; blueprints use the
                // same tint at 0.6 alpha so the planned material is
                // legible before construction starts.
                var tint        = MaterialTint(slot.Material, alpha: 1f);
                var blueprintTint = MaterialTint(slot.Material, alpha: 0.6f);
                // v0.5.70 — FungalWood walls/floors/doors route to dedicated
                // mushroom-themed MMIs (Sam: "Anything made from fungalwood
                // should also have a mushroom-y texture"). The fungal sprite
                // already paints the mushroom palette so we pass a near-
                // white tint that lets the baked cream/spot colours show
                // through unmodified.
                bool isFungalWood = slot.Material == StructureMat.FungalWood;
                // v0.5.84d — wood-family (DeadWood/LivingWood/generic Wood)
                // routes to the dedicated plank-pattern wall MMI. Stone
                // subtypes stay on the existing brick wall MMI.
                bool isWoodWall =
                    slot.Material == StructureMat.DeadWood   ||
                    slot.Material == StructureMat.LivingWood ||
                    slot.Material == StructureMat.Wood;
                var fungalTint = new Color(1f, 1f, 1f, 1f);
                switch (slot.Type)
                {
                    case StructureType.Wall:
                    {
                        // v0.5.84o — compute the 4-bit cardinal-neighbour
                        // mask (N=1, E=2, S=4, W=8) for the autotile dispatch.
                        // Same-family rule: stone subtypes link with each
                        // other; wood subtypes link with each other;
                        // FungalWood links only with FungalWood.
                        int mask = ComputeWallNeighbourMask(x, y, slot.Material);
                        if (isFungalWood)
                        {
                            int idx = wallFungalCounts[mask];
                            if (idx < MaxInstances)
                            {
                                _wallFungalMmis[mask].Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
                                _wallFungalMmis[mask].Multimesh.SetInstanceColor(idx, fungalTint);
                                wallFungalCounts[mask] = idx + 1;
                            }
                        }
                        else if (isWoodWall)
                        {
                            int idx = wallWoodCounts[mask];
                            if (idx < MaxInstances)
                            {
                                _wallWoodMmis[mask].Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
                                _wallWoodMmis[mask].Multimesh.SetInstanceColor(idx, tint);
                                wallWoodCounts[mask] = idx + 1;
                            }
                        }
                        else
                        {
                            int idx = wallStoneCounts[mask];
                            if (idx < MaxInstances)
                            {
                                _wallMmis[mask].Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
                                _wallMmis[mask].Multimesh.SetInstanceColor(idx, tint);
                                wallStoneCounts[mask] = idx + 1;
                            }
                        }
                        break;
                    }
                    case StructureType.Floor when isFungalWood && floorFungalCount < MaxInstances:
                        _floorFungalMmi.Multimesh.SetInstanceTransform2D(floorFungalCount, new Transform2D(0f, origin));
                        _floorFungalMmi.Multimesh.SetInstanceColor(floorFungalCount, fungalTint);
                        floorFungalCount++;
                        break;
                    case StructureType.Floor when isWoodWall && floorWoodCount < MaxInstances:
                        // v0.5.84e — wood-family floors route to the plank
                        // sprite. (isWoodWall is the DeadWood/LivingWood/
                        // Wood family check defined above for walls; same
                        // family applies to floors.)
                        _floorWoodMmi.Multimesh.SetInstanceTransform2D(floorWoodCount, new Transform2D(0f, origin));
                        _floorWoodMmi.Multimesh.SetInstanceColor(floorWoodCount, tint);
                        floorWoodCount++;
                        break;
                    case StructureType.Floor when floorCount < MaxInstances:
                        _floorMmi.Multimesh.SetInstanceTransform2D(floorCount, new Transform2D(0f, origin));
                        _floorMmi.Multimesh.SetInstanceColor(floorCount, tint);
                        floorCount++;
                        break;
                    case StructureType.Door when isFungalWood && doorFungalCount < MaxInstances:
                        _doorFungalMmi.Multimesh.SetInstanceTransform2D(doorFungalCount, new Transform2D(0f, origin));
                        _doorFungalMmi.Multimesh.SetInstanceColor(doorFungalCount, fungalTint);
                        // v0.6.0 — register for per-frame open-animation lookup.
                        _doorRefs.Add(new DoorRenderRef {
                            TileKey = y * _map.Width + x,
                            Centre = origin, Mmi = _doorFungalMmi,
                            InstanceIdx = doorFungalCount, Tint = fungalTint });
                        doorFungalCount++;
                        break;
                    case StructureType.Door when doorCount < MaxInstances:   // v0.5.20
                        _doorMmi.Multimesh.SetInstanceTransform2D(doorCount, new Transform2D(0f, origin));
                        _doorMmi.Multimesh.SetInstanceColor(doorCount, tint);
                        // v0.6.0 — register for per-frame open-animation lookup.
                        _doorRefs.Add(new DoorRenderRef {
                            TileKey = y * _map.Width + x,
                            Centre = origin, Mmi = _doorMmi,
                            InstanceIdx = doorCount, Tint = tint });
                        doorCount++;
                        break;
                    case StructureType.Shelf when shelfCount < MaxInstances:   // v0.5.21
                        _shelfMmi.Multimesh.SetInstanceTransform2D(shelfCount, new Transform2D(0f, origin));
                        _shelfMmi.Multimesh.SetInstanceColor(shelfCount, tint);
                        shelfCount++;
                        break;
                    case StructureType.Workbench when workbenchCount < MaxInstances:   // v0.5.25
                        _workbenchMmi.Multimesh.SetInstanceTransform2D(workbenchCount, new Transform2D(0f, origin));
                        _workbenchMmi.Multimesh.SetInstanceColor(workbenchCount, tint);
                        workbenchCount++;
                        break;
                    case StructureType.Bonfire when bonfireCount < MaxInstances:   // v0.5.25
                        _bonfireMmi.Multimesh.SetInstanceTransform2D(bonfireCount, new Transform2D(0f, origin));
                        // Bonfire is stone-only by design; force neutral tint so
                        // accidental wood mat values still render plausibly.
                        _bonfireMmi.Multimesh.SetInstanceColor(bonfireCount, new Color(1f, 1f, 1f, 1f));
                        bonfireCount++;
                        break;
                    case StructureType.Bed when bedCount < MaxInstances:   // v0.5.35
                        _bedMmi.Multimesh.SetInstanceTransform2D(bedCount, new Transform2D(0f, origin));
                        _bedMmi.Multimesh.SetInstanceColor(bedCount, tint);
                        bedCount++;
                        break;
                    case StructureType.MeditationShrine when shrineCount < MaxInstances:   // v0.5.36
                        _shrineMmi.Multimesh.SetInstanceTransform2D(shrineCount, new Transform2D(0f, origin));
                        _shrineMmi.Multimesh.SetInstanceColor(shrineCount, tint);
                        shrineCount++;
                        break;
                    case StructureType.ShroomBoard when boardCount < MaxInstances:   // v0.5.36
                        _boardMmi.Multimesh.SetInstanceTransform2D(boardCount, new Transform2D(0f, origin));
                        _boardMmi.Multimesh.SetInstanceColor(boardCount, tint);
                        boardCount++;
                        break;
                    case StructureType.GossipBench when benchCount < MaxInstances:   // v0.5.36
                        _benchMmi.Multimesh.SetInstanceTransform2D(benchCount, new Transform2D(0f, origin));
                        _benchMmi.Multimesh.SetInstanceColor(benchCount, tint);
                        benchCount++;
                        break;
                    case StructureType.Table when tableCount < MaxInstances:   // v0.5.37
                        _tableMmi.Multimesh.SetInstanceTransform2D(tableCount, new Transform2D(0f, origin));
                        _tableMmi.Multimesh.SetInstanceColor(tableCount, tint);
                        tableCount++;
                        break;
                    case StructureType.Torch when torchCount < MaxInstances:   // v0.5.84t
                        _torchMmi.Multimesh.SetInstanceTransform2D(torchCount, new Transform2D(0f, origin));
                        _torchMmi.Multimesh.SetInstanceColor(torchCount, tint);
                        torchCount++;
                        break;
                    case StructureType.CookingTable when cookingTableCount < MaxInstances:   // v0.6.2 (Phase 5.6)
                        _cookingTableMmi.Multimesh.SetInstanceTransform2D(cookingTableCount, new Transform2D(0f, origin));
                        _cookingTableMmi.Multimesh.SetInstanceColor(cookingTableCount, tint);
                        cookingTableCount++;
                        break;
                    case StructureType.WallPlanned:
                    case StructureType.FloorPlanned:
                    case StructureType.DoorPlanned:
                    case StructureType.ShelfPlanned:
                    case StructureType.WorkbenchPlanned:
                    case StructureType.BonfirePlanned:
                    case StructureType.BedPlanned:               // v0.5.35
                    case StructureType.MeditationShrinePlanned:  // v0.5.36
                    case StructureType.ShroomBoardPlanned:       // v0.5.36
                    case StructureType.GossipBenchPlanned:       // v0.5.36
                    case StructureType.TablePlanned:             // v0.5.37
                    case StructureType.TorchPlanned:             // v0.5.84t
                    case StructureType.CookingTablePlanned:      // v0.6.2 (Phase 5.6)
                        if (blueprintCount < MaxInstances)
                        {
                            _blueprintMmi.Multimesh.SetInstanceTransform2D(blueprintCount, new Transform2D(0f, origin));
                            _blueprintMmi.Multimesh.SetInstanceColor(blueprintCount, blueprintTint);
                            blueprintCount++;
                        }
                        break;
                }

                // v0.6.2 — Demolish-as-task. Overlay a red X on any built
                // structure flagged for demolition. Runs after the main
                // type-switch so the structure sprite still paints first;
                // the X stacks on top so the player sees both "what's there"
                // and "it's marked for tear-down". The overlay also shows
                // an alpha rising with DemolitionProgress so the player
                // visually tracks how close to completion the work is.
                if (slot.MarkedForDemolition && slot.IsBuilt && demolishMarkCount < MaxInstances)
                {
                    float progress = slot.DemolitionProgress / (float)StructureSlot.BuildProgressTarget;
                    float alpha = 0.55f + progress * 0.40f;   // 0.55 → 0.95 as work nears completion
                    var tintMark = new Color(1.00f, 0.20f, 0.20f, alpha);
                    _demolishMarkMmi.Multimesh.SetInstanceTransform2D(demolishMarkCount, new Transform2D(0f, origin));
                    _demolishMarkMmi.Multimesh.SetInstanceColor(demolishMarkCount, tintMark);
                    demolishMarkCount++;
                }
            }
            // v0.5.84o — per-mask visible counts for all 3 wall families.
            for (int m = 0; m < 16; m++)
            {
                _wallMmis      [m].Multimesh.VisibleInstanceCount = wallStoneCounts [m];
                _wallFungalMmis[m].Multimesh.VisibleInstanceCount = wallFungalCounts[m];
                _wallWoodMmis  [m].Multimesh.VisibleInstanceCount = wallWoodCounts  [m];
            }
            _floorMmi      .Multimesh.VisibleInstanceCount = floorCount;
            _floorFungalMmi.Multimesh.VisibleInstanceCount = floorFungalCount;  // v0.5.70
            _floorWoodMmi  .Multimesh.VisibleInstanceCount = floorWoodCount;    // v0.5.84e
            _doorMmi       .Multimesh.VisibleInstanceCount = doorCount;
            _doorFungalMmi .Multimesh.VisibleInstanceCount = doorFungalCount;   // v0.5.70
            _shelfMmi      .Multimesh.VisibleInstanceCount = shelfCount;
            _workbenchMmi  .Multimesh.VisibleInstanceCount = workbenchCount;
            _bonfireMmi     .Multimesh.VisibleInstanceCount = bonfireCount;
            _bedMmi        .Multimesh.VisibleInstanceCount = bedCount;        // v0.5.35
            _shrineMmi     .Multimesh.VisibleInstanceCount = shrineCount;     // v0.5.36
            _boardMmi      .Multimesh.VisibleInstanceCount = boardCount;      // v0.5.36
            _benchMmi      .Multimesh.VisibleInstanceCount = benchCount;      // v0.5.36
            _tableMmi      .Multimesh.VisibleInstanceCount = tableCount;      // v0.5.37
            _torchMmi      .Multimesh.VisibleInstanceCount = torchCount;      // v0.5.84t
            _cookingTableMmi.Multimesh.VisibleInstanceCount = cookingTableCount; // v0.6.2 (Phase 5.6)
            _blueprintMmi  .Multimesh.VisibleInstanceCount = blueprintCount;
            _demolishMarkMmi.Multimesh.VisibleInstanceCount = demolishMarkCount; // v0.6.2 — demolish-as-task
        }

        // v0.5.84o — autotile mask. Bit 0 = N, 1 = E, 2 = S, 3 = W. A bit
        // is set when the cardinal neighbour is a Wall (or WallPlanned)
        // of the SAME autotile family as the centre tile. Family rule:
        // all Stone subtypes link (Granite next to Marble = linked);
        // wood subtypes link (DeadWood / LivingWood / Wood = one family);
        // FungalWood is its own family. Doors / floors / furniture do
        // NOT count as wall neighbours.
        private int ComputeWallNeighbourMask(int x, int y, StructureMat selfMat)
        {
            int mask = 0;
            if (IsWallSameFamily(x,     y - 1, selfMat)) mask |= 0b0001;   // N
            if (IsWallSameFamily(x + 1, y,     selfMat)) mask |= 0b0010;   // E
            if (IsWallSameFamily(x,     y + 1, selfMat)) mask |= 0b0100;   // S
            if (IsWallSameFamily(x - 1, y,     selfMat)) mask |= 0b1000;   // W
            return mask;
        }

        private bool IsWallSameFamily(int x, int y, StructureMat selfMat)
        {
            if (_map == null) return false;
            if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) return false;
            var s = _map.GetStructure(x, y);
            if (s.Type != StructureType.Wall && s.Type != StructureType.WallPlanned) return false;
            return WallFamily(s.Material) == WallFamily(selfMat);
        }

        // v0.5.84o — three autotile families for wall linking: 0 = stone-
        // subtype, 1 = wood-subtype, 2 = FungalWood.
        private static int WallFamily(StructureMat m)
        {
            if (m == StructureMat.FungalWood) return 2;
            if (m == StructureMat.DeadWood || m == StructureMat.LivingWood || m == StructureMat.Wood) return 1;
            return 0;   // Stone + Granite + Limestone + Marble + Obsidian + Quartz
        }

        private MultiMeshInstance2D CreateMmi(Mesh mesh, Texture2D tex, Node? parent = null)
        {
            var mm = new MultiMesh
            {
                Mesh                 = mesh,
                TransformFormat      = MultiMesh.TransformFormatEnum.Transform2D,
                // v0.5.33 — per-instance color so each tile can be tinted
                // to its StructureMat (Stone grey / DeadWood brown /
                // FungalWood purple / LivingWood green). RimWorld pattern:
                // one base sprite per structure type, colour multiplied
                // per material. Blueprint MMI uses UseColors too so the
                // translucent ghost can tint to indicate the planned
                // material.
                UseColors            = true,
                InstanceCount        = MaxInstances,
                VisibleInstanceCount = 0,
            };
            var mmi = new MultiMeshInstance2D
            {
                Multimesh     = mm,
                Texture       = tex,
                TextureFilter = TextureFilterEnum.Nearest,
            };
            // v0.5.71 — opt-in custom parent for floor MMIs (parented to
            // the _floorLayer sub-node at z=-1). Default keeps everything
            // else as a direct child of StructureOverlay.
            (parent ?? this).AddChild(mmi);
            return mmi;
        }

        // v0.5.33 — converts a StructureMat to the Godot Color used for
        // per-instance tinting. Wraps StructureMatMeta.Tint in a Color
        // struct (the helper returns a (r,g,b) tuple to stay engine-free).
        // Alpha 1.0 for built; blueprint MMI overrides to 0.6 for the
        // translucent ghost effect.
        private static Color MaterialTint(StructureMat mat, float alpha = 1f)
        {
            var (r, g, b) = StructureMatMeta.Tint(mat);
            return new Color(r, g, b, alpha);
        }

        // 16×16 sprites baked at first render. Walls = solid dark grey
        // with a slightly lighter top edge (suggests "stone block"). Floors
        // = mid-tone tan with a faint grid (suggests "laid flooring").
        // Blueprints = translucent white with a dashed border (RimWorld-
        // style ghost).

        // v0.5.25 — improved pixel art. Stone-brick wall pattern:
        // two rows of bricks with offset mortar joints (running bond).
        // Top-row highlight gives a 3D bevel suggesting cap stones.
        // v0.5.84d — stone wall blend-friendly redo. Sam: "Stone Subtypes
        // should ... [be] given the 2.5D treatment with textures that
        // blend in large groups to imitate singular bodies. Analyze
        // Rimworld's art implementation for a reference." RimWorld-style
        // wall reading: walls have a lit top edge + base shadow but
        // NO outer left/top border, so neighbours horizontally fuse into
        // one continuous masonry block. Vertical seam (top of lower
        // wall = light, bottom of upper wall = shadow) breaks slightly
        // but reads as expected wall-stack depth, not per-tile grid.
        //
        // Anatomy (16×16):
        //   Row 0       : cap-light (full row; merges with neighbour above)
        //   Row 1       : cap-mid
        //   Row 2-13    : brick body with horizontal mortar at row 7
        //                 and staggered vertical mortar segments
        //                 (NOT at column 0/15 so adjacent walls don't
        //                 show a grid)
        //   Row 14      : brick-shadow (dim band)
        //   Row 15      : deep-shadow (drops onto next-tile-down)
        //   Col 15      : 1 px subtle shadow column (preserves a bit of
        //                 side depth without breaking the horizontal blend)
        // v0.5.84o — autotile-aware stone wall variant. `mask` is a 4-bit
        // cardinal-neighbour flag: bit 0 = N, bit 1 = E, bit 2 = S, bit 3 = W
        // (set when the tile on that side is a same-family wall). Edge caps
        // are painted only on sides that LACK a neighbour, so a wall in the
        // middle of a square (mask 15) has no caps and merges seamlessly with
        // its neighbours, while a solo pillar (mask 0) has caps on all four
        // sides. Sixteen calls at boot bake the full set; RebuildInstances
        // dispatches per-tile to the right MMI.
        private static ImageTexture BakeWallSpriteStone(int mask)
        {
            bool n = (mask & 0b0001) != 0;
            bool e = (mask & 0b0010) != 0;
            bool s = (mask & 0b0100) != 0;
            bool w = (mask & 0b1000) != 0;

            var stone     = new Color(0.45f, 0.43f, 0.39f, 1.0f);
            var mortar    = new Color(0.30f, 0.28f, 0.24f, 1.0f);
            var capLight  = new Color(0.72f, 0.68f, 0.60f, 1.0f);
            var capMid    = new Color(0.58f, 0.55f, 0.48f, 1.0f);
            var brickLo   = new Color(0.34f, 0.32f, 0.28f, 1.0f);
            var deepShad  = new Color(0.20f, 0.18f, 0.16f, 1.0f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, stone);

            // Horizontal mortar (single row at the middle).
            int mid = TS / 2;
            for (int x = 0; x < TS; x++) img.SetPixel(x, mid, mortar);

            // Vertical mortar segments at quarter columns, OFFSET between
            // the two brick rows (running bond). Stop 1 px shy of edges so
            // adjacent walls don't form a continuous grid line.
            int q1 = TS / 4, q3 = (TS * 3) / 4;
            for (int y = 2; y < mid; y++)        img.SetPixel(q1, y, mortar);
            for (int y = mid + 1; y < TS - 2; y++) img.SetPixel(q3, y, mortar);

            // Top cap — only if no neighbour above (else interior merges
            // cleanly with neighbour's bottom rows).
            if (!n)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, 0, capLight);
                for (int x = 0; x < TS; x++) img.SetPixel(x, 1, capMid);
            }
            // Base shadow gradient — only if no neighbour below.
            if (!s)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 2, brickLo);
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 1, deepShad);
            }
            // Right-side depth column — only if no neighbour east.
            if (!e)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(TS - 1, y, brickLo);
            // Left-side depth column — only if no neighbour west.
            if (!w)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(0, y, brickLo);

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84k — FungalWood wall redo. Sam: "mushroom walls should
        // look more like mushroomy planks woven/bound together, like a
        // more spongy and natural wooden plank texture (that the wooden
        // wall types should have)." Pre-fix was the v0.5.70 side-by-
        // side mushroom-cap motif — distinctive but per-tile (didn't
        // blend in long wall runs) and ornamental rather than reading
        // as material.
        //
        // New anatomy mirrors BakeWallSpriteWood's plank philosophy
        // (3 vertical planks, seams at q1/q3, sunlit cap + base shadow,
        // right-side depth column) but with a cream-peach mushroom
        // palette and two extras for the woven/spongy feel:
        //   • Binding marks at rows 5 and 11 — darker horizontal
        //     bands suggesting cord/sinew lashings holding the planks
        //     together. Stop short of the tile edges so adjacent walls
        //     don't form a continuous grid line, just like the stone
        //     wall's mortar segments.
        //   • Spore freckles within each plank — small russet pixels
        //     instead of the wood wall's grain knots. Read as the
        //     spotting in the FungalWood material itself.
        // Per-instance tint stays near-white at dispatch so the baked
        // cream palette comes through unmodified.
        // v0.5.84o — autotile-aware fungal wall variant. Same mask
        // convention as the stone/wood variants. FungalWood is its own
        // autotile family (does not link with Stone or Wood walls).
        private static ImageTexture BakeWallSpriteFungal(int mask)
        {
            bool n = (mask & 0b0001) != 0;
            bool e = (mask & 0b0010) != 0;
            bool s = (mask & 0b0100) != 0;
            bool w = (mask & 0b1000) != 0;

            var flesh    = new Color(0.92f, 0.84f, 0.70f, 1.0f);
            var seam     = new Color(0.62f, 0.52f, 0.40f, 1.0f);
            var binding  = new Color(0.48f, 0.36f, 0.24f, 1.0f);
            var capLight = new Color(1.00f, 0.94f, 0.80f, 1.0f);
            var capMid   = new Color(0.96f, 0.88f, 0.74f, 1.0f);
            var baseLo   = new Color(0.70f, 0.58f, 0.46f, 1.0f);
            var deepLo   = new Color(0.50f, 0.38f, 0.28f, 1.0f);
            var freckle  = new Color(0.66f, 0.34f, 0.30f, 1.0f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, flesh);

            // Vertical plank seams at q1/q3.
            int q1 = TS / 4, q3 = (TS * 3) / 4;
            for (int y = 2; y < TS - 2; y++)
            {
                img.SetPixel(q1, y, seam);
                img.SetPixel(q3, y, seam);
            }

            // Binding bands — two horizontal lashings at rows 5 + 11.
            for (int x = 1; x < TS - 1; x++)
            {
                if (x == q1 || x == q3) continue;
                img.SetPixel(x, 5,  binding);
                img.SetPixel(x, 11, binding);
            }

            // Spore freckles scattered within planks.
            img.SetPixel(2,  3,  freckle);
            img.SetPixel(6,  4,  freckle);
            img.SetPixel(10, 7,  freckle);
            img.SetPixel(13, 8,  freckle);
            img.SetPixel(4,  9,  freckle);
            img.SetPixel(8,  13, freckle);
            img.SetPixel(13, 13, freckle);

            // Top cap — only if no neighbour above.
            if (!n)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, 0, capLight);
                for (int x = 0; x < TS; x++) img.SetPixel(x, 1, capMid);
            }
            // Base shadow — only if no neighbour below.
            if (!s)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 2, baseLo);
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 1, deepLo);
            }
            // Right depth column — only if no neighbour east.
            if (!e)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(TS - 1, y, baseLo);
            // Left depth column — only if no neighbour west.
            if (!w)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(0, y, baseLo);

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84d — wood wall sprite (DeadWood / LivingWood / generic Wood).
        // Pre-fix these all used BakeWallSprite (stone bricks) tinted brown/
        // green, which read as "tinted bricks" not as wood. New sprite is
        // a vertical plank pattern with subtle grain — 3 planks per tile,
        // dark seam columns at q1/q3 — plus the same 2.5D cap + base
        // shadow as the stone wall so they blend horizontally into long
        // wall runs the same way. Per-instance MaterialTint carries the
        // wood-type colour (warm brown for DeadWood, leafy green for
        // LivingWood, near-cream for generic Wood).
        // v0.5.84o — autotile-aware wood wall variant. Same mask convention
        // as BakeWallSpriteStone: edge caps painted only on sides without
        // a same-family neighbour. Wood/DeadWood/LivingWood treated as
        // one family for autotile (visually consistent plank pattern;
        // per-instance tint differentiates the subtype colour).
        private static ImageTexture BakeWallSpriteWood(int mask)
        {
            bool n = (mask & 0b0001) != 0;
            bool e = (mask & 0b0010) != 0;
            bool s = (mask & 0b0100) != 0;
            bool w = (mask & 0b1000) != 0;

            var wood     = new Color(0.55f, 0.40f, 0.26f, 1.0f);
            var grain    = new Color(0.42f, 0.30f, 0.18f, 1.0f);
            var capLight = new Color(0.78f, 0.60f, 0.42f, 1.0f);
            var capMid   = new Color(0.65f, 0.48f, 0.32f, 1.0f);
            var baseLo   = new Color(0.34f, 0.22f, 0.12f, 1.0f);
            var deepLo   = new Color(0.22f, 0.14f, 0.08f, 1.0f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);

            // Plank seams — 2 vertical lines at q1/q3 (full inner height).
            int q1 = TS / 4, q3 = (TS * 3) / 4;
            for (int y = 2; y < TS - 2; y++)
            {
                img.SetPixel(q1, y, grain);
                img.SetPixel(q3, y, grain);
            }

            // Subtle grain knots (one per plank, asymmetric so they don't
            // align across adjacent tiles).
            img.SetPixel(2,  6,  grain);
            img.SetPixel(8,  9,  grain);
            img.SetPixel(13, 5,  grain);
            img.SetPixel(5,  11, grain);
            img.SetPixel(11, 12, grain);

            // Top cap (sunlit lighter wood) — only if no neighbour above.
            if (!n)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, 0, capLight);
                for (int x = 0; x < TS; x++) img.SetPixel(x, 1, capMid);
            }
            // Base shadow (rows 14-15) — only if no neighbour below.
            if (!s)
            {
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 2, baseLo);
                for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 1, deepLo);
            }
            // Right-side depth column — only if no neighbour east.
            if (!e)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(TS - 1, y, baseLo);
            // Left-side depth column — only if no neighbour west.
            if (!w)
                for (int y = 2; y < TS - 2; y++) img.SetPixel(0, y, baseLo);

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84d — floor sprite redo. Sam: "Neither floor option is
        // ideal. I want the floors to appear flatter (but with texture)
        // to appear in contrast to the walls/stone." Removes the
        // structural plank-seam + dark-border grid that was making the
        // floor read as panelled/tiled. Replaces with a near-flat
        // base + sparse non-aligned single-pixel accents (one slightly-
        // lighter + one slightly-darker shade). Adjacent floor tiles
        // now blend seamlessly into one body — no visible per-tile
        // boundary — and the per-instance MaterialTint passes through
        // cleanly so stone/wood subtypes still differentiate by colour.
        private static ImageTexture BakeFloorSprite()
        {
            var baseCol = new Color(0.60f, 0.52f, 0.42f, 1.0f);
            var accentA = new Color(0.65f, 0.56f, 0.46f, 1.0f);   // +grain
            var accentB = new Color(0.55f, 0.48f, 0.38f, 1.0f);   // −grain
            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, baseCol);
            // Sparse asymmetric accent points. Positions picked so the
            // pattern reads as random across an N×N tiling rather than
            // a regular grid (no point is on a row/column shared by
            // another point in the same horizontal or vertical band).
            (int x, int y, Color c)[] pts =
            {
                (2, 3, accentA),  (11, 5, accentB), (6, 8, accentA),
                (13, 10, accentB), (4, 12, accentB), (9, 13, accentA),
                (7, 2, accentB),  (14, 14, accentA),
            };
            foreach (var (px, py, c) in pts) img.SetPixel(px, py, c);
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84e — FungalWood floor gets subtle plank lines. Sam:
        // "Flooring made from wood types/fungalwood should have subtle
        // plank lines." Two horizontal plank zones separated by a thin
        // darker seam at rows 7 and 15 (bottom edge = seam too, so
        // vertical-adjacent tiles' seams merge into one continuous
        // line — each visible plank is exactly 7 rows whether it spans
        // a tile boundary or not). Plank colour varies subtly between
        // upper and lower half for warmth; a few spore freckles stay
        // for fungal flavour.
        private static ImageTexture BakeFloorSpriteFungal()
        {
            var plankA = new Color(0.86f, 0.74f, 0.60f, 1.0f);   // upper plank
            var plankB = new Color(0.82f, 0.70f, 0.56f, 1.0f);   // lower plank (slightly cooler)
            var seam   = new Color(0.70f, 0.58f, 0.46f, 1.0f);   // soft plank seam
            var spotA  = new Color(0.92f, 0.82f, 0.68f, 1.0f);   // pale spore freckle
            var spotB  = new Color(0.74f, 0.62f, 0.50f, 1.0f);   // earth freckle

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, y < 8 ? plankA : plankB);

            // Plank seams (interior + bottom edge).
            for (int x = 0; x < TS; x++) img.SetPixel(x, 7,      seam);
            for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 1, seam);

            // Spore freckles scattered across both planks at non-grid
            // positions (avoid the seam rows).
            (int x, int y, Color c)[] pts =
            {
                (3, 3, spotA), (10, 2, spotB), (5, 5, spotA),
                (12, 4, spotB), (2, 10, spotB), (8, 11, spotA),
                (13, 12, spotB),
            };
            foreach (var (px, py, c) in pts) img.SetPixel(px, py, c);
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84e — wood floor (DeadWood / LivingWood / generic Wood).
        // Same subtle-plank anatomy as FungalWood: 2 horizontal plank
        // zones with seams at rows 7 + 15 so vertical-adjacent tiles'
        // seams form one continuous line and every visible plank reads
        // as 7 rows wide regardless of whether it spans a tile boundary.
        // Distinct from stone floors (which stay near-flat noise). Tint
        // colours the planks per material — DeadWood → warm brown,
        // LivingWood → leafy green, generic Wood → near-cream.
        private static ImageTexture BakeFloorSpriteWood()
        {
            var plankA = new Color(0.58f, 0.48f, 0.34f, 1.0f);   // upper plank
            var plankB = new Color(0.52f, 0.42f, 0.28f, 1.0f);   // lower plank
            var seam   = new Color(0.36f, 0.26f, 0.16f, 1.0f);   // plank seam
            var grainA = new Color(0.62f, 0.52f, 0.38f, 1.0f);   // +grain knot
            var grainB = new Color(0.46f, 0.36f, 0.22f, 1.0f);   // −grain knot

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, y < 8 ? plankA : plankB);

            // Plank seams (interior row 7 + bottom edge row 15).
            for (int x = 0; x < TS; x++) img.SetPixel(x, 7,      seam);
            for (int x = 0; x < TS; x++) img.SetPixel(x, TS - 1, seam);

            // Subtle grain knots within each plank, asymmetric.
            (int x, int y, Color c)[] pts =
            {
                (3, 2, grainA), (10, 4, grainB), (6, 5, grainA),
                (13, 3, grainB), (2, 10, grainB), (9, 11, grainA),
                (12, 13, grainB), (5, 12, grainA),
            };
            foreach (var (px, py, c) in pts) img.SetPixel(px, py, c);
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.25 — improved Door sprite: paneled wooden door with a
        // visible handle dot. Two recessed panels (top + bottom) for
        // craftsmanship feel.
        private static ImageTexture BakeDoorSprite()
        {
            var wood     = new Color(0.55f, 0.35f, 0.20f, 1.0f);
            var woodHi   = new Color(0.68f, 0.46f, 0.28f, 1.0f);
            var woodLo   = new Color(0.40f, 0.24f, 0.12f, 1.0f);
            var handle   = new Color(0.85f, 0.72f, 0.30f, 1.0f);   // brass handle
            var border   = new Color(0.20f, 0.12f, 0.06f, 1.0f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);

            // Two recessed panels (top + bottom thirds)
            int p1Top = 2, p1Bot = TS / 2 - 1;
            int p2Top = TS / 2 + 1, p2Bot = TS - 3;
            for (int y = p1Top; y <= p1Bot; y++)
            {
                img.SetPixel(2, y, woodLo);
                img.SetPixel(TS - 3, y, woodLo);
            }
            for (int x = 2; x <= TS - 3; x++)
            {
                img.SetPixel(x, p1Top, woodLo);
                img.SetPixel(x, p1Bot, woodLo);
            }
            for (int y = p2Top; y <= p2Bot; y++)
            {
                img.SetPixel(2, y, woodLo);
                img.SetPixel(TS - 3, y, woodLo);
            }
            for (int x = 2; x <= TS - 3; x++)
            {
                img.SetPixel(x, p2Top, woodLo);
                img.SetPixel(x, p2Bot, woodLo);
            }

            // Brass handle on the right side, mid-height
            int hMid = TS / 2;
            img.SetPixel(TS - 4, hMid, handle);
            img.SetPixel(TS - 5, hMid, handle);

            // Border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.70 — FungalWood door: a single tall mushroom (cap up top,
        // stem-shaped door body, base flare at the bottom) with the brass
        // handle preserved. Per-instance tint is white at dispatch so the
        // baked cap palette shows through.
        // v0.5.84j — fungal door redo. Sam, with reference photo of a
        // decorative fairy-tale mushroom door: red mushroom CAP awning
        // overhanging a cream oval door body with a porthole window and
        // vertically-paired brass knobs. Pre-fix was a single mushroom
        // (cap on top, narrow stem-as-door body) with the standard wood
        // door's mortar border. Now: separated cap-on-top + oval-body-
        // below, no outer border, transparent background outside the
        // silhouette so the floor / terrain shows through.
        private static ImageTexture BakeDoorSpriteFungal()
        {
            var clear     = new Color(0f, 0f, 0f, 0f);                // transparent bg
            var cap       = new Color(0.78f, 0.18f, 0.18f, 1.0f);     // bright red cap
            var capHi     = new Color(0.92f, 0.40f, 0.30f, 1.0f);     // cap highlight
            var capDark   = new Color(0.50f, 0.10f, 0.10f, 1.0f);     // cap outline / underside
            var spot      = new Color(0.98f, 0.95f, 0.88f, 1.0f);     // white spore spot
            var gill      = new Color(0.94f, 0.86f, 0.72f, 1.0f);     // cream gill underside
            var body      = new Color(0.94f, 0.90f, 0.82f, 1.0f);     // cream door body
            var bodyEdge  = new Color(0.74f, 0.68f, 0.56f, 1.0f);     // door silhouette outline
            var bodyShade = new Color(0.84f, 0.78f, 0.66f, 1.0f);     // right-side body shadow
            var portFrame = new Color(0.38f, 0.24f, 0.16f, 1.0f);     // dark wood-grain porthole frame
            var portGlow  = new Color(0.98f, 0.88f, 0.55f, 1.0f);     // warm window glow
            var handle    = new Color(0.78f, 0.58f, 0.22f, 1.0f);     // brass knob

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, clear);

            int cx = TS / 2;   // 8

            // Cap dome (rows 0-4) — egg-curve widening to a 13-px overhang.
            for (int x = cx - 1; x <= cx + 1; x++) img.SetPixel(x, 0, cap);   // 3 wide
            for (int x = cx - 3; x <= cx + 3; x++) img.SetPixel(x, 1, cap);   // 7 wide
            for (int x = cx - 4; x <= cx + 4; x++) img.SetPixel(x, 2, cap);   // 9 wide
            for (int x = cx - 5; x <= cx + 5; x++) img.SetPixel(x, 3, cap);   // 11 wide
            for (int x = cx - 6; x <= cx + 6; x++) img.SetPixel(x, 4, cap);   // 13 wide (max overhang)

            // Cap highlights (upper-left, implies top-left light source).
            img.SetPixel(cx - 1, 1, capHi);
            img.SetPixel(cx - 2, 2, capHi);
            img.SetPixel(cx - 3, 3, capHi);

            // White spore spots scattered across the cap (Amanita pattern).
            img.SetPixel(cx + 1, 2, spot);
            img.SetPixel(cx - 1, 3, spot);
            img.SetPixel(cx + 3, 3, spot);
            img.SetPixel(cx - 4, 4, spot);
            img.SetPixel(cx + 2, 4, spot);
            img.SetPixel(cx + 5, 4, spot);

            // Cap outer-edge darkening (row 4 ends + row 5 underside).
            img.SetPixel(cx - 6, 4, capDark);
            img.SetPixel(cx + 6, 4, capDark);

            // Gill underside row (row 5) — full cap width, cream tone
            // (the visible cap underside in the reference).
            for (int x = cx - 6; x <= cx + 6; x++) img.SetPixel(x, 5, gill);

            // Door body (oval/egg, rows 6-15) — narrower than the cap so
            // the cap reads as an awning above. Anti-aliased silhouette
            // via single-px edge tone.
            void Row(int y, int from, int to)
            {
                for (int x = from; x <= to; x++) img.SetPixel(x, y, body);
                if (from > 0)        img.SetPixel(from - 1, y, bodyEdge);
                if (to   < TS - 1)   img.SetPixel(to   + 1, y, bodyEdge);
            }
            Row(6,  cx - 3, cx + 3);   // 7 wide (door top)
            Row(7,  cx - 4, cx + 4);   // 9 wide
            Row(8,  cx - 4, cx + 4);   // 9 wide
            Row(9,  cx - 4, cx + 4);   // 9 wide
            Row(10, cx - 4, cx + 4);   // 9 wide (door middle, widest)
            Row(11, cx - 4, cx + 4);
            Row(12, cx - 4, cx + 4);
            Row(13, cx - 4, cx + 4);
            Row(14, cx - 3, cx + 3);   // 7 wide (door narrowing)
            for (int x = cx - 2; x <= cx + 2; x++) img.SetPixel(x, 15, body);

            // Right-side body shadow column for depth.
            for (int y = 7; y <= 13; y++) img.SetPixel(cx + 4, y, bodyShade);

            // Porthole window — 3×3 dark frame at rows 7-9, with a warm
            // glow at the centre pixel (mirrors the reference's eye-like
            // dark-wood-grain framed circle).
            img.SetPixel(cx - 1, 7, portFrame);
            img.SetPixel(cx,     7, portFrame);
            img.SetPixel(cx + 1, 7, portFrame);
            img.SetPixel(cx - 1, 8, portFrame);
            img.SetPixel(cx,     8, portGlow);
            img.SetPixel(cx + 1, 8, portFrame);
            img.SetPixel(cx - 1, 9, portFrame);
            img.SetPixel(cx,     9, portFrame);
            img.SetPixel(cx + 1, 9, portFrame);

            // Brass knob pair — two vertical dots at lower-middle (the
            // small buttons in the reference). Stacked vertically rather
            // than RimWorld's right-side single dot.
            img.SetPixel(cx,     11, handle);
            img.SetPixel(cx,     12, handle);

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.25 — improved Shelf sprite. Three-tier wooden shelf with
        // visible "stored item" dots on each tier (small coloured pixels
        // suggesting stacked goods).
        private static ImageTexture BakeShelfSprite()
        {
            var wood     = new Color(0.50f, 0.38f, 0.25f, 1.0f);
            var woodHi   = new Color(0.65f, 0.50f, 0.32f, 1.0f);
            var shelfDiv = new Color(0.32f, 0.22f, 0.14f, 1.0f);
            var border   = new Color(0.22f, 0.14f, 0.08f, 1.0f);
            var item1    = new Color(0.85f, 0.40f, 0.40f, 1.0f);   // red item
            var item2    = new Color(0.55f, 0.75f, 0.45f, 1.0f);   // green item
            var item3    = new Color(0.50f, 0.65f, 0.95f, 1.0f);   // blue item

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);

            // Two shelf divisions (3 tiers)
            int h1 = TS / 3, h2 = (TS * 2) / 3;
            for (int x = 1; x < TS - 1; x++)
            {
                img.SetPixel(x, h1, shelfDiv);
                img.SetPixel(x, h2, shelfDiv);
                // Highlight on top of each shelf board
                img.SetPixel(x, h1 - 1, woodHi);
                img.SetPixel(x, h2 - 1, woodHi);
            }

            // "Stored items" — small dots on each tier
            int q1 = TS / 4, q3 = (TS * 3) / 4;
            // Tier 1 (top)
            int tier1Y = h1 - 3;
            img.SetPixel(q1,     tier1Y, item1);
            img.SetPixel(q1 + 1, tier1Y, item1);
            img.SetPixel(q3,     tier1Y, item2);
            img.SetPixel(q3 + 1, tier1Y, item2);
            // Tier 2 (mid)
            int tier2Y = h2 - 3;
            img.SetPixel(q1,     tier2Y, item3);
            img.SetPixel(q1 + 1, tier2Y, item3);
            img.SetPixel(TS / 2, tier2Y, item1);
            // Tier 3 (bottom)
            int tier3Y = TS - 4;
            img.SetPixel(q3,     tier3Y, item2);
            img.SetPixel(q3 + 1, tier3Y, item2);

            // Border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.25 — Workbench sprite. Wooden table with a hammer
        // silhouette + sawdust suggesting active crafting. Distinct
        // top-edge highlight signals "tool surface" rather than shelving.
        private static ImageTexture BakeWorkbenchSprite()
        {
            var wood     = new Color(0.48f, 0.34f, 0.22f, 1.0f);
            var woodHi   = new Color(0.65f, 0.48f, 0.32f, 1.0f);
            var woodLo   = new Color(0.30f, 0.20f, 0.12f, 1.0f);
            var border   = new Color(0.20f, 0.12f, 0.06f, 1.0f);
            var tool     = new Color(0.55f, 0.55f, 0.60f, 1.0f);   // metal hammer head
            var handle   = new Color(0.40f, 0.25f, 0.15f, 1.0f);   // hammer handle
            var sawdust  = new Color(0.85f, 0.72f, 0.50f, 1.0f);   // light wood shavings

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);

            // Bench top (top quarter is the working surface, lighter)
            int benchTop = TS / 4;
            for (int y = 1; y < benchTop; y++)
            for (int x = 1; x < TS - 1; x++)
                img.SetPixel(x, y, woodHi);
            // Top-edge band
            for (int x = 1; x < TS - 1; x++) img.SetPixel(x, benchTop, woodLo);

            // Hammer silhouette laid on the bench (top-left area)
            // Head: 3x2 block
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 3; dx++)
                img.SetPixel(2 + dx, 3 + dy, tool);
            // Handle (extends right)
            for (int dx = 0; dx < 5; dx++)
                img.SetPixel(5 + dx, 4, handle);

            // Sawdust scatter on bench surface (right side)
            img.SetPixel(TS - 5, 2, sawdust);
            img.SetPixel(TS - 4, 3, sawdust);
            img.SetPixel(TS - 3, 2, sawdust);

            // Vertical "table leg" lines on lower half
            for (int y = benchTop + 1; y < TS - 1; y++)
            {
                img.SetPixel(2,        y, woodLo);
                img.SetPixel(TS - 3,   y, woodLo);
            }

            // Border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }

            return ImageTexture.CreateFromImage(img);
        }

        // v0.6.2 (Phase 5.6 ship) — Cooking Table sprite. Reads as a kitchen
        // prep station rather than a tool bench: light wood chopping-block
        // base, dark stone slab on top for the cook surface, plus a cleaver
        // silhouette + a small bowl. Sits in the same Workbench-tier MMI
        // footprint so it stacks on floors and respects all the v0.5.84
        // furniture rendering rules.
        private static ImageTexture BakeCookingTableSprite()
        {
            var wood     = new Color(0.62f, 0.46f, 0.30f, 1.0f);   // lighter than Workbench wood
            var woodHi   = new Color(0.78f, 0.62f, 0.42f, 1.0f);
            var woodLo   = new Color(0.42f, 0.30f, 0.20f, 1.0f);
            var slab     = new Color(0.46f, 0.44f, 0.40f, 1.0f);   // dark stone slab
            var slabHi   = new Color(0.62f, 0.60f, 0.55f, 1.0f);
            var border   = new Color(0.20f, 0.12f, 0.06f, 1.0f);
            var blade    = new Color(0.78f, 0.80f, 0.85f, 1.0f);   // cleaver steel
            var handle   = new Color(0.30f, 0.20f, 0.12f, 1.0f);
            var bowl     = new Color(0.50f, 0.30f, 0.20f, 1.0f);   // ceramic bowl
            var bowlHi   = new Color(0.70f, 0.50f, 0.35f, 1.0f);
            var greens   = new Color(0.45f, 0.65f, 0.30f, 1.0f);   // herb/veg fleck

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);

            // Stone slab covers the top ~40% — the actual cook surface
            int slabTop = TS / 4;
            int slabBot = TS / 2 + 1;
            for (int y = slabTop; y < slabBot; y++)
            for (int x = 1; x < TS - 1; x++)
                img.SetPixel(x, y, slab);
            // Slab highlight on top edge
            for (int x = 1; x < TS - 1; x++) img.SetPixel(x, slabTop, slabHi);

            // Cleaver: rectangular blade + handle, top-left of the slab
            // Blade (4×2 block)
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 4; dx++)
                img.SetPixel(2 + dx, slabTop + 1 + dy, blade);
            // Handle (2 pixels right of the blade)
            img.SetPixel(6, slabTop + 1, handle);
            img.SetPixel(7, slabTop + 1, handle);

            // Bowl on the right side of the slab
            int bcx = TS - 4;
            int bcy = slabTop + 2;
            // Top rim
            img.SetPixel(bcx - 1, bcy,     bowlHi);
            img.SetPixel(bcx,     bcy,     bowlHi);
            img.SetPixel(bcx + 1, bcy,     bowlHi);
            // Body
            img.SetPixel(bcx - 1, bcy + 1, bowl);
            img.SetPixel(bcx,     bcy + 1, bowl);
            img.SetPixel(bcx + 1, bcy + 1, bowl);
            // Contents (one green fleck)
            img.SetPixel(bcx,     bcy,     greens);

            // Wood face beneath the slab — chopping-block grain stripes
            int grain1 = slabBot + 2;
            int grain2 = slabBot + 4;
            for (int x = 2; x < TS - 2; x++)
            {
                img.SetPixel(x, grain1, woodLo);
                img.SetPixel(x, grain2, woodHi);
            }

            // Vertical "table leg" lines on lower half
            for (int y = slabBot + 1; y < TS - 1; y++)
            {
                img.SetPixel(2,        y, woodLo);
                img.SetPixel(TS - 3,   y, woodLo);
            }

            // Border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.25 — Bonfire sprite. Stone fire pit with flames + glowing
        // embers. Reads as the warmest, most alive structure on the map.
        private static ImageTexture BakeBonfireSprite()
        {
            var stone    = new Color(0.42f, 0.40f, 0.36f, 1.0f);
            var stoneHi  = new Color(0.55f, 0.52f, 0.46f, 1.0f);
            var stoneLo  = new Color(0.28f, 0.26f, 0.22f, 1.0f);
            var border   = new Color(0.18f, 0.16f, 0.14f, 1.0f);
            var ember    = new Color(0.95f, 0.35f, 0.10f, 1.0f);   // bright orange
            var emberHi  = new Color(1.00f, 0.85f, 0.30f, 1.0f);   // yellow flame core
            var ash      = new Color(0.30f, 0.20f, 0.15f, 1.0f);   // dark ash

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            // Stone outer ring (border-zone)
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, stone);

            // Inner pit (3 tiles in from each edge)
            int pitMin = 3, pitMax = TS - 4;
            for (int y = pitMin; y <= pitMax; y++)
            for (int x = pitMin; x <= pitMax; x++)
                img.SetPixel(x, y, ash);

            // Fire — embers + flames in the pit
            int cx = TS / 2, cy = TS / 2;
            // Bright yellow core
            img.SetPixel(cx - 1, cy,     emberHi);
            img.SetPixel(cx,     cy,     emberHi);
            img.SetPixel(cx + 1, cy,     emberHi);
            img.SetPixel(cx,     cy - 1, emberHi);
            // Orange surrounding flame
            img.SetPixel(cx - 2, cy,     ember);
            img.SetPixel(cx + 2, cy,     ember);
            img.SetPixel(cx - 1, cy - 1, ember);
            img.SetPixel(cx + 1, cy - 1, ember);
            img.SetPixel(cx - 1, cy + 1, ember);
            img.SetPixel(cx + 1, cy + 1, ember);
            img.SetPixel(cx,     cy - 2, ember);
            img.SetPixel(cx,     cy + 1, ember);

            // Stone ring highlight (top edge of outer band)
            for (int x = 1; x < TS - 1; x++) img.SetPixel(x, 1, stoneHi);
            // Stone ring shadow (bottom edge)
            for (int x = 1; x < TS - 1; x++) img.SetPixel(x, TS - 2, stoneLo);

            // Border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }

            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.35 — Bed: a wooden frame with mattress (light fabric) +
        // pillow. Geometry: outer wood border, mattress region centred,
        // pillow on the upper portion. Tinted at runtime via per-instance
        // colour to match the chosen StructureMat.
        private static ImageTexture BakeBedSprite()
        {
            var frame   = new Color(0.70f, 0.55f, 0.40f, 1f);   // wood frame
            var mat     = new Color(0.92f, 0.88f, 0.78f, 1f);   // mattress (off-white)
            var pillow  = new Color(0.98f, 0.95f, 0.88f, 1f);   // pillow
            var stripe  = new Color(0.78f, 0.62f, 0.55f, 1f);   // mattress stripe
            var border  = new Color(0.30f, 0.20f, 0.12f, 1f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            // wood frame fill
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, frame);
            // mattress in centre (3 px inset)
            for (int y = 3; y < TS - 3; y++)
            for (int x = 3; x < TS - 3; x++)
                img.SetPixel(x, y, mat);
            // pillow (upper third)
            int pillowTop = 4, pillowBot = 6;
            for (int y = pillowTop; y <= pillowBot; y++)
            for (int x = 4; x < TS - 4; x++)
                img.SetPixel(x, y, pillow);
            // stripes across mattress (suggests bed lines)
            for (int x = 3; x < TS - 3; x++) img.SetPixel(x, 9,  stripe);
            for (int x = 3; x < TS - 3; x++) img.SetPixel(x, 11, stripe);
            // outer border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.36 — Meditation Shrine: small altar with a glowing focal
        // point (a lit candle or magic gem). Solitary recreation.
        private static ImageTexture BakeShrineSprite()
        {
            var stone    = new Color(0.55f, 0.50f, 0.45f, 1f);
            var altar    = new Color(0.70f, 0.65f, 0.58f, 1f);
            var glow     = new Color(0.95f, 0.85f, 0.55f, 1f);
            var glowHi   = new Color(1.0f,  1.0f,  0.85f, 1f);
            var border   = new Color(0.25f, 0.20f, 0.18f, 1f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, stone);
            // altar base (lower 1/3)
            for (int y = TS - 5; y < TS - 1; y++)
            for (int x = 3; x < TS - 3; x++)
                img.SetPixel(x, y, altar);
            // candle / gem in centre top
            int cx = TS / 2, cy = TS / 2 - 1;
            img.SetPixel(cx, cy, glow);
            img.SetPixel(cx - 1, cy, glow);
            img.SetPixel(cx + 1, cy, glow);
            img.SetPixel(cx, cy - 1, glowHi);
            img.SetPixel(cx, cy + 1, glow);
            // outer border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.36 — Shroom Board (cerebral recreation, mushroom-themed
        // board game). Wood surface with grid pattern + 2 mushroom tokens.
        private static ImageTexture BakeBoardSprite()
        {
            var wood    = new Color(0.65f, 0.50f, 0.35f, 1f);
            var grid    = new Color(0.45f, 0.32f, 0.22f, 1f);
            var stem    = new Color(0.95f, 0.90f, 0.78f, 1f);
            var cap     = new Color(0.85f, 0.35f, 0.30f, 1f);
            var border  = new Color(0.30f, 0.20f, 0.12f, 1f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);
            // 3x3 grid lines
            int step = TS / 3;
            for (int x = 0; x < TS; x++) { img.SetPixel(x, step, grid); img.SetPixel(x, step * 2, grid); }
            for (int y = 0; y < TS; y++) { img.SetPixel(step, y, grid); img.SetPixel(step * 2, y, grid); }
            // two mushroom tokens (top-left + bottom-right cells)
            void DrawShroom(int ox, int oy)
            {
                img.SetPixel(ox,     oy + 1, stem);
                img.SetPixel(ox + 1, oy + 1, stem);
                img.SetPixel(ox - 1, oy,     cap);
                img.SetPixel(ox,     oy,     cap);
                img.SetPixel(ox + 1, oy,     cap);
                img.SetPixel(ox + 2, oy,     cap);
            }
            DrawShroom(3,  3);
            DrawShroom(TS - 5, TS - 5);
            // border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.36 — Gossip Bench (social recreation). Two-seat bench
        // facing centre, suggests two shroomps chatting.
        private static ImageTexture BakeBenchSprite()
        {
            var wood    = new Color(0.62f, 0.46f, 0.30f, 1f);
            var seat    = new Color(0.78f, 0.62f, 0.45f, 1f);
            var leg     = new Color(0.40f, 0.28f, 0.16f, 1f);
            var border  = new Color(0.25f, 0.18f, 0.10f, 1f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);
            // seat top (top third)
            for (int y = 4; y < 7; y++)
            for (int x = 2; x < TS - 2; x++)
                img.SetPixel(x, y, seat);
            // backrest (above seat)
            for (int y = 1; y < 4; y++)
            for (int x = 3; x < TS - 3; x++)
                img.SetPixel(x, y, wood);
            // legs (below seat)
            for (int y = 8; y < TS - 1; y++)
            {
                img.SetPixel(3, y, leg);
                img.SetPixel(TS - 4, y, leg);
            }
            // border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.37 — Table (eating surface). Flat wood top with central
        // grain pattern; legs at corners.
        private static ImageTexture BakeTableSprite()
        {
            var wood    = new Color(0.72f, 0.56f, 0.38f, 1f);
            var grain   = new Color(0.58f, 0.42f, 0.28f, 1f);
            var leg     = new Color(0.42f, 0.30f, 0.18f, 1f);
            var border  = new Color(0.30f, 0.20f, 0.12f, 1f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            // wood fill
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, wood);
            // grain lines (two horizontal accents)
            for (int x = 2; x < TS - 2; x++)
            {
                img.SetPixel(x, 5, grain);
                img.SetPixel(x, TS - 6, grain);
            }
            // corner legs (small dark squares)
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                img.SetPixel(1 + dx, 1 + dy, leg);
                img.SetPixel(TS - 3 + dx, 1 + dy, leg);
                img.SetPixel(1 + dx, TS - 3 + dy, leg);
                img.SetPixel(TS - 3 + dx, TS - 3 + dy, leg);
            }
            // border
            for (int x = 0; x < TS; x++) { img.SetPixel(x, 0, border); img.SetPixel(x, TS - 1, border); }
            for (int y = 0; y < TS; y++) { img.SetPixel(0, y, border); img.SetPixel(TS - 1, y, border); }
            return ImageTexture.CreateFromImage(img);
        }

        // v0.5.84t — Torch sprite. Wood stick rising to a flame at the top.
        // 16×16 sprite. Player sees a vertical haft + orange-yellow flame.
        // No actual light emission yet (Phase 10 glow grid will visualize
        // the Room.TorchCount data this sprite represents).
        private static ImageTexture BakeTorchSprite()
        {
            var stick   = new Color(0.55f, 0.36f, 0.18f, 1f);
            var stickEd = new Color(0.28f, 0.16f, 0.06f, 1f);
            var flameO  = new Color(1.00f, 0.55f, 0.10f, 1f);   // orange outer
            var flameY  = new Color(1.00f, 0.85f, 0.30f, 1f);   // yellow mid
            var flameW  = new Color(1.00f, 1.00f, 0.85f, 1f);   // white-hot core
            var dark    = new Color(0f, 0f, 0f, 0f);

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            // Transparent background.
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, dark);
            // Stick — vertical at x=7-8, rows 7..14.
            for (int y = 7; y <= 14; y++)
            {
                img.SetPixel(7, y, stick);
                img.SetPixel(8, y, stick);
            }
            // Stick edges (darker outline).
            for (int y = 7; y <= 14; y++)
            {
                img.SetPixel(6, y, stickEd);
                img.SetPixel(9, y, stickEd);
            }
            // Flame — teardrop pointing up. Layered concentric color rings.
            // Outer orange flame.
            int[][] outerFlame = new int[][]
            {
                new[]{ 7, 1 }, new[]{ 8, 1 },
                new[]{ 6, 2 }, new[]{ 7, 2 }, new[]{ 8, 2 }, new[]{ 9, 2 },
                new[]{ 5, 3 }, new[]{ 6, 3 }, new[]{ 7, 3 }, new[]{ 8, 3 }, new[]{ 9, 3 }, new[]{ 10, 3 },
                new[]{ 5, 4 }, new[]{ 6, 4 }, new[]{ 7, 4 }, new[]{ 8, 4 }, new[]{ 9, 4 }, new[]{ 10, 4 },
                new[]{ 6, 5 }, new[]{ 7, 5 }, new[]{ 8, 5 }, new[]{ 9, 5 },
                new[]{ 7, 6 }, new[]{ 8, 6 },
            };
            foreach (var p in outerFlame) img.SetPixel(p[0], p[1], flameO);
            // Yellow inner.
            int[][] innerFlame = new int[][]
            {
                new[]{ 7, 2 }, new[]{ 8, 2 },
                new[]{ 6, 3 }, new[]{ 7, 3 }, new[]{ 8, 3 }, new[]{ 9, 3 },
                new[]{ 7, 4 }, new[]{ 8, 4 },
            };
            foreach (var p in innerFlame) img.SetPixel(p[0], p[1], flameY);
            // White-hot core.
            img.SetPixel(7, 3, flameW);
            img.SetPixel(8, 3, flameW);
            return ImageTexture.CreateFromImage(img);
        }

        // v0.6.2 — Demolish-as-task. Red X overlay drawn on top of any
        // built structure flagged MarkedForDemolition. Reads as the
        // RimWorld-style "deconstruct" mark: two thick diagonal red bands
        // forming an X, transparent everywhere else so the structure
        // sprite shows through behind it. The per-instance tint colour
        // bumps the alpha from 0.55 (just-painted) to 0.95 (near-complete)
        // as DemolitionProgress accumulates, giving the player at-a-glance
        // visibility into how close the tear-down is.
        private static ImageTexture BakeDemolishMarkSprite()
        {
            var trans = new Color(0f, 0f, 0f, 0f);
            var bar   = new Color(1.00f, 0.20f, 0.20f, 1.00f);   // bright red
            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, trans);
            // Thick diagonals (3 pixels wide) — TL↘BR and TR↙BL bands.
            for (int i = 0; i < TS; i++)
            {
                for (int t = -1; t <= 1; t++)
                {
                    int aX = i + t,           aY = i;
                    int bX = (TS - 1 - i) + t, bY = i;
                    if (aX >= 0 && aX < TS) img.SetPixel(aX, aY, bar);
                    if (bX >= 0 && bX < TS) img.SetPixel(bX, bY, bar);
                }
            }
            return ImageTexture.CreateFromImage(img);
        }

        private static ImageTexture BakeBlueprintSprite()
        {
            var fill   = new Color(0.85f, 0.95f, 1.00f, 0.25f);   // translucent ghost
            var border = new Color(0.85f, 0.95f, 1.00f, 0.85f);   // strong outline

            var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
            for (int y = 0; y < TS; y++)
            for (int x = 0; x < TS; x++)
                img.SetPixel(x, y, fill);

            // Dashed border
            for (int x = 0; x < TS; x++)
            {
                if ((x / 2) % 2 == 0)
                {
                    img.SetPixel(x, 0,      border);
                    img.SetPixel(x, TS - 1, border);
                }
            }
            for (int y = 0; y < TS; y++)
            {
                if ((y / 2) % 2 == 0)
                {
                    img.SetPixel(0,      y, border);
                    img.SetPixel(TS - 1, y, border);
                }
            }

            return ImageTexture.CreateFromImage(img);
        }
    }
}
