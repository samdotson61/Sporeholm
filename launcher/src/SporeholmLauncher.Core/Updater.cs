using System.IO.Compression;
using System.Security.Cryptography;

namespace SporeholmLauncher.Core;

public enum UpdateState { UpToDate, UpdateAvailable, NotInstalled, NoBuildForThisOs }

public sealed class UpdateCheck
{
    public UpdateState State { get; init; }
    public Manifest? Manifest { get; init; }
    /// <summary>True if a runnable build is present (recorded in installed.json
    /// OR detected on disk). <see cref="InstalledVersion"/> is null when the build
    /// is detected on disk but has no version record.</summary>
    public bool IsInstalled { get; init; }
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public ManifestFile? File { get; init; }
}

public enum UpdatePhase { Downloading, Verifying, Backing, Installing, Done }
public readonly record struct UpdateProgress(UpdatePhase Phase, double Fraction, string Message);

/// <summary>The heart of the launcher: check for a newer build, then
/// download → verify (SHA-256) → back up the current build → extract the new one.
/// The previous build is retained as game.previous so a bad update can be rolled
/// back in one step. If extraction fails the backup is restored automatically.
/// Nothing outside the launcher's data folders is ever touched.</summary>
public sealed class Updater
{
    private readonly IReleaseSource _source;
    private readonly string _channel;

    public Updater(IReleaseSource source, string channel = "stable")
    {
        _source = source;
        _channel = channel;
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var manifest = await _source.GetManifestAsync(_channel, ct);
        var installed = InstalledState.Load();
        var file = manifest.FileFor(LauncherPaths.CurrentOs);

        // Ground "installed" in what's actually on disk, not just installed.json:
        // a game folder with a runnable build counts as installed even if the
        // record is missing or stale (a hand-copied build, or a lost installed.json).
        bool onDisk = GameLauncher.IsInstalled;
        bool isInstalled = installed.IsInstalled || onDisk;
        string? installedVersion = installed.IsInstalled ? installed.Version : null;

        UpdateState state;
        if (file == null) state = UpdateState.NoBuildForThisOs;
        else if (!isInstalled) state = UpdateState.NotInstalled;
        else if (installedVersion == null) state = UpdateState.UpToDate;   // detected on disk, version unknown — playable, no redundant reinstall
        else state = SemVer.Parse(manifest.Version) > SemVer.Parse(installedVersion)
            ? UpdateState.UpdateAvailable : UpdateState.UpToDate;

        return new UpdateCheck
        {
            State = state,
            Manifest = manifest,
            IsInstalled = isInstalled,
            InstalledVersion = installedVersion,
            LatestVersion = manifest.Version,
            File = file,
        };
    }

    /// <summary>Download, verify and install the given build, retaining the prior
    /// build for rollback. Throws (and self-heals) on any failure.</summary>
    public async Task ApplyAsync(Manifest manifest, ManifestFile file, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        LauncherPaths.EnsureDirs();

        // 1. Download to a .partial file so an interrupted download is never mistaken for a finished one.
        var zipPath = Path.Combine(LauncherPaths.DownloadsDir, file.Name);
        var partial = zipPath + ".partial";
        if (File.Exists(partial)) File.Delete(partial);
        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, $"Downloading {manifest.Version}…"));
        await _source.DownloadAsync(file, partial,
            new Progress<DownloadProgress>(d => progress?.Report(
                new UpdateProgress(UpdatePhase.Downloading, d.Fraction, $"Downloading {manifest.Version}… {Pct(d.Fraction)}"))),
            ct);

        // 2. Verify checksum before trusting the bytes.
        progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, "Verifying download…"));
        if (!string.IsNullOrEmpty(file.Sha256))
        {
            var actual = await Sha256Async(partial, ct);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException(
                    $"Checksum mismatch for {file.Name} — the download was corrupted or tampered with and has been discarded.");
            }
        }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(partial, zipPath);

        // 3. Back up the current build (move, don't copy — instant + space-free).
        progress?.Report(new UpdateProgress(UpdatePhase.Backing, 0, "Backing up current build…"));
        var installed = InstalledState.Load();
        var prevVersion = installed.Version;
        if (Directory.Exists(LauncherPaths.InstallDir))
        {
            if (Directory.Exists(LauncherPaths.PreviousDir)) Directory.Delete(LauncherPaths.PreviousDir, true);
            Directory.Move(LauncherPaths.InstallDir, LauncherPaths.PreviousDir);
        }

        // 4. Extract the new build. If anything fails, restore the backup so the
        //    player is never left without a working install.
        progress?.Report(new UpdateProgress(UpdatePhase.Installing, 0, $"Installing {manifest.Version}…"));
        try
        {
            Directory.CreateDirectory(LauncherPaths.InstallDir);
            ZipFile.ExtractToDirectory(zipPath, LauncherPaths.InstallDir, overwriteFiles: true);
        }
        catch
        {
            try { if (Directory.Exists(LauncherPaths.InstallDir)) Directory.Delete(LauncherPaths.InstallDir, true); } catch { /* best effort */ }
            if (Directory.Exists(LauncherPaths.PreviousDir))
                Directory.Move(LauncherPaths.PreviousDir, LauncherPaths.InstallDir);
            throw;
        }

        new InstalledState
        {
            Version = manifest.Version,
            InstalledUtc = DateTime.UtcNow.ToString("o"),
            PreviousVersion = prevVersion,
        }.Save();

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 1, $"Installed {manifest.Version}."));
    }

    public bool CanRollback() => Directory.Exists(LauncherPaths.PreviousDir);

    /// <summary>Swap the current and previous builds back. One level deep — you
    /// can undo the last update, then re-update to go forward again.</summary>
    public void Rollback()
    {
        if (!Directory.Exists(LauncherPaths.PreviousDir))
            throw new InvalidOperationException("There is no previous build to roll back to.");

        var installed = InstalledState.Load();
        var swap = LauncherPaths.InstallDir + ".swap";
        if (Directory.Exists(swap)) Directory.Delete(swap, true);

        if (Directory.Exists(LauncherPaths.InstallDir)) Directory.Move(LauncherPaths.InstallDir, swap);
        Directory.Move(LauncherPaths.PreviousDir, LauncherPaths.InstallDir);
        if (Directory.Exists(swap)) Directory.Move(swap, LauncherPaths.PreviousDir);

        new InstalledState
        {
            Version = installed.PreviousVersion,
            InstalledUtc = DateTime.UtcNow.ToString("o"),
            PreviousVersion = installed.Version,
        }.Save();
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>SHA-256 of an arbitrary file (used by the packaging script via the CLI too).</summary>
    public static Task<string> ComputeSha256Async(string path, CancellationToken ct = default) => Sha256Async(path, ct);

    private static string Pct(double f) => $"{(int)Math.Round(f * 100)}%";
}
