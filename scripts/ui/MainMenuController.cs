using System;
using Godot;

// Builds and drives the main menu entirely in code. Attached to scenes/MainMenu.tscn.
public partial class MainMenuController : Control
{
	private static readonly Color SkyTop    = new(0.04f, 0.07f, 0.18f);
	private static readonly Color SkyBottom = new(0.08f, 0.18f, 0.10f);
	private static readonly Color ShroompBlue = new(0.35f, 0.65f, 1.00f);
	private static readonly Color Gold      = new(0.95f, 0.80f, 0.28f);
	private static readonly Color Parchment = new(0.96f, 0.91f, 0.72f);

	private VBoxContainer  _buttons       = null!;
	private SettingsPanel  _settingsPanel = null!;
	private Sporeholm.UI.CreditsPanel _creditsPanel = null!;   // v0.5.74
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

		// v0.5.74 — Credits overlay. Renders MusicManager.GetCredits() at
		// open time so playlist edits show up here without touching this file.
		_creditsPanel = new Sporeholm.UI.CreditsPanel { Name = "CreditsPanel" };
		AddChild(_creditsPanel);

		// World generation overlay (New Game flow)
		_worldGen = new WorldGenPanel { Name = "WorldGenPanel" };
		_worldGen.BeginColonyRequested += OnWorldGenConfirmed;
		_worldGen.BackRequested        += _worldGen.Close;
		AddChild(_worldGen);

		// Scenario screen — shown between WorldGenPanel and Game.tscn.
		// Player customises shroomp roster, colony name, and storyteller here.
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

