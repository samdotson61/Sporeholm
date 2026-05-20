using Godot;
using System.Collections.Generic;
using Sporeholm.Simulation.Items;
using Sporeholm.UI;

// Top-bar HUD: era name, S.D. date, population, mood summary, speed controls.
// Phase 3.x refactor — was a full-width flush bar with `ColorRect` background;
// now renders as two floating capsules (stats top-left, speed/menu top-right)
// using `FloatingPanelStyle` so it matches the rest of the Phase 3.x UI and
// leaves the top centre / corners clear for the ResourceHUD + TileInfoOverlay.
public partial class HUDController : Control
{
	[Signal] public delegate void MenuRequestedEventHandler();

	private static readonly Color HudBg    = new(0.14f, 0.09f, 0.04f, 0.92f);
	private static readonly Color Gold     = new(0.88f, 0.70f, 0.22f);
	private static readonly Color Parchment = new(0.95f, 0.89f, 0.70f);
	private static readonly Color Muted    = new(0.60f, 0.50f, 0.32f);

	private Label          _eraLabel = null!, _dateLabel = null!, _popLabel = null!, _moodLabel = null!;

	// v0.3.41 — per-category collapsible widgets for the resource row.
	// Click on the caret toggles the expansion box's visibility; the
	// flag and caret text track the state.
	private sealed class ResourceCategory
	{
		public Button         CaretBtn       = null!;
		public Label          TotalLbl       = null!;
		public VBoxContainer  ExpansionBox   = null!;
		public bool           Expanded;
		// v0.3.46 (Phase 4) — sub-item count labels keyed by ItemRegistry
		// SubType string. _Process walks the inventory snapshot and writes
		// the live totals into these.
		public Dictionary<string, Label> SubLabels = new();
		// v0.3.46 — what ItemKind this category represents, used by the
		// inventory snapshot sum. Stone / Wood are special-cased on
		// MaterialFamily ("Stone" / "Wood") rather than ItemKind.
		public ItemKind  Kind = ItemKind.Food;
		public string?   MaterialFamily;        // non-null = "sum items in this family"
	}
	// v0.4.2 — Magic category re-enabled. Magic items now have production
	// paths: MagicBerry plants drop RawEssence alongside the food item,
	// and MagicCrystal stone-ore-vein excavation drops CrystalShard
	// alongside the StoneBlock. The category is no longer always-zero.
	private ResourceCategory _foodCat  = null!;
	private ResourceCategory _stoneCat = null!;
	private ResourceCategory _woodCat  = null!;
	private ResourceCategory _magicCat = null!;
	private AnimatedButton _pauseBtn = null!;
	// v0.3.28 — kept so GameController can query "is the cursor over a HUD
	// capsule?" before applying mouse-wheel zoom.
	private PanelContainer _leftPanel  = null!;
	private PanelContainer _rightPanel = null!;

	private readonly List<(AnimatedButton btn, float speed)> _speedBtns = new();
	private float _activeSpeed = 1f;

	// Single source of truth — never mirror locally.
	private bool IsPaused => Sim?.Paused ?? false;

	// Injected by GameController after construction
	public Sporeholm.SimulationManager Sim { get; set; } = null!;

	public override void _Ready()
	{
		// Full-rect transparent container; per-layout content lives inside
		// BuildContent() so we can rebuild on UI-scale changes without losing
		// the root Control or its anchor preset.
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Pass;

		BuildContent();
		UITheme.UIScaleChanged += OnUIScaleChanged;
	}

	public override void _ExitTree()
	{
		UITheme.UIScaleChanged -= OnUIScaleChanged;
	}

