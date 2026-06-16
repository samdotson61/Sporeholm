using Godot;
using System.Collections.Generic;
using Sporeholm.World;

// v0.8.5 (Phase 8) — draws pasture cells: the open holding areas where tamed
// livestock gather + graze. A tile is pasture iff it's in LocalMap's pasture set
// (the player paints it with the Pasture tool). Each cell gets a soft meadow-green
// tint so the player can see the grazing area at a glance.
//
// Same lightweight _Draw() pattern as GrowZoneOverlay (pasture cell counts are
// bounded; redraws fire only on PastureChanged). The dirty flag may be set from
// the main thread (PaintPasture via DesignateRect); the snapshot is rebuilt on
// the main thread in _Process before QueueRedraw, so _Draw only reads a local list.
public partial class PastureOverlay : Node2D
{
    private LocalMap? _map;
    private const int TS = LocalMap.TileSize;

    private volatile bool _dirty;
    private List<(int X, int Y)> _snapshot = new();

    private static readonly Color Fill   = new(0.50f, 0.74f, 0.42f, 0.20f);   // meadow-green
    private static readonly Color Border = new(0.40f, 0.62f, 0.30f, 0.45f);

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        // Same z=0 baseline as the stockpile / grow-zone tints; tree order keeps
        // the pasture tint on the floor, beneath structures / designations / units.
        ZIndex = 0;
    }

    public void SetMap(LocalMap map)
    {
        if (_map != null) _map.PastureChanged -= OnPastureChanged;
        _map = map;
        _map.PastureChanged += OnPastureChanged;
        _dirty = true;   // force a refresh on bind (also catches save-restored cells)
    }

    public override void _ExitTree()
    {
        if (_map != null) _map.PastureChanged -= OnPastureChanged;
    }

    private void OnPastureChanged() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty || _map == null) return;
        _dirty = false;
        _snapshot = _map.SnapshotPasture();   // takes the map's lock internally
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
            DrawRect(rect, Fill, filled: true);
            DrawRect(rect, Border, filled: false, width: 1f);
        }
    }
}
