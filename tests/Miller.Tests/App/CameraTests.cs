using System.Diagnostics;
using System.Numerics;
using Miller.App.Rendering;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Xunit;

namespace Miller.Tests.App;

public sealed class CameraTests
{
    private static readonly BoundingBox Part = new(new Vector3(3.259f, 0.477f, 0f), new Vector3(23.741f, 5.477f, 21.971f));

    private static IEnumerable<Vector3> Corners(BoundingBox b)
    {
        for (var i = 0; i < 8; i++)
        {
            yield return new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.78f)]
    [InlineData(0.6f)]
    public void FitToBounds_ProjectsEveryCornerInsideTheClipVolume(float aspect)
    {
        var camera = new Camera { Aspect = aspect };
        camera.FitToBounds(Part);
        Assert.Equal(Part.Center, camera.Target);
        foreach (var corner in Corners(Part))
        {
            var clip = camera.ToClip(corner);
            Assert.True(clip.W > 0, $"corner {corner} is behind the camera");
            Assert.InRange(clip.X / clip.W, -1f, 1f);
            Assert.InRange(clip.Y / clip.W, -1f, 1f);
            Assert.InRange(clip.Z / clip.W, -1f, 1f);
        }
    }

    [Fact]
    public void Projection_MapsNearToMinusOneAndFarToPlusOne()
    {
        var camera = new Camera { Distance = 100f, Aspect = 1.5f };
        var projection = camera.Projection;
        var near = Vector4.Transform(new Vector4(0, 0, -camera.Near, 1), projection);
        var far = Vector4.Transform(new Vector4(0, 0, -camera.Far, 1), projection);
        Assert.Equal(-1f, near.Z / near.W, 4);
        Assert.Equal(1f, far.Z / far.W, 4);
        var side = Vector4.Transform(new Vector4(1, 1, -10, 1), projection);
        Assert.Equal(1.5f, (side.Y / side.W) / (side.X / side.W), 3);
    }

    [Fact]
    public void Pitch_IsClamped_AndZoomMultipliesDistance()
    {
        var camera = new Camera { Pitch = 200f };
        Assert.Equal(Camera.MaxPitch, camera.Pitch);
        camera.Pitch = -200f;
        Assert.Equal(Camera.MinPitch, camera.Pitch);

        camera.Distance = 100f;
        camera.Zoom(1.5f);
        Assert.Equal(150f, camera.Distance, 4);
        camera.Zoom(1e-9f);
        Assert.Equal(Camera.MinDistance, camera.Distance);
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.Zoom(0f));
    }

    [Fact]
    public void Orbit_ChangesAngles_AndPan_MovesTheTargetAcrossTheView()
    {
        var camera = new Camera { Yaw = 10f, Pitch = 20f, Distance = 50f, Target = Vector3.Zero };
        camera.Orbit(15f, -5f);
        Assert.Equal(25f, camera.Yaw, 4);
        Assert.Equal(15f, camera.Pitch, 4);
        Assert.Equal(50f, Vector3.Distance(camera.Position, camera.Target), 3);

        var before = camera.Target;
        camera.Pan(3f, 2f);
        var delta = camera.Target - before;
        Assert.Equal(MathF.Sqrt(13f), delta.Length(), 3);
        Assert.Equal(0f, Vector3.Dot(delta, camera.Direction), 3);
        Assert.Equal(50f, camera.Distance, 4);
    }

    // Milling_Heart_V2.STL in machine space with the default 100 x 100 x 30 stock (from the app log).
    private static readonly BoundingBox HeartInStock = new(new Vector3(39.75878f, 47.5f, 8.029436f), new Vector3(60.24122f, 52.5f, 30f));

    [Fact]
    public void FitToBounds_FixtureFillsTheViewWithoutClipping()
    {
        var camera = new Camera { Aspect = 1400f / 800f };
        camera.FitToBounds(HeartInStock);
        var minX = 1f; var maxX = -1f; var minY = 1f; var maxY = -1f;
        foreach (var corner in Corners(HeartInStock))
        {
            var clip = camera.ToClip(corner);
            Assert.True(clip.W > 0);
            var x = clip.X / clip.W;
            var y = clip.Y / clip.W;
            Assert.InRange(x, -1f, 1f);
            Assert.InRange(y, -1f, 1f);
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
        }

        // The fitted part spans at least half of the narrower clip axis.
        Assert.True(MathF.Max(maxX - minX, maxY - minY) >= 1f, $"projected extent {maxX - minX} x {maxY - minY}");
    }

    // T-085: the CPU side of a stock upload. The fixture stock at cell size 0.1 (300 x 150 with margin)
    // and 0.05 (600 x 300) must build far below one frame; the interactive limit of 1,000,000 cells
    // builds within a second on the development machine (the GPU draws 2 triangles per cell).
    [Theory]
    [InlineData(300, 150, 200)]
    [InlineData(600, 300, 500)]
    [InlineData(1000, 1000, 5000)]
    public void HeightMapBuild_StaysWithinTheInteractiveBudget(int width, int height, int budgetMilliseconds)
    {
        Assert.True(width * height <= HeightMapRenderer.MaxCellsForInteractiveFrame);
        var map = new HeightMap(0, 0, 0.1f, width, height, 30f);
        HeightMapRenderer.Build(map, 0f, Vector4.One);
        var watch = Stopwatch.StartNew();
        var (vertices, indices) = HeightMapRenderer.Build(map, 0f, Vector4.One);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < budgetMilliseconds, $"{width} x {height} took {watch.ElapsedMilliseconds} ms");
        Assert.Equal((width - 1) * (height - 1) * 6 + (2 * (width - 1) + 2 * (height - 1)) * 6, indices.Length);
        Assert.Equal((width * height + 2 * width + 2 * height - 4) * HeightMapRenderer.FloatsPerVertex, vertices.Length);
    }

    [Fact]
    public void FitToBounds_RejectsEmptyBounds()
    {
        Assert.Throws<ArgumentException>(() => new Camera().FitToBounds(BoundingBox.Empty));
    }
}
