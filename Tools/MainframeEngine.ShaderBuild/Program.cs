using MainframeEngine;

// mf-shaders build|check <folder>... | translate <file.mfshader> [pass] (see the csproj comment).
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
    Console.Error.WriteLine("usage: mf-shaders build|check <folder>... | mf-shaders translate <file.mfshader> [color|depth|shadow|shadowpoint|objectid]");
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
    var files = Directory.EnumerateFiles(folder, "*" + Shader.Extension, SearchOption.AllDirectories)
        .Concat(Directory.EnumerateFiles(folder, "*" + Shader.LegacyExtension, SearchOption.AllDirectories));
    foreach (var file in files.Order(StringComparer.Ordinal))
    {
        checkedCount++;
        if (Shader.IsLegacy(file))
            Console.Error.WriteLine($"warning: {file}: the .gdshader extension is deprecated; rename it to .mfshader (ADR 0236)");
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
