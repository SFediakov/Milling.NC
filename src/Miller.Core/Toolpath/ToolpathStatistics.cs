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

    // Minutes = sum of length / rate. Feed and plunge segments carry their rate; rapids use the
    // machine's rapid rate from the parameters because G0 has no F word.
    public static ToolpathStatistics Compute(Toolpath toolpath, CuttingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!(parameters.RapidRate > 0))
        {
            throw new ArgumentException($"Rapid rate must be positive, got {parameters.RapidRate}.", nameof(parameters));
        }

        float rapid = 0, feed = 0, plunge = 0;
        double minutes = 0;
        var retracts = 0;
        foreach (var s in toolpath.Segments)
        {
            var length = s.Length;
            switch (s.Kind)
            {
                case MoveKind.Rapid:
                    rapid += length;
                    minutes += length / parameters.RapidRate;
                    if (s.End.Z > s.Start.Z)
                    {
                        retracts++;
                    }

                    break;
                case MoveKind.Feed:
                    feed += length;
                    minutes += length / s.FeedRate;
                    break;
                case MoveKind.Plunge:
                    plunge += length;
                    minutes += length / s.FeedRate;
                    break;
                default:
                    throw new ArgumentException($"Unknown move kind {s.Kind}.", nameof(toolpath));
            }
        }

        return new ToolpathStatistics(rapid, feed, plunge, toolpath.Count, (float)minutes, retracts);
    }
}
