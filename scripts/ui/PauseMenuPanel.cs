using Godot;

// Full-screen pause overlay. Opened by HUD "Menu" button; closed by Resume or Escape.
public partial class PauseMenuPanel : Control
{
	[Signal] public delegate void ResumeRequestedEventHandler();
	[Signal] public delegate void SaveRequestedEventHandler();
	[Signal] public delegate void LoadRequestedEventHandler();
	[Signal] public delegate void SettingsRequestedEventHandler();
	[Signal] public delegate void ExitRequestedEventHandler();

	private static readonly Color ParchBg  = new(0.84f, 0.76f, 0.56f, 0.97f);
	private static readonly Color DarkWood = new(0.20f, 0.12f, 0.04f);
	private static readonly Color Gold     = new(0.82f, 0.63f, 0.18f);

	private Label _statusLabel = null!;

	public override void _Ready()
	{
		var cfg = new ConfigFile();
		bool tips = cfg.Load("user://settings.cfg") != Error.Ok
			|| (bool)cfg.GetValue("gameplay", "show_tooltips", true);

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		// Dark translucent backdrop
		var overlay = new ColorRect { Color = new Color(0.05f, 0.03f, 0.01f, 0.72f) };
		overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(overlay);

		var center = new CenterContainer();
		center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(center);

		// Parchment card
		var card = new PanelContainer();
		card.CustomMinimumSize = new Vector2(360, 0);
		var style = new StyleBoxFlat { BgColor = ParchBg };
		style.SetBorderWidthAll(4);
		style.BorderColor  = Gold;
		style.SetCornerRadiusAll(10);
		style.ShadowColor  = new Color(0f, 0f, 0f, 0.55f);
		style.ShadowSize   = 14;
		style.ShadowOffset = new Vector2(0, 5);
		card.AddThemeStyleboxOverride("panel", style);
		center.AddChild(card);

		var margin = new MarginContainer();
		foreach (var side in new[] { "left", "right", "top", "bottom" })
			margin.AddThemeConstantOverride($"margin_{side}", 36);
		card.AddChild(margin);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("separation", 16);
		margin.AddChild(vbox);

		// ── Title ─────────────────────────────────────────────────────────────
		var title = new Label
		{
			Text                = "—  Paused  —",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		title.AddThemeColorOverride("font_color", DarkWood);
		title.AddThemeFontSizeOverride("font_size", 32);
		ApplyGrobold(title);
		vbox.AddChild(title);

		var sep = new HSeparator();
		sep.AddThemeColorOverride("color", new Color(0.55f, 0.38f, 0.12f, 0.6f));
		vbox.AddChild(sep);

		// ── Buttons ───────────────────────────────────────────────────────────
		var resume = new AnimatedButton { Text = "Resume" };
		resume.Pressed += () => EmitSignal(SignalName.ResumeRequested);
		if (tips) resume.TooltipText = "Continue the simulation";
		vbox.AddChild(resume);

		var save = new AnimatedButton { Text = "Save Game" };
		save.Pressed += () => EmitSignal(SignalName.SaveRequested);
		if (tips) save.TooltipText = "Open the save browser to name or overwrite a save slot";
		vbox.AddChild(save);

		var load = new AnimatedButton { Text = "Load Save" };
		load.Pressed += () => EmitSignal(SignalName.LoadRequested);
		if (tips) load.TooltipText = "Open the save browser to pick a save to load";
		vbox.AddChild(load);

		var settings = new AnimatedButton { Text = "Settings" };
		settings.Pressed += () => EmitSignal(SignalName.SettingsRequested);
		if (tips) settings.TooltipText = "Adjust audio, display, and gameplay options";
		vbox.AddChild(settings);

		var exit = new AnimatedButton { Text = "Exit to Main Menu" };
		exit.Pressed += () => EmitSignal(SignalName.ExitRequested);
		if (tips) exit.TooltipText = "Auto-save an exit slot and return to the main menu";
		vbox.AddChild(exit);

		// ── Status label ("Game saved!") ──────────────────────────────────────
		_statusLabel = new Label
		{
			Text                = "Game saved!",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		_statusLabel.AddThemeColorOverride("font_color", new Color(0.25f, 0.55f, 0.20f));
		_statusLabel.AddThemeFontSizeOverride("font_size", 15);
		_statusLabel.Modulate = Colors.Transparent;
		vbox.AddChild(_statusLabel);

		Visible = false;
	}

	public void Open()
	{
		_statusLabel.Modulate = Colors.Transparent;
		Visible = true;
	}

	public void Close() => Visible = false;

	public void ShowSaved() => ShowStatus("Game saved!", new Color(0.25f, 0.55f, 0.20f));

	public void ShowStatus(string message, Color color)
	{
		_statusLabel.Text     = message;
		_statusLabel.Modulate = Colors.White;
		_statusLabel.AddThemeColorOverride("font_color", color);
		var t = CreateTween().SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Sine);
		t.TweenInterval(1.4f);
		t.TweenProperty(_statusLabel, "modulate", Colors.Transparent, 0.5f);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (e is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
		{
			GetViewport().SetInputAsHandled();
			EmitSignal(SignalName.ResumeRequested);
		}
	}

	private static void ApplyGrobold(Label l)
	{
		const string font = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(font))
			l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
	}
}
