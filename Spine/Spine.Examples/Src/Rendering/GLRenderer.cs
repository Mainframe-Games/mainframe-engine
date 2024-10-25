using Mainframe.Silk;
using Silk.NET.OpenGL;
using Shader = Mainframe.Silk.Shader;

namespace SilkSpine.Rendering;

internal class GLRenderer(GL gl)
{
    public void Draw(VertexArrayObject<float, uint> va, BufferObject<uint> ib, Shader shader)
    {
        shader.Use();
        va.Bind();
        ib.Bind();
        
        gl.DrawElements(PrimitiveType.Triangles, ib.GetCount(), GLEnum.UnsignedInt, IntPtr.Zero);
    }
}