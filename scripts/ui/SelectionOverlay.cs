using Godot;
using Sporeholm.World;

// v0.4.47 — single-tile selection indicator. Draws RimWorld-style white
// corner brackets around the currently-selected ground tile when the
// player has opened a `TilePropertiesPanel` via a no-tool left-click.
// Lives as a sibling Node2D in the GameController world layer so it
// scales with the camera the same way `LocalMapRenderer` does. Cleared
// when the inspector closes.
public partial class SelectionOverlay : Node2D
{
    private bool _hasSelection;
    private int  _tx, _ty;

    public override void _Ready()
    {
        // Sit between map render (z = -10) and shroomp colony (z = 1) so
        // the bracket halos under any items but over the bare terrain.
        ZIndex      = 0;
        ZAsRelative = false;
    }

    public void SetTileSelection(int tx, int ty)
    {
        if (_hasSelection && _tx == tx && _ty == ty) return;
        _hasSelection = true;
        _tx = tx; _ty = ty;
        QueueRedraw();
    }

    public void ClearSelection()
    {
        if (!_hasSelection) return;
        _hasSelection = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_hasSelection) return;
        float px = _tx * LocalMap.TileSize;
        float py = _ty * LocalMap.TileSize;
        // Brackets sit 1 px outside the tile so the indicator frames the
        // full 16×16 cell with a tiny breathing strip.
        var rect = new Rect2(px - 1f, py - 1f, LocalMap.TileSize + 2f, LocalMap.TileSize + 2f);
        ShroompColonyView.DrawSelectionBrackets(this, rect);
    }
}
