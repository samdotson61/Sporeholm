namespace SporeholmLauncher.Core;

/// <summary>The published manifest that tells the launcher what the latest build
/// is and where to get it. One small JSON file per channel, produced by the
/// packaging script and published alongside the build zips.</summary>
public sealed class Manifest
{
    /// <summary>Latest version string, e.g. "v0.8.9". For a GitHub source the launcher
    /// overrides this with the actual latest release tag; url/folder sources use it as-is.</summary>
    public string Version { get; set; } = "";

    public string Channel { get; set; } = "stable";

    /// <summary>Short "what's new" excerpt (pulled from the changelog) shown on the Play screen.</summary>
    public string? Notes { get; set; }

    /// <summary>ISO-8601 release timestamp (informational).</summary>
    public string? ReleasedUtc { get; set; }

    /// <summary>Per-OS download: key is "windows" | "macos" | "linux".</summary>
    public Dictionary<string, ManifestFile> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ManifestFile? FileFor(string os) => Files.TryGetValue(os, out var f) ? f : null;

    /// <summary>Optional: the launcher's own latest version + per-OS download, so the launcher
    /// can update itself the same way it updates the game. Absent on older releases.</summary>
    public LauncherManifest? Launcher { get; set; }
}

/// <summary>The launcher's own published version + per-OS binary (windows: the .exe;
/// linux: the bare binary; macos: the .app zip).</summary>
public sealed class LauncherManifest
{
    public string Version { get; set; } = "";
    public Dictionary<string, ManifestFile> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ManifestFile? FileFor(string os) => Files.TryGetValue(os, out var f) ? f : null;
}

public sealed class ManifestFile
{
    /// <summary>Asset file name (e.g. "Sporeholm-windows.zip"). Resolved against the
    /// release source's base location unless <see cref="Url"/> is set.</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional absolute override URL for this asset.</summary>
    public string? Url { get; set; }

    /// <summary>Lower-case hex SHA-256 of the zip. Verified before install; empty = skip (not recommended).</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Uncompressed-download size in bytes (used for the progress bar when the server omits Content-Length).</summary>
    public long Size { get; set; }
}
