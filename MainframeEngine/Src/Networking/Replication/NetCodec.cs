using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine.Networking;

using Color = System.Drawing.Color;

/// <summary>
/// Write/read overloads for every type <see cref="ReplicatedAttribute"/> members and <see cref="RpcAttribute"/>
/// parameters may use. Generated code picks the overload at compile time (<c>NetCodec.Read(reader, out Vector3 v)</c>),
/// so nothing boxes. Encodings match <see cref="NetBufferWriter"/>: little-endian, strings as varint length + UTF-8.
/// </summary>
public static class NetCodec
{
    public static void Write(NetBufferWriter w, bool v) => w.Write(v);
    public static void Write(NetBufferWriter w, byte v) => w.Write(v);
    public static void Write(NetBufferWriter w, sbyte v) => w.Write(v);
    public static void Write(NetBufferWriter w, short v) => w.Write(v);
    public static void Write(NetBufferWriter w, ushort v) => w.Write(v);
    public static void Write(NetBufferWriter w, int v) => w.Write(v);
    public static void Write(NetBufferWriter w, uint v) => w.Write(v);
    public static void Write(NetBufferWriter w, long v) => w.Write(v);
    public static void Write(NetBufferWriter w, ulong v) => w.Write(v);
    public static void Write(NetBufferWriter w, float v) => w.Write(v);
    public static void Write(NetBufferWriter w, double v) => w.Write(v);
    public static void Write(NetBufferWriter w, decimal v) => w.Write(v);
    public static void Write(NetBufferWriter w, char v) => w.Write(v);

    /// <summary>Null is sent as an empty string.</summary>
    public static void Write(NetBufferWriter w, string? v) => w.Write((v ?? string.Empty).AsSpan());

    public static void Write(NetBufferWriter w, Vector2 v) => w.Write(in v);
    public static void Write(NetBufferWriter w, Vector3 v) => w.Write(in v);

    public static void Write(NetBufferWriter w, Vector4 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
        w.Write(v.W);
    }

    public static void Write(NetBufferWriter w, Quaternion v) => w.Write(in v);

    /// <summary>ARGB as 4 bytes (named colors arrive as plain ARGB colors).</summary>
    public static void Write(NetBufferWriter w, Color v) => w.Write(v.ToArgb());

    public static void Write(NetBufferWriter w, PeerId v) => w.Write((ulong)v);

    public static void Write(NetBufferWriter w, Transform3D v)
    {
        w.Write(in v.Basis.X);
        w.Write(in v.Basis.Y);
        w.Write(in v.Basis.Z);
        w.Write(in v.Origin);
    }

    public static void Write(NetBufferWriter w, Transform2D v)
    {
        w.Write(in v.X);
        w.Write(in v.Y);
        w.Write(in v.Origin);
    }

    /// <summary>A struct through its <see cref="INetworkTransferable.NetworkWrite"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Arrays (RPC parameters only): a var-uint length, then the elements.
    public static void Write(NetBufferWriter w, byte[]? v)
    {
        var a = v ?? [];
        w.WriteVarUInt32((uint)a.Length);
        foreach (var x in a)
            w.Write(x);
    }

    public static void Write(NetBufferWriter w, int[]? v)
    {
        var a = v ?? [];
        w.WriteVarUInt32((uint)a.Length);
        foreach (var x in a)
            w.Write(x);
    }

    public static void Write(NetBufferWriter w, float[]? v)
    {
        var a = v ?? [];
        w.WriteVarUInt32((uint)a.Length);
        foreach (var x in a)
            w.Write(x);
    }

    public static void Write(NetBufferWriter w, string[]? v)
    {
        var a = v ?? [];
        w.WriteVarUInt32((uint)a.Length);
        foreach (var x in a)
            Write(w, x);
    }

    public static void Read(NetBufferReader r, out byte[] v)
    {
        v = new byte[ArrayLength(r, 1)];
        for (var i = 0; i < v.Length; i++)
            v[i] = r.ReadByte();
    }

    public static void Read(NetBufferReader r, out int[] v)
    {
        v = new int[ArrayLength(r, 4)];
        for (var i = 0; i < v.Length; i++)
            v[i] = r.ReadInt32();
    }

    public static void Read(NetBufferReader r, out float[] v)
    {
        v = new float[ArrayLength(r, 4)];
        for (var i = 0; i < v.Length; i++)
            v[i] = r.ReadSingle();
    }

