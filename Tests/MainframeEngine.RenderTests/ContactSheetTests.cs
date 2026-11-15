using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// G1.17: <c>mfe anim render</c> (run out of process like the render host) bakes nothing itself; on a temp project with the
/// test character and a baked squat it writes a contact sheet whose every cell shows the character over the sky and ground,
/// with the model's own materials and with <c>--flat</c>.
/// </summary>
public class ContactSheetTests
{
    private const string Squat = """
        {
          "model": "Assets/Characters/hero.glb", "library": "Assets/Characters/hero_anims.mfres", "name": "squat",
          "length": 1.6, "loop": "Linear", "root": "inPlace",
          "keys": [
            { "t": 0.0, "pose": "stand", "ease": "inOut", "ik": { "leftFoot": "pin", "rightFoot": "pin" } },
            { "t": 0.8, "ease": "inOut", "hips": { "y": -0.12, "pitch": 10 },
              "joints": { "Spine": { "flex": 6 }, "LeftUpperArm": { "flex": 60 }, "RightUpperArm": { "flex": 60 } } },
            { "t": 1.6, "pose": "stand" }
          ]
        }
        """;

    private static string Project()
    {
        var root = Path.Combine(RenderTestEnvironment.ArtifactsDirectory, "contact-sheet");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var characters = Path.Combine(root, "Assets", "Characters");
        Directory.CreateDirectory(characters);
        var source = Path.Combine(RenderTestEnvironment.RepositoryRoot, "Tests", "Assets", "Models", "Character", "character.glb");
        File.Copy(source, Path.Combine(characters, "hero.glb"));
        File.Copy(source + ".mfmeta", Path.Combine(characters, "hero.glb.mfmeta"));
        File.WriteAllText(Path.Combine(root, "project.mfproj"), """{ "format": 1, "name": "Hero" }""");
        File.WriteAllText(Path.Combine(root, "squat.json"), Squat);
        return root;
    }

    private static (int Exit, string Output) Mfe(string project, params string[] args) => HostRunner.RunTool("mfe.dll", project, args);

    [Fact]
    public void RenderWritesASheetWithTheCharacterInEveryCell()
    {
        var project = Project();
        var (bakeExit, bakeOutput) = Mfe(project, "anim", "bake", "squat.json");
        Assert.True(bakeExit == 0, bakeOutput);

        foreach (var flat in new[] { false, true })
        {
            var sheet = Path.Combine(project, flat ? "sheet-flat.png" : "sheet.png");
            string[] args = ["anim", "render", "Assets/Characters/hero_anims.mfres", "squat", "--model", "Assets/Characters/hero.glb",
                "-o", sheet, "--frames", "4", "--size", "900"];
            var (exit, output) = Mfe(project, flat ? [.. args, "--flat"] : args);
            Assert.True(exit == 0, output);
            Assert.Contains("3 view(s) × 4 frame(s)", output, StringComparison.Ordinal);
            Assert.DoesNotContain("[INFO]", output, StringComparison.Ordinal); // the engine's log stays off stdout

            var image = Png.ReadRgba8(sheet);
            Assert.Equal(900, image.Width);
            // Each of the 12 cells (3 rows × 4 columns, laid out as ContactSheetLayout does) has character pixels: the suit's
            // blue (or, flat, the unshaded light neutral grey, lifted by the tonemap), which neither the sky nor the ground has.
            const int margin = 16, labelWidth = 96, header = 58, columnLabel = 22, gap = 6;
            var cellWidth = (900 - 2 * margin - labelWidth - 3 * gap) / 4;
            var cellHeight = (int)MathF.Round(cellWidth * 4f / 3f);
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 4; column++)
                {
                    var x0 = margin + labelWidth + column * (cellWidth + gap);
                    var y0 = margin + header + columnLabel + row * (cellHeight + gap);
                    var character = 0;
                    for (var y = y0; y < y0 + cellHeight; y++)
                        for (var x = x0; x < x0 + cellWidth; x++)
                        {
                            var i = (y * image.Width + x) * 4;
                            var (r, g, b) = (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
                            if (flat ? r > 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 14 : b > r + 40 && r < 90 && g < 130)
                                character++;
                        }

                    Assert.True(character > cellWidth * cellHeight / 60,
                        $"{(flat ? "flat " : "")}cell row {row} column {column} has {character} character pixels of {cellWidth * cellHeight}");
                }
        }
    }

    [Fact]
    public void TheJsonSummaryNamesTheFrames()
    {
        var project = Project();
        Assert.Equal(0, Mfe(project, "anim", "bake", "squat.json").Exit);

        var (exit, output) = Mfe(project, "anim", "render", "Assets/Characters/hero_anims.mfres", "squat", "--model", "Assets/Characters/hero.glb",
            "-o", "one.png", "--frame", "0.8", "--views", "side", "--json", "--size", "600");

        Assert.True(exit == 0, output);
        var json = output[output.IndexOf('{', StringComparison.Ordinal)..];
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(0.8f, document.RootElement.GetProperty("frames")[0].GetProperty("t").GetSingle(), 0.001f);
        Assert.True(document.RootElement.GetProperty("frames")[0].GetProperty("key").GetBoolean());
        Assert.True(File.Exists(Path.Combine(project, "one.png")));
    }
}
