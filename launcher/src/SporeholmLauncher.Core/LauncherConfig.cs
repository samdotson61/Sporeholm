namespace SporeholmLauncher.Core;

public enum ReleaseSourceKind
{
    /// <summary>Fetch from a GitHub repo's "latest" release assets (default).</summary>
    GitHub,
    /// <summary>Fetch manifest.json + zips from a plain HTTPS base URL.</summary>
    Url,
    /// <summary>Fetch from a local/network folder (great for testing + LAN).</summary>
    Folder,
}

/// <summary>User-editable launcher settings, stored at
/// <see cref="LauncherPaths.ConfigFile"/>. Defaults to this game's GitHub repo;
/// all three source kinds are supported so you can point it anywhere without a
/// code change.</summary>
public sealed class LauncherConfig
{
    public ReleaseSourceKind SourceKind { get; set; } = ReleaseSourceKind.GitHub;

    // GitHub source
    public string GitHubOwner { get; set; } = "samdotson61";
    public string GitHubRepo  { get; set; } = "Sporeholm";

    // Url source
    public string? BaseUrl { get; set; }

    // Folder source
    public string? FolderPath { get; set; }

    public string Channel { get; set; } = "stable";

    /// <summary>Which GitHub release to install/track. null or "latest" = always the
    /// newest; a tag (e.g. "v0.8.9") pins to that release (the Settings dropdown sets this).</summary>
    public string? SelectedRelease { get; set; }

    /// <summary>When true, "Play" checks for and applies an update first.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>When true, the launcher never reaches the network — it just plays the installed build.</summary>
    public bool Offline { get; set; } = false;

    public static LauncherConfig Load() =>
        LauncherJson.Read<LauncherConfig>(LauncherPaths.ConfigFile) ?? new LauncherConfig();

    public void Save() => LauncherJson.Write(LauncherPaths.ConfigFile, this);
}
