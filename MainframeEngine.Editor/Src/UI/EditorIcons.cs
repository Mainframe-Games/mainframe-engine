using System.Reflection;
using System.Runtime.CompilerServices;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Resolves editor icons (docs/design/editor.md#icons): the Tabler icon name and colour family of node and resource
/// types (<see cref="EditorIconAttribute"/>, recorded by the generator in <see cref="NodeTypeInfo.Icon"/>, inherited
/// from the nearest base type that declares one), of files by extension and of inspector properties.
/// </summary>
/// <remarks>
/// The markup contract shared by every editor surface: an icon is <c>&lt;span class="icon icon-NAME"/&gt;</c> with
/// optional family (<c>icon-3d</c> … <c>icon-resource</c>) and size (<c>icon-sm</c> 14 dp, default 16 dp,
/// <c>icon-lg</c> 24 dp) classes; any element with <c>data-tooltip="Title (Shortcut) — description"</c> gets a tooltip.
/// Results per type are cached (weakly: game types can unload) and reset when <see cref="TypeRegistry"/> changes, so
/// data bindings can read them every refresh without allocating.
/// </remarks>
public static class EditorIcons
{
    /// <summary>The icon of a plain <see cref="Node"/>, and of types nothing more specific is known for.</summary>
    public const string Default = "circle-dot";

    /// <summary>The icon of nodes whose type is not loaded (<see cref="MissingNode"/>).</summary>
    public const string Missing = "help-hexagon";

    private sealed record Entry(string Icon, EditorIconFamily Family, string FamilyClass, string Classes);

    private static readonly ConditionalWeakTable<Type, Entry> Cache = [];

    static EditorIcons()
    {
        TypeRegistry.Changed += Cache.Clear;
    }

    /// <summary>The icon name of <paramref name="type"/> (a node or resource type; others get <see cref="Default"/>).</summary>
    public static string For(Type type) => Resolve(type).Icon;

    /// <summary>The icon name of <paramref name="node"/>'s type (<see cref="Missing"/> for a <see cref="MissingNode"/>).</summary>
    public static string For(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is MissingNode ? Missing : For(node.GetType());
    }

    /// <summary>The family class of <paramref name="type"/>: <c>icon-3d</c>, <c>icon-2d</c>, <c>icon-ui</c>, … (<c>icon-logic</c> by default).</summary>
    public static string Family(Type type) => Resolve(type).FamilyClass;

    /// <summary>The colour family of <paramref name="type"/> (never <see cref="EditorIconFamily.Inherit"/>).</summary>
    public static EditorIconFamily FamilyOf(Type type) => Resolve(type).Family;

    /// <summary>The full class list of <paramref name="type"/>'s icon: <c>"icon icon-cube icon-3d"</c> (cached).</summary>
    public static string Classes(Type type) => Resolve(type).Classes;

    /// <summary>The class list of <paramref name="node"/>'s icon (a missing type's icon is red).</summary>
    public static string Classes(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is MissingNode ? "icon icon-" + Missing + " icon-missing" : Classes(node.GetType());
    }

    /// <summary>The CSS class of a family (<see cref="EditorIconFamily.Inherit"/> reads as <c>icon-logic</c>).</summary>
    public static string FamilyClass(EditorIconFamily family) => family switch
    {
        EditorIconFamily.Space3D => "icon-3d",
        EditorIconFamily.Space2D => "icon-2d",
        EditorIconFamily.Ui => "icon-ui",
        EditorIconFamily.Audio => "icon-audio",
        EditorIconFamily.Physics => "icon-physics",
        EditorIconFamily.Network => "icon-net",
        EditorIconFamily.Resource => "icon-resource",
        _ => "icon-logic",
    };

    /// <summary>
    /// The icon of a file (by extension) or folder: scenes <c>movie</c>, resources <c>package</c>, images <c>photo</c>,
    /// audio <c>music</c>, models <c>file-3d</c>, fonts, UI documents and styles, translations, shaders, C#, the project
    /// file, JSON and text; anything else <c>file</c>.
    /// </summary>
    public static string ForFile(string path, bool isDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (isDirectory)
            return "folder";
        var name = Path.GetFileName(path.AsSpan());
        if (name.Equals("project.mfproj", StringComparison.OrdinalIgnoreCase))
            return "file-settings";
        var extension = Path.GetExtension(name);
        // Shaders: name.vk.vert / .frag / .comp (+ .spv)
        return extension switch
        {
            _ when Is(extension, ".mscene") => "movie",
            _ when Is(extension, ".mres") => "package",
            _ when Is(extension, ".msong") => "music",
            _ when Is(extension, ".png") || Is(extension, ".jpg") || Is(extension, ".jpeg") || Is(extension, ".tga") || Is(extension, ".bmp") ||
                   Is(extension, ".ktx2") || Is(extension, ".dds") || Is(extension, ".hdr") => "photo",
            _ when Is(extension, ".wav") || Is(extension, ".ogg") || Is(extension, ".mp3") || Is(extension, ".flac") => "file-music",
            _ when Is(extension, ".gltf") || Is(extension, ".glb") || Is(extension, ".fbx") || Is(extension, ".obj") || Is(extension, ".dae") => "file-3d",
            _ when Is(extension, ".ttf") || Is(extension, ".otf") => "typography",
            _ when Is(extension, ".rml") => "file-type-html",
            _ when Is(extension, ".rcss") => "file-type-css",
            _ when Is(extension, ".po") || Is(extension, ".pot") || Is(extension, ".mo") => "language",
            _ when Is(extension, ".slang") || Is(extension, ".vert") || Is(extension, ".frag") || Is(extension, ".comp") || Is(extension, ".glsl") => "file-code",
            _ when Is(extension, ".spv") => "binary",
            _ when Is(extension, ".cs") => "brand-c-sharp",
            _ when Is(extension, ".csproj") || Is(extension, ".mfproj") || Is(extension, ".sln") || Is(extension, ".slnx") => "file-settings",
            _ when Is(extension, ".json") || Is(extension, ".atlas") || Is(extension, ".skel") => "braces",
            _ when Is(extension, ".txt") || Is(extension, ".md") => "file-text",
            _ => "file",
        };
    }

    /// <summary>The family class of a file's icon: folders <c>icon-dir</c>, scenes and models 3D, audio, resources; empty for the rest.</summary>
    public static string FileFamily(string path, bool isDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (isDirectory)
            return "icon-dir";
        return ForFile(path) switch
        {
            "movie" or "file-3d" => "icon-3d",
            "file-music" or "music" => "icon-audio",
            "package" or "photo" or "typography" => "icon-resource",
            "file-type-html" or "file-type-css" => "icon-ui",
            _ => "",
        };
    }

    private static bool Is(ReadOnlySpan<char> extension, string candidate) => extension.Equals(candidate, StringComparison.OrdinalIgnoreCase);

    private static Entry Resolve(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Cache.TryGetValue(type, out var entry))
            return entry;
        string? icon = null;
        var family = EditorIconFamily.Inherit;
        for (var t = type; t is not null && (icon is null || family == EditorIconFamily.Inherit); t = t.BaseType)
        {
            var (declaredIcon, declaredFamily) = Declared(t);
            icon ??= declaredIcon;
            if (family == EditorIconFamily.Inherit)
                family = declaredFamily;
        }

        if (family == EditorIconFamily.Inherit)
            family = typeof(Resource).IsAssignableFrom(type) ? EditorIconFamily.Resource : EditorIconFamily.Logic;
        icon ??= typeof(Resource).IsAssignableFrom(type) ? "package" : Default;
        var familyClass = FamilyClass(family);
        entry = new Entry(icon, family, familyClass, $"icon icon-{icon} {familyClass}");
        Cache.AddOrUpdate(type, entry);
        return entry;
    }

    // The generated registration when the type has one; [EditorIcon] by reflection for types compiled without the
    // generator (editor-only nodes).
    private static (string? Icon, EditorIconFamily Family) Declared(Type type)
    {
        if (TypeRegistry.Get(type) is { } info && ReferenceEquals(info.Type, type))
            return (info.Icon, info.IconFamily);
        return type.GetCustomAttribute<EditorIconAttribute>(inherit: false) is { } attribute
            ? (attribute.Name.Length > 0 ? attribute.Name : null, attribute.Family)
            : (null, EditorIconFamily.Inherit);
    }
}
