using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using Sporeholm.Simulation;
using Sporeholm.UI;

// Floating "wanted poster" style Shroomp Card with left-side tab strip.
// Tabs: Main (needs, mood, personality) · Health (biological traits, body parts).
public partial class ShroompCardPanel : Control
{
    [Signal] public delegate void RoleChangeRequestedEventHandler(string shroompName, string newRole);

    // v0.3.29 — palette switched from the old parchment-on-tan look to the
    // dark-brown floating-panel theme used by the HUD and BottomTabPanel.
    // All four constants below are aliases pulled from UITheme so any future
    // tweak to the global theme propagates here automatically. The old names
    // are kept (DarkWood / Gold / TabIdle / TabActive) so the rest of this
    // file's references read unchanged.
    private static readonly Color DarkWood  = UITheme.TextPrimary;   // body text
    private static readonly Color Gold      = UITheme.TextAccent;    // titles / star
    private static readonly Color TabIdle   = UITheme.PanelBg;       // inactive tab
    private static readonly Color TabActive = UITheme.PanelActive;   // active tab
    private static readonly Color TextDim   = UITheme.TextMuted;     // secondary text

    private static readonly string[] Roles =
        { "Forager", "Crafter", "Scholar", "Sage", "Caretaker", "Guardian", "Elder", "Unassigned" };

    private static readonly string[] RoleTooltips =
    {
        "Forager\nGathers food and resources.\nPrimary: Foraging · Athletics · Botany",
        "Crafter\nBuilds structures and goods.\nPrimary: Crafting · Construction · Mining",
        "Scholar\nResearches and archives lore.\nPrimary: Research · Lore · Botany",
        "Sage\nChannels arcane energy.\nPrimary: Magic · Study · Social",
        "Caretaker\nTends to injured and sick.\nPrimary: Medicine · Empathy · Social",
        "Guardian\nDefends the colony.\nPrimary: Melee · Ranged · Athletics",
        "Elder\nLeads and advises the colony.\nPrimary: Leadership · Lore · Social",
        "Unassigned\nNo role assigned.\nWill not receive focused training.",
    };

    // ── Fields ────────────────────────────────────────────────────────────────
    private Label        _nameLabel    = null!;
    private Label        _ageLabel     = null!;
    private Label        _moodLabel    = null!;
    private Label        _healthLabel  = null!;
    private Label        _healthStatus = null!;
    private OptionButton _roleDropdown = null!;
    private NeedBar      _nutrition = null!, _rest = null!, _social = null!, _magic = null!, _safety = null!;
    private Button       _closeBtn  = null!;

    // Main tab — personality
    private VBoxContainer _personalityBox = null!;

    // Mood tab
    private Label _moodEffLabel  = null!;
    private Label _moodStateDesc = null!;
    private NeedBar _moodBar = null!;
    private VBoxContainer _moodTraitsBox = null!;
    // v0.4.31 — Thoughts display on the Mood tab (RimWorld-style).
    // Header text tracks the live MoodFromThoughts sum so the player can
    // see what recent events add up to without doing the math themselves.
    private Label         _moodThoughtsHeader = null!;
    private VBoxContainer _moodThoughtsBox    = null!;

    // Health tab — biological traits + body parts
    private Dictionary<string, TraitBar> _traitBars = null!;
    private VBoxContainer _bodyPartsBox = null!;
    private Dictionary<string, Label> _bodyPartValLabels = null!;

    // Skills tab
    private Dictionary<string, SkillBar> _skillBars = null!;

    // Tabs
    private VBoxContainer _mainContent   = null!;
    private VBoxContainer _moodContent   = null!;
    private VBoxContainer _healthContent = null!;
    private VBoxContainer _skillsContent = null!;
    private VBoxContainer _inventoryContent = null!;
    private Button        _tabMain       = null!;
    private Button        _tabMood       = null!;
    private Button        _tabHealth     = null!;
    private Button        _tabSkills     = null!;
    private Button        _tabInventory  = null!;

    // v0.4.3 — Inventory tab dynamic rows + cached SimulationManager reference
    // for equip / unequip / drop actions. Sim reference is provided by
    // GameController on each Open() call so the tab can route actions to
    // the sim thread without holding a long-lived reference.
    public Sporeholm.SimulationManager? Sim { get; set; }
    private VBoxContainer _inventorySlotsBox = null!;
    private VBoxContainer _inventoryCarriedBox = null!;
    private Label         _inventoryCarriedLabel = null!;
    // v0.4.30 — Carrying Capacity bar (RimWorld-style multi-item haul).
    // Shows X/Y where X = sum of Quantity in Inventory and Y = capacity
    // computed from life stage + traits. Worn equipment doesn't count.
    private ProgressBar   _carryBar = null!;
    private Label         _carryBarLabel = null!;

    private string? _selectedName;
    private bool    _suppressRoleSignal;
    private bool    _tips;

