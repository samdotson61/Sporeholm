namespace SporeholmLauncher.Core;

/// <summary>A forgiving parser for the game's "vMAJOR.MINOR.PATCH" version
/// strings (e.g. "v0.8.9"). Tolerates a leading v/V and any pre-release/build
/// suffix after '-' or '+'. Comparison is numeric on (major, minor, patch);
/// the original string is preserved for display.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Raw) : IComparable<SemVer>
{
    public static readonly SemVer Zero = new(0, 0, 0, "v0.0.0");

    public static bool TryParse(string? s, out SemVer version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        int cut = t.IndexOfAny(new[] { '-', '+' });
        var core = cut >= 0 ? t[..cut] : t;
        var parts = core.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out _)) return false;
        int Get(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        version = new SemVer(Get(0), Get(1), Get(2), s.Trim());
        return true;
    }

    public static SemVer Parse(string? s) => TryParse(s, out var v) ? v : Zero;

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);     if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemVer a, SemVer b)  => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b)  => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => Raw;
}
