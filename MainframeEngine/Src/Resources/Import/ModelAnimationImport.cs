using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Key reduction for imported clips (ADR 0208): a channel that never leaves its rest value is dropped, a constant one
/// keeps one key, and with optimisation keys that linear interpolation of their neighbours reproduces within a
/// tolerance are removed.
/// </summary>
internal static class KeyReduction
{
    /// <summary>Reduces a vector channel in place; null when the track is not needed.</summary>
    public static AnimationInterpolation? Reduce(ref float[] times, ref Vector3[] values, Vector3 rest, bool optimize, float tolerance)
    {
        if (values.Length == 0)
            return null;
        var constant = true;
        foreach (var v in values)
            constant &= Vector3.DistanceSquared(v, values[0]) <= tolerance * tolerance;
        if (constant)
        {
            if (Vector3.DistanceSquared(values[0], rest) <= tolerance * tolerance)
                return null;
            times = [times[0]];
            values = [values[0]];
            return AnimationInterpolation.Linear;
        }

        if (!optimize || values.Length < 3)
            return AnimationInterpolation.Linear;

        var t = times;
        var vals = values;
        var keep = Simplify(times.Length, (a, b, j) =>
        {
            var f = (t[j] - t[a]) / Math.Max(t[b] - t[a], 1e-9f);
            return Vector3.DistanceSquared(Vector3.Lerp(vals[a], vals[b], f), vals[j]) <= tolerance * tolerance;
        });
        times = [.. keep.Select(i => t[i])];
        values = [.. keep.Select(i => vals[i])];
        return AnimationInterpolation.Linear;
    }

    /// <summary>Reduces a rotation channel in place (tolerance: 1 − |dot|); null when the track is not needed.</summary>
    public static AnimationInterpolation? Reduce(ref float[] times, ref Quaternion[] values, Quaternion rest, bool optimize, float tolerance)
    {
        if (values.Length == 0)
            return null;
        static bool Close(Quaternion a, Quaternion b, float tolerance) => 1f - MathF.Abs(Quaternion.Dot(a, b)) <= tolerance;
        var constant = true;
        foreach (var q in values)
            constant &= Close(q, values[0], tolerance);
        if (constant)
        {
            if (Close(values[0], rest, tolerance))
                return null;
            times = [times[0]];
            values = [values[0]];
            return AnimationInterpolation.Linear;
        }

        if (!optimize || values.Length < 3)
            return AnimationInterpolation.Linear;

        var t = times;
        var vals = values;
        var keep = Simplify(times.Length, (a, b, j) =>
        {
            var f = (t[j] - t[a]) / Math.Max(t[b] - t[a], 1e-9f);
            return Close(Quaternion.Slerp(vals[a], vals[b], f), vals[j], tolerance);
        });
        times = [.. keep.Select(i => t[i])];
        values = [.. keep.Select(i => vals[i])];
        return AnimationInterpolation.Linear;
    }

    // Greedy: extend each segment from the last kept key while every key inside it is reproduced.
    private static List<int> Simplify(int count, Func<int, int, int, bool> reproduces)
    {
        var keep = new List<int> { 0 };
        var anchor = 0;
        for (var next = 2; next < count; next++)
        {
            var ok = true;
            for (var j = anchor + 1; j < next && ok; j++)
                ok = reproduces(anchor, next, j);
            if (!ok)
            {
                anchor = next - 1;
                keep.Add(anchor);
            }
        }

        keep.Add(count - 1);
        return keep;
    }
}

/// <summary>
/// The <c>extras</c> of a glTF file's animations (ADR 0208), which Assimp does not import: <c>loop</c> (a bool or a loop
/// mode name) and <c>events</c> (<c>{ "HammerFist": 0.42 }</c>, or <c>[ { "name", "time" } ]</c>). Blender's glTF exporter
/// writes an action's custom properties there when <i>Include → Custom Properties</i> is on.
/// </summary>
internal sealed class GltfAnimationExtras
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint JsonChunk = 0x4E4F534A; // "JSON"

    private readonly List<(string Name, AnimationLoopMode? Loop, List<ModelClipEvent>? Events)> _animations = [];

    public static GltfAnimationExtras Read(string fullPath, string projectPath)
    {
        var extras = new GltfAnimationExtras();
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            return extras;
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var json = Json(bytes);
            if (json.IsEmpty)
                return extras;
            using var document = JsonDocument.Parse(json.ToArray());
            if (!document.RootElement.TryGetProperty("animations", out var animations) || animations.ValueKind != JsonValueKind.Array)
                return extras;
            foreach (var animation in animations.EnumerateArray())
            {
                var name = animation.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                AnimationLoopMode? loop = null;
                List<ModelClipEvent>? events = null;
                if (animation.TryGetProperty("extras", out var extra) && extra.ValueKind == JsonValueKind.Object)
                {
                    if (extra.TryGetProperty("loop", out var l))
                        loop = ModelImportSettings.ParseLoop(l);
                    if (extra.TryGetProperty("events", out var e))
                        events = ModelImportSettings.ParseEvents(e);
                }

                extras._animations.Add((name, loop, events));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"[Import] '{projectPath}': could not read the glTF animation extras: {e.Message}");
        }

        return extras;
    }

    /// <summary>The extras of animation <paramref name="index"/> (or, failing that, of the one named <paramref name="name"/>).</summary>
    public (AnimationLoopMode? Loop, IReadOnlyList<ModelClipEvent>? Events)? Get(int index, string name)
    {
        if ((uint)index < (uint)_animations.Count && (_animations[index].Name == name || name.Length == 0))
            return (_animations[index].Loop, _animations[index].Events);
        foreach (var animation in _animations)
            if (animation.Name == name)
                return (animation.Loop, animation.Events);
        return null;
    }

    // The JSON of a .gltf file, or the JSON chunk of a .glb.
    private static ReadOnlySpan<byte> Json(byte[] bytes)
    {
        if (bytes.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            return type == JsonChunk && 20 + (long)length <= bytes.Length ? bytes.AsSpan(20, (int)length) : default;
        }

        return bytes;
    }
}
