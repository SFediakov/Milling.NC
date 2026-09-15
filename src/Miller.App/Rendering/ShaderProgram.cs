using System.Numerics;
using Avalonia.OpenGL;

namespace Miller.App.Rendering;

// Compiles and links one vertex/fragment pair with the platform preamble; failures carry the driver
// log. System.Numerics matrices are row-major with row vectors, which OpenGL reads as the transposed
// column-major matrix, so they are uploaded as they are and multiply column vectors correctly.
public sealed class ShaderProgram : IDisposable
{
    private readonly GlInterface _gl;
    private bool _disposed;

    public ShaderProgram(GlInterface gl, GlVersion version, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        var preamble = GlShaders.Preamble(version);
        var vertex = Compile(GlConsts.GL_VERTEX_SHADER, preamble + vertexSource);
        var fragment = Compile(GlConsts.GL_FRAGMENT_SHADER, preamble + fragmentSource);
        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vertex);
        _gl.AttachShader(Handle, fragment);
        var error = _gl.LinkProgramAndGetError(Handle);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
        if (error is not null)
        {
            _gl.DeleteProgram(Handle);
            throw new InvalidOperationException($"Shader program link failed: {error}");
        }
    }

    public int Handle { get; }

    public void Use() => _gl.UseProgram(Handle);

    public int Uniform(string name)
    {
        var location = _gl.GetUniformLocationString(Handle, name);
        if (location < 0)
        {
            throw new InvalidOperationException($"Uniform {name} does not exist in the shader program (or was optimized away).");
        }

        return location;
    }

    public unsafe void SetMatrix(int location, Matrix4x4 matrix)
        => _gl.UniformMatrix4fv(location, 1, false, &matrix);

    public void SetFloat(int location, float value) => _gl.Uniform1f(location, value);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteProgram(Handle);
    }

    private int Compile(int type, string source)
    {
        var shader = _gl.CreateShader(type);
        var error = _gl.CompileShaderAndGetError(shader, source);
        if (error is not null)
        {
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"Shader compile failed: {error}\n{source}");
        }

        return shader;
    }
}
