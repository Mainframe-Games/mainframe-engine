using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MainframeEngine.Generators;

/// <summary>Turns a class declaration into a <see cref="TypeModel"/> when it is a node or resource type.</summary>
internal static class TypeModelBuilder
{
    public const string NodeType = "MainframeEngine.Node";
    public const string ResourceType = "MainframeEngine.Resource";
    private const string ExportAttribute = "MainframeEngine.ExportAttribute";
    private const string ExportGroupAttribute = "MainframeEngine.ExportGroupAttribute";
    private const string SignalAttribute = "MainframeEngine.SignalAttribute";
    private const string ToolAttribute = "MainframeEngine.ToolAttribute";
    private const string TypeNameAttribute = "MainframeEngine.TypeNameAttribute";
    private const string SerializedVersionAttribute = "MainframeEngine.SerializedVersionAttribute";
    private const string SerializedMigrationAttribute = "MainframeEngine.SerializedMigrationAttribute";
    private const string PropertyBagType = "MainframeEngine.Serialization.PropertyBag";
    private const string ReplicatedAttribute = "MainframeEngine.ReplicatedAttribute";
    private const string RpcAttribute = "MainframeEngine.RpcAttribute";
    private const string TransferableInterface = "MainframeEngine.Networking.INetworkTransferable";

    /// <summary>Most <c>[Replicated]</c> members one type may declare (one bit each in a 64-bit change mask).</summary>
    public const int MaxReplicatedPerType = 64;
    private const string Codecs = "global::MainframeEngine.Serialization.Codecs";

    /// <summary>Fully qualified, no nullable annotations (usable in typeof and generic arguments).</summary>
    public static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly HashSet<string> BuiltInValueTypes =
    [
        "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4", "System.Numerics.Quaternion",
        "System.Drawing.Color", "MainframeEngine.Transform3D", "MainframeEngine.Transform2D", "MainframeEngine.NodePath",
    ];

    /// <summary>
    /// Cheap syntactic filter: classes that could derive from Node/Resource (or be them). Every part of a partial
    /// class qualifies, because only one part needs the base list and the type is built from its first part.
    /// </summary>
    public static bool IsCandidate(SyntaxNode node) =>
        node is ClassDeclarationSyntax c
        && (c.BaseList is not null || c.Modifiers.Any(SyntaxKind.PartialKeyword) || c.Identifier.Text is "Node" or "Resource");

    public static TypeModel? Build(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type)
            return null;

