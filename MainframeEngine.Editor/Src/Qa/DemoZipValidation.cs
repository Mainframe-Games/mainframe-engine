namespace MainframeEngine.Editor;

/// <summary>
/// <c>--validate-demo-zip &lt;zip&gt;</c>: unpacks a Demo zip into a temporary folder with <see cref="DemoArchive"/> (the check the
/// editor's Download Demo runs) and prints <c>ok &lt;root&gt;</c> (exit 0) or the reason (exit 1). CI runs it on the packaged Demo.
/// No window, SDL or engine is created.
/// </summary>
public static class DemoZipValidation
{
    public const string Flag = "--validate-demo-zip";

    public static bool IsValidateDemoZip(IReadOnlyList<string> args) =>
        args is { Count: > 0 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsValidateDemoZip(args) || args.Count != 2)
        {
            error.WriteLine($"Usage: MainframeEngine.Editor {Flag} <zip>");
            return 2;
        }

        var work = Directory.CreateTempSubdirectory("mf-demo-validate").FullName;
        try
        {
            var root = DemoArchive.ExtractAndValidate(Path.GetFullPath(args[1]), work);
            output.WriteLine($"ok {root}");
            return 0;
        }
        catch (DemoDownloadException e)
        {
            error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Could not delete {work}: {e.Message}");
            }
        }
    }
}
