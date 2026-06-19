namespace SporeholmLauncher.Core;

/// <summary>Identity of the launcher itself — distinct from the game version. Bump this when
/// the launcher binary changes; a release whose manifest advertises a newer launcher version
/// triggers the launcher to update itself (see <see cref="LauncherSelfUpdater"/>).</summary>
public static class LauncherInfo
{
    public const string Version = "1.0.0";
}
