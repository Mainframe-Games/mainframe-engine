using System.Text;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Content loads through <see cref="ContentFiles"/> from loose files and from a mounted <c>Assets.mfpak</c> (G19 phase 2):
/// a 200 KB scene's text (Brotli in a pack), a 512² PNG decode (stored) and a UI document. A pack must not load more than
/// 10 % slower than loose files (docs/design/future/protected-exports.md#testing; compare on the baseline machine).
/// </summary>
[MemoryDiagnoser]
public class ContentLoadBenchmarks : IDisposable
{
    private string _directory = "";
    private string _root = "";
    private Both? _mount;

    /// <summary>Where the files come from: <c>Loose</c> on disk, or <c>Pack</c> (the same files in a mounted pack).</summary>
    [Params("Loose", "Pack")]
    public string Source { get; set; } = "Loose";

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mf-content-bench", Guid.NewGuid().ToString("N"));
        var loose = Path.Combine(_directory, "loose");
        var scene = new StringBuilder();
        for (var i = 0; scene.Length < 200_000; i++)
            scene.Append("{ \"type\": \"MeshInstance3D\", \"name\": \"Rock").Append(i).Append("\", \"props\": { \"position\": [1.5, 0, ").Append(i).Append("] } },\n");
        Write(loose, "Assets/Scenes/level.mscene", Encoding.UTF8.GetBytes(scene.ToString()));
        Write(loose, "Assets/UI/hud.rml", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<div class=\"row\"><span>Health</span></div>\n", 300))));
        var rgba = new byte[512 * 512 * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 31 / 7 ^ i >> 9);
        Write(loose, "Assets/Textures/rock.png", Png.EncodeRgba8(512, 512, rgba));

        if (Source == "Loose")
        {
            _root = loose;
            return;
        }

        _root = Path.Combine(_directory, "packed"); // not on disk: the pack is the only copy
        var pack = Path.Combine(_directory, PackFormat.FileName);
        PackWriter.Write(pack, PackWriter.GameFiles(loose));
        var source = new PackContentSource(PackReader.Open(pack), _root);
        _mount = new Both(ContentFiles.Mount(source), source);
    }

    private static void Write(string root, string path, byte[] bytes)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    [Benchmark]
    public int SceneText() => ContentFiles.ReadAllText(Path.Combine(_root, "Assets/Scenes/level.mscene")).Length;

    [Benchmark]
    public int TextureDecode()
    {
        using var stream = ContentFiles.OpenRead(Path.Combine(_root, "Assets/Textures/rock.png"));
        return Png.ReadRgba8(stream).Width;
    }

    [Benchmark]
    public int UiDocumentText() => ContentFiles.ReadAllText(Path.Combine(_root, "Assets/UI/hud.rml")).Length;

    [GlobalCleanup]
    public void Dispose()
    {
        _mount?.Dispose();
        _mount = null;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Both(IDisposable mount, IDisposable source) : IDisposable
    {
        public void Dispose()
        {
            mount.Dispose();
            source.Dispose();
        }
    }
}
