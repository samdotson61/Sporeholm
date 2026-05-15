using Godot;

// Builds and drives the main menu entirely in code. Attached to scenes/MainMenu.tscn.
public partial class MainMenuController : Control
{
	private static readonly Color SkyTop    = new(0.04f, 0.07f, 0.18f);
	private static readonly Color SkyBottom = new(0.08f, 0.18f, 0.10f);
	private static readonly Color SmurfBlue = new(0.35f, 0.65f, 1.00f);
	private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
	private static readonly Color Parchment = new(0.96f, 0.91f, 0.72f);

	private VBoxContainer  _buttons       = null!;
	private SettingsPanel  _settingsPanel = null!;
	private WorldGenPanel  _worldGen      = null!;
	private ScenarioPanel  _scenario      = null!;
	private SaveFileBrowser _loadBrowser  = null!;

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		GrowHorizontal = GrowDirection.Both;
		GrowVertical   = GrowDirection.Both;

		BuildBackground();
		BuildLayout();

		// Settings overlay
		_settingsPanel = new SettingsPanel { Name = "SettingsPanel" };
		_settingsPanel.SettingsClosed += ApplyRuntimeSettings;
		AddChild(_settingsPanel);

		// World generation overlay (New Game flow)
		_worldGen = new WorldGenPanel { Name = "WorldGenPanel" };
		_worldGen.BeginColonyRequested += OnWorldGenConfirmed;
		_worldGen.BackRequested        += _worldGen.Close;
		AddChild(_worldGen);

		// Scenario screen — shown between WorldGenPanel and Game.tscn.
		// Player customises smurf roster, colony name, and storyteller here.
		_scenario = new ScenarioPanel { Name = "ScenarioPanel" };
		_scenario.BeginColonyConfirmed += OnBeginColony;
		_scenario.BackRequested        += OnScenarioBack;
		AddChild(_scenario);

		// Save browser in load mode (Continue flow)
		_loadBrowser = new SaveFileBrowser { Name = "LoadBrowser" };
		_loadBrowser.LoadConfirmed += OnLoadConfirmed;
		_loadBrowser.Cancelled     += () => _loadBrowser.Close();
		AddChild(_loadBrowser);