    // ── Setup ─────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        BuildShell();
        ApplyUniformScale();   // v0.5.46
        UITheme.UIScaleChanged += OnUIScaleChanged;   // v0.5.45
    }

    public override void _ExitTree()
    {
        UITheme.UIScaleChanged -= OnUIScaleChanged;
    }

    // v0.5.45 → v0.5.46 — UI Size change just re-applies the uniform
    // Control.Scale transform; no rebuild needed. Pre-v0.5.46 per-element
    // UITheme.Scaled scaled fonts + anchor offsets but left fixed bar
    // heights / button min-sizes / inner separations at original
    // logical-px values, producing the squished cramped layout Sam
    // called out at low UI Size. Uniform Control.Scale fixes proportions
    // regardless of any individual control's hardcoded sizes.
    private void OnUIScaleChanged()
    {
        ApplyUniformScale();
    }

    // v0.5.46 — uniform scale via Godot Control.Scale. Pivots from the
    // bottom-right of the layout rect so the visible panel shrinks
    // INWARD from the anchored corner, keeping the panel docked to
    // the screen's bottom-right edge regardless of UI Size.
    private void ApplyUniformScale()
    {
        float s = UITheme.UIScale;
        Scale = new Vector2(s, s);
        PivotOffset = new Vector2(Size.X, Size.Y);
    }

    private void BuildShell()
    {
        // v0.3.32 — height reduced from 380 → 320 so the card top stays
        // below the TileInfoOverlay at 720p screens. v0.5.46 — anchor
        // offsets unscaled; uniform Control.Scale handles the visual shrink.
        AnchorLeft = 1f; AnchorTop = 1f; AnchorRight = 1f; AnchorBottom = 1f;
        OffsetLeft   = -320f;
        OffsetRight  = -UITheme.EdgeInset;
        OffsetBottom = -240f;
        OffsetTop    = OffsetBottom - 320f;
        GrowHorizontal = GrowDirection.Begin;

        var cfg = new ConfigFile();
        _tips = cfg.Load("user://settings.cfg") != Error.Ok
              || (bool)cfg.GetValue("gameplay", "show_tooltips", true);

        // ── Background ────────────────────────────────────────────────────────
        // v0.3.29 — uses FloatingPanelStyle.Make() so the card matches the
        // HUD and BottomTabPanel visually (same dark parchment bg, gold
        // border, drop shadow).
        var bg = new PanelContainer();
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        bg.AddThemeStyleboxOverride("panel", FloatingPanelStyle.Make());
        AddChild(bg);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left",   4);
        margin.AddThemeConstantOverride("margin_right",  6);
        margin.AddThemeConstantOverride("margin_top",    6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        bg.AddChild(margin);

        var outerVbox = new VBoxContainer();
        outerVbox.AddThemeConstantOverride("separation", 3);
        margin.AddChild(outerVbox);

        // ── Header (always visible) ───────────────────────────────────────────
        BuildHeader(outerVbox);
        outerVbox.AddChild(HRule());

        // ── Body: tab strip + content ─────────────────────────────────────────
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 4);
        body.SizeFlagsVertical = SizeFlags.Expand | SizeFlags.Fill;
        outerVbox.AddChild(body);

        BuildTabStrip(body);
        BuildContent(body);

        ApplyTooltips();
        SwitchTab(0);
        Visible = false;
    }

    // ── Header ────────────────────────────────────────────────────────────────
    private void BuildHeader(VBoxContainer parent)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        parent.AddChild(header);

        header.AddChild(MakeLabel("★", 16, Gold));

        _nameLabel = MakeLabel("", 13, DarkWood);
        _nameLabel.SizeFlagsHorizontal = SizeFlags.Expand;
        header.AddChild(_nameLabel);

        _closeBtn = new AnimatedButton
        {
            Text = "✕", PlayHoverSound = false, Compact = true,
            CustomMinimumSize = new Vector2(22, 22),
        };
        _closeBtn.Pressed += () => { _selectedName = null; Visible = false; };
        header.AddChild(_closeBtn);
    }

    // ── Tab strip ─────────────────────────────────────────────────────────────
    private void BuildTabStrip(HBoxContainer parent)
    {
        var strip = new VBoxContainer();
        strip.AddThemeConstantOverride("separation", 2);
        strip.CustomMinimumSize = new Vector2(28, 0);
        parent.AddChild(strip);

        _tabMain      = MakeTabButton("Main",   0);
        _tabMood      = MakeTabButton("Mood",   1);
        _tabHealth    = MakeTabButton("Health", 2);
        _tabSkills    = MakeTabButton("Skills", 3);
        _tabInventory = MakeTabButton("Inv",    4);
        strip.AddChild(_tabMain);
        strip.AddChild(_tabMood);
        strip.AddChild(_tabHealth);
        strip.AddChild(_tabSkills);
        strip.AddChild(_tabInventory);
    }

    private Button MakeTabButton(string label, int idx)
    {
        // v0.3.29 — tab pills use FloatingPanelStyle.MakeToolbarButton so
        // they share the look-and-feel of the bottom Orders/Build/Zones/…
        // tab capsule. Active tab pops with the brighter parchment border.
        var btn = new Button
        {
            Text              = label,
            ToggleMode        = true,
            FocusMode         = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(30, 24),
        };
        btn.AddThemeFontSizeOverride("font_size", 9);
        btn.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeColorOverride("font_color",          UITheme.TextMuted);
        btn.AddThemeColorOverride("font_hover_color",    UITheme.TextPrimary);
        btn.AddThemeColorOverride("font_pressed_color",  UITheme.TextAccent);
        btn.Pressed += () => SwitchTab(idx);
        return btn;
    }

    private void SwitchTab(int idx)
    {
        _tabMain.ButtonPressed      = idx == 0;
        _tabMood.ButtonPressed      = idx == 1;
        _tabHealth.ButtonPressed    = idx == 2;
        _tabSkills.ButtonPressed    = idx == 3;
        _tabInventory.ButtonPressed = idx == 4;
        _mainContent.Visible        = idx == 0;
        _moodContent.Visible        = idx == 1;
        _healthContent.Visible      = idx == 2;
        _skillsContent.Visible      = idx == 3;
        _inventoryContent.Visible   = idx == 4;
    }

    // ── Content panels ────────────────────────────────────────────────────────
    private void BuildContent(HBoxContainer parent)
    {
        var contentWrap = new VBoxContainer();
        contentWrap.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
        contentWrap.SizeFlagsVertical   = SizeFlags.Expand | SizeFlags.Fill;
        parent.AddChild(contentWrap);

        // Wrap both tabs in a shared scroll so tall content stays usable.
        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal  = SizeFlags.Expand | SizeFlags.Fill;
        scroll.SizeFlagsVertical    = SizeFlags.Expand | SizeFlags.Fill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        contentWrap.AddChild(scroll);

        // Inner margin keeps content away from the scrollbar shadow.
        var scrollMargin = new MarginContainer();
        scrollMargin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scrollMargin.AddThemeConstantOverride("margin_right", 6);
        scroll.AddChild(scrollMargin);

        // Inner VBox holds both tab panels; only one is visible at a time.
        var inner = new VBoxContainer();
        inner.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
        scrollMargin.AddChild(inner);

        _mainContent      = BuildMainTab();
        _moodContent      = BuildMoodTab();
        _healthContent    = BuildHealthTab();
        _skillsContent    = BuildSkillsTab();
        _inventoryContent = BuildInventoryTab();
        inner.AddChild(_mainContent);
        inner.AddChild(_moodContent);
        inner.AddChild(_healthContent);
        inner.AddChild(_skillsContent);
        inner.AddChild(_inventoryContent);
    }

    // ── Inventory tab (v0.4.3) ───────────────────────────────────────────────
    //
    // Three equipment slots (Tool / Weapon / Apparel) plus the shroomp's
    // current Carried Item. Each row shows what's currently slotted, an
    // Equip button (opens a colony-inventory picker filtered to the
    // slot's compatible kinds), Unequip (moves back to colony pool), and
    // Drop (drops the item on the shroomp's current tile via
    // `SimulationManager.RequestDropEquipped`). Carried items get the
    // Drop button only — they're picked up / put down by the haul loop,
    // not equipped.
    private VBoxContainer BuildInventoryTab()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        vbox.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;

        // v0.4.30 — Carrying Capacity row (label + bar) at the very top
        // so the player sees how much haul room the shroomp still has.
        // Worn Equipment items don't count toward this; only the
        // Inventory list does.
        _carryBarLabel = MakeLabel("Carrying: 0 / 0", 11, Gold);
        vbox.AddChild(_carryBarLabel);
        _carryBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Value = 0, ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 10),
        };
        _carryBar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        vbox.AddChild(_carryBar);

        var header = MakeLabel("Equipment", 12, Gold);
        vbox.AddChild(header);

        _inventorySlotsBox = new VBoxContainer();
        _inventorySlotsBox.AddThemeConstantOverride("separation", 4);
        _inventorySlotsBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;   // v0.5.54
        vbox.AddChild(_inventorySlotsBox);

        // v0.4.30 — renamed "Carried" → "Inventory" since it now lists
        // every item in the multi-item carry, not just the one-slot
        // CarriedItem from v0.4.2-v0.4.29. Each row shows the stack
        // (e.g. "Granite ×27"). Whole list still has a Drop-all button
        // wired through the legacy single-slot RequestDropEquipped path.
        var carriedHeader = MakeLabel("Inventory", 12, Gold);
        vbox.AddChild(carriedHeader);
        _inventoryCarriedBox = new VBoxContainer();
        _inventoryCarriedBox.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(_inventoryCarriedBox);
        _inventoryCarriedLabel = MakeLabel("(empty)", 10, TextDim);
        _inventoryCarriedBox.AddChild(_inventoryCarriedLabel);

        return vbox;
    }

    // v0.4.4 — per-body-part rows mirror DF's "wear an item on each
    // external part" model. Walks every EquipSlot in head-to-toe
    // order. Hand slots show whatever's currently slotted (tool /
    // weapon / shield in the future); other slots show apparel.
    // Dominant hand gets a "✋" highlight so the player knows which
    // side auto-equip will fill first.
    private void RefreshInventoryTab(ShroompSnapshot snap)
    {
        foreach (var c in _inventorySlotsBox.GetChildren()) c.QueueFree();

        // Header showing handedness.
        var hLabel = MakeLabel(
            $"Handedness: {snap.Handedness}",
            10, TextDim);
        _inventorySlotsBox.AddChild(hLabel);

        foreach (var slot in Sporeholm.Simulation.Items.EquipSlotMeta.All)
        {
            string slotKey = slot.ToString();
            string? subType = null;
            string? kindStr = null;
            if (snap.Equipment.TryGetValue(slotKey, out var payload))
            {
                subType = payload.SubType;
                kindStr = payload.Kind;
            }
            bool isDominant =
                (slot == Sporeholm.Simulation.Items.EquipSlot.LeftHand  && snap.Handedness == Handedness.Left)
             || (slot == Sporeholm.Simulation.Items.EquipSlot.RightHand && snap.Handedness == Handedness.Right);
            AddSlotRow(slot, subType, kindStr, isDominant, snap.Name);
        }

        // v0.4.30 — refresh the Carrying Capacity bar.
        int cap = System.Math.Max(1, snap.CarryingCapacity);
        int cur = System.Math.Clamp(snap.CurrentCarriedCount, 0, cap);
        _carryBar.MaxValue = cap;
        _carryBar.Value    = cur;
        _carryBarLabel.Text = $"Carrying: {snap.CurrentCarriedCount} / {snap.CarryingCapacity}";

        // v0.4.30 — list every stack in the shroomp's Inventory rather than
        // the one CarriedItem hint. Each row: "{display} ({material}) ×{qty}".
        // The legacy "Drop" button still routes through the single-slot
        // RequestDropEquipped("Carried") which now drops the most-recent
        // item via the v0.4.30 backward-compat property; multi-item drop
        // UI is a follow-up.
        foreach (var c in _inventoryCarriedBox.GetChildren()) c.QueueFree();
        if (snap.InventoryItems != null && snap.InventoryItems.Count > 0)
        {
            for (int i = 0; i < snap.InventoryItems.Count; i++)
            {
                var entry = snap.InventoryItems[i];
                var defSub = Sporeholm.Simulation.Items.ItemRegistry.Get(
                    System.Enum.TryParse<Sporeholm.Simulation.Items.ItemKind>(entry.Kind, out var ek)
                        ? ek : Sporeholm.Simulation.Items.ItemKind.Material,
                    entry.SubType);
                string display = defSub?.DisplayName ?? entry.SubType;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                _inventoryCarriedBox.AddChild(row);
                string qtySuffix = entry.Quantity > 1 ? $" ×{entry.Quantity}" : "";
                var label = MakeLabel($"{display} ({entry.MaterialFamily}){qtySuffix}", 11, DarkWood);
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                // Only the last item gets the Drop button (legacy single-
                // slot semantics — clears the most-recent via the
                // backward-compat CarriedItem.set = null shim).
                if (i == snap.InventoryItems.Count - 1)
                {
                    var drop = MakeSmallActionBtn("Drop", () =>
                    {
                        Sim?.RequestDropEquipped(snap.Name, "Carried");
                    });
                    row.AddChild(drop);
                }
            }
        }
        else
        {
            var idle = MakeLabel("(empty)", 10, TextDim);
            _inventoryCarriedBox.AddChild(idle);
        }
    }

    // v0.4.4 — single slot row. slot identifies the body part; subType /
    // kindStr name the equipped item (null = empty). isDominant adds
    // a ✋ tag on the dominant hand so the player knows which side
    // auto-equip will fill.
    private void AddSlotRow(Sporeholm.Simulation.Items.EquipSlot slot,
        string? equippedSub, string? kindStr, bool isDominant, string shroompName)
    {
        // v0.5.54 — row widths previously overflowed the panel's right edge
        // (slot 72 + name + two stylebox-padded buttons exceeded the ~262
        // logical-px inventory content area). Tightened slot label, button
        // min-width, and separation so equipped-slot rows fit within the
        // panel. Name label now clips rather than pushing buttons off-screen.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _inventorySlotsBox.AddChild(row);

        string slotDisplay = Sporeholm.Simulation.Items.EquipSlotMeta.Display(slot);
        if (isDominant) slotDisplay = "✋ " + slotDisplay;
        var slotLbl = MakeLabel(slotDisplay, 10, Gold);
        slotLbl.CustomMinimumSize = new Vector2(60, 0);
        slotLbl.ClipText = true;
        row.AddChild(slotLbl);

        string display = "(empty)";
        if (equippedSub != null && kindStr != null
            && System.Enum.TryParse<Sporeholm.Simulation.Items.ItemKind>(kindStr, out var kind))
        {
            var def = Sporeholm.Simulation.Items.ItemRegistry.Get(kind, equippedSub);
            display = def?.DisplayName ?? equippedSub;
        }
        var nameLbl = MakeLabel(display, 10,
            equippedSub != null ? DarkWood : TextDim);
        nameLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameLbl.CustomMinimumSize = new Vector2(0, 0);
        nameLbl.ClipText = true;
        nameLbl.TooltipText = display;   // surface full name when clipped
        row.AddChild(nameLbl);

        string slotName = slot.ToString();
        if (equippedSub != null)
        {
            row.AddChild(MakeSmallActionBtn("Unequip", () => Sim?.RequestUnequip(shroompName, slotName)));
            row.AddChild(MakeSmallActionBtn("Drop",    () => Sim?.RequestDropEquipped(shroompName, slotName)));
        }
        else
        {
            row.AddChild(MakeSmallActionBtn("Equip", () => OpenEquipPickerForSlot(shroompName, slot)));
        }
    }

    private Button MakeSmallActionBtn(string text, System.Action onPressed)
    {
        // v0.5.54 — min-width 52 → 40. The previous 52 px + toolbar-stylebox
        // horizontal padding made each button render ~70-80 px wide, which
        // pushed the equipped-slot rows past the panel's right edge.
        var btn = new Button
        {
            Text              = text,
            CustomMinimumSize = new Vector2(40, 20),
            FocusMode         = FocusModeEnum.None,
        };
        btn.AddThemeFontSizeOverride("font_size", 9);
        btn.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
        btn.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeStyleboxOverride("focus",   FloatingPanelStyle.MakeToolbarButton(true));
        btn.AddThemeColorOverride("font_color",         UITheme.TextPrimary);
        btn.AddThemeColorOverride("font_hover_color",   UITheme.TextAccent);
        btn.AddThemeColorOverride("font_pressed_color", UITheme.TextAccent);
        btn.Pressed += () => onPressed?.Invoke();
        return btn;
    }

    // v0.4.4 — slot-aware equip picker. Filters the colony inventory
    // to items whose `BodyClass` matches the slot — a LeftHand pick
    // lists Hand-class items (tools + weapons), a Torso pick lists
    // Torso-class apparel (cloaks / robes), and so on. Click an item
    // to slot it directly into the chosen body part.
    private void OpenEquipPickerForSlot(string shroompName, Sporeholm.Simulation.Items.EquipSlot slot)
    {
        if (Sim == null) return;

        // Figure out which BodyClass the slot accepts.
        var targetClass = Sporeholm.Simulation.Items.EquipSlotMeta.BodyClass.None;
        foreach (var bc in System.Enum.GetValues<Sporeholm.Simulation.Items.EquipSlotMeta.BodyClass>())
        {
            foreach (var s in Sporeholm.Simulation.Items.EquipSlotMeta.SlotsFor(bc))
            {
                if (s == slot) { targetClass = bc; break; }
            }
            if (targetClass != Sporeholm.Simulation.Items.EquipSlotMeta.BodyClass.None) break;
        }
        if (targetClass == Sporeholm.Simulation.Items.EquipSlotMeta.BodyClass.None) return;

        var inv = Sim.GetInventorySnapshot();
        var matches = new System.Collections.Generic.List<Sporeholm.Simulation.Items.InventoryRow>();
        foreach (var row in inv)
        {
            var def = Sporeholm.Simulation.Items.ItemRegistry.Get(row.Kind, row.SubType);
            if (def == null) continue;
            if (def.BodyClass == targetClass) matches.Add(row);
        }

        var popup = new PopupPanel { Title = $"Equip — {Sporeholm.Simulation.Items.EquipSlotMeta.Display(slot)}" };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left",   8);
        pad.AddThemeConstantOverride("margin_right",  8);
        pad.AddThemeConstantOverride("margin_top",    8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        popup.AddChild(pad);
        pad.AddChild(box);

        if (matches.Count == 0)
        {
            box.AddChild(MakeLabel("Nothing compatible in stockpile.", 11, TextDim));
        }
        else
        {
            foreach (var m in matches)
            {
                var def = Sporeholm.Simulation.Items.ItemRegistry.Get(m.Kind, m.SubType);
                var matDef = Sporeholm.Simulation.Items.MaterialRegistry.Get(
                    new Sporeholm.Simulation.Items.MaterialKey(m.MaterialFamily, m.MaterialSubType));
                string line = $"{def?.DisplayName ?? m.SubType} — {matDef?.DisplayName ?? m.MaterialSubType} ({m.Quality}, ×{m.Quantity})";
                string slotHint = slot.ToString();
                var btn = MakeSmallActionBtn(line, () =>
                {
                    Sim.RequestEquip(shroompName, m.SubType, m.MaterialFamily, m.MaterialSubType, slotHint);
                    popup.Hide();
                    popup.QueueFree();
                });
                btn.CustomMinimumSize = new Vector2(260, 22);
                box.AddChild(btn);
            }
        }

        AddChild(popup);
        popup.PopupCentered();
    }

    // ── Main tab ──────────────────────────────────────────────────────────────
    private VBoxContainer BuildMainTab()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 5);
        vbox.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;

        // Age
        _ageLabel = MakeLabel("", 10, TextDim);
        vbox.AddChild(_ageLabel);

        // Role
        var roleRow = new HBoxContainer();
        roleRow.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(roleRow);
        roleRow.AddChild(MakeLabel("Role:", 10, Gold));

        _roleDropdown = new OptionButton();
        _roleDropdown.SizeFlagsHorizontal = SizeFlags.Expand;
        _roleDropdown.AddThemeFontSizeOverride("font_size", 10);
        foreach (var r in Roles) _roleDropdown.AddItem(r);
        _roleDropdown.ItemSelected += OnRoleSelected;
        roleRow.AddChild(_roleDropdown);

        // Mood
        _moodLabel = MakeLabel("", 10, DarkWood);
        vbox.AddChild(_moodLabel);

        // Health summary
        var healthRow = new HBoxContainer();
        healthRow.AddThemeConstantOverride("separation", 4);
        _healthLabel = MakeLabel("Health: —", 10, DarkWood);
        _healthLabel.SizeFlagsHorizontal = SizeFlags.Expand;
        _healthStatus = MakeLabel("", 10, Colors.White);
        healthRow.AddChild(_healthLabel);
        healthRow.AddChild(_healthStatus);
        vbox.AddChild(healthRow);

        vbox.AddChild(HRule());

        // Needs
        _nutrition = AddNeedBar(vbox, "Nutrition", new Color(0.80f, 0.35f, 0.15f));
        _rest      = AddNeedBar(vbox, "Rest",      new Color(0.30f, 0.55f, 0.90f));
        _social    = AddNeedBar(vbox, "Social",    new Color(0.25f, 0.70f, 0.45f));
        _magic     = AddNeedBar(vbox, "Magic",     new Color(0.70f, 0.30f, 0.90f));
        _safety    = AddNeedBar(vbox, "Safety",    new Color(0.90f, 0.75f, 0.20f));

        vbox.AddChild(HRule());

        // Personality
        vbox.AddChild(MakeLabel("Personality", 10, Gold));
        _personalityBox = new VBoxContainer();
        _personalityBox.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_personalityBox);

        // v0.3.34 — WANTED (for science) stamp removed. The Main tab now
        // fits without scrolling at the card's 320 px height.

        return vbox;
    }

    // ── Mood tab ───────────────────────────────────────────────────────────────
    private VBoxContainer BuildMoodTab()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 5);
        vbox.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;

        var brown = Gold;
        var dim   = TextDim;

        // Effective mood
        _moodEffLabel = MakeLabel("", 11, DarkWood);
        vbox.AddChild(_moodEffLabel);

        _moodBar = new NeedBar("Mood", MoodStateColor(MoodState.Content));
        // v0.3.34 — replace the long "Thresholds" text list with tick marks
        // drawn on the bar itself at the four state boundaries (20/40/60/80).
        // The bar now visually conveys every threshold at a glance.
        _moodBar.AddThresholdTick(20f, MoodStateColor(MoodState.Distressed));
        _moodBar.AddThresholdTick(40f, MoodStateColor(MoodState.Stressed));
        _moodBar.AddThresholdTick(60f, MoodStateColor(MoodState.Content));
        _moodBar.AddThresholdTick(80f, MoodStateColor(MoodState.Inspired));
        _moodBar.MouseFilter = MouseFilterEnum.Pass;
        _moodBar.TooltipText =
            "Mood (0–100). Tick marks are state thresholds:\n" +
            "  Inspired   ≥ 80\n" +
            "  Content    ≥ 60\n" +
            "  Stressed   ≥ 40\n" +
            "  Distressed ≥ 20\n" +
            "  Breaking   > 0\n" +
            "  Collapse   = 0";
        vbox.AddChild(_moodBar);

        _moodStateDesc = MakeLabel("", 9, dim);
        _moodStateDesc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_moodStateDesc);

        vbox.AddChild(HRule());

        // v0.4.52b — Breakdown section removed. Was a two-line dump of
        // the raw needs-contribution score + signed personality modifier
        // (the same numbers folded into the bar + the Personality Effects
        // section below). Sam: redundant noise that pushed the actually-
        // useful Recent Thoughts list further down the card.

        // v0.4.31 — Thoughts (RimWorld-style). The last 5 active thought
        // entries are shown with their headline + signed mood offset; the
        // section header tracks the live MoodFromThoughts sum so the player
        // can see at a glance what the shroomp's recent experiences add up
        // to. Thoughts decay on their own per-def TTL — see ThoughtSystem.
        _moodThoughtsHeader = MakeLabel("Recent Thoughts", 9, brown);
        vbox.AddChild(_moodThoughtsHeader);
        _moodThoughtsBox = new VBoxContainer();
        _moodThoughtsBox.AddThemeConstantOverride("separation", 1);
        vbox.AddChild(_moodThoughtsBox);

        vbox.AddChild(HRule());

        // v0.3.34 — Thresholds text-list section removed; replaced with the
        // bar tick marks above. Tooltip on the bar carries the same info.

        // Personality mood modifiers
        vbox.AddChild(MakeLabel("Personality Effects", 9, brown));
        _moodTraitsBox = new VBoxContainer();
        _moodTraitsBox.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_moodTraitsBox);

        return vbox;
    }

    // ── Health tab ─────────────────────────────────────────────────────────────
    private VBoxContainer BuildHealthTab()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 5);
        vbox.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;

        // Body parts
        vbox.AddChild(MakeLabel("Body", 10, Gold));
        _bodyPartsBox = new VBoxContainer();
        _bodyPartsBox.AddThemeConstantOverride("separation", 1);
        _bodyPartValLabels = new Dictionary<string, Label>();
        vbox.AddChild(_bodyPartsBox);

        vbox.AddChild(HRule());

        // Biological traits
        vbox.AddChild(MakeLabel("Biological Traits", 10, Gold));

        _traitBars = new Dictionary<string, TraitBar>();
        foreach (var def in TraitRegistry.All)
        {
            var bar = new TraitBar(FormatTraitName(def.Name));
            if (_tips) bar.TooltipText = $"{FormatTraitName(def.Name)}\n{def.Description}\nPenetrance: 0% suppressed → 100% fully active.";
            vbox.AddChild(bar);
            _traitBars[def.Name] = bar;
        }

        return vbox;
    }

    // ── Skills tab ────────────────────────────────────────────────────────────
    private VBoxContainer BuildSkillsTab()
    {
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 3);
        vbox.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;

        _skillBars = new Dictionary<string, SkillBar>();

        string currentDomain = "";
        foreach (var def in SkillRegistry.All)
        {
            if (def.Domain != currentDomain)
            {
                if (currentDomain != "") vbox.AddChild(HRule());
                currentDomain = def.Domain;
                vbox.AddChild(MakeLabel(def.Domain, 9, Gold));
            }

            var bar = new SkillBar(def.Name);
            if (_tips)
                bar.TooltipText = $"{def.Name}  [{def.Domain}]\n{def.Description}\n0 = untrained  ·  20 = master.\nGold +N shows the role bonus applied during work.";
            vbox.AddChild(bar);
            _skillBars[def.Name] = bar;
        }

        return vbox;
    }

    private void RefreshSkillsTab(IReadOnlyDictionary<string, int> skills, string role)
    {
        foreach (var def in SkillRegistry.All)
        {
            if (!_skillBars.TryGetValue(def.Name, out var bar)) continue;
            skills.TryGetValue(def.Name, out int level);
            bar.SetValue(level, SkillRegistry.GetRoleBonus(role, def.Name));
        }
    }

    // ── Tooltips (applied once) ───────────────────────────────────────────────
    private void ApplyTooltips()
    {
        if (!_tips) return;

        _closeBtn.TooltipText  = "Close shroomp card";
        _tabMain.TooltipText   = "Overview — needs, mood summary, and personality.";
        _tabMood.TooltipText   = "Mood — detailed mental state and personality effects.";
        _tabHealth.TooltipText = "Health — body part conditions and biological traits.";
        _tabSkills.TooltipText = "Skills — trained abilities (0 untrained → 20 master).";
        _tabInventory.TooltipText = "Inventory — equipment slots and carried items.\nEquip from the colony stockpile, unequip back to it, or drop on the ground.";

        _ageLabel.MouseFilter = MouseFilterEnum.Pass;
        _ageLabel.TooltipText = "Age and life stage.\nSprout (0–19) · Juvenile (20–49) · Adult (50–399) · Elder (400–544) · Last Season (545+).";

        _healthLabel.MouseFilter  = MouseFilterEnum.Pass;
        _healthStatus.MouseFilter = MouseFilterEnum.Pass;
        _healthLabel.TooltipText  = "Weighted health average.\nVital organs count 3× — their damage has greater impact.";

        _moodLabel.MouseFilter = MouseFilterEnum.Pass;
        _moodLabel.TooltipText =
            "Mood (0–100) from all five needs.\n" +
            "Inspired 80+ · Content 60+ · Stressed 40+\n" +
            "Distressed 20+ · Breaking 1+ · Collapse 0";

        _roleDropdown.TooltipText = "Determines work, need decay, and skills.";
        for (int i = 0; i < Roles.Length; i++)
            _roleDropdown.SetItemTooltip(i, RoleTooltips[i]);

        _moodEffLabel.MouseFilter  = MouseFilterEnum.Pass;
        _moodEffLabel.TooltipText  = "Effective mood = needs contribution + personality modifier, clamped 0–100.";
        _moodStateDesc.MouseFilter = MouseFilterEnum.Pass;
        _moodStateDesc.TooltipText = "Description of the shroomp's current mental state.";

        _nutrition.TooltipText = "Nutrition (0–100)\nDrains over time. Foragers slow the decline.";
        _rest.TooltipText      = "Rest (0–100)\nSleep and energy. Low rest drags mood.";
        _social.TooltipText    = "Social (0–100)\nCommunity need. High Communal Bonding = faster drain.";
        _magic.TooltipText     = "Magic Resonance (0–100)\nSages regenerate it. All shroomps need a baseline.";
        _safety.TooltipText    = "Safety (0–100)\nMaintained by Guardians and a stable colony.";
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void Show(ShroompSnapshot snap)
    {
        _selectedName = snap.Name;
        RefreshFromSnapshot(snap);
        SwitchTab(0);

        Visible     = true;
        OffsetLeft  = 0f;
        OffsetRight = 310f;
        var t = CreateTween().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        t.TweenProperty(this, "offset_left",  -310f, 0.25f);
        t.TweenProperty(this, "offset_right",    0f, 0.25f);
    }

    public void Refresh(IReadOnlyList<ShroompSnapshot> snaps)
    {
        if (!Visible || _selectedName == null) return;
        foreach (var snap in snaps)
        {
            if (snap.Name != _selectedName) continue;
            RefreshFromSnapshot(snap);
            return;
        }
        _selectedName = null;
        Visible = false;
    }

    // v0.3.36 — O(1) lookup path using SimulationSnapshot's by-name index.
    // Prefer this over Refresh(list) when the caller already holds the
    // SimulationSnapshot (e.g. OnTick); avoids the linear scan above.
    public void Refresh(SimulationSnapshot snap)
    {
        if (!Visible || _selectedName == null) return;
        if (snap.ShroompsByName.TryGetValue(_selectedName, out var ss))
        {
            RefreshFromSnapshot(ss);
            return;
        }
        _selectedName = null;
        Visible = false;
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    private void RefreshFromSnapshot(ShroompSnapshot snap)
    {
        // v0.3.46 — show the shroomp's current activity beside their name so
        // the unit card surfaces what they're doing right now without the
        // player having to open the roster. Format mirrors Sam's brief
        // ("Name - Task") and uses the shared TaskVerb table so the
        // wording stays in sync with the roster Activity column.
        _nameLabel.Text = $"{snap.Name} — {TaskVerb.Of(snap.CurrentTask)}";
        _ageLabel.Text  = $"Age: {snap.AgeInYears} y  ·  {snap.LifeStage}";
        _moodLabel.Text = $"Mood: {snap.MoodState}  ({snap.MoodScore:F0}/100)";

        SetDropdownRole(snap.Role);

        _nutrition.Value = snap.Nutrition;
        _rest.Value      = snap.Rest;
        _social.Value    = snap.Social;
        _magic.Value     = snap.MagicResonance;
        _safety.Value    = snap.Safety;

        // Health summary (vital parts weighted 3×, non-vital 1×)
        float weightedSum = 0f, totalWeight = 0f;
        foreach (var def in BodyPartRegistry.Template)
        {
            if (!snap.BodyParts.TryGetValue(def.Name, out float cond)) continue;
            float w = def.Vital ? 3f : 1f;
            weightedSum += cond * w;
            totalWeight += w;
        }
        float healthPct = totalWeight > 0f ? weightedSum / totalWeight : 100f;
        _healthLabel.Text = $"Health: {healthPct:F0}%";
        if (healthPct >= 75f)
        { _healthStatus.Text = "Healthy"; _healthStatus.AddThemeColorOverride("font_color", new Color(0.20f, 0.70f, 0.25f)); }
        else if (healthPct >= 40f)
        { _healthStatus.Text = "OK";      _healthStatus.AddThemeColorOverride("font_color", new Color(0.80f, 0.65f, 0.10f)); }
        else
        { _healthStatus.Text = "Bad";     _healthStatus.AddThemeColorOverride("font_color", new Color(0.80f, 0.18f, 0.18f)); }

        // Personality
        RefreshPersonality(snap.Personality);

        // Mood tab
        RefreshMoodTab(snap);

        // Biological traits
        foreach (var (name, penetrance) in snap.Traits)
            if (_traitBars.TryGetValue(name, out var bar))
                bar.Value = penetrance * 100f;

        // Body parts
        RefreshBodyParts(snap.BodyParts);

        // Skills
        RefreshSkillsTab(snap.Skills, snap.Role);
        RefreshInventoryTab(snap);
    }

    private void RefreshPersonality(IReadOnlyList<string> traits)
    {
        // Rebuild labels only when trait set changes (rare — usually once on Show).
        int existing = _personalityBox.GetChildCount();
        bool same = existing == traits.Count;
        if (same)
        {
            for (int i = 0; i < existing; i++)
                if (((Label)_personalityBox.GetChild(i)).Text != $"  • {traits[i]}")
                { same = false; break; }
        }
        if (same) return;

        foreach (Node c in _personalityBox.GetChildren()) c.QueueFree();

        foreach (var traitName in traits)
        {
            var lbl = new Label { Text = $"  • {traitName}", MouseFilter = MouseFilterEnum.Pass };
            lbl.AddThemeColorOverride("font_color", DarkWood);
            lbl.AddThemeFontSizeOverride("font_size", 9);
            var def = PersonalityRegistry.Get(traitName);
            if (_tips && def != null)
                lbl.TooltipText = PersonalityRegistry.BuildGameplayTooltip(def);   // v0.5.1
            _personalityBox.AddChild(lbl);
        }
    }

    private void RefreshMoodTab(ShroompSnapshot snap)
    {
        // Effective mood header + bar
        _moodEffLabel.Text = $"Mood: {snap.MoodScore:F0} / 100  ({snap.MoodState})";
        var moodColor = MoodStateColor(snap.MoodState);
        _moodEffLabel.AddThemeColorOverride("font_color", moodColor);
        _moodBar.Value = snap.MoodScore;
        _moodBar.SetBarColor(moodColor);

        _moodStateDesc.Text = snap.MoodState switch
        {
            MoodState.Inspired   => "Inspired — at their best. Productivity up.",
            MoodState.Content    => "Content — stable and functional.",
            MoodState.Stressed   => "Stressed — struggling but managing.",
            MoodState.Distressed => "Distressed — needs attention soon.",
            MoodState.Breaking   => "Breaking — on the edge of collapse.",
            MoodState.Collapse   => "Collapse — completely overwhelmed.",
            _ => ""
        };

        // v0.4.52b — Breakdown section removed (see Build_MoodTab note).

        // v0.4.31 — Recent thoughts (RimWorld-style). Header tracks the
        // live MoodFromThoughts sum; list shows up to 5 most-recent
        // entries (sorted by TicksRemaining desc in the snapshot helper).
        // Each row is a small label "  {headline} ({mood-sign}{abs}) [decay]"
        // with green/red color matching the offset sign. Empty list shows
        // an "(none recent)" placeholder. Rebuild every refresh — the
        // walk is bounded by ThoughtCapacity = 8, cheap.
        {
            string sumSign = snap.MoodFromThoughts >= 0f ? "+" : "";
            _moodThoughtsHeader.Text = $"Recent Thoughts  ({sumSign}{snap.MoodFromThoughts:F0})";
            _moodThoughtsHeader.AddThemeColorOverride("font_color",
                snap.MoodFromThoughts > 0f ? new Color(0.20f, 0.65f, 0.25f) :
                snap.MoodFromThoughts < 0f ? new Color(0.80f, 0.25f, 0.20f) :
                                              Gold);

            foreach (Node c in _moodThoughtsBox.GetChildren()) c.QueueFree();
            int shown = 0;
            const int MaxShown = 5;
            if (snap.Thoughts != null)
            {
                for (int i = 0; i < snap.Thoughts.Count && shown < MaxShown; i++)
                {
                    var th = snap.Thoughts[i];
                    string sign = th.MoodOffset >= 0f ? "+" : "";
                    string ctx  = string.IsNullOrEmpty(th.Context) ? "" : $" — {th.Context}";
                    var lbl = new Label
                    {
                        Text         = $"  {th.Headline}{ctx}  ({sign}{th.MoodOffset:F0})",
                        MouseFilter  = MouseFilterEnum.Pass,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    };
                    lbl.AddThemeColorOverride("font_color",
                        th.MoodOffset > 0f ? new Color(0.30f, 0.70f, 0.35f) :
                        th.MoodOffset < 0f ? new Color(0.85f, 0.35f, 0.30f) :
                                              TextDim);
                    lbl.AddThemeFontSizeOverride("font_size", 9);
                    if (_tips)
                    {
                        // Approximate decay seconds @ 1× = ticks / 60.
                        int secs = th.TicksRemaining / 60;
                        lbl.TooltipText = $"{th.Headline}\nMood: {sign}{th.MoodOffset:F0}\nFades in ~{secs}s";
                    }
                    _moodThoughtsBox.AddChild(lbl);
                    shown++;
                }
            }
            if (shown == 0)
            {
                var none = new Label { Text = "  (no recent thoughts)", MouseFilter = MouseFilterEnum.Pass };
                none.AddThemeColorOverride("font_color", TextDim);
                none.AddThemeFontSizeOverride("font_size", 9);
                _moodThoughtsBox.AddChild(none);
            }
        }

        // Personality mood modifier list — rebuild only when traits change.
        var moodTraits = new List<(string Name, float Mod)>();
        foreach (var name in snap.Personality)
        {
            var def = PersonalityRegistry.Get(name);
            if (def != null && def.MoodModifier != 0f)
                moodTraits.Add((name, def.MoodModifier));
        }

        int existing = _moodTraitsBox.GetChildCount();
        bool same = existing == moodTraits.Count;
        if (same)
        {
            for (int i = 0; i < existing; i++)
            {
                var (n, m) = moodTraits[i];
                string sign = m >= 0f ? "+" : "";
                if (((Label)_moodTraitsBox.GetChild(i)).Text != $"  {n}: {sign}{m:F0}")
                { same = false; break; }
            }
        }

        if (!same)
        {
            foreach (Node c in _moodTraitsBox.GetChildren()) c.QueueFree();

            if (moodTraits.Count == 0)
            {
                var none = new Label { Text = "  No mood effects.", MouseFilter = MouseFilterEnum.Pass };
                none.AddThemeColorOverride("font_color", TextDim);
                none.AddThemeFontSizeOverride("font_size", 9);
                _moodTraitsBox.AddChild(none);
            }
            else
            {
                foreach (var (name, mod) in moodTraits)
                {
                    string sign = mod >= 0f ? "+" : "";
                    var lbl = new Label { Text = $"  {name}: {sign}{mod:F0}", MouseFilter = MouseFilterEnum.Pass };
                    lbl.AddThemeColorOverride("font_color",
                        mod > 0f ? new Color(0.20f, 0.65f, 0.25f) : new Color(0.80f, 0.25f, 0.20f));
                    lbl.AddThemeFontSizeOverride("font_size", 9);
                    var pdef = PersonalityRegistry.Get(name);
                    if (_tips && pdef != null)
                        lbl.TooltipText = PersonalityRegistry.BuildGameplayTooltip(pdef);   // v0.5.1
                    _moodTraitsBox.AddChild(lbl);
                }
            }
        }
    }

    private static Color MoodStateColor(MoodState state) => state switch
    {
        MoodState.Inspired   => new Color(0.20f, 0.70f, 0.30f),
        MoodState.Content    => new Color(0.30f, 0.55f, 0.20f),
        MoodState.Stressed   => new Color(0.80f, 0.65f, 0.10f),
        MoodState.Distressed => new Color(0.85f, 0.40f, 0.10f),
        MoodState.Breaking   => new Color(0.80f, 0.15f, 0.15f),
        MoodState.Collapse   => new Color(0.55f, 0.05f, 0.05f),
        _ => DarkWood
    };

    private void RefreshBodyParts(IReadOnlyDictionary<string, float> parts)
    {
        // Build once; after that, update value labels through the cached dictionary.
        if (_bodyPartValLabels.Count == 0)
        {
            bool firstParent = true;
            foreach (var def in BodyPartRegistry.Template)
            {
                if (!parts.TryGetValue(def.Name, out float cond)) continue;
                bool isParent = def.Parent == "";

                if (isParent)
                {
                    if (!firstParent) _bodyPartsBox.AddChild(HRule());
                    firstParent = false;
                }

                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 2);

                string indent = isParent ? "" : "    ";
                var nameLbl = new Label { Text = $"{indent}{def.Name}" };
                nameLbl.AddThemeColorOverride("font_color", isParent ? DarkWood : Gold);
                nameLbl.AddThemeFontSizeOverride("font_size", isParent ? 10 : 9);
                nameLbl.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
                row.AddChild(nameLbl);

                var valLbl = new Label { Text = $"{cond:F0}%", HorizontalAlignment = HorizontalAlignment.Right };
                valLbl.AddThemeFontSizeOverride("font_size", isParent ? 10 : 9);
                row.AddChild(valLbl);
                _bodyPartValLabels[def.Name] = valLbl;

                if (_tips)
                {
                    string vital = def.Vital ? "Vital — destruction is fatal." : "Non-vital.";
                    row.TooltipText = $"{def.Name}\n{vital}";
                }

                _bodyPartsBox.AddChild(row);
            }
        }

        // Always update values and colors.
        foreach (var def in BodyPartRegistry.Template)
        {
            if (!parts.TryGetValue(def.Name, out float cond)) continue;
            if (!_bodyPartValLabels.TryGetValue(def.Name, out var valLbl)) continue;
            valLbl.Text = $"{cond:F0}%";
            valLbl.AddThemeColorOverride("font_color",
                cond >= 80f ? new Color(0.20f, 0.55f, 0.20f) :
                cond >= 50f ? new Color(0.70f, 0.55f, 0.10f) :
                cond >= 20f ? new Color(0.75f, 0.35f, 0.10f) :
                              new Color(0.70f, 0.15f, 0.15f));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private void SetDropdownRole(string role)
    {
        int idx = Array.IndexOf(Roles, role);
        if (idx < 0) return;
        _suppressRoleSignal = true;
        _roleDropdown.Selected = idx;
        _suppressRoleSignal = false;
    }

    private void OnRoleSelected(long index)
    {
        if (_suppressRoleSignal || _selectedName == null) return;
        EmitSignal(SignalName.RoleChangeRequested, _selectedName, Roles[index]);
    }

    private static NeedBar AddNeedBar(VBoxContainer parent, string label, Color color)
    {
        var bar = new NeedBar(label, color);
        parent.AddChild(bar);
        return bar;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        // v0.5.45 → v0.5.46 — font size is unscaled here; the parent
        // Control.Scale transform (ApplyUniformScale) shrinks/grows the
        // entire panel + every label proportionally. Pre-v0.5.46 the
        // per-label scaling fought the panel's hardcoded bar/button
        // heights, producing the squished layout Sam called out.
        l.AddThemeFontSizeOverride("font_size", size);
        const string font = "res://assets/fonts/Grobold.ttf";
        if (ResourceLoader.Exists(font))
            l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
        return l;
    }

    private static HSeparator HRule()
    {
        // v0.3.29 — translucent gold to match UITheme.PanelBorderColour so
        // separators inside the card read as the same family as the panel
        // border.
        var h = new HSeparator();
        h.AddThemeColorOverride("color", new Color(
            UITheme.PanelBorderColour.R, UITheme.PanelBorderColour.G,
            UITheme.PanelBorderColour.B, 0.50f));
        return h;
    }

    private static string FormatTraitName(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
}

// Compact trait penetrance bar (0–100 scale).
public partial class TraitBar : HBoxContainer
{
    private readonly ProgressBar _bar;
    private readonly Label       _valLbl;

    public float Value
    {
        get => (float)_bar.Value;
        set { _bar.Value = value; _valLbl.Text = $"{value:F0}%"; }
    }

    public TraitBar(string label)
    {
        // v0.3.34 — trait name overlaid INSIDE the bar wrap (white with
        // shadow, anchored to the left) instead of as an external left-
        // column label. Result: every bar takes the same horizontal width
        // regardless of name length, and the same row carries both pieces
        // of information.
        AddThemeConstantOverride("separation", 0);

        // Bar wrap fills the row — no external left-column label.
        var wrap = new Control();
        wrap.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
        wrap.CustomMinimumSize   = new Vector2(0, 14);
        AddChild(wrap);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 0, ShowPercentage = false };
        _bar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var barColor = new Color(0.35f, 0.58f, 0.40f);
        var fill = new StyleBoxFlat { BgColor = barColor };
        fill.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("fill", fill);
        var track = new StyleBoxFlat { BgColor = new Color(barColor.R * 0.35f, barColor.G * 0.35f, barColor.B * 0.35f, 0.45f) };
        track.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("background", track);
        wrap.AddChild(_bar);

        // Trait name — white text on left, with shadow for legibility over
        // the bar fill or the dark panel background behind the unfilled
        // portion of the track.
        var nameLbl = new Label
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
            AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = 6f, OffsetRight = -34f,   // room for value on the right
            MouseFilter = MouseFilterEnum.Ignore,
        };
        nameLbl.AddThemeColorOverride("font_color", Colors.White);
        nameLbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.90f));
        nameLbl.AddThemeConstantOverride("shadow_offset_x", 1);
        nameLbl.AddThemeConstantOverride("shadow_offset_y", 1);
        nameLbl.AddThemeFontSizeOverride("font_size", 9);
        wrap.AddChild(nameLbl);

        _valLbl = new Label
        {
            Text = "", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = -32f, OffsetRight = -4f,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _valLbl.AddThemeColorOverride("font_color", Colors.White);
        _valLbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.90f));
        _valLbl.AddThemeConstantOverride("shadow_offset_x", 1);
        _valLbl.AddThemeConstantOverride("shadow_offset_y", 1);
        _valLbl.AddThemeFontSizeOverride("font_size", 9);
        wrap.AddChild(_valLbl);
    }
}

