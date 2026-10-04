using Microsoft.CodeAnalysis;

namespace MainframeEngine.Generators;

internal static class Diagnostics
{
    private const string Category = "MainframeEngine.Serialization";

    public static readonly DiagnosticDescriptor ExportNotAccessible = new(
        "MFG001", "[Export] member is not accessible",
        "[Export] member '{0}' must be a non-static {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedExportType = new(
        "MFG002", "[Export] type is not serializable",
        "[Export] member '{0}' has type '{1}', which scenes cannot store (primitives, string, enums, vectors, " +
        "Quaternion, Color, Transform3D/2D, NodePath, Resource subclasses and arrays/List<T> of these)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SignalNotAccessible = new(
        "MFG003", "[Signal] event is not accessible",
        "[Signal] event '{0}' must be a non-static public or internal event{1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateTypeName = new(
        "MFG004", "Duplicate scene type name",
        "Scene type name '{0}' is used by more than one type in this assembly ({1}); rename one or add [TypeName(\"...\")]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMigration = new(
        "MFG005", "Invalid serialized migration",
        "[SerializedMigration] method '{0}' must be 'static void {0}(MainframeEngine.Serialization.PropertyBag)', public or internal{1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TypeNotAccessible = new(
        "MFG006", "Node or resource type is not accessible",
        "'{0}' declares [Export] or [Signal] members but cannot be registered: it must be public or internal and not nested in a private type{1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "MFG001" => ExportNotAccessible,
        "MFG002" => UnsupportedExportType,
        "MFG003" => SignalNotAccessible,
        "MFG004" => DuplicateTypeName,
        "MFG005" => InvalidMigration,
        _ => TypeNotAccessible,
    };
}
