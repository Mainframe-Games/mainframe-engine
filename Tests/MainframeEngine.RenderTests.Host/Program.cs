using System.Text.Json;
using MainframeEngine;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

// Content paths are relative to the output folder.
Environment.CurrentDirectory = AppContext.BaseDirectory;

var options = HostOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

RenderTestGame game = options.Scene switch
{
    "lit-shapes" => new LitShapesScene(options),
    "multi-light" => new MultiLightScene(options),
    "spine" => new SpineScene(options),
    "spine-no-shadows" => new SpineNoShadowsScene(options),
    "sandbox" => new SandboxScene(options),
    _ => throw new ArgumentException(
        $"Unknown scene '{options.Scene}'. Known: lit-shapes, multi-light, spine, spine-no-shadows, sandbox."),
};

using (game)
{
    var exit = game.Run();
    if (game.Result is null)
    {
        Console.Error.WriteLine($"Scene '{options.Scene}' did not complete (exit {exit}).");
        return 2;
    }

    // The result is written whatever the exit code, so tests can check Quit(ExitCode.Error) too.
    var json = JsonSerializer.Serialize(game.Result with { ExitCode = (int)exit }, HostResult.JsonOptions);
    File.WriteAllText(Path.Combine(options.OutputDirectory, HostResult.FileName), json);
    return (int)exit;
}
