using Miller.Core.Native;
using Miller.Core.Progress;

namespace Miller.Core.Toolpaths.Strategies;

// Runs a strategy of the native library over a context (mn_strategy_generate).
internal static class NativeStrategy
{
    public const int ZLayer = 0;

    public static unsafe Toolpath Generate(int strategy, ToolpathContext context, IProgress<StepProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var plan = new CoreNative.NativePlan(context.Plan, context.EffectiveTip);
        using var callback = new CoreNative.Progress(progress);
        using var cancel = new CoreNative.CancelFlag(cancellation);
        CoreNative.Segment* segments = null;
        int count;
        fixed (float* model = context.Model.Z, tip = context.Tip.Z, effective = context.EffectiveTip.Z, limit = context.HeadLimit.Z, stock = context.Stock.Z)
        {
            var native = new CoreNative.Context
            {
                Grid = CoreNative.GridOf(context.EffectiveTip),
                Model = model,
                Tip = tip,
                EffectiveTip = effective,
                HeadLimit = limit,
                Stock = stock,
                Plan = plan.Handle,
                Tool = CoreNative.ToolOf(context.Tool),
                Parameters = CoreNative.ParametersOf(context.Parameters),
                StockTop = context.StockTop,
                Floor = context.Floor,
                CollisionMode = (int)context.CollisionMode,
                FarAllowed = null,
                FarArea = null,
            };
            CoreNative.Check(CoreNative.mn_strategy_generate(strategy, &native, callback.Pointer, IntPtr.Zero, cancel.Pointer, &segments, &count), cancellation);
        }

        try
        {
            return CoreNative.ToolpathOf(segments, count);
        }
        finally
        {
            CoreNative.mn_free(segments);
        }
    }
}
