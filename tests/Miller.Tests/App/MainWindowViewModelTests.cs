using Miller.App.ViewModels;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-vm-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private MainWindowViewModel Create() => TestServices.MainWindowViewModel(_root, _dialogs, _errors);

    [Fact]
    public async Task OpenStl_ImportsTheFixtureAndUpdatesStatusProjectAndSettings()
    {
        var vm = Create();
        var fixture = TestMeshes.FixturePath();
        _dialogs.OpenResults.Enqueue(fixture);

        await vm.OpenStlCommand.ExecuteAsync(null);

        Assert.Equal("4050 triangles, 20.482 x 5.000 x 21.971 mm", vm.StatusText);
        Assert.Equal(fixture, vm.Project.Current.StlPath);
        Assert.True(vm.Project.IsDirty);
        Assert.EndsWith(" *", vm.Title);
        Assert.Equal(Path.GetDirectoryName(fixture), vm.Settings.LastStlDirectory);
        Assert.True(File.Exists(vm.Settings.FilePath));
        Assert.True(vm.MeshImport.HasMesh);
        Assert.Empty(_errors.Shown);
        Assert.Equal("Open STL", Assert.Single(_dialogs.Titles));
    }

    [Fact]
    public async Task OpenStl_Cancelled_ChangesNothing()
    {
        var vm = Create();
        await vm.OpenStlCommand.ExecuteAsync(null);
        Assert.Equal(MainWindowViewModel.ReadyStatus, vm.StatusText);
        Assert.False(vm.Project.IsDirty);
        Assert.False(vm.MeshImport.HasMesh);
        Assert.Equal(string.Empty, vm.Project.Current.StlPath);
    }

    [Fact]
    public async Task OpenStl_UnreadableFile_GoesToTheErrorDialog()
    {
        Directory.CreateDirectory(_root);
        var bad = Path.Combine(_root, "bad.stl");
        File.WriteAllText(bad, "this is not an stl");
        var vm = Create();
        _dialogs.OpenResults.Enqueue(bad);

        await vm.OpenStlCommand.ExecuteAsync(null);

        Assert.IsType<InvalidDataException>(Assert.Single(_errors.Shown));
        Assert.Equal(MainWindowViewModel.ReadyStatus, vm.StatusText);
        Assert.False(vm.Project.IsDirty);
        Assert.False(vm.MeshImport.HasMesh);
    }

    [Fact]
    public void Stubs_ReportNotImplemented_AndExitRaisesTheEvent()
    {
        var vm = Create();
        vm.GenerateCommand.Execute(null);
        Assert.EndsWith(MainWindowViewModel.NotImplementedSuffix, vm.StatusText);

        var raised = 0;
        vm.ExitRequested += (_, _) => raised++;
        vm.ExitCommand.Execute(null);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task StockFit_AndAxisSwap_UseTheOrientedFixtureBounds()
    {
        var vm = Create();
        _dialogs.OpenResults.Enqueue(TestMeshes.FixturePath());
        await vm.OpenStlCommand.ExecuteAsync(null);

        Assert.True(vm.Stock.FitToModelCommand.CanExecute(null));
        vm.Stock.FitToModelCommand.Execute(null);
        Assert.Equal(30.482f, vm.Stock.SizeX, 3);
        Assert.Equal(15f, vm.Stock.SizeY, 3);
        Assert.Equal(21.971f, vm.Stock.SizeZ, 3);

        vm.Axes.MapY = Miller.Core.Setup.ModelAxis.Z;
        vm.Axes.MapZ = Miller.Core.Setup.ModelAxis.Y;
        Assert.StartsWith("20.482 x 21.971 x 5.000 mm", vm.Axes.MachineBoundsText);
        Assert.Null(vm.Axes.MappingError);

        vm.Axes.MapZ = Miller.Core.Setup.ModelAxis.Z;
        Assert.NotNull(vm.Axes.MappingError);
        Assert.Equal(vm.Axes.MappingError, vm.Axes.MachineBoundsText);
        Assert.False(vm.Stock.FitToModelCommand.CanExecute(null));

        vm.Tool.HeadDiameter = 5f;
        Assert.NotNull(vm.Tool.HeadDiameterError);
        vm.Tool.HeadDiameter = 10f;
        Assert.Null(vm.Tool.HeadDiameterError);
    }

    [Fact]
    public void Title_CarriesTheVersion_AndChildPanelsExist()
    {
        var vm = Create();
        Assert.Equal($"Miller {TestServices.Version}", vm.Title);
        Assert.NotNull(vm.Tool);
        Assert.NotNull(vm.Stock);
        Assert.NotNull(vm.Axes);
    }
}
