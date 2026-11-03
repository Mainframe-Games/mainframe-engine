using System.Text.RegularExpressions;

namespace Neon.Tests;

public sealed partial class ShaderTests
{
    private static string ShadersDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Shaders"));

    /// <summary>
    /// A varying that carries instanceCustom is one value per instance, so it is <c>nointerpolation</c>. Interpolated, the three equal
    /// vertex values come back a few ulps apart from pixel to pixel; the shaders hash, floor and step them (a facade's room
    /// widths, lit windows and televisions), so the result changed along rasterised rows, and with TAA's jitter moving the
    /// samples, from frame to frame: the towers' windows flickered in streaks.
    /// </summary>
    [Fact]
    public void InstanceCustomDataReachesTheFragmentFlat()
    {
        var checkedAny = false;
        foreach (var path in Directory.EnumerateFiles(ShadersDirectory, "*.slang"))
        {
            var source = File.ReadAllText(path);
            foreach (Match assignment in InstanceCustomAssignment().Matches(source))
            {
                var name = assignment.Groups["name"].Value;
                var declaration = Regex.Match(source, $@"^\s*(?<flat>nointerpolation\s+)?\w+\s+{name}\s*;", RegexOptions.Multiline);
                Assert.True(declaration.Success, $"{Path.GetFileName(path)}: no Varyings field declares {name}.");
                Assert.True(declaration.Groups["flat"].Success, $"{Path.GetFileName(path)}: Varyings.{name} carries instanceCustom but is not nointerpolation.");
                checkedAny = true;
            }
        }

        Assert.True(checkedAny, $"No shader in {ShadersDirectory} passes instanceCustom to a varying.");
    }

    // `o.name = v.instanceCustom…;` in vertex(): a Varyings field set from the instance's custom data.
    [GeneratedRegex(@"^\s*o\.(?<name>\w+)\s*=\s*v\.instanceCustom\b", RegexOptions.Multiline)]
    private static partial Regex InstanceCustomAssignment();
}
