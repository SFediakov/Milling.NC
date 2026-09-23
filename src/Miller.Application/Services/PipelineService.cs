using System.Globalization;
using Miller.Application.Progress;
using Miller.Application.Validation;
using Miller.Core.Generation;
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

    // Stock the collision check found in the way of the head and turned from may-cut into must-cut.
    public bool[,] ShouldCut { get; init; } = new bool[0, 0];
}

// Runs the stages of docs/ARCHITECTURE.md 5.1 in order: the validation here, every later stage in one
// call of the native library (ToolpathGeneration); the routed toolpath is simplified to vectors
// within the tolerance before the statistics. Progress fractions are cumulative over the
// stage weights below; the message names the stage and, for the stages with an inner loop, the
// unit and number of the iteration and the percent of the stage. Cancellation is honoured between
// stages and inside the reach map, the head clearance and the strategy.
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

    // Head limit iterations rarely need more than two rounds; the cap keeps a pathological grid finite
    // (the native pipeline uses the same number).
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

        StrategyRegistry.GetById(project.RoutingStrategyId);
        cancellation.ThrowIfCancellationRequested();

        // Every stage after the validation runs in the native library; its reports carry the stage.
        var generated = ToolpathGeneration.Run(project, meshes, (stage, step) => reporter.Report(stage, step), cancellation);
        reporter.Done();

        return new PipelineResult(generated.MachineMesh, generated.Stock, generated.Model, generated.Tip, generated.EffectiveTip, generated.HeadLimit, generated.HeadLimitedMask, generated.Plan, generated.Toolpath, generated.Statistics, generated.Profile, generated.Floor, project.Parameters.Tolerance)
        {
            Parameters = project.Parameters,
            Standing = generated.Standing,
            ShouldCut = generated.ShouldCut,
        };
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
    }
}
