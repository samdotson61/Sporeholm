using SporeholmLauncher.Core;
using Xunit;

namespace SporeholmLauncher.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("v0.8.9", 0, 8, 9)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("0.8.10", 0, 8, 10)]
    [InlineData("  v2.0.0  ", 2, 0, 0)]
    [InlineData("1.0.0-beta.1", 1, 0, 0)]      // pre-release suffix ignored for ordering
    [InlineData("1.0.0+build42", 1, 0, 0)]     // build metadata ignored
    [InlineData("3", 3, 0, 0)]                 // missing components default to 0
    [InlineData("3.1", 3, 1, 0)]
    public void Parses_expected_forms(string input, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(input, out var v));
        Assert.Equal((major, minor, patch), (v.Major, v.Minor, v.Patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v.1.2")]
    public void Rejects_garbage(string? input)
    {
        Assert.False(SemVer.TryParse(input, out var v));
        Assert.Equal(SemVer.Zero, v);
        Assert.Equal(SemVer.Zero, SemVer.Parse(input)); // Parse falls back to Zero, never throws
    }

    [Fact]
    public void Orders_numerically_not_lexically()
    {
        // The self-update comparison depends on this: 0.8.10 > 0.8.9 (string compare would say otherwise).
        Assert.True(SemVer.Parse("v0.8.10") > SemVer.Parse("v0.8.9"));
        Assert.True(SemVer.Parse("v1.0.2") > SemVer.Parse("v1.0.1"));
        Assert.True(SemVer.Parse("v0.9.0") > SemVer.Parse("v0.8.99"));
        Assert.True(SemVer.Parse("v1.0.0") >= SemVer.Parse("1.0.0"));
        Assert.False(SemVer.Parse("v1.0.0") > SemVer.Parse("1.0.0")); // v-prefix is display-only
    }

    [Fact]
    public void Raw_string_is_preserved_for_display()
    {
        Assert.Equal("v0.8.9", SemVer.Parse("v0.8.9").ToString());
    }
}
