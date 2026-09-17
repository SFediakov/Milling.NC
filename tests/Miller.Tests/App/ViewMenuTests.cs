using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// The View menu shows what the viewport draws (check marks bound to the flags, one flip per click
// or shortcut), and a generated toolpath hides the imported models until the result is cleared.
public sealed class ViewMenuTests
{
    private static readonly (string Item, string Flag)[] Items =
    {
        ("ShowModelItem", "ShowModel"), ("ShowStockItem", "ShowStock"), ("ShowToolpathItem", "ShowToolpath"), ("ShowToolItem", "ShowTool"),
    };

    [AvaloniaFact]
    public void ShowItems_AreCheckItems_WhoseMarkFollowsTheFlag_AndAClickFlipsItOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-viewmenu-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            var menuItems = window.GetLogicalDescendants().OfType<MenuItem>().Distinct().ToDictionary(m => m.Name ?? string.Empty);
            foreach (var (name, flag) in Items)
            {
                var item = menuItems[name];
                Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
                Assert.Null(item.Command);
                Assert.True(item.IsChecked);
                var property = typeof(Miller.App.ViewModels.ViewportViewModel).GetProperty(flag)!;

                property.SetValue(viewModel.Viewport, false);
                Assert.False(item.IsChecked);

                // The menu interaction handler toggles IsChecked on a click; the binding writes it back.
                item.IsChecked = true;
                Assert.True((bool)property.GetValue(viewModel.Viewport)!);
                property.SetValue(viewModel.Viewport, true);
            }

            window.GetLogicalDescendants().OfType<TabItem>().First().Focus();
            window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
            Assert.False(viewModel.Viewport.ShowModel);
            Assert.False(menuItems["ShowModelItem"].IsChecked);
            window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Digit1, RawInputModifiers.Control);
            Assert.True(viewModel.Viewport.ShowModel);
            Assert.True(menuItems["ShowModelItem"].IsChecked);
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

    [Fact]
    public async Task GeneratedToolpath_HidesTheModels_UntilTheResultIsCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-hide-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var viewModel = TestServices.MainWindowViewModel(root);
            var stl = Path.Combine(root, "box.stl");
            File.WriteAllText(stl, TestMeshes.AsciiCubeText());
            Assert.True(await viewModel.OpenStlFileAsync(stl));
            viewModel.Stock.SizeX = 10;
            viewModel.Stock.SizeY = 10;
            viewModel.Stock.SizeZ = 3;
            viewModel.Cutting.CellSize = 0.5f;
            Assert.True(viewModel.Viewport.ShowModel);

            await viewModel.GenerateCommand.ExecuteAsync(null);
            Assert.NotNull(viewModel.LastResult);
            Assert.False(viewModel.Viewport.ShowModel);
            Assert.True(viewModel.Viewport.ShowStock);
            Assert.NotNull(viewModel.Viewport.StockMap);

            // A second model invalidates the result: the models are visible again.
            Assert.True(await viewModel.OpenStlFileAsync(stl));
            Assert.Null(viewModel.LastResult);
            Assert.True(viewModel.Viewport.ShowModel);

            viewModel.Models.RemoveCommand.Execute(null);
            await viewModel.GenerateCommand.ExecuteAsync(null);
            Assert.False(viewModel.Viewport.ShowModel);
            viewModel.NewProjectCommand.Execute(null);
            Assert.True(viewModel.Viewport.ShowModel);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
