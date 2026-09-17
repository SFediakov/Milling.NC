using Miller.Core.HeightMaps;

namespace Miller.Core.Analysis;

public sealed record AnalysisResult(
    DeviationMap Map,
    HeightMap FinalStock,
    int OkCells,
    int RestCells,
    int GougeCells,
    int NoModelCells,
    float RestVolume,
    float GougeVolume,
    float CellArea)
{
    public float RestArea => RestCells * CellArea;

    public float GougeArea => GougeCells * CellArea;
}

// Classifies every cell of the final stock against the model (guide 6.3): where the model exists
// above the floor, |stock - model| within the tolerance is Ok, more stock is RestMaterial (the tool
// could not reach), less is Gouge (a strategy bug); cells with the model at the floor are NoModel.
// Stock cut away completely (NaN) counts as the floor.
public static class FinalModelAnalyzer
{
    public const float FloorTolerance = 1e-4f;

    public static AnalysisResult Analyze(HeightMap finalStock, HeightMap model, float floor, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(finalStock);
        ArgumentNullException.ThrowIfNull(model);
        if (!finalStock.SameGridAs(model))
        {
            throw new ArgumentException("Final stock and model must share the same grid.", nameof(finalStock));
        }

        if (!(tolerance >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must not be negative.");
        }

        var values = new HeightMap(model.OriginX, model.OriginY, model.CellSize, model.Width, model.Height, float.NaN);
        var categories = new CellCategory[model.CellCount];
        var area = model.CellSize * model.CellSize;
        int ok = 0, rest = 0, gouge = 0, none = 0;
        float restVolume = 0, gougeVolume = 0;
        for (var k = 0; k < model.CellCount; k++)
        {
            var m = model.Z[k];
            if (float.IsNaN(m) || m <= floor + FloorTolerance)
            {
                categories[k] = CellCategory.NoModel;
                none++;
                continue;
            }

            var s = finalStock.Z[k];
            var stockHeight = float.IsNaN(s) ? floor : s;
            var deviation = stockHeight - m;
            values.Z[k] = deviation;
            if (MathF.Abs(deviation) <= tolerance)
            {
                categories[k] = CellCategory.Ok;
                ok++;
            }
            else if (deviation > 0)
            {
                categories[k] = CellCategory.RestMaterial;
                rest++;
                restVolume += deviation * area;
            }
            else
            {
                categories[k] = CellCategory.Gouge;
                gouge++;
                gougeVolume += -deviation * area;
            }
        }

        return new AnalysisResult(new DeviationMap(values, categories), finalStock, ok, rest, gouge, none, restVolume, gougeVolume, area);
    }
}