		// v0.5.75 — Small top-left music player widget (play/pause/skip
		// + Now Playing label). Added AFTER MusicManager.Play so the
		// widget's RefreshLabels picks up the current track on first
		// render rather than showing "(no track)".
		var musicPlayer = new Sporeholm.UI.MusicPlayerWidget { Name = "MusicPlayer" };
		AddChild(musicPlayer);
	}

	// ── Background ─────────────────────────────────────────────────────────────

	private void BuildBackground()
	{
		// v0.5.71+ — base sky colour as a flat fill behind the painted
		// village. MushroomVillage._Draw paints a dusk gradient over the
		// top, so this only shows for the brief frame before the village
		// finishes its initial Draw + as a fallback if Size is zero.
		var sky = new ColorRect { Color = SkyTop };
		sky.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(sky);

		// v0.5.72 — Sam: "Change the main menu background graphic to look
		// like a little 2D mushroom village in a mushroom grove fitting our
		// setting." Procedurally-drawn dusk scene with mushroom houses,
		// background mushroom trees, distant hills, faint stars, crescent
		// moon, and drifting firefly motes. Seeded so the layout is stable
		// across re-opens of the menu.
		var village = new MushroomVillage();
		village.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(village);
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
		AddBtn("Credits",   OnCredits);   // v0.5.74
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
			Text = "Sporeholm",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		ApplyGrobold(title, 72);
		title.AddThemeColorOverride("font_color", ShroompBlue);
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
			Text = "v0.5.84",
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

	private void OnCredits()  => _creditsPanel.Open();   // v0.5.74

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

// v0.5.72 — Procedural 2D mushroom-village background for the main menu.
// All shapes are drawn in _Draw via Godot's Control draw API — no asset
// files, matching the rest of the project's bake-from-code convention.
// Layout: dusk sky gradient + crescent moon + faint stars + 3 hill
// silhouette layers + giant background mushroom trees + a row of foreground
// mushroom houses (red Amanita caps with white spots, cream stems, lit
// windows, arched doors) + drifting firefly motes.
//
// The scene is seeded (constant rng seed) so the village layout is stable
// across menu reopens; only fireflies animate via _Process.
public partial class MushroomVillage : Control
{
	private struct StarSpec       { public Vector2 NormPos; public float Size; public float Alpha; }
	private struct FireflySpec    { public Vector2 NormPos; public float Phase; public float Speed; public float Amp; }
	private struct HouseSpec      { public float XFrac; public float Scale; public Color CapCol; public bool[] LitWindows; public int Spots; public float CapAspect; }
	private struct BgTreeSpec     { public float XFrac; public float YFrac; public float Scale; public Color CapCol; public int Spots; }
	private struct HillSpec       { public float YFrac; public Color Col; public float Amp; public float Freq; public float Phase; }

	private readonly StarSpec[]    _stars;
	private readonly FireflySpec[] _fireflies;
	private readonly HouseSpec[]   _houses;
	private readonly BgTreeSpec[]  _bgTrees;
	private readonly BgTreeSpec[]  _midTrees;
	private readonly HillSpec[]    _hills;
	private double                 _time;

	// Sky palette (top → horizon)
	private static readonly Color SkyZenith   = new(0.05f, 0.06f, 0.18f);   // deep indigo
	private static readonly Color SkyMid      = new(0.18f, 0.10f, 0.28f);   // dusk violet
	private static readonly Color SkyHorizon  = new(0.62f, 0.30f, 0.32f);   // sunset rose
	private static readonly Color SkyGlow     = new(0.85f, 0.55f, 0.32f);   // horizon glow band
	private static readonly Color MoonCol     = new(0.97f, 0.94f, 0.82f);
	private static readonly Color MoonGlow    = new(0.97f, 0.94f, 0.82f, 0.18f);
	private static readonly Color StarCol     = new(1.00f, 0.98f, 0.88f);
	private static readonly Color FireflyCol  = new(1.00f, 0.90f, 0.45f, 0.85f);
	private static readonly Color WindowGlow  = new(1.00f, 0.82f, 0.40f, 1.0f);
	private static readonly Color WindowDark  = new(0.18f, 0.13f, 0.08f, 1.0f);
	private static readonly Color DoorCol     = new(0.18f, 0.10f, 0.06f);

	public MushroomVillage()
	{
		MouseFilter = MouseFilterEnum.Ignore;   // pass clicks through to buttons above
		var rng = new RandomNumberGenerator { Seed = 0xF00DBABE };

		_stars = new StarSpec[60];
		for (int i = 0; i < _stars.Length; i++)
		{
			_stars[i] = new StarSpec
			{
				NormPos = new Vector2(rng.Randf(), rng.RandfRange(0f, 0.45f)),
				Size    = rng.RandfRange(0.6f, 1.8f),
				Alpha   = rng.RandfRange(0.25f, 0.85f),
			};
		}

		_fireflies = new FireflySpec[36];
		for (int i = 0; i < _fireflies.Length; i++)
		{
			_fireflies[i] = new FireflySpec
			{
				NormPos = new Vector2(rng.Randf(), rng.RandfRange(0.55f, 0.95f)),
				Phase   = rng.RandfRange(0f, Mathf.Tau),
				Speed   = rng.RandfRange(0.6f, 2.2f),
				Amp     = rng.RandfRange(0.4f, 1.0f),
			};
		}

		// Distant hills — 3 layers, back to front, lighter sky tint each step.
		_hills = new[]
		{
			new HillSpec { YFrac = 0.62f, Col = new Color(0.20f, 0.16f, 0.26f), Amp = 0.04f, Freq = 1.8f, Phase = 0.3f },
			new HillSpec { YFrac = 0.68f, Col = new Color(0.13f, 0.12f, 0.20f), Amp = 0.05f, Freq = 2.5f, Phase = 1.7f },
			new HillSpec { YFrac = 0.74f, Col = new Color(0.08f, 0.10f, 0.12f), Amp = 0.06f, Freq = 3.4f, Phase = 4.1f },
		};

		// Background mushroom trees — tall silhouettes scattered behind
		// the village, sized down toward the horizon for depth. Skip the
		// horizontal middle band (0.35-0.55) so they don't sit behind the
		// menu title.
		_bgTrees = new BgTreeSpec[7];
		int bgIdx = 0;
		while (bgIdx < _bgTrees.Length)
		{
			float xf = rng.Randf();
			if (xf > 0.32f && xf < 0.55f) continue;
			_bgTrees[bgIdx++] = new BgTreeSpec
			{
				XFrac = xf,
				YFrac = rng.RandfRange(0.55f, 0.66f),
				Scale = rng.RandfRange(0.55f, 0.90f),
				CapCol = PickBgCapColour(rng),
				Spots  = rng.RandiRange(0, 3),
			};
		}

		// Mid-ground trees — closer / larger, more detail.
		_midTrees = new BgTreeSpec[5];
		int midIdx = 0;
		while (midIdx < _midTrees.Length)
		{
			float xf = rng.Randf();
			if (xf > 0.40f && xf < 0.55f) continue;
			_midTrees[midIdx++] = new BgTreeSpec
			{
				XFrac = xf,
				YFrac = rng.RandfRange(0.66f, 0.74f),
				Scale = rng.RandfRange(0.85f, 1.20f),
				CapCol = PickBgCapColour(rng),
				Spots  = rng.RandiRange(2, 5),
			};
		}

		// Foreground houses — distributed across the bottom, two clusters
		// (left + right of centre) so the buttons stay readable.
		var houseSlots = new System.Collections.Generic.List<float>
		{
			0.05f, 0.13f, 0.21f, 0.29f,            // left cluster
			0.71f, 0.79f, 0.87f, 0.95f,            // right cluster
		};
		_houses = new HouseSpec[houseSlots.Count];
		for (int i = 0; i < houseSlots.Count; i++)
		{
			var litW = new bool[2];
			litW[0] = rng.Randf() < 0.85f;
			litW[1] = rng.Randf() < 0.65f;
			_houses[i] = new HouseSpec
			{
				XFrac      = houseSlots[i] + rng.RandfRange(-0.015f, 0.015f),
				Scale      = rng.RandfRange(0.85f, 1.20f),
				CapCol     = PickHouseCapColour(rng),
				LitWindows = litW,
				Spots      = rng.RandiRange(3, 6),
				CapAspect  = rng.RandfRange(0.55f, 0.72f),
			};
		}
	}

	private static Color PickBgCapColour(RandomNumberGenerator rng)
	{
		int pick = rng.RandiRange(0, 4);
		return pick switch
		{
			0 => new Color(0.65f, 0.22f, 0.18f),   // russet red
			1 => new Color(0.55f, 0.32f, 0.18f),   // brown bolete
			2 => new Color(0.50f, 0.32f, 0.55f),   // dusk purple
			3 => new Color(0.78f, 0.55f, 0.28f),   // ochre chanterelle
			_ => new Color(0.40f, 0.25f, 0.45f),   // muted violet
		};
	}

	private static Color PickHouseCapColour(RandomNumberGenerator rng)
	{
		int pick = rng.RandiRange(0, 3);
		return pick switch
		{
			0 => new Color(0.82f, 0.22f, 0.16f),   // bright Amanita red
			1 => new Color(0.72f, 0.30f, 0.20f),   // warm russet
			2 => new Color(0.55f, 0.30f, 0.55f),   // mystical purple
			_ => new Color(0.78f, 0.50f, 0.22f),   // ochre
		};
	}

	public override void _Process(double delta)
	{
		_time += delta;
		QueueRedraw();   // firefly motion + window flicker
	}

	public override void _Draw()
	{
		var size = Size;
		if (size.X < 1f || size.Y < 1f) size = new Vector2(1920f, 1080f);

		DrawSky(size);
		DrawMoon(size);
		DrawStars(size);
		DrawHorizonGlow(size);
		foreach (var h in _hills) DrawHill(size, h);
		foreach (var t in _bgTrees) DrawMushroomTree(size, t, alpha: 0.55f);
		foreach (var t in _midTrees) DrawMushroomTree(size, t, alpha: 0.85f);
		DrawGround(size);
		foreach (var h in _houses) DrawMushroomHouse(size, h);
		DrawFireflies(size);
	}

	// ── Sky ────────────────────────────────────────────────────────────────────
	private void DrawSky(Vector2 size)
	{
		// 24 horizontal bands of interpolated sky colour. Cheap + looks
		// like a real gradient because each band is ~45 px tall at 1080p.
		const int bands = 24;
		float bandH = size.Y / bands;
		for (int i = 0; i < bands; i++)
		{
			float t = i / (float)(bands - 1);
			Color c = t < 0.65f
				? LerpColor(SkyZenith, SkyMid, t / 0.65f)
				: LerpColor(SkyMid, SkyHorizon, (t - 0.65f) / 0.35f);
			DrawRect(new Rect2(0, i * bandH, size.X, bandH + 1f), c);
		}
	}

	private void DrawHorizonGlow(Vector2 size)
	{
		// Warm horizon halo behind the hills — a soft golden band.
		float y = size.Y * 0.66f;
		for (int i = 0; i < 16; i++)
		{
			float alpha = Mathf.Lerp(0.45f, 0f, i / 15f);
			var col = new Color(SkyGlow.R, SkyGlow.G, SkyGlow.B, alpha);
			DrawRect(new Rect2(0, y + i * 2f, size.X, 2f), col);
		}
	}

	// ── Moon ───────────────────────────────────────────────────────────────────
	private void DrawMoon(Vector2 size)
	{
		var moonPos = new Vector2(size.X * 0.82f, size.Y * 0.18f);
		float r = MathF.Min(size.X, size.Y) * 0.035f;
		// Soft outer halo
		DrawCircle(moonPos, r * 2.4f, new Color(MoonGlow.R, MoonGlow.G, MoonGlow.B, 0.08f));
		DrawCircle(moonPos, r * 1.6f, new Color(MoonGlow.R, MoonGlow.G, MoonGlow.B, 0.14f));
		// Crescent: bright moon then a sky-coloured circle offset to bite a wedge.
		DrawCircle(moonPos, r, MoonCol);
		DrawCircle(moonPos + new Vector2(r * 0.35f, -r * 0.05f), r * 0.92f, SkyZenith);
	}

	// ── Stars ──────────────────────────────────────────────────────────────────
	private void DrawStars(Vector2 size)
	{
		foreach (var s in _stars)
		{
			var p = new Vector2(s.NormPos.X * size.X, s.NormPos.Y * size.Y);
			DrawCircle(p, s.Size, new Color(StarCol.R, StarCol.G, StarCol.B, s.Alpha));
		}
	}

	// ── Hills ──────────────────────────────────────────────────────────────────
	private void DrawHill(Vector2 size, HillSpec h)
	{
		// Sample the hill skyline as a low-frequency sine and emit a filled
		// polygon from skyline down to the bottom of the screen.
		const int samples = 64;
		var pts = new Vector2[samples + 2];
		for (int i = 0; i < samples; i++)
		{
			float xf = i / (float)(samples - 1);
			float wobble = MathF.Sin(xf * h.Freq * Mathf.Tau + h.Phase) * h.Amp
				+ MathF.Sin(xf * h.Freq * 2.3f * Mathf.Tau + h.Phase * 0.7f) * (h.Amp * 0.4f);
			pts[i] = new Vector2(xf * size.X, (h.YFrac + wobble) * size.Y);
		}
		pts[samples]     = new Vector2(size.X, size.Y);
		pts[samples + 1] = new Vector2(0,      size.Y);
		DrawColoredPolygon(pts, h.Col);
	}

	// ── Mushroom trees (background / mid-ground silhouettes) ───────────────────
	private void DrawMushroomTree(Vector2 size, BgTreeSpec t, float alpha)
	{
		float baseX = t.XFrac * size.X;
		float baseY = t.YFrac * size.Y + size.Y * 0.18f;   // anchor at ground line
		float scale = t.Scale * MathF.Min(size.X / 1920f, size.Y / 1080f);

		float stemH = 110f * scale;
		float stemW = 22f  * scale;
		float capW  = 120f * scale;
		float capH  = 60f  * scale;

		// Stem (cream tube with side shadow)
		var stemCol  = new Color(0.86f, 0.80f, 0.66f, alpha);
		var stemDark = new Color(0.66f, 0.60f, 0.46f, alpha);
		DrawRect(new Rect2(baseX - stemW * 0.5f, baseY - stemH, stemW, stemH), stemCol);
		DrawRect(new Rect2(baseX - stemW * 0.5f, baseY - stemH, stemW * 0.25f, stemH), stemDark);

		// Cap dome — flattened half-ellipse via polygon
		DrawCapPolygon(baseX, baseY - stemH, capW, capH, MultiplyAlpha(t.CapCol, alpha));
		// Cap rim shadow (thin darker band at the bottom of the cap)
		DrawCapPolygon(baseX, baseY - stemH + capH * 0.15f, capW * 1.02f, capH * 0.18f,
			MultiplyAlpha(new Color(t.CapCol.R * 0.55f, t.CapCol.G * 0.55f, t.CapCol.B * 0.55f), alpha));

		// Spots on the cap
		var spotCol = new Color(0.95f, 0.92f, 0.82f, alpha);
		for (int i = 0; i < t.Spots; i++)
		{
			float fx = (i + 0.5f) / t.Spots - 0.5f;
			float spotX = baseX + fx * capW * 0.65f;
			float spotY = baseY - stemH - capH * 0.35f + (i % 2 == 0 ? -capH * 0.08f : capH * 0.06f);
			DrawCircle(new Vector2(spotX, spotY), capH * 0.10f, spotCol);
		}
	}

	// Filled cap dome (flattened half-ellipse) — anchor is the cap's BOTTOM-CENTRE.
	private void DrawCapPolygon(float ax, float ayBottom, float wTotal, float h, Color col)
	{
		const int samples = 22;
		var pts = new Vector2[samples + 2];
		for (int i = 0; i < samples; i++)
		{
			float tf = i / (float)(samples - 1);
			float theta = Mathf.Pi * tf;                       // 0 → π (left base, over top, right base)
			float dx = MathF.Cos(theta) * (wTotal * 0.5f);
			float dy = -MathF.Sin(theta) * h;
			pts[i] = new Vector2(ax + dx, ayBottom + dy);
		}
		pts[samples]     = new Vector2(ax + wTotal * 0.5f, ayBottom);
		pts[samples + 1] = new Vector2(ax - wTotal * 0.5f, ayBottom);
		DrawColoredPolygon(pts, col);
	}

	// ── Ground ─────────────────────────────────────────────────────────────────
	private void DrawGround(Vector2 size)
	{
		// Dark mossy band along the bottom, slightly above the very edge so
		// houses appear to sit on it.
		float yTop = size.Y * 0.82f;
		DrawRect(new Rect2(0, yTop, size.X, size.Y - yTop), new Color(0.06f, 0.10f, 0.08f));
		// Mid-tone grass ripple
		DrawRect(new Rect2(0, yTop, size.X, 6f),            new Color(0.16f, 0.24f, 0.16f));
		DrawRect(new Rect2(0, yTop + 6f, size.X, 4f),       new Color(0.10f, 0.16f, 0.10f));
	}

	// ── Mushroom houses (foreground) ───────────────────────────────────────────
	private void DrawMushroomHouse(Vector2 size, HouseSpec h)
	{
		float baseX = h.XFrac * size.X;
		float baseY = size.Y * 0.84f;   // sit slightly above the ground band
		float scale = h.Scale * MathF.Min(size.X / 1920f, size.Y / 1080f);

		float stemH = 130f * scale;
		float stemW = 90f  * scale;
		float capW  = 200f * scale;
		float capH  = capW * h.CapAspect * 0.5f;

		// Stem (cream barrel)
		var stemCol  = new Color(0.95f, 0.88f, 0.74f);
		var stemSide = new Color(0.78f, 0.72f, 0.58f);
		// Rounded stem: render as a rect with shaded sides via two narrow side bars.
		var stemRect = new Rect2(baseX - stemW * 0.5f, baseY - stemH, stemW, stemH);
		DrawRect(stemRect, stemCol);
		DrawRect(new Rect2(stemRect.Position.X, stemRect.Position.Y, stemW * 0.12f, stemH), stemSide);
		DrawRect(new Rect2(stemRect.Position.X + stemW * 0.88f, stemRect.Position.Y, stemW * 0.12f, stemH), stemSide);

		// Foundation shadow on the ground beneath the stem
		DrawRect(new Rect2(baseX - stemW * 0.6f, baseY - 2f, stemW * 1.2f, 8f),
			new Color(0f, 0f, 0f, 0.35f));

		// Door (arched — rect + half-circle on top)
		float doorW = stemW * 0.35f;
		float doorH = stemH * 0.45f;
		float doorBaseY = baseY;
		var doorRect = new Rect2(baseX - doorW * 0.5f, doorBaseY - doorH, doorW, doorH);
		DrawRect(doorRect, DoorCol);
		DrawCircle(new Vector2(baseX, doorBaseY - doorH), doorW * 0.5f, DoorCol);
		// Door knob
		DrawCircle(new Vector2(baseX + doorW * 0.30f, doorBaseY - doorH * 0.45f),
			MathF.Max(1.5f, 2.2f * scale), new Color(0.85f, 0.72f, 0.30f));

		// Windows — two square panes on either side of the door
		float winSize = stemW * 0.22f;
		float winY    = baseY - stemH * 0.72f;
		DrawWindow(baseX - stemW * 0.30f - winSize * 0.5f, winY, winSize, h.LitWindows[0]);
		DrawWindow(baseX + stemW * 0.30f - winSize * 0.5f, winY, winSize, h.LitWindows[1]);

		// Cap (large dome over the stem)
		DrawCapPolygon(baseX, baseY - stemH + capH * 0.12f, capW, capH, h.CapCol);
		// Cap rim shadow under the brim
		DrawCapPolygon(baseX, baseY - stemH + capH * 0.18f, capW * 1.03f, capH * 0.16f,
			new Color(h.CapCol.R * 0.45f, h.CapCol.G * 0.45f, h.CapCol.B * 0.45f));
		// Pore-pad ring (small dots under the cap) — implies "this is the
		// underside of a mushroom cap" like the in-game Shroomp sprite.
		var poreCol = new Color(0.78f, 0.70f, 0.32f);
		for (int i = -2; i <= 2; i++)
			DrawCircle(new Vector2(baseX + i * capW * 0.18f, baseY - stemH + capH * 0.20f),
				MathF.Max(1.2f, 2.0f * scale), poreCol);

		// Cap spots (white Amanita dots)
		var spotCol = new Color(0.96f, 0.93f, 0.85f);
		var spotRng = new RandomNumberGenerator { Seed = (ulong)(h.Spots * 7919 + (int)(h.XFrac * 1000)) };
		for (int i = 0; i < h.Spots; i++)
		{
			float angle = spotRng.RandfRange(0.18f, Mathf.Pi - 0.18f);
			float r     = spotRng.RandfRange(0.30f, 0.85f);
			float spotX = baseX + MathF.Cos(angle) * capW * 0.5f * r;
			float spotY = (baseY - stemH + capH * 0.12f) - MathF.Sin(angle) * capH * 0.95f * r;
			float spotR = spotRng.RandfRange(0.06f, 0.12f) * capH;
			DrawCircle(new Vector2(spotX, spotY), spotR, spotCol);
		}
	}

	private void DrawWindow(float x, float y, float s, bool lit)
	{
		var col = lit ? WindowGlow : WindowDark;
		// Frame
		DrawRect(new Rect2(x - 1, y - 1, s + 2, s + 2), new Color(0.18f, 0.12f, 0.06f));
		// Pane
		DrawRect(new Rect2(x, y, s, s), col);
		// Mullions (cross)
		DrawRect(new Rect2(x + s * 0.5f - 0.8f, y, 1.6f, s), new Color(0.18f, 0.12f, 0.06f));
		DrawRect(new Rect2(x, y + s * 0.5f - 0.8f, s, 1.6f), new Color(0.18f, 0.12f, 0.06f));
		if (lit)
		{
			// Warm glow halo around lit windows
			DrawCircle(new Vector2(x + s * 0.5f, y + s * 0.5f), s * 1.8f,
				new Color(1f, 0.82f, 0.40f, 0.08f));
		}
	}

	// ── Fireflies ──────────────────────────────────────────────────────────────
	private void DrawFireflies(Vector2 size)
	{
		foreach (var f in _fireflies)
		{
			float pulse = 0.5f + 0.5f * MathF.Sin((float)_time * f.Speed + f.Phase);
			float drift = MathF.Sin((float)_time * 0.3f * f.Speed + f.Phase) * f.Amp * 0.01f;
			var p = new Vector2((f.NormPos.X + drift) * size.X, f.NormPos.Y * size.Y);
			DrawCircle(p, 1.6f, new Color(FireflyCol.R, FireflyCol.G, FireflyCol.B, FireflyCol.A * pulse));
			DrawCircle(p, 4.5f, new Color(FireflyCol.R, FireflyCol.G, FireflyCol.B, 0.10f * pulse));
		}
	}

	private static Color LerpColor(Color a, Color b, float t) =>
		new(a.R + (b.R - a.R) * t,
			a.G + (b.G - a.G) * t,
			a.B + (b.B - a.B) * t,
			a.A + (b.A - a.A) * t);

	private static Color MultiplyAlpha(Color c, float a) =>
		new(c.R, c.G, c.B, c.A * a);
}
