using System.Numerics;

namespace MainframeEngine;

public interface ICamera
{
    Vector3 Position { get; set; }
    Vector3 Forward { get; set; }
    Vector3 Up { get; set; }
    
    Matrix4x4 ViewMatrix { get; }
    Matrix4x4 ProjectionMatrix { get; }

    void ModifyZoom(float zoomAmount);
}