// Skill bar: 0–20 scale with color-coded tier fill.
// SetValue(base, roleBonus) — bar fills to base level; bonus shown as "+N" in the label.
public partial class SkillBar : HBoxContainer
{
    private readonly ProgressBar _bar;
    private readonly Label       _valLbl;
    private readonly Label       _bonusLbl;

    public void SetValue(int baseLevel, int roleBonus = 0)
    {
        _bar.Value = baseLevel;
        ApplyColor(baseLevel);
        _valLbl.Text  = baseLevel.ToString();
        _bonusLbl.Text = roleBonus > 0 ? $"+{roleBonus}" : "";
    }

    public SkillBar(string label)
    {
        AddThemeConstantOverride("separation", 4);

        var nameLbl = new Label { Text = label };
        nameLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        nameLbl.AddThemeFontSizeOverride("font_size", 9);
        nameLbl.CustomMinimumSize = new Vector2(78, 0);
        nameLbl.VerticalAlignment = VerticalAlignment.Center;
        AddChild(nameLbl);

        var wrap = new Control();
        wrap.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
        wrap.CustomMinimumSize   = new Vector2(0, 12);
        AddChild(wrap);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 20, Value = 0, ShowPercentage = false };
        _bar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var fill  = new StyleBoxFlat { BgColor = TierColor(0) };
        fill.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("fill", fill);

