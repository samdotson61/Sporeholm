using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SmurfulationC.Simulation;
using SmurfulationC.Simulation.Items;
using SmurfulationC.World;

// Autoload singleton. Named save slots stored in user://saves/{name}.json.
// The reserved slot "exit-save" is created automatically on Exit to Main Menu.
public partial class SaveManager : Node
{
	public static SaveManager Instance { get; private set; } = null!;

	private const string SaveDir     = "user://saves";
	private const string LegacyPath  = "user://smurfulation_save.json";

	public bool        HasSave       { get; private set; }
	public ColonySave? CurrentSave   { get; private set; }
	public string?     CurrentSlot   { get; private set; }

	// v0.3.47 — wired by GameController.StartSim so SaveToSlot can read
	// the live colony inventory + per-smurf work priorities at save
	// time. Null until the in-game scene starts.
	private SmurfulationC.SimulationManager? _sim;
	public void RegisterSimulation(SmurfulationC.SimulationManager sim) => _sim = sim;
	public void UnregisterSimulation() => _sim = null;

	// ── Data transfer objects ──────────────────────────────────────────────────

	public record SmurfSaveData(
		string Name, int Age, string Sex, string Role,
		float Nutrition, float Rest, float Social, float MagicResonance, float Safety)
	{
		public float PosX    { get; init; } = 0f;
		public float PosY    { get; init; } = 0f;
		public float TargetX { get; init; } = 0f;
		public float TargetY { get; init; } = 0f;

		// Nullable so saves from before these fields still load fine.
		public int BirthdayDayOfYear { get; init; } = 0;
		public Dictionary<string, float>? Traits      { get; init; } = null;
		public Dictionary<string, int>?   Skills      { get; init; } = null;
		public List<string>?              Personality { get; init; } = null;
		public Dictionary<string, float>? BodyParts   { get; init; } = null;

		// v0.4.7 (bugreport B-1) — per-smurf state added since v0.3.40 now
		// round-trips through saves. All fields are nullable so older
		// saves (pre-equipment / pre-handedness / pre-preferences / pre-
		// thoughts) still load and the missing fields fall back to the
		// fresh-roll path the LoadFromSave loader already implements.
		public string?                         Handedness     { get; init; } = null;
		public List<EquipmentSaveData>?        Equipment      { get; init; } = null;
		public ItemSaveData?                   CarriedItem    { get; init; } = null;
		public PreferencesSaveData?            Preferences    { get; init; } = null;
		public List<ThoughtSaveData>?          Thoughts       { get; init; } = null;
	}

