using Godot;
using SmurfulationC.UI;
using System.Collections.Generic;
using System.Linq;
using SmurfulationC;
using SmurfulationC.World;

// Root node for the in-game scene. Wires simulation → tile map → colony view → HUD.
public partial class GameController : Node
{
	private SimulationManager  _sim            = null!;
	private LocalMapRenderer   _mapRenderer    = null!;
	private SmurfColonyView    _colony         = null!;
	private Camera2D           _camera         = null!;
	private HUDController      _hud            = null!;
	private SmurfCardPanel     _card           = null!;
	private PauseMenuPanel     _pauseMenu      = null!;
	private SettingsPanel      _settings       = null!;
	private SaveFileBrowser    _saveBrowser    = null!;
	private GameOverPanel      _gameOver       = null!;
	private Control            _savingOverlay  = null!;
	private TileInfoOverlay    _tileInfo       = null!;
	private SmurfulationC.UI.DevPanel _devPanel = null!;
	private TilePropertiesPanel _tileProps     = null!;
	private SelectionOverlay   _selOverlay     = null!;   // v0.4.47 — single-tile selection brackets

	private Control    _generatingOverlay = null!;
	private MessageLog _msgLog            = null!;
	private string     _lastDate          = "";

	// Phase 3.x — floating UI additions. ResourceHUD merged into HUDController
	// as a second row on the left capsule in v0.3.19, so it's no longer a
	// separate field.
	private DesignationToolbar    _toolbar         = null!;
	private ResourcesPanel        _resourcesPanel  = null!;
	private JobsPanel             _jobsPanel       = null!;
	private AlertsPane            _alertsPane      = null!;
	private DesignationOverlay    _designations    = null!;
	private ItemDropOverlay       _itemOverlay     = null!;
	private StockpileOverlay      _stockpileOverlay = null!;   // v0.5.0 Phase 5A
	private OrderQueueOverlay     _orderQueueOverlay = null!;  // v0.5.2 chain-order waypoints
	private DragSelectionPreview  _dragPreview     = null!;
	// v0.3.24 — tabbed bottom shell + Smurfs roster + RTS box-select + flash.
	private BottomTabPanel        _bottomTabs      = null!;
	private SmurfRosterPanel      _roster          = null!;
	private SelectionBoxPreview   _selectBox       = null!;
	private OrderFeedbackOverlay  _orderFeedback   = null!;
	// v0.3.24 — multi-select replaces the v0.3.20 single string. HashSet so
	// repeat-adds during box-select are O(1) and lookup for "is X selected?"
	// is O(1). Mirrored into SmurfColonyView via SetSelection each time it
	// changes so the yellow selection ring tracks the colony view's draw.
	private readonly System.Collections.Generic.HashSet<string> _selectedSmurfs = new();
	// Name of the smurf currently selected (or null). Right-click move orders
	// target this smurf when set. Mirrors `VisualSmurf.Selected` in colony view.
	// _selectedSmurfName removed v0.3.24 — replaced by _selectedSmurfs HashSet above.

	private bool _returning;
	private bool _wasPausedBeforeMenu;
	private bool _colonyEverAlive;
	private bool _gameOverShown;

	// Three discrete zoom levels: village (see whole map), neighbourhood, individual.
	private static readonly float[] ZoomLevels   = { 0.55f, 1.0f, 2.0f };
	private                  int    _zoomIndex   = 1;   // start at neighbourhood

	public override void _Ready()
	{
		ProjectSettings.SetSetting("gui/timers/tooltip_delay_sec", 1.5);
		var ttCfg = new ConfigFile();
		if (ttCfg.Load("user://settings.cfg") == Error.Ok)
		{
			string ttSize = (string)ttCfg.GetValue("gameplay", "tooltip_size", "large");
			GetTree().Root.Theme = BuildTooltipTheme(SettingsPanel.TooltipFontSize(ttSize));

			int zoomSpeed = (int)(double)ttCfg.GetValue("gameplay", "zoom_speed", 5.0);
			_zoomFactor = SettingsPanel.ZoomFactorFromSpeed(zoomSpeed);
		}
		else
		{
			GetTree().Root.Theme = BuildTooltipTheme(SettingsPanel.TooltipFontSize("large"));
		}

		BuildGameWorld();   // Camera + tile map renderer (no map yet) + smurf view
		BuildUILayer();     // HUD + panels + browser (camera-exempt CanvasLayer)
		StartSim();

		MusicManager.Instance?.Play(MusicManager.Context.Peace);

		// If a map is already loaded into WorldState (e.g. loaded from a save slot before
		// the scene transition), initialise the renderer immediately without a loading screen.
		// Otherwise defer generation by one frame so the generating overlay can appear first.
		if (WorldState.Instance?.CurrentLocalMap != null)
		{
			var preloadedMap = WorldState.Instance.CurrentLocalMap;
			_mapRenderer.SetMap(preloadedMap);
			_designations.SetMap(preloadedMap);
			_itemOverlay.SetMap(preloadedMap);
			_stockpileOverlay.SetMap(preloadedMap);   // v0.5.0
			_colony.UpdateMapSize(preloadedMap);
			// Phase 3: bind the LocalMap to the sim thread so BehaviorSystem
			// can read terrain/vegetation and seed sim positions. Without this,
			// save-loaded games would never bind the map to the sim.
			_sim.BindLocalMap(preloadedMap);
			CallDeferred(MethodName.SeedColonyVisuals);
		}
		else
		{
			_generatingOverlay.Visible = true;
			CallDeferred(MethodName.GenerateAndInitMap);
		}
	}

	// Runs on the first deferred frame: generates the map on the main thread (safe for
	// FastNoiseLite), then hands it to the renderer and seeds the colony sprites.
	// The generating overlay is visible for the duration, giving the player visual feedback
	// on large maps where generation may take ~100 ms.
	private void GenerateAndInitMap()
	{
		WorldState.Instance?.EnsureDefaultMap();
		_generatingOverlay.Visible = false;

		var map = WorldState.Instance?.CurrentLocalMap;
		if (map != null)
		{
			_mapRenderer.SetMap(map);
			_designations.SetMap(map);
			_itemOverlay.SetMap(map);
			_stockpileOverlay.SetMap(map);   // v0.5.0
			// Re-centre the camera on the actual generated map. BuildGameWorld() used
			// DefaultWidth/DefaultHeight to initialise camera position, which can be
			// smaller than the generated map, leaving the camera stuck in the top-left.
			float mapW = map.Width  * LocalMap.TileSize;
			float mapH = map.Height * LocalMap.TileSize;
			_camera.Position = new Vector2(mapW / 2f, mapH / 2f);
			// Sync the colony view's wander bounds to the real map size.
			_colony.UpdateMapSize(map);
			// Phase 3: hand the LocalMap to the sim thread and re-seed sim
			// positions now that the real map exists for the cluster anchor.
			_sim.BindLocalMap(map);
		}

		SeedColonyVisuals();
	}

	// ── Scene assembly ─────────────────────────────────────────────────────────

