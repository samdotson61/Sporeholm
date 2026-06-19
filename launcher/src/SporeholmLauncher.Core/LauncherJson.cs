using System.Text.Json;
using System.Text.Json.Serialization;

namespace SporeholmLauncher.Core;

/// <summary>Shared JSON settings + tiny read/write helpers for the launcher's
/// config / manifest / state files. camelCase on disk, enums as strings,
/// tolerant of missing/unknown keys so old files load cleanly.</summary>
public static class LauncherJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options); }
        catch { return null; }   // a corrupt config should never crash the launcher
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
