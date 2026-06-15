using Godot;
using System.Collections.Generic;
using Sporeholm.World;

// v0.8.0 (Phase 8) — draws farm grow-zone tiles. A tile is farmland iff it
// carries a CropSlot on LocalMap (the _crops dictionary IS the grow-zone — no
// separate zone object, mirroring how the player paints with the Farm tool).
// Each such tile gets a translucent tint keyed to its CropStage so the player
// reads growth at a glance: tilled brown → green as it grows → gold when ripe.
//
// Unlike StockpileOverlay's MultiMesh (one shared sprite, fixed colour), this
// uses a _Draw() pass: crop counts are bounded and redraws fire only on
// CropsChanged (paint + stage transitions), so per-stage colouring is free and
// the code stays small. The dirty flag is set from either thread (sim-thread
// TickCrops or main-thread PaintGrowZone); the snapshot is rebuilt on the main
// thread in _Process before QueueRedraw, so _Draw only ever reads a local list.
public partial class GrowZoneOverlay : Node2D
{
    private LocalMap? _map;
    private const int TS = LocalMap.TileSize;

    private volatile bool _dirty;
    private List<(int X, int Y, CropType Crop, byte Stage, int Growth)> _snapshot = new();

    private static readonly Color Border = new(0.30f, 0.45f, 0.20f, 0.55f);

    // Tilled soil → greening crop → gold-ready. Ripe is the brightest/most
    // saturated so "harvest me" reads instantly across a large field.
    private static Color TintFor(CropStage st) => st switch
    {
        CropStage.Empty     => new(0.45f, 0.32f, 0.18f, 0.30f),   // tilled, awaiting sowing
        CropStage.Sown      => new(0.50f, 0.38f, 0.20f, 0.34f),
        CropStage.Sprouting => new(0.45f, 0.62f, 0.30f, 0.32f),
        CropStage.Growing   => new(0.40f, 0.74f, 0.34f, 0.34f),
        CropStage.Ripening  => new(0.58f, 0.80f, 0.28f, 0.38f),
        CropStage.Ripe      => new(0.95f, 0.84f, 0.25f, 0.48f),   // gold = ready to harvest
        CropStage.Wilted    => new(0.52f, 0.42f, 0.28f, 0.36f),
        _                   => new(0.45f, 0.32f, 0.18f, 0.30f),
    };

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        // Same z=0 baseline as the stockpile tint; tree order (added right
        // after StockpileOverlay) keeps the grow-zone tint ON the floor but
        // BENEATH structures / designations / items / shroomps.
        ZIndex = 0;
    }

    public void SetMap(LocalMap map)
    {
        if (_map != null) _map.CropsChanged -= OnCropsChanged;
        _map = map;
        _map.CropsChanged += OnCropsChanged;
        _dirty = true;
    }

    public override void _ExitTree()
    {
        if (_map != null) _map.CropsChanged -= OnCropsChanged;
    }

    private void OnCropsChanged() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty || _map == null) return;
        _dirty = false;
        _snapshot = _map.SnapshotCrops();   // takes the map's lock internally
        QueueRedraw();
    }

    public override void _Draw()
    {
        var snap = _snapshot;
        if (snap == null) return;
        for (int i = 0; i < snap.Count; i++)
        {
            var c = snap[i];
            var rect = new Rect2(c.X * TS, c.Y * TS, TS, TS);
            DrawRect(rect, TintFor((CropStage)c.Stage), filled: true);
            DrawRect(rect, Border, filled: false, width: 1f);
        }
    }
}
