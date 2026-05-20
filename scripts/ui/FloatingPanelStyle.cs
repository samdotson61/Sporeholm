using Godot;

namespace Sporeholm.UI
{
    // Roadmap §3.x.7 — shared StyleBoxFlat factory for floating panels.
    // Every Phase-3.x UI element calls Make() (and optionally MakeHover() /
    // MakeActive()) to get the same rounded-rectangle + drop-shadow + border
    // treatment, so visual changes propagate by editing one file rather than
    // chasing per-panel theme overrides across the UI tree.
    public static class FloatingPanelStyle
    {
        // Default floating panel (HUD capsule / message log / shroomp card / etc.).
        public static StyleBoxFlat Make() => Build(
            bg: UITheme.PanelBg,
            border: UITheme.PanelBorderColour);

        // Hover state — slightly lighter background, brighter border.
        public static StyleBoxFlat MakeHover() => Build(
            bg: UITheme.PanelHover,
            border: UITheme.PanelBorderFocus,
            borderWidth: UITheme.PanelBorder + 1);

        // Active / pressed state for toggle buttons in toolbars.
        public static StyleBoxFlat MakeActive() => Build(
            bg: UITheme.PanelActive,
            border: UITheme.PanelBorderFocus,
            borderWidth: UITheme.PanelBorder + 1);

        // Toolbar-button style (more compact padding than a full panel).
        public static StyleBoxFlat MakeToolbarButton(bool active = false) =>
            Build(
                bg: active ? UITheme.PanelActive : UITheme.PanelBg,
                border: active ? UITheme.PanelBorderFocus : UITheme.PanelBorderColour,
                borderWidth: active ? UITheme.PanelBorder + 1 : UITheme.PanelBorder,
                contentPadX: 8,
                contentPadY: 6);

        private static StyleBoxFlat Build(
            Color bg,
            Color border,
            int borderWidth = UITheme.PanelBorder,
            int contentPadX = UITheme.ContentPadX,
            int contentPadY = UITheme.ContentPadY)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(UITheme.PanelCorner);
            s.SetBorderWidthAll(borderWidth);
            s.BorderColor         = border;
            s.ContentMarginLeft   = contentPadX;
            s.ContentMarginRight  = contentPadX;
            s.ContentMarginTop    = contentPadY;
            s.ContentMarginBottom = contentPadY;
            s.ShadowColor         = UITheme.ShadowColour;
            s.ShadowSize          = UITheme.ShadowSize;
            s.ShadowOffset        = new Vector2(0, 2);
            return s;
        }
    }
}
