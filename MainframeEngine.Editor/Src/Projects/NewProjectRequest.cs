namespace MainframeEngine.Editor;

/// <summary>What the New Project dialog asks for.</summary>
/// <param name="Name">The game's name: its folder, solution, assemblies and root namespace (<see cref="NewProjectValidation.ValidateName"/>).</param>
/// <param name="ParentDirectory">The folder the project folder is created in.</param>
/// <param name="EnginePath">The engine checkout the game references (<c>--engine-path</c>).</param>
public sealed record NewProjectRequest(string Name, string ParentDirectory, string EnginePath)
{
    /// <summary>The new project's folder: <c>&lt;ParentDirectory&gt;/&lt;Name&gt;</c>.</summary>
    public string ProjectDirectory => Path.Combine(ParentDirectory, Name);
}

/// <summary>
/// Checks a <see cref="NewProjectRequest"/> before anything runs, so the dialog can show the problem next to the field.
/// Each check returns null when the value is fine, else one user-facing sentence.
/// </summary>
public static class NewProjectValidation
{
    /// <summary>The longest project name accepted.</summary>
    public const int MaxNameLength = 64;

    // C# reserved keywords: the name becomes a namespace and assembly name, so these cannot be used (contextual
    // keywords such as 'var' or 'record' are legal identifiers).
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    // Namespaces a game's root namespace would shadow or clash with (compared case-insensitively: assembly and folder
    // names are case-insensitive on Windows and usually on macOS).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MainframeEngine", "System", "Microsoft", "Program",
        // Device names Windows cannot use as folder names.
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Type names the template's code uses unqualified inside the game's namespace; a namespace with the same name
    // would hide them and the new project would not compile. Engine types are checked by reflection.
    private static readonly HashSet<string> TemplateTypeNames = new(StringComparer.Ordinal) { "Vector3" };

    private static readonly Lazy<HashSet<string>> EngineTypeNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in typeof(Node).Assembly.GetExportedTypes())
        {
            if (type.IsNested)
                continue;
            var name = type.Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            names.Add(tick >= 0 ? name[..tick] : name);
        }

        return names;
    });

    /// <summary>
    /// The name must work as a folder, assembly and C# namespace: an ASCII letter first, then letters, digits or
    /// underscores, at most <see cref="MaxNameLength"/> characters, not a C# keyword, not a reserved name
    /// (<c>MainframeEngine</c>, <c>System</c>, <c>Microsoft</c>, any case) and not the name of an engine type the
    /// game's code would lose access to (<c>Node</c>).
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name for the project.";
        if (name.Length > MaxNameLength)
            return $"The project name is too long ({name.Length} characters); use at most {MaxNameLength}.";
        if (!char.IsAsciiLetter(name[0]))
            return "The project name must start with a letter (A–Z).";
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return c == ' '
                    ? "The project name cannot contain spaces; use letters, digits and underscores (for example MyGame or My_Game)."
                    : $"The project name cannot contain '{c}'; use letters (A–Z), digits and underscores only.";
        if (Keywords.Contains(name))
            return $"'{name}' is a C# keyword; choose another project name.";
        if (ReservedNames.Contains(name))
            return $"'{name}' is a reserved name; choose another project name.";
        if (EngineTypeNames.Value.Contains(name) || TemplateTypeNames.Contains(name))
            return $"'{name}' is the name of an engine type the game's code uses; choose another project name.";
        return null;
    }

    /// <summary>
    /// The parent folder must exist, and <c>&lt;parent&gt;/&lt;name&gt;</c> must not exist yet or be an empty folder.
    /// </summary>
    public static string? ValidateLocation(string parentDirectory, string name)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            return "Choose the folder to create the project in.";
        string target;
        try
        {
            if (!Path.IsPathFullyQualified(parentDirectory))
                return $"'{parentDirectory}' is not a full folder path; choose the folder to create the project in.";
            if (!Directory.Exists(parentDirectory))
                return File.Exists(parentDirectory)
                    ? $"'{parentDirectory}' is a file, not a folder; choose the folder to create the project in."
                    : $"The folder '{parentDirectory}' does not exist.";
            if (string.IsNullOrWhiteSpace(name))
                return null;
            target = Path.Combine(parentDirectory, name);
            if (File.Exists(target))
                return $"There is already a file named '{name}' in '{parentDirectory}'; choose another name or folder.";
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return $"The folder '{target}' already exists and is not empty; choose another name or folder.";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"The folder '{parentDirectory}' cannot be used: {e.Message}";
        }

        return null;
    }

    /// <summary>The engine path must be a checkout holding <c>MainframeEngine/MainframeEngine.csproj</c>.</summary>
    public static string? ValidateEnginePath(string enginePath)
    {
        if (string.IsNullOrWhiteSpace(enginePath))
            return "Choose the Mainframe Engine folder (the checkout holding MainframeEngine.sln).";
        try
        {
            if (!Path.IsPathFullyQualified(enginePath))
                return $"'{enginePath}' is not a full folder path; choose the Mainframe Engine folder.";
            if (!Directory.Exists(enginePath))
                return $"The engine folder '{enginePath}' does not exist.";
            if (!File.Exists(Path.Combine(enginePath, TemplateLocator.EngineProjectRelativePath)))
                return $"'{enginePath}' is not a Mainframe Engine checkout: it has no {TemplateLocator.EngineProjectRelativePath.Replace('\\', '/')}.";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"The engine folder '{enginePath}' cannot be used: {e.Message}";
        }

        return null;
    }

    /// <summary>The first problem with <paramref name="request"/> (name, then location, then engine path), or null.</summary>
    public static string? Validate(NewProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValidateName(request.Name)
               ?? ValidateLocation(request.ParentDirectory, request.Name)
               ?? ValidateEnginePath(request.EnginePath);
    }
}
