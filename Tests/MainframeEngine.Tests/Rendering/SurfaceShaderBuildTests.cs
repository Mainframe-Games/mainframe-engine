namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// <see cref="SurfaceShaderBuild"/> with slangc (ADR 0237; skipped without it, as on CI's build-test runners): a shader that
/// imports a helper module builds every pass and its layout, slangc's errors name the shader's file and line (no
/// <c>#line</c> directives), and the lock goes stale when an imported module changes.
/// </summary>
public sealed class SurfaceShaderBuildTests
{
    private const string Noise = """
        // A helper module (not a shader: no `import mainframe.…;` first).
        float hash(float2 p)
        {
            return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
        }
        """;

    private const string Ground = """
        import mainframe.spatial;
        import noise;

        struct Material
        {
            float k = 0.5;
        };

        void fragment(inout SurfaceFragment s, Material m)
        {
            s.albedo = float3(m.k * hash(s.uv));
        }
        """;

    [Fact]
    public void AShaderBuildsEveryPassAndItsLayoutAndALockThatCoversItsModules()
    {
        var slangc = SlangcOrSkip();
        var dir = Directory.CreateTempSubdirectory("mf-surfacebuild-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "noise.slang"), Noise);
            var ground = Path.Combine(dir.FullName, "ground.slang");
            File.WriteAllText(ground, Ground);
            Assert.True(SurfaceShaderBuild.Build(ground, IncludeDirectory, slangc));
            Assert.All(SurfaceShaderBuild.SpvFiles(ground, SurfaceShaderKind.Spatial), f => Assert.True(File.Exists(f), f));
            Assert.True(SurfaceShaderBuild.IsUpToDate(ground, IncludeDirectory));
            Assert.False(SurfaceShaderBuild.Build(ground, IncludeDirectory, slangc)); // up to date

            var lockLines = File.ReadAllLines(ground + SurfaceShaderBuild.LockExtension);
            Assert.Contains(lockLines, l => l.StartsWith("noise.slang ", StringComparison.Ordinal));
            Assert.Contains(lockLines, l => l.StartsWith("include:mainframe/surface/spatial.slang ", StringComparison.Ordinal));

            var shader = Shader.Load(ground);
            Assert.Equal((0, 4, 16), (shader.Uniforms.Single().Offset, shader.Uniforms.Single().Size, shader.UniformBlockSize));
            Assert.Equal([0.5f], shader.Uniforms.Single().Default!);
            Assert.Equal(ground, shader.SpvBasePath);

            // The imported module changes: `check` sees it (Shader.Load, without the includes, checks only the shader).
            File.WriteAllText(Path.Combine(dir.FullName, "noise.slang"), Noise.Replace("43758.5453", "43758.5", StringComparison.Ordinal));
            Assert.False(SurfaceShaderBuild.IsUpToDate(ground, IncludeDirectory));
            Assert.True(SurfaceShaderBuild.Build(ground, IncludeDirectory, slangc));
            Assert.True(SurfaceShaderBuild.IsUpToDate(ground, IncludeDirectory));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ACanvasShaderBuildsBothStages()
    {
        var slangc = SlangcOrSkip();
        var dir = Directory.CreateTempSubdirectory("mf-surfacebuild-");
        try
        {
            var wave = Path.Combine(dir.FullName, "wave.slang");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Rendering", "Fixtures", "wave_canvas.slang"), wave);
            Assert.True(SurfaceShaderBuild.Build(wave, IncludeDirectory, slangc));
            var shader = Shader.Load(wave);
            Assert.Equal(CanvasBlendMode.Mul, shader.BlendMode);
            Assert.True(shader.HasVertexFunction);
            Assert.True(File.Exists(shader.VertexSpvPath) && File.Exists(shader.FragmentSpvPath));
            // The vertex stage writes only what the fragment stage reads: the UV + position, the varying (not the colour).
            Assert.Equal([0, 2], SpirvInputs.InputLocations(File.ReadAllBytes(shader.FragmentSpvPath!)).Order());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("s.albedo = float3(m.k * hash(s.uv));", "s.albedo = float3(m.k * undefined_thing);", "ground.slang(11): error 30015: undefined identifier 'undefined_thing'")]
    [InlineData("s.albedo = float3(m.k * hash(s.uv));", "s.albedoo = float3(m.k);", "ground.slang(11): error 30027: 'albedoo' is not a member of 'SurfaceFragment'")]
    [InlineData("struct Material", "[cull(Cull.Disabld)]\nstruct Material", "ground.slang(4): error 30027: 'Disabld' is not a member of 'typeof(Cull)'")]
    public void SlangErrorsNameTheShadersFileAndLine(string find, string replace, string expected)
    {
        var slangc = SlangcOrSkip();
        var dir = Directory.CreateTempSubdirectory("mf-surfacebuild-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "noise.slang"), Noise);
            var ground = Path.Combine(dir.FullName, "ground.slang");
            File.WriteAllText(ground, Ground.Replace(find, replace, StringComparison.Ordinal));
            var e = Assert.Throws<InvalidOperationException>(() => SurfaceShaderBuild.Build(ground, IncludeDirectory, slangc));
            Assert.Contains(expected, e.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(ground + SurfaceShaderLayout.Extension));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void RealPathResolvesLinkedFoldersAsSlangcsDepfileDoes()
    {
        // macOS's temp folder is /var/..., a link to /private/var/...; slangc's depfile names the real path, and the lock
        // must not list the build's own temp files (deleted afterwards) because the two spellings differ.
        var dir = Directory.CreateTempSubdirectory("mf-realpath-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(dir.FullName, "real")).FullName;
            File.WriteAllText(Path.Combine(real, "a.slang"), "");
            var link = Path.Combine(dir.FullName, "link");
            Directory.CreateSymbolicLink(link, real);
            var expected = Path.Combine(SurfaceShaderBuild.RealPath(real), "a.slang");
            Assert.Equal(expected, SurfaceShaderBuild.RealPath(Path.Combine(link, "a.slang")));
            Assert.Equal(expected, SurfaceShaderBuild.RealPath(Path.Combine(link, "..", "real", "a.slang")));
            Assert.Equal(Path.Combine(SurfaceShaderBuild.RealPath(real), "missing", "b.slang"), SurfaceShaderBuild.RealPath(Path.Combine(link, "missing", "b.slang")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string IncludeDirectory => Path.Combine(FindRepoRoot(), "MainframeEngine", "Assets", "Shaders", "include");

    private static string SlangcOrSkip() =>
        SurfaceShaderBuild.FindSlangc() ?? throw Xunit.Sdk.SkipException.ForSkip("slangc is not on PATH or in $VULKAN_SDK/bin");

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
