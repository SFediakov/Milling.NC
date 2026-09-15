using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class StrategyRegistryTests
{
    private sealed record FakeStrategy(string Id, string DisplayName, MillingOperation Operation) : IToolpathStrategy
    {
        public Toolpath Generate(ToolpathContext context, IProgress<float>? progress, CancellationToken cancellation)
            => new();
    }

    [Fact]
    public void RegisteredStrategies_HaveUniqueValidIdsAndDisplayNames()
    {
        var ids = StrategyRegistry.All.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.True(RegistryRules.IsValidId(id), id));
        Assert.All(StrategyRegistry.All, s => Assert.False(string.IsNullOrWhiteSpace(s.DisplayName)));
        Assert.All(StrategyRegistry.All, s => Assert.Same(s, StrategyRegistry.GetById(s.Id)));
        Assert.Equal(StrategyRegistry.All.Count,
            StrategyRegistry.ForOperation(MillingOperation.Roughing).Count() + StrategyRegistry.ForOperation(MillingOperation.Finishing).Count());
    }

    [Fact]
    public void GetById_UnknownId_ThrowsNamingTheKnownIds()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => StrategyRegistry.GetById("missing"));
        Assert.Contains("missing", ex.Message);
        Assert.Contains(StrategyRegistry.All.Count == 0 ? "none registered" : string.Join(", ", StrategyRegistry.All.Select(s => s.Id)), ex.Message);
    }

    [Theory]
    [InlineData("raster-roughing", true)]
    [InlineData("a", true)]
    [InlineData("contour2", true)]
    [InlineData("Raster", false)]
    [InlineData("raster_roughing", false)]
    [InlineData("-raster", false)]
    [InlineData("1raster", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IdRule_AcceptsLowercaseHyphenOnly(string? id, bool valid)
    {
        Assert.Equal(valid, RegistryRules.IsValidId(id));
    }

    [Fact]
    public void Validate_RejectsDuplicatesMalformedIdsAndEmptyNames()
    {
        var good = new IToolpathStrategy[]
        {
            new FakeStrategy("one", "One", MillingOperation.Roughing),
            new FakeStrategy("two-b", "Two", MillingOperation.Finishing),
        };
        Assert.Same(good, RegistryRules.Validate(good, s => s.Id, s => s.DisplayName, "strategy"));

        var duplicate = new IToolpathStrategy[] { good[0], new FakeStrategy("one", "Again", MillingOperation.Finishing) };
        Assert.Contains("Duplicate", Assert.Throws<InvalidOperationException>(() => RegistryRules.Validate(duplicate, s => s.Id, s => s.DisplayName, "strategy")).Message);

        var malformed = new IToolpathStrategy[] { new FakeStrategy("Bad Id", "Bad", MillingOperation.Roughing) };
        Assert.Contains("Invalid", Assert.Throws<InvalidOperationException>(() => RegistryRules.Validate(malformed, s => s.Id, s => s.DisplayName, "strategy")).Message);

        var unnamed = new IToolpathStrategy[] { new FakeStrategy("ok", " ", MillingOperation.Roughing) };
        Assert.Contains("display name", Assert.Throws<InvalidOperationException>(() => RegistryRules.Validate(unnamed, s => s.Id, s => s.DisplayName, "strategy")).Message);
    }

    [Fact]
    public void FindById_ReturnsTheMatchOrListsTheKnownIds()
    {
        var items = new IToolpathStrategy[]
        {
            new FakeStrategy("one", "One", MillingOperation.Roughing),
            new FakeStrategy("two", "Two", MillingOperation.Finishing),
        };
        Assert.Same(items[1], RegistryRules.FindById(items, s => s.Id, "two", "strategy"));
        var ex = Assert.Throws<KeyNotFoundException>(() => RegistryRules.FindById(items, s => s.Id, "three", "strategy"));
        Assert.Contains("one, two", ex.Message);
        Assert.Contains("none registered", Assert.Throws<KeyNotFoundException>(() => RegistryRules.FindById(Array.Empty<IToolpathStrategy>(), s => s.Id, "x", "strategy")).Message);
    }
}
