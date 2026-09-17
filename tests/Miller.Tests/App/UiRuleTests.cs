using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Miller.Tests.App;

// Two rules over the views: no static description text beyond two words outside labels and headers
// (the panels carry fields, not help paragraphs), and no plain TextBox on a numeric view-model
// property (numbers go through NumericBox so typed text is never rewritten). The About window is
// the help itself and is exempt from the first rule.
public sealed partial class UiRuleTests
{
    public const int MaxDescriptionWords = 2;

    private static readonly string[] ExemptFromTextRule = { "AboutWindow.axaml" };

    [GeneratedRegex(@"<TextBlock\b[^>]*\bText=""(?<text>[^""{}]*)""[^>]*/?>")]
    private static partial Regex StaticTextBlock();

    [GeneratedRegex(@"Classes=""(?<classes>[^""]*)""")]
    private static partial Regex ClassesAttribute();

    [GeneratedRegex(@"x:DataType=""vm:(?<type>\w+)""")]
    private static partial Regex DataType();

    [GeneratedRegex(@"<TextBox\b[^>]*\bText=""\{Binding (?<path>[A-Za-z_][\w.]*)")]
    private static partial Regex BoundTextBox();

    private static string ViewsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Miller.App", "Views"));

    public static IEnumerable<string> ViewFiles() => Directory.EnumerateFiles(ViewsRoot, "*.axaml").OrderBy(f => f, StringComparer.Ordinal);

    [Fact]
    public void StaticTexts_AreLabelsHeadersOrAtMostTwoWords()
    {
        var offenders = new List<string>();
        foreach (var file in ViewFiles())
        {
            if (ExemptFromTextRule.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in StaticTextBlock().Matches(text))
            {
                var classes = ClassesAttribute().Match(match.Value);
                var kind = classes.Success ? classes.Groups["classes"].Value : string.Empty;
                if (kind.Contains("label", StringComparison.Ordinal) || kind.Contains("header", StringComparison.Ordinal))
                {
                    continue;
                }

                var words = match.Groups["text"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > MaxDescriptionWords)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Groups["text"].Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "description texts in views:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NumericViewModelProperties_UseNumericBox_NotTextBox()
    {
        var assembly = typeof(Miller.App.ViewModels.MainWindowViewModel).Assembly;
        var numeric = new[] { typeof(float), typeof(double), typeof(int), typeof(decimal), typeof(long) };
        var offenders = new List<string>();
        var checked_ = 0;
        foreach (var file in ViewFiles())
        {
            var text = File.ReadAllText(file);
            var dataType = DataType().Match(text);
            if (!dataType.Success)
            {
                continue;
            }

            var type = assembly.GetType($"Miller.App.ViewModels.{dataType.Groups["type"].Value}");
            Assert.NotNull(type);
            foreach (Match match in BoundTextBox().Matches(text))
            {
                var path = match.Groups["path"].Value;
                var property = type.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    continue;
                }

                checked_++;
                if (numeric.Contains(property.PropertyType))
                {
                    offenders.Add($"{Path.GetFileName(file)}: TextBox bound to {type.Name}.{path} ({property.PropertyType.Name})");
                }
            }
        }

        Assert.True(checked_ > 0, "no bound TextBox found; the rule scan is broken");
        Assert.True(offenders.Count == 0, "numeric properties behind a plain TextBox:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryNumericBox_BindsValue_NotText()
    {
        var offenders = new List<string>();
        var count = 0;
        foreach (var file in ViewFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"<c:NumericBox\b[^>]*>"))
            {
                count++;
                if (match.Value.Contains(" Text=", StringComparison.Ordinal) || !match.Value.Contains(" Value=\"{Binding ", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.True(count >= 30, $"only {count} NumericBox controls found");
        Assert.True(offenders.Count == 0, "NumericBox without a Value binding:\n" + string.Join("\n", offenders));
    }
}
