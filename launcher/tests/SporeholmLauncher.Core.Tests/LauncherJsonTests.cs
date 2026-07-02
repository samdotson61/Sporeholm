using SporeholmLauncher.Core;
using Xunit;

namespace SporeholmLauncher.Core.Tests;

public class LauncherJsonTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("splt-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Round_trips_camelCase_on_disk()
    {
        var path = Path.Combine(_dir, "m.json");
        LauncherJson.Write(path, new Manifest { Version = "v1.2.3", Channel = "stable" });

        var text = File.ReadAllText(path);
        Assert.Contains("\"version\"", text);            // camelCase keys on disk
        Assert.DoesNotContain("\"Version\"", text);

        var back = LauncherJson.Read<Manifest>(path)!;
        Assert.Equal("v1.2.3", back.Version);
    }

    [Fact]
    public void Corrupt_file_reads_as_null_instead_of_throwing()
    {
        var path = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(path, "{ not json !!");
        Assert.Null(LauncherJson.Read<Manifest>(path));   // a bad config must never crash the launcher
    }

    [Fact]
    public void Missing_file_reads_as_null()
        => Assert.Null(LauncherJson.Read<Manifest>(Path.Combine(_dir, "nope.json")));

    [Fact]
    public void Write_creates_parent_directories()
    {
        var path = Path.Combine(_dir, "a", "b", "c.json");
        LauncherJson.Write(path, new Manifest { Version = "v1" });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Unknown_keys_are_tolerated()
    {
        var path = Path.Combine(_dir, "extra.json");
        File.WriteAllText(path, """{ "version": "v2", "someFutureField": 42 }""");
        Assert.Equal("v2", LauncherJson.Read<Manifest>(path)!.Version);
    }
}
