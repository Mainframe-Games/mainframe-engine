using System.Numerics;

namespace SilkTest.Examples;

public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector2 TexCoords;
    public Vector3 Bitangent;

    public const int MAX_BONE_INFLUENCE = 4;
    public int[] BoneIds;
    public float[] Weights;
}
