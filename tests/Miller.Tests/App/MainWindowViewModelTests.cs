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

    private readonly FakeConfirmDialogService _confirm = new();

    private MainWindowViewModel Create() => TestServices.MainWindowViewModel(_root, _dialogs, _errors, _confirm);

    // A 10 x 10 x 5 box as an ASCII STL in the temp directory, with a small stock and coarse grid.
    private async Task<MainWindowViewModel> CreateWithBoxAsync()
    {
        Directory.CreateDirectory(_root);
        var stl = Path.Combine(_root, "box.stl");
        File.WriteAllText(stl, TestMeshes.AsciiCubeText());
        var vm = Create();
        _dialogs.OpenResults.Enqueue(stl);
        await vm.OpenStlCommand.ExecuteAsync(null);
        vm.Stock.SizeX = 10;
        vm.Stock.SizeY = 10;
        vm.Stock.SizeZ = 3;
        vm.Cutting.CellSize = 0.5f;
        return vm;
    }

    [Fact]
    public async Task Generate_ProducesAResult_AndExportWritesTheFile()
    {
        var vm = await CreateWithBoxAsync();
        Assert.True(vm.GenerateCommand.CanExecute(null));
        Assert.False(vm.ExportNcCommand.CanExecute(null));

        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.NotNull(vm.LastResult);
        Assert.False(vm.IsBusy);
        Assert.StartsWith("Toolpath ready:", vm.StatusText);
        Assert.NotEqual(StrategySelectionViewModel.NoToolpathText, vm.Strategy.StatisticsText);
        Assert.True(vm.ExportNcCommand.CanExecute(null));

        var nc = Path.Combine(_root, "box.nc");
        _dialogs.SaveResults.Enqueue(nc);
        await vm.ExportNcCommand.ExecuteAsync(null);
        Assert.True(File.Exists(nc));
        Assert.StartsWith($"( Miller {TestServices.Version} )", File.ReadAllText(nc));
        Assert.Equal(_root, vm.Settings.LastExportDirectory);
        Assert.Empty(_errors.Shown);
    }

    [Fact]
    public async Task Viewport_FollowsImportProjectChangesAndGeneration()
    {
        var vm = await CreateWithBoxAsync();
        var viewport = vm.Viewport;
        Assert.NotNull(viewport.Mesh);
        Assert.NotNull(viewport.StockBounds);
        Assert.NotNull(viewport.Tool);
        Assert.Null(viewport.Toolpath);
        var meshVersion = viewport.MeshVersion;
        var stockVersion = viewport.StockVersion;

        vm.Stock.SizeX = 12;
        Assert.True(viewport.StockVersion > stockVersion);
        Assert.Equal(12f, viewport.StockBounds!.Value.Size.X, 3);

        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.NotNull(viewport.Toolpath);
        Assert.NotNull(viewport.StockMap);
        Assert.Equal(vm.LastResult!.Toolpath.Count, viewport.Toolpath!.Count);

        vm.ToggleStockCommand.Execute(null);
        Assert.False(viewport.ShowStock);
        vm.ResetCameraCommand.Execute(null);
        Assert.True(viewport.FitPending);

        vm.NewProjectCommand.Execute(null);
        Assert.Null(viewport.Mesh);
        Assert.Null(viewport.Toolpath);
        Assert.Null(viewport.StockMap);
        Assert.True(viewport.MeshVersion > meshVersion);
    }

    [Fact]
    public async Task Generate_InvalidProject_GoesToTheErrorDialog()
    {
        var vm = await CreateWithBoxAsync();
        vm.Cutting.Stepdown = 0f;
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Null(vm.LastResult);
        Assert.IsType<Miller.Application.Validation.ValidationException>(Assert.Single(_errors.Shown));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveAs_New_Open_RoundTripsTheProjectAndReimportsTheMesh()
    {
        var vm = await CreateWithBoxAsync();
        vm.Tool.CutterDiameter = 4.5f;
        var file = Path.Combine(_root, "box.miller.json");
        _dialogs.SaveResults.Enqueue(file);
        await vm.SaveProjectAsCommand.ExecuteAsync(null);
        Assert.True(File.Exists(file));
        Assert.False(vm.Project.IsDirty);
        Assert.Equal(file, vm.Project.Path);

        vm.NewProjectCommand.Execute(null);
        Assert.Equal(6f, vm.Tool.CutterDiameter);
        Assert.False(vm.MeshImport.HasMesh);

        _dialogs.OpenResults.Enqueue(file);
        await vm.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(4.5f, vm.Tool.CutterDiameter);
        Assert.Equal(10f, vm.Stock.SizeX);
        Assert.True(vm.MeshImport.HasMesh);
        Assert.False(vm.Project.IsDirty);

        vm.Tool.CutterDiameter = 5f;
        await vm.SaveProjectCommand.ExecuteAsync(null);
        Assert.False(vm.Project.IsDirty);
        Assert.Contains("\"CutterDiameter\": 5", File.ReadAllText(file));
    }

    [Fact]
    public async Task Exit_WhenDirty_FollowsTheConfirmDecision()
    {
        var vm = Create();
        var raised = 0;
        vm.ExitRequested += (_, _) => raised++;
        vm.Tool.CutterDiameter = 4f;

        _confirm.Answers.Enqueue(Miller.App.Services.SaveDecision.Cancel);
        await vm.ExitCommand.ExecuteAsync(null);
        Assert.Equal(0, raised);

        _confirm.Answers.Enqueue(Miller.App.Services.SaveDecision.Save);
        await vm.ExitCommand.ExecuteAsync(null);
        Assert.Equal(0, raised); // the save dialog was cancelled, so the exit was cancelled too

        _confirm.Answers.Enqueue(Miller.App.Services.SaveDecision.Save);
        Directory.CreateDirectory(_root);
        _dialogs.SaveResults.Enqueue(Path.Combine(_root, "exit.miller.json"));
        await vm.ExitCommand.ExecuteAsync(null);
        Assert.Equal(1, raised);
        Assert.False(vm.Project.IsDirty);

        vm.Tool.CutterDiameter = 3f;
        _confirm.Answers.Enqueue(Miller.App.Services.SaveDecision.Discard);
        await vm.ExitCommand.ExecuteAsync(null);
        Assert.Equal(2, raised);
        Assert.Equal(4, _confirm.Asked);
    }

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
        vm.PlayCommand.Execute(null);
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
