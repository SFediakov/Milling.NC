using System.Text.RegularExpressions;
using Xunit;

namespace Miller.Tests.App;

// Root rule: colors are declared in exactly one file. Every other source file under src/ must be free
// of color literals and of programmatic color construction.
public sealed partial class ColorRuleTests
{
    private const string AllowedFile = "Styles/Colors.axaml";

    [GeneratedRegex(@"#[0-9A-Fa-f]{6,8}\b|Color\.Parse\(|Color\.FromRgb\(|Color\.FromArgb\(|Colors\.[A-Z][a-zA-Z]+\b")]
    private static partial Regex ColorLiteral();

    private static string SourceRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));

    public static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(SourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void OnlyColorsAxaml_ContainsColorLiterals()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var relative = Path.GetRelativePath(SourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.EndsWith(AllowedFile, StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var n = 0; n < lines.Length; n++)
            {
                if (ColorLiteral().IsMatch(lines[n]))
                {
                    offenders.Add($"{relative}:{n + 1}: {lines[n].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "color literals outside Styles/Colors.axaml:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ColorsAxaml_ExistsAndDefinesTheExpectedResources()
    {
        var path = Path.Combine(SourceRoot, "Miller.App", "Styles", "Colors.axaml");
        Assert.True(File.Exists(path), path);
        var text = File.ReadAllText(path);
        foreach (var name in new[]
        {
            "WindowBackground", "PanelBackground", "Text", "Accent", "Error", "Warning", "ViewportBackground", "Model", "Stock",
            "ToolpathRapid", "ToolpathFeed", "ToolpathPlunge", "ToolCutter", "ToolHead", "CategoryOk", "CategoryRestMaterial",
            "CategoryGouge", "CategoryOverhang", "CategoryHeadLimited", "CategoryCornerLimited", "AxisX", "AxisY", "AxisZ",
        })
        {
            Assert.Contains($"x:Key=\"{name}Color\"", text);
            Assert.Contains($"x:Key=\"{name}Brush\"", text);
        }
    }
}
