using Godot;
using System.Collections.Generic;

// RimWorld/DF-style event message log, anchored to the bottom-left corner.
//
// Design for future configurability
// ──────────────────────────────────
// • Every message has a Category. SetCategoryVisible() toggles a category on or off;
//   hidden categories are silently dropped in Post().
// • MessagePosted fires after each accepted message. GameController (or a future
//   SettingsPanel) can subscribe and pause the simulation on specific categories,
//   e.g. Category.Death or Category.Combat.
// • Both hooks are intentionally minimal — no UI for them yet. They exist so the
//   plumbing is in place before the settings screen is built.
public partial class MessageLog : Control
{
    public enum Category { Birth, Death, MoodDrop, Combat, Research, General }

    // Fired after a message is accepted (post-filter). Subscribe to implement
    // pause-on-event or notification sounds in a later phase.
    public event System.Action<Category, string>? MessagePosted;

    // ── Internal message record ────────────────────────────────────────────────

    private sealed class Entry
    {
        public string   Text { get; init; } = "";
        public Category Cat  { get; init; }
        public Color    Col  { get; init; }
        public string   Date { get; init; } = "";
        public float    Age  { get; set;  }   // seconds since posted
    }

    private const int   MaxVisible        = 10;
    private const int   FontSize          = 9;
    private const int   LineHeight        = FontSize + 3;
    private const float FadeStartSeconds  = 15f;
    private const float FadeDuration      = 2f;   // seconds to fade from fully opaque to gone

    // All categories visible by default; toggle via SetCategoryVisible().
    private readonly Dictionary<Category, bool> _filter = new()
    {
        [Category.Birth]    = true,
        [Category.Death]    = true,
        [Category.MoodDrop] = true,
        [Category.Combat]   = true,
        [Category.Research] = true,
        [Category.General]  = true,
    };

    // Newest entry first.
    private readonly LinkedList<Entry> _entries = new();
    private Label[] _lines = System.Array.Empty<Label>();

    // ── Initialisation ─────────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Bottom-left corner, measuring upward from the screen bottom edge.
        AnchorLeft   = 0f;
        AnchorRight  = 0f;
        AnchorTop    = 1f;
        AnchorBottom = 1f;

        OffsetLeft   = 10f;
        OffsetRight  = 510f;
        OffsetTop    = -(20f + MaxVisible * LineHeight);
        OffsetBottom = -20f;

        MouseFilter = MouseFilterEnum.Ignore;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 2);
        AddChild(vbox);

        _lines = new Label[MaxVisible];
        for (int i = 0; i < MaxVisible; i++)
        {
            var lbl = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                AutowrapMode        = TextServer.AutowrapMode.Off,
                ClipText            = false,
                Visible             = false,
                MouseFilter         = MouseFilterEnum.Ignore,
            };
            lbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.70f));
            lbl.AddThemeConstantOverride("shadow_offset_x", 1);
            lbl.AddThemeConstantOverride("shadow_offset_y", 1);
            lbl.AddThemeFontSizeOverride("font_size", FontSize);
            vbox.AddChild(lbl);
            _lines[i] = lbl;
        }

        Visible = false;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    // Post a message. Returns false if the category is currently filtered out.
    // date should be a compact in-game timestamp, e.g. "D1 Y0" (pass "" to omit).
    public bool Post(string text, Category cat, string date = "")
    {
        if (!_filter.GetValueOrDefault(cat, true)) return false;

        _entries.AddFirst(new Entry { Text = text, Cat = cat, Col = CategoryColor(cat), Date = date });
        while (_entries.Count > MaxVisible)
            _entries.RemoveLast();

        Refresh();
        MessagePosted?.Invoke(cat, text);
        return true;
    }

    // Show or hide an entire message category (future settings integration).
    public void SetCategoryVisible(Category cat, bool visible)
    {
        _filter[cat] = visible;
        Refresh();
    }

    // ── Per-frame age + fade ───────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_entries.Count == 0) return;

        float dt   = (float)delta;
        bool  dirty = false;

        // Iterate oldest-first (tail → head) so removals don't invalidate forward iteration.
        var node = _entries.Last;
        while (node != null)
        {
            var prev = node.Previous;
            node.Value.Age += dt;
            if (node.Value.Age >= FadeStartSeconds + FadeDuration)
            {
                _entries.Remove(node);
                dirty = true;
            }
            node = prev;
        }

        Refresh();
        if (dirty && _entries.Count == 0)
            Visible = false;
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private void Refresh()
    {
        int i = 0;
        foreach (var e in _entries)
        {
            if (i >= MaxVisible) break;
            var lbl = _lines[i];
            lbl.Text    = FormatEntry(e);
            lbl.AddThemeColorOverride("font_color", e.Col);
            lbl.Visible = true;

            float alpha = e.Age < FadeStartSeconds
                ? 1f
                : 1f - (e.Age - FadeStartSeconds) / FadeDuration;
            lbl.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(alpha, 0f, 1f));

            i++;
        }
        for (; i < MaxVisible; i++)
            _lines[i].Visible = false;

        Visible = _entries.Count > 0;
    }

    private static string FormatEntry(Entry e)
    {
        string datePfx = e.Date.Length > 0 ? $"[{e.Date}] " : "";
        return $"· {datePfx}{e.Text}";
    }

    private static Color CategoryColor(Category cat) => cat switch
    {
        Category.Birth    => new Color(0.55f, 0.95f, 0.55f),       // green
        Category.Death    => new Color(0.95f, 0.50f, 0.50f),       // red
        Category.MoodDrop => new Color(0.95f, 0.80f, 0.35f),       // amber
        Category.Combat   => new Color(0.95f, 0.55f, 0.35f),       // orange
        Category.Research => new Color(0.55f, 0.80f, 0.95f),       // blue
        _                 => new Color(1.00f, 1.00f, 1.00f, 0.88f), // white
    };
}
