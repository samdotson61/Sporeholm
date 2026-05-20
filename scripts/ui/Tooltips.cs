using Godot;
using System.Collections.Generic;

namespace Sporeholm.UI
{
    // v0.5.47 — RimWorld-parity tooltip system. Replaces Godot's default
    // `Control.TooltipText` rendering (which stretches across the entire
    // screen width when the text is long — Sam: "Plan walls. Shroomps
    // deliver Stone or Wood and build them — completed walls are
    // impassable. (Crafter role builds fastest.)" → 1700-px wide tooltip
    // obscuring the toolbar) with a compact, word-wrapped popup that
    // matches RimWorld's small-box-near-cursor convention.
    //
    // Mechanics modeled on RimWorld:
    //   • Hover delay: 0.45 s before the popup appears (RimWorld is ~0.4)
    //   • Position: cursor + (16, 16) offset, auto-flipped if near a
    //     viewport edge so the popup stays on-screen
    //   • Word-wrap at ~280 logical px max width
    //   • Dark panel background + light text + thin border
    //   • Font size follows the Settings → Tooltip Size dropdown (Large/
    //     Normal/Small) via SettingsPanel.TooltipFontSize
    //   • Disappears the moment the cursor leaves the source Control
    //   • Auto-flips to the LEFT/ABOVE the cursor if positioning right/
    //     below would clip off-screen
    //
    // Integration: rather than touching the 72 existing `TooltipText = …`
    // call sites scattered across the UI, `ScanAndConvert(root)` walks
    // the scene tree once after UI build (and again after every
    // UIScaleChanged rebuild), hooks every Control whose TooltipText is
    // set, suppresses the Godot default, and registers hover handlers
    // that show the custom popup. Marks each converted Control with a
    // meta tag so re-scans don't double-wrap.
    //
    // For new code: per-callsite `Tooltips.Apply(control, text)` is also
    // available — equivalent semantics, no need to set TooltipText first.
    public static class Tooltips
    {
        private const float HoverDelaySec  = 0.45f;
        private const int   MaxWidthLogical = 280;   // logical px before scale
        private const int   CursorOffsetX  = 16;
        private const int   CursorOffsetY  = 16;
        private const string ManagedMetaKey = "_tooltips_managed";

        // Active popup (null until first hover). One global popup reused
        // across all tooltips — switching between hovered Controls just
        // updates the text + position instead of allocating.
        private static Control? _popup;
        private static Label?   _popupLabel;
        private static long     _hoverToken;   // monotonic; invalidates pending Show timeouts

        // Apply the custom tooltip to a single Control. Suppresses Godot's
        // default tooltip + connects hover handlers to schedule the popup.
        // Safe to call on a Control multiple times — the managed-meta tag
        // prevents double-wiring (subsequent calls just update the text).
        public static void Apply(Control c, string text)
        {
            if (c == null) return;
            if (string.IsNullOrEmpty(text))
            {
                c.TooltipText = "";
                return;
            }
            // Stash the tooltip text in metadata so ScanAndConvert can
            // re-read it on re-scan (rebuilt panels lose connections).
            c.SetMeta("_tooltips_text", text);
            c.TooltipText = "";   // suppress Godot's default

            if (c.HasMeta(ManagedMetaKey)) return;
            c.SetMeta(ManagedMetaKey, true);

            c.MouseEntered += () => Schedule(c);
            c.MouseExited  += Cancel;
        }

        // Walks the tree under `root`, finds every Control whose
        // TooltipText is set, and converts each to use the custom popup.
        // Idempotent — managed Controls are skipped on re-scan. Cheap
        // enough to run on every UIScaleChanged because most UI panels
        // are ≤ 100 nodes deep and the walk is one pass.
        public static void ScanAndConvert(Node root)
        {
            if (root == null) return;
            foreach (var node in WalkTree(root))
            {
                if (node is Control c && !string.IsNullOrEmpty(c.TooltipText) && !c.HasMeta(ManagedMetaKey))
                {
                    Apply(c, c.TooltipText);
                }
            }
        }

        private static IEnumerable<Node> WalkTree(Node n)
        {
            yield return n;
            foreach (var child in n.GetChildren())
                foreach (var grand in WalkTree(child))
                    yield return grand;
        }

        // Schedule a popup show after HoverDelaySec. Each scheduling bumps
        // the token; Cancel() bumps it too, so a delayed Show closure that
        // fires after the cursor has left compares against the new token
        // and aborts.
        private static void Schedule(Control source)
        {
            long myToken = ++_hoverToken;
            string text = source.HasMeta("_tooltips_text")
                ? (string)source.GetMeta("_tooltips_text")
                : "";
            if (string.IsNullOrEmpty(text)) return;

            var timer = source.GetTree().CreateTimer(HoverDelaySec);
            timer.Timeout += () =>
            {
                if (myToken != _hoverToken) return;
                if (!IsInstanceValid(source) || !source.IsInsideTree()) return;
                Show(source, text);
            };
        }