        var track = new StyleBoxFlat { BgColor = new Color(0.20f, 0.20f, 0.20f, 0.30f) };
        track.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("background", track);
        wrap.AddChild(_bar);

        // Base level number.
        _valLbl = new Label
        {
            Text = "0", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = -36f, OffsetRight = -18f,
        };
        _valLbl.AddThemeColorOverride("font_color", Colors.White);
        _valLbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
        _valLbl.AddThemeConstantOverride("shadow_offset_x", 1);
        _valLbl.AddThemeConstantOverride("shadow_offset_y", 1);
        _valLbl.AddThemeFontSizeOverride("font_size", 8);
        wrap.AddChild(_valLbl);

        // Role bonus "+N" shown in gold to the right of the base level.
        _bonusLbl = new Label
        {
            Text = "", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = -18f, OffsetRight = -1f,
        };
        _bonusLbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.70f, 0.15f));
        _bonusLbl.AddThemeConstantOverride("shadow_offset_x", 1);
        _bonusLbl.AddThemeConstantOverride("shadow_offset_y", 1);
        _bonusLbl.AddThemeFontSizeOverride("font_size", 7);
        wrap.AddChild(_bonusLbl);
    }

    private void ApplyColor(int level)
    {
        var fill = new StyleBoxFlat { BgColor = TierColor(level) };
        fill.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("fill", fill);
    }

    // Novice (1–4) steel blue · Capable (5–9) bright blue · Skilled (10–14) green
    // Expert (15–19) gold · Master (20) amber
    private static Color TierColor(int level) => level switch
    {
        0      => new Color(0.30f, 0.30f, 0.30f),
        <= 4   => new Color(0.40f, 0.50f, 0.72f),
        <= 9   => new Color(0.22f, 0.58f, 0.82f),
        <= 14  => new Color(0.20f, 0.65f, 0.30f),
        <= 19  => new Color(0.80f, 0.65f, 0.10f),
        _      => new Color(0.85f, 0.42f, 0.10f),
    };
}

