using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Generation;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-174 to T-177 (user request): the collision mode Backwards_GPU runs the backward check's sweeps, the
// intended surface, the route floors and extra start tours on the discrete GPU (OpenCL). The ported
// processes give the CPU's bits; the tours are priced exactly beside the CPU's start walks. These tests
// need an OpenCL GPU and are skipped without one; the mode itself then fails validation.
public sealed class GpuTests
{
    private static void RequireGpu() => Assert.SkipWhen(!GpuDevice.Available, $"No OpenCL GPU: {GpuDevice.Description}");

    // Values with NaN cells, equal neighbours and negative heights, the same for every run.
    private static float[] Map(int cells, int seed, float nanShare)
    {
        var random = new Random(seed);
        var values = new float[cells];
        for (var k = 0; k < cells; k++)
        {
            values[k] = random.NextDouble() < nanShare ? float.NaN : MathF.Round((float)(random.NextDouble() * 20 - 5), random.Next(0, 3));
        }

        return values;
    }

    [Fact]
    public void TheClosing_GivesTheBitsOfTheCpu()
    {
        RequireGpu();
        var profile = ToolProfile.Create(new ToolDefinition { CutterDiameter = 3f, TipType = TipType.Ball }, 0.25f);
        var tip = new HeightMap(0f, 0f, 0.25f, 97, 61, float.NaN);
        var values = Map(tip.Z.Length, 7, 0.2f);
        Array.Copy(values, tip.Z, values.Length);
        var cpu = HeightMapDilation.ComputeRemaining(tip, profile);
        var gpu = GpuProcesses.Remaining(tip, profile);
        Assert.Equal(cpu.Z.Select(BitConverter.SingleToInt32Bits), gpu.Z.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void TheRouteFloor_GivesTheBitsOfTheCpu()
    {
        RequireGpu();
        const int cells = 5000;
        var stock = Map(cells, 1, 0.1f);
        var tip = Map(cells, 2, 0.1f);
        var floor = Map(cells, 3, 0.05f);
        for (var k = 0; k < cells; k += 7)
        {
            floor[k] = stock[k];
            tip[k] = stock[k];
        }

        var cpu = GpuProcesses.RouteFloor(stock, tip, floor, 12f);
        var gpu = GpuProcesses.GpuRouteFloor(stock, tip, floor, 12f);
        Assert.Equal(cpu.Select(BitConverter.SingleToInt32Bits), gpu.Select(BitConverter.SingleToInt32Bits));
        Assert.Contains(cpu.Take(cells), v => v == 12f);
    }

    // Every kept tour visits every node once, from the start, with the fixed end last, shortest first and
    // the same in every run; the plain nearest-neighbour walk (tour 0) is kept or beaten.
    [Fact]
    public void StartTours_AreTours_FromTheStartToTheEnd()
    {
        RequireGpu();
        const int n = 300;
        var random = new Random(11);
        var x = Enumerable.Range(0, n).Select(_ => (float)random.NextDouble() * 50).ToArray();
        var y = Enumerable.Range(0, n).Select(_ => (float)random.NextDouble() * 30).ToArray();
        float Length(IReadOnlyList<int> order)
        {
            var length = 0f;
            for (var k = 1; k < order.Count; k++)
            {
                int a = order[k - 1], b = order[k];
                length += MathF.Sqrt((x[b] - x[a]) * (x[b] - x[a]) + (y[b] - y[a]) * (y[b] - y[a]));
            }

            return length;
        }

        var tours = GpuProcesses.StartTours(x, y, 5, 17, 64, 4);
        Assert.Equal(4, tours.Count);
        Assert.Equal(tours, GpuProcesses.StartTours(x, y, 5, 17, 64, 4));
        for (var t = 0; t < tours.Count; t++)
        {
            Assert.Equal(Enumerable.Range(0, n), tours[t].OrderBy(v => v));
            Assert.Equal(5, tours[t][0]);
            Assert.Equal(17, tours[t][^1]);
            Assert.True(t == 0 || Length(tours[t - 1]) <= Length(tours[t]), $"tour {t} is shorter than tour {t - 1}");
        }

        var nearest = new List<int> { 5 };
        var left = Enumerable.Range(0, n).Where(k => k != 5).ToHashSet();
        while (left.Count > 0)
        {
            var from = nearest[^1];
            var next = left.Where(k => k != 17 || left.Count == 1).OrderBy(k => MathF.Sqrt((x[k] - x[from]) * (x[k] - x[from]) + (y[k] - y[from]) * (y[k] - y[from]))).ThenBy(k => k).First();
            nearest.Add(next);
            left.Remove(next);
        }

        Assert.True(tours.Any(t => t.SequenceEqual(nearest)) || Length(tours[^1]) <= Length(nearest), "the plain nearest-neighbour tour is neither kept nor beaten");
    }

    private static MillingProject Frustum(CollisionMode mode, float farStepdown)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterLength = 2f;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7f;
        project.Tool.HeadTopDiameter = 10f;
        project.Tool.HeadLength = 10f;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.Stepover = 1f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    // On the frustum box the GPU sweeps settle on the CPU's levels, the GPU tours are not cheaper than the
    // CPU's walks there, so the toolpath is the CPU's segment for segment.
    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void BackwardsGpu_GivesTheResultOfBackwards(float farStepdown)
    {
        RequireGpu();
        var mesh = new[] { TestMeshes.Box(10, 10, 4) };
        var cpu = new PipelineService().Run(Frustum(CollisionMode.Backwards, farStepdown), mesh, null, CancellationToken.None);
        var gpu = new PipelineService().Run(Frustum(CollisionMode.Backwards_GPU, farStepdown), mesh, null, CancellationToken.None);
        Assert.Equal(cpu.Unreached, gpu.Unreached);
        Assert.Equal(cpu.Uncut, gpu.Uncut);
        Assert.Equal(cpu.Toolpath.Segments, gpu.Toolpath.Segments);
        Assert.Empty(gpu.Collisions.Report!.Events);
        Assert.Equal(GpuDevice.Description, gpu.Gpu);
        Assert.Null(cpu.Gpu);
        Assert.True(gpu.Times.Seconds[ProcessTimes.Names.ToList().IndexOf("GPU start tours")] > 0, "the GPU tours must have run");
        Assert.Equal(0, cpu.Times.Seconds[ProcessTimes.Names.ToList().IndexOf("GPU start tours")]);
    }

    // Two generations at once on the GPU keep their own route floors (each holds its stock and tip).
    [Fact]
    public async Task TwoGpuGenerationsAtOnce_GiveTheirOwnResults()
    {
        RequireGpu();
        var box = new[] { TestMeshes.Box(10, 10, 4) };
        var wide = new[] { TestMeshes.Box(16, 6, 3) };
        var first = new PipelineService().Run(Frustum(CollisionMode.Backwards_GPU, 0f), box, null, CancellationToken.None);
        var second = new PipelineService().Run(Frustum(CollisionMode.Backwards_GPU, 4f), wide, null, CancellationToken.None);
        var both = await Task.WhenAll(
            Task.Run(() => new PipelineService().Run(Frustum(CollisionMode.Backwards_GPU, 0f), box, null, CancellationToken.None)),
            Task.Run(() => new PipelineService().Run(Frustum(CollisionMode.Backwards_GPU, 4f), wide, null, CancellationToken.None)));
        Assert.Equal(first.Toolpath.Segments, both[0].Toolpath.Segments);
        Assert.Equal(second.Toolpath.Segments, both[1].Toolpath.Segments);
    }

    [Fact]
    public void BackwardsGpu_NeedsTheZLayer_AndRoundTripsByName()
    {
        var project = Frustum(CollisionMode.Backwards_GPU, 0f);
        Assert.Equal(GpuDevice.Available, !ProjectValidator.Validate(project, null).Errors.Any(e => e.Field == "Strategy.CollisionMode"));
        project.RoutingStrategyId = ThreeAxisFreedomStrategy.StrategyId;
        Assert.Contains(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Strategy.CollisionMode" && e.Message.Contains("Z layer", StringComparison.Ordinal));
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"CollisionMode\": \"Backwards_GPU\"", json);
        Assert.Equal(CollisionMode.Backwards_GPU, ProjectSerializer.Deserialize(json).CollisionMode);
    }
}
