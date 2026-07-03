using Godot;
using System.Collections.Generic;
using System.Globalization;
using Sporeholm.Simulation.Items;
using Sporeholm.UI;

// Top-bar HUD: era name, S.D. date, population, mood summary, resource
// readout, speed controls. Renders as two floating capsules (stats +
// resources top-left, speed/menu top-right) using `FloatingPanelStyle`.
//
// v0.8.11 — resource readout rebuilt for counting parity + presentation:
//   • Every category total is computed as the SUM OF ITS BREAKDOWN ROWS,
//     so the header number always equals what the expanded rows show.
//     Pre-v0.8.11 the total folded in map-ground items via the
//     ColonyResources float getters while the rows only counted stored
//     inventory — Sam's screenshot: Wood 4806 over rows summing 52.
//   • v0.8.12 — counters count ONLY STORED goods: the colony store plus
//     items sitting on storage tiles (stockpile-zone cells + built
//     Shelves). Loose items scattered on the map are NOT counted until
//     hauled in; their per-row / per-category amounts surface in the
//     tooltips as "loose on the map" so the number never reads as a bug.
//   • Rows derive from ItemRegistry / MaterialRegistry instead of a
//     hard-coded list, so new foods / minerals surface automatically
//     (the old list was missing all six v0.5.15 minerals and every
//     Phase 8 food). A catch-all "Other" row guards the parity invariant
//     even for unregistered sub-types. Zero-count rows stay hidden.
//   • All colours come from UITheme (the local Gold/Parchment/Muted
//     duplicates drifted from the shared palette); hairlines use the
//     shared UITheme.Hairline; numbers are thousands-formatted.
//   • Both capsule rows are flow containers with a width budget so the
//     left capsule wraps instead of colliding with the Speed/Menu
//     capsule on narrow windows or large UI Size.
public partial class HUDController : Control
{
	[Signal] public delegate void MenuRequestedEventHandler();

	private const int BandSeparation = 12;
	private const int StatsSepX      = 10;
	private const int ResSepX        = 12;
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	private Label _eraLabel = null!, _dateLabel = null!, _popLabel = null!, _moodLabel = null!;

	// One breakdown row inside a category — a hidden-until-nonzero HBox
	// with a name label and a right-aligned count. Stored/Loose are
	// per-frame accumulators filled by _Process's aggregation walk:
	// Stored = colony store + items on storage tiles (the counted number);
	// Loose = items on the map outside storage (tooltip-only, not counted).
	private sealed class SubRow
	{
		public Control Root     = null!;
		public Label   ValueLbl = null!;
		public string  Name     = "";
		public int Stored, Loose;
		public int ShownStored = -1, ShownLoose = -1;   // tooltip write-elide
	}

	// v0.3.41 — per-category collapsible widgets for the resource row.
	// v0.8.11 — rows are registry-driven (see class comment) and the
	// category total is the literal sum of them.
	private sealed class ResourceCategory
	{
		public string Name  = "";
		public string Blurb = "";
		public Button        CaretBtn     = null!;
		public Label         TitleLbl     = null!;
		public Label         TotalLbl     = null!;
		public VBoxContainer ExpansionBox = null!;
		public Label         EmptyLbl     = null!;
		public bool          Expanded;
		// Lookup by aggregation key (item SubType, or Material.SubType for
		// the Stone/Wood material families) + ordered list for display.
		public Dictionary<string, SubRow> Rows    = new();
		public List<SubRow>               RowList = new();
		public SubRow                     OtherRow = null!;
		public ItemKind Kind = ItemKind.Food;
		public string?  MaterialFamily;          // non-null = bucket by material sub-type
		public int Stored, Loose;
		public int ShownStored = -1, ShownLoose = -1;   // tooltip write-elide
	}

	private ResourceCategory _foodCat  = null!;
	private ResourceCategory _stoneCat = null!;
	private ResourceCategory _woodCat  = null!;
	private ResourceCategory _magicCat = null!;
	private ResourceCategory[] _cats = System.Array.Empty<ResourceCategory>();

	// Reused across frames by CopyDroppedGroupTotals — no steady-state alloc.
	// _groundTallies = every dropped item; _storedGroundTallies = the subset
	// on storage tiles (stockpile cells + built Shelves).
	private readonly Dictionary<(ItemKind Kind, string Family, string MatSub, string ItemSub), int>
		_groundTallies = new();
	private readonly Dictionary<(ItemKind Kind, string Family, string MatSub, string ItemSub), int>
		_storedGroundTallies = new();

