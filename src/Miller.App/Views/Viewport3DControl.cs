using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private bool _dragging;

    public Viewport3DControl()
    {
        Focusable = true;
    }

    public ViewportViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Owned here, not by the scene: pointer input changes it with or without a GL context.
    public Camera Camera { get; } = new();

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
            _scene = new SceneRenderer(gl, GlVersion, Camera);
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

    // A control without drawn content is invisible to hit testing; the transparent fill sits under
    // the GL surface and makes every pointer event over the viewport ours, with or without GL.
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        base.Render(context);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        // Only a left double click fits; the click counter does not distinguish buttons.
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed)
        {
            ViewModel?.RequestFit();
            return;
        }

        // Right button rotates, the wheel button moves, the left button picks a model and drags
        // it in X and Y while held.
        _orbiting = point.Properties.IsRightButtonPressed;
        _panning = point.Properties.IsMiddleButtonPressed;
        _dragging = point.Properties.IsLeftButtonPressed && ViewModel is { } viewModel && Ray(point.Position) is { } ray
            && viewModel.BeginDrag(ray.Origin, ray.Direction);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_dragging)
        {
            if (ViewModel is { } viewModel && Ray(position) is { } ray)
            {
                viewModel.DragTo(ray.Origin, ray.Direction);
            }

            return;
        }

        if (!_orbiting && !_panning)
        {
            return;
        }

        var dx = (float)(position.X - _lastPointer.X);
        var dy = (float)(position.Y - _lastPointer.Y);
        _lastPointer = position;
        if (_orbiting)
        {
            Camera.Orbit(-dx * OrbitDegreesPerPixel, dy * OrbitDegreesPerPixel);
        }
        else
        {
            var scale = Camera.Distance * PanUnitsPerPixelPerDistance;
            Camera.Pan(-dx * scale, dy * scale);
        }

        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ViewModel?.EndDrag();
        _orbiting = false;
        _panning = false;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // World ray through a viewport pixel for the current camera; null before the control has a size.
    private (Vector3 Origin, Vector3 Direction)? Ray(Point position)
    {
        if (!(Bounds.Width > 0) || !(Bounds.Height > 0))
        {
            return null;
        }

        Camera.Aspect = (float)(Bounds.Width / Bounds.Height);
        return Camera.PickRay((float)position.X, (float)position.Y, (float)Bounds.Width, (float)Bounds.Height);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space)
        {
            ViewModel?.RequestPlayPause();
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Camera.Zoom(e.Delta.Y > 0 ? 1f / ZoomStepFactor : ZoomStepFactor);
        RequestNextFrameRendering();
    }

    private void OnRequestRender(object? sender, EventArgs e) => RequestNextFrameRendering();
}
