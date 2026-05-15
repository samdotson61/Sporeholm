using Godot;

// Full-screen overlay shown when the user clicks Settings.
// Persists audio volumes (0-100 scale), resolution, and fullscreen to user://settings.cfg.
public partial class SettingsPanel : Control
{
	[Signal] public delegate void SettingsClosedEventHandler();

	private const string CfgPath = "user://settings.cfg";

	private static readonly Color ParchBg  = new(0.84f, 0.76f, 0.56f, 0.97f);
	private static readonly Color DarkWood = new(0.20f, 0.12f, 0.04f);
	private static readonly Color Gold     = new(0.82f, 0.63f, 0.18f);
	private static readonly Color Brown    = new(0.45f, 0.28f, 0.08f);

	private static readonly (int W, int H)[] Resolutions =
	{
		(1280,  720),
		(1600,  900),
		(1920, 1080),
		(2560, 1440),
		(3840, 2160),
	};

	private HSlider        _masterSlider = null!, _musicSlider = null!, _sfxSlider = null!;
	private Label          _masterVal    = null!, _musicVal    = null!, _sfxVal    = null!;
	private HSlider        _zoomSlider   = null!;
	private Label          _zoomVal      = null!;
	private OptionButton   _resDrop        = null!;
	private AnimatedButton _fullscreenBtn   = null!;
	private AnimatedButton _windowedBtn    = null!;
	private AnimatedButton _pauseOnStartBtn  = null!;
	private AnimatedButton _showTooltipsBtn  = null!;
	private AnimatedButton _devModeBtn       = null!;
	private bool           _devMode          = false;
	private AnimatedButton _boxSelCtrlBtn    = null!;
	private bool           _boxSelRequiresCtrl = true;

	// v0.4.36 — live-readable input gate. GameController consults this
	// flag in HandleMouseClick to decide whether a no-tool left-drag
	// starts a multi-select box. Defaults to ON so accidental drags
	// don't unintentionally rope a dozen smurfs into a move order —
	// players who want the v0.3.24 behaviour can flip it off here.
	public static bool BoxSelectRequiresCtrl { get; private set; } = true;
	private OptionButton   _tooltipSizeDrop  = null!;
	private OptionButton   _uiScaleDrop      = null!;
	private bool           _isFullscreen;
	private bool           _updatingMode;
	private bool           _pauseOnStart  = true;
	private bool           _showTooltips  = true;
	private string         _tooltipSize   = "large";
	private int            _zoomSpeed     = 5;
	// 0 = 1.00× (default), 1 = 0.66×, 2 = 0.33×. Index matches dropdown order.
	private int            _uiScaleIdx    = 0;

	private static readonly string[] TooltipSizeKeys = { "large", "normal", "small" };
	private static readonly float[]  UIScaleValues   = { 1.00f, 0.66f, 0.33f };
	// v0.3.30 — display names changed from raw percentages to plain English.
	// The float values still correspond to 100 % / 66 % / 33 %; this is only
	// the dropdown text the player sees.
	private static readonly string[] UIScaleLabels   = { "Large", "Normal", "Small" };

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		var overlay = new ColorRect { Color = new Color(0f, 0f, 0f, 0.60f) };
		overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(overlay);

		// Card anchored to viewport so its height is always bounded by the screen.
		// CenterContainer is intentionally not used here — it would let the card
		// grow past the bottom edge on small screens.
		var card = new PanelContainer();
		card.AnchorLeft   = 0.5f; card.AnchorRight  = 0.5f;
		card.AnchorTop    = 0f;   card.AnchorBottom  = 1f;
		card.OffsetLeft   = -285f; card.OffsetRight  = 285f;
		card.OffsetTop    = 14f;   card.OffsetBottom = -14f;
		card.GrowHorizontal = GrowDirection.Both;
		var style = new StyleBoxFlat { BgColor = ParchBg };
		style.SetBorderWidthAll(4);
		style.BorderColor  = Gold;
		style.SetCornerRadiusAll(10);
		style.ShadowColor  = new Color(0f, 0f, 0f, 0.5f);
		style.ShadowSize   = 12;
		style.ShadowOffset = new Vector2(0, 4);
		card.AddThemeStyleboxOverride("panel", style);
		AddChild(card);

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left",   20);
		margin.AddThemeConstantOverride("margin_right",  28);
		margin.AddThemeConstantOverride("margin_top",    20);
		margin.AddThemeConstantOverride("margin_bottom", 20);
		card.AddChild(margin);

		// outerVBox: title (pinned) ── scroll area ── buttons (pinned)
		var outerVBox = new VBoxContainer();
		outerVBox.AddThemeConstantOverride("separation", 10);
		margin.AddChild(outerVBox);

		outerVBox.AddChild(BigLabel("Settings"));
		outerVBox.AddChild(MakeSep());

		// Scrollable content — fills all space between title and buttons
		var scroll = new ScrollContainer();
		scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
		scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
		outerVBox.AddChild(scroll);

		var vbox = new VBoxContainer();
		vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		vbox.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(vbox);

