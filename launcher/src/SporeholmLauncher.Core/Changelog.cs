using System.Text.RegularExpressions;

namespace SporeholmLauncher.Core;

/// <summary>One parsed changelog/news entry for the launcher's news feed.</summary>
public sealed record NewsEntry(string Version, string? Date, string Title, string Body)
{
    /// <summary>"v0.8.9    Title" for the feed header (binding helper for the GUI).</summary>
    public string Header => string.IsNullOrEmpty(Title) ? Version : $"{Version}    {Title}";
    public bool HasDate => !string.IsNullOrEmpty(Date);
}

/// <summary>Parses the game's changelog.md into a news feed. Handles the
/// project's heading format — "## [0.8.9] — 2026-06-16 — Agricultural audit fixes"
/// — and is forgiving of variations (missing date, hyphen instead of em-dash,
/// no brackets).</summary>
public static class Changelog
{
    public static List<NewsEntry> Parse(string? markdown, int max = 40)
    {
        var entries = new List<NewsEntry>();
        if (string.IsNullOrWhiteSpace(markdown)) return entries;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && !IsHeading(lines[i])) i++;     // skip the file preamble

        while (i < lines.Length && entries.Count < max)
        {
            var heading = lines[i].Substring(3).Trim();           // text after "## "
            i++;
            var body = new List<string>();
            while (i < lines.Length && !IsHeading(lines[i]))
            {
                if (lines[i].Trim() != "---") body.Add(CleanBodyLine(lines[i]));
                i++;
            }
            var (version, date, title) = SplitHeading(heading);
            entries.Add(new NewsEntry(version, date, title, string.Join("\n", body).Trim()));
        }
        return entries;
    }

    /// <summary>The feed renders plain text, so markdown markers would show literally
    /// (v1.0.1 displayed raw "###" section markers). Sub-headings keep their text,
    /// list markers become a bullet glyph, and bold markers are dropped.</summary>
    internal static string CleanBodyLine(string line)
    {
        var m = Regex.Match(line, @"^(\s*)(#{1,6})\s+(.*)$");
        if (m.Success) return m.Groups[1].Value + m.Groups[3].Value.TrimEnd();
        m = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
        if (m.Success) line = m.Groups[1].Value + "• " + m.Groups[2].Value;
        return line.Replace("**", "");
    }

    private static bool IsHeading(string line) => line.StartsWith("## ", StringComparison.Ordinal);

    private static (string version, string? date, string title) SplitHeading(string h)
    {
        string version = h;
        var lb = h.IndexOf('['); var rb = h.IndexOf(']');
        if (lb >= 0 && rb > lb) version = h.Substring(lb + 1, rb - lb - 1).Trim();
        else
        {
            // No brackets ("## 0.7.0 - date - title"): the version is the first segment,
            // not the whole heading.
            var first = h.Split(new[] { " — ", " – ", " - " }, StringSplitOptions.None)[0].Trim();
            if (first.Length > 0) version = first;
        }

        var parts = h.Split(new[] { " — ", " – ", " - " }, StringSplitOptions.None);
        string? date = null;
        foreach (var p in parts)
            if (Regex.IsMatch(p.Trim(), @"^\d{4}-\d{2}-\d{2}$")) { date = p.Trim(); break; }

        var title = parts.Length > 1 ? parts[^1].Trim() : "";
        if (string.IsNullOrEmpty(title) || title == version) title = "";
        return (version, date, title);
    }
}

/// <summary>Fetches + parses the changelog from the configured release source
/// (published alongside the manifest as changelog.md). Returns an empty list if
/// the source has no changelog or can't be reached — the news feed is never fatal.</summary>
public static class NewsService
{
    public const string FileName = "changelog.md";

    public static async Task<List<NewsEntry>> GetAsync(IReleaseSource source, CancellationToken ct = default)
    {
        var md = await source.TryGetTextAsync(FileName, ct);
        return Changelog.Parse(md);
    }
}
