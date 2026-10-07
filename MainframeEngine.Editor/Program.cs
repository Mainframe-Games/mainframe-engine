using MainframeEngine;
using MainframeEngine.Editor;

// MainframeEngine.Editor [scene.mscene] [--layout <file>] [--size WxH] [--hidden]
//   [--qa-script <file> --qa-out <dir>]   scripted clicks/keys + frame captures (docs/design/editor.md#qa)
//   [--smoke <dir> [--smoke-scene <file>]]   headless-style smoke run used by the render tests
//   --validate-demo-zip <zip> [--build [--engine <checkout>]]   check a packaged Demo zip like Download Demo does, and optionally build it (CI); no window, SDL or engine
//   --render-song <path.msong>   render a song into its project (output + .meta) and exit 0/1; no window, SDL or engine
//   --apply-update <root> --wait-pid <pid> --from <version> [--project <folder>]   (internal: editor updates)
// The staged editor of an update (docs/design/editor-updates.md): installs itself over the old editor and relaunches.
// No window, SDL or engine is created.
if (ApplyUpdateRequest.IsApplyUpdate(args))
    return UpdateApplier.RunFromCommandLine(args);

if (DemoZipValidation.IsValidateDemoZip(args))
    return DemoZipValidation.Run(args, Console.Out, Console.Error);

if (MainframeEngine.Editor.Music.SongRenderCommand.IsRenderSong(args))
    return MainframeEngine.Editor.Music.SongRenderCommand.Run(args, Console.Out, Console.Error);

EditorAppOptions options;
try
{
    options = EditorCommandLine.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(EditorCommandLine.Usage);
    return 2;
}

using var app = new EditorApp(options);

// Crash safety: an unhandled exception writes recovery copies of unsaved scenes before the process dies.
AppDomain.CurrentDomain.UnhandledException += (_, _) =>
{
    if (app.Workspace is { } workspace)
        foreach (var path in workspace.WriteRecoveryCopies())
            Console.Error.WriteLine($"[Editor] Recovery copy written: {path}");
};

var exit = app.Run();
options.Automation?.OnExited((int)exit);
return (int)exit;