        private static void Cancel()
        {
            _hoverToken++;
            if (_popup != null && _popup.IsInsideTree())
                _popup.Visible = false;
        }

        private static void Show(Control source, string text)
        {
            EnsurePopup(source);
            if (_popup == null || _popupLabel == null) return;

            // Read tooltip font size from settings each show. Cheap and
            // keeps the popup live-responsive when the player tweaks the
            // dropdown without a scene reload.
            int fontPt = LoadTooltipFontSize();
            _popupLabel.AddThemeFontSizeOverride("font_size", UITheme.Scaled(fontPt));
            _popupLabel.CustomMinimumSize = new Vector2(UITheme.Scaled(MaxWidthLogical), 0);
            _popupLabel.Text = text;

            // Resolve final size — Godot computes after a tick when the
            // label re-measures. CallDeferred so positioning uses the
            // post-relayout dimensions instead of the previous frame's
            // stale rect.
            _popup.Visible = true;
            // First pass: snap to cursor offset right away so the popup
            // appears at the right spot on this frame. Second pass:
            // clamp to viewport via a deferred Callable so the autowrap
            // Label has settled its size by the time we measure.
            _popup.Position = GetCursorOffsetPos(source);
            Callable.From(ClampPopupToViewport).CallDeferred();
        }

        // Position calculation — cursor + offset, auto-flipping if the
        // popup would clip off the right/bottom of the viewport.
        private static Vector2 GetCursorOffsetPos(Control source)
        {
            var vp = source.GetViewport();
            var mouse = vp.GetMousePosition();
            return new Vector2(mouse.X + CursorOffsetX, mouse.Y + CursorOffsetY);
        }

        // Called via CallDeferred after Show so the popup's size is
        // settled. Flips position left/up if the popup would clip the
        // viewport edge — RimWorld parity.
        private static void ClampPopupToViewport()
        {
            if (_popup == null) return;
            var vp = _popup.GetViewport();
            var size = _popup.Size;
            var vsize = vp.GetVisibleRect().Size;
            var pos = _popup.Position;
            if (pos.X + size.X > vsize.X) pos.X = vsize.X - size.X - 4;
            if (pos.Y + size.Y > vsize.Y) pos.Y = vsize.Y - size.Y - 4;
            if (pos.X < 4) pos.X = 4;
            if (pos.Y < 4) pos.Y = 4;
            _popup.Position = pos;
        }

        private static void EnsurePopup(Node anyNodeInTree)
        {
            if (_popup != null && IsInstanceValid(_popup) && _popup.IsInsideTree()) return;

            var bg = new StyleBoxFlat
            {
                BgColor          = new Color(0.12f, 0.12f, 0.12f, 0.97f),
                BorderColor      = new Color(0.55f, 0.45f, 0.18f, 1f),
                ShadowColor      = new Color(0f, 0f, 0f, 0.45f),
                ShadowSize       = 4,
                ContentMarginLeft   = 8f,
                ContentMarginRight  = 8f,
                ContentMarginTop    = 5f,
                ContentMarginBottom = 5f,
            };
            bg.SetBorderWidthAll(1);
            bg.SetCornerRadiusAll(3);

            var pc = new PanelContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible     = false,
                ZIndex      = 4096,   // above everything
            };
            pc.AddThemeStyleboxOverride("panel", bg);
            pc.TopLevel = true;

            var label = new Label
            {
                AutowrapMode      = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(UITheme.Scaled(MaxWidthLogical), 0),
                MouseFilter       = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeColorOverride("font_color", new Color(0.92f, 0.88f, 0.74f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.55f));
            label.AddThemeConstantOverride("shadow_offset_x", 1);
            label.AddThemeConstantOverride("shadow_offset_y", 1);
            pc.AddChild(label);

            // Park the popup at the SceneTree root so it floats above
            // every other CanvasLayer (HUD, panels, etc).
            anyNodeInTree.GetTree().Root.AddChild(pc);
            _popup = pc;
            _popupLabel = label;
        }

        // Reads the Settings → Tooltip Size dropdown. Default "large"
        // (matches v0.3.30 player default). Routes through SettingsPanel's
        // existing font-size lookup so the three settings (large/normal/
        // small) map to the same point sizes as the legacy theme.
        private static int LoadTooltipFontSize()
        {
            var cfg = new ConfigFile();
            if (cfg.Load("user://settings.cfg") != Error.Ok)
                return SettingsPanel.TooltipFontSize("large");
            string ttSize = (string)cfg.GetValue("gameplay", "tooltip_size", "large");
            return SettingsPanel.TooltipFontSize(ttSize);
        }

        // Godot-friendly IsInstanceValid wrapper (avoids using GodotObject.
        // IsInstanceValid which requires a GodotObject reference).
        private static bool IsInstanceValid(GodotObject o) =>
            GodotObject.IsInstanceValid(o);
    }
}
