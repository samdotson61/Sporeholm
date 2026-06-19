using System.Net.Http;
using System.Text.Json;

namespace SporeholmLauncher.Core;

public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : 0;
}

/// <summary>A release the player can choose to install (from the GitHub releases list).</summary>
public sealed record ReleaseInfo(string Tag, string Name, string? PublishedUtc, bool PreRelease)
{
    public string Display => string.IsNullOrWhiteSpace(Name) || Name == Tag ? Tag : $"{Tag}  —  {Name}";
}

/// <summary>Where the launcher fetches the manifest + build zips. Three
/// interchangeable implementations (GitHub Releases / web URL / local folder)
/// so the same launcher works against any of them by config alone.</summary>
public interface IReleaseSource
{
    string Describe();
    Task<Manifest> GetManifestAsync(string channel, CancellationToken ct = default);
    Task DownloadAsync(ManifestFile file, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct = default);
    /// <summary>Fetch a small text asset (e.g. changelog.md) from the source. Returns null if absent/unreachable.</summary>
    Task<string?> TryGetTextAsync(string fileName, CancellationToken ct = default);
    /// <summary>List installable releases (GitHub → the repo's releases; URL/folder → empty, they expose one manifest).</summary>
    Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct = default);
}

public static class ReleaseSourceFactory
{
    public static IReleaseSource Create(LauncherConfig cfg) => cfg.SourceKind switch
    {
        ReleaseSourceKind.Folder => new FolderReleaseSource(
            cfg.FolderPath ?? throw new InvalidOperationException("Source is 'folder' but folderPath is not set in launcher.json.")),
        ReleaseSourceKind.Url => new UrlReleaseSource(
            cfg.BaseUrl ?? throw new InvalidOperationException("Source is 'url' but baseUrl is not set in launcher.json.")),
        _ => new GitHubReleaseSource(cfg.GitHubOwner, cfg.GitHubRepo, cfg.SelectedRelease),
    };
}

/// <summary>Shared HTTP plumbing for the GitHub + URL sources, including a
/// streaming download with progress reporting.</summary>
public abstract class HttpReleaseSource : IReleaseSource
{
    protected static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SporeholmLauncher/1.0");
        return c;
    }

    protected abstract string ManifestUrl(string channel);
    protected abstract string AssetUrl(ManifestFile file);
    protected abstract string FileUrl(string fileName);
    public abstract string Describe();

    public virtual async Task<Manifest> GetManifestAsync(string channel, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ManifestUrl(channel), ct);
        return JsonSerializer.Deserialize<Manifest>(json, LauncherJson.Options)
               ?? throw new InvalidDataException("The manifest could not be parsed.");
    }

    public async Task DownloadAsync(ManifestFile file, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var resp = await Http.GetAsync(AssetUrl(file), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength ?? (file.Size > 0 ? file.Size : null);

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buffer = new byte[1 << 16];
        long received = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            progress?.Report(new DownloadProgress(received, total));
        }
    }

    public async Task<string?> TryGetTextAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(FileUrl(fileName), ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch { return null; }
    }

    public virtual Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
}

/// <summary>GitHub Releases. Uses the stable download URLs (no API token needed for
/// a public repo): latest → /releases/latest/download/{asset}; a specific tag →
/// /releases/download/{tag}/{asset}. ListReleasesAsync uses the public API so the
/// player can pick any uploaded release in Settings.</summary>
public sealed class GitHubReleaseSource : HttpReleaseSource
{
    private readonly string _owner, _repo;
    private readonly string? _tag;   // null → latest

    public GitHubReleaseSource(string owner, string repo, string? tag = null)
    {
        _owner = owner; _repo = repo; _tag = NormalizeTag(tag);
    }

    private static string? NormalizeTag(string? t) =>
        string.IsNullOrWhiteSpace(t) || t.Equals("latest", StringComparison.OrdinalIgnoreCase) ? null : t.Trim();

    private string? _resolvedTag;   // the real latest release tag, resolved from the API when tracking "latest"
    private bool _resolved;

    private string Base
    {
        get
        {
            var tag = _tag ?? _resolvedTag;
            return tag == null
                ? $"https://github.com/{_owner}/{_repo}/releases/latest/download"
                : $"https://github.com/{_owner}/{_repo}/releases/download/{tag}";
        }
    }

