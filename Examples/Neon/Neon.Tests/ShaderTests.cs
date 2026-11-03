using System.Text.RegularExpressions;

namespace Neon.Tests;

public sealed partial class ShaderTests
{
    private static string ShadersDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Shaders"));

    /// <summary>
    /// A varying that carries INSTANCE_CUSTOM is one value per instance, so it is <c>flat</c>. Interpolated, the three equal
    /// vertex values come back a few ulps apart from pixel to pixel; the shaders hash, floor and step them (a facade's room
    /// widths, lit windows and televisions), so the result changed along rasterised rows, and with TAA's jitter moving the
    /// samples, from frame to frame: the towers' windows flickered in streaks.
    /// </summary>
    [Fact]
    public void InstanceCustomDataReachesTheFragmentFlat()
    {
        var checkedAny = false;
        foreach (var path in Directory.EnumerateFiles(ShadersDirectory, "*.gdshader"))
        {
            var source = File.ReadAllText(path);
            foreach (Match assignment in InstanceCustomAssignment().Matches(source))
            {
                var name = assignment.Groups["name"].Value;
                var declaration = Regex.Match(source, $@"^varying\s+(?<flat>flat\s+)?\w+\s+{name}\s*;", RegexOptions.Multiline);
                Assert.True(declaration.Success, $"{Path.GetFileName(path)}: no varying declares {name}.");
                Assert.True(declaration.Groups["flat"].Success, $"{Path.GetFileName(path)}: varying {name} carries INSTANCE_CUSTOM but is not flat.");
                checkedAny = true;
            }
        }

        Assert.True(checkedAny, $"No shader in {ShadersDirectory} passes INSTANCE_CUSTOM to a varying.");
    }

    [GeneratedRegex(@"^\s*(?<name>\w+)\s*=\s*INSTANCE_CUSTOM\b", RegexOptions.Multiline)]
    private static partial Regex InstanceCustomAssignment();
}