	public record ColonySave(
		int Year, string Season, int Day,
		List<SmurfSaveData> Smurfs)
	{
		// World fields — nullable/default so old saves still deserialise.
		public int    WorldSeed      { get; init; } = 0;
		public int    WorldTileX     { get; init; } = WorldMapGenerator.DefaultGridSize / 2;
		public int    WorldTileY     { get; init; } = WorldMapGenerator.DefaultGridSize / 2;
		public int    WorldGridSize  { get; init; } = WorldMapGenerator.DefaultGridSize;
		public int    LocalMapWidth  { get; init; } = LocalMap.DefaultWidth;
		public int    LocalMapHeight { get; init; } = LocalMap.DefaultHeight;
		public string WorldName      { get; init; } = "";
		// Scenario screen output — preserved across save/load so the exit-save
		// slot name and HUD label stay consistent with what the player typed.
		public string ColonyName     { get; init; } = "";
		public float  ElevBias       { get; init; } = 0f;
		public float  RainBias       { get; init; } = 0f;
		public float  TempBias       { get; init; } = 0f;
		public float  MagicBias      { get; init; } = 0f;

		// Phase 2.5 delta lists — null when no mutations exist (omitted from JSON).
		// On load: regenerate map from seed, then apply these deltas to restore runtime state.
		public List<TerrainDelta>?     TerrainDeltas     { get; init; } = null;
		public List<VegetationDelta>?  VegetationDeltas  { get; init; } = null;
		// v0.3.37 — active player-issued designations. Saved so the
		// excavation / gather work queue survives a save → load cycle.
		// Null/empty when no designations are active. The flags live on the
		// LocalTile struct; LocalMap.SnapshotDesignations() returns them in
		// the (X, Y, IsExcavate) shape that maps directly to this record.
		public List<DesignationDelta>? DesignationDeltas { get; init; } = null;

		// v0.3.47 (Phase 4 sub-B) — colony inventory snapshot. One entry
		// per stack; on load each entry is reconstructed via ItemFactory
		// shape so the Inventory.Add stacking rules still apply (any
		// stacks that should merge will merge as they're re-added). Null
		// for saves predating Phase 4 sub-A.
		public List<ItemSaveData>? ColonyInventory { get; init; } = null;

		// v0.3.47 — per-smurf work priorities (RimWorld Jobs tab). Outer
		// dict keyed by smurf Name; inner dict keyed by category string
		// (Doctor / Mine / Cook / Haul / etc.); values are 0 (off) or
		// 1-4 (priority). Null for saves predating the Jobs tab.
		public Dictionary<string, Dictionary<string, byte>>? WorkPriorities { get; init; } = null;

		// v0.4.7 (bugreport B-1) — items dropped on the map waiting for
		// haul. Null for saves predating per-tile drops (pre-v0.4.2).
		public List<DroppedItemSaveData>? DroppedItems { get; init; } = null;

		// v0.4.7 (bugreport B-7) — per-Boulder stone subtype overrides.
		// Most tiles will match the deterministic-from-seed regeneration,
		// so we only save tiles whose stored subtype differs from what
		// the generator would re-produce. Null for saves predating the
		// stone variation system (pre-v0.4.2).
		public List<StoneTileDelta>? StoneTileDeltas { get; init; } = null;
	}

	// v0.4.7 (bugreport B-7) — one Boulder tile whose stone subtype
	// differs from what the deterministic generator would produce. On
	// load we apply these as overrides after the generator's
	// AssignStoneVariation pass.
	public record StoneTileDelta(int X, int Y, string MaterialSubType);

	// v0.4.7 (bugreport B-1) — one equipment slot on disk. Slot is the
	// EquipSlot enum name ("Head", "Torso", "LeftHand", …). Item shape
	// matches the existing ItemSaveData record so equipment + carried +
	// dropped + inventory all share the same Item serialisation.
	public record EquipmentSaveData(string Slot, ItemSaveData Item);

	// v0.4.7 (bugreport B-1) — per-smurf preferences. Mirrors the
	// `Preferences` class with all six list fields. Null on legacy saves
	// → loader rolls a fresh preference set as it did pre-v0.4.7.
	public record PreferencesSaveData
	{
		public List<string> LikedItems         { get; init; } = new();
		public List<string> DislikedItems      { get; init; } = new();
		public List<string> LikedActivities    { get; init; } = new();
		public List<string> DislikedActivities { get; init; } = new();
		public List<string> LikedSmurfs        { get; init; } = new();
		public List<string> DislikedSmurfs     { get; init; } = new();
	}

	// v0.4.7 (bugreport B-1) — per-smurf thought ring slot. Only active
	// thoughts (TicksRemaining > 0) are written; the loader rebuilds the
	// 8-slot ring on restore. MoodOffset is saved so a registry change
	// can't silently re-tune a in-flight thought's contribution.
	public record ThoughtSaveData(
		string Key, int TicksRemaining, float MoodOffset, string Context);

	// v0.4.7 (bugreport B-1) — one item lying on a tile waiting for haul.
	// Item shape matches the existing ItemSaveData.
	public record DroppedItemSaveData(int X, int Y, ItemSaveData Item);

