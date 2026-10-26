using MainframeEngine;

// The Forest: a first-person walk (GameHost flags: --scene, --max-frames, --hidden, --fixed-fps, --no-vsync,
// --editor-port, --screenshot; game flags after ++: --autowalk, --no-capture, --no-audio). `--write-scenes <dir>`
// regenerates the scene files.
if (args is ["--write-scenes", var directory])
    return Forest.SceneWriter.WriteAll(Path.GetFullPath(directory));
// A sized run (a benchmark, a capture at a resolution) opens windowed: a maximized window would resize asynchronously.
var game = Array.IndexOf(args, "++");
if (game >= 0 && args.Skip(game + 1).Any(a => a is "--resolution" or "--benchmark") && !args.Contains("--windowed"))
    args = [.. args[..game], "--windowed", .. args[game..]];
return GameHost.Run(args, typeof(Forest.FirstPersonController).Assembly);
