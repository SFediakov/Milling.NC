using Avalonia.OpenGL;

namespace Miller.App.Rendering;

// One vertex array over one interleaved float buffer, with an optional index buffer. The layout
// lists (attribute location, component count) in buffer order; every attribute is float.
public sealed unsafe class VertexBuffer : IDisposable
{
    private const int FloatBytes = sizeof(float);
    private const int IndexBytes = sizeof(uint);

    private readonly GlInterface _gl;
    private readonly int _vao;
    private readonly int _vbo;
    private readonly int _ebo;
    private bool _disposed;

    public VertexBuffer(GlInterface gl, params (int Location, int Components)[] layout)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        if (layout.Length == 0)
        {
            throw new ArgumentException("A vertex layout needs at least one attribute.", nameof(layout));
        }

        Stride = layout.Sum(a => a.Components);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);
        gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, _ebo);
        var offset = 0;
        foreach (var (location, components) in layout)
        {
            gl.VertexAttribPointer(location, components, GlConsts.GL_FLOAT, 0, Stride * FloatBytes, (IntPtr)(offset * FloatBytes));
            gl.EnableVertexAttribArray(location);
            offset += components;
        }

        gl.BindVertexArray(0);
    }

    // Floats per vertex.
    public int Stride { get; }

    public int VertexCount { get; private set; }

    public int IndexCount { get; private set; }

    public void Upload(float[] data, int floatCount, int usage)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (floatCount % Stride != 0)
        {
            throw new ArgumentException($"{floatCount} floats do not fit a stride of {Stride}.", nameof(floatCount));
        }

        _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);
        fixed (float* p = data)
        {
            _gl.BufferData(GlConsts.GL_ARRAY_BUFFER, (IntPtr)(floatCount * FloatBytes), (IntPtr)p, usage);
        }

        VertexCount = floatCount / Stride;
    }

    public void UploadIndices(uint[] indices, int indexCount, int usage)
    {
        ArgumentNullException.ThrowIfNull(indices);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(GlConsts.GL_ELEMENT_ARRAY_BUFFER, _ebo);
        fixed (uint* p = indices)
        {
            _gl.BufferData(GlConsts.GL_ELEMENT_ARRAY_BUFFER, (IntPtr)(indexCount * IndexBytes), (IntPtr)p, usage);
        }

        _gl.BindVertexArray(0);
        IndexCount = indexCount;
    }

    // Replaces a float range inside an already uploaded buffer.
    public void Update(GlFunctions functions, float[] data, int firstFloat, int floatCount)
    {
        ArgumentNullException.ThrowIfNull(functions);
        if (firstFloat < 0 || floatCount <= 0 || firstFloat + floatCount > VertexCount * Stride)
        {
            throw new ArgumentOutOfRangeException(nameof(firstFloat), $"Range {firstFloat}..{firstFloat + floatCount} is outside the buffer.");
        }

        _gl.BindBuffer(GlConsts.GL_ARRAY_BUFFER, _vbo);
        fixed (float* p = &data[firstFloat])
        {
            functions.BufferSubData(GlConsts.GL_ARRAY_BUFFER, firstFloat * FloatBytes, floatCount * FloatBytes, (IntPtr)p);
        }
    }

    public void Draw(int mode) => Draw(mode, 0, VertexCount);

    public void Draw(int mode, int firstVertex, int vertexCount)
    {
        if (vertexCount <= 0)
        {
            return;
        }

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(mode, firstVertex, (IntPtr)vertexCount);
        _gl.BindVertexArray(0);
    }

    public void DrawIndexed(int mode)
    {
        if (IndexCount <= 0)
        {
            return;
        }

        _gl.BindVertexArray(_vao);
        _gl.DrawElements(mode, IndexCount, GlConstants.GL_UNSIGNED_INT, IntPtr.Zero);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
