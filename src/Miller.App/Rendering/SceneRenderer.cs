using System.Numerics;
using Avalonia.OpenGL;
using Miller.App.ViewModels;

namespace Miller.App.Rendering;

// Composes the renderers; the single place that clears the frame and issues draw calls. Sync copies
// what changed in the view model into GPU buffers (by version counters); Render draws the frame.
public sealed class SceneRenderer : IDisposable
{
    public const float AmbientLight = 0.35f;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.3f, -0.5f, 0.8f));

    private readonly GlInterface _gl;
    private readonly GlFunctions _functions;
    private readonly ShaderProgram _lit;
    private readonly ShaderProgram _lines;
    private readonly int _litMvp;
    private readonly int _litLight;
    private readonly int _litAmbient;
    private readonly int _linesMvp;
    private readonly Vector4 _background;
    private readonly AxisTriadRenderer _axes;
    private readonly MeshRenderer _mesh;
    private int _meshVersion = -1;
    private int _stockVersion = -1;
    private bool _showModel = true;
    private bool _showStock = true;

    public SceneRenderer(GlInterface gl, GlVersion version)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _functions = new GlFunctions(gl);
        _lit = new ShaderProgram(gl, version, GlShaders.LitVertex, GlShaders.ColorFragment);
        _lines = new ShaderProgram(gl, version, GlShaders.LineVertex, GlShaders.ColorFragment);
        _litMvp = _lit.Uniform("uModelViewProjection");
        _litLight = _lit.Uniform("uLightDirection");
        _litAmbient = _lit.Uniform("uAmbient");
        _linesMvp = _lines.Uniform("uModelViewProjection");
        _background = ThemeColors.Get("ViewportBackgroundColor");
        _axes = new AxisTriadRenderer(gl, ThemeColors.Get("AxisXColor"), ThemeColors.Get("AxisYColor"), ThemeColors.Get("AxisZColor"), ThemeColors.Get("StockColor"));
        _mesh = new MeshRenderer(gl, ThemeColors.Get("ModelColor"));
    }

    public Camera Camera { get; } = new();

    public void Sync(ViewportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _showModel = viewModel.ShowModel;
        _showStock = viewModel.ShowStock;
        if (viewModel.MeshVersion != _meshVersion)
        {
            _meshVersion = viewModel.MeshVersion;
            _mesh.Upload(viewModel.Mesh);
        }

        if (viewModel.StockVersion != _stockVersion)
        {
            _stockVersion = viewModel.StockVersion;
            _axes.Build(viewModel.StockBounds, viewModel.StockDefinition);
        }

        if (viewModel.FitPending)
        {
            viewModel.ClearFitRequest();
            var bounds = viewModel.Mesh?.Bounds ?? viewModel.StockBounds;
            if (bounds is { IsEmpty: false } target)
            {
                Camera.FitToBounds(target);
            }
        }
    }

    // Returns the OpenGL error code left by this frame, GL_NO_ERROR when the frame was clean.
    public int Render(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return GlConsts.GL_NO_ERROR;
        }

        _gl.Viewport(0, 0, width, height);
        Camera.Aspect = width / (float)height;
        _gl.ClearColor(_background.X, _background.Y, _background.Z, _background.W);
        _gl.Enable(GlConsts.GL_DEPTH_TEST);
        _gl.DepthFunc(GlConstants.GL_LEQUAL);
        _gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT | GlConsts.GL_DEPTH_BUFFER_BIT);

        var viewProjection = Camera.ViewProjection;
        if (_showModel)
        {
            _lit.Use();
            _lit.SetMatrix(_litMvp, viewProjection);
            _functions.Uniform3f(_litLight, LightDirection.X, LightDirection.Y, LightDirection.Z);
            _lit.SetFloat(_litAmbient, AmbientLight);
            _mesh.Draw();
        }

        _lines.Use();
        _lines.SetMatrix(_linesMvp, viewProjection);
        _axes.Draw(_showStock);
        return _gl.GetError();
    }

    public void Dispose()
    {
        _mesh.Dispose();
        _axes.Dispose();
        _lines.Dispose();
        _lit.Dispose();
    }
}
