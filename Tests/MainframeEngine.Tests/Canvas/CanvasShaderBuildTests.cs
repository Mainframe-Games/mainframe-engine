namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// How <see cref="CanvasShaderBuild"/> runs slangc: the obfuscated compile gets the generated Slang without its <c>#line</c>
/// directives (slangc 2026.1's Linux build crashes on them under <c>-obfuscate</c>), and a failed shader's errors come from a
/// second, non-obfuscated compile with them, so they still name the <c>.mfshader</c>'s lines.
/// </summary>
public sealed class CanvasShaderBuildTests
{
    private const string Ground = "shader_type spatial;\n\nuniform float k = 0.5;\n\nvoid fragment() {\n\tALBEDO = vec3(k);\n}\n";

    [Fact]
    public void TheObfuscatedCompileGetsNoLineDirectivesAndKeepsTheLineCount()
    {
        var code = SpatialShaderCompiler.Translate(Ground, "ground.mfshader").FragmentSource(SpatialPass.Color);
        Assert.Contains("#line ", code, StringComparison.Ordinal);

        // Each directive becomes an empty line; every other line is unchanged.
        var stripped = CanvasShaderBuild.WithoutLineDirectives(code);
        Assert.DoesNotContain("#line", stripped, StringComparison.Ordinal);
        Assert.Equal(code.Split('\n').Select(l => l.StartsWith("#line ", StringComparison.Ordinal) ? "" : l), stripped.Split('\n'));
    }

    [Fact]
    public void TheDiagnosticCompileIsNotObfuscated()
    {
        var arguments = CanvasShaderBuild.SlangcArguments("in.slang", "fragment", "include", "out.spv", obfuscate: false);
        Assert.DoesNotContain("-obfuscate", arguments);
        Assert.Equal(
            CanvasShaderBuild.SlangcArguments("in.slang", "fragment", "include", "out.spv").Where(a => a != "-obfuscate"),
            arguments);
    }

    // The crash: on Linux, slangc 2026.1 exits 139 for this shader when its #line directives reach the obfuscated compile.
    [Fact]
    public void ASpatialShaderBuildsAndAnErrorNamesTheShaderFileLine()
    {
        var slangc = SlangcOrSkip();
        var dir = Directory.CreateTempSubdirectory("mf-shaderbuild-");
        try
        {
            var ground = Path.Combine(dir.FullName, "ground.mfshader");
            File.WriteAllText(ground, Ground);
            Assert.True(CanvasShaderBuild.Build(ground, IncludeDirectory, slangc));
            Assert.True(CanvasShaderBuild.IsUpToDate(ground));

            var broken = Path.Combine(dir.FullName, "broken.mfshader");
            File.WriteAllText(broken, Ground.Replace("vec3(k)", "vec3(undefined_thing)", StringComparison.Ordinal));
            var e = Assert.Throws<InvalidOperationException>(() => CanvasShaderBuild.Build(broken, IncludeDirectory, slangc));
            Assert.Contains("broken.mfshader(6)", e.Message, StringComparison.Ordinal);
            Assert.Contains("undefined_thing", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string IncludeDirectory => Path.Combine(FindRepoRoot(), "MainframeEngine", "Assets", "Shaders", "include");

    private static string SlangcOrSkip() =>
        CanvasShaderBuild.FindSlangc() ?? throw Xunit.Sdk.SkipException.ForSkip("slangc is not on PATH or in $VULKAN_SDK/bin");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (MainframeEngine.slnx) not found above the test output.");
    }
}