	private AnimatedButton _pauseBtn = null!;
	// v0.3.28 — kept so GameController can query "is the cursor over a HUD
	// capsule?" before applying mouse-wheel zoom.
	private PanelContainer _leftPanel  = null!;
	private PanelContainer _rightPanel = null!;
	private HFlowContainer _statsFlow  = null!;
	private HFlowContainer _resFlow    = null!;

	private readonly List<(AnimatedButton btn, float speed)> _speedBtns = new();
	private float _activeSpeed = 1f;
	private bool  _tips = true;

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
		// Re-budget the capsule width whenever the window (this full-rect
		// control) resizes, and once now that the first layout has settled.
		Resized += () => Callable.From(UpdateResponsiveLayout).CallDeferred();
		Callable.From(UpdateResponsiveLayout).CallDeferred();
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
		Callable.From(UpdateResponsiveLayout).CallDeferred();
		// On the next _Process the resource labels populate from the sim, and
		// SyncPauseButton runs on the next UpdateStats — no explicit refresh
		// needed.
	}

	// All HUD content construction. Originally inlined in `_Ready`; extracted
	// so OnUIScaleChanged can call it again after clearing children.
	private void BuildContent()
	{
		var cfg = new ConfigFile();
		_tips = cfg.Load("user://settings.cfg") != Error.Ok
			|| (bool)cfg.GetValue("gameplay", "show_tooltips", true);

		var band = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
		band.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
		band.OffsetLeft   = UITheme.EdgeInset;
		band.OffsetRight  = -UITheme.EdgeInset;
		band.OffsetTop    = UITheme.EdgeInset;
		band.AddThemeConstantOverride("separation", BandSeparation);
		AddChild(band);

		// ────────────────────────────────────────────────────────────────────
		// Left capsule — stats row over a hairline rule over the resource row.
		// Both rows are flow containers: UpdateResponsiveLayout gives them a
		// width budget so they wrap on narrow windows instead of pushing the
		// Speed/Menu capsule off-screen.
		// ────────────────────────────────────────────────────────────────────
		_leftPanel = new PanelContainer
		{
			MouseFilter         = MouseFilterEnum.Stop,
			// v0.3.44 — pin each capsule to the band's top edge so an
			// expanded left capsule (e.g. all resource categories open)
			// doesn't pull the right-hand Speed/Menu capsule down to match
			// its height.
			SizeFlagsVertical   = SizeFlags.ShrinkBegin,
		};
		_leftPanel.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
		band.AddChild(_leftPanel);

		var leftVbox = new VBoxContainer();
		leftVbox.AddThemeConstantOverride("separation", 5);
		_leftPanel.AddChild(leftVbox);

		// Row 1 — stats
		_statsFlow = new HFlowContainer { MouseFilter = MouseFilterEnum.Pass };
		_statsFlow.AddThemeConstantOverride("h_separation", StatsSepX);
		_statsFlow.AddThemeConstantOverride("v_separation", 4);
		leftVbox.AddChild(_statsFlow);

		_statsFlow.AddChild(Lbl("🌅", UITheme.Scaled(16), UITheme.TextAccent));
		_eraLabel = Lbl("Dawn Era", UITheme.Scaled(15), UITheme.TextPrimary);
		_statsFlow.AddChild(_eraLabel);

		_statsFlow.AddChild(Divider());

		_statsFlow.AddChild(Lbl("📅", UITheme.Scaled(14), UITheme.TextPrimary));
		_dateLabel = Lbl("Day 1, Spring, Year 0 S.D.", UITheme.Scaled(13), UITheme.TextPrimary);
		_statsFlow.AddChild(_dateLabel);

		_statsFlow.AddChild(Divider());

		_statsFlow.AddChild(ShroompIcon(UITheme.Scaled(18)));
		_popLabel = Lbl("Pop: 7", UITheme.Scaled(13), UITheme.TextPrimary);
		_statsFlow.AddChild(_popLabel);

		_statsFlow.AddChild(Divider());

		_moodLabel = Lbl("😊 0  😢 0", UITheme.Scaled(13), UITheme.TextPrimary);
		_statsFlow.AddChild(_moodLabel);

		// Hairline rule between the two rows — same treatment as the
		// vertical dividers so the capsule reads as one designed unit.
		var rule = new HSeparator();
		rule.AddThemeColorOverride("color", UITheme.Hairline);
		leftVbox.AddChild(rule);

		// Row 2 — resources. Each category is a fixed-min-width column:
		// caret + icon + name on the left, right-aligned total on a stable
		// edge. Expanding drops the per-sub-type breakdown beneath; rows
		// derive from the registries and only show when non-zero.
		_resFlow = new HFlowContainer { MouseFilter = MouseFilterEnum.Pass };
		_resFlow.AddThemeConstantOverride("h_separation", ResSepX);
		_resFlow.AddThemeConstantOverride("v_separation", 6);
		leftVbox.AddChild(_resFlow);

		_foodCat = AddCollapsibleResource(_resFlow, "🍓", "Food",
			"Edible stores — counted once stockpiled or shelved.",
			ItemKind.Food, materialFamily: null, RowsFromItemKind(ItemKind.Food));
		_resFlow.AddChild(Divider());
		_stoneCat = AddCollapsibleResource(_resFlow, "🪨", "Stone",
			"Stored stone and minerals, by type.",
			ItemKind.Material, materialFamily: "Stone", RowsFromMaterialFamily("Stone"));
		_resFlow.AddChild(Divider());
		_woodCat = AddCollapsibleResource(_resFlow, "🪵", "Wood",
			"Stored timber, by wood type.",
			ItemKind.Material, materialFamily: "Wood", RowsFromMaterialFamily("Wood"));
		_resFlow.AddChild(Divider());
		_magicCat = AddCollapsibleResource(_resFlow, "✨", "Magic",
			"Stored essence, shards, and magical preparations.",
			ItemKind.Magic, materialFamily: null, RowsFromItemKind(ItemKind.Magic));

		_cats = new[] { _foodCat, _stoneCat, _woodCat, _magicCat };

		// Flexible spacer — the left and right capsules size to their
		// content so they never crowd each other.
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

		var rightHbox = new HBoxContainer();
		rightHbox.AddThemeConstantOverride("separation", 6);
		_rightPanel.AddChild(rightHbox);

		rightHbox.AddChild(Lbl("Speed:", UITheme.Scaled(12), UITheme.TextAccent));

		// Pause button
		_pauseBtn = MakeSmallBtn("⏸");
		_pauseBtn.Pressed += OnPauseToggle;
		if (_tips) Tooltips.Apply(_pauseBtn, "Pause / Unpause simulation");
		rightHbox.AddChild(_pauseBtn);

		// Speed preset buttons
		// v0.4.19 — multiplier values match the displayed labels. The sim
		// tick interval is `BaseTickIntervalMs / SpeedMultiplier`, so
		// movement, animations, and clock progression all scale linearly
		// off this value.
		AddSpeedBtn(rightHbox, "1×",  1f,  _tips ? "Normal speed (1× — real-time)" : "");
		AddSpeedBtn(rightHbox, "2×",  2f,  _tips ? "Double speed (2×)"             : "");
		AddSpeedBtn(rightHbox, "5×",  5f,  _tips ? "Fast (5×)"                     : "");
		AddSpeedBtn(rightHbox, "10×", 10f, _tips ? "Maximum speed (10×)"           : "");

		SetActiveSpeed(1f);

		// Main Menu button
		rightHbox.AddChild(Divider());
		var menu = MakeSmallBtn("Menu");
		menu.Modulate = UITheme.TextAccent;
		menu.Pressed += () => EmitSignal(SignalName.MenuRequested);
		if (_tips) Tooltips.Apply(menu, "Open pause menu");
		rightHbox.AddChild(menu);

		// ── Stat label tooltips ────────────────────────────────────────────
		if (_tips)
		{
			_eraLabel.MouseFilter  = MouseFilterEnum.Pass;
			Tooltips.Apply(_eraLabel,  "Current historical era of the colony.\nEra advances as population and culture grow.");
			_dateLabel.MouseFilter = MouseFilterEnum.Pass;
			Tooltips.Apply(_dateLabel, "In-game date: Season, Day, Year S.D.\n120 days per year (4 seasons × 30 days).");
			_popLabel.MouseFilter  = MouseFilterEnum.Pass;
			Tooltips.Apply(_popLabel,  "Total living shroomps in the colony.");
			_moodLabel.MouseFilter = MouseFilterEnum.Pass;
			Tooltips.Apply(_moodLabel, "😊 Inspired shroomps (mood ≥ 80)\n😢 Distressed or worse (mood < 40).");
		}
	}

	// ── Registry-driven row seeds ──────────────────────────────────────────────
	// Single source of truth: whatever the registries define is what the HUD
	// can break down. Adding a new food / mineral / magic item automatically
	// gives it a row here (hidden until the colony owns one).

	private static IEnumerable<(string Icon, string Name, string Key)> RowsFromItemKind(ItemKind kind)
	{
		foreach (var def in ItemRegistry.InKind(kind))
			yield return (def.Icon, def.DisplayName, def.SubType);
	}

	private static IEnumerable<(string Icon, string Name, string Key)> RowsFromMaterialFamily(string family)
	{
		foreach (var def in MaterialRegistry.InFamily(family))
			yield return (def.Icon, def.DisplayName, def.Key.SubType);
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
		_pauseBtn.Modulate = paused ? UITheme.TextAccent : Colors.White;
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
		if (tooltip.Length > 0) Tooltips.Apply(btn, tooltip);
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
			btn.Modulate = active ? UITheme.TextAccent : Colors.White;
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

	// ── Resource aggregation ───────────────────────────────────────────────────

	// Each `_Process` tick, aggregate the colony's stores into the four
	// category widgets in a single walk each. COUNTING SCOPE (v0.8.12):
	// counted = colony store (Inventory) + items on storage tiles
	// (stockpile-zone cells + built Shelves). Loose ground items are
	// tracked separately for the tooltips but are NOT counted — they
	// join the number when a hauler brings them in (or a zone is painted
	// under them). PARITY INVARIANT: a category's header total is computed
	// as the sum of what lands in its rows (including the "Other"
	// catch-all), so the number always equals the expanded breakdown.
	// Cheap per frame: one inventory snapshot (pre-existing cost), one
	// small two-dictionary copy, and write-elided label updates.
	public override void _Process(double delta)
	{
		if (Sim == null || _cats.Length == 0) return;

		var inv = Sim.GetInventorySnapshot();
		Sim.CopyDroppedGroupTotals(_groundTallies, _storedGroundTallies);

		foreach (var cat in _cats)
		{
			cat.Stored = cat.Loose = 0;
			foreach (var row in cat.RowList) row.Stored = row.Loose = 0;
		}

		foreach (var row in inv)
			Accumulate(row.Kind, row.MaterialFamily, row.MaterialSubType, row.SubType,
				storedQty: row.Quantity, looseQty: 0);
		foreach (var kv in _groundTallies)
		{
			_storedGroundTallies.TryGetValue(kv.Key, out int storedOnGround);
			Accumulate(kv.Key.Kind, kv.Key.Family, kv.Key.MatSub, kv.Key.ItemSub,
				storedQty: storedOnGround, looseQty: kv.Value - storedOnGround);
		}

		bool rowsChanged = false;
		foreach (var cat in _cats) rowsChanged |= FlushCategory(cat);
		// Row visibility changes can alter the capsule's natural width —
		// re-budget so the flow wrap stays correct.
		if (rowsChanged) Callable.From(UpdateResponsiveLayout).CallDeferred();
	}

	private void Accumulate(ItemKind kind, string family, string matSub, string itemSub, int storedQty, int looseQty)
	{
		if (storedQty <= 0 && looseQty <= 0) return;
		ResourceCategory? cat = kind switch
		{
			ItemKind.Food  => _foodCat,
			ItemKind.Magic => _magicCat,
			ItemKind.Material when family == "Stone" => _stoneCat,
			ItemKind.Material when family == "Wood"  => _woodCat,
			_ => null,   // other kinds/families live in the Resources tab ledger
		};
		if (cat == null) return;

		string key = cat.MaterialFamily != null ? matSub : itemSub;
		if (key == null || !cat.Rows.TryGetValue(key, out var row))
			row = cat.OtherRow;   // unregistered sub-type — still counted, still displayed

		if (storedQty > 0) { row.Stored += storedQty; cat.Stored += storedQty; }
		if (looseQty  > 0) { row.Loose  += looseQty;  cat.Loose  += looseQty;  }
	}

	// Writes a category's accumulated counts into its labels + tooltips.
	// Only the STORED amount is displayed/counted; loose ground amounts
	// ride along in the tooltips so a big pile awaiting haul is visible
	// without inflating the number. Returns true when any row's
	// visibility flipped (layout re-budget cue).
	private bool FlushCategory(ResourceCategory cat)
	{
		SetTextIfChanged(cat.TotalLbl, cat.Stored.ToString("N0", Inv));

		bool rowsChanged = false;
		int visibleRows = 0;
		foreach (var row in cat.RowList)
		{
			bool show = row.Stored > 0;
			if (row.Root.Visible != show) { row.Root.Visible = show; rowsChanged = true; }
			if (!show) continue;
			visibleRows++;
			SetTextIfChanged(row.ValueLbl, row.Stored.ToString("N0", Inv));
			if (_tips && (row.Stored != row.ShownStored || row.Loose != row.ShownLoose))
			{
				row.ShownStored = row.Stored;
				row.ShownLoose  = row.Loose;
				string tip = $"{row.Name}: {row.Stored.ToString("N0", Inv)} in storage";
				if (row.Loose > 0)
					tip += $"\nLoose on the map (not counted): {row.Loose.ToString("N0", Inv)}";
				Tooltips.Apply(row.Root, tip);
			}
		}

		bool showEmpty = visibleRows == 0;
		if (cat.EmptyLbl.Visible != showEmpty) { cat.EmptyLbl.Visible = showEmpty; rowsChanged = true; }

		if (_tips && (cat.Stored != cat.ShownStored || cat.Loose != cat.ShownLoose))
		{
			cat.ShownStored = cat.Stored;
			cat.ShownLoose  = cat.Loose;
			string tip = $"{cat.Name}: {cat.Stored.ToString("N0", Inv)} in storage\n{cat.Blurb}";
			if (cat.Loose > 0)
				tip += $"\nLoose on the map (not counted): {cat.Loose.ToString("N0", Inv)} — haul it to a stockpile or shelf.";
			Tooltips.Apply(cat.TitleLbl, tip);
			Tooltips.Apply(cat.TotalLbl, tip);
		}
		return rowsChanged;
	}

	// v0.4.23 — write-elide. `Label.Text =` always triggers a Godot text-layout
	// pass and a canvas redraw of the label, even when the new value is the
	// same as the old. With dozens of HUD labels per frame, the redundant
	// writes burned through the main-thread budget for no visual effect.
	private static void SetTextIfChanged(Label lbl, string newText)
	{
		if (lbl.Text != newText) lbl.Text = newText;
	}

	// ── Responsive width budget ────────────────────────────────────────────────

	// Gives both left-capsule flow rows a width budget of "viewport minus
	// the Speed/Menu capsule and insets". At or under their natural
	// single-line width the capsule hugs its content (floating look); a
	// narrow window or large UI Size makes the rows wrap instead of the
	// two capsules colliding. Called on window resize, UI-scale rebuild,
	// caret toggles, and row-set changes.
	private void UpdateResponsiveLayout()
	{
		if (_statsFlow == null || _resFlow == null || _rightPanel == null) return;
		if (!IsInsideTree()) return;

		float rightW = _rightPanel.GetCombinedMinimumSize().X;
		float avail  = Size.X - UITheme.EdgeInset * 2f - BandSeparation - rightW
		             - UITheme.ContentPadX * 2f;
		float budget = Mathf.Max(UITheme.ScaledF(240), avail);
		ApplyFlowBudget(_statsFlow, budget, StatsSepX);
		ApplyFlowBudget(_resFlow,   budget, ResSepX);
	}

	private static void ApplyFlowBudget(HFlowContainer flow, float budget, int sepX)
	{
		// Natural single-line width = Σ visible children + separations.
		// Clamping the flow's minimum width to min(natural, budget) is what
		// makes it wrap: a flow container inside a shrink-sized capsule
		// otherwise collapses to its widest child and wraps everything.
		float natural = 0f;
		int visible = 0;
		foreach (var child in flow.GetChildren())
		{
			if (child is Control c && c.Visible)
			{
				natural += c.GetCombinedMinimumSize().X;
				visible++;
			}
		}
		if (visible > 1) natural += sepX * (visible - 1);
		flow.CustomMinimumSize = new Vector2(Mathf.Min(natural, budget), 0);
	}

	// ── Category widget construction ───────────────────────────────────────────

	// v0.3.41 — collapsible resource category. The expansion box is hidden by
	// default; clicking the caret toggles it and flips ▶/▼.
	private ResourceCategory AddCollapsibleResource(
		Container parent,
		string icon,
		string name,
		string blurb,
		ItemKind kind,
		string? materialFamily,
		IEnumerable<(string Icon, string Name, string Key)> subItems)
	{
		var cat = new ResourceCategory
		{
			Kind           = kind,
			MaterialFamily = materialFamily,
			Name           = name,
			Blurb          = blurb,
		};

		// Column holding the header row plus its own expansion box. The
		// min width keeps the four category columns on a consistent grid
		// and gives the right-aligned totals a stable edge.
		var col = new VBoxContainer();
		col.AddThemeConstantOverride("separation", 2);
		col.SizeFlagsVertical = SizeFlags.ShrinkBegin;
		col.CustomMinimumSize = new Vector2(UITheme.Scaled(126), 0);
		parent.AddChild(col);

		// Header: ▶ icon + name … total (right-aligned).
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
		cat.CaretBtn.AddThemeColorOverride("font_color",         UITheme.TextMuted);
		cat.CaretBtn.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
		cat.CaretBtn.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
		if (_tips) Tooltips.Apply(cat.CaretBtn, $"Show or hide the {name} breakdown.");
		header.AddChild(cat.CaretBtn);

		cat.TitleLbl = Lbl($"{icon} {name}", UITheme.Scaled(11), UITheme.TextMuted);
		cat.TitleLbl.MouseFilter = MouseFilterEnum.Pass;
		header.AddChild(cat.TitleLbl);

		cat.TotalLbl = Lbl("0", UITheme.Scaled(13), UITheme.TextPrimary);
		cat.TotalLbl.HorizontalAlignment   = HorizontalAlignment.Right;
		cat.TotalLbl.SizeFlagsHorizontal   = SizeFlags.ExpandFill;
		cat.TotalLbl.MouseFilter           = MouseFilterEnum.Pass;
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
			AddSubRow(cat, subCol, subIcon, subName, subKey);

		// Catch-all row: anything counted for this category that isn't a
		// registered sub-type still shows up, so the rows always sum to
		// the header total.
		cat.OtherRow = AddSubRow(cat, subCol, "•", "Other", key: null);

		cat.EmptyLbl = Lbl("(nothing stored yet)", UITheme.Scaled(10), UITheme.TextMuted);
		cat.EmptyLbl.Visible = false;
		subCol.AddChild(cat.EmptyLbl);

		// Local capture so the lambda doesn't reference a re-used variable.
		var captured = cat;
		cat.CaretBtn.Pressed += () =>
		{
			captured.Expanded = !captured.Expanded;
			captured.ExpansionBox.Visible = captured.Expanded;
			captured.CaretBtn.Text = captured.Expanded ? "▼" : "▶";
			Callable.From(UpdateResponsiveLayout).CallDeferred();
		};

		return cat;
	}

	private SubRow AddSubRow(ResourceCategory cat, VBoxContainer host, string icon, string name, string? key)
	{
		var rowBox = new HBoxContainer
		{
			Visible     = false,               // shown once the colony owns one
			MouseFilter = MouseFilterEnum.Pass, // row-level tooltip hover target
		};
		rowBox.AddThemeConstantOverride("separation", 4);
		rowBox.AddChild(Lbl($"{icon} {name}", UITheme.Scaled(10), UITheme.TextMuted));

		var valueLbl = Lbl("0", UITheme.Scaled(10), UITheme.TextPrimary);
		valueLbl.HorizontalAlignment = HorizontalAlignment.Right;
		valueLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		valueLbl.CustomMinimumSize   = new Vector2(UITheme.Scaled(34), 0);
		rowBox.AddChild(valueLbl);
		host.AddChild(rowBox);

		var row = new SubRow { Root = rowBox, ValueLbl = valueLbl, Name = name };
		cat.RowList.Add(row);
		if (key != null) cat.Rows[key] = row;
		return row;
	}

	// ── Shared widget helpers ──────────────────────────────────────────────────

	// v0.5.43 — speed / menu buttons scale with UI Size (see that entry for
	// the pre-history).
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
		v.AddThemeColorOverride("color", UITheme.Hairline);
		return v;
	}
}
