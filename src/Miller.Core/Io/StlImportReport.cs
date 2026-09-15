using Miller.Core.Geometry;

namespace Miller.Core.Io;

public enum StlFormat
{
    Binary,
    Ascii,
}

public sealed record StlImportReport(
    StlFormat Format,
    int TriangleCount,
    int DegenerateCount,
    BoundingBox Bounds,
    string Path);
