using System.Runtime.InteropServices;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Spine;
using Skeleton = Spine.Skeleton;
using Texture = Mainframe.Silk.Texture;

namespace SilkSpine;

internal class SpineModel
{
    private static readonly uint[] _vertexOrderNormal = [0, 1, 2, 4];
    private static readonly uint[] _vertexOrderReverse = [4, 2, 1, 0];

    private const int MAX_VERTICES_PER_ATTACHMENT = 2048;
    private readonly float[] _worldVerticesPositions = new float[MAX_VERTICES_PER_ATTACHMENT];
    private readonly Vertex[] _vertices = new Vertex[MAX_VERTICES_PER_ATTACHMENT];

    private float _anti_z_fighting_index;

    /// <summary>
    ///  For 3D set to -1f
    /// </summary>
    public float SP_LAYER_SPACING_BASE { get; set; }

    /// <summary>
    /// For 3D set to 0.5f
    /// </summary>
    public float SP_LAYER_SPACING { get; set; }

    /// <summary>
    /// Configure spine to draw double faced and to minimize zfigting artifacts
    /// </summary>
    public bool SP_DRAW_DOUBLE_FACED { get; set; }
    public bool SP_RENDER_WIREFRAME { get; set; }

    private struct Vertex
    {
        // Position in x/y plane
        // csharpier-ignore
        public float x, y, z;

        // UV coordinates
        // csharpier-ignore
        public float u, v;

        // Color, each channel in the range from 0-1
        // (Should really be a 32-bit RGBA packed color)
        // csharpier-ignore
        public float r, g, b, a;
    }

    private readonly GL gl;
    private readonly Skeleton skeleton;
    private readonly Texture texture;
    private readonly bool pma;
    private readonly BufferObject<Vertex> _vertexBuffer;
    private readonly BufferObject<uint> _indexBuffer;
    private readonly VertexArrayObject<Vertex, uint> vbo;

    public ushort DrawCalls { get; private set; }
    
