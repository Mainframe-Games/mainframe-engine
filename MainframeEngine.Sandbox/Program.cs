using MainframeEngine;
using MainframeEngine.Sandbox;

// --write-scene <path>: regenerate the scene file from SandboxSceneBuilder (no window, no GPU).
if (args is ["--write-scene", var scenePath])
{
    var uid = SandboxSceneBuilder.Write(Path.GetFullPath(scenePath));
    Log.Info($"Wrote {scenePath} ({uid})");
    return 0;
}

// Parse first: --qa-capture paths are relative to where the Sandbox was launched from.
var qa = QaCapture.FromArgs(args);

// Content paths are relative ("Content/..."); resolve them against the build output no matter where
// the Sandbox is launched from (dotnet run, just, IDE).
Environment.CurrentDirectory = AppContext.BaseDirectory;

var options = qa?.Apply(Game.DefaultOptions) ?? Game.DefaultOptions;

using var game = new Game(options) { QaCapture = qa };
var exitCode = game.Run();
Log.Debug($"Game Exit: {exitCode}");
return (int)exitCode;