	private void BuildGameWorld()
	{
		// Camera2D lives in the root; controls pan/zoom for everything below.
		_camera = new Camera2D { Name = "Camera" };
		_camera.Zoom = new Vector2(ZoomLevels[_zoomIndex], ZoomLevels[_zoomIndex]);
		var localMap = WorldState.Instance?.CurrentLocalMap;
		float mapW = (localMap?.Width  ?? LocalMap.DefaultWidth)  * LocalMap.TileSize;
		float mapH = (localMap?.Height ?? LocalMap.DefaultHeight) * LocalMap.TileSize;
		_camera.Position = new Vector2(mapW / 2f, mapH / 2f);
		AddChild(_camera);

		// Tile map drawn first so smurfs appear on top.
		// Map is NOT assigned here — SetMap() is called after generation completes.
		_mapRenderer = new LocalMapRenderer { Name = "MapRenderer" };
		AddChild(_mapRenderer);

		// v0.5.0 (Phase 5A) — stockpile zone fill. v0.5.6 — moved up in
		// tree order to render right after MapRenderer (was added below
		// Designations and used ZIndex=-1, which in Godot 4 means UNDER
		// siblings — so the map tiles hid the yellow tint entirely).
		// Now at the same z=0 as everything else; tree order puts the
		// tint ON the floor but BENEATH designation glyphs / item icons /
		// selection brackets, which is the original design intent.
		_stockpileOverlay = new StockpileOverlay { Name = "StockpileOverlay" };
		AddChild(_stockpileOverlay);

		// v0.3.21 — designation overlay (Node2D in world space). Draws coloured
		// glyphs on every tile flagged for Excavate / Gather. Added after the
		// map but before the colony so smurfs render on top of the markers.
		_designations = new DesignationOverlay { Name = "Designations" };
		AddChild(_designations);

		// v0.4.2 — on-tile item drops. Sits between designation glyphs
		// (z=0) and the smurf colony view (z=1) so smurfs walking over
		// items visually obscure them. Reads from LocalMap.ItemsChanged.
		_itemOverlay = new ItemDropOverlay { Name = "ItemDrops" };
		AddChild(_itemOverlay);

		// v0.4.47 — single-tile selection brackets (RimWorld style) drawn
		// over the map and items but under the smurf colony, so a
		// selected tile reads with the bracket frame around any items
		// dropped on it.
		_selOverlay = new SelectionOverlay { Name = "SelectionOverlay" };
		AddChild(_selOverlay);

		// v0.5.2 — RTS chain-order waypoint visualisation. Cyan dots +
		// connecting line at each pending shift+right-click destination
		// for selected smurfs only.
		_orderQueueOverlay = new OrderQueueOverlay { Name = "OrderQueueOverlay" };
		AddChild(_orderQueueOverlay);

		// v0.3.21 — live drag rectangle. Visible only while the player is
		// holding the mouse during a designation drag.
		_dragPreview = new DragSelectionPreview { Name = "DragPreview" };
		AddChild(_dragPreview);

		// v0.3.24 — RTS box-select rectangle (visible only during smurf-
		// selection drag, distinct from the designation drag preview).
		_selectBox = new SelectionBoxPreview { Name = "SelectBox" };
		AddChild(_selectBox);

		// v0.3.24 — short-lived flash/ring feedback for each player order.
		_orderFeedback = new OrderFeedbackOverlay { Name = "OrderFeedback" };
		AddChild(_orderFeedback);

		// Generating overlay: shown while map generation runs on large maps.
		_generatingOverlay = BuildGeneratingOverlay();
		AddChild(_generatingOverlay);

		// Smurf colony view drawn over the tile map.
		_colony = new SmurfColonyView { Name = "Colony" };
		AddChild(_colony);
	}

	private static Control BuildGeneratingOverlay()
	{
		var overlay = new ColorRect
		{
			Name    = "GeneratingOverlay",
			Color   = new Color(0.04f, 0.07f, 0.10f, 1f),
			Visible = false,
		};
		overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

		var center = new CenterContainer();
		center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		overlay.AddChild(center);

		var lbl = new Label
		{
			Text = "Generating level…",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		lbl.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.72f));
		lbl.AddThemeFontSizeOverride("font_size", 28);
		center.AddChild(lbl);

