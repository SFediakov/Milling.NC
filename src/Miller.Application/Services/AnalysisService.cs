using Miller.Core.Analysis;
using Miller.Core.Simulation;

namespace Miller.Application.Services;

// Runs its own engine to the end on a fresh stock clone and classifies the result; independent of
// the interactive simulation, so both can exist at the same time.
public sealed class AnalysisService
{
    public Task<AnalysisResult> AnalyzeAsync(PipelineResult result, CancellationToken cancellation)
        => Task.Run(() => Analyze(result, cancellation), cancellation);

    public Task<UncuttableResult> UncuttableAsync(PipelineResult result, CancellationToken cancellation)
        => Task.Run(() => Uncuttable(result), cancellation);

    public AnalysisResult Analyze(PipelineResult result, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(result);
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        engine.RunToEnd(cancellation);
        return FinalModelAnalyzer.Analyze(stock, result.Model, result.Floor, result.Tolerance);
    }

    public UncuttableResult Uncuttable(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return UncuttableRegions.Compute(result.MachineMesh, result.Model, result.EffectiveTip, result.HeadLimitedMask, result.Profile, result.Floor, result.Tolerance);
    }
}
