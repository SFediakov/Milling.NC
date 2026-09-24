using System.Globalization;
using System.Text;
using Miller.Application.Progress;
using Miller.Core.Simulation;

namespace Miller.Application.Services;

// Report is null exactly when the check failed; Error then carries the reason.
public sealed record CollisionCheck(CollisionReport? Report, string? Error)
{
    public bool Succeeded => Report is not null;
}

// The gate of the collision check cluster: runs the generated toolpath once over a clone of the
// pipeline stock and records every head and rapid collision with the cells they entered
// (CollisionRecorder, the rule of the simulation panel). A failure inside the cluster comes back as a
// check with an error, never as an exception, so the generated toolpath stays usable; cancellation
// still ends the call.
public sealed class CollisionService
{
    public const string StageName = "collision check";
    public const int MaxListedEvents = 5;
    public const string NoCollisionsText = "No collisions: the head and the rapid moves stay clear of the stock and the model.";

    public Task<CollisionCheck> RunAsync(PipelineResult result, IProgress<ProgressReport>? progress, CancellationToken cancellation)
        => Task.Run(() => Run(result, progress, cancellation), cancellation);

    public CollisionCheck Run(PipelineResult result, IProgress<ProgressReport>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var recorder = new CollisionRecorder(result.Profile, result.Profile.Tool.CutterLength, result.Model, result.Floor);
            var engine = new SimulationEngine(result.Toolpath, result.Stock.Map.Clone(), result.Profile);
            engine.Sampled += sample => recorder.Record(engine.Stock, sample);
            var reporter = new FractionReporter(progress);
            reporter.Report(0f);
            engine.RunToEnd(cancellation, reporter);
            reporter.Report(1f);
            return new CollisionCheck(recorder.Report(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CollisionCheck(null, ex.Message);
        }
    }

    // Text of the summary window: the counts per kind, the model split and the first events.
    public static string Summarize(CollisionCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Report is not { } report)
        {
            return $"The collision check failed: {check.Error}\nThe toolpath is kept; the analysis shows no collision marks.";
        }

        if (report.Events.Count == 0)
        {
            return NoCollisionsText;
        }

        var text = new StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Collisions in {report.Segments} segments of the toolpath.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Head touches material: {report.Count(SimulationEventKind.HeadCollision)} segments.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Rapid move into material: {report.Count(SimulationEventKind.RapidIntoMaterial)} segments.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Touching the model: {report.ModelSegments} segments, {report.Cells(CollisionContact.Model)} cells.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Stock only: {report.Cells(CollisionContact.Stock)} cells.\n"));
        text.Append("The Analysis tab marks the cells.\n\nFirst collisions:\n");
        foreach (var e in report.Events.Take(MaxListedEvents))
        {
            text.Append(e.Message).Append('\n');
        }

        if (report.Events.Count > MaxListedEvents)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"... and {report.Events.Count - MaxListedEvents} more.\n"));
        }

        return text.ToString().TrimEnd('\n');
    }

    // Short form for the status bar.
    public static string StatusSuffix(CollisionCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Report is { } report
            ? string.Create(CultureInfo.InvariantCulture, $"collisions: {report.Events.Count}")
            : "collision check failed";
    }

    // Forwards the engine fraction as the stage report once the bar has moved by
    // PipelineService.MinVisibleDelta, so a per-segment producer does not flood the UI thread.
    private sealed class FractionReporter : IProgress<float>
    {
        private readonly IProgress<ProgressReport>? _progress;
        private float _last = -1f;

        public FractionReporter(IProgress<ProgressReport>? progress) => _progress = progress;

        public void Report(float value)
        {
            if (_progress is null || (value - _last < PipelineService.MinVisibleDelta && !(value >= 1f && _last < 1f)))
            {
                return;
            }

            _last = value;
            _progress.Report(new ProgressReport(StageName, value,
                string.Create(CultureInfo.InvariantCulture, $"{StageName}: {(int)MathF.Round(value * 100f, MidpointRounding.AwayFromZero)}%")));
        }
    }
}
