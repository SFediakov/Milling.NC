using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Miller.App.Services;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class MainWindowHeadlessTests
{
    private static readonly string[] TopLevelMenus = { "FileMenu", "ToolpathMenu", "ViewMenu", "SimulationMenu", "HelpMenu" };

    private static readonly string[] MenuItems =
    {
        "OpenStlItem", "OpenProjectItem", "SaveProjectItem", "SaveProjectAsItem", "ExportNcItem", "ExitItem", "GenerateItem", "CancelGenerateItem",
        "ResetCameraItem", "ShowModelItem", "ShowStockItem", "ShowToolpathItem", "ShowToolItem",
        "PlayItem", "PauseItem", "StopItem", "RunToEndItem", "AboutItem",
    };

    private static readonly string[] Tabs = { "PresetsTab", "ModelsTab", "ToolTab", "StockTab", "AxesTab", "CuttingTab", "StrategyTab", "SimulationTab", "AnalysisTab" };

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

    [AvaloniaFact]
    public void MainWindow_MenuHotKeysWorkWithoutOpeningTheMenu()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-headless-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.GetLogicalDescendants().OfType<TabItem>().First().Focus();
            var status = viewModel.StatusText;
            Assert.True(viewModel.Viewport.ShowStock);
            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Digit2, RawInputModifiers.Control);
            Assert.False(viewModel.Viewport.ShowStock);

            window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
            Assert.True(viewModel.Viewport.FitPending);

            // No mesh: F5 must reach the command and be refused quietly.
            window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.F5, RawInputModifiers.None);
            Assert.Equal(status, viewModel.StatusText);
            Assert.Null(viewModel.LastResult);

            // The menu still announces the gestures.
            var item = window.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Name == "ShowStockItem");
            Assert.Equal("Ctrl+D2", item.InputGesture?.ToString());
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

    [AvaloniaFact]
    public async Task UiTimer_MovesTheSimulationIntoTheViewport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-headless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var viewModel = TestServices.MainWindowViewModel(root);
        using var timer = new UiTimer(viewModel.Simulation, viewModel.Viewport);
        try
        {
            var stl = Path.Combine(root, "box.stl");
            File.WriteAllText(stl, TestMeshes.AsciiCubeText());
            Assert.True(await viewModel.OpenStlFileAsync(stl));
            viewModel.Stock.SizeX = 10;
            viewModel.Stock.SizeY = 10;
            viewModel.Stock.SizeZ = 3;
            viewModel.Cutting.CellSize = 0.5f;
            await viewModel.GenerateCommand.ExecuteAsync(null);

            var ticks = 0;
            timer.Ticked += (_, _) => ticks++;
            timer.Tick(1);
            Assert.Equal(0, ticks);

            viewModel.SimulationPanel.PlayCommand.Execute(null);
            viewModel.Simulation.SpeedFactor = 100f;
            timer.Tick(0.5);
            Assert.Equal(1, ticks);
            Assert.True(viewModel.Viewport.ToolpathProgressIndex > 0);
            Assert.False(viewModel.Viewport.StockDirty.IsEmpty);
            Assert.Equal(viewModel.Simulation.ToolPosition, viewModel.Viewport.ToolPosition);
            var taken = viewModel.Viewport.TakeStockDirty();
            Assert.False(taken.IsEmpty);
            Assert.True(viewModel.Viewport.StockDirty.IsEmpty);

            timer.Start();
            Assert.True(timer.IsRunning);
            timer.Stop();
            Assert.False(timer.IsRunning);
        }
        finally
        {
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
