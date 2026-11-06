using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// Mesh simplification for generated levels of detail (G6.4, ADR 0256) through the <c>mfmeshopt</c> native library:
/// meshoptimizer 1.3, the simplifier Godot's importer uses. <see cref="GenerateLods"/> builds index-only
/// <see cref="MeshLod"/> levels for a surface; the model importer calls it for <c>"generateLods": true</c>.
/// </summary>
public static unsafe partial class MeshSimplifier
{
    private const string Library = "mfmeshopt";
    private const uint AbiVersion = (1 << 16) | 0;

    private const uint OptionLockBorder = 1u << 0;
    private const uint OptionErrorAbsolute = 1u << 2;

    /// <summary>Default weight of each normal component against positions (<see cref="GenerateLods"/>).</summary>
    public const float DefaultNormalWeight = 0.5f;

    /// <summary>A level must keep at most this fraction of the previous level's indices, or generation stops.</summary>
    public const float MinimumReduction = 0.85f;

    /// <summary>Levels stop once a level would have fewer triangles than this.</summary>
    public const int MinimumTriangles = 4;

    private static readonly Lock Gate = new();
    private static int _state; // 0 unknown, 1 available, -1 missing or incompatible
    private static string? _problem;

    /// <summary>Whether <c>mfmeshopt</c> loads on this platform and matches the engine's ABI.</summary>
    public static bool IsAvailable => EnsureLibrary();

    /// <summary>Why <see cref="IsAvailable"/> is false (null when it is true or was not checked yet).</summary>
    public static string? Problem
    {
        get
        {
            EnsureLibrary();
            return _problem;
        }
    }

    /// <summary>
    /// Coarser index-only levels of <paramref name="surface"/>, each about half the previous one's triangles, with their
    /// geometric error in mesh units (meshoptimizer's absolute error, normals included), ascending. Stops when a level
    /// saves less than 15 %, would have fewer than <see cref="MinimumTriangles"/> triangles, or
    /// <see cref="MeshLod.MaxLevels"/> levels exist. <paramref name="lockBorders"/> keeps open edges in place (no cracks
    /// between the surfaces of one mesh). <paramref name="normalWeight"/> is how much normal changes count against
    /// position changes, relative to the mesh's size (0 = positions only). Empty for surfaces with fewer than
    /// 2 × <see cref="MinimumTriangles"/> triangles.
    /// </summary>
    /// <exception cref="InvalidOperationException">The native library is missing or incompatible.</exception>
    public static MeshLod[] GenerateLods(MeshSurface surface, bool lockBorders = false, float normalWeight = DefaultNormalWeight)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!EnsureLibrary())
            throw new InvalidOperationException($"Generated LODs need the mfmeshopt native library: {_problem}");
        var source = surface.Indices;
        var positions = surface.Positions;
        if (source.Length < 6 * MinimumTriangles || source.Length % 3 != 0 || positions.Length == 0)
            return [];

        var normals = surface.Normals.Length == positions.Length && normalWeight > 0f ? surface.Normals : null;
        var options = OptionErrorAbsolute | (lockBorders ? OptionLockBorder : 0u);
        var weights = stackalloc float[3] { normalWeight, normalWeight, normalWeight };
        var scratch = new uint[source.Length];
        var levels = new List<MeshLod>(MeshLod.MaxLevels - 1);
        var previousCount = source.Length;
        var previousError = 0f;
        fixed (int* indices = source)
        fixed (Vector3* p = positions)
        fixed (Vector3* n = normals)
        fixed (uint* destination = scratch)
        {
            while (levels.Count < MeshLod.MaxLevels - 1)
            {
                var target = previousCount / 2 / 3 * 3;
                if (target < 3 * MinimumTriangles)
                    break;
                // Each level is simplified from the full surface (meshoptimizer's advice: errors stay measured against it).
                float error;
                var count = (int)NativeSimplify(destination, (uint*)indices, (nuint)source.Length, (float*)p, (nuint)positions.Length,
                    (nuint)sizeof(Vector3), (float*)n, normals is null ? 0 : (nuint)sizeof(Vector3), normals is null ? null : weights,
                    normals is null ? 0 : 3u, (nuint)target, float.MaxValue, options, &error);
                if (count < 3 * MinimumTriangles || count > previousCount * MinimumReduction)
                    break;
                NativeOptimizeVertexCache(destination, destination, (nuint)count, (nuint)positions.Length);
                error = float.IsFinite(error) ? Math.Max(error, previousError) : previousError;
                var level = new int[count];
                new ReadOnlySpan<uint>(destination, count).CopyTo(MemoryMarshal.Cast<int, uint>(level.AsSpan()));
                levels.Add(new MeshLod(error, level));
                previousCount = count;
                previousError = error;
            }
        }

        return [.. levels];
    }

    private static bool EnsureLibrary()
    {
        if (_state != 0)
            return _state > 0;
        lock (Gate)
        {
            if (_state != 0)
                return _state > 0;
            if (!NativeLibrary.TryLoad(Library, typeof(MeshSimplifier).Assembly, null, out _))
            {
                _problem = $"{Library} is not shipped for {RuntimeInformation.RuntimeIdentifier}";
                _state = -1;
                return false;
            }

            var version = NativeAbiVersion();
            if (version >> 16 != AbiVersion >> 16)
            {
                _problem = $"{Library} ABI {version >> 16}.{version & 0xFFFF} does not match the engine's {AbiVersion >> 16}.{AbiVersion & 0xFFFF}";
                _state = -1;
                return false;
            }

            _state = 1;
            return true;
        }
    }

    [LibraryImport(Library, EntryPoint = "mfmeshopt_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint NativeAbiVersion();

    [LibraryImport(Library, EntryPoint = "mfmeshopt_simplify")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint NativeSimplify(uint* destination, uint* indices, nuint indexCount, float* positions, nuint vertexCount,
        nuint positionStride, float* attributes, nuint attributeStride, float* attributeWeights, nuint attributeCount, nuint targetIndexCount,
        float targetError, uint options, float* resultError);

    [LibraryImport(Library, EntryPoint = "mfmeshopt_optimize_vertex_cache")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void NativeOptimizeVertexCache(uint* destination, uint* indices, nuint indexCount, nuint vertexCount);
}
