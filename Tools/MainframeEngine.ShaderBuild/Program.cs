using MainframeEngine;

// mf-shaders build|check <folder>... | translate <file.gdshader> [pass] (see the csproj comment).
if (args.Length >= 2 && args[0] == "translate")
{
    // Prints the generated Slang (debugging a translation): a canvas shader's two stages, or a spatial pass's.
    var text = File.ReadAllText(args[1]);
    var file = Path.GetFileName(args[1]);
    if (SpatialShaderCompiler.IsSpatial(text))
    {
        var spatial = SpatialShaderCompiler.Translate(text, file);
        var pass = args.Length > 2 ? Enum.Parse<SpatialPass>(args[2], ignoreCase: true) : SpatialPass.Color;
        Console.WriteLine(spatial.VertexSource(pass));
        Console.WriteLine("// ──────── fragment ────────");
        Console.WriteLine(spatial.FragmentSource(pass));
    }
    else
    {
        var canvas = CanvasShaderCompiler.Translate(text, file);
        Console.WriteLine(canvas.VertexSource);
        Console.WriteLine("// ──────── fragment ────────");
        Console.WriteLine(canvas.FragmentSource);
    }

    return 0;
}

if (args.Length < 2 || args[0] is not ("build" or "check"))
{
    Console.Error.WriteLine("usage: mf-shaders build|check <folder>... | mf-shaders translate <file.gdshader> [color|depth|shadow|shadowpoint|objectid]");
    return 2;
}

var include = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "include");
if (!Directory.Exists(include))
    include = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainframeEngine", "Assets", "Shaders", "include"));

var failures = 0;
var built = 0;
var checkedCount = 0;
foreach (var folder in args.Skip(1))
{
    foreach (var file in Directory.EnumerateFiles(folder, "*.gdshader", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        checkedCount++;
        try
        {
            if (args[0] == "check")
            {
                if (!CanvasShaderBuild.IsUpToDate(file))
                {
                    Console.Error.WriteLine($"stale: {file} (run mf-shaders build)");
                    failures++;
                }
            }
            else if (CanvasShaderBuild.Build(file, include))
            {
                Console.WriteLine($"built  {file}");
                built++;
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
    ? $"{checkedCount} Godot shader(s) checked (canvas_item, spatial), {failures} stale or invalid."
    : $"{checkedCount} Godot shader(s) (canvas_item, spatial), {built} built, {failures} failed.");
return failures == 0 ? 0 : 1;
