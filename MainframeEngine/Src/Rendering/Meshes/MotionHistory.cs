using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A node's model matrix a frame ago, for per-object motion vectors (ADR 0163). The depth prepass asks for it with this
/// frame's matrix every frame it draws the node; the first request of a frame moves the stored matrix to "previous" when
/// it was stored the frame before. A node the prepass did not draw last frame (culled, just added, prepass off) has no
/// history: its previous matrix is this frame's, so it moves with the camera only for that frame. Physics bodies need
/// nothing special: their model matrix is the interpolated render pose.
/// </summary>
internal struct MotionHistory
{
    private ulong _frame;
    private Matrix4x4 _model;
    private Matrix4x4 _previous;

    /// <summary>The model matrix of frame <paramref name="frame"/> − 1, given this frame's <paramref name="model"/>.</summary>
    public Matrix4x4 Previous(in Matrix4x4 model, ulong frame)
    {
        if (_frame != frame)
        {
            _previous = _frame != 0 && _frame + 1 == frame ? _model : model;
            _frame = frame;
        }

        _model = model;
        return _previous;
    }
}
