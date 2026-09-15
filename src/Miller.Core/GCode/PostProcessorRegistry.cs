using Miller.Core.Toolpaths;

namespace Miller.Core.GCode;

// Explicit list of every post-processor. Adding one = a new file in GCode plus one entry here.
public static class PostProcessorRegistry
{
    public static IReadOnlyList<IPostProcessor> All { get; } = RegistryRules.Validate(
        new IPostProcessor[]
        {
            new GrblPostProcessor(),
        },
        p => p.Id,
        p => p.DisplayName,
        "post-processor");

    public static IPostProcessor GetById(string id) => RegistryRules.FindById(All, p => p.Id, id, "post-processor");
}
