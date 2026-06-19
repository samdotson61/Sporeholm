namespace SporeholmLauncher.Core;

/// <summary>A mod is just a self-describing folder under mods/. mod.json carries
/// its display metadata; load-order.json (written by the launcher) carries the
/// player's enable/disable + ordering. The in-game modding API is a later phase —
/// the launcher owns the LAYOUT now so it never has to be reorganised once mods
/// arrive.</summary>
public sealed class ModInfo
{
    public string Id { get; set; } = "";          // folder name — the stable key
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
}

public sealed class ModLoadOrder
{
    public List<ModEntry> Mods { get; set; } = new();
}

public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class ModManager
{
    /// <summary>All mods found under mods/, in load order (unlisted mods sort last by name).</summary>
    public static List<ModInfo> List()
    {
        LauncherPaths.EnsureDirs();
        var order = LauncherJson.Read<ModLoadOrder>(LauncherPaths.ModLoadOrderFile) ?? new ModLoadOrder();
        var byId = new Dictionary<string, (ModEntry entry, int index)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Mods.Count; i++) byId[order.Mods[i].Id] = (order.Mods[i], i);

        var result = new List<ModInfo>();
        foreach (var folder in Directory.EnumerateDirectories(LauncherPaths.ModsDir))
        {
            var id = Path.GetFileName(folder);
            var info = LauncherJson.Read<ModInfo>(Path.Combine(folder, "mod.json")) ?? new ModInfo { Name = id };
            info.Id = id;
            if (string.IsNullOrEmpty(info.Name)) info.Name = id;
            if (byId.TryGetValue(id, out var o)) { info.Enabled = o.entry.Enabled; info.Order = o.index; }
            else info.Order = int.MaxValue;
            result.Add(info);
        }
        return result.OrderBy(m => m.Order).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Persist the given mods as the load order (the game reads this at startup).</summary>
    public static void SaveOrder(IEnumerable<ModInfo> mods)
    {
        var lo = new ModLoadOrder
        {
            Mods = mods.Select(m => new ModEntry { Id = m.Id, Enabled = m.Enabled }).ToList(),
        };
        LauncherJson.Write(LauncherPaths.ModLoadOrderFile, lo);
    }

    public static void SetEnabled(string id, bool enabled)
    {
        var mods = List();
        var m = mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No mod named '{id}'.");
        m.Enabled = enabled;
        SaveOrder(mods);
    }

    /// <summary>Shift a mod up (delta -1) or down (delta +1) in load order.</summary>
    public static void Move(string id, int delta)
    {
        var mods = List();
        int idx = mods.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new InvalidOperationException($"No mod named '{id}'.");
        int target = Math.Clamp(idx + delta, 0, mods.Count - 1);
        if (target == idx) return;
        var item = mods[idx];
        mods.RemoveAt(idx);
        mods.Insert(target, item);
        SaveOrder(mods);
    }
}
