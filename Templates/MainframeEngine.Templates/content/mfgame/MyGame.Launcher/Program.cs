using MainframeEngine;

// Runs project.mfproj with the engine's GameHost: the window, input map, autoloads and main scene come from the
// project file; the game's behaviour lives in node types (MyGame). Flags: --scene <uid|path>, --max-frames <n>,
// --hidden, --fixed-fps <n>, --no-vsync, --editor-port <n> (used by the editor's Play button).
return GameHost.Run(args, typeof(MyGame.Spinner).Assembly);
