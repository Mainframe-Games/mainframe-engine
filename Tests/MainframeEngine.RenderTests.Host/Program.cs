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
    "spine" => new SpineScene(options),
    "sandbox" => new SandboxScene(options),
    _ => throw new ArgumentException($"Unknown scene '{options.Scene}'. Known: lit-shapes, spine, sandbox."),
};

using (game)
{
    var exit = game.Run();
    if (exit != ExitCode.Ok || game.Result is null)
    {
        Console.Error.WriteLine($"Scene '{options.Scene}' did not complete (exit {exit}).");
        return 1;
    }

    var json = JsonSerializer.Serialize(game.Result, HostResult.JsonOptions);
    File.WriteAllText(Path.Combine(options.OutputDirectory, HostResult.FileName), json);
}

return 0;
