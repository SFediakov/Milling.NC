using System.Numerics;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Application;

public sealed class ProjectValidatorTests
{
    [Fact]
    public void DefaultProject_IsValid()
    {
        var result = ProjectValidator.Validate(MillingProject.Default(), null);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Constants_MatchSection67()
    {
        Assert.Equal(0.01f, ProjectValidator.MinCellSize);
        Assert.Equal(5f, ProjectValidator.MaxCellSize);
        Assert.Equal(4_000_000, ProjectValidator.MaxCells);
        Assert.Equal(1_000_000, ProjectValidator.MaxInteractiveCells);
    }

    [Fact]
    public void CellSize_AboveTheInteractiveLimit_WarnsWithoutAnError()
    {
        // 100 x 100 stock at 0.05 gives 4,000,000 cells: allowed, but far above the interactive limit.
        var result = Validate(p => p.Parameters.CellSize = 0.05f);
        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings, w => w.Field == "Parameters.CellSize");
        Assert.Contains("4000000", warning.Message);
        Assert.Contains("1000000", warning.Message);

        // Exactly at the limit: no warning.
        Assert.Empty(Validate(p => p.Parameters.CellSize = 0.1f).Warnings);
    }

    [Fact]
    public void CutterDiameter_MustBePositive()
    {
        AssertSingleError(p => p.Tool.CutterDiameter = 0, "Tool.CutterDiameter",
            // stepover rules also fail against a zero cutter diameter
            "Parameters.Stepover", "Parameters.FinishingStepover");
    }

    [Fact]
    public void MinIslandVolume_MustNotBeNegative()
    {
        AssertSingleError(p => p.MinIslandVolume = -1f, "Strategy.MinIslandVolume");
        Assert.True(Validate(p => p.MinIslandVolume = 0f).IsValid);
        Assert.True(Validate(p => p.MinIslandVolume = 250f).IsValid);
        AssertSingleError(p => p.MinIslandVolume = float.NaN, "Strategy.MinIslandVolume");
    }

    [Fact]
    public void CutterLength_MustBePositive()
    {
        AssertSingleError(p => p.Tool.CutterLength = -1, "Tool.CutterLength");
    }

    [Fact]
    public void HeadDiameter_MustExceedCutterDiameter()
    {
        AssertSingleError(p => p.Tool.HeadDiameter = 6, "Tool.HeadDiameter");
        AssertSingleError(p => p.Tool.HeadDiameter = 5, "Tool.HeadDiameter");
    }

    [Fact]
    public void FrustumHead_NeedsATopWiderThanTheCutterAndAPositiveLength()
    {
        AssertValid(p => p.Tool.HeadShape = HeadShape.Frustum);
        AssertValid(p => { p.Tool.HeadShape = HeadShape.Frustum; p.Tool.HeadTopDiameter = 8; });
        AssertSingleError(p => { p.Tool.HeadShape = HeadShape.Frustum; p.Tool.HeadTopDiameter = 6; }, "Tool.HeadTopDiameter");
        AssertSingleError(p => { p.Tool.HeadShape = HeadShape.Frustum; p.Tool.HeadTopDiameter = float.NaN; }, "Tool.HeadTopDiameter");
        AssertSingleError(p => { p.Tool.HeadShape = HeadShape.Frustum; p.Tool.HeadLength = 0; }, "Tool.HeadLength");
        AssertSingleError(p => { p.Tool.HeadShape = HeadShape.Frustum; p.Tool.HeadLength = float.PositiveInfinity; }, "Tool.HeadLength");
    }

    [Fact]
    public void CylinderHead_IgnoresTheFrustumFields()
    {
        AssertValid(p => { p.Tool.HeadTopDiameter = 0; p.Tool.HeadLength = -1; });
    }

    [Fact]
    public void HeadShape_MustBeKnown()
    {
        AssertSingleError(p => p.Tool.HeadShape = (HeadShape)5, "Tool.HeadShape");
    }

    [Fact]
    public void Stepover_MustBeInZeroToCutterDiameter()
    {
        AssertSingleError(p => p.Parameters.Stepover = 0, "Parameters.Stepover");
        AssertSingleError(p => p.Parameters.Stepover = 6.01f, "Parameters.Stepover");
        AssertValid(p => p.Parameters.Stepover = 6);
    }

    [Fact]
    public void FinishingStepover_MustBeInZeroToCutterDiameter()
    {
        AssertSingleError(p => p.Parameters.FinishingStepover = -0.5f, "Parameters.FinishingStepover");
        AssertSingleError(p => p.Parameters.FinishingStepover = 7, "Parameters.FinishingStepover");
    }

    [Fact]
    public void AxisMapping_MustBeAPermutation()
    {
        AssertSingleError(p => p.Axes.MapY = ModelAxis.X, "Axes.MapX");
        AssertValid(p => { p.Axes.MapX = ModelAxis.Z; p.Axes.MapZ = ModelAxis.X; });
    }

    [Fact]
    public void Stepdown_MustBePositive()
    {
        AssertSingleError(p => p.Parameters.Stepdown = 0, "Parameters.Stepdown");
    }

    [Fact]
    public void SafeHeight_IsAClearanceAboveTheStockTop()
    {
        AssertSingleError(p => p.Parameters.SafeHeight = 0, "Parameters.SafeHeight");
        AssertSingleError(p => p.Parameters.SafeHeight = -2, "Parameters.SafeHeight");

        // The clearance does not depend on where machine zero sits.
        foreach (var mode in Enum.GetValues<OriginMode>())
        {
            AssertValid(p =>
            {
                p.Axes.OriginMode = mode;
                p.Parameters.SafeHeight = 1;
            });
        }
    }

