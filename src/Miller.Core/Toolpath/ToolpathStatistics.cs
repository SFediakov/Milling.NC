using Miller.Core.Native;
using Miller.Core.Setup;

namespace Miller.Core.Toolpaths;

public sealed record ToolpathStatistics(
    float RapidLength,
    float FeedLength,
    float PlungeLength,
    int SegmentCount,
    float EstimatedMinutes,
    int RetractCount)
{
    public float TotalLength => RapidLength + FeedLength + PlungeLength;

    // Minutes = sum of length / rate (native mn_statistics_compute). Feed and plunge segments carry
    // their rate; rapids use the machine's rapid rate from the parameters because G0 has no F word.
    public static unsafe ToolpathStatistics Compute(Toolpath toolpath, CuttingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!(parameters.RapidRate > 0))
        {
            throw new ArgumentException($"Rapid rate must be positive, got {parameters.RapidRate}.", nameof(parameters));
        }

        var segments = CoreNative.SegmentsOf(toolpath);
        CoreNative.Statistics statistics;
        fixed (CoreNative.Segment* s = segments)
        {
            CoreNative.Check(CoreNative.mn_statistics_compute(s, segments.Length, parameters.RapidRate, &statistics));
        }

        return Of(statistics);
    }

    internal static ToolpathStatistics Of(in CoreNative.Statistics s)
        => new(s.RapidLength, s.FeedLength, s.PlungeLength, s.SegmentCount, s.EstimatedMinutes, s.RetractCount);
}
