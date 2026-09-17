using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// Dragging a model in X and Y: the press picks and starts the drag on the horizontal plane through
// the hit point, every move reports the plane delta, hidden models are not hit, and the main view
// model turns the delta into an offset edit.
public sealed class ModelDragTests
{
    private const double Width = 800;
    private const double Height = 600;

    [Fact]
    public void PlanePoint_IntersectsTheHorizontalPlane_AndRefusesParallelOrBackwardRays()
    {
        Assert.True(ViewportViewModel.PlanePoint(new Vector3(0, 0, 10), Vector3.Normalize(new Vector3(1, 1, -1)), 0f, out var point));
        Assert.Equal(10f, point.X, 4);
        Assert.Equal(10f, point.Y, 4);
        Assert.False(ViewportViewModel.PlanePoint(new Vector3(0, 0, 10), Vector3.UnitX, 0f, out _));
        Assert.False(ViewportViewModel.PlanePoint(new Vector3(0, 0, 10), Vector3.UnitZ, 0f, out _));
    }

    [Fact]
    public void BeginDrag_SelectsTheHitModel_AndDragTo_ReportsThePlaneDelta()
    {
        var viewport = new ViewportViewModel();
        viewport.SetMeshes(new[] { TestMeshes.Box(10, 10, 5) });
        var deltas = new List<Vector2>();
        viewport.ModelDragged += (_, d) => deltas.Add(d);

        Assert.False(viewport.BeginDrag(new Vector3(50, 50, 50), -Vector3.UnitZ));
        Assert.False(viewport.IsDragging);
        Assert.Equal(-1, viewport.SelectedModelIndex);

        Assert.True(viewport.BeginDrag(new Vector3(5, 5, 50), -Vector3.UnitZ));
        Assert.True(viewport.IsDragging);
        Assert.Equal(0, viewport.SelectedModelIndex);
        // The hit point is the box top (z 5); a ray to (8, 4) on that plane moves the model by (3, -1).
        viewport.DragTo(new Vector3(8, 4, 50), -Vector3.UnitZ);
        Assert.Single(deltas);
        Assert.Equal(3f, deltas[0].X, 4);
        Assert.Equal(-1f, deltas[0].Y, 4);
        // Same point again: no delta; a parallel ray: no delta.
        viewport.DragTo(new Vector3(8, 4, 50), -Vector3.UnitZ);
        viewport.DragTo(new Vector3(8, 4, 50), Vector3.UnitX);
        Assert.Single(deltas);
        viewport.EndDrag();
        Assert.False(viewport.IsDragging);
        viewport.DragTo(new Vector3(9, 4, 50), -Vector3.UnitZ);
        Assert.Single(deltas);
    }

    [Fact]
    public void HiddenModels_AreNeitherPickedNorDragged()
    {
        var viewport = new ViewportViewModel();
        viewport.SetMeshes(new[] { TestMeshes.Box(10, 10, 5) });
        viewport.ShowModel = false;
        viewport.Pick(new Vector3(5, 5, 50), -Vector3.UnitZ);
        Assert.Equal(-1, viewport.SelectedModelIndex);
        Assert.False(viewport.BeginDrag(new Vector3(5, 5, 50), -Vector3.UnitZ));
        viewport.ShowModel = true;
        viewport.Pick(new Vector3(5, 5, 50), -Vector3.UnitZ);
        Assert.Equal(0, viewport.SelectedModelIndex);
    }

    [Fact]
    public async Task MainViewModel_TurnsTheDragIntoAnOffsetEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-drag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var vm = TestServices.MainWindowViewModel(root);
            var stl = Path.Combine(root, "box.stl");
            File.WriteAllText(stl, TestMeshes.AsciiCubeText());
            Assert.True(await vm.OpenStlFileAsync(stl));
            Assert.Equal(0, vm.Models.SelectedIndex);
            var before = vm.Viewport.Meshes[0].Bounds;
            var raised = new List<string?>();
            vm.Models.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var top = before.Max.Z;
            var center = before.Center;
            Assert.True(vm.Viewport.BeginDrag(new Vector3(center.X, center.Y, top + 10), -Vector3.UnitZ));
            vm.Viewport.DragTo(new Vector3(center.X + 2.5f, center.Y - 1.5f, top + 10), -Vector3.UnitZ);
            vm.Viewport.EndDrag();

            Assert.Equal(2.5f, vm.Project.Current.Models[0].Offset.X, 4);
            Assert.Equal(-1.5f, vm.Project.Current.Models[0].Offset.Y, 4);
            Assert.Equal(0f, vm.Project.Current.Models[0].Offset.Z);
            Assert.True(vm.Project.IsDirty);
            Assert.Contains(nameof(ModelsViewModel.OffsetX), raised);
            Assert.Contains(nameof(ModelsViewModel.OffsetY), raised);
            Assert.Equal(before.Min.X + 2.5f, vm.Viewport.Meshes[0].Bounds.Min.X, 3);
            Assert.Equal(before.Min.Y - 1.5f, vm.Viewport.Meshes[0].Bounds.Min.Y, 3);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void LeftDragOnTheModel_MovesIt_ByThePlaneDeltaOfThePickRays()
    {
        var viewModel = new ViewportViewModel();
        var viewport = new Viewport3DControl { ViewModel = viewModel, Width = Width, Height = Height };
        var window = new Window { Width = Width, Height = Height, Content = viewport };
        window.Show();
        window.UpdateLayout();
        try
        {
            viewModel.SetMeshes(new[] { TestMeshes.Box(10, 10, 5) });
            var camera = viewport.Camera;
            camera.Target = viewModel.MeshBounds.Center;
            camera.Distance = 40;
            camera.Aspect = (float)(Width / Height);
            var deltas = new List<Vector2>();
            viewModel.ModelDragged += (_, d) => deltas.Add(d);

            var start = new Point(400, 300);
            var end = new Point(440, 300);
            window.MouseDown(start, MouseButton.Left);
            Assert.Equal(0, viewModel.SelectedModelIndex);
            Assert.True(viewModel.IsDragging);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.False(viewModel.IsDragging);

            // Expected: both rays on the plane through the first hit point (the box top, z 5).
            var (o1, d1) = camera.PickRay((float)start.X, (float)start.Y, (float)Width, (float)Height);
            var (o2, d2) = camera.PickRay((float)end.X, (float)end.Y, (float)Width, (float)Height);
            var hit = viewModel.MeshBounds.IntersectRay(o1, d1)!.Value;
            var z = (o1 + d1 * hit).Z;
            Assert.True(ViewportViewModel.PlanePoint(o1, d1, z, out var p1));
            Assert.True(ViewportViewModel.PlanePoint(o2, d2, z, out var p2));
            var total = deltas.Aggregate(Vector2.Zero, (sum, d) => sum + d);
            Assert.Equal(p2.X - p1.X, total.X, 3);
            Assert.Equal(p2.Y - p1.Y, total.Y, 3);
            Assert.True(total.Length() > 1f);
            Assert.Equal(viewModel.MeshBounds.Center, camera.Target);
        }
        finally
        {
            window.Close();
        }
    }
}
