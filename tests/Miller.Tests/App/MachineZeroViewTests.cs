using System.Numerics;
using Miller.App.ViewModels;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// The axes panel and the viewport for the machine zero modes (T-180): the viewport is drawn in machine
// space with the axes at zero, so on the stock the stock outline stays and the model moves when the
// stock corner changes, while at a global point the stock outline moves and the model and the axes stay.
public sealed class MachineZeroViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-zero-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<MainWindowViewModel> WithCubeAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        var path = Path.Combine(_root, "cube.stl");
        File.WriteAllText(path, TestMeshes.AsciiCubeText());
        _dialogs.OpenResults.Enqueue(path);
        await vm.OpenStlCommand.ExecuteAsync(null);
        vm.Stock.Placement = StockPlacement.Explicit;
        return vm;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void AxesPanel_ListsEveryMode_AndNamesWhatTheOffsetIsMeasuredFrom()
    {
        Assert.Equal(Enum.GetValues<OriginMode>(), AxisSettingsViewModel.OriginModes);
        Assert.Equal(9, AxisSettingsViewModel.OriginModes.Count);
        var vm = TestServices.MainWindowViewModel(_root);
        var labels = new Dictionary<OriginMode, string?>
        {
            [OriginMode.StockCornerMinXYMinZ] = null,
            [OriginMode.StockCenterTopZ] = null,
            [OriginMode.Custom] = AxisSettingsViewModel.StockOffsetLabel,
            [OriginMode.ModelCornerMinXYTopZ] = null,
            [OriginMode.ModelCustom] = AxisSettingsViewModel.ModelOffsetLabel,
            [OriginMode.Global] = AxisSettingsViewModel.GlobalOffsetLabel,
        };
        foreach (var (mode, label) in labels)
        {
            vm.Axes.OriginMode = mode;
            Assert.Equal(mode, vm.Project.Current.Axes.OriginMode);
            Assert.Equal(label is not null, vm.Axes.IsCustom);
            if (label is not null)
            {
                Assert.Equal(label, vm.Axes.OffsetLabel);
            }
        }

        Assert.Equal(AxisSettingsViewModel.NoModelText, vm.Axes.StockBoundsText);
    }

    [Fact]
    public async Task OnTheStock_ACornerEditMovesTheModel_AndTheStockOutlineStays()
    {
        var vm = await WithCubeAsync();
        var stock = vm.Viewport.StockBounds!.Value;
        var model = vm.Viewport.Meshes[0].Bounds;

        vm.Stock.OriginX = vm.Project.Current.Stock.ExplicitOrigin.X + 2f;
        AssertClose(stock.Min, vm.Viewport.StockBounds!.Value.Min);
        AssertClose(model.Min - new Vector3(2, 0, 0), vm.Viewport.Meshes[0].Bounds.Min);
    }

    [Fact]
    public async Task AtAGlobalPoint_ACornerEditMovesTheStockOutline_AndAnOffsetMovesOnlyTheModel()
    {
        var vm = await WithCubeAsync();
        vm.Axes.OriginMode = OriginMode.Global;
        var stock = vm.Viewport.StockBounds!.Value;
        var model = vm.Viewport.Meshes[0].Bounds;

        vm.Stock.OriginX = vm.Project.Current.Stock.ExplicitOrigin.X + 2f;
        AssertClose(stock.Min + new Vector3(2, 0, 0), vm.Viewport.StockBounds!.Value.Min);
        AssertClose(model.Min, vm.Viewport.Meshes[0].Bounds.Min);

        vm.Models.SelectedIndex = 0;
        vm.Models.OffsetY = 5f;
        AssertClose(stock.Min + new Vector3(2, 0, 0), vm.Viewport.StockBounds!.Value.Min);
        AssertClose(model.Min + new Vector3(0, 5, 0), vm.Viewport.Meshes[0].Bounds.Min);

        // The panel shows the box the viewport outlines.
        var shown = vm.Viewport.StockBounds!.Value;
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"min ({shown.Min.X:0.000}, {shown.Min.Y:0.000}, {shown.Min.Z:0.000})"), vm.Axes.StockBoundsText, StringComparison.Ordinal);
        Assert.Empty(_errors.Shown);
    }
}
