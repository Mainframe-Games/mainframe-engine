// The built-in structs of canvas surface shaders (mainframe.canvas re-exports them; documented there). The engine's
// generated entries import this module alone: they include the engine's own constants textually.
module mainframe_surface_canvas;

/// What vertex() reads and writes. Items whose shader has vertex() keep local vertices and draw alone.
public struct CanvasVertex
{
    /// The position in the item's local space; write it to move the vertex.
    public float2 vertex;
    /// The texture coordinate.
    public float2 uv;
    /// The vertex colour (the item's modulate).
    public float4 color;
    /// Local → canvas (the item's canvas-space transform).
    public float4x4 modelMatrix;
    /// Canvas → screen pixels (camera × stretch).
    public float4x4 canvasMatrix;
    /// Screen pixels → clip.
    public float4x4 screenMatrix;
    /// Seconds (the scene tree's shader time).
    public float time;
    /// The size of one texel of the item's texture, in UV.
    public float2 texturePixelSize;
    /// Zero.
    public float4 instanceCustom;
    /// The instance's index.
    public int instanceId;
    /// The vertex's index.
    public int vertexId;
};

/// What fragment() reads and writes.
public struct CanvasFragment
{
    /// The output colour: on entry, the vertex colour × the item's texture at uv.
    public float4 color;
    /// The texture coordinate.
    public float2 uv;
    /// The position in screen pixels.
    public float2 vertex;
    /// The window position (pixels).
    public float4 fragCoord;
    /// The position in the screen, 0..1.
    public float2 screenUv;
    /// The size of one screen pixel in screenUv.
    public float2 screenPixelSize;
    /// The size of one texel of the item's texture, in UV.
    public float2 texturePixelSize;
    /// Seconds.
    public float time;
    /// The item's texture.
    public Sampler2D texture;
};
