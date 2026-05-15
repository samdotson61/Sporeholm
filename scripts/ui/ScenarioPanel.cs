using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SmurfulationC;
using SmurfulationC.Simulation;
using SmurfulationC.Simulation.Items;

// Scenario configuration screen — RimWorld master/detail layout with a
// Dwarf-Fortress-"Prepare Carefully"-style colony-wide starting inventory.
//
// Layout uses FullRect anchoring with explicit margins so the footer (the
// primary "Begin Colony" CTA) stays pinned to the bottom of the screen
// regardless of how much content the master/detail section produces.
//
// Output is written to WorldState.PendingScenario; SimulationManager.SeedColony
// reads it on the next scene transition.
public partial class ScenarioPanel : Control
{
    [Signal] public delegate void BeginColonyConfirmedEventHandler();
    [Signal] public delegate void BackRequestedEventHandler();

    private static readonly Color ParchBg     = new(0.07f, 0.05f, 0.02f, 1.00f);
    private static readonly Color Gold         = new(0.95f, 0.80f, 0.28f);
    private static readonly Color Parchment    = new(0.96f, 0.91f, 0.72f);
    private static readonly Color Muted        = new(0.60f, 0.50f, 0.32f);
    private static readonly Color RowBg        = new(0.14f, 0.10f, 0.05f, 1.00f);
    private static readonly Color RowSelected  = new(0.32f, 0.22f, 0.08f, 1.00f);
    private static readonly Color RowBorder    = new(0.45f, 0.32f, 0.10f, 0.5f);

    private static readonly string[] Roles =
        { "Forager", "Crafter", "Guardian", "Caretaker", "Scholar", "Mage", "Elder", "Unassigned" };

    private readonly ScenarioConfig _config        = new();
    private readonly Random         _rng           = new();
    private int                     _selectedIndex = 0;

    // ── UI refs ────────────────────────────────────────────────────────────
    private LineEdit     _colonyNameEdit  = null!;
    private OptionButton _storytellerDrop = null!;
    private SpinBox      _countSpin       = null!;
    private VBoxContainer _listVBox       = null!;
    private VBoxContainer _detailContainer = null!;
    private Label        _inventorySummary = null!;

    private const int DefaultSmurfCount = 7;
    private const int MaxPersonalitySelections = 5;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new ColorRect { Color = ParchBg };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Root VBox anchored to FullRect with margins. This guarantees the
        // footer is always pinned to the bottom of the visible viewport —
        // the previous CenterContainer + sized VBox approach clipped the
        // footer when content height exceeded the inner VBox min size.
        var outer = new VBoxContainer();
        outer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        outer.OffsetLeft   = 40;
        outer.OffsetTop    = 32;
        outer.OffsetRight  = -40;
        outer.OffsetBottom = -32;
        outer.AddThemeConstantOverride("separation", 12);
        AddChild(outer);

        BuildHeader(outer);
        BuildTopSettings(outer);
        BuildMainSplit(outer);
        BuildInventoryStrip(outer);
        BuildFooter(outer);

