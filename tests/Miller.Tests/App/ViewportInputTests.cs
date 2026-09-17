
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Miller.App.Rendering;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// Mouse mapping of the viewport without OpenGL: the control owns the camera, so the headless
// platform can drive the pointer and the camera state is asserted directly.
public sealed class ViewportInputTests
{
    private const double Width = 800;
    private const double Height = 600;

    private static (Window Window, Viewport3DControl Viewport, ViewportViewModel ViewModel) Show()
    {
        var viewModel = new ViewportViewModel();
        var viewport = new Viewport3DControl { ViewModel = viewModel, Width = Width, Height = Height };
        var window = new Window { Width = Width, Height = Height, Content = viewport };
        window.Show();
        window.UpdateLayout();
        return (window, viewport, viewModel);
    }

    [AvaloniaFact]
    public void RightDrag_Orbits_MiddleDrag_Pans_LeftDrag_DoesNeither()
    {
        var (window, viewport, _) = Show();
        try
        {
            var camera = viewport.Camera;
            var yaw = camera.Yaw;
            var pitch = camera.Pitch;
            var target = camera.Target;

            window.MouseDown(new Point(400, 300), MouseButton.Right);
            window.MouseMove(new Point(450, 320), RawInputModifiers.RightMouseButton);
            window.MouseUp(new Point(450, 320), MouseButton.Right);
            Assert.Equal(yaw - 50 * Viewport3DControl.OrbitDegreesPerPixel, camera.Yaw, 3);
            Assert.Equal(pitch + 20 * Viewport3DControl.OrbitDegreesPerPixel, camera.Pitch, 3);
            Assert.Equal(target, camera.Target);

            yaw = camera.Yaw;
            window.MouseDown(new Point(400, 300), MouseButton.Middle);
            window.MouseMove(new Point(430, 300), RawInputModifiers.MiddleMouseButton);
            window.MouseUp(new Point(430, 300), MouseButton.Middle);
            Assert.NotEqual(target, camera.Target);
            Assert.Equal(yaw, camera.Yaw);

            target = camera.Target;
            window.MouseDown(new Point(400, 300), MouseButton.Left);
            window.MouseMove(new Point(460, 340), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(460, 340), MouseButton.Left);
            Assert.Equal(yaw, camera.Yaw);
            Assert.Equal(target, camera.Target);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Space_InTheFocusedViewport_RequestsPlayPause()
    {
        var (window, viewport, viewModel) = Show();
        try
        {
            var requests = 0;
            viewModel.PlayPauseRequested += (_, _) => requests++;
            viewport.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Equal(1, requests);
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            Assert.Equal(1, requests);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_Zooms_AndLeftClick_PicksTheModelUnderTheCursor()
    {
        var (window, viewport, viewModel) = Show();
        try
        {
            var camera = viewport.Camera;
            var distance = camera.Distance;
            window.MouseWheel(new Point(400, 300), new Avalonia.Vector(0, -1));
            Assert.Equal(distance * Viewport3DControl.ZoomStepFactor, camera.Distance, 3);
            window.MouseWheel(new Point(400, 300), new Avalonia.Vector(0, 1));
            Assert.Equal(distance, camera.Distance, 3);

            viewModel.SetMeshes(new[] { TestMeshes.Box(10, 10, 5) });
            camera.Target = viewModel.MeshBounds.Center;
            camera.Distance = 40;
            window.MouseDown(new Point(400, 300), MouseButton.Left);
            window.MouseUp(new Point(400, 300), MouseButton.Left);
            Assert.Equal(0, viewModel.SelectedModelIndex);

            window.MouseDown(new Point(20, 20), MouseButton.Left);
            window.MouseUp(new Point(20, 20), MouseButton.Left);
            Assert.Equal(-1, viewModel.SelectedModelIndex);
        }
        finally
        {
            window.Close();
        }
    }
}
