using System.Globalization;
using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Slicing;

namespace Miller.Application.Validation;

// Every rule of docs/DEVELOPMENT_GUIDE.md section 6.7, field names as listed there.
public static class ProjectValidator
{
    public const float MinCellSize = 0.01f;
    public const float MaxCellSize = 5f;
    public const long MaxCells = 4_000_000;

    // Above this the viewport upload and draw of the stock grid stop being interactive (T-085);
    // the App layer reads it as HeightMapRenderer.MaxCellsForInteractiveFrame.
    public const long MaxInteractiveCells = 1_000_000;

    public static ValidationResult Validate(MillingProject project, BoundingBox? modelBoundsMachine)
    {
        ArgumentNullException.ThrowIfNull(project);
        var errors = new List<ValidationMessage>();
        var warnings = new List<ValidationMessage>();
        var tool = project.Tool;
        var stock = project.Stock;
        var p = project.Parameters;

        Positive(errors, "Tool.CutterDiameter", tool.CutterDiameter);
        Positive(errors, "Tool.CutterLength", tool.CutterLength);
        if (!(tool.HeadDiameter > tool.CutterDiameter))
        {
            errors.Add(new ValidationMessage("Tool.HeadDiameter",
                $"Head diameter {F(tool.HeadDiameter)} must be larger than the cutter diameter {F(tool.CutterDiameter)}."));
        }

        if (!Enum.IsDefined(tool.HeadShape))
        {
            errors.Add(new ValidationMessage("Tool.HeadShape", $"Unknown head shape {tool.HeadShape}."));
        }
        else if (tool.HeadShape == HeadShape.Frustum)
        {
            if (!(float.IsFinite(tool.HeadTopDiameter) && tool.HeadTopDiameter > tool.CutterDiameter))
            {
                errors.Add(new ValidationMessage("Tool.HeadTopDiameter",
                    $"Head top diameter {F(tool.HeadTopDiameter)} must be larger than the cutter diameter {F(tool.CutterDiameter)}."));
            }

            if (!(float.IsFinite(tool.HeadLength) && tool.HeadLength > 0))
            {
                errors.Add(new ValidationMessage("Tool.HeadLength", $"Head length must be greater than 0, got {F(tool.HeadLength)}."));
            }
        }

        if (!project.Axes.IsPermutation)
        {
            errors.Add(new ValidationMessage("Axes.MapX",
                $"Axis mapping must use each model axis once, got X={project.Axes.MapX}, Y={project.Axes.MapY}, Z={project.Axes.MapZ}."));
        }

        InRange(errors, "Parameters.Stepover", p.Stepover, tool.CutterDiameter);
        InRange(errors, "Parameters.FinishingStepover", p.FinishingStepover, tool.CutterDiameter);
        Positive(errors, "Parameters.Stepdown", p.Stepdown);
        if (p.Stepdown > 0 && p.FarStepdown != 0 && !IsFarStepdown(p.FarStepdown, p.Stepdown))
        {
            errors.Add(new ValidationMessage("Parameters.FarStepdown",
                $"Far stepdown {F(p.FarStepdown)} must be 0 (none) or a whole multiple of the stepdown {F(p.Stepdown)} of at least twice it."));
        }

        var stockSize = AxisSetup.StockBoundingSize(stock);
        Positive(errors, "Parameters.SafeHeight", p.SafeHeight);

        if (!(p.CellSize >= MinCellSize && p.CellSize <= MaxCellSize))
        {
            errors.Add(new ValidationMessage("Parameters.CellSize",
                $"Cell size {F(p.CellSize)} must be between {F(MinCellSize)} and {F(MaxCellSize)}."));
        }
        else
        {
            var cells = (long)MathF.Ceiling(stockSize.X / p.CellSize) * (long)MathF.Ceiling(stockSize.Y / p.CellSize);
            if (cells > MaxCells)
            {
                errors.Add(new ValidationMessage("Parameters.CellSize",
                    $"Cell size {F(p.CellSize)} gives {cells} cells over the stock; the limit is {MaxCells}."));
            }
            else if (cells > MaxInteractiveCells)
            {
                warnings.Add(new ValidationMessage("Parameters.CellSize",
                    $"Cell size {F(p.CellSize)} gives {cells} cells over the stock; above {MaxInteractiveCells} the viewport responds slowly."));
            }
        }

        Positive(errors, "Parameters.FeedRate", p.FeedRate);
        Positive(errors, "Parameters.PlungeRate", p.PlungeRate);
        Positive(errors, "Parameters.RapidRate", p.RapidRate);
        Positive(errors, "Parameters.SpindleRpm", p.SpindleRpm);
        NonNegative(errors, "Strategy.MinIslandVolume", project.MinIslandVolume);
        var bridges = project.BridgeCount;
        if (!(bridges >= 0 && bridges <= MillingProject.MaxBridgeCount && bridges == MathF.Floor(bridges)))
        {
            errors.Add(new ValidationMessage("Strategy.BridgeCount",
                $"Bridges per part must be a whole number from 0 to {MillingProject.MaxBridgeCount}, got {F(bridges)}."));
        }

        if (!(float.IsFinite(project.BridgeWidth) && project.BridgeWidth > 0))
        {
            errors.Add(new ValidationMessage("Strategy.BridgeWidth", $"Bridge width must be a finite number greater than 0, got {F(project.BridgeWidth)}."));
        }
        else if (p.CellSize > 0 && project.BridgeWidth < p.CellSize)
        {
            warnings.Add(new ValidationMessage("Strategy.BridgeWidth",
                $"Bridge width {F(project.BridgeWidth)} is below the cell size {F(p.CellSize)}: every bridge is one cell wide."));
        }

        // Compared with the stock height only when that height is valid; an invalid stock reports itself.
        if (!(float.IsFinite(project.BridgeHeight) && project.BridgeHeight > 0) || (stockSize.Z > 0 && project.BridgeHeight >= stockSize.Z))
        {
            errors.Add(new ValidationMessage("Strategy.BridgeHeight",
                $"Bridge height must be greater than 0 and below the stock height {F(stockSize.Z)}, got {F(project.BridgeHeight)}."));
        }
        if (!Enum.IsDefined(project.CollisionMode))
        {
            errors.Add(new ValidationMessage("Strategy.CollisionMode", $"Unknown collision mode {project.CollisionMode}."));
        }

        Finite(errors, "Strategy.RecursionRatio", project.RecursionRatio);
        Finite(errors, "Strategy.OneRunRatio", project.OneRunRatio);
        if (!(project.ReachPercent > ReachMap.MinPercent && project.ReachPercent <= ReachMap.MaxPercent))
        {
            errors.Add(new ValidationMessage("Strategy.ReachPercent",
                $"Reach percent {F(project.ReachPercent)} must be above {F(ReachMap.MinPercent)} and at most {F(ReachMap.MaxPercent)}."));
        }

        if (stock.Shape == StockShape.Box)
        {
            Positive(errors, "Stock.SizeX", stock.SizeX);
            Positive(errors, "Stock.SizeY", stock.SizeY);
            Positive(errors, "Stock.SizeZ", stock.SizeZ);
        }
        else
        {
            Positive(errors, "Stock.Diameter", stock.Diameter);
            Positive(errors, "Stock.Height", stock.Height);
        }

        if (stock.Margin < 0)
        {
            errors.Add(new ValidationMessage("Stock.Margin", $"Margin {F(stock.Margin)} must not be negative."));
        }

        foreach (var placement in project.Models)
        {
            var s = placement.Scale;
            if (!(float.IsFinite(s.X) && s.X > 0 && float.IsFinite(s.Y) && s.Y > 0 && float.IsFinite(s.Z) && s.Z > 0))
            {
                errors.Add(new ValidationMessage("Models.Scale",
                    $"Scale of {placement.DisplayName} must be a finite factor greater than 0 on every axis, got {F(s.X)}, {F(s.Y)}, {F(s.Z)}."));
            }
        }

        if (modelBoundsMachine is { IsEmpty: false } model && !StockContains(project, model))
        {
            warnings.Add(new ValidationMessage("Stock.Placement",
                $"Model bounds {model.Min} to {model.Max} are not inside the stock."));
        }

        return new ValidationResult(errors, warnings);
    }