	// v0.3.20 — rebuilds the HUD content on Settings → UI Size changes so the
	// player doesn't have to return to the main menu. The HUDController root
	// stays in place; only the inner band + capsule structure is torn down
	// and re-created with the new scale. `_speedBtns` is cleared first so the
	// old buttons don't leak into the new active-speed tracking.
	private void OnUIScaleChanged()
	{
		_speedBtns.Clear();
		foreach (Node c in GetChildren()) c.QueueFree();
		BuildContent();
		// On the next _Process the resource labels populate from the sim, and
		// SyncPauseButton runs on the next UpdateStats — no explicit refresh
		// needed.
	}

	// All HUD content construction. Originally inlined in `_Ready`; extracted
	// so OnUIScaleChanged can call it again after clearing children.
	private void BuildContent()
	{
		var cfg = new ConfigFile();
		bool tips = cfg.Load("user://settings.cfg") != Error.Ok
			|| (bool)cfg.GetValue("gameplay", "show_tooltips", true);

		var band = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
		band.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		band.OffsetLeft   = UITheme.EdgeInset;
		band.OffsetRight  = -UITheme.EdgeInset;
		band.OffsetTop    = UITheme.EdgeInset;
		band.AddThemeConstantOverride("separation", 12);
		AddChild(band);

		// ────────────────────────────────────────────────────────────────────
		// Left capsule — two rows: stats on top, resources beneath.
		// ResourceHUD was a separate centred-floating component (v0.3.14–v0.3.18);
		// v0.3.19 merges it into here per the user's "top-left, no overlap"
		// request. Single VBox keeps the panel compact in the top-left corner.
		// ────────────────────────────────────────────────────────────────────
		_leftPanel = new PanelContainer
		{
			MouseFilter         = MouseFilterEnum.Stop,
			// v0.3.44 — pin each capsule to the band's top edge so an
			// expanded left capsule (e.g. all resource categories open)
			// doesn't pull the right-hand Speed/Menu capsule down to match
			// its height. HBoxContainer's default vertical sizing is
			// Fill, which is what produced the symptom in the screenshot.
			SizeFlagsVertical   = SizeFlags.ShrinkBegin,
		};
		_leftPanel.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
		band.AddChild(_leftPanel);
		var leftPanel = _leftPanel;

		var leftVbox = new VBoxContainer();
		leftVbox.AddThemeConstantOverride("separation", 4);
		leftPanel.AddChild(leftVbox);

		// Row 1 — stats
		var statsRow = new HBoxContainer();
		statsRow.AddThemeConstantOverride("separation", 10);
		leftVbox.AddChild(statsRow);

		statsRow.AddChild(Lbl("🌅", UITheme.Scaled(16), Gold));
		_eraLabel = Lbl("Dawn Era", UITheme.Scaled(15), Parchment);
		statsRow.AddChild(_eraLabel);

		statsRow.AddChild(Divider());

		statsRow.AddChild(Lbl("📅", UITheme.Scaled(14), Parchment));
		_dateLabel = Lbl("Day 1, Spring, Year 0 S.D.", UITheme.Scaled(13), Parchment);
		statsRow.AddChild(_dateLabel);

		statsRow.AddChild(Divider());

		statsRow.AddChild(ShroompIcon(UITheme.Scaled(18)));
		_popLabel = Lbl("Pop: 7", UITheme.Scaled(13), Parchment);
		statsRow.AddChild(_popLabel);

		statsRow.AddChild(Divider());

		_moodLabel = Lbl("😊 0  😢 0", UITheme.Scaled(13), Parchment);
		statsRow.AddChild(_moodLabel);

		// Row 2 — resources. Each category is a VBox: a header row (caret
		// + name + total) and an initially-hidden expansion VBox listing
		// the known sub-items. Clicking the caret toggles the expansion.
		// Sub-items here are placeholders until the Phase 4 procedural
		// item system lands; the Resources tab in BottomTabPanel will
		// show the full granular ledger.
		var resRow = new HBoxContainer();
		resRow.AddThemeConstantOverride("separation", 14);
		resRow.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		leftVbox.AddChild(resRow);

		// v0.3.46 (Phase 4) — sub-item rows are keyed by their canonical
		// ItemRegistry / MaterialRegistry sub-type string so _Process can
		// look them up against the inventory snapshot. The display label
		// uses the friendly name; the lookup key is the raw sub-type.
		// v0.4.2 — sub-items aligned with the new taxonomy. Food shows
		// the four real food sub-types (Capberry / SmallMushroom /
		// HerbCluster / MagicBerry); SmallMushroom row counts EVERY
		// mushroom variant via material aggregation. Stone adds Quartz
		// + MagicCrystal. Wood reduces to DeadWood / LivingWood / Fungal.
		_foodCat  = AddCollapsibleResource(resRow, "🍓", "Food",
			ItemKind.Food, materialFamily: null,
			new[]
			{
				("🫐", "Capberry",      "Capberry"),
				("🍄", "Small Mushroom",  "SmallMushroom"),
				("🌿", "Herb Cluster",    "HerbCluster"),
				("🌺", "Magic Berry",     "MagicBerry"),
			});
		_stoneCat = AddCollapsibleResource(resRow, "🪨", "Stone",
			ItemKind.Material, materialFamily: "Stone",
			new[]
			{
				("◼",  "Granite",       "Granite"),
				("◻",  "Limestone",     "Limestone"),
				("◼",  "Marble",        "Marble"),
				("⬛", "Obsidian",       "Obsidian"),
				("◇",  "Quartz",        "Quartz"),
				("✨", "Magic Stone",    "Magicstone"),
				("💎", "Magic Crystal",  "MagicCrystal"),
			});
		_woodCat  = AddCollapsibleResource(resRow, "🪵", "Wood",
			ItemKind.Material, materialFamily: "Wood",
			new[]
			{
				("🪵", "Dead Wood",   "DeadWood"),
				("🌿", "Living Wood", "LivingWood"),
				("🍄", "Fungal Wood", "Fungal"),
			});
		_magicCat = AddCollapsibleResource(resRow, "✨", "Magic",
			ItemKind.Magic, materialFamily: null,
			new[]
			{
				("✨", "Raw Essence",   "RawEssence"),
				("💎", "Crystal Shard", "CrystalShard"),
			});

		var qualifier = Lbl("(unstored — Phase 5 will gate)", UITheme.Scaled(10), Muted);
		qualifier.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		resRow.AddChild(qualifier);

		// Flexible spacer — the ResourceHUD floats centred over this gap; the
		// left and right capsules size to their content so they never crowd it.
		band.AddChild(new Control
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			MouseFilter         = MouseFilterEnum.Pass,
		});