		return overlay;
	}

	private void BuildUILayer()
	{
		// v0.3.19: stop scaling the CanvasLayer transform. Instead, push the
		// saved UI scale into `UITheme.UIScale` so each Phase-3.x component
		// reads it at construction time and scales its own font sizes /
		// minimum sizes — anchors stay at viewport edges, only content shrinks.
		float scale = SettingsPanel.LoadSavedUIScale();
		SmurfulationC.UI.UITheme.SetUIScale(scale);

		// Two-layer canvas: gameplay UI on UILayer (10), modal overlays on
		// ModalLayer (20). Higher Layer values render on top, so every overlay
		// panel automatically sits above the HUD / alerts pane / toolbar /
		// tile-info overlay. v0.3.18 had everything on UILayer and the order-
		// of-addition determined z-order, which let `ResourceHUD` render on
		// top of `SettingsPanel` because it was added later. The two-layer
		// split makes the precedence structural and impossible to break.
		var ul = new CanvasLayer { Name = "UILayer",    Layer = 10 };
		var ml = new CanvasLayer { Name = "ModalLayer", Layer = 20 };
		AddChild(ul);
		AddChild(ml);

		// ── Gameplay UI (lower layer) ─────────────────────────────────────
		// `_hud.Sim` is assigned later in StartSim() after the SimulationManager
		// is constructed — BuildUILayer runs before StartSim.
		_hud = new HUDController { Name = "HUD" };
		ul.AddChild(_hud);

		_card = new SmurfCardPanel { Name = "SmurfCard" };
		ul.AddChild(_card);

		_tileInfo = new TileInfoOverlay { Name = "TileInfo" };
		ul.AddChild(_tileInfo);

		// v0.4.32 — floating Developer Mode panel. Hidden unless DevMode
		// is on (toggled in Settings). F12 toggles its visibility on top
		// of that. Plugs `Sim`, the map node (for cursor → tile), and a
		// callback that returns the current selection so it doesn't need
		// a hard reference back to GameController fields.
		_devPanel = new SmurfulationC.UI.DevPanel { Name = "DevPanel" };
		ul.AddChild(_devPanel);
		// v0.4.55 — `_devPanel.Sim` is assigned in StartSim() alongside
		// the other panels' Sim bindings (HUD / Resources / Jobs / Card).
		// BuildUILayer runs BEFORE StartSim, so `_sim` is still null
		// here — the old `_devPanel.Sim = _sim` baked that null in
		// permanently, making every Sim?.Dev* call inside the panel
		// return null. ForEachSelected then silently logged "Drained
		// needs ×1" while the action was a no-op, which is what
		// produced Sam's "I clicked Drain 10 times and nothing happened"
		// report. The v0.4.54 hit/miss diagnostic surfaced this as
		// "Smurf not found ×1" once it could see the null result;
		// rather than keep the diagnostic and limp along with a broken
		// binding, the binding itself is moved to StartSim where `_sim`
		// is actually populated. MapNode and GetSelectedSmurfs are set
		// here because they don't depend on the sim instance.
		_devPanel.MapNode = _mapRenderer;
		_devPanel.GetSelectedSmurfs = () => _selectedSmurfs;

		// v0.4.34 — stationary-target inspector. Floating card mirroring
		// SmurfCardPanel; opens on left-click on a tile with items or
		// vegetation when no smurf was hit. Snapshot-only — no per-tick
		// refresh — so a stale view is the trade-off for zero idle cost.
		_tileProps = new TilePropertiesPanel { Name = "TilePropertiesPanel" };
		ul.AddChild(_tileProps);

		_msgLog = new MessageLog { Name = "MessageLog" };
		ul.AddChild(_msgLog);

		// v0.3.24 — bottom-anchored tabbed shell. Two tabs:
		//   • Orders   → embedded DesignationToolbar (existing buttons).
		//   • Smurfs   → new SmurfRosterPanel (roster list, double-click = zoom).
		// The container does the bottom-centre anchoring; the toolbar fills
		// its tab slot rather than self-anchoring like it used to.
		_toolbar         = new DesignationToolbar { Name = "DesignationToolbar" };
		_roster          = new SmurfRosterPanel   { Name = "SmurfRoster" };
		_resourcesPanel  = new ResourcesPanel     { Name = "ResourcesPanel" };
		_jobsPanel       = new JobsPanel          { Name = "JobsPanel" };
		_bottomTabs      = new BottomTabPanel     { Name = "BottomTabs" };
		ul.AddChild(_bottomTabs);
		_bottomTabs.Attach(_toolbar, _roster, _resourcesPanel, _jobsPanel);
		_roster.SmurfZoomRequested   += OnRosterZoomRequested;
		_roster.SmurfSelectRequested += OnRosterSelectRequested;

		// ── Modal overlays (higher layer) ─────────────────────────────────
		_pauseMenu = new PauseMenuPanel { Name = "PauseMenu" };
		_pauseMenu.ResumeRequested   += OnPauseMenuResume;
		_pauseMenu.SaveRequested     += OnPauseMenuSave;
		_pauseMenu.LoadRequested     += OnPauseMenuLoad;
		_pauseMenu.SettingsRequested += OnPauseMenuSettings;
		_pauseMenu.ExitRequested     += OnPauseMenuExit;
		ml.AddChild(_pauseMenu);

		_settings = new SettingsPanel { Name = "SettingsPanel" };
		_settings.SettingsClosed += ApplyRuntimeSettings;
		ml.AddChild(_settings);

		_saveBrowser = new SaveFileBrowser { Name = "SaveBrowser" };
		_saveBrowser.SaveConfirmed += OnBrowserSaveConfirmed;
		_saveBrowser.LoadConfirmed += OnBrowserLoadConfirmed;
		_saveBrowser.Cancelled     += () => { _saveBrowser.Close(); _pauseMenu.Open(); };
		ml.AddChild(_saveBrowser);

		_savingOverlay = BuildSavingOverlay();
		ml.AddChild(_savingOverlay);

		_gameOver = new GameOverPanel { Name = "GameOver" };
		ml.AddChild(_gameOver);

		_alertsPane = new AlertsPane { Name = "AlertsPane" };
		ml.AddChild(_alertsPane);

		_colony.SmurfClicked += OnSmurfClicked;

		_card.RoleChangeRequested += (name, newRole) =>
			_sim.RequestRoleChange(name, newRole);
	}

	// Roadmap §3.x.3 / v0.3.21 — designation input dispatcher.
	//
	// Right-click is always a move-order on the currently-selected smurf,
	// independent of the active tool (RimWorld convention — right-click is
	// the universal "do this with the selected pawn" verb).
	//
	// Left-click is tool-aware:
	//   • Tool.None      → falls through to SmurfColonyView selection.
	//   • Tool.Gather    → drag-box paints DesignatedForGather on every food-
	//                      yielding plant inside the rect on mouse-up.
	//   • Tool.Excavate  → drag-box paints DesignatedForExcavation on every
	//                      Boulder / DeadLog / LivingWood in the rect.
	//   • Tool.Remove    → drag-box wipes both designation flags in the rect.
	//
	// Single-cell clicks (press + release at the same tile, no drag) commit a
	// 1×1 rect — convenient for fixing up one tile after a sweep.
	private (int x, int y)? MouseTile(Vector2 mousePos)
	{
		var map = WorldState.Instance?.CurrentLocalMap;
		if (map == null) return null;
		int tx = (int)(mousePos.X / LocalMap.TileSize);
		int ty = (int)(mousePos.Y / LocalMap.TileSize);
		if (!map.InBounds(tx, ty)) return null;
		return (tx, ty);
	}

	// v0.3.24 — left-click semantics depend on what's under the cursor and
	// what tool is active:
	//   • No tool, click on a smurf       → single-select (clears others).
	//   • No tool, drag on empty terrain  → RTS box-select (multi).
	//   • Tool active                     → designation drag-box (existing).
	//
	// Right-click semantics with one-or-more smurfs selected:
	//   • Right-click on enemy            → combat order stub (Phase 8 stub).
	//   • Right-click on passable tile    → move order to all selected.
	// v0.4.3 — context-aware right-click action descriptor. Resolved by
	// `ResolveRightClickActions` for the (tile, click-world-pos) under
	// the cursor; each entry has a label, an icon glyph, and an
	// Execute callback that dispatches the action through the existing
	// SimulationManager APIs. The first entry is the auto-fire default
	// when Alt is not held.
	private sealed class RightClickAction
	{
		public string       Label   = "";
		public string       Glyph   = "";
		public System.Action Execute = () => { };
	}

	private System.Collections.Generic.List<RightClickAction> ResolveRightClickActions(
		(int x, int y) tile, Vector2 worldPos,
		SmurfulationC.World.LocalMap map)
	{
		var list = new System.Collections.Generic.List<RightClickAction>();

		// Pick-up item on this tile.
		var items = map.GetItemsOnTile(tile.x, tile.y);
		if (items.Count > 0)
		{
			var first = items[0];
			var subDef = SmurfulationC.Simulation.Items.ItemRegistry.Get(first.Kind, first.SubType);
			string display = subDef?.DisplayName ?? first.SubType;
			list.Add(new RightClickAction
			{
				Label = $"Pick up {display}",
				Glyph = "📦",
				Execute = () =>
				{
					foreach (var name in _selectedSmurfs)
						_sim.RequestPickUp(name, worldPos);
					_orderFeedback.RingPickUp(worldPos);
				},
			});

			// v0.5.0 (Phase 5A — rimport N5) — Forbid / Allow toggle on
			// every item on the tile. Toggles ALL items on the tile in one
			// action (matches RimWorld's right-click "Forbid all" UX).
			// State of the toggle reads from the first item; if any items
			// are unforbidden, the action forbids all; if all are
			// forbidden, the action allows all.
			bool anyAllowed = false;
			for (int i = 0; i < items.Count; i++)
				if (!items[i].IsForbidden) { anyAllowed = true; break; }
			string label = anyAllowed ? $"Forbid {display}" : $"Allow {display}";
			string glyph = anyAllowed ? "🚫" : "✓";
			bool target = anyAllowed;   // capture for the lambda
			list.Add(new RightClickAction
			{
				Label = label,
				Glyph = glyph,
				Execute = () =>
				{
					_sim.SetForbiddenOnTile(tile.x, tile.y, target);
					// v0.5.1 — visible feedback ring on every Forbid/Allow toggle.
					if (target) _orderFeedback.RingForbid(worldPos);
					else        _orderFeedback.RingAllow(worldPos);
				},
			});
		}

		// Move to tile (always available if passable).
		if (map.IsPassable(tile.x, tile.y))
		{
			list.Add(new RightClickAction
			{
				Label = items.Count > 0 ? "Move to tile" : "Move here",
				Glyph = "→",
				Execute = () =>
				{
					_sim.RequestPlayerMoveOrderGroup(_selectedSmurfs, worldPos);
					_orderFeedback.RingMove(worldPos);
				},
			});
		}

		// Force craft on a workbench — Phase 5 stub. No workbenches exist
		// today, so this branch never fires. Once StructureSlot's
		// FurnitureType includes Workbench/Loom/etc., add an
		// `if (map.IsWorkbenchAt(...))` branch here.

		return list;
	}

	// v0.4.3 — RimWorld-style right-click context menu. Pops a small
	// floating panel at the cursor with each available action as a
	// button. Disposes itself on selection or click-outside.
	private PopupPanel? _contextMenu;
	private void OpenContextMenu(Vector2 viewportPos,
		System.Collections.Generic.List<RightClickAction> actions)
	{
		_contextMenu?.QueueFree();
		var popup = new PopupPanel();
		var box = new VBoxContainer();
		box.AddThemeConstantOverride("separation", 2);
		var pad = new MarginContainer();
		pad.AddThemeConstantOverride("margin_left",   6);
		pad.AddThemeConstantOverride("margin_right",  6);
		pad.AddThemeConstantOverride("margin_top",    6);
		pad.AddThemeConstantOverride("margin_bottom", 6);
		popup.AddChild(pad);
		pad.AddChild(box);

		foreach (var act in actions)
		{
			var btn = new Button
			{
				Text              = $"{act.Glyph}  {act.Label}",
				CustomMinimumSize = new Vector2(180, 24),
				FocusMode         = Control.FocusModeEnum.None,
			};
			btn.AddThemeFontSizeOverride("font_size", 11);
			btn.AddThemeStyleboxOverride("normal",  FloatingPanelStyle.MakeToolbarButton(false));
			btn.AddThemeStyleboxOverride("hover",   FloatingPanelStyle.MakeToolbarButton(false));
			btn.AddThemeStyleboxOverride("pressed", FloatingPanelStyle.MakeToolbarButton(true));
			btn.AddThemeColorOverride("font_color",       UITheme.TextPrimary);
			btn.AddThemeColorOverride("font_hover_color", UITheme.TextAccent);
			var captured = act;
			btn.Pressed += () =>
			{
				captured.Execute?.Invoke();
				popup.Hide();
				popup.QueueFree();
				_contextMenu = null;
			};
			box.AddChild(btn);
		}

		AddChild(popup);
		_contextMenu = popup;
		// Open at the cursor position. PopupPanel takes a Rect2I in
		// screen coords; viewportPos is already in viewport space.
		popup.Position = new Vector2I((int)viewportPos.X, (int)viewportPos.Y);
		popup.Popup();
	}

	private bool TryHandleMouseButton(InputEventMouseButton mb)
	{
		var map = WorldState.Instance?.CurrentLocalMap;
		if (map == null) return false;
		var click = _camera != null ? _camera.GetGlobalMousePosition() : mb.Position;
		var tile = MouseTile(click);

		// ── Right-click — defer order until release so right-DRAG can pan ─
		// v0.3.30 — Camera pan is now right-drag (was left-drag). To keep the
		// "right-click on terrain = issue order" behaviour, we record the
		// press position and let `PanWithMouseDrag` decide whether the gesture
		// became a pan. On release, if the player never crossed the pan
		// threshold AND smurfs are selected, we issue the move order at the
		// release tile. If they did pan, no order fires.
		if (mb.ButtonIndex == MouseButton.Right)
		{
			if (mb.Pressed)
			{
				_rightDownPos = GetViewport().GetMousePosition();
				_rightPanning = false;
				_lastMouseScreenPos = _rightDownPos.Value;
				return true;
			}
			// Release
			var wasPanning = _rightPanning;
			_rightDownPos = null;
			_rightPanning = false;
			if (wasPanning) return true;  // pan completed; no action — preserves right-drag camera pan

			// v0.5.2 — RimWorld-style stationary-right-click priority cascade.
			// Sam follow-up: deselecting smurfs / closing panels via
			// right-click would break the planned RTS combat controls
			// (right-click on enemy = attack), so right-click cancellation
			// is now SCOPED to the toolbar tool only — never to the
			// selection or panels. Two priorities:
			//
			//   1. Active toolbar tool → cancel it (deselect tool / zone).
			//      Matches RimWorld's "right-click cancels designator"
			//      behaviour, applied to both Orders-tab and Zones-tab
			//      tools (single source of active-tool truth via
			//      DesignationToolbar.SetActiveTool).
			//   2. Smurf(s) selected + actionable tile → context-action
			//      (move / pickup / forbid). Selection persists across the
			//      order so the player can chain commands.
			//
			// SHIFT + right-click variant: when shift is held during the
			// release, the order is QUEUED via the new chain-order API
			// (Smurf.MoveOrderQueue) instead of replacing the smurf's
			// CurrentTask. Standard RTS pattern (StarCraft / Warcraft):
			// shift-click queues, plain click replaces + clears queue.
			//
			// Right-DRAG that crossed the pan threshold is intercepted at
			// `wasPanning` above, so the cascade only runs for
			// genuinely-stationary right-clicks. Camera pan workflow
			// unaffected. Smurf selection + open panels are NEVER cleared
			// by right-click (per Sam's feedback) — the player uses
			// left-click on empty space (or future Esc binding) for that.

			// Priority 1 — cancel active tool.
			if (_toolbar.ActiveTool != DesignationToolbar.Tool.None)
			{
				// SetActiveTool(currentTool) toggles to None per the
				// toolbar's click-twice-to-deselect convention.
				_toolbar.SetActiveTool(_toolbar.ActiveTool);
				return true;
			}

			// Priority 2 — context action with selected smurfs.
			if (_selectedSmurfs.Count > 0
				&& tile.HasValue
				&& map.IsPassable(tile.Value.x, tile.Value.y))
			{
				bool shiftHeld = Input.IsKeyPressed(Key.Shift);

				// v0.4.3 — context-aware right-click. Resolve the available
				// actions for this (tile, world-state) combo; if the
				// context-menu hotkey is held (default Alt, rebindable in
				// Settings → Keybindings) AND multiple actions are
				// available, surface a context menu the player can pick
				// from. Otherwise execute the top-priority action
				// automatically.
				var actions = ResolveRightClickActions(tile.Value, click, map);
				bool altHeld = Input.IsActionPressed("kb_context_menu")
							   || Input.IsKeyPressed(Key.Alt);
				if (altHeld && actions.Count > 1)
				{
					OpenContextMenu(GetViewport().GetMousePosition(), actions);
					return true;
				}
				if (actions.Count > 0)
				{
					// v0.5.2 — shift-click → queue Move order instead of
					// replacing CurrentTask. Today only Move is queueable
					// (Pick-up / Forbid stay one-shot); a richer queued-
					// order type lands when combat orders need queueing.
					if (shiftHeld)
					{
						_sim.RequestPlayerMoveOrderGroupQueued(_selectedSmurfs, click);
						_orderFeedback.RingMove(click);
					}
					else
					{
						actions[0].Execute();
					}
					return true;
				}
			}

			// No active tool, no actionable target — right-click is a no-op.
			// Player must use left-click on empty space to deselect smurfs
			// (preserves RTS combat-control plan where right-click on enemy
			// = attack, never deselect).
			return false;
		}

		if (mb.ButtonIndex != MouseButton.Left) return false;

		var desig = _toolbar.ActiveDesignation;

		// ── Tool active → designation drag-box (existing behaviour) ───────
		if (desig != SmurfulationC.UI.DesignationTool.None && tile.HasValue)
		{
			if (mb.Pressed)
			{
				_dragPreview.Begin(desig, tile.Value.x, tile.Value.y);
				return true;
			}
			if (_dragPreview.DragActive)
			{
				_dragPreview.Update(tile.Value.x, tile.Value.y);
				var (x0, y0, x1, y1) = _dragPreview.GetRect();
				_sim.DesignateRect(_dragPreview.Tool, x0, y0, x1, y1);
				// v0.4.27 — force the overlay to ingest the new tiles
				// inside the same input frame. `DesignateRect` writes
				// the designation flags directly on the main thread now
				// (v0.4.27); `RebuildIfDirty` flushes the MMI instance
				// buffers so the very next render shows the full drag
				// rect instead of waiting up to a `_Process` frame.
				_designations.RebuildIfDirty();
				_orderFeedback.FlashDesignationRect(_dragPreview.Tool, x0, y0, x1, y1);
				_dragPreview.End();
				return true;
			}
			return false;
		}

		// ── No tool active → smurf selection (single-click or box-drag) ───
		if (mb.Pressed)
		{
			var hit = _colony.GetSmurfNameAt(click);
			if (hit != null)
			{
				SelectSingleSmurf(hit);
				_colony.EmitSmurfClicked(hit);  // existing wiring opens SmurfCardPanel
				return true;
			}
			// v0.4.34 — no smurf hit. If the clicked tile has items or
			// vegetation, open the stationary-inspector card; otherwise
			// fall through to the existing select-box drag and close any
			// inspector that was open from a previous click.
			if (tile.HasValue)
			{
				var lmap = WorldState.Instance?.CurrentLocalMap;
				if (lmap != null)
				{
					bool hasItems = lmap.GetItemsOnTile(tile.Value.x, tile.Value.y).Count > 0;
					bool hasVeg   = lmap.GetVegetation(tile.Value.x, tile.Value.y).IsPresent;
					if (hasItems || hasVeg)
					{
						_card.Visible = false;        // mutually exclusive with the smurf card
						_tileProps.Open(tile.Value.x, tile.Value.y, lmap);
						_selOverlay.SetTileSelection(tile.Value.x, tile.Value.y);
						return true;
					}
				}
			}
			_tileProps.Close();
			_selOverlay.ClearSelection();
			// v0.4.36 — Ctrl-gate the box-select drag (settings toggle,
			// default ON). Without Ctrl held, an empty left-drag just
			// clears the selection instead of roping a dozen smurfs into
			// an accidental crowd order. Single-smurf left-clicks still
			// work either way (the smurf-hit branch above already
			// returned).
			if (SettingsPanel.BoxSelectRequiresCtrl
				&& !Input.IsKeyPressed(Key.Ctrl))
			{
				ClearSelection();
				return true;
			}
			_selectBox.Begin(click);
			return true;
		}
		// Left-release with an active select-box → commit multi-selection.
		if (_selectBox.DragActive)
		{
			_selectBox.Update(click);
			var rect = _selectBox.GetWorldRect();
			_selectBox.End();
			if (rect.Size.X < 4f && rect.Size.Y < 4f)
			{
				ClearSelection();
				return true;
			}
			var names = _colony.GetSmurfNamesInRect(rect);
			ClearSelection();
			foreach (var n in names) _selectedSmurfs.Add(n);
			_colony.SetSelection(_selectedSmurfs);
			_orderQueueOverlay?.SetSelection(_selectedSmurfs);   // v0.5.2
			return true;
		}
		return false;
	}

	private void HandleMouseMotion(InputEventMouseMotion mm)
	{
		// v0.3.39 (O-L.1) — single early-out before the world-pos resolve.
		// Saves a Camera2D global-position read on every mouse-motion event
		// when no drag is in progress (the common case).
		if (!_dragPreview.DragActive && !_selectBox.DragActive) return;
		var world = _camera != null ? _camera.GetGlobalMousePosition() : mm.Position;
		if (_dragPreview.DragActive)
		{
			var tile = MouseTile(world);
			if (tile.HasValue) _dragPreview.Update(tile.Value.x, tile.Value.y);
		}
		if (_selectBox.DragActive)
			_selectBox.Update(world);
	}

	// ── Selection plumbing (v0.3.24) ──────────────────────────────────────────

	private void SelectSingleSmurf(string name)
	{
		_selectedSmurfs.Clear();
		_selectedSmurfs.Add(name);
		_colony.SetSelection(_selectedSmurfs);
		_orderQueueOverlay?.SetSelection(_selectedSmurfs);   // v0.5.2
	}

	private void ClearSelection()
	{
		_selectedSmurfs.Clear();
		_colony.SetSelection(_selectedSmurfs);
		_orderQueueOverlay?.SetSelection(_selectedSmurfs);   // v0.5.2
	}

	private void OnRosterZoomRequested(string name)
	{
		var pos = _colony.GetSmurfPosition(name);
		if (pos.HasValue)
		{
			_camera.Position = pos.Value;
			ClampCamera();
			_orderFeedback.RingMove(pos.Value);
		}
	}

	private void OnRosterSelectRequested(string name)
	{
		SelectSingleSmurf(name);
		_colony.EmitSmurfClicked(name);
	}

	private static Control BuildSavingOverlay()
	{
		var overlay = new Control { Name = "SavingOverlay" };
		overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		overlay.MouseFilter = Control.MouseFilterEnum.Stop;
		overlay.Visible  = false;
		overlay.Modulate = Colors.Transparent;

		var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f) };
		bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		overlay.AddChild(bg);

		var center = new CenterContainer();
		center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		overlay.AddChild(center);

		var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
		vbox.AddThemeConstantOverride("separation", 12);
		center.AddChild(vbox);

		var star = new Label { Text = "★", HorizontalAlignment = HorizontalAlignment.Center };
		star.AddThemeColorOverride("font_color", new Color(0.95f, 0.80f, 0.28f));
		star.AddThemeFontSizeOverride("font_size", 48);
		ApplyGrobold(star);
		vbox.AddChild(star);

		var lbl = new Label { Text = "Saving...", HorizontalAlignment = HorizontalAlignment.Center };
		lbl.AddThemeColorOverride("font_color", new Color(0.96f, 0.91f, 0.72f));
		lbl.AddThemeFontSizeOverride("font_size", 72);
		ApplyGrobold(lbl);
		vbox.AddChild(lbl);

		return overlay;
	}

	private static void ApplyGrobold(Label l)
	{
		const string font = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(font))
			l.AddThemeFontOverride("font", GD.Load<FontFile>(font));
	}

	public static Theme BuildTooltipTheme(int fontSize = 10)
	{
		var theme = new Theme();

		var panel = new StyleBoxFlat { BgColor = new Color(0.12f, 0.12f, 0.12f, 0.97f), ShadowSize = 0 };
		panel.SetBorderWidthAll(1);
		panel.BorderColor = new Color(0.32f, 0.32f, 0.32f, 1f);
		panel.ContentMarginLeft   = 6f;
		panel.ContentMarginRight  = 6f;
		panel.ContentMarginTop    = 4f;
		panel.ContentMarginBottom = 4f;
		theme.SetStylebox("panel", "TooltipPanel", panel);

		theme.SetFontSize("font_size", "TooltipLabel", fontSize);
		theme.SetColor("font_color",    "TooltipLabel", new Color(0.88f, 0.86f, 0.80f));
		theme.SetColor("font_shadow_color", "TooltipLabel", new Color(0f, 0f, 0f, 0.5f));
		theme.SetConstant("shadow_offset_x", "TooltipLabel", 1);
		theme.SetConstant("shadow_offset_y", "TooltipLabel", 1);

		return theme;
	}

	// ── Simulation wiring ──────────────────────────────────────────────────────

	private void StartSim()
	{
		_sim = new SimulationManager { Name = "SimulationManager" };
		AddChild(_sim);

		_hud.Sim = _sim;
		_resourcesPanel.Sim = _sim;
		_jobsPanel.Sim = _sim;
		_card.Sim = _sim;
		// v0.4.55 — bind the dev panel here (after _sim exists) instead of
		// in BuildUILayer where _sim was still null. See note at the
		// _devPanel construction site.
		_devPanel.Sim = _sim;
		// v0.3.47 — register the live SimulationManager with SaveManager
		// so save writes can pull the colony inventory + work priorities.
		SaveManager.Instance?.RegisterSimulation(_sim);
		_hud.MenuRequested += OnMenuRequested;

		var cfg = new ConfigFile();
		bool pauseOnStart = cfg.Load("user://settings.cfg") == Error.Ok
			? (bool)cfg.GetValue("gameplay", "pause_on_start", true)
			: true;
		if (pauseOnStart)
		{
			_sim.Paused = true;
			_hud.SyncPauseButton();
		}

		_sim.Connect(SimulationManager.SignalName.TickCompleted,
			Callable.From<string, int, int, int>(OnTick));

		_sim.Connect(SimulationManager.SignalName.SmurfDied,
			Callable.From<string, int, string>(OnSmurfDied));

		_sim.Connect(SimulationManager.SignalName.BirthOccurred,
			Callable.From<string, string>(OnBirthOccurred));

		// v0.3.47 — wandering-in event surfaces in the alerts pane.
		_sim.Connect(SimulationManager.SignalName.WandererArrived,
			Callable.From<string, string, string, int>(OnWandererArrived));

		_sim.Connect(SimulationManager.SignalName.YearTicked,
			Callable.From<int>(OnYearTicked));

		_sim.Connect(SimulationManager.SignalName.MoodThresholdCrossed,
			Callable.From<string, string, string>(OnMoodThresholdCrossed));
	}

	private void SeedColonyVisuals()
	{
		var roster = new List<(string name, string role)>();
		Dictionary<string, (Godot.Vector2, Godot.Vector2)>? positions = null;

		if (SaveManager.Instance?.HasSave == true && SaveManager.Instance.CurrentSave is { } save)
		{
			positions = new();
			foreach (var s in save.Smurfs)
			{
				roster.Add((s.Name, s.Role));
				positions[s.Name] = (new Godot.Vector2(s.PosX, s.PosY),
									 new Godot.Vector2(s.TargetX, s.TargetY));
			}
		}
		else
		{
			// Read the LIVE sim roster instead of the founding-seven hard-coded
			// names that v0.3.13 inherited from the pre-scenario placeholder.
			// SimulationManager.SeedColony has already run by this point and has
			// either materialised the scenario's smurfs or fallen back to the
			// founding seven itself, so this query is the authoritative source.
			var snap = _sim.GetLastSnapshot();
			if (snap != null)
			{
				foreach (var s in snap.Smurfs) roster.Add((s.Name, s.Role));
			}
			// If no snapshot has arrived yet (paused-on-start before first tick),
			// fall back to the founding seven so we never seed an empty roster.
			if (roster.Count == 0)
			{
				roster = new List<(string, string)>
				{
					("Papa",      "Elder"),
					("Brainy",    "Scholar"),
					("Hefty",     "Guardian"),
					("Smurfette", "Caretaker"),
					("Clumsy",    "Forager"),
					("Handy",     "Crafter"),
					("Grouchy",   "Forager"),
				};
			}
		}

		_colony.SeedSmurfs(roster, positions);
	}

	public override void _Process(double delta)
	{
		// v0.4.43 — re-assert pause every frame the menu is open. Sam
		// reported the pause menu no longer pausing the game; rather
		// than hunt for the rogue setter that's unpausing behind the
		// scenes, stamp Paused = true while the menu is visible so the
		// player can rely on "menu open = sim halted" regardless of
		// what else is touching the flag. OnPauseMenuResume restores
		// _wasPausedBeforeMenu on close, so this re-assertion doesn't
		// stick after the player resumes.
		if (_pauseMenu.Visible) _sim.Paused = true;

		_colony.SpeedMultiplier = _sim.SpeedMultiplier;
		_colony.Paused          = _sim.Paused;
		UpdateTileInfo();
		PanWithKeys((float)delta);
		PanWithMouseDrag();
		FinalizeStrandedDrag();
		// v0.3.39 (O-H.2) — push camera position to sim thread for LOD
		// tick-phase assignment. Sim reads on its phase-assign schedule
		// (~every 32 ticks); brief tearing is acceptable since the band
		// thresholds are 320 px wide.
		if (_camera != null) _sim.SetCameraFollow(_camera.Position);
	}

	// v0.3.21 — if the player drags a designation box and releases the mouse
	// over the toolbar (or any other Stop-mouse-filter control), the release
	// event is swallowed before _UnhandledInput sees it and DragSelectionPreview
	// would stay visible forever. Poll the mouse state each frame: when drag is
	// active but the left button is no longer pressed, commit the rect using
	// the last tile the preview was updated to and tear it down.
	private void FinalizeStrandedDrag()
	{
		if (!_dragPreview.DragActive) return;
		// If a modal opened mid-drag, discard the rectangle silently rather
		// than committing — the player wasn't paying attention to it.
		if (IsAnyOverlayOpen())
		{
			_dragPreview.End();
			return;
		}
		if (Input.IsMouseButtonPressed(MouseButton.Left)) return;
		var (x0, y0, x1, y1) = _dragPreview.GetRect();
		// v0.3.23 — same fix as TryHandleMouseButton: commit with the drag's
		// own tool, not the toolbar's current selection. This matters
		// especially here because if the player released on the toolbar
		// while clicking a *different* tool button (which is the only way
		// to land here without TryHandleMouseButton seeing the release),
		// ActiveDesignation has already moved to the new tool.
		var tool = _dragPreview.Tool;
		if (tool != SmurfulationC.UI.DesignationTool.None)
		{
			_sim.DesignateRect(tool, x0, y0, x1, y1);
			_designations.RebuildIfDirty();   // v0.4.27 — instant visual
			_orderFeedback.FlashDesignationRect(tool, x0, y0, x1, y1);  // v0.3.24
		}
		_dragPreview.End();
	}

	private void PanWithKeys(float delta)
	{
		if (IsAnyOverlayOpen()) return;
		var dir = Vector2.Zero;
		if (Input.IsKeyPressed(Key.W)) dir.Y -= 1f;
		if (Input.IsKeyPressed(Key.S)) dir.Y += 1f;
		if (Input.IsKeyPressed(Key.A)) dir.X -= 1f;
		if (Input.IsKeyPressed(Key.D)) dir.X += 1f;
		if (dir == Vector2.Zero) return;
		// Divide by zoom so WASD covers the same screen area at any zoom level.
		_camera.Position += dir.Normalized() * PanSpeed / _camera.Zoom.X * delta;
		ClampCamera();
	}

	private void PanWithMouseDrag()
	{
		// v0.3.30 — Camera pan is now right-drag (was left-drag) so left-drag
		// is free for the box-selection mechanic and designation drag-box.
		// Right-press alone is a move/combat order; the user has to move the
		// cursor more than `RightDragPanThreshold` px before we commit to a
		// pan. That way short right-clicks still route to TryHandleMouseButton
		// on release for the order path.
		if (IsAnyOverlayOpen() || _rightDownPos == null)
		{
			return;
		}
		var mousePos = GetViewport().GetMousePosition();
		if (!Input.IsMouseButtonPressed(MouseButton.Right))
		{
			// Stranded release case — the release event was consumed by a
			// Control (e.g. the player dragged onto the bottom tab bar then
			// let go). TryHandleMouseButton won't fire; clear state here so
			// the next press starts clean and we don't accidentally issue
			// an order for the now-stale press position.
			_rightDownPos = null;
			_rightPanning = false;
			return;
		}

		if (!_rightPanning)
		{
			float drift = (mousePos - _rightDownPos.Value).Length();
			if (drift > RightDragPanThreshold)
				_rightPanning = true;
		}

		if (_rightPanning)
		{
			_camera.Position -= (mousePos - _lastMouseScreenPos) / _camera.Zoom.X;
			ClampCamera();
		}
		_lastMouseScreenPos = mousePos;
	}

	private void UpdateTileInfo()
	{
		var map = WorldState.Instance?.CurrentLocalMap;
		if (map == null) { _tileInfo.Clear(); return; }

		// GetGlobalMousePosition() on a Node2D in the default canvas is already
		// camera-corrected — no manual transform inversion needed.
		var world = _mapRenderer.GetGlobalMousePosition();
		int tx = (int)(world.X / LocalMap.TileSize);
		int ty = (int)(world.Y / LocalMap.TileSize);

		if (!map.InBounds(tx, ty)) { _tileInfo.Clear(); return; }

		var biome = WorldState.Instance?.WorldMap != null
			? WorldState.Instance.WorldMap[
				WorldState.Instance.SelectedTileX,
				WorldState.Instance.SelectedTileY].Biome
			: BiomeType.Plains;

		// v0.4.30 — also pass dropped items so the hover shows the
		// full label of every stack on the tile (RimWorld convention).
		_tileInfo.ShowTile(map.Get(tx, ty), map.GetVegetation(tx, ty), biome,
			map.GetTileStone(tx, ty),
			map.GetItemsOnTile(tx, ty));
	}

	// ── Input ─────────────────────────────────────────────────────────────────

	private const float MinZoom    = 0.25f;
	private const float MaxZoom    = 4.0f;
	private const float PanSpeed   = 420f;    // screen-pixels/sec for WASD
	private const float RightDragPanThreshold = 4f;  // px before a right-press becomes a pan
	private       float _zoomFactor = 1.15f;  // overridden at startup from settings
	// _dragging removed v0.3.30 — was the left-drag pan latch; right-drag
	// now uses _rightDownPos / _rightPanning below.
	private       Vector2 _lastMouseScreenPos = Vector2.Zero;
	// v0.3.30 — right-drag pan state (was left-drag prior). _rightDownPos is
	// non-null while right is held; _rightPanning latches once the cursor has
	// moved more than RightDragPanThreshold from the press position. The
	// latch is what disambiguates "right-click to issue a move order" from
	// "right-drag to pan the camera" — TryHandleMouseButton checks it on the
	// release event.
	private Vector2? _rightDownPos;
	private bool     _rightPanning;

	// v0.4.18 — `_savingOverlay` added to the modal-overlay set. During
	// `OnPauseMenuExit`'s fade-in + save + post-save linger (~1+ second
	// of awaited time) the previous list omitted the saving overlay, so
	// Space-bar would route through `_Input` and toggle the sim back to
	// running mid-exit — defeating "pause and leave paused until exit".
	private bool IsAnyOverlayOpen() =>
		_pauseMenu.Visible || _settings.Visible || _saveBrowser.Visible
		|| _gameOver.Visible || _savingOverlay.Visible;

	private bool IsMouseOverCard() =>
		_card.Visible && _card.GetGlobalRect().HasPoint(GetViewport().GetMousePosition());

	// v0.3.28 — superset of IsMouseOverCard: returns true whenever the cursor
	// is over any non-modal in-game UI panel. Used to suppress the mouse-wheel
	// zoom path so scrolling over the Smurfs roster (or any other tab content)
	// doesn't yank the camera. Modal overlays already block input upstream via
	// IsAnyOverlayOpen — they don't need to be listed here.
	private bool IsMouseOverUI()
	{
		if (IsMouseOverCard()) return true;
		if (_bottomTabs != null && _bottomTabs.IsMouseOverContent()) return true;
		if (_hud != null && _hud.IsMouseOverBars()) return true;
		return false;
	}

	private void ClampCamera()
	{
		var localMap = WorldState.Instance?.CurrentLocalMap;
		if (localMap == null) return;
		float pad  = 12f * LocalMap.TileSize;
		float mapW = localMap.Width  * LocalMap.TileSize;
		float mapH = localMap.Height * LocalMap.TileSize;
		_camera.Position = new Vector2(
			Mathf.Clamp(_camera.Position.X, -pad, mapW + pad),
			Mathf.Clamp(_camera.Position.Y, -pad, mapH + pad));
	}

	// _Input fires before GUI processing, so SetInputAsHandled() prevents a focused
	// button from also receiving the spacebar and double-toggling pause.
	public override void _Input(InputEvent ev)
	{
		if (ev is InputEventKey { Keycode: Key.Space, Pressed: true, Echo: false }
			&& !IsAnyOverlayOpen())
		{
			_sim.TogglePause();
			_hud.SyncPauseButton();
			GetViewport().SetInputAsHandled();
		}
		// v0.4.32 — F12 toggles the floating Developer Mode panel when
		// dev mode is enabled in settings. Pressing it with dev mode OFF
		// is a no-op (panel stays hidden). Routed through _Input rather
		// than _UnhandledInput so it works even when an overlay is open.
		else if (ev is InputEventKey { Keycode: Key.F12, Pressed: true, Echo: false }
			&& DevMode.IsEnabled)
		{
			_devPanel?.HandleHotkey();
			GetViewport().SetInputAsHandled();
		}
		// v0.4.43 — Esc toggles the pause menu. PauseMenuPanel._UnhandledInput
		// already handles Esc-to-close (emits ResumeRequested), so this
		// branch only fires when the pause menu is NOT visible AND no other
		// overlay (Settings / SaveBrowser / GameOver) is on top. Settings
		// and SaveBrowser have their own Esc-to-close paths.
		else if (ev is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false }
			&& !IsAnyOverlayOpen())
		{
			OnMenuRequested();
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _UnhandledInput(InputEvent ev)
	{
		if (IsAnyOverlayOpen()) return;
		switch (ev)
		{
			case InputEventKey key when key.Keycode == Key.Tab && key.Pressed && !key.Echo:
				{
					_zoomIndex = (_zoomIndex + 1) % ZoomLevels.Length;
					ApplyZoom(ZoomLevels[_zoomIndex], useMouseAnchor: false);
					GetViewport().SetInputAsHandled();
				}
				break;

			case InputEventMouseButton mb:
				if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp && !IsMouseOverUI())
				{
					ApplyZoom(_camera.Zoom.X * _zoomFactor, useMouseAnchor: true);
					GetViewport().SetInputAsHandled();
				}
				else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown && !IsMouseOverUI())
				{
					ApplyZoom(_camera.Zoom.X / _zoomFactor, useMouseAnchor: true);
					GetViewport().SetInputAsHandled();
				}
				else if (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right)
				{
					// v0.3.21 — designation dispatcher handles both press and
					// release for left-click (drag-box semantics). Right-click
					// only fires on press. Falls through (no SetInputAsHandled)
					// when no tool is active and no smurf is selected, so the
					// colony view's left-click selection still runs.
					if (TryHandleMouseButton(mb))
						GetViewport().SetInputAsHandled();
				}
				break;

			case InputEventMouseMotion mm:
				HandleMouseMotion(mm);
				break;
		}
	}

	// Applies a new zoom level, optionally re-centering on the current mouse position
	// so the world point under the cursor stays fixed (RimWorld-style zoom).
	private void ApplyZoom(float newZoom, bool useMouseAnchor)
	{
		newZoom = Mathf.Clamp(newZoom, MinZoom, MaxZoom);
		float oldZoom = _camera.Zoom.X;

		if (useMouseAnchor && !Mathf.IsEqualApprox(oldZoom, newZoom))
		{
			var vpSize       = GetViewport().GetVisibleRect().Size;
			var mouseScreen  = GetViewport().GetMousePosition();
			var centerOffset = mouseScreen - vpSize / 2f;

			// World point currently under the cursor.
			var worldAnchor  = _camera.Position + centerOffset / oldZoom;
			_camera.Zoom     = new Vector2(newZoom, newZoom);
			// Shift camera so that same world point remains under the cursor.
			_camera.Position = worldAnchor - centerOffset / newZoom;
		}
		else
		{
			_camera.Zoom = new Vector2(newZoom, newZoom);
		}
		ClampCamera();
	}

	private void OnTick(string date, int pop, int inspired, int distressed)
	{
		_lastDate = CompactDate(date);
		_hud.UpdateStats(date, pop, inspired, distressed);

		var snap = _sim.GetLastSnapshot();
		if (snap != null)
		{
			_colony.UpdateFromTick(snap.Smurfs);
			// v0.5.2 — chain-order overlay reads MoveOrderQueue per smurf
			// from the snapshot. Push on every tick so completed queue
			// entries (popped by BehaviorSystem) clear from the visualisation
			// the same frame they execute.
			_orderQueueOverlay.SetSnapshot(snap);
			// v0.4.26 — visibility-gated panel refreshes. `_card.Refresh`
			// and the roster Refresh (rows list construction + the per-
			// row UpdateRow that touches multiple Label.Text values per
			// smurf) both ran on every snapshot push (60 Hz at 1× speed)
			// regardless of whether the user could see them — at 250
			// smurfs the hidden roster alone was burning ~525 ms/sec on
			// the main thread (250 rows × ~35 µs of label diffs and
			// ProgressBar value updates × 60 Hz). That's exactly the
			// 0.6 FPS-per-smurf linear cost Sam measured. Now the
			// refreshes only run when the panel is actually displayed;
			// while hidden, the snapshot still drives `_colony.UpdateFromTick`
			// (smurfs move) but the heavy roster diff stays off the
			// main thread.
			if (_card.IsVisibleInTree())
				_card.Refresh(snap);  // v0.3.36 — O(1) by-name lookup

			// v0.3.24 — refresh the Smurfs roster tab so it always reflects
			// the current colony. Skipped silently when the panel isn't built
			// (early frames before BuildUILayer completes) OR isn't visible.
			// `IsVisibleInTree` (not just `Visible`) catches the case where
			// the panel itself is `Visible = true` but is hidden because its
			// containing tab is collapsed — the common case in normal play.
			if (_roster != null && _roster.IsVisibleInTree())
			{
				// v0.3.27 — populate the rich roster table (RimWorld-style)
				// with name/role/mood + need bars + activity verb + combat
				// status. The roster panel diffs by name and updates in
				// place so per-tick refreshes don't flicker.
				var rows = new System.Collections.Generic.List<SimulationC_Roster_Row>(snap.Smurfs.Count);
				foreach (var s in snap.Smurfs)
					rows.Add(new SimulationC_Roster_Row(
						s.Name, s.Role, s.MoodState,
						s.Nutrition, s.Rest, s.Social, s.MagicResonance, s.Safety,
						s.CurrentTask, s.CombatTargetName));
				_roster.Refresh(rows);
			}
		}

		if (pop > 0) _colonyEverAlive = true;

		if (_colonyEverAlive && pop == 0 && !_gameOverShown)
		{
			_gameOverShown = true;
			_sim.Paused    = true;
			_gameOver.Show(date);
		}
	}

	// ── Smurf click ────────────────────────────────────────────────────────────

	private void OnSmurfClicked(string name)
	{
		// v0.3.24 — selection state lives in _selectedSmurfs; SelectSingleSmurf
		// has already been called by the input dispatcher before this signal.
		// This handler only opens the unit card.
		// v0.3.36 — O(1) by-name lookup via SimulationSnapshot.SmurfsByName.
		// FirstOrDefault was O(N) per call; at 1000 smurfs that's 1000× per
		// click. The struct-based snapshot also dropped 4 dictionary
		// allocations per smurf per tick.
		// v0.4.34 — also close the stationary-inspector card if it was
		// open from a prior click on a tile; the two cards share screen
		// space at top-right so they're mutually exclusive.
		_tileProps?.Close();
		_selOverlay?.ClearSelection();
		var snap = _sim.GetLastSnapshot();
		if (snap == null) return;
		if (snap.SmurfsByName.TryGetValue(name, out var smurf))
			_card.Show(smurf);
	}

	// ── Simulation events ──────────────────────────────────────────────────────

	private void OnSmurfDied(string name, int age, string cause)
	{
		GD.Print($"[Colony] {name} has passed at age {age} ({cause}).");
		_msgLog.Post($"{name} has died, age {age}. ({cause})", MessageLog.Category.Death, _lastDate);
	}

	private void OnBirthOccurred(string name, string sex)
	{
		GD.Print($"[Colony] A new smurf was born: {name} ({sex})!");
		_msgLog.Post($"{name} has joined the colony.", MessageLog.Category.Birth, _lastDate);
	}

	// v0.3.47 — wandering-in event. Auto-accepts for sub-B; Phase 8
	// will gain Accept/Decline prompts via the storyteller event pipeline.
	private void OnWandererArrived(string name, string sex, string role, int age)
	{
		GD.Print($"[Colony] A wanderer arrived: {name} ({sex}, {role}, age {age}).");
		_msgLog.Post($"{name} ({role}, age {age}) wandered into the colony.",
			MessageLog.Category.Birth, _lastDate);
		_alertsPane?.AddAlert(
			$"{name} ({role}, age {age}) has joined as a wanderer.",
			AlertsPane.AlertLevel.Info);
	}

	private void OnYearTicked(int year)
	{
		GD.Print($"[Colony] Year {year} S.D. begins.");
	}

	private void OnMoodThresholdCrossed(string name, string from, string to)
	{
		GD.Print($"[Mood] {name}: {from} → {to}");
		if (MoodOrdinal(to) < MoodOrdinal(from))
			_msgLog.Post($"{name}: mood worsened to {to}.", MessageLog.Category.MoodDrop, _lastDate);
	}

	// Compact in-game timestamp: "Hour 6 Day 3, Spring, Year 1 S.D." → "D3 Y1"
	private static string CompactDate(string date)
	{
		string day = "?", year = "0";
		int di = date.IndexOf("Day ");
		if (di >= 0)
		{
			var rest = date[(di + 4)..];
			int ci = rest.IndexOf(',');
			day = ci > 0 ? rest[..ci].Trim() : rest.Trim();
		}
		int yi = date.IndexOf("Year ");
		if (yi >= 0)
		{
			var rest = date[(yi + 5)..];
			int si = rest.IndexOf(' ');
			year = si > 0 ? rest[..si].Trim() : rest.Trim();
		}
		return $"D{day} Y{year}";
	}

	private static int MoodOrdinal(string mood) => mood switch
	{
		"Inspired"   => 5,
		"Content"    => 4,
		"Stressed"   => 3,
		"Distressed" => 2,
		"Breaking"   => 1,
		"Collapse"   => 0,
		_            => 4,
	};

	// ── Menu / save ────────────────────────────────────────────────────────────

	private void OnMenuRequested()
	{
		if (_returning) return;
		_wasPausedBeforeMenu = _sim.Paused;
		_sim.Paused = true;
		_pauseMenu.Open();
	}

	private void OnPauseMenuResume()
	{
		_pauseMenu.Close();
		_sim.Paused = _wasPausedBeforeMenu;
	}

	private void OnPauseMenuSettings() => _settings.Open();

	// Re-reads settings.cfg after the panel closes and re-applies runtime values.
	// Tooltip theme replacement only reliably propagates when applied from the scene root.
	private void ApplyRuntimeSettings()
	{
		var cfg = new ConfigFile();
		if (cfg.Load("user://settings.cfg") != Error.Ok) return;
		string ttSize = (string)cfg.GetValue("gameplay", "tooltip_size", "large");
		GetTree().Root.Theme = BuildTooltipTheme(SettingsPanel.TooltipFontSize(ttSize));
		int zoomSpeed = (int)(double)cfg.GetValue("gameplay", "zoom_speed", 5.0);
		_zoomFactor = SettingsPanel.ZoomFactorFromSpeed(zoomSpeed);
	}

	// "Save Game" → open save browser; player names/picks the slot there.
	private void OnPauseMenuSave()
	{
		_pauseMenu.Close();
		_saveBrowser.OpenForSave();
	}

	// "Load Save" → open load browser.
	private void OnPauseMenuLoad()
	{
		_pauseMenu.Close();
		_saveBrowser.OpenForLoad();
	}

	private void OnBrowserSaveConfirmed(string slotName)
	{
		var snap = _sim.GetLastSnapshot();
		if (snap != null)
			SaveManager.Instance?.SaveToSlot(slotName, snap, _colony.GetPositions());
		_saveBrowser.Refresh();
		_saveBrowser.ShowStatus($"✓  Saved as '{slotName}'!");
	}

	private void OnBrowserLoadConfirmed(string slotName)
	{
		_saveBrowser.Close();
		if (SaveManager.Instance?.LoadSlot(slotName) != true) return;

		var save = SaveManager.Instance.CurrentSave;
		if (save != null && save.WorldSeed != 0)
			WorldState.Instance?.LoadFromSave(save);
		else
			WorldState.Instance?.EnsureDefaultMap();

		GetTree().ReloadCurrentScene();
	}

	// "Exit to Main Menu" always saves to the "exit-save" slot silently.
	private async void OnPauseMenuExit()
	{
		if (_returning) return;
		_returning = true;
		// v0.4.18 — pin the sim to paused for the entire exit flow.
		// `OnMenuRequested` already paused when the pause menu opened,
		// but `_wasPausedBeforeMenu` could have been false and a stray
		// input mid-await could have flipped it back on; the explicit
		// re-assertion here (plus `_savingOverlay.Visible` now being
		// part of `IsAnyOverlayOpen`) keeps the world frozen from the
		// click of "Exit" through the scene change.
		_sim.Paused = true;
		_pauseMenu.Close();

		_savingOverlay.Visible  = true;
		_savingOverlay.Modulate = Colors.Transparent;
		var fadeIn = _savingOverlay.CreateTween();
		fadeIn.TweenProperty(_savingOverlay, "modulate", Colors.White, 0.25f)
			  .SetTrans(Tween.TransitionType.Sine);
		await ToSignal(fadeIn, Tween.SignalName.Finished);
		_sim.Paused = true;   // defensive: re-assert after the await

		var snap = _sim.GetLastSnapshot();
		if (snap != null)
		{
			// Exit-save slot derived from the colony name. Pre-scenario saves
			// (no ColonyName set) fall back to the legacy "exit-save" slot so
			// older worlds keep their save continuity.
			string colonyName = WorldState.Instance?.ColonyName ?? "";
			string slot = string.IsNullOrWhiteSpace(colonyName)
				? "exit-save"
				: SaveManager.SanitizeSlotName(colonyName);
			SaveManager.Instance?.SaveToSlot(slot, snap, _colony.GetPositions());
		}

		await ToSignal(GetTree().CreateTimer(0.7f), SceneTreeTimer.SignalName.Timeout);
		_sim.Paused = true;   // defensive: re-assert before the scene change

		GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
	}

	public override void _ExitTree()
	{
		// SimulationManager disposes its thread in _ExitTree automatically.
	}
}