		MusicManager.Instance?.Play(MusicManager.Context.Menu);
	}

	// ── Background ─────────────────────────────────────────────────────────────

	private void BuildBackground()
	{
		var sky = new ColorRect { Color = SkyTop };
		sky.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(sky);

		var forest = new ColorRect { Color = new Color(0.03f, 0.12f, 0.04f, 0.50f) };
		forest.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
		forest.OffsetTop = -180;
		AddChild(forest);

		var stars = new StarField();
		stars.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(stars);
	}

	// ── Central layout ─────────────────────────────────────────────────────────

	private void BuildLayout()
	{
		var center = new CenterContainer();
		center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(center);

		var vbox = new VBoxContainer();
		vbox.AddThemeConstantOverride("separation", 8);
		center.AddChild(vbox);

		AddTitle(vbox);
		AddSpacer(vbox, 20);
		AddSubtitle(vbox, "A Colony & Evolutionary Simulation");
		AddSpacer(vbox, 36);

		_buttons = new VBoxContainer();
		_buttons.AddThemeConstantOverride("separation", 12);
		_buttons.CustomMinimumSize = new Vector2(340, 0);
		vbox.AddChild(_buttons);

		bool hasSave = SaveManager.Instance?.HasSave == true;

		AddBtn("New Game",  OnNewGame);
		AddBtn("Load Game", hasSave ? OnContinue : null);
		AddBtn("Settings",  OnSettings);
		AddBtn("Quit",      OnQuit);

		AddSpacer(vbox, 18);
		AddVersion(vbox);
	}

	// ── Title ──────────────────────────────────────────────────────────────────

	private void AddTitle(VBoxContainer parent)
	{
		var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		row.AddThemeConstantOverride("separation", 16);
		parent.AddChild(row);

		row.AddChild(StarLabel(40));

		var title = new Label
		{
			Text = "SmurfulationC",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		ApplyGrobold(title, 72);
		title.AddThemeColorOverride("font_color", SmurfBlue);
		title.AddThemeColorOverride("font_shadow_color", new Color(0f, 0.1f, 0.5f, 0.6f));
		title.AddThemeConstantOverride("shadow_offset_x", 3);
		title.AddThemeConstantOverride("shadow_offset_y", 3);
		row.AddChild(title);

		row.AddChild(StarLabel(40));
	}

	private Label StarLabel(int size)
	{
		var l = new Label { Text = "★" };
		ApplyGrobold(l, size);
		l.AddThemeColorOverride("font_color", Gold);
		l.VerticalAlignment = VerticalAlignment.Center;
		return l;
	}

	private void AddSubtitle(VBoxContainer parent, string text)
	{
		var sub = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
		ApplyGrobold(sub, 22);
		sub.AddThemeColorOverride("font_color", Parchment);
		parent.AddChild(sub);
	}

	private void AddVersion(VBoxContainer parent)
	{
		var v = new Label
		{
			Text = "v0.5.13",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		ApplyGrobold(v, 14);
		v.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
		parent.AddChild(v);
	}

	// ── Buttons ────────────────────────────────────────────────────────────────

	private void AddBtn(string label, System.Action? callback)
	{
		var btn = new AnimatedButton
		{
			Text = label,
			Disabled = callback == null,
			SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
			CustomMinimumSize = new Vector2(340, 0),
		};
		if (callback != null)
			btn.Pressed += callback.Invoke;
		_buttons.AddChild(btn);
	}

	private static void AddSpacer(VBoxContainer parent, int height)
	{
		parent.AddChild(new Control { CustomMinimumSize = new Vector2(0, height) });
	}

	// ── Navigation ─────────────────────────────────────────────────────────────

	private void OnNewGame()
	{
		// Clear current-slot memory without deleting other saves on disk.
		SaveManager.Instance?.ClearCurrentSlot();
		WorldState.Instance?.Clear();
		_worldGen.Open();
	}

	// New flow: WorldGenPanel emits BeginColonyRequested AFTER tile selection.
	// We close the world panel and open the scenario screen — the actual scene
	// change happens only after the player confirms in the scenario screen.
	private void OnWorldGenConfirmed()
	{
		_worldGen.Close();
		_scenario.Open();
	}

	// Scenario "Back" — re-open the world panel at the tile-select stage.
	private void OnScenarioBack()
	{
		_scenario.Close();
		_worldGen.Open();
	}

	// Called when ScenarioPanel emits BeginColonyConfirmed. By this point
	// WorldState.PendingScenario and WorldState.ColonyName have both been
	// written by ScenarioPanel — SimulationManager.SeedColony will read them
	// from the new Game scene.
	private void OnBeginColony()
	{
		_scenario.Close();
		GetTree().ChangeSceneToFile("res://scenes/Game.tscn");
	}

	// "Continue" opens the load browser so the player picks which slot to load.
	private void OnContinue() => _loadBrowser.OpenForLoad();

	private void OnLoadConfirmed(string slotName)
	{
		if (SaveManager.Instance?.LoadSlot(slotName) != true) return;

		var save = SaveManager.Instance.CurrentSave;
		if (save != null && save.WorldSeed != 0)
			WorldState.Instance?.LoadFromSave(save);
		else
			WorldState.Instance?.EnsureDefaultMap();

		_loadBrowser.Close();
		GetTree().ChangeSceneToFile("res://scenes/Game.tscn");
	}

	private void OnSettings() => _settingsPanel.Open();

	private void OnQuit() => GetTree().Quit();

	private void ApplyRuntimeSettings()
	{
		var cfg = new ConfigFile();
		if (cfg.Load("user://settings.cfg") != Error.Ok) return;
		string ttSize = (string)cfg.GetValue("gameplay", "tooltip_size", "large");
		GetTree().Root.Theme = GameController.BuildTooltipTheme(SettingsPanel.TooltipFontSize(ttSize));
	}

	// ── Font helper ────────────────────────────────────────────────────────────

	private static void ApplyGrobold(Control node, int size)
	{
		const string path = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(path))
			node.AddThemeFontOverride("font", GD.Load<FontFile>(path));
		node.AddThemeFontSizeOverride("font_size", size);
	}
}

// Lightweight Control that draws random stars in its _Draw pass.
public partial class StarField : Control
{
	private readonly (Vector2 pos, float size, float alpha)[] _stars;

	public StarField()
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();
		_stars = new (Vector2, float, float)[120];
		for (int i = 0; i < _stars.Length; i++)
			_stars[i] = (new Vector2(rng.RandfRange(0, 1920), rng.RandfRange(0, 600)),
						 rng.RandfRange(1.0f, 2.8f),
						 rng.RandfRange(0.3f, 0.9f));
	}

	public override void _Draw()
	{
		foreach (var (pos, size, alpha) in _stars)
			DrawCircle(pos, size, new Color(1f, 1f, 0.92f, alpha));
	}
}
