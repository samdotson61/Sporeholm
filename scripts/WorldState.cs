using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Sporeholm;
using Sporeholm.World;

// Autoload singleton. Carries world-map and local-map data between scenes.
// WorldGenPanel writes here; GameController reads here on startup.
public partial class WorldState : Node
{
    public static WorldState? Instance { get; private set; }

    private const string WorldsDir = "user://worlds";

    public int    WorldSeed      { get; private set; } = 0;
    public int    WorldGridSize  { get; private set; } = WorldMapGenerator.DefaultGridSize;
    public int    LocalMapWidth  { get; private set; } = LocalMap.DefaultWidth;
    public int    LocalMapHeight { get; private set; } = LocalMap.DefaultHeight;
    public string WorldName      { get; private set; } = "";
    public float  ElevBias       { get; private set; } = 0f;
    public float  RainBias       { get; private set; } = 0f;
    public float  TempBias       { get; private set; } = 0f;
    public float  MagicBias      { get; private set; } = 0f;
    // v0.5.84t — worldgen resource scarcity multiplier.
    //   1.00 = Abundant (default, pre-v0.5.84t behaviour).
    //   0.25 = Scarce (¼ of vegetation + ore veins).
    // Applied at LocalMapGenerator placement time: vegetation rolls,
    // min-mushroom/magic guarantees, and resource-vein density all scale.
    public float  ResourceScarcity { get; private set; } = 1.0f;

    public int           SelectedTileX   { get; private set; } = WorldMapGenerator.DefaultGridSize / 2;
    public int           SelectedTileY   { get; private set; } = WorldMapGenerator.DefaultGridSize / 2;
    public WorldTile[,]? WorldMap        { get; private set; }
    public LocalMap?     CurrentLocalMap { get; private set; }

    // Scenario screen output: written by ScenarioPanel when the player clicks
    // Begin Colony, consumed by SimulationManager.SeedColony on the next scene.
    // Null = no scenario applies (loading from save, or legacy quick-start).
    public ScenarioConfig? PendingScenario { get; set; }

    // Colony name set on the Scenario screen. Replaces "exit-save" as the
    // automatic save slot name when the player returns to the main menu.
    // Default falls back to "Colony MM-DD-YY" if the scenario screen wasn't
    // visited (loading older saves that pre-date this field).
    public string ColonyName { get; set; } = "";

    // ── World file records ─────────────────────────────────────────────────────

    public record WorldFileData(
        string Name, int Seed, int GridSize, int LocalMapWidth, int LocalMapHeight,
        float ElevBias, float RainBias, float TempBias, float MagicBias)
    {
        // v0.5.84t — defaulted in body so old saved-world JSONs that omit
        // the field still deserialise (System.Text.Json fills the init-only
        // property with the body default when the JSON field is missing).
        public float ResourceScarcity { get; init; } = 1.0f;
    }

    public record WorldFileInfo(
        string Name, string FileName, int Seed, int GridSize,
        int LocalMapWidth, int LocalMapHeight,
        float ElevBias, float RainBias, float TempBias, float MagicBias,
        long LastModifiedUnix)
    {
        public float ResourceScarcity { get; init; } = 1.0f;
    }

    public override void _Ready()
    {
        Instance = this;
        EnsureWorldsDir();
    }

    // ── World generation ───────────────────────────────────────────────────────

    // Called by WorldGenPanel when the player generates a new world.
    public WorldTile[,] GenerateWorld(int seed, int gridSize = WorldMapGenerator.DefaultGridSize,
        float elevBias = 0f, float rainBias = 0f, float tempBias = 0f, float magicBias = 0f,
        string? name = null, int localWidth = LocalMap.DefaultWidth, int localHeight = LocalMap.DefaultHeight,
        float resourceScarcity = 1.0f)
    {
        WorldSeed        = seed;
        WorldGridSize    = Mathf.Clamp(gridSize, 8, WorldMapGenerator.MaxGridSize);
        ElevBias         = elevBias;
        RainBias         = rainBias;
        TempBias         = tempBias;
        MagicBias        = magicBias;
        ResourceScarcity = Mathf.Clamp(resourceScarcity, 0.25f, 1.0f);
        WorldName        = string.IsNullOrWhiteSpace(name) ? $"world-{seed}" : name.Trim();
        LocalMapWidth    = localWidth;
        LocalMapHeight   = localHeight;
        SelectedTileX    = WorldGridSize / 2;
        SelectedTileY    = WorldGridSize / 2;
        WorldMap         = WorldMapGenerator.Generate(seed, WorldGridSize, elevBias, rainBias, tempBias, magicBias);
        return WorldMap;
    }

