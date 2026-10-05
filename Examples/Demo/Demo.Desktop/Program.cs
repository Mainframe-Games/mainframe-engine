using MainframeEngine;

// The Demo: one scene per engine feature, a nav bar on top (GameHost flags: --scene, --max-frames, --hidden,
// --fixed-fps, --no-vsync, --editor-port, --screenshot). `--write-scenes <dir>` regenerates the scene files.
if (args is ["--write-scenes", var directory])
    return Demo.SceneWriter.WriteAll(Path.GetFullPath(directory));
return GameHost.Run(args, typeof(Demo.DemoScenes).Assembly);
