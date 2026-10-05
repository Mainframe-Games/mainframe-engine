using MainframeEngine;

// mf-shaders build|check <folder>... (see the csproj comment).
if (args.Length < 2 || args[0] is not ("build" or "check"))
{
    Console.Error.WriteLine("usage: mf-shaders build|check <folder>...");
    return 2;
}

var include = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", "include");
if (!Directory.Exists(include))
    include = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "MainframeEngine", "Content", "Shaders", "include"));

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
                var program = CanvasShaderCompiler.Translate(File.ReadAllText(file), Path.GetFileName(file));
                if (!CanvasShaderBuild.IsUpToDate(file, program))
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
    ? $"{checkedCount} canvas shader(s) checked, {failures} stale or invalid."
    : $"{checkedCount} canvas shader(s), {built} built, {failures} failed.");
return failures == 0 ? 0 : 1;