		// ── Audio ─────────────────────────────────────────────────────────────
		vbox.AddChild(SectionLabel("Audio"));

		(_masterSlider, _masterVal) = AddVolumeRow(vbox, "Master",        "Master", 100);
		(_musicSlider,  _musicVal)  = AddVolumeRow(vbox, "Music",         "Music",   50);
		(_sfxSlider,    _sfxVal)    = AddVolumeRow(vbox, "Sound Effects", "SFX",    100);

		vbox.AddChild(MakeSep());

		// ── Gameplay ──────────────────────────────────────────────────────────
		vbox.AddChild(SectionLabel("Gameplay"));
		AddPauseOnStartRow(vbox);
		AddShowTooltipsRow(vbox);
		AddTooltipSizeRow(vbox);
		AddUIScaleRow(vbox);
		AddZoomSpeedRow(vbox);
		AddDeveloperModeRow(vbox);
		AddBoxSelectCtrlRow(vbox);

		vbox.AddChild(MakeSep());

		// ── Display ───────────────────────────────────────────────────────────
		vbox.AddChild(SectionLabel("Display"));
		_isFullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
		AddResolutionRow(vbox);
		AddWindowedRow(vbox);
		AddFullscreenRow(vbox);

		vbox.AddChild(MakeSep());

		// ── Keybindings (v0.4.3) ──────────────────────────────────────────────
		vbox.AddChild(SectionLabel("Keybindings"));
		BuildKeybindingsSection(vbox);

		// ── Buttons (pinned below scroll) ─────────────────────────────────────
		outerVBox.AddChild(MakeSep());
		var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		btnRow.AddThemeConstantOverride("separation", 16);
		outerVBox.AddChild(btnRow);

		var reset = new AnimatedButton { Text = "Reset Defaults" };
		reset.Pressed += OnReset;
		btnRow.AddChild(reset);

		var back = new AnimatedButton { Text = "Back" };
		back.Pressed += OnBack;
		btnRow.AddChild(back);

