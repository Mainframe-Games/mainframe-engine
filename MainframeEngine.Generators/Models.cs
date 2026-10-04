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
    string? Group,
    bool Translatable);

/// <summary>A <c>[Signal]</c> event.</summary>
internal sealed record SignalModel(string Name, string DelegateType, EquatableArray<string> ParameterTypes, bool CanForward);

/// <summary>A <c>[SerializedMigration(n)]</c> method.</summary>
internal sealed record MigrationModel(int FromVersion, string MethodName);

/// <summary>How generated replication code reads and writes one value with <c>NetCodec</c>.</summary>
internal enum NetCodecKind
{
    /// <summary><c>NetCodec.Write(w, v)</c> / <c>NetCodec.Read(r, out T v)</c> overloads (primitives, string, vectors...).</summary>
    Builtin,

    /// <summary>An enum: written as its underlying integer type (<see cref="NetValueModel.UnderlyingType"/>).</summary>
    Enum,

    /// <summary>A struct implementing <c>INetworkTransferable</c>: <c>NetCodec.WriteValue</c> / <c>ReadValue</c>.</summary>
    Transferable,
}

/// <summary>A value the network code serializes: a <c>[Replicated]</c> member or an <c>[Rpc]</c> parameter.</summary>
internal sealed record NetValueModel(string Name, string Type, NetCodecKind Kind, string? UnderlyingType);

/// <summary>A <c>[Replicated]</c> property or field.</summary>
internal sealed record ReplicatedModel(NetValueModel Value, bool Interpolate);

/// <summary>An <c>[Rpc]</c> method.</summary>
internal sealed record RpcModel(string Name, int Mode, bool Reliable, bool CallLocal, bool IsPublic, EquatableArray<NetValueModel> Parameters);

/// <summary>
/// Everything the emitters need about one node or resource type. Per-member features add a collection here,
/// fill it in <see cref="TypeModelBuilder"/> and get their own emitter; <c>[Replicated]</c>/<c>[Rpc]</c>
/// (<see cref="Replicated"/>, <see cref="Rpcs"/>) are emitted by <see cref="ReplicationEmitter"/> and the
/// registration emitter stays unchanged.
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
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary><c>[Replicated]</c> members in declaration order (networking, <see cref="ReplicationEmitter"/>).</summary>
    public EquatableArray<ReplicatedModel> Replicated { get; init; } = EquatableArray<ReplicatedModel>.Empty;

    /// <summary><c>[Rpc]</c> methods in declaration order.</summary>
    public EquatableArray<RpcModel> Rpcs { get; init; } = EquatableArray<RpcModel>.Empty;

    /// <summary>The containing namespace (null for the global namespace).</summary>
    public string? Namespace { get; init; }

    /// <summary>The type and every containing type are public.</summary>
    public bool IsPublic { get; init; }

    /// <summary>Containing types and the type, joined with <c>_</c> (unique within the namespace).</summary>
    public string FlatName { get; init; } = TypeName;

    public bool HasNetworking => Replicated.Count > 0 || Rpcs.Count > 0;
}