    [Fact]
    public void CellSize_MustBeInRange()
    {
        AssertSingleError(p => p.Parameters.CellSize = 0.005f, "Parameters.CellSize");
        AssertSingleError(p => p.Parameters.CellSize = 5.5f, "Parameters.CellSize");
        AssertValid(p => p.Parameters.CellSize = 5);
    }

    [Fact]
    public void GridSize_MustNotExceedMaxCells()
    {
        // 100 mm / 0.01 mm = 10000 cells per side = 1e8 cells.
        var result = Validate(p => p.Parameters.CellSize = 0.01f);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Parameters.CellSize", error.Field);
        Assert.Contains("4000000", error.Message);

        // 100 mm / 0.05 mm = 2000 per side = 4e6 cells, exactly the limit.
        AssertValid(p => p.Parameters.CellSize = 0.05f);
    }

    [Fact]
    public void FeedRates_MustBePositive()
    {
        AssertSingleError(p => p.Parameters.FeedRate = 0, "Parameters.FeedRate");
        AssertSingleError(p => p.Parameters.PlungeRate = -5, "Parameters.PlungeRate");
        AssertSingleError(p => p.Parameters.RapidRate = 0, "Parameters.RapidRate");
    }

    [Fact]
    public void SpindleRpm_MustBePositive()
    {
        AssertSingleError(p => p.Parameters.SpindleRpm = 0, "Parameters.SpindleRpm");
    }

    [Fact]
    public void BoxStockDimensions_MustBePositive()
    {
        AssertSingleError(p => p.Stock.SizeX = 0, "Stock.SizeX");
        AssertSingleError(p => p.Stock.SizeY = -1, "Stock.SizeY");
        AssertSingleError(p => p.Stock.SizeZ = 0, "Stock.SizeZ");
    }

    [Fact]
    public void CylinderStockDimensions_MustBePositive()
    {
        AssertSingleError(p => { p.Stock.Shape = StockShape.Cylinder; p.Stock.Diameter = 0; }, "Stock.Diameter");
        AssertSingleError(p => { p.Stock.Shape = StockShape.Cylinder; p.Stock.Height = 0; }, "Stock.Height");
        AssertValid(p => { p.Stock.Shape = StockShape.Cylinder; p.Stock.SizeX = 0; });
    }

    [Fact]
    public void Margin_MustNotBeNegative()
    {
        AssertSingleError(p => p.Stock.Margin = -1, "Stock.Margin");
        AssertValid(p => p.Stock.Margin = 0);
    }

    [Fact]
    public void ModelOutsideStock_IsAWarningOnPlacement()
    {
        // The stock box in machine space is 0..100 x 0..100 x 0..30 whatever the model: the explicit
        // origin moves machine zero, not the box.
        var project = MillingProject.Default();
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(-10, -10, -20);

        var inside = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(10, 10, 10)));
        Assert.True(inside.IsValid);
        Assert.Empty(inside.Warnings);

        var outside = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(110, 10, 10)));
        Assert.True(outside.IsValid);
        var warning = Assert.Single(outside.Warnings);
        Assert.Equal("Stock.Placement", warning.Field);
    }

    [Fact]
    public void ModelOutsideAutoFitStock_IsAWarning()
    {
        // Auto-fit centers a 100 x 100 stock on the model; a 120 mm wide model sticks out.
        var project = MillingProject.Default();
        var result = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(120, 10, 10)));
        Assert.True(result.IsValid);
        Assert.Equal("Stock.Placement", Assert.Single(result.Warnings).Field);

        var fits = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(90, 90, 30)));
        Assert.Empty(fits.Warnings);
    }

    [Fact]
    public void ModelOutsideCylinder_IsAWarningEvenInsideItsBoundingBox()
    {
        var project = MillingProject.Default();
        project.Stock.Shape = StockShape.Cylinder;
        project.Stock.Diameter = 100;
        project.Stock.Height = 30;

        // The cylinder is centred at (50, 50) in machine space. 80 x 80 around that centre fits the
        // 100 x 100 bounding box but its corners lie 56.6 mm from the centre.
        var corners = ProjectValidator.Validate(project, new BoundingBox(new Vector3(10, 10, 0), new Vector3(90, 90, 10)));
        Assert.Equal("Stock.Placement", Assert.Single(corners.Warnings).Field);

        // 60 x 60 around the centre has corners 42.4 mm from it, inside the 50 mm radius.
        var fits = ProjectValidator.Validate(project, new BoundingBox(new Vector3(20, 20, 0), new Vector3(80, 80, 10)));
        Assert.Empty(fits.Warnings);
    }

    [Fact]
    public void ValidationException_CarriesResultAndFieldNames()
    {
        var result = Validate(p => p.Parameters.Stepdown = 0);
        var ex = new ValidationException(result);
        Assert.Same(result, ex.Result);
        Assert.Contains("Parameters.Stepdown", ex.Message);
    }

    private static ValidationResult Validate(Action<MillingProject> change)
    {
        var project = MillingProject.Default();
        change(project);
        return ProjectValidator.Validate(project, null);
    }

    private static void AssertValid(Action<MillingProject> change)
    {
        var result = Validate(change);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}")));
    }

    private static void AssertSingleError(Action<MillingProject> change, string field, params string[] alsoExpected)
    {
        var result = Validate(change);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == field);
        var expected = new HashSet<string>(alsoExpected) { field };
        Assert.All(result.Errors, e => Assert.Contains(e.Field, expected));
        Assert.Equal(expected.Count, result.Errors.Count);
    }
}
