using Godot;
using System.Collections.Generic;

// Full-screen modal panel for browsing named save slots.
// In Save mode: lists existing slots + "New Save" button; player names and confirms.
// In Load mode: lists existing slots; player clicks one to load.
public partial class SaveFileBrowser : Control
{
    public enum BrowserMode { Save, Load }

    [Signal] public delegate void SaveConfirmedEventHandler(string slotName);
    [Signal] public delegate void LoadConfirmedEventHandler(string slotName);
    [Signal] public delegate void CancelledEventHandler();

    private static readonly Color ParchBg  = new(0.84f, 0.76f, 0.56f, 0.97f);
    private static readonly Color DarkWood = new(0.20f, 0.12f, 0.04f);
    private static readonly Color Gold     = new(0.82f, 0.63f, 0.18f);
    private static readonly Color Green    = new(0.25f, 0.55f, 0.20f);
    private static readonly Color Red      = new(0.70f, 0.20f, 0.12f);

    private BrowserMode      _mode;
    private Label            _titleLabel  = null!;
    private VBoxContainer    _slotList    = null!;
    private Label            _statusLabel = null!;
    private LineEdit?        _newNameEdit;
    private string?          _renamingSlot;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        // Dark backdrop
        var backdrop = new ColorRect { Color = new Color(0.04f, 0.02f, 0.01f, 0.78f) };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        // Anchor-based card so it never overflows the viewport at any window size.
        var card = new PanelContainer();
        card.AnchorLeft = 0.5f; card.AnchorRight  = 0.5f;
        card.AnchorTop  = 0f;   card.AnchorBottom  = 1f;
        card.OffsetLeft = -360f; card.OffsetRight  = 360f;
        card.OffsetTop  = 30f;  card.OffsetBottom  = -30f;
        card.GrowVertical = GrowDirection.Both;
        var style = new StyleBoxFlat { BgColor = ParchBg };
        style.SetBorderWidthAll(4);
        style.BorderColor  = Gold;
        style.SetCornerRadiusAll(10);
        style.ShadowColor  = new Color(0f, 0f, 0f, 0.55f);
        style.ShadowSize   = 16;
        style.ShadowOffset = new Vector2(0, 5);
        card.AddThemeStyleboxOverride("panel", style);
        AddChild(card);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 24);
        card.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 14);
        margin.AddChild(vbox);

        // Title
        _titleLabel = MakeLabel("Save Game", 28, DarkWood);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(_titleLabel);

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.38f, 0.12f, 0.6f));
        vbox.AddChild(sep);

        // Slot list — expands to fill remaining card height.
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical    = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(scroll);

        // Inner margin keeps slot rows away from the scrollbar shadow.
        var scrollMargin = new MarginContainer();
        scrollMargin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scrollMargin.AddThemeConstantOverride("margin_right", 8);
        scroll.AddChild(scrollMargin);

        _slotList = new VBoxContainer();
        _slotList.AddThemeConstantOverride("separation", 8);
        _slotList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scrollMargin.AddChild(_slotList);

        // Status label
        _statusLabel = MakeLabel("", 16, Green);
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.Modulate = Colors.Transparent;
        vbox.AddChild(_statusLabel);

        // Cancel button
        var cancel = new AnimatedButton { Text = "✕   Cancel" };
        cancel.Pressed += () => { EmitSignal(SignalName.Cancelled); Close(); };
        vbox.AddChild(cancel);

        Visible = false;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void OpenForSave()
    {
        _mode = BrowserMode.Save;
        _titleLabel.Text = "Save Game";
        _renamingSlot = null;
        Refresh();
        Visible = true;
    }

    public void OpenForLoad()
    {
        _mode = BrowserMode.Load;
        _titleLabel.Text = "Load Game";
        _renamingSlot = null;
        Refresh();
        Visible = true;
    }

    public void Close()
    {
        _renamingSlot = null;
        Visible = false;
    }

    public void ShowStatus(string msg, Color? col = null)
    {
        _statusLabel.Text    = msg;
        _statusLabel.Modulate = Colors.White;
        _statusLabel.AddThemeColorOverride("font_color", col ?? Green);
        var t = CreateTween().SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Sine);
        t.TweenInterval(2.5f);
        t.TweenProperty(_statusLabel, "modulate", Colors.Transparent, 0.5f);
    }

    // ── Build slot list ────────────────────────────────────────────────────────

    public void Refresh()
    {
        foreach (var child in _slotList.GetChildren())
            child.QueueFree();

        var slots = SaveManager.Instance?.GetSaveSlots() ?? new System.Collections.Generic.List<SaveManager.SaveSlotInfo>();

        if (_mode == BrowserMode.Save)
            AddNewSaveRow();

        if (slots.Count == 0)
        {
            var empty = MakeLabel("No saves found.", 15, DarkWood);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            _slotList.AddChild(empty);
            return;
        }

        foreach (var slot in slots)
            _slotList.AddChild(BuildSlotRow(slot));
    }

    private void AddNewSaveRow()
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);

        var nameEdit = new LineEdit
        {
            PlaceholderText = "New save name…",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 36),
        };
        StyleLineEdit(nameEdit);
        hbox.AddChild(nameEdit);

        var saveBtn = new AnimatedButton { Text = "Save", Compact = true };
        saveBtn.Pressed += () =>
        {
            string name = SanitiseName(nameEdit.Text);
            if (name.Length == 0) { ShowStatus("Enter a save name.", new Color(0.8f, 0.5f, 0.1f)); return; }
            EmitSignal(SignalName.SaveConfirmed, name);
            ShowStatus($"Saved as '{name}'!");
            nameEdit.Text = "";
        };
        hbox.AddChild(saveBtn);

        _slotList.AddChild(hbox);

        // Divider below the new-save row
        var div = new HSeparator();
        div.AddThemeColorOverride("color", new Color(0.55f, 0.38f, 0.12f, 0.5f));
        _slotList.AddChild(div);
    }

    private Control BuildSlotRow(SaveManager.SaveSlotInfo slot)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        // Slot display name + info.
        // CustomMinimumSize prevents HBoxContainer from collapsing the VBox to zero.
        // TextOverrunBehavior trims long names with "…" instead of hiding them.
        var nameVbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nameVbox.CustomMinimumSize = new Vector2(80, 0);
        nameVbox.AddThemeConstantOverride("separation", 2);

        string displayName = slot.Name == "exit-save" ? "Exit Save" : slot.Name;
        var nameLabel = MakeLabel(displayName, 16, DarkWood);
        nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameVbox.AddChild(nameLabel);
        var infoLabel = MakeLabel(slot.DisplayText, 12, new Color(0.45f, 0.28f, 0.10f));
        infoLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameVbox.AddChild(infoLabel);
        row.AddChild(nameVbox);

        // Timestamp — shown before action buttons
        if (slot.LastModifiedUnix > 0)
        {
            var tsLabel = MakeLabel(FormatTimestamp(slot.LastModifiedUnix), 10,
                new Color(0.45f, 0.40f, 0.35f));
            tsLabel.HorizontalAlignment = HorizontalAlignment.Right;
            tsLabel.VerticalAlignment   = VerticalAlignment.Center;
            row.AddChild(tsLabel);
        }

        if (_mode == BrowserMode.Load)
        {
            // Load button
            var loadBtn = new AnimatedButton { Text = "Load", Compact = true };
            loadBtn.Pressed += () =>
            {
                EmitSignal(SignalName.LoadConfirmed, slot.Name);
                Close();
            };
            row.AddChild(loadBtn);
        }
        else
        {
            // Overwrite button
            var overBtn = new AnimatedButton { Text = "Overwrite", Compact = true };
            overBtn.Pressed += () =>
            {
                EmitSignal(SignalName.SaveConfirmed, slot.Name);
                ShowStatus($"Overwritten '{slot.Name}'!");
            };
            row.AddChild(overBtn);

            // Rename button — swaps name label to inline edit
            var renBtn = new AnimatedButton { Text = "Rename", Compact = true };
            renBtn.Pressed += () => StartRename(nameVbox, nameLabel, slot.Name);
            row.AddChild(renBtn);
        }

        // Delete button (available in both modes)
        var delBtn = new AnimatedButton { Text = "✕", Compact = true };
        delBtn.Modulate = new Color(1f, 0.55f, 0.45f);
        delBtn.Pressed += () =>
        {
            SaveManager.Instance?.DeleteSlot(slot.Name);
            Refresh();
            ShowStatus($"Deleted '{slot.Name}'.", Red);
        };
        row.AddChild(delBtn);

        return row;
    }

    private void StartRename(VBoxContainer nameVbox, Label nameLabel, string oldName)
    {
        if (_renamingSlot == oldName) return;
        _renamingSlot = oldName;

        // Replace name label with inline edit
        nameLabel.Visible = false;

        var edit = new LineEdit { Text = oldName, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        StyleLineEdit(edit);
        nameLabel.AddSibling(edit);
        edit.GrabFocus();
        edit.SelectAll();

        void Commit()
        {
            string newName = SanitiseName(edit.Text);
            if (newName.Length > 0 && newName != oldName)
            {
                SaveManager.Instance?.RenameSlot(oldName, newName);
                ShowStatus($"Renamed to '{newName}'.");
            }
            _renamingSlot = null;
            Refresh();
        }

        edit.TextSubmitted += _ => Commit();
        edit.FocusExited   += Commit;
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            EmitSignal(SignalName.Cancelled);
            Close();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string FormatTimestamp(long unixSeconds)
    {
        var dt = System.DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return dt.ToString("MMM d\nh:mm tt");
    }

    private static string SanitiseName(string raw)
    {
        // Strip characters that would break file paths.
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ')
                sb.Append(c);
        return sb.ToString().Trim().Replace(' ', '-').ToLowerInvariant();
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        l.MouseFilter = MouseFilterEnum.Ignore;
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        return l;
    }

    private static void StyleLineEdit(LineEdit edit)
    {
        edit.CustomMinimumSize = new Vector2(0, 34);
        var style = new StyleBoxFlat
        {
            BgColor     = new Color(0.96f, 0.91f, 0.72f),
            BorderColor = new Color(0.55f, 0.38f, 0.12f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(5);
        style.ContentMarginLeft  = 8f;
        style.ContentMarginRight = 8f;
        edit.AddThemeStyleboxOverride("normal", style);
        edit.AddThemeStyleboxOverride("focus",  style);
        edit.AddThemeColorOverride("font_color",             new Color(0.20f, 0.12f, 0.04f));
        edit.AddThemeColorOverride("font_placeholder_color", new Color(0.55f, 0.42f, 0.22f));
        edit.AddThemeFontSizeOverride("font_size", 15);
    }
}
