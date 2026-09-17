using System.Numerics;
using Miller.App.Rendering;
using Miller.App.ViewModels;
using Miller.Core.Geometry;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class PickingTests
{
    private static readonly BoundingBox Box = new(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));

    [Fact]
    public void Ray_HitsTheBoxFromOutside_AtTheNearFace()
    {
        var hit = Box.IntersectRay(new Vector3(-5, 0, 0), Vector3.UnitX);
        Assert.Equal(4f, hit!.Value, 5);
        Assert.Null(Box.IntersectRay(new Vector3(-5, 2, 0), Vector3.UnitX));
        Assert.Null(Box.IntersectRay(new Vector3(5, 0, 0), Vector3.UnitX));
        Assert.Null(BoundingBox.Empty.IntersectRay(Vector3.Zero, Vector3.UnitX));
    }

    [Fact]
    public void Ray_StartingInsideTheBox_HitsAtZero_AndParallelRaysOutsideMiss()
    {
        Assert.Equal(0f, Box.IntersectRay(Vector3.Zero, Vector3.UnitZ));
        Assert.Null(Box.IntersectRay(new Vector3(0, 3, 0), Vector3.UnitZ));
        var diagonal = Box.IntersectRay(new Vector3(-3, -3, -3), Vector3.Normalize(Vector3.One));
        Assert.Equal(MathF.Sqrt(3f) * 2f, diagonal!.Value, 4);
    }

    [Fact]
    public void PickRay_ThroughTheViewportCenter_PointsAtTheTarget()
    {
        var camera = new Camera { Aspect = 1.6f, Target = new Vector3(50, 40, 10), Distance = 80 };
        var (origin, direction) = camera.PickRay(800, 500, 1600, 1000);
        Assert.Equal(camera.Position, origin);
        Assert.Equal(1f, direction.Length(), 5);
        Assert.Equal(-1f, Vector3.Dot(direction, camera.Direction), 4);
        var closest = origin + direction * Vector3.Dot(camera.Target - origin, direction);
        Assert.True(Vector3.Distance(closest, camera.Target) < 1e-2f, $"ray passes {Vector3.Distance(closest, camera.Target)} from the target");
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.PickRay(1, 1, 0, 10));
    }

    [Fact]
    public void PickRay_ThroughACornerPixel_ProjectsBackToThatCorner()
    {
        var camera = new Camera { Aspect = 1.5f, Target = Vector3.Zero, Distance = 30 };
        var (origin, direction) = camera.PickRay(0, 0, 900, 600);
        var point = origin + direction * 40f;
        var clip = camera.ToClip(point);
        Assert.Equal(-1f, clip.X / clip.W, 3);
        Assert.Equal(1f, clip.Y / clip.W, 3);
    }

    [Fact]
    public void ViewModel_PickSelectsTheMeshAndAMissClearsIt()
    {
        var viewport = new ViewportViewModel();
        var changes = 0;
        viewport.SelectionChanged += (_, _) => changes++;
        viewport.Pick(new Vector3(0, 0, 50), -Vector3.UnitZ);
        Assert.Equal(-1, viewport.SelectedModelIndex);

        viewport.SetMeshes(new[] { TestMeshes.Box(10, 10, 5) });
        Assert.Single(viewport.ModelBounds);
        viewport.Pick(new Vector3(5, 5, 50), -Vector3.UnitZ);
        Assert.Equal(0, viewport.SelectedModelIndex);
        Assert.Equal(1, changes);

        viewport.Pick(new Vector3(50, 50, 50), -Vector3.UnitZ);
        Assert.Equal(-1, viewport.SelectedModelIndex);
        Assert.Equal(2, changes);

        viewport.Select(0);
        viewport.SetMeshes(Array.Empty<Mesh>());
        Assert.Equal(-1, viewport.SelectedModelIndex);
        Assert.Empty(viewport.ModelBounds);
    }
}
