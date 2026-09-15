using System.Text;
using Miller.Core.Geometry;

namespace Miller.Core.Io;

public static class StlReader
{
    public static (Mesh Mesh, StlImportReport Report) Read(string path)
        => Read(File.ReadAllBytes(path), path);

    // Binary is decided by the size rule alone; a header starting with "solid" proves nothing.
    // Degenerate facets are dropped from the mesh and counted in the report.
    public static (Mesh Mesh, StlImportReport Report) Read(byte[] data, string pathForReport)
    {
        ArgumentNullException.ThrowIfNull(data);
        var format = StlBinaryParser.MatchesSizeRule(data) ? StlFormat.Binary : StlFormat.Ascii;
        var raw = format == StlFormat.Binary
            ? StlBinaryParser.Parse(data)
            : StlAsciiParser.Parse(Encoding.UTF8.GetString(data));
        var mesh = raw.RemoveDegenerate();
        var report = new StlImportReport(
            format,
            mesh.TriangleCount,
            raw.TriangleCount - mesh.TriangleCount,
            mesh.Bounds,
            pathForReport);
        return (mesh, report);
    }
}
