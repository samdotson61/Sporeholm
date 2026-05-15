using Godot;
using SmurfulationC.World;

// v0.5.0 (Phase 5A — rimport N1) — sibling Node2D to LocalMapRenderer that
// draws a translucent fill on every cell belonging to a stockpile zone.
// Mirrors the v0.4.56 throttle pattern from DesignationOverlay/ItemDropOverlay
// (per-tile dirty set + 200 ms refresh interval) so paint events don't
// thrash the renderer.
//
// Visual: low-alpha yellow fill, slightly stronger border, no glyph. Per-
// zone colour variation (RimWorld pattern — each new zone gets a deterministic
// hue) can land in Phase 5C alongside the stockpile-config UI; v0.5.0
// ships with one shared yellow.
public partial class StockpileOverlay : Node2D
{
    private LocalMap? _map;
    private const int TS = LocalMap.TileSize;

    // Single MultiMeshInstance2D drawing a tinted square per stockpile cell.
    private MultiMeshInstance2D _mmi = null!;
    private int                 _count = 0;
    private const int MaxInstances = 16000;   // headroom for big stockpile colonies

    private readonly System.Collections.Generic.HashSet<(int X, int Y)> _dirtyTiles = new();
    private readonly object _dirtyLock = new();
    private const double MinRefreshIntervalSec = 0.20;
    private double _timeSinceRefresh = 1.0;

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        // v0.5.6 — was ZIndex=-1, which in Godot 4 means *render before*
        // siblings (i.e., UNDERNEATH them). The map renderer is z=0, so
        // the map's tiles drew on top of the stockpile tint and hid it
        // entirely — Sam: "Stockpile zones actually not visible on the
        // ground." Direction was inverted in the original comment.
        //
        // Correct ordering: same z=0 baseline as the map + designations,
        // and rely on tree order — GameController adds the stockpile
        // overlay right after the map but before designations / items /
        // selection so the tint sits ON the floor, BENEATH the
        // designation glyphs and item icons (which is what the original
        // intent was). Smurfs at z=1 still walk over everything.
        ZIndex = 0;

        var quad = new QuadMesh { Size = new Vector2(TS, TS) };
        var tex = BakeStockpileSprite();
        _mmi = CreateMmi(quad, tex);
    }

    public void SetMap(LocalMap map)
    {
        if (_map != null) _map.StockpileChanged -= OnStockpileChanged;
        _map = map;
        _map.StockpileChanged += OnStockpileChanged;
        // Force a refresh on bind via the sentinel pattern from v0.4.56.
        lock (_dirtyLock) _dirtyTiles.Add((-1, -1));
        _timeSinceRefresh = MinRefreshIntervalSec;
    }

    public override void _ExitTree()
    {
        if (_map != null) _map.StockpileChanged -= OnStockpileChanged;
    }

    private void OnStockpileChanged(int x, int y)
    {
        lock (_dirtyLock) _dirtyTiles.Add((x, y));
    }

    public override void _Process(double delta)
    {
        _timeSinceRefresh += delta;
        if (_map == null) return;
        bool hasDirty;
        lock (_dirtyLock) hasDirty = _dirtyTiles.Count > 0;
        if (!hasDirty) return;
        if (_timeSinceRefresh < MinRefreshIntervalSec) return;

        lock (_dirtyLock) _dirtyTiles.Clear();
        _timeSinceRefresh = 0;

        RebuildInstances();
    }

    private void RebuildInstances()
    {
        if (_map == null) return;
        _count = 0;
        var zones = _map.SnapshotStockpileZones();
        foreach (var z in zones)
        {
            foreach (var (cx, cy) in z.Cells)
            {
                if (_count >= MaxInstances) break;
                var origin = new Vector2(cx * TS + TS * 0.5f, cy * TS + TS * 0.5f);
                _mmi.Multimesh.SetInstanceTransform2D(_count, new Transform2D(0f, origin));
                _count++;
            }
            if (_count >= MaxInstances) break;
        }
        _mmi.Multimesh.VisibleInstanceCount = _count;
    }

    private MultiMeshInstance2D CreateMmi(Mesh mesh, Texture2D tex)
    {
        var mm = new MultiMesh
        {
            Mesh                 = mesh,
            TransformFormat      = MultiMesh.TransformFormatEnum.Transform2D,
            InstanceCount        = MaxInstances,
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

    // Pre-baked 16×16 RGBA sprite: low-alpha yellow fill + slightly stronger
    // gold border. Identical for every cell; per-zone colour can be added
    // in Phase 5C.
    private static ImageTexture BakeStockpileSprite()
    {
        var fill   = new Color(1.0f, 0.92f, 0.35f, 0.18f);   // pale yellow at low alpha
        var border = new Color(0.92f, 0.78f, 0.20f, 0.45f);  // gold at 45% alpha

        var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
        for (int y = 0; y < TS; y++)
        for (int x = 0; x < TS; x++)
            img.SetPixel(x, y, fill);

        for (int x = 0; x < TS; x++)
        {
            img.SetPixel(x, 0,      border);
            img.SetPixel(x, TS - 1, border);
        }
        for (int y = 0; y < TS; y++)
        {
            img.SetPixel(0,      y, border);
            img.SetPixel(TS - 1, y, border);
        }

        return ImageTexture.CreateFromImage(img);
    }
}
