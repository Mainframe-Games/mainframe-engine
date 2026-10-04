namespace MainframeEngine.Editor;

/// <summary>
/// Finds the engine checkout new games reference (<c>--engine-path</c>) and the <c>mfgame</c> template. Today the
/// editor runs from a checkout (the template and the engine sources live there); a release may ship a packaged copy
/// of the template next to the app (<c>Templates/mfgame</c>).
/// </summary>
public static class TemplateLocator
{
    /// <summary>Environment variable naming the engine checkout to use; checked before walking up from the app.</summary>
    public const string EnginePathVariable = "MAINFRAME_ENGINE_PATH";

    /// <summary>The engine project inside a checkout (what makes a folder a checkout).</summary>
    public static readonly string EngineProjectRelativePath = Path.Combine("MainframeEngine", "MainframeEngine.csproj");

    /// <summary>The template folder inside a checkout.</summary>
    public static readonly string CheckoutTemplateRelativePath = Path.Combine("Templates", "MainframeEngine.Templates", "content", "mfgame");

    /// <summary>The template folder next to a packaged editor.</summary>
    public static readonly string PackagedTemplateRelativePath = Path.Combine("Templates", "mfgame");

    /// <summary>
    /// The engine checkout: <see cref="EnginePathVariable"/> when it holds <c>MainframeEngine/MainframeEngine.csproj</c>,
    /// else the first folder at or above <paramref name="startDirectory"/> (default: the app folder) holding both the
    /// engine project and the <c>mfgame</c> template. Null when there is none (a packaged editor without sources).
    /// </summary>
    public static string? FindEngineCheckout(string? startDirectory = null)
    {
        if (Environment.GetEnvironmentVariable(EnginePathVariable) is { Length: > 0 } configured)
        {
            var path = configured.Trim().Trim('"');
            if (Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, EngineProjectRelativePath)))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Log.Warning($"[Editor] {EnginePathVariable}='{configured}' is not an engine checkout (no {EngineProjectRelativePath}); ignoring it.");
        }

        var start = startDirectory ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(start))
            return null;
        for (var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start)); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, EngineProjectRelativePath)) && IsTemplate(Path.Combine(directory, CheckoutTemplateRelativePath)))
                return directory;
        }

        return null;
    }

    /// <summary>
    /// The <c>mfgame</c> template folder: the one in <paramref name="engineCheckout"/>, else a packaged copy at
    /// <c>&lt;appDirectory&gt;/Templates/mfgame</c> (default: the app folder). Null when neither exists.
    /// </summary>
    public static string? FindTemplate(string? engineCheckout, string? appDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(engineCheckout))
        {
            var inCheckout = Path.GetFullPath(Path.Combine(engineCheckout, CheckoutTemplateRelativePath));
            if (IsTemplate(inCheckout))
                return inCheckout;
        }

        var app = appDirectory ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(app))
            return null;
        var packaged = Path.GetFullPath(Path.Combine(app, PackagedTemplateRelativePath));
        return IsTemplate(packaged) ? packaged : null;
    }

    /// <summary>True when <paramref name="directory"/> holds <c>.template.config/template.json</c>.</summary>
    public static bool IsTemplate(string directory) =>
        !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, ".template.config", "template.json"));
}
