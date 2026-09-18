using System.Globalization;
using Miller.Application.Progress;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.Application.Services;

// Everything the pipeline produced; the viewport, the simulation and the analysis read from it.
// Tip is the reach floor (ReachMap), EffectiveTip the same under the head limit.
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

    // What stands after the levels besides the model (cut scope): the stock surface over cells never
    // cut, the terrace level over trench cells, NaN where only the model constrains the tool.
    public HeightMap Standing { get; init; } = new(0, 0, 1, 1, 1, float.NaN);
}

// Runs the stages of docs/ARCHITECTURE.md 5.1 in order; the routed toolpath is simplified to vectors
// within the tolerance before the statistics. Progress fractions are cumulative over the
// stage weights below; the message names the stage and, for the stages with an inner loop, the
// unit and number of the iteration and the percent of the stage. Cancellation is honoured between
// stages and inside the strategy.
public sealed class PipelineService
{
    private static readonly (string Stage, float Weight, string Unit)[] Stages =
    {
        ("validate", 0.02f, ""),
        ("transform", 0.03f, ""),
        ("stock", 0.05f, ""),
        ("model map", 0.15f, ""),
        ("reach map", 0.15f, "round"),
        ("head clearance", 0.10f, "iteration"),
        ("slice", 0.05f, ""),
        ("route", 0.38f, "pass"),
        ("simplify", 0.02f, ""),
        ("statistics", 0.05f, ""),
    };

    // A report that repeats the last message is forwarded only once the bar has moved by this much
    // (one pixel of a 200 px bar), so a row-by-row producer does not flood the UI thread.
    public const float MinVisibleDelta = 0.005f;

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

        var strategy = StrategyRegistry.GetById(project.RoutingStrategyId);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(1);
        var machineMesh = ModelLayout.MergeMachineMeshes(project, meshes);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(2);
        var p = project.Parameters;
        // The stock is aligned to the models before their offsets, not to the merged mesh, so an
        // offset model sits where the viewport shows it and may hang outside the stock.
        var stock = StockModel.Create(project.Stock, ModelLayout.AnchorBoundsMachine(project, meshes.Select(m => m.Bounds).ToList()), p.CellSize);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(3);
        var floor = stock.StockBottom;
        var model = MeshRasterizer.CreateGridFor(stock.Bounds, p.CellSize, floor);
        MeshRasterizer.Rasterize(machineMesh, model, floor);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(4);
        var profile = ToolProfile.Create(project.Tool, p.CellSize);
        var tip = ReachMap.Compute(model, stock.Map, profile, floor, project.ReachPercent, p.Tolerance, reporter.StageProgress(4));
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(5);
        // The head must clear what the cutter leaves, not only the model: the limit comes from the
        // remaining material (closing of the tip map), rounded up to the level it stands at
        // until the pass that reaches it, since neighbours are cut level by level. A raised tip leaves
        // more, so iterate until the effective tip settles; limits only rise, so the loop is bounded.
        // The stock a narrower cut scope leaves standing is not part of this: the separation region
        // terraces its trench so the head clears that stock by construction, and feeding it back here
        // would widen the model region without end.
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
            reporter.Report(5, new StepProgress(iteration, MaxHeadIterations, (float)iteration / MaxHeadIterations));
            if (settled || iteration >= MaxHeadIterations)
            {
                break;
            }
        }

        var headLimited = HeadClearance.HeadLimitedMask(tip, limit, p.Tolerance);

        reporter.Begin(6);
        var sliced = Slicer.Build(effective, stock.Map, p);
        var scoped = project.CutScope switch
        {
            CutScope.Everything => SeparationRegion.Everything(sliced, effective),
            CutScope.Separation => SeparationRegion.Build(sliced, effective, stock.Map, project.Tool, p, stock.StockTop, floor, project.MinIslandVolume),
            _ => throw new ArgumentException($"Unknown cut scope {project.CutScope}.", nameof(project)),
        };
        var plan = scoped.Plan;
        // Strategies stay above the standing stock as well as above the model.
        var strategyTip = HeadClearance.ApplyHeadLimit(effective, scoped.Standing);
        var context = new ToolpathContext(model, tip, strategyTip, limit, stock.Map, plan, project.Tool, profile, p, stock.StockTop);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(7);
        var routed = strategy.Generate(context, reporter.StageProgress(7), cancellation);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(8);
        var toolpath = ToolpathSimplifier.Simplify(routed, strategyTip, p.Tolerance);
        cancellation.ThrowIfCancellationRequested();

        reporter.Begin(9);
        var statistics = ToolpathStatistics.Compute(toolpath, p);
        reporter.Done();

        return new PipelineResult(machineMesh, stock, model, tip, effective, limit, headLimited, plan, toolpath, statistics, profile, floor, p.Tolerance)
        {
            Parameters = p,
            Standing = scoped.Standing,
        };
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

    // Turns the stage table and the producers' StepProgress into ProgressReports: the fraction is the
    // stage start plus its weight times the stage fraction, the message the stage name followed by
    // "unit step of steps, percent%" when the stage has an inner loop.
    private sealed class StageReporter
    {
        private readonly IProgress<ProgressReport>? _progress;
        private readonly float[] _starts;
        private string _lastMessage = string.Empty;
        private float _lastFraction = -1f;

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

        public void Begin(int stage) => Report(stage, new StepProgress(0, 0, 0f));

        public void Done() => Forward(new ProgressReport("done", 1f, "Toolpath ready"));

        public IProgress<StepProgress> StageProgress(int stage) => new Forwarder(step => Report(stage, step));

        public void Report(int stage, StepProgress step)
        {
            var (name, weight, unit) = Stages[stage];
            var fraction = Math.Clamp(step.Fraction, 0f, 1f);
            var message = step.Steps == 0
                ? name
                : string.Create(CultureInfo.InvariantCulture, $"{name}: {unit} {step.Step} of {step.Steps}, {(int)MathF.Round(fraction * 100f, MidpointRounding.AwayFromZero)}%");
            Forward(new ProgressReport(name, _starts[stage] + weight * fraction, message));
        }

        private void Forward(ProgressReport report)
        {
            if (report.Message == _lastMessage && report.Fraction - _lastFraction < MinVisibleDelta)
            {
                return;
            }

            _lastMessage = report.Message;
            _lastFraction = report.Fraction;
            _progress?.Report(report);
        }

        private sealed class Forwarder : IProgress<StepProgress>
        {
            private readonly Action<StepProgress> _report;

            public Forwarder(Action<StepProgress> report) => _report = report;

            public void Report(StepProgress value) => _report(value);
        }
    }
}