        // Partial types: build once, from the first declaration.
        var first = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is not null && (first.SyntaxTree != declaration.SyntaxTree || first.Span != declaration.Span))
            return null;

        var kind = Classify(type);
        if (kind is null || type.IsStatic || IsGenericOrInGeneric(type) || type.IsFileLocal)
            return null;

        return Build(type, kind.Value, ct);
    }

    internal static TypeModel? Build(INamedTypeSymbol type, RegisteredKind kind, CancellationToken ct)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var members = OrderedMembers(type);
        var hasAnnotatedMembers = members.Any(m => HasAttribute(m, ExportAttribute) || HasAttribute(m, SignalAttribute)
                                                   || HasAttribute(m, ReplicatedAttribute) || HasAttribute(m, RpcAttribute));

        if (!IsAccessibleFromAssembly(type))
        {
            if (!hasAnnotatedMembers)
                return null;
            diagnostics.Add(new DiagnosticInfo(Diagnostics.TypeNotAccessible.Id, LocationInfo.From(type.Locations.FirstOrDefault()),
                type.ToDisplayString(), ""));
            return Empty(type, kind, diagnostics);
        }

        var exports = new List<ExportModel>();
        var signals = new List<SignalModel>();
        var migrations = new List<MigrationModel>();
        var replicated = new List<ReplicatedModel>();
        var rpcs = new List<RpcModel>();
        string? group = null;

        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            if (GetAttribute(member, ExportGroupAttribute) is { } groupAttribute)
                group = groupAttribute.ConstructorArguments.FirstOrDefault().Value as string is { Length: > 0 } g ? g : null;

            // Networking attributes are independent of [Export]: a member may be both saved and replicated.
            if (GetAttribute(member, ReplicatedAttribute) is { } replicatedAttribute)
            {
                if (BuildReplicated(member, replicatedAttribute, diagnostics) is { } model)
                    replicated.Add(model);
            }

            if (member is IMethodSymbol rpcMethod && GetAttribute(rpcMethod, RpcAttribute) is { } rpcAttribute)
            {
                if (BuildRpc(rpcMethod, rpcAttribute, diagnostics) is { } model)
                    rpcs.Add(model);
            }

            if (GetAttribute(member, ExportAttribute) is { } export)
            {
                if (BuildExport(member, export, group, diagnostics) is { } model)
                    exports.Add(model);
            }
            else if (member is IEventSymbol evt && HasAttribute(evt, SignalAttribute))
            {
                if (BuildSignal(evt, diagnostics) is { } model)
                    signals.Add(model);
            }
            else if (member is IMethodSymbol method && GetAttribute(method, SerializedMigrationAttribute) is { } migration)
            {
                if (BuildMigration(method, migration, diagnostics) is { } model)
                    migrations.Add(model);
            }
        }

        if (replicated.Count > MaxReplicatedPerType)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.TooManyReplicated.Id, LocationInfo.From(type.Locations.FirstOrDefault()),
                type.Name, replicated.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            replicated.RemoveRange(MaxReplicatedPerType, replicated.Count - MaxReplicatedPerType);
        }

        return new TypeModel(
            FullName: type.ToDisplayString(TypeFormat),
            TypeName: GetAttribute(type, TypeNameAttribute)?.ConstructorArguments.FirstOrDefault().Value as string ?? type.Name,
            BaseType: type.BaseType is { SpecialType: not SpecialType.System_Object } baseType ? baseType.ToDisplayString(TypeFormat) : null,
            Kind: kind,
            IsAbstract: type.IsAbstract,
            HasFactory: !type.IsAbstract && HasAccessibleParameterlessConstructor(type),
            IsTool: HasAttribute(type, ToolAttribute),
            Version: GetAttribute(type, SerializedVersionAttribute)?.ConstructorArguments.FirstOrDefault().Value as int? ?? 1,
            Exports: EquatableArray.From(exports),
            Signals: EquatableArray.From(signals),
            Migrations: EquatableArray.From(migrations),
            Location: LocationInfo.From(type.Locations.FirstOrDefault()),
            Diagnostics: EquatableArray.From(diagnostics))
        {
            Replicated = EquatableArray.From(replicated),
            Rpcs = EquatableArray.From(rpcs),
            Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            IsPublic = IsPublicFromOutside(type),
            FlatName = FlatName(type),
        };
    }

    // Inaccessible types are reported, never registered (see RegistrationEmitter).
    private static TypeModel Empty(INamedTypeSymbol type, RegisteredKind kind, List<DiagnosticInfo> diagnostics) =>
        new(type.ToDisplayString(TypeFormat), type.Name, null, kind, type.IsAbstract, false, false, 1,
            EquatableArray<ExportModel>.Empty, EquatableArray<SignalModel>.Empty, EquatableArray<MigrationModel>.Empty,
            LocationInfo.From(type.Locations.FirstOrDefault()), EquatableArray.From(diagnostics));

    internal static RegisteredKind? Classify(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var name = t.ToDisplayString();
            if (name == NodeType)
                return RegisteredKind.Node;
            if (name == ResourceType)
                return RegisteredKind.Resource;
        }

        return null;
    }

    private static ExportModel? BuildExport(ISymbol member, AttributeData attribute, string? group, List<DiagnosticInfo> diagnostics)
    {
        ITypeSymbol valueType;
        var location = LocationInfo.From(member.Locations.FirstOrDefault());
        switch (member)
        {
            case IPropertySymbol property:
                if (property.IsStatic || property.IsIndexer || property.GetMethod is null || property.SetMethod is null
                    || property.SetMethod.IsInitOnly || !IsAccessible(property.GetMethod) || !IsAccessible(property.SetMethod))
                {
                    diagnostics.Add(new DiagnosticInfo(Diagnostics.ExportNotAccessible.Id, location, member.Name,
                        "property with a public or internal getter and setter (not init-only)"));
                    return null;
                }

                valueType = property.Type;
                break;
            case IFieldSymbol field:
                if (field.IsStatic || field.IsReadOnly || field.IsConst || !IsAccessible(field))
                {
                    diagnostics.Add(new DiagnosticInfo(Diagnostics.ExportNotAccessible.Id, location, member.Name,
                        "public or internal field that is not readonly or const"));
                    return null;
                }

                valueType = field.Type;
                break;
            default:
                return null;
        }

        var codec = CodecFor(valueType);
        if (codec is null)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.UnsupportedExportType.Id, location, member.Name, valueType.ToDisplayString()));
            return null;
        }

        string? range = null, file = null, nodeType = null;
        bool directory = false, multiline = false, flags = false, translatable = false;
        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Range": range = named.Value.Value as string; break;
                case "File": file = named.Value.Value as string ?? ""; break;
                case "Directory": directory = named.Value.Value is true; break;
                case "Multiline": multiline = named.Value.Value is true; break;
                case "Flags": flags = named.Value.Value is true; break;
                case "NodeType": nodeType = (named.Value.Value as ITypeSymbol)?.ToDisplayString(TypeFormat); break;
                case "Translatable": translatable = named.Value.Value is true; break;
            }
        }

        if (translatable && !IsStringOrStringCollection(valueType))
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.TranslatableNotString.Id, location, member.Name, valueType.ToDisplayString()));
            translatable = false;
        }

        return new ExportModel(member.Name, valueType.ToDisplayString(TypeFormat), codec, range, file, directory, multiline, flags, nodeType, group,
            translatable);
    }

    /// <summary><c>string</c>, <c>string[]</c> or <c>List&lt;string&gt;</c>: the types <c>Translatable</c> accepts.</summary>
    private static bool IsStringOrStringCollection(ITypeSymbol type) => type switch
    {
        { SpecialType: SpecialType.System_String } => true,
        IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_String } => true,
        INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named =>
            named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>"
            && named.TypeArguments[0].SpecialType == SpecialType.System_String,
        _ => false,
    };

    /// <summary>The codec expression for <paramref name="type"/>, or null when scenes cannot store it.</summary>
    internal static string? CodecFor(ITypeSymbol type)
    {
        var display = type.ToDisplayString(TypeFormat);
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_String:
                return $"{Codecs}.Get<{display}>()";
        }

        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return CodecFor(array.ElementType) is { } element ? $"{Codecs}.ArrayOf({element})" : null;

        if (type is not INamedTypeSymbol named)
            return null;

        if (named.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum)
            return $"{Codecs}.EnumOf<{display}>()";

        var metadataName = named.OriginalDefinition.ToDisplayString();
        if (BuiltInValueTypes.Contains(metadataName))
            return $"{Codecs}.Get<{display}>()";

        if (metadataName == "System.Collections.Generic.List<T>" && named.TypeArguments.Length == 1)
            return CodecFor(named.TypeArguments[0]) is { } element ? $"{Codecs}.ListOf({element})" : null;

        if (named.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class && Classify(named) == RegisteredKind.Resource)
            return $"{Codecs}.ResourceOf<{display}>()";

        return null;
    }

    private static SignalModel? BuildSignal(IEventSymbol evt, List<DiagnosticInfo> diagnostics)
    {
        var location = LocationInfo.From(evt.Locations.FirstOrDefault());
        if (evt.IsStatic || !IsAccessible(evt) || evt.Type is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke } delegateType)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.SignalNotAccessible.Id, location, evt.Name, ""));
            return null;
        }

        var parameters = invoke.Parameters.Select(p => p.Type.ToDisplayString(TypeFormat)).ToImmutableArray();
        var canForward = invoke.ReturnsVoid && invoke.Parameters.Length <= 8
                         && invoke.Parameters.All(p => p.RefKind == RefKind.None && !p.Type.IsRefLikeType);
        return new SignalModel(evt.Name, delegateType.ToDisplayString(TypeFormat), new EquatableArray<string>(parameters), canForward);
    }

    private static ReplicatedModel? BuildReplicated(ISymbol member, AttributeData attribute, List<DiagnosticInfo> diagnostics)
    {
        var location = LocationInfo.From(member.Locations.FirstOrDefault());
        ITypeSymbol valueType;
        switch (member)
        {
            case IPropertySymbol property:
                if (property.IsStatic || property.IsIndexer || property.GetMethod is null || property.SetMethod is null
                    || property.SetMethod.IsInitOnly || !IsAccessible(property.GetMethod) || !IsAccessible(property.SetMethod))
                {
                    diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidReplicated.Id, location, member.Name,
                        "must be a non-static property with a public or internal getter and setter (not init-only)"));
                    return null;
                }

                valueType = property.Type;
                break;
            case IFieldSymbol field:
                if (field.IsStatic || field.IsReadOnly || field.IsConst || !IsAccessible(field))
                {
                    diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidReplicated.Id, location, member.Name,
                        "must be a non-static public or internal field that is not readonly or const"));
                    return null;
                }

                valueType = field.Type;
                break;
            default:
                return null;
        }

        var value = NetValue(member.Name, valueType, requireEquatable: true, out var error);
        if (value is null)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidReplicated.Id, location, member.Name, error ?? ""));
            return null;
        }

        var interpolate = attribute.NamedArguments.Any(a => a.Key == "Interpolate" && a.Value.Value is true);
        if (interpolate && !IsInterpolatable(valueType))
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidReplicated.Id, location, member.Name,
                $"has type '{valueType.ToDisplayString()}', which cannot be interpolated (float, double, Vector2/3/4, Quaternion)"));
            return null;
        }

        return new ReplicatedModel(value, interpolate);
    }

    private static RpcModel? BuildRpc(IMethodSymbol method, AttributeData attribute, List<DiagnosticInfo> diagnostics)
    {
        var location = LocationInfo.From(method.Locations.FirstOrDefault());
        if (method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod || !IsAccessible(method)
            || method.MethodKind != MethodKind.Ordinary)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidRpc.Id, location, method.Name,
                "must be a non-static, non-generic public or internal method returning void"));
            return null;
        }

        var parameters = new List<NetValueModel>();
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.IsParams)
            {
                diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidRpc.Id, location, method.Name,
                    $"parameter '{parameter.Name}' must be passed by value (no ref, out, in or params)"));
                return null;
            }

            if (parameter.Name.StartsWith("__mf", System.StringComparison.Ordinal))
            {
                diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidRpc.Id, location, method.Name,
                    $"parameter '{parameter.Name}' uses the '__mf' prefix reserved for generated code"));
                return null;
            }

            var value = NetValue(parameter.Name, parameter.Type, requireEquatable: false, out var error);
            if (value is null)
            {
                diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidRpc.Id, location, method.Name, $"parameter '{parameter.Name}' {error}"));
                return null;
            }

            parameters.Add(value);
        }

        // Enum arguments arrive boxed as the enum's underlying type (RpcMode is a byte).
        var mode = attribute.ConstructorArguments.FirstOrDefault().Value is System.IConvertible m
            ? m.ToInt32(System.Globalization.CultureInfo.InvariantCulture)
            : 1; // RpcMode.Authority
        var reliable = true;
        var callLocal = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Reliable")
                reliable = named.Value.Value is true;
            else if (named.Key == "CallLocal")
                callLocal = named.Value.Value is true;
        }

        return new RpcModel(method.Name, mode, reliable, callLocal, method.DeclaredAccessibility == Accessibility.Public,
            EquatableArray.From(parameters));
    }

    private static readonly HashSet<string> BuiltInNetTypes =
    [
        "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4", "System.Numerics.Quaternion",
        "System.Drawing.Color", "MainframeEngine.Networking.PeerId", "MainframeEngine.Transform3D", "MainframeEngine.Transform2D",
    ];

    private static readonly HashSet<string> InterpolatableTypes =
    [
        "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4", "System.Numerics.Quaternion",
    ];

    /// <summary>How the network codec handles <paramref name="type"/>, or null (with <paramref name="error"/>).</summary>
    internal static NetValueModel? NetValue(string name, ITypeSymbol type, bool requireEquatable, out string? error)
    {
        error = null;
        var display = type.ToDisplayString(TypeFormat);
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_Char:
            case SpecialType.System_String:
                return new NetValueModel(name, display, NetCodecKind.Builtin, null);
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum && named.EnumUnderlyingType is { } underlying)
                return new NetValueModel(name, display, NetCodecKind.Enum, underlying.ToDisplayString(TypeFormat));

            if (BuiltInNetTypes.Contains(named.OriginalDefinition.ToDisplayString()))
                return new NetValueModel(name, display, NetCodecKind.Builtin, null);

            if (named.TypeKind == Microsoft.CodeAnalysis.TypeKind.Struct && !named.IsRefLikeType
                && named.AllInterfaces.Any(i => i.ToDisplayString() == TransferableInterface))
            {
                if (requireEquatable && !named.AllInterfaces.Any(i =>
                        i.OriginalDefinition.ToDisplayString() == "System.IEquatable<T>"
                        && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], named)))
                {
                    error = $"has type '{type.ToDisplayString()}': replicated INetworkTransferable structs must implement " +
                            $"IEquatable<{type.Name}> (change detection must not box)";
                    return null;
                }

                return new NetValueModel(name, display, NetCodecKind.Transferable, null);
            }
        }

        error = $"has type '{type.ToDisplayString()}', which the network codec cannot send (bool, integers, float, double, decimal, " +
                "char, string, enums, Vector2/3/4, Quaternion, Color, Transform3D/2D, PeerId and INetworkTransferable structs)";
        return null;
    }

    private static bool IsInterpolatable(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Single or SpecialType.System_Double
        || InterpolatableTypes.Contains(type.OriginalDefinition.ToDisplayString());

    private static bool IsPublicFromOutside(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (t.DeclaredAccessibility != Accessibility.Public)
                return false;
        return true;
    }

    private static string FlatName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
            parts.Insert(0, t.Name);
        return string.Join("_", parts);
    }

    private static MigrationModel? BuildMigration(IMethodSymbol method, AttributeData attribute, List<DiagnosticInfo> diagnostics)
    {
        var valid = method.IsStatic && method.ReturnsVoid && IsAccessible(method) && method.Parameters.Length == 1
                    && method.Parameters[0].Type.ToDisplayString() == PropertyBagType && !method.IsGenericMethod;
        if (!valid || attribute.ConstructorArguments.FirstOrDefault().Value is not int from)
        {
            diagnostics.Add(new DiagnosticInfo(Diagnostics.InvalidMigration.Id, LocationInfo.From(method.Locations.FirstOrDefault()), method.Name, ""));
            return null;
        }

        return new MigrationModel(from, method.Name);
    }

    /// <summary>Declared members in source order (partial declarations by file, then position).</summary>
    private static List<ISymbol> OrderedMembers(INamedTypeSymbol type) =>
        type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource))
            .OrderBy(m => m.Locations.First(l => l.IsInSource).SourceTree?.FilePath, System.StringComparer.Ordinal)
            .ThenBy(m => m.Locations.First(l => l.IsInSource).SourceSpan.Start)
            .ToList();

    private static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && IsAccessible(c))
        && !type.GetMembers().Any(m => m is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });

    private static bool IsAccessible(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

    private static bool IsAccessibleFromAssembly(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (!IsAccessible(t))
                return false;
        return true;
    }

    private static bool IsGenericOrInGeneric(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
            if (t.TypeParameters.Length > 0)
                return true;
        return false;
    }

    private static AttributeData? GetAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeName);

    private static bool HasAttribute(ISymbol symbol, string attributeName) => GetAttribute(symbol, attributeName) is not null;

    /// <summary>A member name usable in generated code (keywords escaped).</summary>
    public static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
