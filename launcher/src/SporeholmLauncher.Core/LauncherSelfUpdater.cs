using System.Diagnostics;
using System.IO.Compression;

namespace SporeholmLauncher.Core;

/// <summary>Lets the launcher update ITSELF, the same way it updates the game: when a release
/// advertises a newer launcher version, download the matching launcher for this OS, verify its
/// checksum, swap it in for the running one, and relaunch. Best-effort — if the launcher lives
/// somewhere it can't write (e.g. Program Files), it throws and the caller tells the user to
/// download manually. The previous launcher is left as a ".old" sibling and cleaned up on the
/// next start (a running binary can't delete itself).</summary>
public sealed class LauncherSelfUpdater
{
    private readonly IReleaseSource _source;
    public LauncherSelfUpdater(IReleaseSource source) => _source = source;

    /// <summary>True if the manifest advertises a newer launcher for this OS.</summary>
    public bool Available(Manifest manifest, out string latest, out ManifestFile? file)
    {
        latest = manifest.Launcher?.Version ?? "";
        file = manifest.Launcher?.FileFor(LauncherPaths.CurrentOs);
        return !string.IsNullOrEmpty(latest) && file != null
               && SemVer.Parse(latest) > SemVer.Parse(LauncherInfo.Version);
    }

    /// <summary>Download + verify + swap in the new launcher, then start it. Returns when the new
    /// launcher has been started — the caller must exit promptly so only one instance runs.</summary>
    public async Task ApplyAsync(ManifestFile file, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Can't determine the launcher's own path.");
        LauncherPaths.EnsureDirs();
        var staging = Path.Combine(LauncherPaths.DownloadsDir, "launcher-update" + Path.GetExtension(file.Name));

        if (File.Exists(staging)) File.Delete(staging);
        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, "Downloading launcher update…"));
        await _source.DownloadAsync(file, staging, new Progress<DownloadProgress>(d =>
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, d.Fraction, $"Downloading launcher… {Pct(d.Fraction)}"))), ct);

        progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, "Verifying launcher…"));
        if (!string.IsNullOrEmpty(file.Sha256))
        {
            var actual = await Updater.ComputeSha256Async(staging, ct);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(staging);
                throw new InvalidDataException("Launcher update checksum mismatch — discarded.");
            }
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Installing, 0, "Applying launcher update…"));
        var os = LauncherPaths.CurrentOs;
        var target = os == "macos" ? SwapMacApp(exe, staging)   // staging = the .app zip
                                   : SwapBinary(exe, staging);  // staging = the bare binary

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 1, "Restarting launcher…"));
        Relaunch(target, os);
    }

    // windows / linux: the launcher is a single file. Rename the running file aside, move the new in.
    private static string SwapBinary(string exe, string staging)
    {
        var old = exe + ".old";
        DeleteQuiet(old, dir: false);
        File.Move(exe, old);            // a running executable can be renamed (Windows) / replaced (Unix)
        File.Move(staging, exe);
        if (LauncherPaths.CurrentOs == "linux") Run("chmod", $"+x \"{exe}\"");
        return exe;
    }

    // macos: the launcher is a .app bundle; replace the whole bundle directory, relaunch via `open`.
    private static string SwapMacApp(string exe, string stagingZip)
    {
        var appPath = FindAppRoot(exe)
                      ?? throw new InvalidOperationException("Launcher isn't running from a .app bundle.");
        var extract = Path.Combine(Path.GetTempPath(), "sporeholm-lupd-" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(stagingZip, extract);
        var newApp = Directory.EnumerateDirectories(extract, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new InvalidDataException("No .app found inside the launcher update.");

        var old = appPath + ".old";
        DeleteQuiet(old, dir: true);
        Directory.Move(appPath, old);  // the running bundle can be renamed; open file handles survive on macOS
        Directory.Move(newApp, appPath);
        Run("chmod", $"-R +x \"{Path.Combine(appPath, "Contents", "MacOS")}\"");
        Run("xattr", $"-dr com.apple.quarantine \"{appPath}\"");
        DeleteQuiet(stagingZip, dir: false);
        return appPath;
    }

    private static void Relaunch(string target, string os)
    {
        var psi = os == "macos"
            ? new ProcessStartInfo { FileName = "open", Arguments = $"\"{target}\"", UseShellExecute = false }
            : new ProcessStartInfo
              {
                  FileName = target,
                  UseShellExecute = os == "windows",
                  WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
              };
        Process.Start(psi);
    }

    /// <summary>On startup, remove a leftover ".old" launcher from a previous self-update (a
    /// running binary can't delete itself, so cleanup happens on the next launch).</summary>
    public static void CleanupStale()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            DeleteQuiet(exe + ".old", dir: false);
            var app = FindAppRoot(exe);
            if (app != null) DeleteQuiet(app + ".old", dir: true);
        }
        catch { /* never block startup on cleanup */ }
    }

    private static string? FindAppRoot(string path)
    {
        var dir = (string?)path;
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static void DeleteQuiet(string path, bool dir)
    {
        try
        {
            if (dir) { if (Directory.Exists(path)) Directory.Delete(path, true); }
            else { if (File.Exists(path)) File.Delete(path); }
        }
        catch { /* best effort */ }
    }

    private static void Run(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = false })?.WaitForExit(3000); }
        catch { /* best effort */ }
    }

    private static string Pct(double f) => $"{(int)Math.Round(f * 100)}%";
}
