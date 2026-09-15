using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class MainWindowHeadlessTests
{
    private static readonly string[] TopLevelMenus = { "FileMenu", "ViewMenu", "SimulationMenu", "HelpMenu" };

    private static readonly string[] MenuItems =
    {
        "OpenStlItem", "OpenProjectItem", "SaveProjectItem", "SaveProjectAsItem", "ExportNcItem", "ExitItem",
        "ResetCameraItem", "ShowModelItem", "ShowStockItem", "ShowToolpathItem", "ShowToolItem",
        "PlayItem", "PauseItem", "StopItem", "RunToEndItem", "AboutItem",
    };

    private static readonly string[] Tabs = { "ToolTab", "StockTab", "AxesTab", "CuttingTab", "StrategyTab", "SimulationTab", "AnalysisTab" };

    [AvaloniaFact]
    public void MainWindow_ShowsEveryMenuItemAndTab()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-headless-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            Assert.Equal(viewModel.Title, window.Title);

            // The selected tab content is reachable through two logical paths, hence Distinct below.
            var menu = window.GetLogicalDescendants().OfType<Menu>().Distinct().Single();
            var names = new HashSet<string>(StringComparer.Ordinal);
            Collect(menu.Items, names);
            foreach (var expected in TopLevelMenus.Concat(MenuItems))
            {
                Assert.Contains(expected, names);
            }

            var tabs = window.GetLogicalDescendants().OfType<TabItem>().Select(t => t.Name).ToList();
            Assert.Equal(Tabs, tabs);
            Assert.NotNull(window.FindControl<TextBlock>("StatusText"));
            Assert.NotNull(window.FindControl<Border>("ViewportHost"));
            Assert.Single(window.GetLogicalDescendants().OfType<ToolSettingsView>().Distinct());
            Assert.Single(window.GetLogicalDescendants().OfType<StockSettingsView>().Distinct());
            Assert.Single(window.GetLogicalDescendants().OfType<AxisSettingsView>().Distinct());
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void Collect(IEnumerable<object?> items, HashSet<string> names)
    {
        foreach (var item in items)
        {
            if (item is MenuItem menuItem)
            {
                if (menuItem.Name is not null)
                {
                    names.Add(menuItem.Name);
                }

                Collect(menuItem.Items, names);
            }
        }
    }
}