    // Called when the player clicks a tile on the world map.
    public void SelectTile(int x, int y)
    {
        if (WorldMap == null) return;
        SelectedTileX   = x;
        SelectedTileY   = y;
        CurrentLocalMap = LocalMapGenerator.Generate(WorldMap[x, y], LocalMapWidth, LocalMapHeight, ResourceScarcity);
    }

    // Called by GameController / MainMenuController when loading a save that has world data.
    public void LoadFromSave(SaveManager.ColonySave save)
    {
        WorldSeed      = save.WorldSeed;
        WorldGridSize  = save.WorldGridSize;
        LocalMapWidth  = save.LocalMapWidth;
        LocalMapHeight = save.LocalMapHeight;
        WorldName      = save.WorldName;
        ColonyName     = save.ColonyName;
        ElevBias         = save.ElevBias;
        RainBias         = save.RainBias;
        TempBias         = save.TempBias;
        MagicBias        = save.MagicBias;
        ResourceScarcity = Mathf.Clamp(save.ResourceScarcity, 0.25f, 1.0f);
        SelectedTileX    = Mathf.Clamp(save.WorldTileX, 0, WorldGridSize - 1);
        SelectedTileY    = Mathf.Clamp(save.WorldTileY, 0, WorldGridSize - 1);
        WorldMap         = WorldMapGenerator.Generate(WorldSeed, WorldGridSize, ElevBias, RainBias, TempBias, MagicBias);
        CurrentLocalMap  = LocalMapGenerator.Generate(WorldMap[SelectedTileX, SelectedTileY], LocalMapWidth, LocalMapHeight, ResourceScarcity);

        // Restore any tile mutations that happened before this save was written.
        if (save.TerrainDeltas != null)
            foreach (var d in save.TerrainDeltas)
                if (System.Enum.TryParse<TerrainType>(d.Terrain, out var t))
                    CurrentLocalMap.ApplyTerrainDelta(d.X, d.Y, t);

        if (save.VegetationDeltas != null)
            foreach (var d in save.VegetationDeltas)
                CurrentLocalMap.ApplyVegetationDelta(d.X, d.Y, d.YieldRemaining, d.RegrowthTimer);

        // v0.4.7 (bugreport B-7) — restore per-Boulder stone subtypes.
        // Runs after terrain deltas so it only re-applies on tiles that
        // are still Boulders post-restoration; an excavated tile (now
        // Mud) gets skipped because GetTileStone vs SetTileStone don't
        // affect mud tiles' rendering anyway.
        if (save.StoneTileDeltas != null)
            foreach (var d in save.StoneTileDeltas)
                CurrentLocalMap.SetTileStone(d.X, d.Y,
                    new Sporeholm.Simulation.Items.MaterialKey("Stone", d.MaterialSubType));

        // v0.3.37 — restore active designations AFTER terrain & vegetation
        // deltas so the validity checks inside SetXDesignation see the
        // post-load tile state. v0.3.38 — switched to the Kind enum so
        // ChopWood / Cut designations also round-trip. Old saves (without
        // Kind) fall back to the IsExcavate bool.
        if (save.DesignationDeltas != null)
            foreach (var d in save.DesignationDeltas)
            {
                switch (d.Kind)
                {
                    case "Excavate":
                        CurrentLocalMap.SetExcavationDesignation(d.X, d.Y, true); break;
                    case "Gather":
                        CurrentLocalMap.SetGatherDesignation(d.X, d.Y, true); break;
                    case "ChopWood":
                        CurrentLocalMap.SetChopWoodDesignation(d.X, d.Y, true); break;
                    case "Cut":
                        CurrentLocalMap.SetCutDesignation(d.X, d.Y, true); break;
                    default:
                        // Pre-v0.3.38 save — no Kind field; use the bool.
                        if (d.IsExcavate)
                            CurrentLocalMap.SetExcavationDesignation(d.X, d.Y, true);
                        else
                            CurrentLocalMap.SetGatherDesignation(d.X, d.Y, true);
                        break;
                }
            }

        // v0.5.73 — restore structures (walls / floors / doors / blueprints
        // / furniture). Routes through SetStructure inside ApplyStructureDelta
        // so the passability cache + blueprint index + room/region dirty
        // flags rebuild correctly. Order: after terrain/vegetation/designations
        // so the structure's tile sits on its restored terrain. Sam: "Ensure
        // all structures, items, inventories, etc. are saved/loaded.
        // Structures disappear on save."
        if (save.StructureDeltas != null)
            foreach (var d in save.StructureDeltas)
            {
                if (!System.Enum.TryParse<StructureType>(d.Type, out var stype)) continue;
                System.Enum.TryParse<StructureMat>(d.Material, out var smat);
                System.Enum.TryParse<Sporeholm.Simulation.Items.Quality>(d.Quality, out var quality);
                // v0.5.84c — restore floor underneath. Old saves predating
                // v0.5.84 deserialise FloorBeneath = null + HasFloorBeneath
                // = false, so the slot loads with no floor (existing v0.5.73
                // behaviour preserved).
                StructureMat floorBeneath = default;
                if (d.HasFloorBeneath && d.FloorBeneath != null)
                    System.Enum.TryParse<StructureMat>(d.FloorBeneath, out floorBeneath);
                CurrentLocalMap.ApplyStructureDelta(d.X, d.Y, new StructureSlot
                {
                    Type               = stype,
                    Material           = smat,
                    BuildProgress      = d.BuildProgress,
                    MaterialsDelivered = d.MaterialsDelivered,
                    Quality            = quality,
                    RoomId             = 0,   // rebuilt by RoomDetector on next room query
                    FloorBeneath       = floorBeneath,
                    HasFloorBeneath    = d.HasFloorBeneath,
                });
            }

        // v0.5.73 — restore stockpile zones. Cells re-attach to their
        // saved Ids so future references via GetStockpileIdAt stay valid.
        if (save.StockpileZones != null)
            foreach (var z in save.StockpileZones)
            {
                if (!System.Enum.TryParse<StoragePriority>(z.Priority, out var prio))
                    prio = StoragePriority.Normal;
                var kinds = new List<Sporeholm.Simulation.Items.ItemKind>(z.AcceptedKinds?.Count ?? 0);
                if (z.AcceptedKinds != null)
                    foreach (var ks in z.AcceptedKinds)
                        if (System.Enum.TryParse<Sporeholm.Simulation.Items.ItemKind>(ks, out var k))
                            kinds.Add(k);
                var cells = new List<(int X, int Y)>(z.Cells?.Count ?? 0);
                if (z.Cells != null)
                    foreach (var c in z.Cells) cells.Add((c.X, c.Y));
                CurrentLocalMap.ApplyStockpileZone(z.Id, z.Name ?? "Stockpile", prio, kinds, cells);
            }

        // v0.5.73 — restore named areas (Home + any custom areas the
        // player created via the Areas tab). EnsureDefaultAreas already
        // re-created an empty Home at LocalMap construction; ApplyNamedArea
        // overwrites that mask with the saved one if Home appears in the
        // save (it always does post-v0.5.44).
        if (save.NamedAreas != null)
            foreach (var a in save.NamedAreas)
            {
                var cells = new List<(int X, int Y)>(a.Cells?.Count ?? 0);
                if (a.Cells != null)
                    foreach (var c in a.Cells) cells.Add((c.X, c.Y));
                CurrentLocalMap.ApplyNamedArea(a.Name, cells);
            }

        // v0.5.84s — Phase 5.5 Crafting Bills System. Restore per-
        // workbench bill queues from save. Each WorkbenchBillsSave
        // entry pairs a tile coord with its list of bills.
        if (save.WorkbenchBills != null)
            foreach (var wb in save.WorkbenchBills)
            {
                var bills = new List<Sporeholm.Simulation.Crafting.Bill>(wb.Bills?.Count ?? 0);
                if (wb.Bills != null)
                    foreach (var b in wb.Bills)
                        bills.Add(new Sporeholm.Simulation.Crafting.Bill
                        {
                            RecipeId         = b.RecipeId,
                            Mode             = (Sporeholm.Simulation.Crafting.BillRepeatMode)b.Mode,
                            RepeatCount      = b.RepeatCount,
                            TargetCount      = b.TargetCount,
                            Suspended        = b.Suspended,
                            ProgressTicks    = b.ProgressTicks,
                            RepeatsRemaining = b.RepeatsRemaining,
                        });
                CurrentLocalMap.ApplyWorkbenchBills(wb.X, wb.Y, bills);
            }
    }

