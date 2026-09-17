using System.Numerics;
using Miller.App.ViewModels;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class ModelsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-models-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<MainWindowViewModel> WithTwoCubesAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        foreach (var name in new[] { "first.stl", "second.stl" })
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, TestMeshes.AsciiCubeText());
            _dialogs.OpenResults.Enqueue(path);
            await vm.OpenStlCommand.ExecuteAsync(null);
        }

        return vm;
    }

    [Fact]
    public async Task AddingModels_ListsThem_SelectsTheLast_AndShowsBothInTheViewport()
    {
        var vm = await WithTwoCubesAsync();
        Assert.Equal(new[] { "first.stl", "second.stl" }, vm.Models.Names);
        Assert.Equal(2, vm.Project.Current.Models.Count);
        Assert.Equal(2, vm.MeshImport.Meshes.Count);
        Assert.Equal(1, vm.Models.SelectedIndex);
        Assert.Equal(1, vm.Viewport.SelectedModelIndex);
        Assert.Equal(2, vm.Viewport.Meshes.Count);
        Assert.True(vm.Models.HasSelection);
        Assert.Contains("second.stl", vm.Models.PlacementText);
        Assert.Empty(_errors.Shown);
    }

    [Fact]
    public async Task Offsets_MoveTheSelectedModel_AndCenteringUsesOneAxis()
    {
        var vm = await WithTwoCubesAsync();
        vm.Models.SelectedIndex = 1;
        var before = vm.Viewport.Meshes[1].Bounds;
        vm.Models.OffsetX = 20f;
        Assert.Equal(new Vector3(20, 0, 0), vm.Project.Current.Models[1].Offset);
        Assert.True(vm.Project.IsDirty);
        // The stock auto-fits around both cubes, so the second one moves right by 10 relative to the first.
        Assert.Equal(vm.Viewport.Meshes[0].Bounds.Min.X + 20f, vm.Viewport.Meshes[1].Bounds.Min.X, 3);
        Assert.NotEqual(before, vm.Viewport.Meshes[1].Bounds);

        vm.Models.RotationZ = 45f;
        Assert.Equal(45f, vm.Project.Current.Models[1].RotationZ);

        vm.Models.OffsetY = 7f;
        vm.Models.CenterYCommand.Execute(null);
        var placement = vm.Project.Current.Models[1];
        Assert.Equal(20f, placement.Offset.X, 3);
        Assert.NotEqual(7f, placement.Offset.Y);
        Assert.Equal(0f, placement.Offset.Z, 3);
        var machine = ModelLayout.MachineBounds(vm.Project.Current, vm.MeshImport.Bounds);
        var second = vm.Viewport.Meshes[1].Bounds;
        Assert.Equal(machine.Center.Y, second.Center.Y, 2);
    }

    [Fact]
    public async Task Remove_DropsPlacementAndMesh_AndViewportSelectionFollowsTheList()
    {
        var vm = await WithTwoCubesAsync();
        vm.Models.SelectedIndex = 0;
        Assert.Equal(0, vm.Viewport.SelectedModelIndex);
        vm.Models.RemoveCommand.Execute(null);
        Assert.Equal(new[] { "second.stl" }, vm.Models.Names);
        Assert.Single(vm.MeshImport.Meshes);
        Assert.Single(vm.Viewport.Meshes);
        Assert.Equal(-1, vm.Models.SelectedIndex);
        Assert.False(vm.Models.RemoveCommand.CanExecute(null));

        vm.Viewport.Select(0);
        Assert.Equal(0, vm.Models.SelectedIndex);
        Assert.True(vm.Models.CenterXCommand.CanExecute(null));

        vm.NewProjectCommand.Execute(null);
        Assert.Empty(vm.Models.Names);
        Assert.Equal(-1, vm.Models.SelectedIndex);
        Assert.False(vm.Models.HasSelection);
    }

    [Fact]
    public async Task SaveAndReopen_KeepsEveryModelWithItsPlacement()
    {
        var vm = await WithTwoCubesAsync();
        vm.Models.SelectedIndex = 1;
        vm.Models.OffsetX = 12f;
        var projectPath = Path.Combine(_root, "two.miller.json");
        _dialogs.SaveResults.Enqueue(projectPath);
        await vm.SaveProjectAsCommand.ExecuteAsync(null);

        var reopened = TestServices.MainWindowViewModel(_root, _dialogs, _errors);
        _dialogs.OpenResults.Enqueue(projectPath);
        await reopened.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(2, reopened.Project.Current.Models.Count);
        Assert.Equal(2, reopened.MeshImport.Meshes.Count);
        Assert.Equal(12f, reopened.Project.Current.Models[1].Offset.X);
        Assert.Equal(2, reopened.Viewport.Meshes.Count);
        Assert.Empty(_errors.Shown);
    }
    [Fact]
    public async Task Align_MovesTheSelectedModelToTheStockSide_AndReportsWhenTheStockFollowsIt()
    {
        var vm = await WithTwoCubesAsync();
        vm.Models.SelectedIndex = 1;
        vm.Models.OffsetX = 20f;
        Assert.True(vm.Models.AlignCommand.CanExecute(AlignTarget.XMax));

        // Auto-fit stock centred on the union: the second cube reaches the stock's +X side and its
        // bottom while the first cube anchors the union.
        vm.Models.AlignCommand.Execute(AlignTarget.XMax);
        Assert.Null(vm.Models.AlignmentMessage);
        var stock = vm.Project.Current.Stock;
        var machine = ModelLayout.MachineBounds(vm.Project.Current, vm.MeshImport.Bounds);
        Assert.Equal(machine.Max.X, vm.Viewport.Meshes[1].Bounds.Max.X, 2);
        Assert.Equal(stock.SizeX, machine.Max.X, 2);
        vm.Models.AlignCommand.Execute(AlignTarget.ZMin);
        Assert.Null(vm.Models.AlignmentMessage);
        Assert.Equal(0f, vm.Viewport.Meshes[1].Bounds.Min.Z, 2);

        // A lone cube: the stock hangs from its top, so Z Min has no fixed point and says so; the
        // message clears when the selection changes.
        vm.Models.RemoveCommand.Execute(null);
        vm.Models.SelectedIndex = 0;
        vm.Models.AlignCommand.Execute(AlignTarget.ZMin);
        Assert.NotNull(vm.Models.AlignmentMessage);
        Assert.Contains("Z", vm.Models.AlignmentMessage);
        Assert.Equal(Vector3.Zero, vm.Project.Current.Models[0].Offset);
        vm.Models.SelectedIndex = -1;
        Assert.Null(vm.Models.AlignmentMessage);
        Assert.False(vm.Models.AlignCommand.CanExecute(AlignTarget.XMin));
    }

    [Fact]
    public async Task StockAlignment_MovesTheUnionInsideTheStock()
    {
        var vm = await WithTwoCubesAsync();
        Assert.True(vm.Models.IsAutoFit);
        Assert.Equal(StockAlignment.Max, vm.Models.StockAlignZ);
        Assert.Equal(new[] { StockAlignment.Min, StockAlignment.Center, StockAlignment.Max }, ModelsViewModel.StockAlignments);

        vm.Models.StockAlignZ = StockAlignment.Min;
        Assert.Equal(StockAlignment.Min, vm.Project.Current.Stock.AlignZ);
        Assert.True(vm.Project.IsDirty);
        var machine = ModelLayout.MachineBounds(vm.Project.Current, vm.MeshImport.Bounds);
        Assert.Equal(0f, machine.Min.Z, 3);
        Assert.Equal(0f, vm.Viewport.Meshes[0].Bounds.Min.Z, 3);

        vm.Models.StockAlignX = StockAlignment.Min;
        machine = ModelLayout.MachineBounds(vm.Project.Current, vm.MeshImport.Bounds);
        Assert.Equal(0f, machine.Min.X, 3);

        vm.Stock.Placement = StockPlacement.Explicit;
        Assert.False(vm.Models.IsAutoFit);
        vm.NewProjectCommand.Execute(null);
        Assert.Equal(StockAlignment.Max, vm.Models.StockAlignZ);
        Assert.True(vm.Models.IsAutoFit);
    }
}
