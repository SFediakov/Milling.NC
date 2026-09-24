using System.Numerics;
using Avalonia.OpenGL;
using Miller.App.ViewModels;
using Miller.Core.Analysis;
using Miller.Core.Toolpaths;

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
    private readonly List<MeshRenderer> _models = new();
    private readonly Vector4 _modelColor;
    private readonly Vector4 _selectionColor;
    private readonly ToolpathRenderer _toolpath;
    private readonly HeightMapRenderer _stockMap;
    private readonly ToolRenderer _tool;
    private int _meshVersion = -1;
    private int _stockVersion = -1;
    private int _toolpathVersion = -1;
    private int _stockMapVersion = -1;
    private int _categoriesVersion = -1;
    private readonly Dictionary<CellCategory, Vector4> _categoryColors;
    private int _toolVersion = -1;
    private bool _showModel = true;
    private bool _showStock = true;
    private bool _showToolpath = true;
    private bool _showTool = true;
    private int _progressIndex;
    private Vector3 _toolPosition;
    private bool _machineLive;

    // The camera belongs to the control so input works without a GL context.
    public SceneRenderer(GlInterface gl, GlVersion version, Camera camera)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _functions = new GlFunctions(gl);
        _lit = new ShaderProgram(gl, version, GlShaders.LitVertex, GlShaders.ColorFragment);
        _lines = new ShaderProgram(gl, version, GlShaders.LineVertex, GlShaders.ColorFragment);
        _litMvp = _lit.Uniform("uModelViewProjection");
        _litLight = _lit.Uniform("uLightDirection");
        _litAmbient = _lit.Uniform("uAmbient");
        _linesMvp = _lines.Uniform("uModelViewProjection");
        _background = ThemeColors.Get("ViewportBackgroundColor");
        _axes = new AxisTriadRenderer(gl, ThemeColors.Get("AxisXColor"), ThemeColors.Get("AxisYColor"), ThemeColors.Get("AxisZColor"), ThemeColors.Get("StockColor"));
        _modelColor = ThemeColors.Get("ModelColor");
        _selectionColor = ThemeColors.Get("SelectionColor");
        _toolpath = new ToolpathRenderer(
            gl,
            new Dictionary<MoveKind, Vector4>
            {
                [MoveKind.Rapid] = ThemeColors.Get("ToolpathRapidColor"),
                [MoveKind.Feed] = ThemeColors.Get("ToolpathFeedColor"),
                [MoveKind.Plunge] = ThemeColors.Get("ToolpathPlungeColor"),
            },
            _background);
        _stockMap = new HeightMapRenderer(gl);
        _categoryColors = new Dictionary<CellCategory, Vector4>
        {
            [CellCategory.Ok] = ThemeColors.Get("CategoryOkColor"),
            [CellCategory.RestMaterial] = ThemeColors.Get("CategoryRestMaterialColor"),
            [CellCategory.Gouge] = ThemeColors.Get("CategoryGougeColor"),
            [CellCategory.NoModel] = ThemeColors.Get("StockColor"),
            [CellCategory.Overhang] = ThemeColors.Get("CategoryOverhangColor"),
            [CellCategory.HeadLimited] = ThemeColors.Get("CategoryHeadLimitedColor"),
            [CellCategory.CornerLimited] = ThemeColors.Get("CategoryCornerLimitedColor"),
        };
        _tool = new ToolRenderer(gl, ThemeColors.Get("ToolCutterColor"), ThemeColors.Get("ToolHeadColor"));
    }

    public Camera Camera { get; }

    public void Sync(ViewportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _showModel = viewModel.ShowModel;
        _showStock = viewModel.ShowStock;
        _showToolpath = viewModel.ShowToolpath;
        _showTool = viewModel.ShowTool;
        _progressIndex = viewModel.ToolpathProgressIndex;
        _toolPosition = viewModel.ToolPosition;
        _machineLive = viewModel.MachineLive;
        if (viewModel.ToolpathVersion != _toolpathVersion)
        {
            _toolpathVersion = viewModel.ToolpathVersion;
            _toolpath.Upload(viewModel.Toolpath);
        }

        if (viewModel.StockMapVersion != _stockMapVersion)
        {
            _stockMapVersion = viewModel.StockMapVersion;
            _stockMap.Upload(viewModel.StockMap, viewModel.StockFloorZ, ThemeColors.Get("StockColor"));
            viewModel.TakeStockDirty();
            _categoriesVersion = -1;
        }

        if (viewModel.CategoriesVersion != _categoriesVersion)
        {
            _categoriesVersion = viewModel.CategoriesVersion;
            if (viewModel.StockCategories is { } categories && viewModel.StockMap is not null)
            {
                _stockMap.SetCategories(categories, _categoryColors);
            }
            else if (viewModel.StockMap is not null)
            {
                _stockMap.Upload(viewModel.StockMap, viewModel.StockFloorZ, ThemeColors.Get("StockColor"));
            }
        }
        else if (viewModel.StockMap is { } stockMap && !viewModel.StockDirty.IsEmpty)
        {
            var dirty = viewModel.TakeStockDirty();
            _stockMap.Update(_functions, stockMap, dirty.I0, dirty.J0, dirty.I1, dirty.J1);
        }

        if (viewModel.ToolVersion != _toolVersion)
        {
            _toolVersion = viewModel.ToolVersion;
            _tool.Build(viewModel.Tool);
        }
        if (viewModel.MeshVersion != _meshVersion)
        {
            _meshVersion = viewModel.MeshVersion;
            foreach (var renderer in _models)
            {
                renderer.Dispose();
            }

            _models.Clear();
            for (var k = 0; k < viewModel.Meshes.Count; k++)
            {
                var renderer = new MeshRenderer(_gl, k == viewModel.SelectedModelIndex ? _selectionColor : _modelColor);
                renderer.Upload(viewModel.Meshes[k]);
                _models.Add(renderer);
            }
        }

        if (viewModel.StockVersion != _stockVersion)
        {
            _stockVersion = viewModel.StockVersion;
            _axes.Build(viewModel.StockBounds, viewModel.StockDefinition);
        }

        if (viewModel.FitPending)
        {
            viewModel.ClearFitRequest();
            var bounds = viewModel.Meshes.Count > 0 ? viewModel.MeshBounds : viewModel.StockBounds;
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
        _lit.Use();
        _lit.SetMatrix(_litMvp, viewProjection);
        _functions.Uniform3f(_litLight, LightDirection.X, LightDirection.Y, LightDirection.Z);
        _lit.SetFloat(_litAmbient, AmbientLight);
        if (_showModel)
        {
            foreach (var renderer in _models)
            {
                renderer.Draw();
            }
        }

        if (_showStock)
        {
            _stockMap.Draw();
        }

        if (_showTool && (_toolpath.SegmentCount > 0 || _machineLive) && _tool.HasGeometry)
        {
            _tool.Draw(_lit, _litMvp, viewProjection, _toolPosition);
        }

        _lines.Use();
        _lines.SetMatrix(_linesMvp, viewProjection);
        _axes.Draw(_showStock);
        if (_showToolpath)
        {
            _toolpath.Draw(_progressIndex);
        }
        return _gl.GetError();
    }

    public void Dispose()
    {
        _tool.Dispose();
        _stockMap.Dispose();
        _toolpath.Dispose();
        foreach (var renderer in _models)
        {
            renderer.Dispose();
        }

        _axes.Dispose();
        _lines.Dispose();
        _lit.Dispose();
    }
}
