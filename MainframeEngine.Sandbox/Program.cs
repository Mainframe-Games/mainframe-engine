using MainframeEngine;
using MainframeEngine.Sandbox;

// --write-scene <path>: regenerate the scene file from SandboxSceneBuilder (no window, no GPU).
if (args is ["--write-scene", var scenePath])
{
    var uid = SandboxSceneBuilder.Write(Path.GetFullPath(scenePath));
    Log.Info($"Wrote {scenePath} ({uid})");
    return 0;
}

// --write-net-scene <path>: regenerate the network demo's box scene (no window, no GPU).
if (args is ["--write-net-scene", var netScenePath])
{
    var uid = SandboxSceneBuilder.WriteNetBox(Path.GetFullPath(netScenePath));
    Log.Info($"Wrote {netScenePath} ({uid})");
    return 0;
}

// --qa-capture paths are relative to where the Sandbox was launched from. Content paths ("Content/...") resolve
// against the build output through ContentPaths / AssetDatabase.Current, whatever the working directory.
var qa = QaCapture.FromArgs(args);
var qaAudio = QaAudio.FromArgs(args);

// --server [port] / --client <host> [port]: the multiplayer demo (M5).
var network = NetworkDemo.FromArgs(args);

// --locale <name>: start in a language (en, es, qps); otherwise the OS language when it has a catalog (M9).
var locale = Array.IndexOf(args, "--locale") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

var options = qa?.Apply(Game.DefaultOptions) ?? Game.DefaultOptions;
options.Locale = locale;
if (network is not null)
{
    // Wall-clock time (not the QA fixed step) so the server's and the client's clocks run at the same rate.
    options = options with { FixedDeltaTime = 0, VSync = true };
}

if (network is not null)
    options.GameName += network.IsServer ? $" [server :{network.Port}]" : $" [client -> {network.Host}:{network.Port}]";

using var game = new Game(options) { QaCapture = qa, Network = network, QaAudio = qaAudio };
var exitCode = game.Run();
Log.Debug($"Game Exit: {exitCode}");
return (int)exitCode;