    protected override string ManifestUrl(string channel) =>
        channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
            ? $"{Base}/manifest.json" : $"{Base}/manifest-{channel}.json";

    protected override string AssetUrl(ManifestFile file) =>
        !string.IsNullOrEmpty(file.Url) ? file.Url! : $"{Base}/{file.Name}";

    protected override string FileUrl(string fileName) => $"{Base}/{fileName}";

    public override string Describe() => $"GitHub Releases ({_owner}/{_repo}, {_tag ?? "latest"})";

    /// <summary>Take the <i>version</i> from the repo's release data (the latest
    /// release's tag), not the manifest's self-reported field — so "latest" is whatever
    /// GitHub actually published — and pin downloads to that exact tag. Falls back to the
    /// manifest's own version (and the latest/download redirect) if the API is unreachable
    /// or there are no releases yet.</summary>
    public override async Task<Manifest> GetManifestAsync(string channel, CancellationToken ct = default)
    {
        if (_tag == null && !_resolved)
        {
            _resolved = true;
            _resolvedTag = await ResolveLatestTagAsync(ct);
        }
        var manifest = await base.GetManifestAsync(channel, ct);
        var tag = _tag ?? _resolvedTag;
        if (!string.IsNullOrEmpty(tag)) manifest.Version = tag;
        return manifest;
    }

    /// <summary>The latest published (non-draft, non-prerelease) release's tag via the
    /// public API. Null when there are no releases yet, or the API is unreachable.</summary>
    private async Task<string?> ResolveLatestTagAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    public override async Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=30");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<ReleaseInfo>();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = new List<ReleaseInfo>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                var tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(tag)) continue;
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
                var pub  = el.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                var pre  = el.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
                list.Add(new ReleaseInfo(tag, name, pub, pre));
            }
            return list;
        }
        catch { return Array.Empty<ReleaseInfo>(); }
    }
}

/// <summary>A plain static-hosting base URL holding manifest.json + the zips.</summary>
public sealed class UrlReleaseSource : HttpReleaseSource
{
    private readonly string _base;
    public UrlReleaseSource(string baseUrl) { _base = baseUrl.TrimEnd('/'); }

    protected override string ManifestUrl(string channel) =>
        channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
            ? $"{_base}/manifest.json" : $"{_base}/manifest-{channel}.json";

    protected override string AssetUrl(ManifestFile file) =>
        !string.IsNullOrEmpty(file.Url) ? file.Url! : $"{_base}/{file.Name}";

    protected override string FileUrl(string fileName) => $"{_base}/{fileName}";

    public override string Describe() => $"Web URL ({_base})";
}

/// <summary>A local or network folder containing manifest.json + the zips. No
/// network involved — ideal for testing and LAN distribution.</summary>
public sealed class FolderReleaseSource : IReleaseSource
{
    private readonly string _dir;
    public FolderReleaseSource(string dir) { _dir = dir; }

    public string Describe() => $"Local folder ({_dir})";

    public Task<string?> TryGetTextAsync(string fileName, CancellationToken ct = default)
    {
        var p = Path.Combine(_dir, fileName);
        return Task.FromResult(File.Exists(p) ? File.ReadAllText(p) : (string?)null);
    }

    public Task<IReadOnlyList<ReleaseInfo>> ListReleasesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());

    private string ManifestPath(string channel) =>
        Path.Combine(_dir, channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
            ? "manifest.json" : $"manifest-{channel}.json");

    public Task<Manifest> GetManifestAsync(string channel, CancellationToken ct = default)
    {
        var p = ManifestPath(channel);
        if (!File.Exists(p)) throw new FileNotFoundException($"Manifest not found at {p}");
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p), LauncherJson.Options)
                ?? throw new InvalidDataException("The manifest could not be parsed.");
        return Task.FromResult(m);
    }

    public async Task DownloadAsync(ManifestFile file, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var srcPath = !string.IsNullOrEmpty(file.Url) ? file.Url! : Path.Combine(_dir, file.Name);
        if (!File.Exists(srcPath)) throw new FileNotFoundException($"Release asset not found: {srcPath}");
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var s = File.OpenRead(srcPath);
        await using var d = File.Create(destPath);
        long total = s.Length;
        var buffer = new byte[1 << 16];
        long received = 0;
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            await d.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            progress?.Report(new DownloadProgress(received, total));
        }
    }
}
