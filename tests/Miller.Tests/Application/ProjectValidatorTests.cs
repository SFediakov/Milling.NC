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
    }

    [Fact]
    public void CutterDiameter_MustBePositive()
    {
        AssertSingleError(p => p.Tool.CutterDiameter = 0, "Tool.CutterDiameter",
            // stepover rules also fail against a zero cutter diameter
            "Parameters.Stepover", "Parameters.FinishingStepover");
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
        var project = MillingProject.Default();
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(-10, -10, -20);

        var inside = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(10, 10, 10)));
        Assert.True(inside.IsValid);
        Assert.Empty(inside.Warnings);

        var outside = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(100, 10, 10)));
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

        // 80 x 80 fits the 100 x 100 bounding box but its corners lie 56.6 mm from the center.
        var corners = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(80, 80, 10)));
        Assert.Equal("Stock.Placement", Assert.Single(corners.Warnings).Field);

        // 60 x 60 has corners 42.4 mm from the center, inside the 50 mm radius.
        var fits = ProjectValidator.Validate(project, new BoundingBox(Vector3.Zero, new Vector3(60, 60, 10)));
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