    public static void Read(NetBufferReader r, out string[] v)
    {
        v = new string[ArrayLength(r, 1)];
        for (var i = 0; i < v.Length; i++)
            v[i] = r.ReadString();
    }

    private static int ArrayLength(NetBufferReader r, int elementBytes)
    {
        var length = r.ReadVarUInt32();
        if (length > (uint)r.Remaining / (uint)elementBytes)
            throw new InvalidDataException($"Array length {length} exceeds the {r.Remaining} bytes left.");
        return (int)length;
    }

    public static void WriteValue<T>(NetBufferWriter w, in T v) where T : struct, INetworkTransferable => w.WriteValue(in v);

    public static void Read(NetBufferReader r, out bool v) => v = r.ReadBoolean();
    public static void Read(NetBufferReader r, out byte v) => v = r.ReadByte();
    public static void Read(NetBufferReader r, out sbyte v) => v = r.ReadSByte();
    public static void Read(NetBufferReader r, out short v) => v = r.ReadInt16();
    public static void Read(NetBufferReader r, out ushort v) => v = r.ReadUInt16();
    public static void Read(NetBufferReader r, out int v) => v = r.ReadInt32();
    public static void Read(NetBufferReader r, out uint v) => v = r.ReadUInt32();
    public static void Read(NetBufferReader r, out long v) => v = r.ReadInt64();
    public static void Read(NetBufferReader r, out ulong v) => v = r.ReadUInt64();
    public static void Read(NetBufferReader r, out float v) => v = r.ReadSingle();
    public static void Read(NetBufferReader r, out double v) => v = r.ReadDouble();
    public static void Read(NetBufferReader r, out decimal v) => v = r.ReadDecimal();
    public static void Read(NetBufferReader r, out char v) => v = r.ReadChar();

    /// <summary>Allocates the string (only when the member changed).</summary>
    public static void Read(NetBufferReader r, out string v) => v = r.ReadString();

    public static void Read(NetBufferReader r, out Vector2 v) => v = r.ReadVector2();
    public static void Read(NetBufferReader r, out Vector3 v) => v = r.ReadVector3();

    public static void Read(NetBufferReader r, out Vector4 v)
    {
        var x = r.ReadSingle();
        var y = r.ReadSingle();
        var z = r.ReadSingle();
        var w = r.ReadSingle();
        v = new Vector4(x, y, z, w);
    }

    public static void Read(NetBufferReader r, out Quaternion v) => v = r.ReadQuaternion();

    public static void Read(NetBufferReader r, out Color v) => v = Color.FromArgb(r.ReadInt32());

    public static void Read(NetBufferReader r, out PeerId v) => v = new PeerId(r.ReadUInt64());

    public static void Read(NetBufferReader r, out Transform3D v)
    {
        var x = r.ReadVector3();
        var y = r.ReadVector3();
        var z = r.ReadVector3();
        var origin = r.ReadVector3();
        v = new Transform3D(new Basis(x, y, z), origin);
    }

    public static void Read(NetBufferReader r, out Transform2D v)
    {
        var x = r.ReadVector2();
        var y = r.ReadVector2();
        var origin = r.ReadVector2();
        v = new Transform2D(x, y, origin);
    }

    /// <summary>A struct through its <see cref="INetworkTransferable.NetworkRead"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadValue<T>(NetBufferReader r, out T v) where T : struct, INetworkTransferable => v = r.ReadValue<T>();
}

/// <summary>
/// Blends for <see cref="ReplicatedAttribute.Interpolate"/> members. <c>t</c> may exceed 1 (extrapolation), so these
/// are unclamped.
/// </summary>
public static class NetLerp
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static double Lerp(double a, double b, float t) => a + (b - a) * t;

    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;

    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    /// <summary>Shortest-path spherical blend; past 1 it keeps rotating at the same angular rate.</summary>
    public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
    {
        if (t is >= 0 and <= 1)
            return Quaternion.Slerp(a, b, t);

        // Extrapolate: apply the a→b rotation (t - 1) more times past b.
        if (Quaternion.Dot(a, b) < 0)
            b = -b;
        var delta = Quaternion.Normalize(b * Quaternion.Conjugate(a));
        var angle = 2 * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
        if (angle < 1e-6f)
            return b;
        var axis = new Vector3(delta.X, delta.Y, delta.Z) / MathF.Sin(angle / 2);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, angle * (t - 1)) * b);
    }
}
