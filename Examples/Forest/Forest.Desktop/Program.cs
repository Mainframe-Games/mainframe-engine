using MainframeEngine;

// The Forest: a first-person walk (GameHost flags: --scene, --max-frames, --hidden, --fixed-fps, --no-vsync,
// --editor-port, --screenshot; game flags after ++: --autowalk, --no-capture, --no-audio). `--write-scenes <dir>`
// regenerates the scene files.
if (args is ["--write-scenes", var directory])
    return Forest.SceneWriter.WriteAll(Path.GetFullPath(directory));
return GameHost.Run(args, typeof(Forest.FirstPersonController).Assembly);
