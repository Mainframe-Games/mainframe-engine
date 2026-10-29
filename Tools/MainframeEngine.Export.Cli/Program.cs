using MainframeEngine.Export;

// mf-export: see ExportCli.Usage (docs/design/game-export.md). Ctrl+C cancels the running export (dotnet publish is killed
// with its process tree, the stage and any partial archive are deleted, the previous artifact stays).
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
return await ExportCli.RunAsync(args, Console.Out, Console.Error, cancel.Token);
