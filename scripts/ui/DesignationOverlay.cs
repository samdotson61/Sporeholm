using Godot;
using SmurfulationC.World;

// v0.3.21 — sibling Node2D to LocalMapRenderer that draws semi-transparent
// coloured rectangles + glyphs on every tile flagged DesignatedForExcavation
// / Gather / ChopWood / Cut.
//
// v0.4.25 — `MultiMeshInstance2D`-based rendering. The pre-v0.4.25 path
// emitted ~3 procedural canvas commands per designation (filled rect,
// outline rect, glyph) and re-built that list every redraw. At 500+
// designations on a busy dig site that was 1 500+ canvas commands every
// `DesignationChanged` event, all on the main thread. Now: one pre-baked
// 16×16 RGBA sprite per `DesignationKind` (tint + border + glyph all
// baked into the texture), one `MultiMeshInstance2D` child per kind, and
// per-tile transforms pushed into the multimesh buffer when the
// designation set changes. The body pass becomes exactly 4 instanced GPU
// draw calls regardless of designation count.
public partial class DesignationOverlay : Node2D
{
    private LocalMap? _map;

    // v0.3.31 — DesignationChanged fires from the sim thread; main-thread
    // `_Process` consumes the dirty flag and rebuilds the multimesh
    // instance buffers at most once per frame, even when many tiles flip
    // in the same tick.
    private volatile bool _dirty;

    private const int TS    = LocalMap.TileSize;
    private const int Kinds = 4;
    private const int MaxInstancesPerKind = 8000;  // generous; ~1 designation per tile

    private MultiMeshInstance2D[] _mmi = System.Array.Empty<MultiMeshInstance2D>();
    private int[] _counts = System.Array.Empty<int>();

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        // Above the map texture but below the smurf colony view so a smurf
        // walking over a designation visually obscures the tile tint.
        ZIndex = 0;