    public SpineModel(GL gl, Skeleton skeleton, bool pma, Texture texture)
    {
        this.gl = gl;
        this.skeleton = skeleton;
        this.pma = pma;
        this.texture = texture;
        
        _vertexBuffer = new BufferObject<Vertex>(gl, [], BufferTargetARB.ArrayBuffer);
        _indexBuffer = new BufferObject<uint>(gl, [], BufferTargetARB.ElementArrayBuffer);
        vbo = new VertexArrayObject<Vertex, uint>(gl, _vertexBuffer, _indexBuffer);
        
        var vertexSize = (uint)Marshal.SizeOf<Vertex>() / sizeof(float);
        vbo.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, vertexSize, 0);
        vbo.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, vertexSize, 3);
        vbo.VertexAttributePointer(2, 4, VertexAttribPointerType.Float, vertexSize, 5);
    }
    
    public void Draw()
    {
        DrawCalls = 0;
        var vertexIndex = 0;

        // For each slot in the draw order array of the skeleton
        _anti_z_fighting_index = SP_LAYER_SPACING_BASE;
        for (int i = skeleton.DrawOrder.Count - 1; i >= 0; i--)
        {
            _anti_z_fighting_index -= SP_LAYER_SPACING;
            var slot = skeleton.DrawOrder.Items[i];

            // Fetch the currently active attachment, continue
            // with the next slot in the draw order if no
            // attachment is active on the slot
            var attachment = slot.Attachment;
            if (attachment is null)
                continue;

            // Calculate the tinting color based on the skeleton's color
            // and the slot's color. Each color channel is given in the
            // range [0-1], you may have to multiply by 255 and cast to
            // and int if your engine uses integer ranges for color channels.
            var tintA = skeleton.A * slot.A;
            var alpha = pma ? tintA : 1;
            var tintR = skeleton.R * slot.R * alpha;
            var tintG = skeleton.G * slot.G * alpha;
            var tintB = skeleton.B * slot.B * alpha;

            // Fill the vertices array depending on the type of attachment
            Texture texture;

            switch (attachment)
            {
                // Cast to an spRegionAttachment so we can get the rendererObject
                // and compute the world vertices
                case RegionAttachment regionAttachment:

                    {
                        // Our engine specific Texture is stored in the spAtlasRegion which was
                        // assigned to the attachment on load. It represents the texture atlas
                        // page that contains the image the region attachment is mapped to
                        texture = (Texture)
                            ((AtlasRegion)regionAttachment.Region).page.rendererObject;

                        // Computed the world vertices positions for the 4 vertices that make up
                        // the rectangular region attachment. This assumes the world transform of the
                        // bone to which the slot (and hence attachment) is attached has been calculated
                        // before rendering via spSkeleton_updateWorldTransform
                        regionAttachment.ComputeWorldVertices(slot, _worldVerticesPositions, 0);

                        // Create 2 triangles, with 3 vertices each from the region's
                        // world vertex positions and its UV coordinates (in the range [0-1]).
                        AddVertex(
                            _worldVerticesPositions[0],
                            _worldVerticesPositions[1],
                            regionAttachment.UVs[0],
                            regionAttachment.UVs[1],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[2],
                            _worldVerticesPositions[3],
                            regionAttachment.UVs[2],
                            regionAttachment.UVs[3],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[4],
                            _worldVerticesPositions[5],
                            regionAttachment.UVs[4],
                            regionAttachment.UVs[5],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[4],
                            _worldVerticesPositions[5],
                            regionAttachment.UVs[4],
                            regionAttachment.UVs[5],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[6],
                            _worldVerticesPositions[7],
                            regionAttachment.UVs[6],
                            regionAttachment.UVs[7],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[0],
                            _worldVerticesPositions[1],
                            regionAttachment.UVs[0],
                            regionAttachment.UVs[1],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            ref vertexIndex
                        );

                        // BeginBlendMode(slot);
                        // var vertexOrder =
                        //     skeleton.ScaleX * skeleton.ScaleY < 0
                        //         ? _vertexOrderNormal
                        //         : _vertexOrderReverse;
                        // DrawRegion(_vertices, vertexIndex, texture, vertexOrder);
                    }
                    break;

                // Cast to an spMeshAttachment so we can get the rendererObject
                // and compute the world vertices
                case MeshAttachment mesh:

                    {
                        // Check the number of vertices in the mesh attachment. If it is bigger
                        // than our scratch buffer, we don't render the mesh. We do this here
                        // for simplicity, in production you want to reallocate the scratch buffer
                        // to fit the mesh.
                        if (mesh.WorldVerticesLength > MAX_VERTICES_PER_ATTACHMENT)
                            continue;

                        // Our engine specific Texture is stored in the spAtlasRegion which was
                        // assigned to the attachment on load. It represents the texture atlas
                        // page that contains the image the mesh attachment is mapped to
                        texture = (Texture)((AtlasRegion)mesh.Region).page.rendererObject;

                        // Computed the world vertices positions for the vertices that make up
                        // the mesh attachment. This assumes the world transform of the
                        // bone to which the slot (and hence attachment) is attached has been calculated
                        // before rendering via spSkeleton_updateWorldTransform
                        mesh.ComputeWorldVertices(slot, _worldVerticesPositions);

                        // Mesh attachments use an array of vertices, and an array of indices to define which
                        // 3 vertices make up each triangle. We loop through all triangle indices
                        // and simply emit a vertex for each triangle's vertex.
                        for (int j = 0; j < mesh.Triangles.Length; ++j)
                        {
                            var index = mesh.Triangles[j] << 1;
                            AddVertex(
                                _worldVerticesPositions[index],
                                _worldVerticesPositions[index + 1],
                                mesh.UVs[index],
                                mesh.UVs[index + 1],
                                tintR,
                                tintG,
                                tintB,
                                tintA,
                                ref vertexIndex
                            );
                        }
                        // BeginBlendMode(slot);
                        // var vertexOrder =
                        //     skeleton.ScaleX * skeleton.ScaleY < 0
                        //         ? _vertexOrderNormal
                        //         : _vertexOrderReverse;
                        // DrawRegion(_vertices, vertexIndex, texture, vertexOrder);
                    }

                    break;
            } // end attachment
            // EndBlendMode();
        } // end draw order
        
        BeginBlendMode();
        {
            var vertexOrder =
                skeleton.ScaleX * skeleton.ScaleY < 0
                    ? _vertexOrderNormal
                    : _vertexOrderReverse;
            DrawRegion(_vertices, vertexIndex, texture, _vertexOrderNormal);
        }
        EndBlendMode();
    }
    
    private void DrawRegion(Vertex[] vertices, int count, Texture texture, uint[] vertexOrder)
    {
        texture.Bind();
        vbo.Bind();
        
        // _vertexBuffer.Bind();
        _vertexBuffer.Update(vertices);
        
        // _indexBuffer.Bind();
        _indexBuffer.Update(vertexOrder);
        
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
        DrawCalls++;
    }

    private void BeginBlendMode()
    {
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }
    
    private void BeginBlendMode(Slot slot)
    {
        gl.Enable(EnableCap.Blend);
        if (pma)
        {
            // Console.WriteLine($"BeginBlendMode_pma: {slot.Data.BlendMode}");
            switch (slot.Data.BlendMode)
            {
                case BlendMode.Normal:
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    break;
                case BlendMode.Additive:
                    break;
                case BlendMode.Multiply:
                    break;
                case BlendMode.Screen:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        else
        {
            // Console.WriteLine($"BeginBlendMode: {slot.Data.BlendMode}");
            switch (slot.Data.BlendMode)
            {
                case BlendMode.Normal:
                    gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                    break;
                case BlendMode.Additive:
                    break;
                case BlendMode.Multiply:
                    break;
                case BlendMode.Screen:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private void EndBlendMode()
    {
        gl.Disable(EnableCap.Blend);
    }

    private void AddVertex(
        float x,
        float y,
        float u,
        float v,
        float r,
        float g,
        float b,
        float a,
        ref int index
    )
    {
        // pos
        _vertices[index].x = x;
        _vertices[index].y = y;
        _vertices[index].z = 0;

        // uv
        _vertices[index].u = u;
        _vertices[index].v = v;

        // color
        _vertices[index].r = r;
        _vertices[index].g = g;
        _vertices[index].b = b;
        _vertices[index].a = a;
        index++;
    }
}
