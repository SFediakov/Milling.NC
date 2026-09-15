namespace Miller.App.ViewModels;

// Content of the About window: version and the vendored package families with their licenses.
public sealed class AboutViewModel
{
    public const string LicensesFile = "THIRD_PARTY_LICENSES.md";

    public static readonly IReadOnlyList<PackageInfo> Packages = new[]
    {
        new PackageInfo("Avalonia 12.1.2", "MIT"),
        new PackageInfo("CommunityToolkit.Mvvm 8.4.2", "MIT"),
        new PackageInfo("SkiaSharp and HarfBuzzSharp (via Avalonia)", "MIT"),
        new PackageInfo("xunit v3 3.2.2 (tests only)", "Apache 2.0"),
        new PackageInfo(".NET 10 runtime packs", "MIT"),
    };

    public AboutViewModel(string appVersion)
    {
        AppVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
    }

    public string AppVersion { get; }

    public string Title => $"{App.WindowTitle} {AppVersion}";

    public string Description => "Converts an STL model into G-code for a 3-axis CNC mill, with simulation and final-model preview.";

    public string LicensesNote => $"Full license texts: {LicensesFile} in the repository.";

    public IReadOnlyList<PackageInfo> PackageList => Packages;

    public sealed record PackageInfo(string Name, string License)
    {
        public string Line => $"{Name} - {License}";
    }
}
