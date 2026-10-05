using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// A release version <c>X.Y.Z</c> (release tags are <c>vX.Y.Z</c>, docs/design/release.md). Pre-release and build
/// suffixes (<c>0.0.0-dev</c>) are not release versions: development builds never update.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>Parses <c>X.Y.Z</c> or <c>vX.Y.Z</c> (digits only, nothing else).</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
            return false;
        var span = text.AsSpan();
        if (span[0] is 'v' or 'V')
            span = span[1..];
        Span<Range> parts = stackalloc Range[4];
        if (span.Split(parts, '.') != 3 ||
            !TryPart(span[parts[0]], out var major) || !TryPart(span[parts[1]], out var minor) || !TryPart(span[parts[2]], out var patch))
            return false;
        version = new ReleaseVersion(major, minor, patch);
        return true;
    }

    private static bool TryPart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        return part.Length is > 0 and <= 9 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;
}
