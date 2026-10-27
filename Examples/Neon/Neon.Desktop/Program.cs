using MainframeEngine;

// Neon: a first-person walk round a loft apartment (GameHost flags: --scene, --max-frames, --hidden, --fixed-fps,
// --no-vsync, --editor-port, --screenshot; game flags after ++: --no-capture, --view x,y,z,yaw[,pitch], --open-doors,
// --lights-off <Room>). `--write-scenes <dir>` regenerates the
// scene files.
if (args is ["--write-scenes", var directory])
    return Neon.SceneWriter.WriteAll(Path.GetFullPath(directory));
return GameHost.Run(args, typeof(Neon.FirstPersonController).Assembly);