		// ────────────────────────────────────────────────────────────────────
		// Right capsule — Speed / Menu
		// ────────────────────────────────────────────────────────────────────
		_rightPanel = new PanelContainer
		{
			MouseFilter         = MouseFilterEnum.Stop,
			SizeFlagsVertical   = SizeFlags.ShrinkBegin,   // see v0.3.44 note on _leftPanel
		};
		_rightPanel.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
		band.AddChild(_rightPanel);
		var rightPanel = _rightPanel;

		var rightHbox = new HBoxContainer();
		rightHbox.AddThemeConstantOverride("separation", 6);
		rightPanel.AddChild(rightHbox);

		rightHbox.AddChild(Lbl("Speed:", UITheme.Scaled(12), Gold));

		// Pause button
		_pauseBtn = MakeSmallBtn("⏸");
		_pauseBtn.Pressed += OnPauseToggle;
		if (tips) _pauseBtn.TooltipText = "Pause / Unpause simulation";
		rightHbox.AddChild(_pauseBtn);

		// Speed preset buttons
		// v0.4.19 — multiplier values now match the displayed labels. Until
		// this patch the buttons displayed 1×/2×/5×/10× but actually
		// requested 1×/5×/20×/100× from `SimulationManager.SetSpeed`,
		// so "2×" was running the sim five times faster than the
		// player asked for. The sim tick interval is `BaseTickIntervalMs /
		// SpeedMultiplier`, so movement, animations, and clock
		// progression all scale linearly off this value.
		AddSpeedBtn(rightHbox, "1×",  1f,  tips ? "Normal speed (1× — real-time)"          : "");
		AddSpeedBtn(rightHbox, "2×",  2f,  tips ? "Double speed (2×)"                       : "");
		AddSpeedBtn(rightHbox, "5×",  5f,  tips ? "Fast (5×)"                               : "");
		AddSpeedBtn(rightHbox, "10×", 10f, tips ? "Maximum speed (10×)"                     : "");