    // Model bounds in machine space against the stock box in machine space (ModelLayout.StockBoundsMachine).
    private static bool StockContains(MillingProject project, BoundingBox model)
    {
        var stock = project.Stock;
        var box = ModelLayout.StockBoundsMachine(project);
        if (!box.Contains(model))
        {
            return false;
        }

        if (stock.Shape != StockShape.Cylinder)
        {
            return true;
        }

        var corner = box.Min;
        var size = box.Size;
        var center = new Vector2(corner.X + size.X / 2, corner.Y + size.Y / 2);
        var radius = stock.Diameter / 2;
        foreach (var x in new[] { model.Min.X, model.Max.X })
        {
            foreach (var y in new[] { model.Min.Y, model.Max.Y })
            {
                if (Vector2.Distance(new Vector2(x, y), center) > radius)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A whole multiple of the stepdown of at least twice it, within the level tolerance.
    public static bool IsFarStepdown(float farStepdown, float stepdown)
    {
        if (!(float.IsFinite(farStepdown) && farStepdown > stepdown))
        {
            return false;
        }

        var k = MathF.Round(farStepdown / stepdown);
        return k >= 2 && MathF.Abs(farStepdown - k * stepdown) <= Slicer.LevelTolerance;
    }

    private static void Positive(List<ValidationMessage> errors, string field, float value)
    {
        if (!(value > 0))
        {
            errors.Add(new ValidationMessage(field, $"{field} must be greater than 0, got {F(value)}."));
        }
    }

    private static void NonNegative(List<ValidationMessage> errors, string field, float value)
    {
        if (!(value >= 0))
        {
            errors.Add(new ValidationMessage(field, $"{field} must be 0 or greater, got {F(value)}."));
        }
    }

    private static void Finite(List<ValidationMessage> errors, string field, float value)
    {
        if (!(float.IsFinite(value) && value >= 0))
        {
            errors.Add(new ValidationMessage(field, $"{field} must be a finite number of 0 or greater, got {F(value)}."));
        }
    }

    private static void InRange(List<ValidationMessage> errors, string field, float value, float max)
    {
        if (!(value > 0 && value <= max))
        {
            errors.Add(new ValidationMessage(field, $"{field} must be in (0, {F(max)}], got {F(value)}."));
        }
    }

    private static string F(float value) => value.ToString(CultureInfo.InvariantCulture);
}
