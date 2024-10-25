using Silk.NET.OpenGL;

namespace Mainframe.Silk;

//Our buffer object abstraction.
public class BufferObject<TDataType> : IDisposable
    where TDataType : unmanaged
{
    //Our handle, buffertype and the GL instance this class will use, these are private because they have no reason to be public.
    //Most of the time you would want to abstract items to make things like this invisible.
    private readonly uint _handle;
    private readonly BufferTargetARB _bufferType;
    private readonly GL _gl;

    public BufferObject(GL gl, Span<TDataType> data, BufferTargetARB bufferType)
    {
        _gl = gl;
        _bufferType = bufferType;
        _handle = _gl.GenBuffer();
        Bind();
        Update(data);
    }

    public void Bind()
    {
        _gl.BindBuffer(_bufferType, _handle);
    }

    public unsafe void Update(Span<TDataType> data, BufferUsageARB bufferUsage = BufferUsageARB.StaticDraw)
    {
        fixed (void* d = data)
        {
            _gl.BufferData(
                _bufferType,
                (nuint)(data.Length * sizeof(TDataType)),
                d,
                bufferUsage
            );
        }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_handle);
    }
}
