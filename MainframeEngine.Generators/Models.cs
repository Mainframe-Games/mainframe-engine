using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MainframeEngine.Generators;

/// <summary>
/// An immutable array with value equality, so models compare structurally and the incremental pipeline can
/// skip regeneration when nothing relevant changed.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items) => _items = items;

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Count => Items.Length;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items)
            hash = unchecked(hash * 31 + item.GetHashCode());
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EquatableArray
{
    public static EquatableArray<T> From<T>(IEnumerable<T> items) where T : IEquatable<T> => new(items.ToImmutableArray());
}

internal enum RegisteredKind
{
    Node,
    Resource,
}

/// <summary>A location that survives incremental caching (no syntax tree references).</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location) =>
        location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, string MessageArg0, string MessageArg1)
{
    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(), MessageArg0, MessageArg1);
}

/// <summary>An <c>[Export]</c> member.</summary>
internal sealed record ExportModel(
    string Name,
    string ValueType,
    string CodecExpression,
    string? Range,
    string? File,
    bool Directory,
    bool Multiline,
    bool Flags,
    string? NodeType,
    string? Group);

/// <summary>A <c>[Signal]</c> event.</summary>
internal sealed record SignalModel(string Name, string DelegateType, EquatableArray<string> ParameterTypes, bool CanForward);

/// <summary>A <c>[SerializedMigration(n)]</c> method.</summary>
internal sealed record MigrationModel(int FromVersion, string MethodName);

/// <summary>
/// Everything the emitters need about one node or resource type. New per-member features (for example
/// <c>[Replicated]</c> for networking) add a collection here, fill it in <see cref="TypeModelBuilder"/> and get
/// their own emitter; the registration emitter stays unchanged.
/// </summary>
internal sealed record TypeModel(
    string FullName,
    string TypeName,
    string? BaseType,
    RegisteredKind Kind,
    bool IsAbstract,
    bool HasFactory,
    bool IsTool,
    int Version,
    EquatableArray<ExportModel> Exports,
    EquatableArray<SignalModel> Signals,
    EquatableArray<MigrationModel> Migrations,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics);
