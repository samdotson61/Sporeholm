using SporeholmLauncher.Core;
using Xunit;

namespace SporeholmLauncher.Core.Tests;

public class ManifestTests
{
    // Mirrors the real published shape (see samples/manifest.json + the live release).
    private const string FullManifest = """
    {
      "version": "v0.8.9",
      "channel": "stable",
      "notes": "What's new…",
      "releasedUtc": "2026-06-16T00:00:00Z",
      "files": {
        "windows": { "name": "Sporeholm-windows.zip", "sha256": "aa", "size": 1 },
        "linux":   { "name": "Sporeholm-linux.zip",   "sha256": "bb", "size": 2 },
        "macos":   { "name": "Sporeholm-macos.zip",   "sha256": "cc", "size": 3 }
      },
      "launcher": {
        "version": "1.0.2",
        "files": {
          "windows": { "name": "SporeholmLauncher.exe",       "sha256": "dd", "size": 4 },
          "linux":   { "name": "SporeholmLauncher-linux",     "sha256": "ee", "size": 5 },
          "macos":   { "name": "SporeholmLauncher-macos.zip", "sha256": "ff", "size": 6 }
        }
      }
    }
    """;

    [Fact]
    public void Parses_full_manifest_including_launcher_section()
    {
        var m = System.Text.Json.JsonSerializer.Deserialize<Manifest>(FullManifest, LauncherJson.Options)!;

        Assert.Equal("v0.8.9", m.Version);
        Assert.Equal(3, m.Files.Count);
        Assert.Equal("Sporeholm-macos.zip", m.FileFor("macos")!.Name);
        Assert.Equal("cc", m.FileFor("macos")!.Sha256);

        Assert.NotNull(m.Launcher);
        Assert.Equal("1.0.2", m.Launcher!.Version);
        Assert.Equal("SporeholmLauncher-macos.zip", m.Launcher.FileFor("macos")!.Name);
    }

    [Fact]
    public void FileFor_is_case_insensitive_and_null_for_unknown_os()
    {
        var m = System.Text.Json.JsonSerializer.Deserialize<Manifest>(FullManifest, LauncherJson.Options)!;
        Assert.NotNull(m.FileFor("MacOS"));
        Assert.NotNull(m.FileFor("WINDOWS"));
        Assert.Null(m.FileFor("freebsd"));
    }

    [Fact]
    public void Launcher_section_is_optional_for_older_releases()
    {
        const string old = """{ "version": "v0.8.5", "files": {} }""";
        var m = System.Text.Json.JsonSerializer.Deserialize<Manifest>(old, LauncherJson.Options)!;
        Assert.Null(m.Launcher);
        Assert.Null(m.FileFor("windows"));
    }

    [Fact]
    public void Self_update_triggers_only_on_strictly_newer_launcher_version()
    {
        // LauncherSelfUpdater.Available() gates on SemVer(manifest) > SemVer(LauncherInfo.Version).
        var current = SemVer.Parse(LauncherInfo.Version);
        Assert.False(SemVer.Parse(LauncherInfo.Version) > current);            // same → no update
        var older = SemVer.Parse("0.9.9");
        Assert.False(older > current);                                          // older → no update
        var newer = new SemVer(current.Major, current.Minor, current.Patch + 1, "next");
        Assert.True(newer > current);                                           // newer patch → update
    }
}
