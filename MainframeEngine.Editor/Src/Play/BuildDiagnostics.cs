using System.Globalization;
using System.Text.RegularExpressions;

namespace MainframeEngine.Editor;

/// <summary>How serious a <see cref="BuildDiagnostic"/> is.</summary>
public enum BuildDiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>One compiler/MSBuild diagnostic from a build's output.</summary>
/// <param name="Severity">Error, warning or info.</param>
/// <param name="Code">The diagnostic code (<c>CS1002</c>, <c>NU1101</c>, <c>MSB1009</c>); empty when the line has none.</param>
/// <param name="Message">The text, without the trailing <c>[project]</c>.</param>
/// <param name="File">The source or project file; null for tool origins (<c>MSBUILD</c>, <c>CSC</c>) or none.</param>
/// <param name="Line">1-based line, 0 when unknown.</param>
/// <param name="Column">1-based column, 0 when unknown.</param>
/// <param name="Project">The project that reported it (the <c>[…proj]</c> suffix), null when unknown.</param>
public sealed record BuildDiagnostic(
    BuildDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File,
    int Line,
    int Column,
    string? Project);

/// <summary>
/// Parses MSBuild/csc "canonical" diagnostic lines, e.g. <c>/abs/Foo.cs(12,5): error CS1002: ; expected [/abs/MyGame.csproj]</c>,
/// <c>/x/y.csproj : error NU1101: …</c> or <c>MSBUILD : error MSB1009: …</c>.
/// </summary>
public static partial class MsBuildOutputParser
{
    // origin "(" location ")" ":" category code ":" text "[" project "]" — origin and location optional; Windows paths
    // contain ':' so the origin is lazy and must be followed by ": category".
    [GeneratedRegex(
        @"^\s*(?:(?<origin>\S.*?)\s*(?:\((?<loc>\d+(?:[,-]\d+){0,3})\))?\s*:\s*)?(?<cat>error|warning|info)(?:\s+(?<code>[A-Za-z][A-Za-z0-9_]*\d))?\s*:\s*(?<text>.*?)(?:\s+\[(?<project>[^\[\]]+?\.(?:[A-Za-z]*proj|slnx?))(?:::[^\[\]]*)?\])?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex CanonicalLine();

    /// <summary>Parses one output line; false when it is not a diagnostic.</summary>
    public static bool TryParse(string line, out BuildDiagnostic diagnostic)
    {
        diagnostic = null!;
        if (string.IsNullOrWhiteSpace(line) || line.Length > 8192)
            return false;
        var match = CanonicalLine().Match(line);
        if (!match.Success)
            return false;

        var severity = match.Groups["cat"].Value.ToUpperInvariant() switch
        {
            "ERROR" => BuildDiagnosticSeverity.Error,
            "WARNING" => BuildDiagnosticSeverity.Warning,
            _ => BuildDiagnosticSeverity.Info,
        };
        var code = match.Groups["code"].Value;
        // "info : Restoring packages…" (NuGet chatter) is not a diagnostic; an info with a code is.
        if (severity == BuildDiagnosticSeverity.Info && code.Length == 0)
            return false;

        var origin = match.Groups["origin"].Success ? match.Groups["origin"].Value.Trim() : null;
        var hasLocation = match.Groups["loc"].Success;
        int line1 = 0, column = 0;
        if (hasLocation)
        {
            var parts = match.Groups["loc"].Value.Split([',', '-']);
            _ = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out line1);
            if (parts.Length > 1 && match.Groups["loc"].Value.Contains(',', StringComparison.Ordinal))
                _ = int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out column);
        }

        string? file = null;
        if (!string.IsNullOrEmpty(origin) && (hasLocation || !IsToolName(origin)))
            file = origin;
        var project = match.Groups["project"].Success ? match.Groups["project"].Value : null;
        if (project is null && file is not null && IsProjectFile(file))
            project = file;

        diagnostic = new BuildDiagnostic(severity, code, match.Groups["text"].Value, file, line1, column, project);
        return true;
    }

    /// <summary>Every diagnostic in <paramref name="lines"/>, first occurrences only (MSBuild repeats them in its summary), in order.</summary>
    public static IReadOnlyList<BuildDiagnostic> ParseAll(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var seen = new HashSet<BuildDiagnostic>();
        var result = new List<BuildDiagnostic>();
        foreach (var line in lines)
            if (TryParse(line, out var diagnostic) && seen.Add(diagnostic))
                result.Add(diagnostic);
        return result;
    }

    // "MSBUILD", "CSC", "EXEC": an all-caps tool origin rather than a file.
    private static bool IsToolName(string origin)
    {
        foreach (var c in origin)
            if (!(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
                return false;
        return true;
    }

    private static bool IsProjectFile(string path) =>
        path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}
