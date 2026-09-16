namespace Miller.Core.Toolpaths;

// The exchangeable routing algorithm. Implementations live in Toolpath/Strategies and are listed in
// StrategyRegistry. A strategy returns the whole program from the first plunge to the last retract,
// and the result must pass GougeChecker against the effective tip map.
public interface IToolpathStrategy
{
    // Lowercase letters, digits and hyphens; unique across the registry.
    string Id { get; }

    string DisplayName { get; }

    Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation);
}
