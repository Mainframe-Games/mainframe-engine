using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Per-instance colour variation from the instance's origin (ADR 0175): a stand of one tree species, or a field of one
/// grass clump, reads as many plants instead of copies. Nothing is stored per instance: the vertex shader hashes the
/// instance transform's translation (<c>include/instance_variation.slang</c>, the same maths as here) into two numbers in
/// [−1, 1], one scaling the albedo's brightness by 1 ± <see cref="FoliageMaterial3D.InstanceValueJitter"/>, the other
/// shifting its hue warmer (olive, yellow) or cooler (blue-green) by up to <see cref="FoliageMaterial3D.InstanceHueJitter"/>.
/// Every level of detail of one tree (meshes and impostor) shares its origin, so they agree.
/// </summary>
public static class InstanceVariation
{
    /// <summary>Origins are quantised to 1/16 m before hashing (so float noise in a transform does not change the result).</summary>
    public const float Quantum = 1f / 16f;

    /// <summary>The per-channel direction of a warm hue shift (red and a little green up, blue down); cool is its negative.</summary>
    public static readonly Vector3 WarmAxis = new(0.45f, 0.12f, -0.7f);

    /// <summary>The material block's <c>variation</c> vector: x = value jitter, y = hue jitter (each 0–0.5).</summary>
    public static Vector4 Pack(float valueJitter, float hueJitter) =>
        new(Math.Clamp(valueJitter, 0f, 0.5f), Math.Clamp(hueJitter, 0f, 0.5f), 0f, 0f);

    /// <summary>The hash of an origin (PCG, as <c>instanceHash</c> in the shader).</summary>
    public static uint Hash(Vector3 origin)
    {
        var x = (uint)(int)MathF.Floor(origin.X / Quantum + 0.5f);
        var y = (uint)(int)MathF.Floor(origin.Y / Quantum + 0.5f);
        var z = (uint)(int)MathF.Floor(origin.Z / Quantum + 0.5f);
        return Pcg(x + Pcg(y + Pcg(z)));
    }

    /// <summary>The two numbers in [−1, 1] an origin gives: x for the value, y for the hue.</summary>
    public static Vector2 Random(Vector3 origin)
    {
        var h = Hash(origin);
        return new Vector2((h & 0xFFFFu) / 65535f * 2f - 1f, (h >> 16) / 65535f * 2f - 1f);
    }

    /// <summary>The per-channel albedo factor of an instance at <paramref name="origin"/> (never negative).</summary>
    public static Vector3 Tint(Vector3 origin, float valueJitter, float hueJitter)
    {
        if (valueJitter <= 0f && hueJitter <= 0f)
            return Vector3.One;
        var r = Random(origin);
        var value = 1f + Math.Clamp(valueJitter, 0f, 0.5f) * r.X;
        var hue = Vector3.One + Math.Clamp(hueJitter, 0f, 0.5f) * r.Y * WarmAxis;
        return Vector3.Max(hue * value, Vector3.Zero);
    }

    private static uint Pcg(uint v)
    {
        var state = v * 747796405u + 2891336453u;
        var word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }
}
