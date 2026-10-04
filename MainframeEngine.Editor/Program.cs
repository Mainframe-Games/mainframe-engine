using MainframeEngine;
using MainframeEngine.Editor;

// MainframeEngine.Editor [scene.mscene] [--layout <file>] [--size WxH] [--hidden]
//   [--qa-script <file> --qa-out <dir>]   scripted clicks/keys + frame captures (docs/design/editor.md#qa)
//   [--smoke <dir> [--smoke-scene <file>]]   headless-style smoke run used by the render tests
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
