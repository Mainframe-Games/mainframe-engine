using MainframeEngine;

// mf-shaders [--include <dir>] build|check <folder>... | translate <file.slang> [pass] (see the csproj comment).
// The engine's shader include folder: --include, else the package's (tools/mf-shaders/ → mainframe/shaders/include/),
// else a checkout's (Tools/MainframeEngine.ShaderBuild/bin/<cfg>/<tfm>/ → MainframeEngine/Assets/Shaders/include/).
string? include = null;
var includeAt = Array.IndexOf(args, "--include");
if (includeAt >= 0)
{
    if (includeAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("error: --include needs a folder.");
        return 2;
    }

    include = Path.GetFullPath(args[includeAt + 1]);
    args = [.. args[..includeAt], .. args[(includeAt + 2)..]];
}

include ??= new[]
{
    Path.Combine(AppContext.BaseDirectory, "..", "..", "mainframe", "shaders", "include"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainframeEngine", "Assets", "Shaders", "include"),
}.Select(Path.GetFullPath).FirstOrDefault(d => File.Exists(Path.Combine(d, "mainframe", "spatial.slang")));
if (include is null || !Directory.Exists(include))
{
    Console.Error.WriteLine("error: the engine's shader include folder was not found; pass --include <dir> (an engine checkout's MainframeEngine/Assets/Shaders/include).");
    return 2;
}

if (args.Length >= 2 && args[0] == "translate")
{
    // Prints the generated entries (debugging a shader): a canvas shader's two stages, or a spatial pass's. The vertex
    // stage is the one before the fragment stage's inputs are known (every output written); the layout is the committed one.
    var text = File.ReadAllText(args[1]);
    var file = Path.GetFileName(args[1]);
    var parsed = SurfaceShaderSource.Parse(text, file);
    var layoutPath = args[1] + SurfaceShaderLayout.Extension;
    if (!File.Exists(layoutPath))
    {
        Console.Error.WriteLine($"error: {args[1]} has no layout file; run mf-shaders build first.");
        return 1;
    }

    var layout = SurfaceShaderLayout.Read(File.ReadAllText(layoutPath), file);
    if (parsed.Kind == SurfaceShaderKind.Spatial)
    {
        var pass = args.Length > 2 ? Enum.Parse<SpatialPass>(args[2], ignoreCase: true) : SpatialPass.Color;
        Console.WriteLine(SurfaceShaderCompiler.SpatialVertex(parsed, layout, file, pass));
        Console.WriteLine("// ──────── fragment ────────");
        Console.WriteLine(SurfaceShaderCompiler.SpatialFragment(parsed, layout, file, pass));
    }
    else
    {
        Console.WriteLine(SurfaceShaderCompiler.CanvasVertex(parsed, layout, file));
        Console.WriteLine("// ──────── fragment ────────");
        Console.WriteLine(SurfaceShaderCompiler.CanvasFragment(parsed, layout, file));
    }

    return 0;
}

if (args.Length < 2 || args[0] is not ("build" or "check"))
{
    Console.Error.WriteLine("usage: mf-shaders [--include <dir>] build|check <folder>... | mf-shaders translate <file.slang> [color|depth|shadow|shadowpoint|objectid]");
    return 2;
}

var failures = 0;
var built = 0;
var checkedCount = 0;
foreach (var folder in args.Skip(1))
{
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        if (Shader.IsGodotSyntax(file))
        {
            Console.Error.WriteLine($"error: {file}: Godot-syntax shaders were removed (ADR 0237); port it to a Slang surface shader (docs/design/shaders.md#porting-from-godot).");
            failures++;
            continue;
        }

        // Helper modules share the extension: only files starting with `import mainframe.spatial|canvas;` are shaders.
        if (!file.EndsWith(Shader.Extension, StringComparison.OrdinalIgnoreCase) || !SurfaceShaderSource.IsSurfaceShader(File.ReadAllText(file)))
            continue;
        checkedCount++;
        try
        {
            if (args[0] == "check")
            {
                if (!SurfaceShaderBuild.IsUpToDate(file, include))
                {
                    Console.Error.WriteLine($"stale: {file} (run mf-shaders build)");
                    failures++;
                }
            }
            else
            {
                var warnings = new List<string>();
                if (SurfaceShaderBuild.Build(file, include, warnings: warnings))
                {
                    Console.WriteLine($"built  {file}");
                    built++;
                }

                foreach (var warning in warnings)
                    Console.Error.WriteLine($"warning: {warning}");
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"error: {file}: {e.Message}");
            failures++;
        }
    }
}

Console.WriteLine(args[0] == "check"
    ? $"{checkedCount} surface shader(s) checked, {failures} stale or invalid."
    : $"{checkedCount} surface shader(s), {built} built, {failures} failed.");
return failures == 0 ? 0 : 1;
