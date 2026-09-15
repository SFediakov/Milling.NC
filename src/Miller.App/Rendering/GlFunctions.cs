using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Miller.App.Rendering;

// Entry points that GlInterface lacks, resolved once per context through GetProcAddress. A missing
// entry point throws at construction, so a driver problem shows up at viewport init, not mid-draw.
public sealed class GlFunctions
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BufferSubDataDelegate(int target, IntPtr offset, IntPtr size, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Uniform3fDelegate(int location, float x, float y, float z);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Uniform4fDelegate(int location, float x, float y, float z, float w);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void LineWidthDelegate(float width);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CullFaceDelegate(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void PolygonOffsetDelegate(float factor, float units);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DisableVertexAttribArrayDelegate(int index);

    private readonly BufferSubDataDelegate _bufferSubData;
    private readonly Uniform3fDelegate _uniform3f;
    private readonly Uniform4fDelegate _uniform4f;
    private readonly LineWidthDelegate _lineWidth;
    private readonly CullFaceDelegate _cullFace;
    private readonly PolygonOffsetDelegate _polygonOffset;
    private readonly DisableVertexAttribArrayDelegate _disableVertexAttribArray;

    public GlFunctions(GlInterface gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _bufferSubData = Load<BufferSubDataDelegate>(gl, "glBufferSubData");
        _uniform3f = Load<Uniform3fDelegate>(gl, "glUniform3f");
        _uniform4f = Load<Uniform4fDelegate>(gl, "glUniform4f");
        _lineWidth = Load<LineWidthDelegate>(gl, "glLineWidth");
        _cullFace = Load<CullFaceDelegate>(gl, "glCullFace");
        _polygonOffset = Load<PolygonOffsetDelegate>(gl, "glPolygonOffset");
        _disableVertexAttribArray = Load<DisableVertexAttribArrayDelegate>(gl, "glDisableVertexAttribArray");
    }

    public void BufferSubData(int target, int offsetBytes, int sizeBytes, IntPtr data)
        => _bufferSubData(target, (IntPtr)offsetBytes, (IntPtr)sizeBytes, data);

    public void Uniform3f(int location, float x, float y, float z) => _uniform3f(location, x, y, z);

    public void Uniform4f(int location, float x, float y, float z, float w) => _uniform4f(location, x, y, z, w);

    public void LineWidth(float width) => _lineWidth(width);

    public void CullFace(int mode) => _cullFace(mode);

    public void PolygonOffset(float factor, float units) => _polygonOffset(factor, units);

    public void DisableVertexAttribArray(int index) => _disableVertexAttribArray(index);

    private static T Load<T>(GlInterface gl, string name)
        where T : Delegate
    {
        var address = gl.GetProcAddress(name);
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL entry point {name} is not available in this context.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}
