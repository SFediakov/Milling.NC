using Miller.App.ViewModels;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

public sealed class SettingsViewModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-panels-{Guid.NewGuid():N}");
    private readonly MainWindowViewModel _vm;

    public SettingsViewModelsTests()
    {
        _vm = TestServices.MainWindowViewModel(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Tool_WritesToTheProjectAndValidates()
    {
        _vm.Tool.CutterDiameter = 4f;
        Assert.Equal(4f, _vm.Project.Current.Tool.CutterDiameter);
        Assert.True(_vm.Project.IsDirty);
        Assert.Null(_vm.Tool.CutterDiameterError);

        _vm.Tool.HeadDiameter = 3f;
        Assert.NotNull(_vm.Tool.HeadDiameterError);
        _vm.Tool.HeadDiameter = 12f;
        Assert.Null(_vm.Tool.HeadDiameterError);
        _vm.Tool.TipType = TipType.Ball;
        Assert.True(_vm.Tool.IsBall);
        Assert.True(_vm.Tool.HeadWidth > _vm.Tool.CutterWidth);
    }

    [Fact]
    public void Tool_FrustumHead_WritesItsFieldsAndDrawsATrapezoid()
    {
        Assert.False(_vm.Tool.IsFrustum);
        var cylinder = _vm.Tool.HeadOutline;
        Assert.Equal(cylinder[0].X, cylinder[3].X);
        Assert.Equal(cylinder[1].X, cylinder[2].X);

        _vm.Tool.HeadShape = HeadShape.Frustum;
        Assert.True(_vm.Tool.IsFrustum);
        Assert.Equal(HeadShape.Frustum, _vm.Project.Current.Tool.HeadShape);
        _vm.Tool.HeadTopDiameter = 16f;
        _vm.Tool.HeadLength = 4f;
        Assert.Equal(16f, _vm.Project.Current.Tool.HeadTopDiameter);
        Assert.Equal(4f, _vm.Project.Current.Tool.HeadLength);
        Assert.Null(_vm.Tool.HeadTopDiameterError);
        Assert.Null(_vm.Tool.HeadLengthError);

        // Bottom 10, top 16: the top edge is 1.6 times the bottom edge and as wide as the schematic head.
        var outline = _vm.Tool.HeadOutline;
        var bottom = outline[1].X - outline[0].X;
        var top = outline[2].X - outline[3].X;
        Assert.Equal(1.6, top / bottom, 6);
        Assert.Equal(_vm.Tool.HeadWidth, top, 6);
        Assert.Equal(ToolSettingsViewModel.SchematicHeadHeight, outline[0].Y);
        Assert.Equal(0, outline[3].Y);

        _vm.Tool.HeadTopDiameter = 5f;
        Assert.NotNull(_vm.Tool.HeadTopDiameterError);
        _vm.Tool.HeadLength = 0f;
        Assert.NotNull(_vm.Tool.HeadLengthError);
        _vm.Tool.HeadShape = HeadShape.Cylinder;
        Assert.Null(_vm.Tool.HeadTopDiameterError);
        Assert.Null(_vm.Tool.HeadLengthError);
    }

    [Fact]
    public void Stock_SwitchesShapeAndValidates()
    {
        _vm.Stock.Shape = StockShape.Cylinder;
        Assert.True(_vm.Stock.IsCylinder);
        Assert.False(_vm.Stock.IsBox);
        Assert.Equal(StockShape.Cylinder, _vm.Project.Current.Stock.Shape);
        _vm.Stock.Height = 0f;
        Assert.NotNull(_vm.Stock.HeightError);
        _vm.Stock.Height = 30f;
        Assert.Null(_vm.Stock.HeightError);
        _vm.Stock.Margin = -1f;
        Assert.NotNull(_vm.Stock.MarginError);
        _vm.Stock.Placement = StockPlacement.Explicit;
        Assert.True(_vm.Stock.IsExplicit);
        _vm.Stock.OriginX = 2.5f;
        Assert.Equal(2.5f, _vm.Project.Current.Stock.ExplicitOrigin.X);
    }

    [Fact]
    public void Axes_ValidateTheMappingAndReportNoModel()
    {
        Assert.Equal(AxisSettingsViewModel.NoModelText, _vm.Axes.MachineBoundsText);
        _vm.Axes.MapY = ModelAxis.X;
        Assert.NotNull(_vm.Axes.MappingError);
        Assert.Equal(_vm.Axes.MappingError, _vm.Axes.MachineBoundsText);
        _vm.Axes.MapY = ModelAxis.Y;
        Assert.Null(_vm.Axes.MappingError);
        _vm.Axes.RotationZ = 90f;
        _vm.Axes.FlipX = true;
        _vm.Axes.OriginMode = OriginMode.Custom;
        Assert.True(_vm.Axes.IsCustom);
        _vm.Axes.OffsetZ = -3f;
        Assert.Equal(90f, _vm.Project.Current.Axes.RotationZ);
        Assert.True(_vm.Project.Current.Axes.FlipX);
        Assert.Equal(-3f, _vm.Project.Current.Axes.CustomOffset.Z);
    }

    [Fact]
    public void Cutting_ValidatesAgainstTheToolAndReportsTheGrid()
    {
        _vm.Cutting.Stepover = 7f;
        Assert.NotNull(_vm.Cutting.StepoverError);
        _vm.Cutting.Stepover = 3f;
        Assert.Null(_vm.Cutting.StepoverError);

        _vm.Cutting.CellSize = 0.01f;
        Assert.NotNull(_vm.Cutting.CellSizeError);
        Assert.Contains("10000 x 10000", _vm.Cutting.GridSizeText);
        _vm.Cutting.CellSize = 0.5f;
        Assert.Null(_vm.Cutting.CellSizeError);
        Assert.Null(_vm.Cutting.CellSizeWarning);
        Assert.StartsWith("200 x 200 = 40,000 cells", _vm.Cutting.GridSizeText);
        _vm.Cutting.CellSize = 0.05f;
        Assert.Null(_vm.Cutting.CellSizeError);
        Assert.NotNull(_vm.Cutting.CellSizeWarning);
        _vm.Cutting.CellSize = 0.5f;
        Assert.Null(_vm.Cutting.CellSizeWarning);

        _vm.Cutting.SafeHeight = 0f;
        Assert.NotNull(_vm.Cutting.SafeHeightError);
    }

    [Fact]
    public void Strategy_ListsTheRegistriesAndWritesIds()
    {
        Assert.Equal(new[] { "z-layer-by-layer", "three-axis-freedom" }, StrategySelectionViewModel.Strategies.Select(s => s.Id));
        Assert.Equal(new[] { "grbl" }, _vm.Strategy.PostProcessors.Select(p => p.Id));
        Assert.Equal("z-layer-by-layer", _vm.Strategy.Strategy!.Id);

        _vm.Strategy.Strategy = StrategyRegistry.GetById("three-axis-freedom");
        Assert.Equal("three-axis-freedom", _vm.Project.Current.RoutingStrategyId);
        Assert.True(_vm.Project.IsDirty);
        Assert.Equal(StrategySelectionViewModel.NoToolpathText, _vm.Strategy.StatisticsText);
        Assert.Same(_vm.GenerateCommand, _vm.Strategy.GenerateCommand);
    }

    [Fact]
    public void Strategy_CutScope_WritesTheProjectAndFollowsAReload()
    {
        Assert.Equal(new[] { CutScope.Everything, CutScope.Separation }, StrategySelectionViewModel.CutScopes);
        Assert.Equal(CutScope.Everything, _vm.Strategy.CutScope);
        _vm.Strategy.CutScope = CutScope.Separation;
        Assert.Equal(CutScope.Separation, _vm.Project.Current.CutScope);
        Assert.True(_vm.Project.IsDirty);

        var raised = new List<string?>();
        _vm.Strategy.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _vm.NewProjectCommand.Execute(null);
        Assert.Equal(CutScope.Everything, _vm.Strategy.CutScope);
        Assert.Contains(nameof(StrategySelectionViewModel.CutScope), raised);
    }

    [Fact]
    public void Strategy_MinIslandVolume_FollowsTheScopeAndValidates()
    {
        Assert.False(_vm.Strategy.IsSeparation);
        Assert.Equal(0f, _vm.Strategy.MinIslandVolume);
        var raised = new List<string?>();
        _vm.Strategy.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _vm.Strategy.CutScope = CutScope.Separation;
        Assert.True(_vm.Strategy.IsSeparation);
        Assert.Contains(nameof(StrategySelectionViewModel.IsSeparation), raised);

        _vm.Strategy.MinIslandVolume = 30f;
        Assert.Equal(30f, _vm.Project.Current.MinIslandVolume);
        Assert.Null(_vm.Strategy.MinIslandVolumeError);
        _vm.Strategy.MinIslandVolume = -2f;
        Assert.NotNull(_vm.Strategy.MinIslandVolumeError);
        Assert.Contains(nameof(StrategySelectionViewModel.MinIslandVolumeError), raised);

        _vm.NewProjectCommand.Execute(null);
        Assert.False(_vm.Strategy.IsSeparation);
        Assert.Equal(0f, _vm.Strategy.MinIslandVolume);
        Assert.Null(_vm.Strategy.MinIslandVolumeError);
    }

    [Fact]
    public void NewProject_ReloadsEveryPanelWithoutMarkingDirty()
    {
        _vm.Tool.CutterDiameter = 4f;
        _vm.Cutting.Stepdown = 9f;
        _vm.NewProjectCommand.Execute(null);
        Assert.Equal(ToolDefinition.DefaultCutterDiameter, _vm.Tool.CutterDiameter);
        Assert.Equal(CuttingParameters.DefaultStepdown, _vm.Cutting.Stepdown);
        Assert.False(_vm.Project.IsDirty);
        Assert.Equal(MainWindowViewModel.NewProjectStatus, _vm.StatusText);
    }
}
