using System.Numerics;
using Mainframe.Silk;
using Silk.NET.OpenGL;
using Spine;
using Shader = Mainframe.Silk.Shader;
using Skeleton = Spine.Skeleton;
using Texture = Mainframe.Silk.Texture;

namespace SilkSpine;

internal class SpineRenderer
{
    private const int MAX_VERTICES_PER_ATTACHMENT = 2048*2;
    private readonly float[] _worldVerticesPositions = new float[MAX_VERTICES_PER_ATTACHMENT];
    
    /// <summary>
    /// number of floats within the vertex
    /// </summary>
    private const int VERTEX_COUNT = 10; 
    private readonly float[] _vertices = new float[MAX_VERTICES_PER_ATTACHMENT * VERTEX_COUNT];
    private static readonly uint[] _indexArray = [0, 1, 2, 4];

    /// <summary>
    /// Configure spine to draw double faced and to minimize zfigting artifacts
    /// </summary>
    public bool SP_DRAW_DOUBLE_FACED { get; set; }
    public bool SP_RENDER_WIREFRAME { get; set; }
    
    public BlendingFactor SrcFactor = BlendingFactor.One;
    public BlendingFactor DestFactor = BlendingFactor.OneMinusSrcAlpha;

    private readonly GL _gl;
    private readonly Skeleton _skeleton;
    private readonly bool _pma;
    private readonly BufferObject<float> _vertexBuffer;
    private readonly VertexArrayObject<float, uint> _vbo;
    private readonly Shader _shader;
    private readonly List<Texture> _textures;

    // mvp
    private Matrix4x4 _modelMatrix;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    
    public SpineRenderer(GL gl, Skeleton skeleton, bool pma, List<Texture> textures)
    {
        _gl = gl;
        _skeleton = skeleton;
        _pma = pma;
        _textures = textures;

        _vertexBuffer = new BufferObject<float>(gl, [], BufferTargetARB.ArrayBuffer);
        var indexBuffer = new BufferObject<uint>(gl, _indexArray, BufferTargetARB.ElementArrayBuffer);
        _vbo = new VertexArrayObject<float, uint>(gl, _vertexBuffer, indexBuffer);
        
        _vbo.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, VERTEX_COUNT, 0);
        _vbo.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, VERTEX_COUNT, 3);
        _vbo.VertexAttributePointer(2, 4, VertexAttribPointerType.Float, VERTEX_COUNT, 5);
        _vbo.VertexAttributePointer(3, 1, VertexAttribPointerType.Float, VERTEX_COUNT, 9);
        
        _shader = new Shader(gl,
            "Content/Shaders/Spine.vert",
            "Content/Shaders/Spine.frag");
    }

    /// <summary>
    /// Renders the spine animation by drawing the skeleton's attachments in the order specified by the draw order array.
    /// </summary>
    /// <param name="zSpacing">The spacing between attachments along the z-axis.</param>
    /// <param name="model"></param>
    /// <param name="view">The view matrix applied for rendering.</param>
    /// <param name="projection">The projection matrix applied for rendering.</param>
    /// <returns>The number of draw calls made during the rendering process.</returns>
    public void Draw(
        float zSpacing,
        Matrix4x4 model,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        _modelMatrix = model;
        _viewMatrix = view;
        _projectionMatrix = projection;
        
        var vertexIndex = 0;
        var z = 0f;

        // For each slot in the draw order array of the skeleton
        for (int i = 0; i < _skeleton.DrawOrder.Count; i++)
        {
            var slot = _skeleton.DrawOrder.Items[i];

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
            var tintA = _skeleton.A * slot.A;
            var alpha = _pma ? tintA : 1;
            var tintR = _skeleton.R * slot.R * alpha;
            var tintG = _skeleton.G * slot.G * alpha;
            var tintB = _skeleton.B * slot.B * alpha;

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
                            z,
                            regionAttachment.UVs[0],
                            regionAttachment.UVs[1],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[2],
                            _worldVerticesPositions[3],
                            z,
                            regionAttachment.UVs[2],
                            regionAttachment.UVs[3],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[4],
                            _worldVerticesPositions[5],
                            z,
                            regionAttachment.UVs[4],
                            regionAttachment.UVs[5],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[4],
                            _worldVerticesPositions[5],
                            z,
                            regionAttachment.UVs[4],
                            regionAttachment.UVs[5],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[6],
                            _worldVerticesPositions[7],
                            z,
                            regionAttachment.UVs[6],
                            regionAttachment.UVs[7],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );

                        AddVertex(
                            _worldVerticesPositions[0],
                            _worldVerticesPositions[1],
                            z,
                            regionAttachment.UVs[0],
                            regionAttachment.UVs[1],
                            tintR,
                            tintG,
                            tintB,
                            tintA,
                            _textures.IndexOf(texture),
                            ref vertexIndex
                        );
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
                                z,
                                mesh.UVs[index],
                                mesh.UVs[index + 1],
                                tintR,
                                tintG,
                                tintB,
                                tintA,
                                _textures.IndexOf(texture),
                                ref vertexIndex
                            );
                        }
                    }

                    break;
            } // end attachment

            z += zSpacing;
            
        } // end draw order

        // draw
        BeginBlendMode();
        DrawCall(_vertices, vertexIndex);
        EndBlendMode();
    }
    
    private void DrawCall(float[] vertices, int count)
    {
        for (int i = 0; i < _textures.Count; i++)
            _textures[i].BindTextureUnit((uint)i);
        
        _shader.Use();
        _shader.SetUniform("uTextures", [0, 1]);
        _shader.SetUniform("uModel", _modelMatrix);
        
        _shader.SetUniform("uView", _viewMatrix);
        _shader.SetUniform("uProjection", _projectionMatrix);
        
        _vbo.Bind();
        
        _vertexBuffer.Update(vertices);
        _gl.CullFace(TriangleFace.Back);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)count);
    }

    private void BeginBlendMode()
    {
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(SrcFactor, DestFactor);
    }
    
    private void BeginBlendMode(Slot slot)
    {
        _gl.Enable(EnableCap.Blend);
        if (_pma)
        {
            switch (slot.Data.BlendMode)
            {
                case BlendMode.Normal:
                    _gl.BlendFunc(SrcFactor, DestFactor);
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
            switch (slot.Data.BlendMode)
            {
                case BlendMode.Normal:
                    _gl.BlendFunc(SrcFactor, DestFactor);
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
        _gl.Disable(EnableCap.Blend);
    }

    private void AddVertex(
        float x,
        float y,
        float z,
        float u,
        float v,
        float r,
        float g,
        float b,
        float a,
        float textureIndex,
        ref int vertexIndex
    )
    {
        var vertexPos = vertexIndex * VERTEX_COUNT;
        
        // pos
        _vertices[vertexPos + 0] = x;
        _vertices[vertexPos + 1] = y;
        _vertices[vertexPos + 2] = z;

        // uv
        _vertices[vertexPos + 3] = u;
        _vertices[vertexPos + 4] = v;

        // color
        _vertices[vertexPos + 5] = r;
        _vertices[vertexPos + 6] = g;
        _vertices[vertexPos + 7] = b;
        _vertices[vertexPos + 8] = a;
        
        // texture index
        _vertices[vertexPos + 9] = textureIndex;
        
        vertexIndex++;
    }
}
