namespace SporeholmLauncher.Core;

/// <summary>What is currently installed, recorded at
/// <see cref="LauncherPaths.InstalledFile"/>. <see cref="PreviousVersion"/> backs
/// the one-step rollback (the retained game.previous build).</summary>
public sealed class InstalledState
{
    public string? Version { get; set; }
    public string? InstalledUtc { get; set; }
    public string? PreviousVersion { get; set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(Version) && Directory.Exists(LauncherPaths.InstallDir);

    public static InstalledState Load() =>
        LauncherJson.Read<InstalledState>(LauncherPaths.InstalledFile) ?? new InstalledState();

    public void Save() => LauncherJson.Write(LauncherPaths.InstalledFile, this);
}
