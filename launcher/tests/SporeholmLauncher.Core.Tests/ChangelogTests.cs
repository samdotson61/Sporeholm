using SporeholmLauncher.Core;
using Xunit;

namespace SporeholmLauncher.Core.Tests;

public class ChangelogTests
{
    // A miniature of the game's real changelog.md format.
    private const string Sample = """
    # Sporeholm — Changelog

    Version format: `aa.bb.cc`

    ---

    ## [0.8.10] — 2026-07-01 — macOS release repaired

    Intro line.

    ### The three defects
    - **No executable permissions** — zips built on Windows.
    * Second bullet with star marker.

    ---

    ## [0.8.9] — 2026-06-16 — Agricultural audit fixes

    Body of the older entry.

    ## 0.7.0 - 2026-05-01 - Combat
    No brackets, plain hyphens.
    """;

    [Fact]
    public void Parses_entries_with_version_date_and_title()
    {
        var entries = Changelog.Parse(Sample);

        Assert.Equal(3, entries.Count);
        Assert.Equal("0.8.10", entries[0].Version);
        Assert.Equal("2026-07-01", entries[0].Date);
        Assert.Equal("macOS release repaired", entries[0].Title);
        Assert.True(entries[0].HasDate);

        Assert.Equal("0.8.9", entries[1].Version);
        Assert.Equal("0.7.0", entries[2].Version);       // bracket-less + hyphen variant
        Assert.Equal("Combat", entries[2].Title);
    }

    [Fact]
    public void Body_has_no_markdown_markers_and_no_separators()
    {
        var body = Changelog.Parse(Sample)[0].Body;

        Assert.DoesNotContain("###", body);              // headings keep text, lose markers
        Assert.Contains("The three defects", body);
        Assert.DoesNotContain("**", body);               // bold markers dropped
        Assert.DoesNotContain("\n---", "\n" + body);     // separators dropped
        Assert.Contains("• No executable permissions", body);
        Assert.Contains("• Second bullet", body);        // '*' bullets too
    }

    [Theory]
    [InlineData("### Heading text", "Heading text")]
    [InlineData("  ## Indented", "  Indented")]
    [InlineData("- bullet", "• bullet")]
    [InlineData("* star bullet", "• star bullet")]
    [InlineData("  - nested bullet", "  • nested bullet")]
    [InlineData("plain **bold** text", "plain bold text")]
    [InlineData("no markers here", "no markers here")]
    [InlineData("a - b in prose stays", "a - b in prose stays")]
    public void CleanBodyLine_cases(string input, string expected)
        => Assert.Equal(expected, Changelog.CleanBodyLine(input));

    [Fact]
    public void Empty_or_missing_changelog_yields_empty_feed()
    {
        Assert.Empty(Changelog.Parse(null));
        Assert.Empty(Changelog.Parse(""));
        Assert.Empty(Changelog.Parse("no headings at all"));
    }

    [Fact]
    public void Max_caps_the_feed_length()
    {
        var many = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"## [0.0.{i}] — title\nbody"));
        Assert.Equal(40, Changelog.Parse(many).Count);           // default cap
        Assert.Equal(5, Changelog.Parse(many, max: 5).Count);
    }
}