        var quad = new QuadMesh { Size = new Vector2(TS, TS) };
        _mmi    = new MultiMeshInstance2D[Kinds];
        _counts = new int[Kinds];
        for (int kind = 0; kind < Kinds; kind++)
        {
            var tex = BakeKindSprite((LocalMap.DesignationKind)kind);
            _mmi[kind] = CreateMmi(quad, tex);
        }
    }

    public void SetMap(LocalMap map)
    {
        if (_map != null) _map.DesignationChanged -= OnDesignationChanged;
        _map = map;
        _map.DesignationChanged += OnDesignationChanged;
        _dirty = true;
    }

    public override void _ExitTree()
    {
        if (_map != null) _map.DesignationChanged -= OnDesignationChanged;
    }

    private void OnDesignationChanged(int x, int y) => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty || _map == null || _mmi.Length == 0) return;
        _dirty = false;
        RebuildInstances();
    }

    // v0.4.27 — force a synchronous rebuild from the same input frame
    // that issued the designation. Called by `GameController` after
    // `SimulationManager.DesignateRect` so the player sees the new
    // designations on the very next render, not one main-thread frame
    // (~90 ms at 11 FPS) later when `_Process` next picks up the dirty
    // flag. Safe to call from any frame: cheap if `_dirty` is false.
    public void RebuildIfDirty()
    {
        if (!_dirty || _map == null || _mmi.Length == 0) return;
        _dirty = false;
        RebuildInstances();
    }

    private void RebuildInstances()
    {
        for (int i = 0; i < Kinds; i++) _counts[i] = 0;

        var designations = _map!.SnapshotDesignations();
        for (int i = 0; i < designations.Count; i++)
        {
            var (x, y, kind) = designations[i];
            int kIdx = (int)kind;
            if ((uint)kIdx >= (uint)Kinds) continue;
            int idx = _counts[kIdx];
            if (idx >= MaxInstancesPerKind) continue;

            // QuadMesh of size (TS, TS) is centred at its local origin, so
            // translate by tile-pixel + half-tile to land the quad over
            // exactly the (x, y) tile.
            var origin = new Vector2(x * TS + TS * 0.5f, y * TS + TS * 0.5f);
            _mmi[kIdx].Multimesh.SetInstanceTransform2D(idx, new Transform2D(0f, origin));
            _counts[kIdx] = idx + 1;
        }

        for (int i = 0; i < Kinds; i++)
            _mmi[i].Multimesh.VisibleInstanceCount = _counts[i];
    }

    private MultiMeshInstance2D CreateMmi(Mesh mesh, Texture2D tex)
    {
        var mm = new MultiMesh
        {
            Mesh                 = mesh,
            TransformFormat      = MultiMesh.TransformFormatEnum.Transform2D,
            InstanceCount        = MaxInstancesPerKind,
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

    // ── Sprite baking ──────────────────────────────────────────────────────

    // Pre-baked 16×16 RGBA sprite per designation kind: semi-transparent
    // tile-tint background, 1-px border, kind-specific glyph in the centre.
    // Replaces the v0.3.35 procedural DrawLine / DrawCircle / DrawRect
    // commands that fired per designation per redraw.
    private static ImageTexture BakeKindSprite(LocalMap.DesignationKind kind)
    {
        var (fill, glyph) = ColoursFor(kind);

        var img = Image.CreateEmpty(TS, TS, false, Image.Format.Rgba8);
        // Semi-transparent fill across the full tile.
        for (int y = 0; y < TS; y++)
        for (int x = 0; x < TS; x++)
            img.SetPixel(x, y, fill);

        // 1-px border in the glyph (accent) colour.
        for (int x = 0; x < TS; x++)
        {
            img.SetPixel(x, 0,      glyph);
            img.SetPixel(x, TS - 1, glyph);
        }
        for (int y = 0; y < TS; y++)
        {
            img.SetPixel(0,      y, glyph);
            img.SetPixel(TS - 1, y, glyph);
        }

        // Kind-specific glyph in the centre. (cx, cy) is the tile centre.
        int cx = TS / 2;
        int cy = TS / 2;
        switch (kind)
        {
            case LocalMap.DesignationKind.Excavate:
                // Pickaxe: diagonal handle + short cross-bar at the head.
                DrawLineOnImage(img, cx - 4, cy - 4, cx + 4, cy + 4, glyph);
                DrawLineOnImage(img, cx - 5, cy - 2, cx - 2, cy - 5, glyph);
                break;
            case LocalMap.DesignationKind.Gather:
                // Three-berry cluster.
                FillCircleOnImage(img, cx - 2, cy + 1, 2.0f, glyph);
                FillCircleOnImage(img, cx + 2, cy + 1, 2.0f, glyph);
                FillCircleOnImage(img, cx,     cy - 2, 2.0f, glyph);
                break;
            case LocalMap.DesignationKind.ChopWood:
                // Axe: anti-diagonal handle + short crossbar near the head.
                DrawLineOnImage(img, cx - 4, cy + 4, cx + 4, cy - 4, glyph);
                DrawLineOnImage(img, cx + 1, cy - 4, cx + 5, cy - 1, glyph);
                break;
            case LocalMap.DesignationKind.Cut:
                // Crossed scissors / X.
                DrawLineOnImage(img, cx - 4, cy - 4, cx + 4, cy + 4, glyph);
                DrawLineOnImage(img, cx - 4, cy + 4, cx + 4, cy - 4, glyph);
                break;
        }

        return ImageTexture.CreateFromImage(img);
    }

    private static (Color Fill, Color Glyph) ColoursFor(LocalMap.DesignationKind k) => k switch
    {
        LocalMap.DesignationKind.Excavate => (new(0.95f, 0.45f, 0.15f, 0.32f), new(1.00f, 0.85f, 0.55f, 0.95f)),
        LocalMap.DesignationKind.Gather   => (new(0.40f, 0.85f, 0.30f, 0.32f), new(0.95f, 1.00f, 0.70f, 0.95f)),
        LocalMap.DesignationKind.ChopWood => (new(0.65f, 0.40f, 0.20f, 0.32f), new(0.95f, 0.75f, 0.50f, 0.95f)),
        LocalMap.DesignationKind.Cut      => (new(0.40f, 0.75f, 0.85f, 0.32f), new(0.85f, 1.00f, 1.00f, 0.95f)),
        _                                  => (new(0.40f, 0.85f, 0.30f, 0.32f), new(0.95f, 1.00f, 0.70f, 0.95f)),
    };

    // Bresenham line into an Image (one-time bake; cheap).
    private static void DrawLineOnImage(Image img, int x0, int y0, int x1, int y1, Color col)
    {
        int dx = System.Math.Abs(x1 - x0), dy = -System.Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int w = img.GetWidth(), h = img.GetHeight();
        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h) img.SetPixel(x0, y0, col);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // Scan-fill disk on Image (one-time bake).
    private static void FillCircleOnImage(Image img, int cx, int cy, float r, Color col)
    {
        int ir = (int)System.Math.Ceiling(r);
        float r2 = r * r;
        int w = img.GetWidth(), h = img.GetHeight();
        for (int dy = -ir; dy <= ir; dy++)
        {
            int py = cy + dy;
            if ((uint)py >= (uint)h) continue;
            float dy2 = dy * dy;
            if (dy2 > r2) continue;
            int halfW = (int)System.Math.Floor(System.Math.Sqrt(r2 - dy2));
            int xLo = System.Math.Max(0, cx - halfW);
            int xHi = System.Math.Min(w - 1, cx + halfW);
            for (int x = xLo; x <= xHi; x++) img.SetPixel(x, py, col);
        }
    }
}
