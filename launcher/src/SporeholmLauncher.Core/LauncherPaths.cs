using System.Runtime.InteropServices;

namespace SporeholmLauncher.Core;

/// <summary>Every path the launcher uses, in one place. Everything lives under a
/// single per-user data folder; the launcher ONLY ever creates/replaces the
/// folders below it. The game's own save files (Godot's user:// directory) live
/// elsewhere and are never touched — that is what makes updates "save-safe by
/// construction".</summary>
public static class LauncherPaths
{
    public const string AppFolderName = "Sporeholm";

    /// <summary>Per-user data root: %APPDATA%\Sporeholm (Windows),
    /// ~/Library/Application Support/Sporeholm (macOS),
    /// $XDG_DATA_HOME|~/.local/share/Sporeholm (Linux).</summary>
    public static string DataDir
    {
        get
        {
            // Test / explicit override: point all launcher state at a custom folder.
            var overrideDir = Environment.GetEnvironmentVariable("SPOREHOLM_LAUNCHER_DATA");
            if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;

            // Portable / "prebuilt with the game" mode: if a portable.txt marker sits
            // next to the launcher executable, keep everything (the game, updates, mods,
            // config) in the launcher's own folder. This is how the bundled download
            // works — unzip, double-click, and the launcher updates + plays the
            // co-located game. No marker → per-user install (the AppData paths below).
            var exeDir = ExeDir();
            if (exeDir != null && File.Exists(Path.Combine(exeDir, "portable.txt"))) return exeDir;

            string baseDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                baseDir = Path.Combine(Home, "Library", "Application Support");
            else
                baseDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                          is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".local", "share");
            return Path.Combine(baseDir, AppFolderName);
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Directory containing the running launcher executable (single-file safe).</summary>
    public static string? ExeDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return Path.GetDirectoryName(p);
        }
        catch { /* fall through */ }
        return AppContext.BaseDirectory;
    }

    /// <summary>True when running as a co-located bundle (portable.txt next to the exe).</summary>
    public static bool IsPortable
    {
        get { var d = ExeDir(); return d != null && File.Exists(Path.Combine(d, "portable.txt")); }
    }

    public static string InstallDir       => Path.Combine(DataDir, "game");           // the live build
    public static string PreviousDir      => Path.Combine(DataDir, "game.previous");   // retained for one rollback
    public static string ModsDir          => Path.Combine(DataDir, "mods");
    public static string DownloadsDir     => Path.Combine(DataDir, "downloads");
    public static string ConfigFile       => Path.Combine(DataDir, "launcher.json");
    public static string InstalledFile    => Path.Combine(DataDir, "installed.json");
    public static string ModLoadOrderFile => Path.Combine(ModsDir, "load-order.json");

    /// <summary>"windows" | "macos" | "linux" — the manifest key for the current OS.</summary>
    public static string CurrentOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macos"   : "linux";

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(DownloadsDir);
    }
}
