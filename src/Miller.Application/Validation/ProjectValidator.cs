using System.Globalization;
using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;

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

        if (!project.Axes.IsPermutation)
        {
            errors.Add(new ValidationMessage("Axes.MapX",
                $"Axis mapping must use each model axis once, got X={project.Axes.MapX}, Y={project.Axes.MapY}, Z={project.Axes.MapZ}."));
        }

        InRange(errors, "Parameters.Stepover", p.Stepover, tool.CutterDiameter);
        InRange(errors, "Parameters.FinishingStepover", p.FinishingStepover, tool.CutterDiameter);
        Positive(errors, "Parameters.Stepdown", p.Stepdown);

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

        if (modelBoundsMachine is { IsEmpty: false } model && !StockContains(stock, model))
        {
            warnings.Add(new ValidationMessage("Stock.Placement",
                $"Model bounds {model.Min} to {model.Max} are not inside the stock."));
        }

        return new ValidationResult(errors, warnings);
    }

    private static bool StockContains(StockDefinition stock, BoundingBox model)
    {
        var corner = AxisSetup.StockCorner(model, stock);
        var size = AxisSetup.StockBoundingSize(stock);
        var box = new BoundingBox(corner, corner + size);
        if (!box.Contains(model))
        {
            return false;
        }

        if (stock.Shape != StockShape.Cylinder)
        {
            return true;
        }

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

    private static void Positive(List<ValidationMessage> errors, string field, float value)
    {
        if (!(value > 0))
        {
            errors.Add(new ValidationMessage(field, $"{field} must be greater than 0, got {F(value)}."));
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
