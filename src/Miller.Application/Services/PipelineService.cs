using Miller.Application.Progress;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.Application.Services;

// Everything the pipeline produced; the viewport, the simulation and the analysis read from it.
public sealed record PipelineResult(
    Mesh MachineMesh,
    StockGeometry Stock,
    HeightMap Model,
    HeightMap Tip,
    HeightMap EffectiveTip,
    HeightMap HeadLimit,
    bool[,] HeadLimitedMask,
    SlicePlan Plan,
    Toolpath Toolpath,
    ToolpathStatistics Statistics,
    ToolProfile Profile,
    float Floor,
    float Tolerance)
{
    public float SafeZ => Stock.StockTop + Parameters.SafeHeight;

    public CuttingParameters Parameters { get; init; } = CuttingParameters.Default();
}

// Runs the stages of docs/ARCHITECTURE.md 5.1 in order. Progress fractions are cumulative over the
// stage weights below; cancellation is honoured between stages and inside the strategies.
public sealed class PipelineService
{
    private static readonly (string Stage, float Weight)[] Stages =
    {
        ("validate", 0.02f),
        ("transform", 0.03f),
        ("stock", 0.05f),
        ("model map", 0.15f),
        ("tip map", 0.15f),
        ("head clearance", 0.10f),
        ("slice", 0.05f),
        ("roughing", 0.20f),
        ("finishing", 0.20f),
        ("statistics", 0.05f),
    };

    // Head limit iterations rarely need more than two rounds; the cap keeps a pathological grid finite.
    public const int MaxHeadIterations = 8;

    public Task<PipelineResult> RunAsync(MillingProject project, IReadOnlyList<Mesh> meshes, IProgress<ProgressReport>? progress, CancellationToken cancellation)
        => Task.Run(() => Run(project, meshes, progress, cancellation), cancellation);

    // One mesh per model placement of the project, in the same order.
    public PipelineResult Run(MillingProject project, IReadOnlyList<Mesh> meshes, IProgress<ProgressReport>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(meshes);
        if (meshes.Count == 0 || meshes.Count != project.Models.Count)
        {
            throw new ArgumentException($"{meshes.Count} meshes for {project.Models.Count} model placements.", nameof(meshes));
        }

        var reporter = new StageReporter(progress);

        reporter.Begin(0);
        var validation = ProjectValidator.Validate(project, null);
        if (!validation.IsValid)
        {
            throw new ValidationException(validation);
        }

        var roughing = RequireStrategy(project.RoughingStrategyId, MillingOperation.Roughing);
        var finishing = RequireStrategy(project.FinishingStrategyId, MillingOperation.Finishing);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(1);
        var machineMesh = ModelLayout.MergeMachineMeshes(project, meshes);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(2);
        var p = project.Parameters;
        var stock = StockModel.Create(project.Stock, machineMesh.Bounds, p.CellSize);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(3);
        var floor = stock.StockBottom;
        var model = MeshRasterizer.CreateGridFor(stock.Bounds, p.CellSize, floor);
        MeshRasterizer.Rasterize(machineMesh, model, floor);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(4);
        var profile = ToolProfile.Create(project.Tool, p.CellSize);
        var tip = HeightMapDilation.ComputeTipMap(model, profile);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(5);
        // The head must clear what the cutter leaves, not only the model: the limit comes from the
        // remaining material (closing of the tip map), rounded up to the roughing level it stands at
        // until the pass that reaches it, since neighbours are cut level by level. A raised tip leaves
        // more, so iterate until the effective tip settles; limits only rise, so the loop is bounded.
        var effective = tip;
        HeightMap limit;
        for (var iteration = 1; ; iteration++)
        {
            var remaining = Slicer.CeilToLevels(HeightMapDilation.ComputeRemaining(effective, profile), stock.StockTop, p.Stepdown);
            limit = HeadClearance.ComputeHeadLimit(remaining, profile, project.Tool.CutterLength);
            var next = HeadClearance.ApplyHeadLimit(tip, limit);
            var settled = SameWithin(next, effective, p.Tolerance);
            effective = next;
            cancellation.ThrowIfCancellationRequested();
            if (settled || iteration >= MaxHeadIterations)
            {
                break;
            }
        }

        var headLimited = HeadClearance.HeadLimitedMask(tip, limit, p.Tolerance);

        reporter.Begin(6);
        var plan = Slicer.Build(effective, stock.Map, p);
        var context = new ToolpathContext(model, tip, effective, limit, stock.Map, plan, project.Tool, profile, p, stock.StockTop);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(7);
        var roughingPath = roughing.Generate(context, reporter.StageProgress(7), cancellation);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(8);
        var finishingPath = finishing.Generate(context, reporter.StageProgress(8), cancellation);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(9);
        var toolpath = Join(roughingPath, finishingPath, p);
        var statistics = ToolpathStatistics.Compute(toolpath, p);
        reporter.Done();

        return new PipelineResult(machineMesh, stock, model, tip, effective, limit, headLimited, plan, toolpath, statistics, profile, floor, p.Tolerance)
        {
            Parameters = p,
        };
    }

    // Each strategy output already starts with a plunge from safe Z and ends with a retract to it, so
    // one rapid at safe Z connects them.
    public static Toolpath Join(Toolpath roughing, Toolpath finishing, CuttingParameters parameters)
    {
        var result = new Toolpath();
        result.AddRange(roughing.Segments);
        if (roughing.Count > 0 && finishing.Count > 0)
        {
            var from = roughing.Segments[^1].End;
            var to = finishing.Segments[0].Start;
            if (from != to)
            {
                result.Add(new ToolpathSegment(from, to, MoveKind.Rapid, parameters.RapidRate));
            }
        }

        result.AddRange(finishing.Segments);
        return result;
    }

    private static bool SameWithin(HeightMap a, HeightMap b, float tolerance)
    {
        for (var k = 0; k < a.Z.Length; k++)
        {
            var x = a.Z[k];
            var y = b.Z[k];
            if (float.IsNaN(x) != float.IsNaN(y) || (!float.IsNaN(x) && MathF.Abs(x - y) > tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static IToolpathStrategy RequireStrategy(string id, MillingOperation operation)
    {
        var strategy = StrategyRegistry.GetById(id);
        if (strategy.Operation != operation)
        {
            throw new ArgumentException($"Strategy '{id}' is a {strategy.Operation} strategy; a {operation} strategy is required.");
        }

        return strategy;
    }

    private sealed class StageReporter
    {
        private readonly IProgress<ProgressReport>? _progress;
        private readonly float[] _starts;

        public StageReporter(IProgress<ProgressReport>? progress)
        {
            _progress = progress;
            _starts = new float[Stages.Length];
            var sum = 0f;
            for (var k = 0; k < Stages.Length; k++)
            {
                _starts[k] = sum;
                sum += Stages[k].Weight;
            }
        }

        public void Begin(int stage) => Report(stage, 0f);

        public void Done() => _progress?.Report(new ProgressReport("done", 1f, "Toolpath ready"));

        public IProgress<float> StageProgress(int stage) => new Forwarder(f => Report(stage, f));

        private void Report(int stage, float fractionOfStage)
        {
            var (name, weight) = Stages[stage];
            _progress?.Report(new ProgressReport(name, _starts[stage] + weight * Math.Clamp(fractionOfStage, 0f, 1f), name));
        }

        private sealed class Forwarder : IProgress<float>
        {
            private readonly Action<float> _report;

            public Forwarder(Action<float> report) => _report = report;

            public void Report(float value) => _report(value);
        }
    }
}
