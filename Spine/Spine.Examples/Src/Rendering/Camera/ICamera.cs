using System.Numerics;

namespace SilkSpine;

public interface ICamera
{
    Matrix4x4 ViewMatrix { get; }
    Matrix4x4 ProjectionMatrix { get; }
}