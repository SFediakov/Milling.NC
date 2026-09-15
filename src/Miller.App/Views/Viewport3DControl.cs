using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Miller.App.Rendering;
using Miller.App.ViewModels;

namespace Miller.App.Views;

// Hosts the OpenGL scene. Every GL call happens inside the three overrides; input only changes the
// camera and asks for a frame.
public sealed class Viewport3DControl : OpenGlControlBase
{
    public const float OrbitDegreesPerPixel = 0.4f;
    public const float PanUnitsPerPixelPerDistance = 0.0025f;
    public const float ZoomStepFactor = 1.15f;

    public static readonly StyledProperty<ViewportViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<Viewport3DControl, ViewportViewModel?>(nameof(ViewModel));

    private SceneRenderer? _scene;
    private Point _lastPointer;
    private bool _orbiting;
    private bool _panning;

    public Viewport3DControl()
    {
        Focusable = true;
    }

    public ViewportViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public Camera? Camera => _scene?.Camera;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is ViewportViewModel old)
            {
                old.RequestRender -= OnRequestRender;
            }

            if (change.NewValue is ViewportViewModel added)
            {
                added.RequestRender += OnRequestRender;
            }

            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _scene = new SceneRenderer(gl, GlVersion);
        }
        catch (InvalidOperationException ex)
        {
            ViewModel?.ReportGlError($"OpenGL init failed ({GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}): {ex.Message}");
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _scene?.Dispose();
        _scene = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_scene is null)
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            _scene.Sync(viewModel);
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var error = _scene.Render((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        if (error != GlConsts.GL_NO_ERROR)
        {
            ViewModel?.ReportGlError($"OpenGL error 0x{error:X} while rendering");
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (e.ClickCount == 2)
        {
            ViewModel?.RequestFit();
            return;
        }

        _orbiting = point.Properties.IsLeftButtonPressed;
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_scene is null || (!_orbiting && !_panning))
        {
            return;
        }

        var position = e.GetPosition(this);
        var dx = (float)(position.X - _lastPointer.X);
        var dy = (float)(position.Y - _lastPointer.Y);
        _lastPointer = position;
        if (_orbiting)
        {
            _scene.Camera.Orbit(-dx * OrbitDegreesPerPixel, dy * OrbitDegreesPerPixel);
        }
        else
        {
            var scale = _scene.Camera.Distance * PanUnitsPerPixelPerDistance;
            _scene.Camera.Pan(-dx * scale, dy * scale);
        }

        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _orbiting = false;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_scene is null)
        {
            return;
        }

        _scene.Camera.Zoom(e.Delta.Y > 0 ? 1f / ZoomStepFactor : ZoomStepFactor);
        RequestNextFrameRendering();
    }

    private void OnRequestRender(object? sender, EventArgs e) => RequestNextFrameRendering();
}