	// v0.3.47 (Phase 4 sub-B) — one inventory stack on disk. Mirror the
	// in-memory `Item` fields by value. AvgBirthTick is stored as a
	// relative-to-load offset because absolute GlobalTick rolls over on
	// new game; loaders restore the tick relative to the current
	// SimulationCore.GlobalTick (so a 5-day-old item stays 5 days old).
	public record ItemSaveData(
		string Kind, string SubType,
		string MaterialFamily, string MaterialSubType,
		string Quality, string State,
		int Quantity, float AvgCondition, float DurabilityCap,
		long AgeInTicks)
	{
		// v0.4.35 — populated only for `Kind == "Corpse"` items. Null on
		// every other item path so saves remain compact. The `record` body
		// syntax lets us add an init-only property without disrupting the
		// positional ctor signature already used by every existing call
		// site.
		public CorpseSaveData? Corpse { get; init; } = null;
	}

	// v0.4.35 — biographical sidecar for Corpse-kind items. Saves the
	// dead smurf's static attributes so the unit-card / hover obituary
	// survives reload. `DeathAgeTicks` is stored relative to the save's
	// current tick (so a body that died 2 days ago still reads "2 days
	// ago" after a reload at any later tick — same convention as
	// ItemSaveData.AgeInTicks).
	public record CorpseSaveData(
		string       Name,
		int          AgeYears,
		string       Sex,
		string       Role,
		string       Cause,
		long         DeathAgeTicks,
		List<string> Personality,
		string       Handedness);

	// A tile whose terrain was permanently changed from its seed-generated state.
	public record TerrainDelta(int X, int Y, string Terrain);

	// A vegetation slot whose yield/regrowth state differs from freshly generated.
	public record VegetationDelta(int X, int Y, byte YieldRemaining, ushort RegrowthTimer);

	// v0.3.37 — a player-issued tile designation. v0.3.38 widened to carry
	// a `Kind` string for the four designation types (Excavate / Gather /
	// ChopWood / Cut). The pre-v0.3.38 `IsExcavate` bool is kept for save
	// backwards compatibility: when Kind is null we fall back to IsExcavate
	// to decide between Excavate and Gather.
	public record DesignationDelta(int X, int Y, bool IsExcavate)
	{
		public string? Kind { get; init; } = null;
	}

	// One entry shown in the save browser.
	public record SaveSlotInfo(string Name, string DisplayText, long LastModifiedUnix);

