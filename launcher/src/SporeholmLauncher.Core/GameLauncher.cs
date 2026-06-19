using System.Diagnostics;

namespace SporeholmLauncher.Core;

/// <summary>Finds and starts the installed game binary for the current OS. The
/// packaging script exports per-OS builds (Sporeholm.exe / Sporeholm.x86_64 /
/// Sporeholm.app); this locates whichever is present and launches it.</summary>
public static class GameLauncher
{
    public static bool IsInstalled => FindExecutable() != null;

    public static string? FindExecutable()
    {
        var dir = LauncherPaths.InstallDir;
        if (!Directory.Exists(dir)) return null;

        var os = LauncherPaths.CurrentOs;

        if (os == "macos")
            return Directory.EnumerateDirectories(dir, "*.app", SearchOption.AllDirectories).FirstOrDefault();

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

        if (os == "windows")
            return files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => Path.GetFileName(f).Contains("Sporeholm", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();

        // linux: prefer a Godot *.x86_64 or anything named Sporeholm; fall back to an extensionless file.
        return files.Where(f => f.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase)
                                || Path.GetFileName(f).Contains("Sporeholm", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.EndsWith(".x86_64", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault()
               ?? files.FirstOrDefault(f => !Path.GetFileName(f).Contains('.'));
    }

    public static Process Launch()
    {
        var exe = FindExecutable()
                  ?? throw new FileNotFoundException("No game build is installed yet — update first.");
        var os = LauncherPaths.CurrentOs;

        ProcessStartInfo psi;
        if (os == "macos")
        {
            psi = new ProcessStartInfo { FileName = "open", Arguments = $"\"{exe}\"", UseShellExecute = false };
        }
        else
        {
            if (os == "linux") TryMakeExecutable(exe);
            psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
            };
        }

        return Process.Start(psi)
               ?? throw new InvalidOperationException("Failed to start the game process.");
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo { FileName = "chmod", Arguments = $"+x \"{path}\"", UseShellExecute = false });
            p?.WaitForExit(2000);
        }
        catch { /* not fatal; the file may already be executable */ }
    }
}
