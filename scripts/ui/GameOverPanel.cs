using Godot;

// Full-screen game-over overlay. Call Show(date) when the colony is wiped out.
public partial class GameOverPanel : Control
{
    private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Parchment = new(0.96f, 0.91f, 0.72f);
    private static readonly Color Crimson   = new(0.80f, 0.15f, 0.15f);

    private Label _dateLabel = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible  = false;
        Modulate = Colors.Transparent;

        // Dark backdrop
        var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        vbox.AddThemeConstantOverride("separation", 18);
        vbox.CustomMinimumSize = new Vector2(420, 0);
        center.AddChild(vbox);

        // Skull icon stand-in
        var skull = MakeLbl("✦", 56, Gold);
        skull.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(skull);

        // Title
        var title = MakeLbl("The Colony Has Fallen", 42, Crimson);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(title);

        // Date subtitle
        _dateLabel = MakeLbl("", 18, new Color(0.65f, 0.55f, 0.42f));
        _dateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(_dateLabel);

        // Divider
        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.18f, 0.5f));
        vbox.AddChild(sep);

        // Flavour line
        var flavour = MakeLbl("Every shroomp has perished.\nThe mushroom village stands empty.", 16, Parchment);
        flavour.HorizontalAlignment = HorizontalAlignment.Center;
        flavour.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(flavour);

        // Return button
        var btn = new AnimatedButton
        {
            Text = "Return to Main Menu",
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(340, 0),
        };
        btn.Pressed += OnReturnPressed;
        vbox.AddChild(btn);
    }

    public void Show(string date)
    {
        _dateLabel.Text = $"Last day: {date}";
        Visible  = true;
        Modulate = Colors.Transparent;
        var t = CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        t.TweenProperty(this, "modulate", Colors.White, 0.6f);
    }

    private void OnReturnPressed()
    {
        // Delete the save so Continue won't load a dead colony.
        SaveManager.Instance?.DeleteSave();
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }

    private static Label MakeLbl(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", size);
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        return l;
    }
}
