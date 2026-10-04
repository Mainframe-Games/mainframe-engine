using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>Per-draw matrix work: every lit shape reads View/Projection and its model matrix.</summary>
[MemoryDiagnoser]
public class CameraBenchmarks : IDisposable
{
    private readonly PerspectiveCamera _camera3D = new() { Position = new Vector3(0, 5, 10), AspectRatio = 16f / 9f };
    private readonly OrthographicCamera _camera2D = new() { Size = new Vector2(1920, 1080) };
    private readonly Node3D _node = new() { Position = new Vector3(3, 1, 0), RotationDegrees = new Vector3(10, 20, 30), Scale = new Vector3(2) };

    [Benchmark]
    public Matrix4x4 Camera3DViewProjection() => _camera3D.ViewMatrix * _camera3D.ProjectionMatrix;

    [Benchmark]
    public Matrix4x4 Camera2DViewProjection() => _camera2D.ViewMatrix * _camera2D.ProjectionMatrix;

    [Benchmark]
    public Vector3 Camera3DModifyDirection()
    {
        _camera3D.ModifyDirection(0.1f, 0.05f);
        return _camera3D.Forward;
    }

    /// <summary>Recomposes the (cached) model matrix after a transform change: TRS → matrix.</summary>
    [Benchmark]
    public Matrix4x4 Node3DModelMatrix()
    {
        _node.Position = new Vector3(3, 1, 0);
        return _node.ModelMatrix;
    }

    public void Dispose() => _node.Dispose();
}