// Reusable labeled progress bar with numeric value overlaid.
public partial class NeedBar : HBoxContainer
{
    private readonly ProgressBar _bar;
    private readonly Label       _valLbl;
    private readonly Control     _wrap;

    public float Value
    {
        get => (float)_bar.Value;
        set { _bar.Value = value; _valLbl.Text = $"{value:F0}"; }
    }

    public void SetBarColor(Color color)
    {
        var fill = new StyleBoxFlat { BgColor = color };
        fill.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("fill", fill);
        var track = new StyleBoxFlat { BgColor = new Color(color.R * 0.35f, color.G * 0.35f, color.B * 0.35f, 0.45f) };
        track.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("background", track);
    }

    // v0.3.34 — draws a vertical tick at `value` (in the bar's 0–100 range)
    // overlaid on the progress bar's wrap. Used by BuildMoodTab to mark the
    // mood-state thresholds (20 / 40 / 60 / 80) so the player can see at a
    // glance how far the current mood is from the next state boundary.
    public void AddThresholdTick(float value, Color colour)
    {
        float pct = Mathf.Clamp(value / 100f, 0f, 1f);
        var tick = new ColorRect
        {
            Color       = new Color(colour.R, colour.G, colour.B, 0.85f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        tick.AnchorLeft   = pct;
        tick.AnchorRight  = pct;
        tick.AnchorTop    = 0f;
        tick.AnchorBottom = 1f;
        tick.OffsetLeft   = -1f;
        tick.OffsetRight  = 1f;
        tick.OffsetTop    = -1f;   // small protrusion above/below for visibility
        tick.OffsetBottom = 1f;
        _wrap.AddChild(tick);
    }

    public NeedBar(string label, Color color)
    {
        AddThemeConstantOverride("separation", 4);

        var nameLbl = new Label { Text = label };
        nameLbl.AddThemeColorOverride("font_color", UITheme.TextPrimary);
        nameLbl.AddThemeFontSizeOverride("font_size", 10);
        nameLbl.CustomMinimumSize = new Vector2(48, 0);
        nameLbl.VerticalAlignment = VerticalAlignment.Center;
        AddChild(nameLbl);

        _wrap = new Control();
        _wrap.SizeFlagsHorizontal = SizeFlags.Expand | SizeFlags.Fill;
        _wrap.CustomMinimumSize   = new Vector2(0, 14);
        AddChild(_wrap);
        var wrap = _wrap;

        _bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = 100, ShowPercentage = false };
        _bar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var fill = new StyleBoxFlat { BgColor = color };
        fill.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("fill", fill);
        var track = new StyleBoxFlat { BgColor = new Color(color.R * 0.35f, color.G * 0.35f, color.B * 0.35f, 0.45f) };
        track.SetCornerRadiusAll(3);
        _bar.AddThemeStyleboxOverride("background", track);
        wrap.AddChild(_bar);

        _valLbl = new Label
        {
            Text = "100", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 1f,
            OffsetLeft = -30f, OffsetRight = -2f,
        };
        _valLbl.AddThemeColorOverride("font_color", Colors.White);
        _valLbl.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.85f));
        _valLbl.AddThemeConstantOverride("shadow_offset_x", 1);
        _valLbl.AddThemeConstantOverride("shadow_offset_y", 1);
        _valLbl.AddThemeFontSizeOverride("font_size", 9);
        wrap.AddChild(_valLbl);
    }
}
