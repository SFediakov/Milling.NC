namespace Miller.Machine.Grbl;

// The XY rectangle a program's moves end in, in millimetres of the work coordinate system, from the
// modal walk of GrblMotion: lines with an X or Y target count, lines whose axis words are no target
// in work coordinates (G53, G10, G92, G28, G30) do not. Arcs count by their end points, so a program
// with arcs can reach slightly beyond the rectangle.
public sealed record GrblBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public static GrblBounds? Of(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var step in GrblMotion.Walk(program))
        {
            if (!step.HasXY)
            {
                continue;
            }

            minX = Math.Min(minX, step.End.X);
            minY = Math.Min(minY, step.End.Y);
            maxX = Math.Max(maxX, step.End.X);
            maxY = Math.Max(maxY, step.End.Y);
        }

        return minX <= maxX ? new GrblBounds(minX, minY, maxX, maxY) : null;
    }
}