    // Ensures a valid local map exists even for saves without world data.
    public void EnsureDefaultMap()
    {
        if (CurrentLocalMap != null) return;
        if (WorldMap == null)
        {
            WorldSeed     = 42;
            WorldMap      = WorldMapGenerator.Generate(WorldSeed, WorldGridSize);
            SelectedTileX = WorldGridSize / 2;
            SelectedTileY = WorldGridSize / 2;
        }
        SelectTile(SelectedTileX, SelectedTileY);
    }

    public void Clear()
    {
        WorldSeed      = 0;
        WorldGridSize  = WorldMapGenerator.DefaultGridSize;
        LocalMapWidth  = LocalMap.DefaultWidth;
        LocalMapHeight = LocalMap.DefaultHeight;
        WorldName      = "";
        ElevBias = RainBias = TempBias = MagicBias = 0f;
        ResourceScarcity = 1.0f;
        SelectedTileX  = WorldMapGenerator.DefaultGridSize / 2;
        SelectedTileY  = WorldMapGenerator.DefaultGridSize / 2;
        WorldMap        = null;
        CurrentLocalMap = null;
        PendingScenario = null;
        ColonyName      = "";
    }

    // ── World file I/O ─────────────────────────────────────────────────────────

    public void SaveWorldFile()
    {
        if (string.IsNullOrWhiteSpace(WorldName)) return;
        var data = new WorldFileData(WorldName, WorldSeed, WorldGridSize,
            LocalMapWidth, LocalMapHeight, ElevBias, RainBias, TempBias, MagicBias)
            { ResourceScarcity = ResourceScarcity };
        string safeName = SanitiseName(WorldName);
        string path = $"{WorldsDir}/{safeName}.json";
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(data, opts));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[World] Failed to save world '{WorldName}': {ex.Message}");
        }
    }

    public bool LoadWorldFile(string fileName)
    {
        string path = $"{WorldsDir}/{fileName}";
        if (!FileAccess.FileExists(path)) return false;
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<WorldFileData>(file.GetAsText(), opts);
            if (data == null) return false;
            WorldName        = data.Name;
            WorldSeed        = data.Seed;
            WorldGridSize    = data.GridSize;
            LocalMapWidth    = data.LocalMapWidth;
            LocalMapHeight   = data.LocalMapHeight;
            ElevBias         = data.ElevBias;
            RainBias         = data.RainBias;
            TempBias         = data.TempBias;
            MagicBias        = data.MagicBias;
            ResourceScarcity = Mathf.Clamp(data.ResourceScarcity, 0.25f, 1.0f);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[World] Failed to load world file '{fileName}': {ex.Message}");
            return false;
        }
    }

    public void DeleteWorldFile(string fileName)
    {
        string path = $"{WorldsDir}/{fileName}";
        if (FileAccess.FileExists(path))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }

    public List<WorldFileInfo> GetSavedWorlds()
    {
        var list = new List<WorldFileInfo>();
        using var dir = DirAccess.Open(WorldsDir);
        if (dir == null) return list;

        dir.ListDirBegin();
        string fname = dir.GetNext();
        while (fname != "")
        {
            if (!fname.StartsWith('.') && fname.EndsWith(".json"))
            {
                string fpath = $"{WorldsDir}/{fname}";
                try
                {
                    using var file = FileAccess.Open(fpath, FileAccess.ModeFlags.Read);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = JsonSerializer.Deserialize<WorldFileData>(file.GetAsText(), opts);
                    if (data != null)
                    {
                        long modified = 0;
                        try { modified = (long)FileAccess.GetModifiedTime(fpath); } catch { }
                        list.Add(new WorldFileInfo(
                            data.Name, fname, data.Seed, data.GridSize,
                            data.LocalMapWidth, data.LocalMapHeight,
                            data.ElevBias, data.RainBias, data.TempBias, data.MagicBias,
                            modified)
                            { ResourceScarcity = data.ResourceScarcity });
                    }
                }
                catch { }
            }
            fname = dir.GetNext();
        }
        dir.ListDirEnd();

        list.Sort((a, b) => b.LastModifiedUnix.CompareTo(a.LastModifiedUnix));
        return list;
    }

    private void EnsureWorldsDir()
    {
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(WorldsDir)))
            DirAccess.MakeDirAbsolute(ProjectSettings.GlobalizePath(WorldsDir));
    }

    private static string SanitiseName(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in raw.Trim())
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ')
                sb.Append(c);
        return sb.ToString().Trim().Replace(' ', '-').ToLowerInvariant();
    }
}