		SetActiveSpeed(1f);

		// Main Menu button
		rightHbox.AddChild(Divider());
		var menu = MakeSmallBtn("Menu");
		menu.Modulate = new Color(1.0f, 0.90f, 0.50f);
		menu.Pressed += () => EmitSignal(SignalName.MenuRequested);
		if (tips) menu.TooltipText = "Open pause menu";
		rightHbox.AddChild(menu);

		// ── Stat label tooltips ────────────────────────────────────────────
		if (tips)
		{
			_eraLabel.MouseFilter  = MouseFilterEnum.Pass;
			_eraLabel.TooltipText  = "Current historical era of the colony.\nEra advances as population and culture grow.";
			_dateLabel.MouseFilter = MouseFilterEnum.Pass;
			_dateLabel.TooltipText = "In-game date: Season, Day, Year S.D.\n120 days per year (4 seasons × 30 days).";
			_popLabel.MouseFilter  = MouseFilterEnum.Pass;
			_popLabel.TooltipText  = "Total living shroomps in the colony.";
			_moodLabel.MouseFilter = MouseFilterEnum.Pass;
			_moodLabel.TooltipText = "😊 Inspired shroomps (mood ≥ 80)\n😢 Distressed or worse (mood < 40).";
		}
	}

	// ── Public update ──────────────────────────────────────────────────────────

	// v0.3.28 — used by GameController.IsMouseOverUI to suppress mouse-wheel
	// zoom while the cursor is over either HUD capsule. Returns true for
	// the left (stats/resources) and right (speed/menu) panels.
	public bool IsMouseOverBars()
	{
		var m = GetViewport().GetMousePosition();
		if (_leftPanel  != null && _leftPanel .GetGlobalRect().HasPoint(m)) return true;
		if (_rightPanel != null && _rightPanel.GetGlobalRect().HasPoint(m)) return true;
		return false;
	}

	public void UpdateStats(string date, int pop, int inspired, int distressed)
	{
		// v0.4.23 — write-elide. UpdateStats fires per snapshot push (60 Hz
		// at 1× speed). Skipping unchanged label writes saves the
		// Godot text-layout pass + canvas redraw for stats that change
		// at most once per in-game hour.
		SetTextIfChanged(_dateLabel, date);
		SetTextIfChanged(_popLabel,  $"Pop: {pop}");
		SetTextIfChanged(_moodLabel, $"😊 {inspired}  😢 {distressed}");
		UpdateEraLabel(date);
		SyncPauseButton(); // keep button label/tint truthful on every tick
	}

	// ── Pause ──────────────────────────────────────────────────────────────────

	private void OnPauseToggle()
	{
		if (Sim == null) return;
		Sim.TogglePause();
		SyncPauseButton();
		RefreshSpeedHighlights();
	}

	public void SyncPauseButton()
	{
		bool paused = IsPaused;
		_pauseBtn.Text     = paused ? "▶" : "⏸";
		_pauseBtn.Modulate = paused ? new Color(1.0f, 0.85f, 0.30f) : Colors.White;
	}

	// ── Speed buttons ──────────────────────────────────────────────────────────

	private void AddSpeedBtn(HBoxContainer parent, string label, float speed, string tooltip = "")
	{
		var btn = MakeSmallBtn(label);
		btn.Pressed += () =>
		{
			if (Sim == null) return;
			if (IsPaused)
			{
				Sim.TogglePause();
				SyncPauseButton();
			}
			Sim.SetSpeed(speed);
			SetActiveSpeed(speed);
		};
		if (tooltip.Length > 0) btn.TooltipText = tooltip;
		_speedBtns.Add((btn, speed));
		parent.AddChild(btn);
	}

	private void SetActiveSpeed(float speed)
	{
		_activeSpeed = speed;
		RefreshSpeedHighlights();
	}

	private void RefreshSpeedHighlights()
	{
		foreach (var (btn, speed) in _speedBtns)
		{
			bool active = !IsPaused && Mathf.IsEqualApprox(speed, _activeSpeed);
			btn.Modulate = active
				? new Color(1.0f, 0.85f, 0.30f)  // gold = active
				: Colors.White;
		}
	}

	// ── Era label ──────────────────────────────────────────────────────────────

	private void UpdateEraLabel(string date)
	{
		if (!int.TryParse(ExtractYear(date), out int year)) return;
		_eraLabel.Text = year switch
		{
			< 50  => "Dawn Era",
			< 100 => "Shrinking Era",
			< 160 => "Blue Emergence",
			< 240 => "Stork Pact Era",
			< 340 => "Bottleneck Era",
			< 430 => "Mushroom Age",
			< 550 => "Classical Era",
			_     => "Modern Era",
		};
	}

	private static string ExtractYear(string date)
	{
		var idx = date.IndexOf("Year ");
		if (idx < 0) return "0";
		var rest = date[(idx + 5)..];
		var sp   = rest.IndexOf(' ');
		return sp > 0 ? rest[..sp] : rest;
	}

	// ── Helpers ────────────────────────────────────────────────────────────────

	// Each `_Process` tick, pull the colony's resource ledger from the sim and
	// update the four resource labels. Cheap to call every frame — snapshot
	// returns a copy of four floats.
	// v0.4.27 — throttle removed (was 200 ms in v0.4.23). Gameplay
	// requirement: resource totals must visibly update the moment they
	// actually change in the sim. With v0.4.23's `SetTextIfChanged`
	// guard the per-frame cost when nothing changed is a quick
	// inventory-snapshot walk + a handful of string compares — no
	// `Label.Text` writes, no canvas redraws, no GPU sync points. Only
	// frames where a resource truly changed pay any visible cost.
	public override void _Process(double delta)
	{
		if (Sim == null) return;

		// v0.3.46 (Phase 4) — pull the inventory snapshot once per frame
		// refresh and aggregate per-category + per-subtype totals in a
		// single walk. The float-ledger fallback (r.Food etc.) still
		// works because ColonyResources.Food is now an inventory-derived
		// total + back-compat unstored buffer; we use the inventory
		// directly here so the same walk fills the sub-item breakdown
		// labels.
		var inv = Sim.GetInventorySnapshot();
		int foodTotal = 0, magicTotal = 0;
		int stoneTotal = 0, woodTotal = 0;

		// Zero out all sub-labels first so depleted entries fall back to 0.
		// `SetTextIfChanged` skips the label write when the text is
		// already what we'd be setting it to — a Label only redraws
		// when its text actually mutates.
		foreach (var lbl in _foodCat .SubLabels.Values) SetTextIfChanged(lbl, "0");
		foreach (var lbl in _stoneCat.SubLabels.Values) SetTextIfChanged(lbl, "0");
		foreach (var lbl in _woodCat .SubLabels.Values) SetTextIfChanged(lbl, "0");
		foreach (var lbl in _magicCat.SubLabels.Values) SetTextIfChanged(lbl, "0");

		foreach (var row in inv)
		{
			switch (row.Kind)
			{
				case ItemKind.Food:
					foodTotal += row.Quantity;
					if (_foodCat.SubLabels.TryGetValue(row.SubType, out var fl))
						SetTextIfChanged(fl, (ParseInt(fl.Text) + row.Quantity).ToString());
					break;
				case ItemKind.Material:
					if (row.MaterialFamily == "Stone")
					{
						stoneTotal += row.Quantity;
						if (_stoneCat.SubLabels.TryGetValue(row.MaterialSubType, out var sl))
							SetTextIfChanged(sl, (ParseInt(sl.Text) + row.Quantity).ToString());
					}
					else if (row.MaterialFamily == "Wood")
					{
						woodTotal += row.Quantity;
						if (_woodCat.SubLabels.TryGetValue(row.MaterialSubType, out var wl))
							SetTextIfChanged(wl, (ParseInt(wl.Text) + row.Quantity).ToString());
					}
					break;
				case ItemKind.Magic:
					magicTotal += row.Quantity;
					if (_magicCat.SubLabels.TryGetValue(row.SubType, out var ml))
						SetTextIfChanged(ml, (ParseInt(ml.Text) + row.Quantity).ToString());
					break;
			}
		}

		// Fold the legacy "unstored" buffer in so the category total still
		// reflects float-ledger writes from any code path we haven't migrated.
		var r = Sim.GetResourcesSnapshot();
		SetTextIfChanged(_foodCat .TotalLbl, (foodTotal  + System.Math.Max(0, (int)r.Food         - foodTotal )).ToString());
		SetTextIfChanged(_stoneCat.TotalLbl, (stoneTotal + System.Math.Max(0, (int)r.Stone        - stoneTotal)).ToString());
		SetTextIfChanged(_woodCat .TotalLbl, (woodTotal  + System.Math.Max(0, (int)r.Wood         - woodTotal )).ToString());
		SetTextIfChanged(_magicCat.TotalLbl, (magicTotal + System.Math.Max(0, (int)r.MagicEssence - magicTotal)).ToString());
	}

	// v0.4.23 — write-elide. `Label.Text =` always triggers a Godot text-layout
	// pass and a canvas redraw of the label, even when the new value is the
	// same as the old. With dozens of HUD labels per frame, the redundant
	// writes burned through the main-thread budget for no visual effect.
	private static void SetTextIfChanged(Label lbl, string newText)
	{
		if (lbl.Text != newText) lbl.Text = newText;
	}

	private static int ParseInt(string s) =>
		int.TryParse(s, out var v) ? v : 0;

	// v0.3.41 — collapsible resource category. Returns a ResourceCategory
	// wrapping the caret button, total label, and expansion VBox. The
	// expansion box is hidden by default; clicking the caret toggles it
	// and flips ▶/▼.
	private ResourceCategory AddCollapsibleResource(
		HBoxContainer parent,
		string icon,
		string name,
		ItemKind kind,
		string? materialFamily,
		(string Icon, string Name, string Key)[] subItems)
	{
		var cat = new ResourceCategory
		{
			Kind = kind,
			MaterialFamily = materialFamily,
		};

		// Column holding the header row plus its own expansion box.
		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 2);
		col.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		parent.AddChild(col);

		// Header: ▶ icon + name + total. Caret button doubles as the
		// click target; the whole header pressing it would be nicer but
		// keeping the caret as a dedicated Button is simpler and matches
		// the user's "click the caret" phrasing.
		var header = new HBoxContainer();
		header.AddThemeConstantOverride("separation", 4);
		col.AddChild(header);

		cat.CaretBtn = new Button
		{
			Text              = "▶",
			Flat              = true,
			FocusMode         = FocusModeEnum.None,
			CustomMinimumSize = new Vector2(UITheme.Scaled(16), UITheme.Scaled(16)),
		};
		cat.CaretBtn.AddThemeFontSizeOverride("font_size", UITheme.Scaled(10));
		cat.CaretBtn.AddThemeColorOverride("font_color",         Muted);
		cat.CaretBtn.AddThemeColorOverride("font_hover_color",   Gold);
		cat.CaretBtn.AddThemeColorOverride("font_pressed_color", Gold);
		cat.CaretBtn.TooltipText = $"Toggle {name} breakdown";
		header.AddChild(cat.CaretBtn);

		var titleLbl = Lbl($"{icon} {name}", UITheme.Scaled(11), Muted);
		header.AddChild(titleLbl);

		cat.TotalLbl = Lbl("—", UITheme.Scaled(12), Parchment);
		header.AddChild(cat.TotalLbl);

		// Expansion VBox — initially hidden.
		cat.ExpansionBox = new VBoxContainer { Visible = false };
		cat.ExpansionBox.AddThemeConstantOverride("separation", 1);
		col.AddChild(cat.ExpansionBox);

		// Indent sub-items so they line up under the name, not the caret.
		var indent = new MarginContainer();
		indent.AddThemeConstantOverride("margin_left", UITheme.Scaled(20));
		cat.ExpansionBox.AddChild(indent);

		var subCol = new VBoxContainer();
		subCol.AddThemeConstantOverride("separation", 1);
		indent.AddChild(subCol);

		foreach (var (subIcon, subName, subKey) in subItems)
		{
			var row = new HBoxContainer();
			row.AddThemeConstantOverride("separation", 4);
			row.AddChild(Lbl($"{subIcon} {subName}", UITheme.Scaled(10), Muted));
			row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
			var valueLbl = Lbl("0", UITheme.Scaled(10), Muted);
			row.AddChild(valueLbl);
			subCol.AddChild(row);
			cat.SubLabels[subKey] = valueLbl;
		}

		// Local capture so the lambda doesn't reference a re-used variable.
		var captured = cat;
		cat.CaretBtn.Pressed += () =>
		{
			captured.Expanded = !captured.Expanded;
			captured.ExpansionBox.Visible = captured.Expanded;
			captured.CaretBtn.Text = captured.Expanded ? "▼" : "▶";
		};

		return cat;
	}

	// v0.5.43 — speed / menu buttons now scale with UI Size. Pre-v0.5.43
	// these were hardcoded to a 32-px height + AnimatedButton's internal
	// 13-pt Compact font, neither of which scaled with the v0.5.29 UI Size
	// slider. Result: the speed/menu capsule stayed at 100 % size while
	// the rest of the HUD shrank, causing visual overlap on small UI Size.
	// Sam: "Speed/Menu panel does not currently scale with the rest of the
	// UI, causing overlap at small UI sizes."
	private static AnimatedButton MakeSmallBtn(string text)
	{
		var btn = new AnimatedButton
		{
			Text              = text,
			CustomMinimumSize = new Vector2(0, UITheme.Scaled(32)),
			PlayHoverSound    = false,
			Compact           = true,
		};
		// Override the AnimatedButton.Compact 13-pt default with a UI-scaled
		// font size. CallDeferred (snake_case for the engine) so the
		// override stomps AnimatedButton._Ready → ApplyStyle's internal
		// 13-pt assignment that would otherwise overwrite us.
		btn.CallDeferred("add_theme_font_size_override",
			"font_size", UITheme.Scaled(13));
		return btn;
	}

	private static Label Lbl(string text, int size, Color color)
	{
		var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
		l.AddThemeColorOverride("font_color", color);
		l.AddThemeFontSizeOverride("font_size", size);
		const string font = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(font))
			l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
		return l;
	}

	private static TextureRect ShroompIcon(int size)
	{
		const string path = "res://assets/icons/shroomp_icon.svg";
		var rect = new TextureRect
		{
			CustomMinimumSize = new Vector2(size, size),
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			StretchMode       = TextureRect.StretchModeEnum.KeepAspectCentered,
			ExpandMode        = TextureRect.ExpandModeEnum.IgnoreSize,
		};
		if (ResourceLoader.Exists(path))
			rect.Texture = GD.Load<Texture2D>(path);
		return rect;
	}

	private static VSeparator Divider()
	{
		var v = new VSeparator();
		v.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.15f, 0.7f));
		return v;
	}
}
