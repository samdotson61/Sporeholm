using Godot;

// v0.3.24 — RTS-style click-and-drag selection rectangle. Drawn while the
// player is dragging on empty terrain with no designation tool active. On
// release, GameController collects every smurf whose visual position falls
// inside the rect and adds them to the multi-selection. Low alpha so the
// player can still see which smurfs they're sweeping over.
public partial class SelectionBoxPreview : Node2D
{
    public bool DragActive { get; private set; }
    private Vector2 _start;
    private Vector2 _end;

    private static readonly Color FillColor    = new(0.45f, 0.85f, 1.00f, 0.12f);
    private static readonly Color OutlineColor = new(0.50f, 0.90f, 1.00f, 0.80f);

    public void Begin(Vector2 worldPos)
    {
        _start = worldPos;
        _end   = worldPos;
        DragActive = true;
        QueueRedraw();
    }

    public void Update(Vector2 worldPos)
    {
        if (!DragActive) return;
        _end = worldPos;
        QueueRedraw();
    }

    public void End()
    {
        DragActive = false;
        QueueRedraw();
    }

    // Returns the current world-space rectangle (normalised so x/y are min,
    // size is positive). Empty when DragActive is false.
    public Rect2 GetWorldRect()
    {
        var pos  = new Vector2(Mathf.Min(_start.X, _end.X), Mathf.Min(_start.Y, _end.Y));
        var size = new Vector2(Mathf.Abs(_end.X - _start.X), Mathf.Abs(_end.Y - _start.Y));
        return new Rect2(pos, size);
    }

    public override void _Draw()
    {
        if (!DragActive) return;
        var rect = GetWorldRect();
        DrawRect(rect, FillColor,    filled: true);
        DrawRect(rect, OutlineColor, filled: false, width: 1.5f);
    }
}