        Visible = false;
    }

    // ── Public API ─────────────────────────────────────────────────────────
    public void Open()
    {
        _config.ColonyName       = DefaultColonyName();
        _config.Storyteller      = StorytellerType.Balanced;
        _config.StartingInventory = DefaultStartingInventory();
        _colonyNameEdit.Text      = _config.ColonyName;
        _storytellerDrop.Selected = 0;
        _countSpin.Value          = DefaultSmurfCount;
        RebuildSmurfTemplates(DefaultSmurfCount);
        _selectedIndex = 0;
        RebuildList();
        RebuildDetail();
        RefreshInventorySummary();
        Visible = true;
    }

    public void Close() => Visible = false;

    // ── Header + top settings ──────────────────────────────────────────────

    private void BuildHeader(VBoxContainer parent)
    {
        var title = new Label
        {
            Text                = "Configure Colony",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeColorOverride("font_color", Gold);
        title.AddThemeFontSizeOverride("font_size", 36);
        parent.AddChild(title);

        var sub = new Label
        {
            Text                = "Customise your founding colonists before they land.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeColorOverride("font_color", Muted);
        parent.AddChild(sub);

        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        parent.AddChild(sep);
    }

    private void BuildTopSettings(VBoxContainer parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        row.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        parent.AddChild(row);

        row.AddChild(MakeLabel("Colony Name"));
        _colonyNameEdit = new LineEdit
        {
            PlaceholderText   = "Colony Name",
            CustomMinimumSize = new Vector2(260, 0),
        };
        _colonyNameEdit.TextChanged += t => _config.ColonyName = t.Trim();
        row.AddChild(_colonyNameEdit);

        row.AddChild(MakeLabel("Storyteller"));
        _storytellerDrop = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        _storytellerDrop.AddItem("Balanced — steady pacing");
        _storytellerDrop.AddItem("Patient — slow build-up (coming soon)");
        _storytellerDrop.AddItem("Random — unpredictable (coming soon)");
        _storytellerDrop.AddItem("Cataclysmic — rare big spikes (coming soon)");
        _storytellerDrop.SetItemDisabled(1, true);
        _storytellerDrop.SetItemDisabled(2, true);
        _storytellerDrop.SetItemDisabled(3, true);
        _storytellerDrop.ItemSelected += idx => _config.Storyteller = (StorytellerType)idx;
        row.AddChild(_storytellerDrop);

        row.AddChild(MakeLabel("Smurf Count"));
        _countSpin = new SpinBox
        {
            MinValue          = ScenarioConfig.MinSmurfs,
            MaxValue          = ScenarioConfig.MaxSmurfs,
            Step              = 1,
            Value             = DefaultSmurfCount,
            CustomMinimumSize = new Vector2(90, 0),
        };
        _countSpin.ValueChanged += v =>
        {
            RebuildSmurfTemplates((int)v);
            if (_selectedIndex >= _config.Smurfs.Count)
                _selectedIndex = _config.Smurfs.Count - 1;
            RebuildList();
            RebuildDetail();
        };
        row.AddChild(_countSpin);
    }

    // ── Main split — left list, right detail card ──────────────────────────

    private void BuildMainSplit(VBoxContainer parent)
    {
        var split = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
        };
        split.AddThemeConstantOverride("separation", 16);
        parent.AddChild(split);

        // ── Left column: list of smurfs ────────────────────────────────────
        var leftCol = new VBoxContainer
        {
            CustomMinimumSize  = new Vector2(320, 0),
            SizeFlagsVertical  = SizeFlags.ExpandFill,
        };
        leftCol.AddThemeConstantOverride("separation", 6);
        split.AddChild(leftCol);

        var listHeader = new Label { Text = "Founding Smurfs" };
        listHeader.AddThemeColorOverride("font_color", Parchment);
        listHeader.AddThemeFontSizeOverride("font_size", 18);
        leftCol.AddChild(listHeader);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal   = SizeFlags.ExpandFill,
            SizeFlagsVertical     = SizeFlags.ExpandFill,
            HorizontalScrollMode  = ScrollContainer.ScrollMode.Disabled,
        };
        leftCol.AddChild(scroll);

        _listVBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _listVBox.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_listVBox);

        // ── Right column: detail card for selected smurf ───────────────────
        var rightCol = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
        };
        rightCol.AddThemeStyleboxOverride("panel", MakePanelStyle(RowBg));
        split.AddChild(rightCol);

        _detailContainer = new VBoxContainer();
        _detailContainer.AddThemeConstantOverride("separation", 14);
        _detailContainer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _detailContainer.SizeFlagsVertical   = SizeFlags.ExpandFill;
        rightCol.AddChild(_detailContainer);
    }

    // ── Colony-wide inventory strip (DF Prepare Carefully style) ───────────

    private void BuildInventoryStrip(VBoxContainer parent)
    {
        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        parent.AddChild(sep);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        var label = new Label { Text = "Colony Starting Inventory" };
        label.AddThemeColorOverride("font_color", Parchment);
        label.AddThemeFontSizeOverride("font_size", 18);
        row.AddChild(label);

        _inventorySummary = new Label
        {
            Text                = "—",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _inventorySummary.AddThemeColorOverride("font_color", Muted);
        row.AddChild(_inventorySummary);

        var editBtn = new AnimatedButton
        {
            Text              = "✎ Edit Inventory…",
            Compact           = true,
            CustomMinimumSize = new Vector2(200, 38),
            TooltipText       = "Configure the colony's shared starting supplies (DF Prepare Carefully style)",
        };
        editBtn.Pressed += OpenInventoryModal;
        row.AddChild(editBtn);
    }

    // ── Footer ─────────────────────────────────────────────────────────────

    private void BuildFooter(VBoxContainer parent)
    {
        var sep = new HSeparator();
        sep.AddThemeColorOverride("color", new Color(0.55f, 0.40f, 0.12f, 0.5f));
        parent.AddChild(sep);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        var back = new AnimatedButton
        {
            Text              = "← Back",
            Compact           = true,
            CustomMinimumSize = new Vector2(140, 44),
        };
        back.Pressed += () => EmitSignal(SignalName.BackRequested);
        row.AddChild(back);

        var randomAll = new AnimatedButton
        {
            Text              = "🎲 Randomize All",
            Compact           = true,
            CustomMinimumSize = new Vector2(180, 44),
        };
        randomAll.Pressed += () =>
        {
            foreach (var t in _config.Smurfs) RandomizeOne(t);
            // Randomize All is an automatic-generation path, so the female
            // floor applies (per-smurf 🎲 dice don't enforce — those are
            // explicit single-smurf rolls the player asked for).
            EnsureAtLeastOneFemaleAmongIndices(0, _config.Smurfs.Count);
            RebuildList();
            RebuildDetail();
        };
        row.AddChild(randomAll);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);

        var begin = new AnimatedButton
        {
            Text              = "✦ Begin Colony",
            CustomMinimumSize = new Vector2(260, 52),
            TooltipText       = "Load into the level and start the game with the chosen settings",
        };
        begin.Pressed += OnBeginPressed;
        row.AddChild(begin);
    }

    // ── Smurf list (left column) ───────────────────────────────────────────

    private void RebuildList()
    {
        foreach (Node child in _listVBox.GetChildren()) child.QueueFree();

        for (int i = 0; i < _config.Smurfs.Count; i++)
        {
            int idx = i;
            var t   = _config.Smurfs[i];
            bool selected = i == _selectedIndex;

            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            panel.AddThemeStyleboxOverride("panel",
                MakeRowStyle(selected ? RowSelected : RowBg, selected));
            _listVBox.AddChild(panel);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            panel.AddChild(row);

            var num = new Label
            {
                Text                = $"#{i + 1}",
                CustomMinimumSize   = new Vector2(34, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            num.AddThemeColorOverride("font_color", Muted);
            row.AddChild(num);

            var sex = new Label
            {
                Text                = t.Sex == Sex.Female ? "♀" : "♂",
                CustomMinimumSize   = new Vector2(18, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            sex.AddThemeColorOverride("font_color",
                t.Sex == Sex.Female ? new Color(0.95f, 0.55f, 0.78f) : new Color(0.50f, 0.78f, 1.0f));
            sex.AddThemeFontSizeOverride("font_size", 18);
            row.AddChild(sex);

            var name = new Label
            {
                Text                = string.IsNullOrEmpty(t.Name) ? "(unnamed)" : t.Name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            name.AddThemeColorOverride("font_color", selected ? Gold : Parchment);
            name.AddThemeFontSizeOverride("font_size", 16);
            row.AddChild(name);

            var role = new Label
            {
                Text                = t.Role,
                CustomMinimumSize   = new Vector2(86, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            role.AddThemeColorOverride("font_color", Muted);
            role.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(role);

            panel.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    _selectedIndex = idx;
                    RebuildList();
                    RebuildDetail();
                }
            };
            panel.MouseDefaultCursorShape = CursorShape.PointingHand;
        }
    }

    // ── Detail card (right column) ─────────────────────────────────────────

    private void RebuildDetail()
    {
        foreach (Node child in _detailContainer.GetChildren()) child.QueueFree();

        if (_selectedIndex < 0 || _selectedIndex >= _config.Smurfs.Count) return;
        int idx = _selectedIndex;
        var t   = _config.Smurfs[idx];

        // Top row: large name field + per-smurf randomize.
        var topRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topRow.AddThemeConstantOverride("separation", 12);
        _detailContainer.AddChild(topRow);

        var label = new Label { Text = $"#{idx + 1}" };
        label.AddThemeColorOverride("font_color", Muted);
        label.AddThemeFontSizeOverride("font_size", 28);
        topRow.AddChild(label);

        var nameEdit = new LineEdit
        {
            Text                = t.Name,
            PlaceholderText     = "Smurf Name",
            CustomMinimumSize   = new Vector2(0, 44),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        nameEdit.AddThemeFontSizeOverride("font_size", 22);
        nameEdit.TextChanged += s =>
        {
            _config.Smurfs[idx].Name = s.Trim();
            UpdateListRowName(idx);
        };
        topRow.AddChild(nameEdit);

        var rand = new AnimatedButton
        {
            Text              = "🎲",
            Compact           = true,
            CustomMinimumSize = new Vector2(56, 44),
            TooltipText       = "Randomize this smurf",
        };
        rand.Pressed += () =>
        {
            RandomizeOne(_config.Smurfs[idx]);
            RebuildList();
            RebuildDetail();
        };
        topRow.AddChild(rand);

        // Properties row: sex toggle | role | age
        var propsRow = new HBoxContainer();
        propsRow.AddThemeConstantOverride("separation", 24);
        _detailContainer.AddChild(propsRow);

        propsRow.AddChild(MakeLabel("Sex"));
        var sexBtn = new Button
        {
            Text              = t.Sex == Sex.Female ? "♀ Female" : "♂ Male",
            CustomMinimumSize = new Vector2(120, 32),
        };
        sexBtn.AddThemeColorOverride("font_color",
            t.Sex == Sex.Female ? new Color(0.95f, 0.55f, 0.78f) : new Color(0.50f, 0.78f, 1.0f));
        sexBtn.Pressed += () =>
        {
            var s = _config.Smurfs[idx];
            s.Sex = s.Sex == Sex.Male ? Sex.Female : Sex.Male;
            var used = _config.Smurfs.Where(x => x != s).Select(x => x.Name);
            s.Name = SmurfNameGenerator.Generate(used, _rng, s.Sex);
            RebuildList();
            RebuildDetail();
        };
        propsRow.AddChild(sexBtn);

        propsRow.AddChild(MakeLabel("Role"));
        var roleDrop = new OptionButton { CustomMinimumSize = new Vector2(150, 32) };
        for (int r = 0; r < Roles.Length; r++)
        {
            roleDrop.AddItem(Roles[r]);
            if (Roles[r] == t.Role) roleDrop.Selected = r;
        }
        roleDrop.ItemSelected += sel =>
        {
            _config.Smurfs[idx].Role = Roles[(int)sel];
            UpdateListRowRole(idx);
        };
        propsRow.AddChild(roleDrop);

        propsRow.AddChild(MakeLabel("Age"));
        var ageSpin = new SpinBox
        {
            MinValue          = 18,
            MaxValue          = 540,
            Step              = 1,
            Value             = t.Age,
            CustomMinimumSize = new Vector2(100, 32),
            TooltipText       = "Age in years (18..540)",
        };
        ageSpin.ValueChanged += v => _config.Smurfs[idx].Age = (int)v;
        propsRow.AddChild(ageSpin);

        // v0.4.9 — Personality + Starting items laid out side-by-side
        // inside the detail card. Previously stacked vertically, which
        // pushed the starting-items section past the visible area on
        // smaller screens and left the right half of the detail card
        // empty. Two-column HBox keeps both panes visible without
        // scrolling.
        var twoCol = new HBoxContainer();
        twoCol.AddThemeConstantOverride("separation", 16);
        twoCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        twoCol.SizeFlagsVertical   = SizeFlags.ExpandFill;
        _detailContainer.AddChild(twoCol);

        var leftCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
        };
        leftCol.AddThemeConstantOverride("separation", 4);
        twoCol.AddChild(leftCol);

        var rightCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
        };
        rightCol.AddThemeConstantOverride("separation", 4);
        twoCol.AddChild(rightCol);

        // ── Left column: personality picker ──────────────────────────────
        var pHeader = new Label { Text = $"Personality (pick up to {MaxPersonalitySelections})" };
        pHeader.AddThemeColorOverride("font_color", Parchment);
        pHeader.AddThemeFontSizeOverride("font_size", 16);
        leftCol.AddChild(pHeader);

        var pScroll = new ScrollContainer
        {
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        leftCol.AddChild(pScroll);

        // v0.4.9 — two-column grid now that the picker occupies half
        // the detail card. The trait pool (25 entries) fits cleanly
        // in two columns × ~13 rows; the ScrollContainer handles any
        // overflow on smaller window sizes.
        var pGrid = new GridContainer
        {
            Columns             = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        pGrid.AddThemeConstantOverride("h_separation", 12);
        pGrid.AddThemeConstantOverride("v_separation", 4);
        pScroll.AddChild(pGrid);

        // ── Right column: starting items summary + reroll button ─────────
        AddStartingItemsSection(idx, t, rightCol);

        var current = new HashSet<string>(t.Personality);
        foreach (var def in PersonalityRegistry.All)
        {
            string traitName = def.Name;
            var chk = new CheckBox
            {
                Text          = $"{traitName}  ({(def.MoodModifier >= 0 ? "+" : "")}{def.MoodModifier:0})",
                ButtonPressed = current.Contains(traitName),
                TooltipText   = PersonalityRegistry.BuildGameplayTooltip(def),   // v0.5.1
            };
            chk.AddThemeColorOverride(
                "font_color",
                def.MoodModifier > 0 ? new Color(0.65f, 0.95f, 0.50f)
              : def.MoodModifier < 0 ? new Color(0.95f, 0.65f, 0.50f)
              :                        Parchment);
            chk.Toggled += pressed =>
            {
                var smurf = _config.Smurfs[idx];
                if (pressed)
                {
                    if (smurf.Personality.Count >= MaxPersonalitySelections)
                    {
                        chk.SetPressedNoSignal(false);
                        return;
                    }
                    if (!smurf.Personality.Contains(traitName))
                        smurf.Personality.Add(traitName);
                }
                else
                {
                    smurf.Personality.Remove(traitName);
                }
            };
            pGrid.AddChild(chk);
        }
    }

    private void UpdateListRowName(int idx)
    {
        if (idx < 0 || idx >= _listVBox.GetChildCount()) return;
        var panel = _listVBox.GetChild(idx);
        if (panel == null || panel.GetChildCount() == 0) return;
        var row = panel.GetChild(0);
        if (row.GetChildCount() < 3) return;
        if (row.GetChild(2) is Label nameLbl)
            nameLbl.Text = string.IsNullOrEmpty(_config.Smurfs[idx].Name)
                ? "(unnamed)" : _config.Smurfs[idx].Name;
    }

    private void UpdateListRowRole(int idx)
    {
        if (idx < 0 || idx >= _listVBox.GetChildCount()) return;
        var panel = _listVBox.GetChild(idx);
        if (panel == null || panel.GetChildCount() == 0) return;
        var row = panel.GetChild(0);
        if (row.GetChildCount() < 4) return;
        if (row.GetChild(3) is Label roleLbl)
            roleLbl.Text = _config.Smurfs[idx].Role;
    }

    // ── Colony inventory modal — DF Prepare-Carefully style ───────────────

    private void OpenInventoryModal()
    {
        var w = new Window
        {
            Title       = $"Colony Starting Inventory — {_config.ColonyName}",
            Size        = new Vector2I(960, 620),
            MinSize     = new Vector2I(720, 480),
            Exclusive   = true,
            Transient   = true,
            Unresizable = false,
            Borderless  = false,
        };
        AddChild(w);

        var bg = new ColorRect { Color = new Color(0.05f, 0.04f, 0.02f, 1f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        w.AddChild(bg);

        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        vbox.OffsetLeft = vbox.OffsetTop = 14;
        vbox.OffsetRight = vbox.OffsetBottom = -14;
        vbox.AddThemeConstantOverride("separation", 10);
        w.AddChild(vbox);

        var header = new Label { Text = "Colony Supplies" };
        header.AddThemeColorOverride("font_color", Gold);
        header.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(header);

        var notice = new Label
        {
            Text = "Allocate the supplies your colony brings on the expedition. Quantities are colony-wide " +
                   "and distributed across smurfs by role / need at game start — same model as Dwarf " +
                   "Fortress's Prepare Carefully screen. Phase 4 will resolve each token into a real Item " +
                   "with material / quality / decay properties.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        notice.AddThemeColorOverride("font_color", Muted);
        vbox.AddChild(notice);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        vbox.AddChild(body);

        // Categories (left)
        var catCol = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
        catCol.AddThemeConstantOverride("separation", 6);
        body.AddChild(catCol);

        var itemColWrap = new ScrollContainer
        {
            SizeFlagsHorizontal  = SizeFlags.ExpandFill,
            SizeFlagsVertical    = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        body.AddChild(itemColWrap);

        var itemList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        itemList.AddThemeConstantOverride("separation", 4);
        itemColWrap.AddChild(itemList);

        // Category toggles. Pressing one rebuilds the right pane.
        Button[] catButtons = new Button[ItemCatalog.Categories.Length];
        for (int c = 0; c < ItemCatalog.Categories.Length; c++)
        {
            int ci = c;
            var b = new Button
            {
                Text                = ItemCatalog.Categories[c].Name,
                ToggleMode          = true,
                ButtonPressed       = c == 0,
                CustomMinimumSize   = new Vector2(0, 36),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            b.Pressed += () =>
            {
                foreach (var ob in catButtons) ob.ButtonPressed = ob == b;
                RebuildItemList(itemList, ItemCatalog.Categories[ci]);
            };
            catButtons[c] = b;
            catCol.AddChild(b);
        }
        RebuildItemList(itemList, ItemCatalog.Categories[0]);

        // Footer with running summary + Close.
        var footer = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        footer.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(footer);

        var totalLbl = new Label
        {
            Text                = ComputeInventoryFooter(),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        totalLbl.AddThemeColorOverride("font_color", Parchment);
        footer.AddChild(totalLbl);

        var close = new AnimatedButton
        {
            Text              = "Close",
            Compact           = true,
            CustomMinimumSize = new Vector2(140, 40),
        };
        void OnClose()
        {
            w.QueueFree();
            RefreshInventorySummary();
        }
        close.Pressed += OnClose;
        w.CloseRequested += OnClose;
        footer.AddChild(close);

        w.PopupCentered();
    }

    private void RebuildItemList(VBoxContainer host, ItemCatalog.Category cat)
    {
        foreach (Node child in host.GetChildren()) child.QueueFree();

        var head = new Label { Text = cat.Description };
        head.AddThemeColorOverride("font_color", Parchment);
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        host.AddChild(head);

        foreach (var entry in cat.Items)
        {
            string token = entry.Token;
            var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            line.AddThemeConstantOverride("separation", 8);
            host.AddChild(line);

            // Name + description
            var info = new Label
            {
                Text                = $"{entry.Name}  —  {entry.Description}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            };
            info.AddThemeColorOverride("font_color", Parchment);
            line.AddChild(info);

            // Per-line quantity spin — colony-wide quantity for this item.
            int existing = FindInventoryQty(token);
            var qty = new SpinBox
            {
                MinValue          = 0,
                MaxValue          = entry.MaxStack,
                Step              = 1,
                Value             = existing,
                CustomMinimumSize = new Vector2(110, 32),
                TooltipText       = "Quantity of this item the colony brings. 0 = none.",
            };
            qty.ValueChanged += v =>
            {
                SetInventoryQty(token, (int)v);
            };
            line.AddChild(qty);
        }
    }

    private int FindInventoryQty(string token)
    {
        foreach (var e in _config.StartingInventory)
            if (e.Token == token) return e.Quantity;
        return 0;
    }

    private void SetInventoryQty(string token, int qty)
    {
        var existing = _config.StartingInventory.FirstOrDefault(e => e.Token == token);
        if (qty <= 0)
        {
            if (existing != null) _config.StartingInventory.Remove(existing);
        }
        else if (existing == null)
        {
            _config.StartingInventory.Add(new InventoryEntry { Token = token, Quantity = qty });
        }
        else
        {
            existing.Quantity = qty;
        }
    }

    // ── Default inventory — colony-appropriate starter pack ────────────────
    // Mirrors a sensible Prepare-Carefully default: a week of rations per
    // smurf, basic role tools, a small bit of weather protection.
    private static List<InventoryEntry> DefaultStartingInventory() => new()
    {
        new() { Token = "food.rations.7d",     Quantity = 7  },
        new() { Token = "food.mushroom.crate", Quantity = 2  },
        new() { Token = "tool.pick",           Quantity = 1  },
        new() { Token = "tool.basket",         Quantity = 2  },
        new() { Token = "tool.kit.heal",       Quantity = 1  },
        new() { Token = "apparel.cloak.wool",  Quantity = 3  },
        new() { Token = "misc.firekit",        Quantity = 1  },
        new() { Token = "misc.rope.50",        Quantity = 2  },
    };

    private void RefreshInventorySummary()
    {
        if (_inventorySummary == null) return;
        if (_config.StartingInventory == null || _config.StartingInventory.Count == 0)
        {
            _inventorySummary.Text = "no supplies — colony lands empty-handed";
            return;
        }
        int kinds  = _config.StartingInventory.Count;
        int total  = _config.StartingInventory.Sum(e => e.Quantity);
        _inventorySummary.Text = $"{kinds} item kind{(kinds == 1 ? "" : "s")} · {total} units total";
    }

    private string ComputeInventoryFooter()
    {
        int kinds  = _config.StartingInventory.Count;
        int total  = _config.StartingInventory.Sum(e => e.Quantity);
        return $"{kinds} kinds · {total} total units";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Label MakeLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", Parchment);
        return l;
    }

    private static StyleBoxFlat MakePanelStyle(Color bg)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(8);
        s.SetBorderWidthAll(1);
        s.BorderColor = RowBorder;
        s.ContentMarginLeft = s.ContentMarginRight = 16;
        s.ContentMarginTop = s.ContentMarginBottom = 14;
        return s;
    }

    private static StyleBoxFlat MakeRowStyle(Color bg, bool selected)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(6);
        s.SetBorderWidthAll(selected ? 2 : 1);
        s.BorderColor = selected ? Gold : RowBorder;
        s.ContentMarginLeft = s.ContentMarginRight = 10;
        s.ContentMarginTop = s.ContentMarginBottom = 8;
        return s;
    }

    private static string DefaultColonyName() => $"Colony {DateTime.Now:MM-dd-yy}";

    private void RebuildSmurfTemplates(int count)
    {
        while (_config.Smurfs.Count > count)
            _config.Smurfs.RemoveAt(_config.Smurfs.Count - 1);
        int growStart = _config.Smurfs.Count;
        while (_config.Smurfs.Count < count)
            _config.Smurfs.Add(MakeRandomTemplate());
        // If the count grew and the roster contains zero females, force one of
        // the NEWLY-ADDED templates to female (not an existing slot — the
        // player may have already explicitly set those to male). Shrinks
        // don't enforce because the player picked the smaller count.
        if (_config.Smurfs.Count > growStart)
            EnsureAtLeastOneFemaleAmongIndices(growStart, _config.Smurfs.Count);
    }

    // Every colony should start with at least one female unless the player
    // has explicitly removed them (via the per-smurf sex toggle or per-smurf
    // randomize). This helper enforces that floor on automatic-generation
    // paths only: initial Open(), Randomize All, and the count-grow branch
    // of RebuildSmurfTemplates. Per-smurf actions intentionally don't call
    // it — those are explicit player edits.
    //
    // `startIdx`..`endIdx` defines the range of indices eligible to be
    // flipped. Callers should pass the range of slots they generated so the
    // flip can't overwrite a player's prior explicit sex toggle on an older
    // slot.
    private void EnsureAtLeastOneFemaleAmongIndices(int startIdx, int endIdx)
    {
        if (startIdx >= endIdx) return;
        // Already a female anywhere in the roster? Roster meets the floor.
        if (_config.Smurfs.Any(t => t.Sex == Sex.Female)) return;
        // Pick a random template in the eligible range and force female.
        // Random pick (rather than always #0) avoids the founding female
        // always landing on Papa-equivalent.
        int pick = startIdx + _rng.Next(endIdx - startIdx);
        var t    = _config.Smurfs[pick];
        t.Sex    = Sex.Female;
        var used = _config.Smurfs.Where(x => x != t).Select(x => x.Name);
        t.Name   = SmurfNameGenerator.Generate(used, _rng, Sex.Female);
    }

    // Canonical Smurf gender ratio: 1 female per 49 males (~2 %). Matches the
    // Smurfs Fandom-canon population structure used by `BirthSystem.TryBirth`.
    // A 7-smurf scenario will roll a female only ~13 % of the time at random;
    // most playthroughs start male-only and rely on Smurfette-style imports or
    // the Phase 4 wandering-in event to introduce their first mother.
    private const double FemaleSpawnChance = 1.0 / 49.0;

    // v0.3.47 (Phase 4 sub-B) — adds the "Starting items" section to the
    // detail panel. Lists each item in the rolled kit with quality + count;
    // 🎲 button re-rolls.
    // v0.4.9 — takes an explicit parent so the two-column layout in
    // BuildDetail can drop the section into the right-hand column.
    // Defaults to `_detailContainer` for any caller that hasn't been
    // updated.
    private void AddStartingItemsSection(int idx, SmurfTemplate t, Container? parent = null)
    {
        var host = parent ?? (Container)_detailContainer;
        var section = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical   = SizeFlags.ExpandFill,
        };
        section.AddThemeConstantOverride("separation", 4);
        host.AddChild(section);

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 8);
        section.AddChild(headerRow);

        var header = new Label
        {
            Text = "Starting items",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        header.AddThemeColorOverride("font_color", Gold);
        header.AddThemeFontSizeOverride("font_size", 16);
        headerRow.AddChild(header);

        var reroll = new AnimatedButton
        {
            Text              = "🎲 Reroll",
            Compact           = true,
            CustomMinimumSize = new Vector2(108, 30),
            TooltipText       = "Re-roll this smurf's starting item kit",
        };
        headerRow.AddChild(reroll);

        var itemsBox = new VBoxContainer();
        itemsBox.AddThemeConstantOverride("separation", 1);
        section.AddChild(itemsBox);

        void Refresh()
        {
            foreach (Node c in itemsBox.GetChildren()) c.QueueFree();
            if (t.StartingItems == null || t.StartingItems.Count == 0)
            {
                itemsBox.AddChild(MakeMutedLabel("(no items yet)"));
                return;
            }
            foreach (var it in t.StartingItems)
            {
                var defSub = ItemRegistry.Get(it.Kind, it.SubType);
                var matDef = MaterialRegistry.Get(it.Material);
                string display = defSub?.DisplayName ?? it.SubType;
                string mat     = matDef?.DisplayName ?? it.Material.SubType;
                string qty     = it.Quantity > 1 ? $" ×{it.Quantity}" : "";
                string line    = $"  {ItemKindMeta.Icon(it.Kind)}  {it.Quality} {mat} {display}{qty}";
                itemsBox.AddChild(MakeMutedLabel(line));
            }
        }
        Refresh();

        reroll.Pressed += () =>
        {
            t.StartingItems = ItemFactory.RollStartingKit(t.Role, _rng, 0);
            Refresh();
        };
    }

    private Label MakeMutedLabel(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", Parchment);
        l.AddThemeFontSizeOverride("font_size", 13);
        return l;
    }

    private SmurfTemplate MakeRandomTemplate()
    {
        var used = _config.Smurfs.Select(t => t.Name);
        var sex  = _rng.NextDouble() < FemaleSpawnChance ? Sex.Female : Sex.Male;
        var t = new SmurfTemplate
        {
            Name        = SmurfNameGenerator.Generate(used, _rng, sex),
            Sex         = sex,
            Role        = Roles[_rng.Next(Roles.Length - 1)],
            Age         = 20 + _rng.Next(380),
            Personality = PersonalityRegistry.Assign(_rng, 20 + _rng.Next(380)),
        };
        // v0.3.43 — roll DF-style preferences alongside personality so the
        // scenario UI can surface "Loves Smurfberries" etc. before begin.
        t.Preferences = PreferenceRegistry.Assign(_rng, t.Personality);
        // v0.3.47 (Phase 4 sub-B) — pre-roll starting items so the player
        // sees them on the detail card before clicking Begin.
        t.StartingItems = ItemFactory.RollStartingKit(t.Role, _rng, 0);
        return t;
    }

    private void RandomizeOne(SmurfTemplate t)
    {
        var used = _config.Smurfs.Where(x => x != t).Select(x => x.Name);
        t.Sex         = _rng.NextDouble() < FemaleSpawnChance ? Sex.Female : Sex.Male;
        t.Name        = SmurfNameGenerator.Generate(used, _rng, t.Sex);
        t.Role        = Roles[_rng.Next(Roles.Length - 1)];
        t.Age         = 20 + _rng.Next(380);
        t.Personality = PersonalityRegistry.Assign(_rng, t.Age);
        // v0.3.43 — preferences re-roll alongside personality.
        t.Preferences = PreferenceRegistry.Assign(_rng, t.Personality);
        // v0.3.47 — starting items re-roll alongside role.
        t.StartingItems = ItemFactory.RollStartingKit(t.Role, _rng, 0);
    }

    private void OnBeginPressed()
    {
        var used = new HashSet<string>();
        foreach (var t in _config.Smurfs)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                t.Name = SmurfNameGenerator.Generate(used, _rng, t.Sex);
            used.Add(t.Name);
        }
        if (string.IsNullOrWhiteSpace(_config.ColonyName))
            _config.ColonyName = DefaultColonyName();

        if (WorldState.Instance != null)
        {
            WorldState.Instance.PendingScenario = _config;
            WorldState.Instance.ColonyName      = _config.ColonyName;
        }
        EmitSignal(SignalName.BeginColonyConfirmed);
    }
}

// ──────────────────────────────────────────────────────────────────────────
// Item catalog stub. Phase 4 will replace this with the real `Item` /
// `ItemRegistry` system. Each entry has a stable Token (preserved on
// `ScenarioConfig.StartingInventory`), a display Name, a Description, and a
// MaxStack that caps how many the colony can bring of that kind.
// ──────────────────────────────────────────────────────────────────────────
internal static class ItemCatalog
{
    public sealed record Entry(string Token, string Name, string Description, int MaxStack = 99);

    public sealed class Category
    {
        public string  Name        { get; init; } = "";
        public string  Description { get; init; } = "";
        public Entry[] Items       { get; init; } = Array.Empty<Entry>();
    }

    public static readonly Category[] Categories =
    {
        new()
        {
            Name        = "Food",
            Description = "Starting rations. Phase 4 will convert these into typed Item instances with spoilage timers and mood modifiers.",
            Items = new Entry[]
            {
                new("food.rations.3d",   "3-day rations",   "Three days of basic food for one smurf.", 25),
                new("food.rations.7d",   "7-day rations",   "A week of food. Standard expedition load.", 25),
                new("food.rations.14d",  "14-day rations",  "Two weeks of food. Heavy but secure.", 12),
                new("food.berry.basket", "Berry basket",    "Smurfberries — sweet, perishes in ~6 days.", 30),
                new("food.mushroom.crate","Mushroom crate", "Mixed mushrooms — fungal protein.", 20),
                new("food.herb.bundle",  "Herb bundle",     "Magical herbs — slight resonance boost when eaten.", 20),
            },
        },
        new()
        {
            Name        = "Tools",
            Description = "Role-specific equipment. Quality tier resolves in Phase 4 via the Crafter skill curve.",
            Items = new Entry[]
            {
                new("tool.pick",     "Stone pick",     "Crafter excavation speed × 1.5.", 10),
                new("tool.basket",   "Forager basket", "Forager yield × 1.2 per gather action.", 10),
                new("tool.focus",    "Mage focus",     "Mage attune rate × 1.3.", 5),
                new("tool.kit.heal", "Healer's kit",   "Caretaker heal rate × 1.4.", 8),
                new("tool.scroll",   "Scholar scroll", "Scholar research rate × 1.3.", 8),
            },
        },
        new()
        {
            Name        = "Apparel",
            Description = "Clothing and light armor. Phase 7 (Combat) gives these defensive values.",
            Items = new Entry[]
            {
                new("apparel.tunic.cloth", "Cloth tunic",    "Basic warm clothing. No armor value.", 25),
                new("apparel.cap.felt",    "Felt cap",       "Extra warmth — winter survival bonus.", 25),
                new("apparel.cloak.wool",  "Wool cloak",     "Weather protection + 5 °C effective temperature.", 25),
                new("apparel.boots.leather","Leather boots", "Foot protection + 5 % movement speed.", 25),
            },
        },
        new()
        {
            Name        = "Weapons",
            Description = "Combat gear. Locked until Phase 7 (Combat System) lands.",
            Items = new Entry[]
            {
                new("weapon.spear",  "Wooden spear",   "Guardian-tier melee weapon (Phase 7 stub).", 15),
                new("weapon.sling",  "Sling",          "Light ranged weapon (Phase 7 stub).", 15),
                new("weapon.dagger", "Iron dagger",    "Close-quarters defence (Phase 7 stub).", 15),
            },
        },
        new()
        {
            Name        = "Trade Goods",
            Description = "Items the colony can later sell to traders. Phase 11 (Technology & Culture) sets exchange rates.",
            Items = new Entry[]
            {
                new("trade.gem.small",  "Small gemstone", "High value per weight.", 15),
                new("trade.relic.minor","Minor relic",    "Cultural artifact — rare.", 5),
                new("trade.tome.minor", "Minor tome",     "Knowledge — boosts Scholar research.", 5),
            },
        },
        new()
        {
            Name        = "Miscellaneous",
            Description = "Quality-of-life consumables.",
            Items = new Entry[]
            {
                new("misc.firekit",  "Fire-starting kit", "Lets the colony spark a hearth fast.", 5),
                new("misc.rope.50",  "50-ft rope",        "General-purpose climbing / hauling.", 10),
                new("misc.lantern",  "Oil lantern",       "Visibility in caves; small heat source (Phase 10 weather).", 8),
            },
        },
    };
}
