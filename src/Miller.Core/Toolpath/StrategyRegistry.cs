using System.Text.RegularExpressions;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths.Strategies;

namespace Miller.Core.Toolpaths;

// Explicit list of every strategy. Adding one = a new file in Strategies plus one entry here.
public static class StrategyRegistry
{
    public static IReadOnlyList<IToolpathStrategy> All { get; } = RegistryRules.Validate(
        new IToolpathStrategy[]
        {
            new RasterRoughingStrategy(),
            new RasterFinishingStrategy(),
            new ContourFinishingStrategy(),
        },
        s => s.Id,
        s => s.DisplayName,
        "strategy");

    public static IToolpathStrategy GetById(string id) => RegistryRules.FindById(All, s => s.Id, id, "strategy");

    public static IEnumerable<IToolpathStrategy> ForOperation(MillingOperation operation)
        => All.Where(s => s.Operation == operation);
}

// Shared rules for the id-keyed registries (strategies, post-processors).
public static partial class RegistryRules
{
    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // Returns the same list when every id is valid and unique and every display name is non-empty;
    // throws InvalidOperationException otherwise (at type initialization for the static registries).
    public static IReadOnlyList<T> Validate<T>(IReadOnlyList<T> items, Func<T, string> id, Func<T, string> displayName, string kind)
    {
        ArgumentNullException.ThrowIfNull(items);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemId = id(item);
            if (!IsValidId(itemId))
            {
                throw new InvalidOperationException($"Invalid {kind} id '{itemId}': use lowercase letters, digits and hyphens, starting with a letter.");
            }

            if (!seen.Add(itemId))
            {
                throw new InvalidOperationException($"Duplicate {kind} id '{itemId}'.");
            }

            if (string.IsNullOrWhiteSpace(displayName(item)))
            {
                throw new InvalidOperationException($"{kind} '{itemId}' has an empty display name.");
            }
        }

        return items;
    }

    public static T FindById<T>(IReadOnlyList<T> items, Func<T, string> id, string wanted, string kind)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        foreach (var item in items)
        {
            if (string.Equals(id(item), wanted, StringComparison.Ordinal))
            {
                return item;
            }
        }

        var known = items.Count == 0 ? "none registered" : string.Join(", ", items.Select(id));
        throw new KeyNotFoundException($"Unknown {kind} id '{wanted}'. Known ids: {known}.");
    }
}
