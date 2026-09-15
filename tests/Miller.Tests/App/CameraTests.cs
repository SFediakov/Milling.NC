using System.Numerics;
using Miller.App.Rendering;
using Miller.Core.Geometry;
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

    [Fact]
    public void FitToBounds_RejectsEmptyBounds()
    {
        Assert.Throws<ArgumentException>(() => new Camera().FitToBounds(BoundingBox.Empty));
    }
}