		LoadSettings();
		Visible = false;
	}

	public void Open()  => Visible = true;
	public void Close() { SaveSettings(); Visible = false; EmitSignal(SignalName.SettingsClosed); }

	// ── Volume rows ────────────────────────────────────────────────────────────

	private (HSlider, Label) AddVolumeRow(
		VBoxContainer parent, string label, string busName, int defaultVal)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		row.AddChild(RowLabel(label));

		// Wrapper: ProgressBar (visual bar) + HSlider (grabber handle) layered together,
		// matching the same approach used by NeedBar.
		var wrap = new Control();
		wrap.SizeFlagsHorizontal = SizeFlags.Fill | SizeFlags.Expand;
		wrap.CustomMinimumSize   = new Vector2(180, 28);
		row.AddChild(wrap);

		// ProgressBar — full-rect background bar, non-interactive
		var bar = new ProgressBar
		{
			MinValue       = 0,
			MaxValue       = 100,
			Value          = defaultVal,
			ShowPercentage = false,
			MouseFilter    = MouseFilterEnum.Ignore,
		};
		bar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		var fill = new StyleBoxFlat { BgColor = Gold };
		fill.SetCornerRadiusAll(4);
		bar.AddThemeStyleboxOverride("fill", fill);

		var track = new StyleBoxFlat
		{
			BgColor = new Color(Gold.R * 0.35f, Gold.G * 0.35f, Gold.B * 0.35f, 0.45f),
		};
		track.SetCornerRadiusAll(4);
		bar.AddThemeStyleboxOverride("background", track);
		wrap.AddChild(bar);

		// HSlider — overlaid on top; track and fill area made transparent so only
		// the grabber handle is visible. Receives all mouse input.
		var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = defaultVal };
		slider.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		var invis = new StyleBoxFlat { BgColor = Colors.Transparent };
		slider.AddThemeStyleboxOverride("slider",                  invis);
		slider.AddThemeStyleboxOverride("grabber_area",            invis);
		slider.AddThemeStyleboxOverride("grabber_area_highlight",  invis);
		wrap.AddChild(slider);

		// Value label to the right of the wrapper
		var valLbl = new Label
		{
			Text                = defaultVal.ToString(),
			CustomMinimumSize   = new Vector2(40, 0),
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		valLbl.AddThemeColorOverride("font_color", DarkWood);
		valLbl.AddThemeFontSizeOverride("font_size", 15);

		slider.ValueChanged += (v) =>
		{
			int iv  = (int)v;
			bar.Value   = iv;
			valLbl.Text = iv.ToString();
			ApplyBusVolume(busName, iv);
		};

		row.AddChild(valLbl);
		return (slider, valLbl);
	}

	private static void ApplyBusVolume(string busName, int sliderValue)
	{
		int idx = AudioServer.GetBusIndex(busName);
		if (sliderValue <= 0)
		{
			AudioServer.SetBusMute(idx, true);
			return;
		}
		AudioServer.SetBusMute(idx, false);
		// Logarithmic: slider 100 → 0 dB, slider 50 → -6 dB, slider 1 → -40 dB
		AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(sliderValue / 100f));
	}

	private void SetVolume(HSlider slider, Label valLbl, string busName, int value)
	{
		slider.Value = value;
		valLbl.Text  = value.ToString();
		ApplyBusVolume(busName, value);
	}

	// ── Resolution row ────────────────────────────────────────────────────────

	private void AddResolutionRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		row.AddChild(RowLabel("Resolution"));

		_resDrop = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
		StyleOptionButton(_resDrop);

		var currentSize = DisplayServer.WindowGetSize();
		int selected = 0;
		for (int i = 0; i < Resolutions.Length; i++)
		{
			var (w, h) = Resolutions[i];
			_resDrop.AddItem($"{w} × {h}");
			if (w == currentSize.X && h == currentSize.Y)
				selected = i;
		}
		_resDrop.Selected = selected;
		_resDrop.ItemSelected += OnResolutionPicked;
		_resDrop.Disabled = _isFullscreen;
		row.AddChild(_resDrop);
	}

	private static void StyleOptionButton(OptionButton btn)
	{
		var bg = new StyleBoxFlat { BgColor = new Color(0.28f, 0.17f, 0.07f) };
		bg.SetBorderWidthAll(2);
		bg.BorderColor = Gold;
		bg.SetCornerRadiusAll(6);
		bg.ContentMarginLeft = bg.ContentMarginRight = 12;
		bg.ContentMarginTop = bg.ContentMarginBottom = 6;
		btn.AddThemeStyleboxOverride("normal", bg);
		btn.AddThemeColorOverride("font_color", new Color(0.96f, 0.89f, 0.68f));
		btn.AddThemeFontSizeOverride("font_size", 15);
	}

	private void OnResolutionPicked(long idx)
	{
		var (w, h) = Resolutions[(int)idx];
		ApplyResolution(w, h);
	}

	private void ApplyResolution(int w, int h)
	{
		// ContentScaleSize stays fixed at the project's 1280×720 base.
		// CanvasItems mode scales every draw call by (window_size / 1280×720), so
		// the UI fills the screen proportionally at any resolution. Changing
		// ContentScaleSize would expand the logical viewport without scaling the
		// fixed-pixel UI elements, causing them to bunch up in a corner.
		// The resolution dropdown therefore only controls the OS window size.
		var root = GetTree().Root;
		root.ContentScaleSize = new Vector2I(1280, 720);
		root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;

		if (!_isFullscreen)
		{
			DisplayServer.WindowSetSize(new Vector2I(w, h));
			var screen = DisplayServer.ScreenGetSize();
			DisplayServer.WindowSetPosition((screen - new Vector2I(w, h)) / 2);
		}
	}

	// ── Fullscreen row ────────────────────────────────────────────────────────

	private void AddWindowedRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);
		row.AddChild(RowLabel("Windowed"));
		_windowedBtn = new AnimatedButton
		{
			Text              = !_isFullscreen ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = !_isFullscreen,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_windowedBtn.Toggled += OnWindowedToggled;
		row.AddChild(_windowedBtn);
	}

	private void AddFullscreenRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);
		row.AddChild(RowLabel("Fullscreen"));
		_fullscreenBtn = new AnimatedButton
		{
			Text              = _isFullscreen ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = _isFullscreen,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_fullscreenBtn.Toggled += OnFullscreenToggled;
		row.AddChild(_fullscreenBtn);
	}

	// Atomically swaps both toggle buttons and applies the window mode.
	// All programmatic ButtonPressed changes go through _updatingMode so the
	// Toggled signal handlers don't re-enter and cause infinite loops.
	private void SetDisplayMode(bool fullscreen)
	{
		_isFullscreen = fullscreen;
		_updatingMode = true;
		_fullscreenBtn.Text          = fullscreen  ? "ON" : "OFF";
		_fullscreenBtn.ButtonPressed = fullscreen;
		_windowedBtn.Text            = !fullscreen ? "ON" : "OFF";
		_windowedBtn.ButtonPressed   = !fullscreen;
		_resDrop.Disabled            = fullscreen;
		_updatingMode = false;
		DisplayServer.WindowSetMode(fullscreen
			? DisplayServer.WindowMode.Fullscreen
			: DisplayServer.WindowMode.Windowed);
		var (w, h) = Resolutions[_resDrop.Selected];
		ApplyResolution(w, h);
	}

	private void OnWindowedToggled(bool on)
	{
		if (_updatingMode) return;
		// Prevent deselecting the active button — at least one must always be chosen.
		if (!on) { _updatingMode = true; _windowedBtn.ButtonPressed = true; _updatingMode = false; return; }
		SetDisplayMode(false);
	}

	private void OnFullscreenToggled(bool on)
	{
		if (_updatingMode) return;
		if (!on) { _updatingMode = true; _fullscreenBtn.ButtonPressed = true; _updatingMode = false; return; }
		SetDisplayMode(true);
	}

	// ── Pause on Start row ────────────────────────────────────────────────────

	private void AddPauseOnStartRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		row.AddChild(RowLabel("Pause on Start"));

		_pauseOnStartBtn = new AnimatedButton
		{
			Text              = _pauseOnStart ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = _pauseOnStart,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_pauseOnStartBtn.Toggled += on =>
		{
			_pauseOnStart         = on;
			_pauseOnStartBtn.Text = on ? "ON" : "OFF";
		};
		row.AddChild(_pauseOnStartBtn);
	}

	// v0.4.36 — Ctrl-gate for the left-click box-select drag. When ON
	// (default), a no-tool left-drag on empty terrain only starts the
	// RTS-style multi-select box if Left Ctrl is held; without it, the
	// drag is treated as a deselect (and items / vegetation under the
	// cursor still open their inspector card as before). Stops
	// accidental drags from roping a dozen smurfs into a single move
	// order when the player was just adjusting the camera.
	private void AddBoxSelectCtrlRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		var lbl = RowLabel("Box-select needs Ctrl");
		lbl.TooltipText =
			"When ON: left-click + drag only starts a multi-select box\n" +
			"while Left Ctrl is held. Without Ctrl, an empty drag just\n" +
			"clears the current selection. Prevents accidental crowd\n" +
			"orders while panning. Single-smurf left-clicks still work\n" +
			"either way.";
		row.AddChild(lbl);

		_boxSelCtrlBtn = new AnimatedButton
		{
			Text              = _boxSelRequiresCtrl ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = _boxSelRequiresCtrl,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_boxSelCtrlBtn.Toggled += on =>
		{
			_boxSelRequiresCtrl     = on;
			_boxSelCtrlBtn.Text     = on ? "ON" : "OFF";
			BoxSelectRequiresCtrl   = on;   // live static so GameController picks it up immediately
		};
		row.AddChild(_boxSelCtrlBtn);
	}

	// v0.4.32 — RimWorld-style Developer Mode toggle. Off by default;
	// flipping it on reveals the floating DevPanel (F12 to show/hide once
	// dev mode is on) and unlocks the sim-manipulation / spawn /
	// visualisation actions inside. Persists to settings.cfg under
	// gameplay/developer_mode.
	private void AddDeveloperModeRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		var lbl = RowLabel("Developer Mode");
		lbl.TooltipText =
			"Developer Mode (RimWorld-style dev tools).\n" +
			"When ON: F12 toggles a floating debug panel with\n" +
			"  • Sim controls (tick once, speed bursts, force-pause)\n" +
			"  • Selected-smurf manipulation (fill/drain needs, kill, spawn thought)\n" +
			"  • Item / smurf spawning at cursor\n" +
			"  • Map mutation + visualisation overlays\n" +
			"  • Stubs for future systems (combat, weather, traders).\n" +
			"Leave OFF for normal play.";
		row.AddChild(lbl);

		_devModeBtn = new AnimatedButton
		{
			Text              = _devMode ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = _devMode,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_devModeBtn.Toggled += on =>
		{
			_devMode         = on;
			_devModeBtn.Text = on ? "ON" : "OFF";
			SmurfulationC.DevMode.IsEnabled = on;   // notifies DevPanel + HUD
		};
		row.AddChild(_devModeBtn);
	}

	private void AddShowTooltipsRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		row.AddChild(RowLabel("Show Tooltips"));

		_showTooltipsBtn = new AnimatedButton
		{
			Text              = _showTooltips ? "ON" : "OFF",
			ToggleMode        = true,
			ButtonPressed     = _showTooltips,
			Compact           = true,
			CustomMinimumSize = new Vector2(72, 0),
		};
		_showTooltipsBtn.Toggled += on =>
		{
			_showTooltips         = on;
			_showTooltipsBtn.Text = on ? "ON" : "OFF";
		};
		row.AddChild(_showTooltipsBtn);
	}

	private void AddTooltipSizeRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		row.AddChild(RowLabel("Tooltip Size"));

		_tooltipSizeDrop = new OptionButton { CustomMinimumSize = new Vector2(115, 0) };
		StyleOptionButton(_tooltipSizeDrop);
		_tooltipSizeDrop.AddItem("Large");
		_tooltipSizeDrop.AddItem("Normal");
		_tooltipSizeDrop.AddItem("Small");
		_tooltipSizeDrop.Selected = 0;
		_tooltipSizeDrop.ItemSelected += idx =>
		{
			_tooltipSize = TooltipSizeKeys[idx];
			ApplyTooltipSize(_tooltipSize);
		};
		row.AddChild(_tooltipSizeDrop);
	}

	// Roadmap §3.x.4 — UI Size lets the player shrink the floating panels so they
	// take up less of the playfield without changing the camera zoom. 100 % is
	// the default; 66 % and 33 % are descending discrete sizes the player can
	// switch between for high-res screens or when running with a small viewport.
	// Applied by scaling the `UILayer` CanvasLayer's transform; GameController
	// reads the setting at scene-ready and on the `SettingsClosed` signal.
	private void AddUIScaleRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		var lbl = RowLabel("UI Size");
		lbl.TooltipText = "Shrinks the font and minimum sizes on every floating UI panel without moving them away from the viewport edges. 'Normal' and 'Small' reclaim playfield space on small or busy maps. Change applies immediately.";
		lbl.MouseFilter = MouseFilterEnum.Pass;
		row.AddChild(lbl);

		_uiScaleDrop = new OptionButton { CustomMinimumSize = new Vector2(115, 0) };
		StyleOptionButton(_uiScaleDrop);
		foreach (var label in UIScaleLabels) _uiScaleDrop.AddItem(label);
		_uiScaleDrop.Selected = _uiScaleIdx;
		_uiScaleDrop.ItemSelected += idx =>
		{
			_uiScaleIdx = (int)idx;
			ApplyUIScale(UIScaleValues[_uiScaleIdx]);
		};
		row.AddChild(_uiScaleDrop);
	}

	// Static public accessor used by GameController on scene-ready / on close
	// of the settings panel to read the persisted UI scale without depending on
	// SettingsPanel being instantiated.
	public static float LoadSavedUIScale()
	{
		var cfg = new ConfigFile();
		if (cfg.Load("user://settings.cfg") != Error.Ok) return 1f;
		int idx = (int)(double)cfg.GetValue("gameplay", "ui_scale_idx", 0.0);
		idx = Mathf.Clamp(idx, 0, UIScaleValues.Length - 1);
		return UIScaleValues[idx];
	}

	private void ApplyUIScale(float scale)
	{
		// v0.3.20: live re-scale. UITheme.SetUIScale fires UIScaleChanged;
		// every Phase-3.x component subscribes in its `_Ready` and rebuilds
		// itself with the new scale on the next frame. No scene reload needed.
		SmurfulationC.UI.UITheme.SetUIScale(scale);
	}

	private void AddZoomSpeedRow(VBoxContainer parent)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 12);
		parent.AddChild(row);

		var lbl = RowLabel("Zoom Speed");
		lbl.TooltipText = "How fast the camera zooms per mouse-wheel tick (1 = slowest, 10 = fastest)";
		lbl.MouseFilter = MouseFilterEnum.Pass;
		row.AddChild(lbl);

		var wrap = new Control();
		wrap.SizeFlagsHorizontal = SizeFlags.Fill | SizeFlags.Expand;
		wrap.CustomMinimumSize   = new Vector2(180, 28);
		row.AddChild(wrap);

		var bar = new ProgressBar
		{
			MinValue       = 1,
			MaxValue       = 10,
			Value          = _zoomSpeed,
			ShowPercentage = false,
			MouseFilter    = MouseFilterEnum.Ignore,
		};
		bar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		var fill = new StyleBoxFlat { BgColor = Gold };
		fill.SetCornerRadiusAll(4);
		bar.AddThemeStyleboxOverride("fill", fill);

		var track = new StyleBoxFlat
		{
			BgColor = new Color(Gold.R * 0.35f, Gold.G * 0.35f, Gold.B * 0.35f, 0.45f),
		};
		track.SetCornerRadiusAll(4);
		bar.AddThemeStyleboxOverride("background", track);
		wrap.AddChild(bar);

		_zoomSlider = new HSlider { MinValue = 1, MaxValue = 10, Step = 1, Value = _zoomSpeed };
		_zoomSlider.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		var invis = new StyleBoxFlat { BgColor = Colors.Transparent };
		_zoomSlider.AddThemeStyleboxOverride("slider",                 invis);
		_zoomSlider.AddThemeStyleboxOverride("grabber_area",           invis);
		_zoomSlider.AddThemeStyleboxOverride("grabber_area_highlight", invis);
		wrap.AddChild(_zoomSlider);

		_zoomVal = new Label
		{
			Text                = _zoomSpeed.ToString(),
			CustomMinimumSize   = new Vector2(40, 0),
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		_zoomVal.AddThemeColorOverride("font_color", DarkWood);
		_zoomVal.AddThemeFontSizeOverride("font_size", 15);

		_zoomSlider.ValueChanged += v =>
		{
			_zoomSpeed  = (int)v;
			bar.Value   = _zoomSpeed;
			_zoomVal.Text = _zoomSpeed.ToString();
		};

		row.AddChild(_zoomVal);
	}

	private void ApplyTooltipSize(string size)
	{
		GetTree().Root.Theme = GameController.BuildTooltipTheme(TooltipFontSize(size));
	}

	// v0.3.20: font-size steps widened from 10/8/6 → 18/14/10 so the difference
	// between Large / Normal / Small is actually perceptible at typical viewport
	// sizes. The old values were technically wired but too close to read apart.
	public static int TooltipFontSize(string size) => size switch
	{
		"normal" => 10,
		"small"  => 6,
		_        => 14,
	};

	// Maps slider 1–10 → zoom multiplier per scroll tick.
	// Speed 5 (default) ≈ 1.155, matching the original hardcoded 1.15.
	public static float ZoomFactorFromSpeed(int speed)
	{
		speed = Mathf.Clamp(speed, 1, 10);
		return 1.04f + (speed - 1) * (1.30f - 1.04f) / 9f;
	}

	// ── Persistence ───────────────────────────────────────────────────────────

	private void SaveSettings()
	{
		var cfg = new ConfigFile();
		cfg.SetValue("audio", "master", (double)_masterSlider.Value);
		cfg.SetValue("audio", "music",  (double)_musicSlider.Value);
		cfg.SetValue("audio", "sfx",    (double)_sfxSlider.Value);
		cfg.SetValue("display", "resolution_idx", _resDrop.Selected);
		cfg.SetValue("display", "fullscreen",      _isFullscreen);
		cfg.SetValue("gameplay", "pause_on_start",  _pauseOnStart);
		cfg.SetValue("gameplay", "show_tooltips",   _showTooltips);
		cfg.SetValue("gameplay", "tooltip_size",    _tooltipSize);
		cfg.SetValue("gameplay", "ui_scale_idx",    _uiScaleIdx);
		cfg.SetValue("gameplay", "zoom_speed",      _zoomSpeed);
		cfg.SetValue("gameplay", "developer_mode",  _devMode);
		cfg.SetValue("gameplay", "box_select_requires_ctrl", _boxSelRequiresCtrl);

		// v0.4.3 — persist each keybinding under [keybindings]. Loader
		// reads back into Godot's InputMap at LoadSettings time.
		foreach (var (action, _, defaultKey, _) in DefaultKeybindings)
		{
			Key k = _keybindings.TryGetValue(action, out var v) ? v : defaultKey;
			cfg.SetValue("keybindings", action, (int)k);
		}

		cfg.Save(CfgPath);
	}

	private void LoadSettings()
	{
		// Apply defaults first so buses are set even with no save file
		SetVolume(_masterSlider, _masterVal, "Master", 100);
		SetVolume(_musicSlider,  _musicVal,  "Music",   50);
		SetVolume(_sfxSlider,    _sfxVal,    "SFX",    100);

		var cfg = new ConfigFile();
		if (cfg.Load(CfgPath) != Error.Ok) return;

		SetVolume(_masterSlider, _masterVal, "Master",
			(int)(double)cfg.GetValue("audio", "master", 100.0));
		SetVolume(_musicSlider, _musicVal, "Music",
			(int)(double)cfg.GetValue("audio", "music",  50.0));
		SetVolume(_sfxSlider, _sfxVal, "SFX",
			(int)(double)cfg.GetValue("audio", "sfx",   100.0));

		int resIdx = (int)cfg.GetValue("display", "resolution_idx", 0);
		if (resIdx >= 0 && resIdx < Resolutions.Length)
		{
			_resDrop.Selected = resIdx;
			OnResolutionPicked(resIdx);
		}

		bool fs = (bool)cfg.GetValue("display", "fullscreen", false);
		if (fs != _isFullscreen)
			SetDisplayMode(fs);

		bool ps = (bool)cfg.GetValue("gameplay", "pause_on_start", true);
		_pauseOnStart                  = ps;
		_pauseOnStartBtn.Text          = ps ? "ON" : "OFF";
		_pauseOnStartBtn.ButtonPressed = ps;

		bool tt = (bool)cfg.GetValue("gameplay", "show_tooltips", true);
		_showTooltips                  = tt;
		_showTooltipsBtn.Text          = tt ? "ON" : "OFF";
		_showTooltipsBtn.ButtonPressed = tt;

		string ts = (string)cfg.GetValue("gameplay", "tooltip_size", "large");
		_tooltipSize = ts;
		int tsIdx = System.Array.IndexOf(TooltipSizeKeys, ts);
		_tooltipSizeDrop.Selected = tsIdx >= 0 ? tsIdx : 0;
		ApplyTooltipSize(_tooltipSize);

		_uiScaleIdx = Mathf.Clamp((int)(double)cfg.GetValue("gameplay", "ui_scale_idx", 0.0),
								  0, UIScaleValues.Length - 1);
		_uiScaleDrop.Selected = _uiScaleIdx;
		ApplyUIScale(UIScaleValues[_uiScaleIdx]);

		int zs = (int)(double)cfg.GetValue("gameplay", "zoom_speed", 5.0);
		_zoomSpeed            = Mathf.Clamp(zs, 1, 10);
		_zoomSlider.Value     = _zoomSpeed;
		_zoomVal.Text         = _zoomSpeed.ToString();

		bool dm = (bool)cfg.GetValue("gameplay", "developer_mode", false);
		_devMode                       = dm;
		_devModeBtn.Text               = dm ? "ON" : "OFF";
		_devModeBtn.ButtonPressed      = dm;
		SmurfulationC.DevMode.IsEnabled = dm;

		bool bs = (bool)cfg.GetValue("gameplay", "box_select_requires_ctrl", true);
		_boxSelRequiresCtrl        = bs;
		_boxSelCtrlBtn.Text        = bs ? "ON" : "OFF";
		_boxSelCtrlBtn.ButtonPressed = bs;
		BoxSelectRequiresCtrl      = bs;

		// v0.4.3 — keybindings. Read each action's persisted keycode (or
		// default) and wire it into Godot's InputMap so call sites can
		// `Input.IsActionPressed("kb_...")`. The button labels follow.
		foreach (var (action, _, defaultKey, _) in DefaultKeybindings)
		{
			int keyInt = (int)cfg.GetValue("keybindings", action, (int)defaultKey);
			Key k = (Key)keyInt;
			_keybindings[action] = k;
			ApplyKeybindingToInputMap(action, k);
			if (_keybindingButtons.TryGetValue(action, out var btn))
				btn.Text = KeyDisplayName(k);
		}
	}

	private void OnReset()
	{
		SetVolume(_masterSlider, _masterVal, "Master", 100);
		SetVolume(_musicSlider,  _musicVal,  "Music",   50);
		SetVolume(_sfxSlider,    _sfxVal,    "SFX",    100);

		_resDrop.Selected = 0;
		OnResolutionPicked(0);

		SetDisplayMode(false);

		// v0.4.3 — reset every keybinding to its registered default.
		foreach (var (action, _, defaultKey, _) in DefaultKeybindings)
		{
			_keybindings[action] = defaultKey;
			ApplyKeybindingToInputMap(action, defaultKey);
			if (_keybindingButtons.TryGetValue(action, out var btn))
				btn.Text = KeyDisplayName(defaultKey);
		}

		_pauseOnStart                  = true;
		_pauseOnStartBtn.Text          = "ON";
		_pauseOnStartBtn.ButtonPressed = true;

		_showTooltips                  = true;
		_showTooltipsBtn.Text          = "ON";
		_showTooltipsBtn.ButtonPressed = true;

		_tooltipSize             = "large";
		_tooltipSizeDrop.Selected = 0;
		ApplyTooltipSize("large");

		_uiScaleIdx              = 0;
		_uiScaleDrop.Selected    = 0;
		ApplyUIScale(UIScaleValues[0]);

		_zoomSpeed        = 5;
		_zoomSlider.Value = 5;
		_zoomVal.Text     = "5";

		_devMode                       = false;
		_devModeBtn.Text               = "OFF";
		_devModeBtn.ButtonPressed      = false;
		SmurfulationC.DevMode.IsEnabled = false;

		_boxSelRequiresCtrl            = true;
		_boxSelCtrlBtn.Text            = "ON";
		_boxSelCtrlBtn.ButtonPressed   = true;
		BoxSelectRequiresCtrl          = true;
	}

	private void OnBack() => Close();

	// ── Helpers ───────────────────────────────────────────────────────────────

	private Label RowLabel(string text)
	{
		var l = new Label
		{
			Text = text,
			CustomMinimumSize   = new Vector2(148, 0),
			VerticalAlignment   = VerticalAlignment.Center,
		};
		l.AddThemeColorOverride("font_color", DarkWood);
		l.AddThemeFontSizeOverride("font_size", 16);
		return l;
	}

	private Label BigLabel(string text)
	{
		var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
		l.AddThemeColorOverride("font_color", DarkWood);
		l.AddThemeFontSizeOverride("font_size", 28);
		ApplyGrobold(l);
		return l;
	}

	private Label SectionLabel(string text)
	{
		var l = new Label { Text = text };
		l.AddThemeColorOverride("font_color", Brown);
		l.AddThemeFontSizeOverride("font_size", 18);
		ApplyGrobold(l);
		return l;
	}

	private static void ApplyGrobold(Label l)
	{
		const string font = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(font))
			l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
	}

	private static HSeparator MakeSep()
	{
		var h = new HSeparator();
		h.AddThemeColorOverride("color", new Color(0.55f, 0.38f, 0.12f, 0.6f));
		return h;
	}

	// v0.4.3 — keybinding catalogue. Single source of truth: every input
	// action the game listens for is registered here as `(action,
	// display label, default-key, description)`. The Settings panel
	// renders one row per entry; persistence writes the per-action key
	// to settings.cfg under [keybindings] and re-binds Godot's InputMap
	// at load time.
	//
	// Adding a new action: one row here + read `Input.IsActionPressed`
	// or pull the key via `KeybindingsRegistry.GetKey` at the call site.
	private static readonly (string Action, string Label, Key DefaultKey, string Description)[] DefaultKeybindings =
	{
		("kb_pause",        "Pause / Unpause",     Key.Space,    "Toggles the simulation tick."),
		("kb_zoom_cycle",   "Cycle Zoom",          Key.Tab,      "Steps through Village / Neighbourhood / Individual zoom levels."),
		("kb_speed_1",      "Speed 1×",            Key.Key1,     "Normal sim speed."),
		("kb_speed_2",      "Speed 2×",            Key.Key2,     "Fast sim speed."),
		("kb_speed_5",      "Speed 5×",            Key.Key3,     "Very fast sim speed."),
		("kb_speed_10",     "Speed 10×",           Key.Key4,     "Maximum sim speed."),
		("kb_context_menu", "Context Menu (hold)", Key.Alt,      "Hold and right-click to open the action context menu."),
		("kb_menu",         "Open Pause Menu",     Key.Escape,   "Opens the pause / save / load menu."),
	};

	private readonly System.Collections.Generic.Dictionary<string, Key> _keybindings = new();
	private readonly System.Collections.Generic.Dictionary<string, Button> _keybindingButtons = new();
	private string? _bindingActionInProgress;

	private void BuildKeybindingsSection(VBoxContainer parent)
	{
		var explain = new Label
		{
			Text                = "Click any binding to rebind. Press Escape to cancel.",
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		explain.AddThemeFontSizeOverride("font_size", 10);
		explain.AddThemeColorOverride("font_color", Brown);
		parent.AddChild(explain);

		foreach (var (action, label, _, desc) in DefaultKeybindings)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 12);
			parent.AddChild(row);

			var lbl = new Label { Text = label, TooltipText = desc };
			lbl.AddThemeFontSizeOverride("font_size", 13);
			lbl.AddThemeColorOverride("font_color", DarkWood);
			lbl.CustomMinimumSize = new Vector2(180, 0);
			row.AddChild(lbl);

			Key cur = _keybindings.TryGetValue(action, out var k) ? k : DefaultKeyFor(action);
			var btn = new AnimatedButton
			{
				Text              = KeyDisplayName(cur),
				CustomMinimumSize = new Vector2(160, 32),
				TooltipText       = $"{desc}\nClick to rebind.",
				Compact           = true,
			};
			string capturedAction = action;
			btn.Pressed += () => BeginRebind(capturedAction);
			_keybindingButtons[action] = btn;
			row.AddChild(btn);

			var resetBtn = new AnimatedButton
			{
				Text              = "Reset",
				CustomMinimumSize = new Vector2(70, 32),
				Compact           = true,
			};
			resetBtn.Pressed += () =>
			{
				_keybindings[capturedAction] = DefaultKeyFor(capturedAction);
				ApplyKeybindingToInputMap(capturedAction, DefaultKeyFor(capturedAction));
				_keybindingButtons[capturedAction].Text = KeyDisplayName(DefaultKeyFor(capturedAction));
			};
			row.AddChild(resetBtn);
		}
	}

	private static Key DefaultKeyFor(string action)
	{
		foreach (var entry in DefaultKeybindings)
			if (entry.Action == action) return entry.DefaultKey;
		return Key.None;
	}

	private void BeginRebind(string action)
	{
		_bindingActionInProgress = action;
		if (_keybindingButtons.TryGetValue(action, out var btn))
			btn.Text = "Press a key…";
	}

	public override void _Input(InputEvent ev)
	{
		if (_bindingActionInProgress != null && ev is InputEventKey ke && ke.Pressed)
		{
			if (ke.Keycode == Key.Escape)
			{
				// Cancel — restore label.
				var act = _bindingActionInProgress;
				_bindingActionInProgress = null;
				if (_keybindingButtons.TryGetValue(act, out var btn))
				{
					Key cur = _keybindings.TryGetValue(act, out var k) ? k : DefaultKeyFor(act);
					btn.Text = KeyDisplayName(cur);
				}
				GetViewport().SetInputAsHandled();
				return;
			}
			var bindingAction = _bindingActionInProgress;
			_bindingActionInProgress = null;
			_keybindings[bindingAction] = ke.Keycode;
			ApplyKeybindingToInputMap(bindingAction, ke.Keycode);
			if (_keybindingButtons.TryGetValue(bindingAction, out var btnFinal))
				btnFinal.Text = KeyDisplayName(ke.Keycode);
			GetViewport().SetInputAsHandled();
		}
	}

	// Writes the (action → keycode) entry into Godot's InputMap so
	// `Input.IsActionPressed(action)` works immediately. Same call
	// fires at LoadSettings time and on every rebind / reset.
	private static void ApplyKeybindingToInputMap(string action, Key key)
	{
		if (!InputMap.HasAction(action)) InputMap.AddAction(action);
		// Clear existing events; we keep one keyboard binding per action.
		var events = InputMap.ActionGetEvents(action);
		foreach (var e in events) InputMap.ActionEraseEvent(action, e);
		var ev = new InputEventKey { Keycode = key };
		InputMap.ActionAddEvent(action, ev);
	}

	// Display string for a key, mirroring the format Godot uses in its
	// editor (Space, Tab, Escape, etc.). Falls back to the OS Key name.
	private static string KeyDisplayName(Key k) => k switch
	{
		Key.None      => "(unbound)",
		Key.Space     => "Space",
		Key.Tab       => "Tab",
		Key.Escape    => "Escape",
		Key.Alt       => "Alt",
		Key.Ctrl      => "Ctrl",
		Key.Shift     => "Shift",
		Key.Key0      => "0",
		Key.Key1      => "1",
		Key.Key2      => "2",
		Key.Key3      => "3",
		Key.Key4      => "4",
		Key.Key5      => "5",
		Key.Key6      => "6",
		Key.Key7      => "7",
		Key.Key8      => "8",
		Key.Key9      => "9",
		_             => k.ToString(),
	};
}
