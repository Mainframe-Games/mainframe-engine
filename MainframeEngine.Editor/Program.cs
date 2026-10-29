using MainframeEngine;
using MainframeEngine.Editor;

// MainframeEngine.Editor [scene.mscene] [--layout <file>] [--size WxH] [--hidden]
//   [--qa-script <file> --qa-out <dir>]   scripted clicks/keys + frame captures (docs/design/editor.md#qa)
//   [--smoke <dir> [--smoke-scene <file>]]   headless-style smoke run used by the render tests
//   --validate-demo-zip <zip> [--build [--engine <checkout>]]   check a packaged Demo zip like Download Demo does, and optionally build it (CI); no window, SDL or engine
//   --render-song <path.msong>   render a song into its project (output + .meta) and exit 0/1; no window, SDL or engine
//   --bake-song <path.msong>   bake an adaptive song into its project (stems + .mmusic) and exit 0/1; no window, SDL or engine
//   --project <dir> --export-release "<preset>" [<out>] | --export-debug "<preset>" [<out>] | --export-all
//                              export project.mfproj presets like File › Export (mf-export's runner; exit 0/1/2); no window, SDL or engine
//   --apply-update <root> --wait-pid <pid> --from <version> [--project <folder>]   (internal: editor updates)
// The staged editor of an update (docs/design/editor-updates.md): installs itself over the old editor and relaunches.
// No window, SDL or engine is created.
if (ApplyUpdateRequest.IsApplyUpdate(args))
    return UpdateApplier.RunFromCommandLine(args);

if (DemoZipValidation.IsValidateDemoZip(args))
    return DemoZipValidation.Run(args, Console.Out, Console.Error);

if (MainframeEngine.Editor.Music.SongRenderCommand.IsRenderSong(args))
    return MainframeEngine.Editor.Music.SongRenderCommand.Run(args, Console.Out, Console.Error);

if (MainframeEngine.Editor.Music.SongBakeCommand.IsBakeSong(args))
    return MainframeEngine.Editor.Music.SongBakeCommand.Run(args, Console.Out, Console.Error);

// G5: headless exports (docs/design/game-export.md), Godot's flag names, before any EditorApp exists.
if (ExportCli.IsEditorExport(args))
    return ExportCli.RunEditorAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();

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

// G15: the editor's own crash reports (~/.mainframe/crashes) and a rotating log (~/.mainframe/logs/editor.log) whose
// previous run an unclean exit's report attaches. Scripted and test runs (no crash folder) keep neither.
var editorLog = EditorCrashReporting.Install(options.Workspace.CrashDirectory, EditorWorkspaceOptions.DefaultLogDirectory);

using var app = new EditorApp(options);

// Crash safety: an unhandled exception writes recovery copies of unsaved scenes before the process dies (listed in the
// crash report, which the next start's crash dialog offers back).
IReadOnlyList<string> WriteRecoveryCopies()
{
    if (app.Workspace is not { } workspace)
        return [];
    var copies = workspace.WriteRecoveryCopies();
    foreach (var path in copies)
        Console.Error.WriteLine($"[Editor] Recovery copy written: {path}");
    return copies;
}

if (CrashReporter.IsInstalled)
    CrashReporter.Crashing += report => EditorCrashReporting.ListRecoveryCopies(report, WriteRecoveryCopies());
else
    AppDomain.CurrentDomain.UnhandledException += (_, _) => WriteRecoveryCopies();

ExitCode exit;
try
{
    exit = app.Run();
}
catch (Exception e) when (EditorCrashReporting.ReportMainThread(e))
{
    throw; // never reached: the filter reports and lets the exception end the process as before
}

options.Automation?.OnExited((int)exit);
EditorCrashReporting.End(editorLog);
return (int)exit;