	// ── Lifecycle ──────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		Instance = this;
		EnsureSaveDir();
		MigrateLegacySave();
		RefreshHasSave();
	}

	// ── Public slot API ────────────────────────────────────────────────────────

	// Converts a free-form colony name into a filesystem-safe save slot name.
	// Strips characters disallowed on Windows / Linux / macOS file systems,
	// collapses whitespace, trims to 60 chars. Pure function — used by the
	// scenario screen's exit-save path and any future slot-from-display path.
	public static string SanitizeSlotName(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return "exit-save";
		var sb = new System.Text.StringBuilder(raw.Length);
		foreach (char c in raw)
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ') sb.Append(c);
			else sb.Append('_');
		}
		string s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
		if (s.Length > 60) s = s.Substring(0, 60).TrimEnd();
		return s.Length == 0 ? "exit-save" : s;
	}

	public void SaveToSlot(string slotName,
		SimulationSnapshot snapshot,
		Dictionary<string, (Godot.Vector2 Pos, Godot.Vector2 Target)>? positions = null)
	{
		// v0.3.47 (Phase 4 sub-B) — colony inventory snapshot + per-smurf
		// work priorities. Both come from the registered SimulationManager;
		// in headless test runs (no SimulationManager) these stay null.
		List<ItemSaveData>? invSave = null;
		Dictionary<string, Dictionary<string, byte>>? prios = null;
		// v0.4.7 (bugreport B-1) — per-smurf extras + dropped-items
		// snapshot. Populated from the registered SimulationManager
		// alongside the inventory + work priorities.
		Dictionary<string, SmurfulationC.SimulationManager.SmurfSaveExtras>? extras = null;
		List<DroppedItemSaveData>? droppedItems = null;
		long globalTick = 0;
		if (_sim != null)
		{
			globalTick = _sim.GetGlobalTick();
			var inventoryRows = _sim.GetInventorySnapshot();
			if (inventoryRows.Length > 0)
			{
				invSave = new List<ItemSaveData>(inventoryRows.Length);
				foreach (var row in inventoryRows)
				{
					long age = System.Math.Max(0, globalTick - row.AvgBirthTick);
					invSave.Add(new ItemSaveData(
						row.Kind.ToString(), row.SubType,
						row.MaterialFamily, row.MaterialSubType,
						row.Quality.ToString(), row.State.ToString(),
						row.Quantity, row.AvgCondition, row.DurabilityCap,
						age));
				}
			}
			var all = _sim.GetWorkPrioritiesSnapshot();
			if (all != null && all.Count > 0) prios = all;
			extras = _sim.GetSmurfSaveExtras(globalTick);
			droppedItems = _sim.GetDroppedItemsSnapshot(globalTick);
		}

		var smurfs = BuildSmurfList(snapshot, positions, extras);

		// Collect terrain and vegetation deltas from the live local map.
		var localMap = WorldState.Instance?.CurrentLocalMap;
		var tDeltas  = localMap?.GetTerrainMutations()
			.Select(m => new TerrainDelta(m.X, m.Y, m.Terrain.ToString()))
			.ToList();
		var vDeltas  = localMap?.GetVegetationMutations()
			.Select(m => new VegetationDelta(m.X, m.Y, m.YieldRemaining, m.RegrowthTimer))
			.ToList();
		// v0.3.37 — active designations. v0.3.38 includes Kind to round-trip
		// all four designation types (Excavate / Gather / ChopWood / Cut).
		var dDeltas  = localMap?.SnapshotDesignations()
			.Select(d => new DesignationDelta(d.X, d.Y, d.Kind == LocalMap.DesignationKind.Excavate)
				{ Kind = d.Kind.ToString() })
			.ToList();
		// v0.4.7 (bugreport B-7) — stone variation per Boulder tile.
		// Saved so future versions whose AssignStoneVariation weights
		// differ from what produced this save still display the same
		// Boulder textures + drop the same materials on excavate.
		var stoneDeltas = localMap?.SnapshotStoneVariation()
			.Select(t => new StoneTileDelta(t.X, t.Y, t.MaterialSubType))
			.ToList();

		var save = new ColonySave(
			snapshot.Date.Year,
			snapshot.Date.Season.ToString(),
			snapshot.Date.Day,
			smurfs)
		{
			WorldSeed        = WorldState.Instance?.WorldSeed      ?? 0,
			WorldTileX       = WorldState.Instance?.SelectedTileX  ?? WorldMapGenerator.DefaultGridSize / 2,
			WorldTileY       = WorldState.Instance?.SelectedTileY  ?? WorldMapGenerator.DefaultGridSize / 2,
			WorldGridSize    = WorldState.Instance?.WorldGridSize  ?? WorldMapGenerator.DefaultGridSize,
			LocalMapWidth    = WorldState.Instance?.LocalMapWidth  ?? LocalMap.DefaultWidth,
			LocalMapHeight   = WorldState.Instance?.LocalMapHeight ?? LocalMap.DefaultHeight,
			WorldName        = WorldState.Instance?.WorldName      ?? "",
			ColonyName       = WorldState.Instance?.ColonyName     ?? "",
			ElevBias         = WorldState.Instance?.ElevBias       ?? 0f,
			RainBias         = WorldState.Instance?.RainBias       ?? 0f,
			TempBias         = WorldState.Instance?.TempBias       ?? 0f,
			MagicBias        = WorldState.Instance?.MagicBias      ?? 0f,
			TerrainDeltas     = (tDeltas?.Count ?? 0) > 0 ? tDeltas : null,
			VegetationDeltas  = (vDeltas?.Count ?? 0) > 0 ? vDeltas : null,
			DesignationDeltas = (dDeltas?.Count ?? 0) > 0 ? dDeltas : null,
			ColonyInventory   = invSave,
			WorkPriorities    = prios,
			DroppedItems      = droppedItems,
			StoneTileDeltas   = (stoneDeltas?.Count ?? 0) > 0 ? stoneDeltas : null,
		};

		string path = SlotPath(slotName);
		try
		{
			var opts = new JsonSerializerOptions { WriteIndented = false };
			var json = JsonSerializer.Serialize(save, opts);
			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			file.StoreString(json);
			CurrentSave  = save;
			CurrentSlot  = slotName;
			HasSave      = true;
			GD.Print($"[Save] Saved '{slotName}' — {smurfs.Count} smurfs at {save.Season} Y{save.Year}.");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[Save] Failed to write '{slotName}': {ex.Message}");
		}
	}

	public bool LoadSlot(string slotName)
	{
		string path = SlotPath(slotName);
		if (!FileAccess.FileExists(path))
		{
			GD.PrintErr($"[Save] Slot '{slotName}' not found.");
			return false;
		}
		try
		{
			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			var json = file.GetAsText();
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			CurrentSave = JsonSerializer.Deserialize<ColonySave>(json, opts);
			if (CurrentSave == null) return false;
			CurrentSlot = slotName;
			HasSave     = true;
			GD.Print($"[Save] Loaded '{slotName}' — Y{CurrentSave.Year}, {CurrentSave.Smurfs.Count} smurfs.");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[Save] Failed to load '{slotName}': {ex.Message}");
			return false;
		}
	}

	public void DeleteSlot(string slotName)
	{
		string path = SlotPath(slotName);
		if (FileAccess.FileExists(path))
			DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		if (CurrentSlot == slotName)
		{
			CurrentSlot = null;
			CurrentSave = null;
		}
		RefreshHasSave();
	}

	public void RenameSlot(string oldName, string newName)
	{
		if (oldName == newName) return;
		string src  = ProjectSettings.GlobalizePath(SlotPath(oldName));
		string dst  = ProjectSettings.GlobalizePath(SlotPath(newName));
		if (!FileAccess.FileExists(SlotPath(oldName))) return;
		DirAccess.RenameAbsolute(src, dst);
		if (CurrentSlot == oldName) CurrentSlot = newName;
	}

	public List<SaveSlotInfo> GetSaveSlots()
	{
		var list = new List<SaveSlotInfo>();
		using var dir = DirAccess.Open(SaveDir);
		if (dir == null) return list;

		dir.ListDirBegin();
		string fname = dir.GetNext();
		while (fname != "")
		{
			if (!fname.StartsWith('.') && fname.EndsWith(".json"))
			{
				string slot = fname[..^5]; // strip .json
				string path = SlotPath(slot);
				string display = SlotDisplayText(path, slot);
				long modified  = SlotModifiedTime(path);
				list.Add(new SaveSlotInfo(slot, display, modified));
			}
			fname = dir.GetNext();
		}
		dir.ListDirEnd();

		list.Sort((a, b) => b.LastModifiedUnix.CompareTo(a.LastModifiedUnix));
		return list;
	}

	public SaveSlotInfo? GetSlotInfo(string slotName)
	{
		string path = SlotPath(slotName);
		if (!FileAccess.FileExists(path)) return null;
		return new SaveSlotInfo(slotName, SlotDisplayText(path, slotName), SlotModifiedTime(path));
	}

	// ── Legacy compatibility (used by GameOverPanel, existing callers) ─────────

	// Saves to the current slot (falls back to "autosave").
	public void Save(SimulationSnapshot snapshot,
		Dictionary<string, (Godot.Vector2 Pos, Godot.Vector2 Target)>? positions = null)
		=> SaveToSlot(CurrentSlot ?? "autosave", snapshot, positions);

	public void Load() { if (CurrentSlot != null) LoadSlot(CurrentSlot); }

	// Deletes the current slot file and clears in-memory state.
	// Used by GameOverPanel to prevent loading a dead colony.
	public void DeleteSave()
	{
		if (CurrentSlot != null)
		{
			string path = SlotPath(CurrentSlot);
			if (FileAccess.FileExists(path))
				DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
		}
		HasSave     = false;
		CurrentSave = null;
		CurrentSlot = null;
		RefreshHasSave();
	}

	// Clears in-memory state without touching any files on disk.
	// Used by the New Game flow so other save slots are preserved.
	public void ClearCurrentSlot()
	{
		HasSave     = false;
		CurrentSave = null;
		CurrentSlot = null;
	}

	// ── Private helpers ────────────────────────────────────────────────────────

	private static string SlotPath(string slotName) =>
		$"{SaveDir}/{slotName}.json";

	private void EnsureSaveDir()
	{
		if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(SaveDir)))
			DirAccess.MakeDirAbsolute(ProjectSettings.GlobalizePath(SaveDir));
	}

	private void MigrateLegacySave()
	{
		if (!FileAccess.FileExists(LegacyPath)) return;
		string src = ProjectSettings.GlobalizePath(LegacyPath);
		string dst = ProjectSettings.GlobalizePath($"{SaveDir}/autosave.json");
		DirAccess.RenameAbsolute(src, dst);
		GD.Print("[Save] Migrated legacy save → autosave slot.");
	}

	private void RefreshHasSave()
	{
		using var dir = DirAccess.Open(SaveDir);
		if (dir == null) { HasSave = false; return; }
		dir.ListDirBegin();
		string f = dir.GetNext();
		while (f != "")
		{
			if (!f.StartsWith('.') && f.EndsWith(".json")) { HasSave = true; return; }
			f = dir.GetNext();
		}
		dir.ListDirEnd();
		HasSave = false;
	}

	private string SlotDisplayText(string path, string slot)
	{
		try
		{
			using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			var save = JsonSerializer.Deserialize<ColonySave>(file.GetAsText(), opts);
			if (save != null)
				return $"{save.Season} Y{save.Year} · {save.Smurfs.Count} smurfs";
		}
		catch { }
		return slot;
	}

	private static long SlotModifiedTime(string path)
	{
		try
		{
			return (long)FileAccess.GetModifiedTime(path);
		}
		catch { return 0L; }
	}

	private static List<SmurfSaveData> BuildSmurfList(
		SimulationSnapshot snapshot,
		Dictionary<string, (Godot.Vector2 Pos, Godot.Vector2 Target)>? positions,
		Dictionary<string, SmurfulationC.SimulationManager.SmurfSaveExtras>? extras = null)
	{
		var smurfs = new List<SmurfSaveData>();
		foreach (var s in snapshot.Smurfs)
		{
			if (!s.IsAlive) continue;
			(Godot.Vector2 Pos, Godot.Vector2 Target) p = default;
			positions?.TryGetValue(s.Name, out p);

			SmurfulationC.SimulationManager.SmurfSaveExtras? ex = null;
			extras?.TryGetValue(s.Name, out ex);

			smurfs.Add(new SmurfSaveData(
				s.Name, s.AgeInYears, s.Sex.ToString(), s.Role,
				s.Nutrition, s.Rest, s.Social, s.MagicResonance, s.Safety)
			{
				PosX    = p.Pos.X,    PosY    = p.Pos.Y,
				TargetX = p.Target.X, TargetY = p.Target.Y,
				BirthdayDayOfYear = s.BirthdayDayOfYear,
				Traits      = new Dictionary<string, float>(s.Traits),
				Skills      = new Dictionary<string, int>(s.Skills),
				Personality = new List<string>(s.Personality),
				BodyParts   = new Dictionary<string, float>(s.BodyParts),
				// v0.4.7 (bugreport B-1) — per-smurf state added since
				// v0.3.40. Null when no extras passed (e.g. headless
				// test runs) so older save flows still work.
				Handedness  = ex?.Handedness,
				Equipment   = ex?.Equipment,
				CarriedItem = ex?.CarriedItem,
				Preferences = ex?.Preferences,
				Thoughts    = ex?.Thoughts,
			});
		}
		return smurfs;
	}
}
