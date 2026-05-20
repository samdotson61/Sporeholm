using Godot;
using Sporeholm.UI;
using Sporeholm.World;

// v0.3.21 — live preview rectangle drawn while the player is dragging a
// designation box. GameController sets the tile-grid rect and active tool;
// the node draws a transparent fill + bright outline aligned to tile edges so
// the player sees exactly which cells will be designated on mouse-up. The
// outline colour matches the active tool's glyph colour for instant feedback.
public partial class DragSelectionPreview : Node2D
{
    public bool DragActive { get; private set; }
    private int _x0, _y0, _x1, _y1;
    private DesignationTool _tool = DesignationTool.None;

    // v0.3.23 — the tool the drag started with. Used by GameController on
    // commit so that mid-drag tool switches (or the stranded-drag-finalizer
    // running after the toolbar consumed the release) commit with the same
    // semantics the player was painting with, not whatever ActiveTool is now.
    public DesignationTool Tool => _tool;

    private static readonly Color GatherOutline  = new(0.40f, 0.95f, 0.40f, 0.95f);
    private static readonly Color GatherFill     = new(0.40f, 0.95f, 0.40f, 0.18f);
    private static readonly Color ExcavateOutline = new(1.00f, 0.60f, 0.20f, 0.95f);
    private static readonly Color ExcavateFill    = new(1.00f, 0.60f, 0.20f, 0.18f);
    private static readonly Color RemoveOutline   = new(1.00f, 0.30f, 0.30f, 0.95f);
    private static readonly Color RemoveFill      = new(1.00f, 0.30f, 0.30f, 0.18f);
    private static readonly Color NoneOutline     = new(0.85f, 0.85f, 0.85f, 0.80f);
    private static readonly Color NoneFill        = new(0.85f, 0.85f, 0.85f, 0.10f);

    public void Begin(DesignationTool tool, int tileX, int tileY)
    {
        _tool = tool;
        _x0 = _x1 = tileX;
        _y0 = _y1 = tileY;
        DragActive = true;
        QueueRedraw();
    }

    public void Update(int tileX, int tileY)
    {
        if (!DragActive) return;
        if (tileX == _x1 && tileY == _y1) return;
        _x1 = tileX; _y1 = tileY;
        QueueRedraw();
    }

    public void End()
    {
        DragActive = false;
        QueueRedraw();
    }

    public (int x0, int y0, int x1, int y1) GetRect() => (_x0, _y0, _x1, _y1);

    public override void _Draw()
    {
        if (!DragActive) return;
        int ts = LocalMap.TileSize;
        int xMin = Mathf.Min(_x0, _x1);
        int xMax = Mathf.Max(_x0, _x1);
        int yMin = Mathf.Min(_y0, _y1);
        int yMax = Mathf.Max(_y0, _y1);

        var rect = new Rect2(xMin * ts, yMin * ts,
                             (xMax - xMin + 1) * ts, (yMax - yMin + 1) * ts);

        var (outline, fill) = _tool switch
        {
            DesignationTool.Gather   => (GatherOutline,   GatherFill),
            DesignationTool.Excavate => (ExcavateOutline, ExcavateFill),
            DesignationTool.Remove   => (RemoveOutline,   RemoveFill),
            _                        => (NoneOutline,    NoneFill),
        };

        DrawRect(rect, fill,    filled: true);
        DrawRect(rect, outline, filled: false, width: 2f);
    }
}
