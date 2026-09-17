using System.Globalization;
using System.Numerics;
using System.Text;
using Miller.Core.Geometry;

namespace Miller.Tests.Fixtures;

// Every builder returns a closed, consistently oriented mesh (outward normals); MeshTests verifies it.
public static class TestMeshes
{
    public const string FixtureFileName = "Milling_Heart_V2.STL";
    public const float SlottedPlateSize = 20f;
    public const float SlottedPlateHeight = 12f;
    public const float SlotWidth = 4f;
    public const float SlotDepth = 10f;
    public const float BumpPlateSize = 20f;
    public const float BumpPlateThickness = 5f;
    public const float BumpRadius = 5f;
    public const float BumpGridStep = 0.5f;
    public const float SpikeBase = 10f;
    public const float SpikeHeight = 10f;

    public static Mesh UnitCube() => Box(1, 1, 1);

    // Axis-aligned box with its minimum corner at the origin.
    public static Mesh Box(float sizeX, float sizeY, float sizeZ)
    {
        var p = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            p[i] = new Vector3((i & 1) == 0 ? 0 : sizeX, (i & 2) == 0 ? 0 : sizeY, (i & 4) == 0 ? 0 : sizeZ);
        }

        var t = new List<Triangle>(12);
        Quad(t, p[0], p[2], p[3], p[1]); // bottom, -Z
        Quad(t, p[4], p[5], p[7], p[6]); // top, +Z
        Quad(t, p[0], p[1], p[5], p[4]); // front, -Y
        Quad(t, p[2], p[6], p[7], p[3]); // back, +Y
        Quad(t, p[0], p[4], p[6], p[2]); // left, -X
        Quad(t, p[1], p[3], p[7], p[5]); // right, +X
        return new Mesh(t);
    }

    // Square pyramid: base SpikeBase x SpikeBase at z = 0, apex above the base center.
    public static Mesh Spike()
    {
        var b0 = new Vector3(0, 0, 0);
        var b1 = new Vector3(SpikeBase, 0, 0);
        var b2 = new Vector3(SpikeBase, SpikeBase, 0);
        var b3 = new Vector3(0, SpikeBase, 0);
        var apex = new Vector3(SpikeBase / 2, SpikeBase / 2, SpikeHeight);
        var t = new List<Triangle>(6);
        Quad(t, b0, b3, b2, b1);
        t.Add(new Triangle(b0, b1, apex));
        t.Add(new Triangle(b1, b2, apex));
        t.Add(new Triangle(b2, b3, apex));
        t.Add(new Triangle(b3, b0, apex));
        return new Mesh(t);
    }

    // 20 x 20 plate, 12 high, with a through slot along Y: 4 wide (x in [8, 12]) and 10 deep, so the
    // slot floor is at z = 2. Built as a U profile in the XZ plane extruded along Y.
    public static Mesh SlottedPlate()
    {
        var left = (SlottedPlateSize - SlotWidth) / 2;
        var right = left + SlotWidth;
        var floor = SlottedPlateHeight - SlotDepth;
        var profile = new[]
        {
            new Vector2(0, 0),
            new Vector2(SlottedPlateSize, 0),
            new Vector2(SlottedPlateSize, SlottedPlateHeight),
            new Vector2(right, SlottedPlateHeight),
            new Vector2(right, floor),
            new Vector2(left, floor),
            new Vector2(left, SlottedPlateHeight),
            new Vector2(0, SlottedPlateHeight),
        };
        // Cap triangulation by profile vertex index; every diagonal is used by exactly two triangles.
        var caps = new[]
        {
            (0, 5, 7), (7, 5, 6), (0, 1, 5), (5, 1, 4), (4, 1, 2), (4, 2, 3),
        };
        return ExtrudeAlongY(profile, caps, 0, SlottedPlateSize);
    }

    // 20 x 20 plate, 5 thick, with a hemisphere of radius 5 centered on top. The top surface is a
    // heightfield on a 0.5 mm grid; the bottom uses the same grid so the walls close exactly.
    public static Mesh BumpPlate()
    {
        var n = (int)MathF.Round(BumpPlateSize / BumpGridStep);
        var center = BumpPlateSize / 2;
        var top = new Vector3[n + 1, n + 1];
        var bottom = new Vector3[n + 1, n + 1];
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= n; j++)
            {
                var x = i * BumpGridStep;
                var y = j * BumpGridStep;
                var dx = x - center;
                var dy = y - center;
                var d2 = dx * dx + dy * dy;
                var bump = d2 < BumpRadius * BumpRadius ? MathF.Sqrt(BumpRadius * BumpRadius - d2) : 0f;
                top[i, j] = new Vector3(x, y, BumpPlateThickness + bump);
                bottom[i, j] = new Vector3(x, y, 0);
            }
        }

        var t = new List<Triangle>(4 * n * n + 8 * n);
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                Quad(t, top[i, j], top[i + 1, j], top[i + 1, j + 1], top[i, j + 1]);
                Quad(t, bottom[i, j], bottom[i, j + 1], bottom[i + 1, j + 1], bottom[i + 1, j]);
            }
        }

        for (var k = 0; k < n; k++)
        {
            Quad(t, bottom[k, 0], bottom[k + 1, 0], top[k + 1, 0], top[k, 0]);         // -Y wall
            Quad(t, bottom[k + 1, n], bottom[k, n], top[k, n], top[k + 1, n]);         // +Y wall
            Quad(t, bottom[0, k + 1], bottom[0, k], top[0, k], top[0, k + 1]);         // -X wall
            Quad(t, bottom[n, k], bottom[n, k + 1], top[n, k + 1], top[n, k]);         // +X wall
        }

        return new Mesh(t);
    }

    public static string AsciiCubeText()
    {
        var sb = new StringBuilder();
        sb.Append("solid cube\n");
        foreach (var t in UnitCube().Triangles)
        {
            sb.Append("  facet normal ").Append(Format(t.Normal)).Append('\n');
            sb.Append("    outer loop\n");
            sb.Append("      vertex ").Append(Format(t.A)).Append('\n');
            sb.Append("      vertex ").Append(Format(t.B)).Append('\n');
            sb.Append("      vertex ").Append(Format(t.C)).Append('\n');
            sb.Append("    endloop\n");
            sb.Append("  endfacet\n");
        }

        sb.Append("endsolid cube\n");
        return sb.ToString();
    }

    // The test assembly runs from tests/Miller.Tests/bin/<Configuration>/net10.0/.
    public static string FixturePath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", FixtureFileName));

    // Closed and consistently oriented: every directed edge occurs exactly once and its reverse exists.
    public static bool IsClosed(Mesh mesh)
    {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        foreach (var t in mesh.Triangles)
        {
            Count(edges, t.A, t.B);
            Count(edges, t.B, t.C);
            Count(edges, t.C, t.A);
        }

        foreach (var ((from, to), count) in edges)
        {
            if (count != 1 || !edges.TryGetValue((to, from), out var reverse) || reverse != 1)
            {
                return false;
            }
        }

        return true;
    }

    public static int EdgeCount(Mesh mesh)
    {
        var edges = new HashSet<(Vector3, Vector3)>();
        foreach (var t in mesh.Triangles)
        {
            edges.Add(Undirected(t.A, t.B));
            edges.Add(Undirected(t.B, t.C));
            edges.Add(Undirected(t.C, t.A));
        }

        return edges.Count;
    }

    private static void Count(Dictionary<(Vector3, Vector3), int> edges, Vector3 from, Vector3 to)
    {
        edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
    }

    private static (Vector3, Vector3) Undirected(Vector3 a, Vector3 b)
    {
        var aFirst = a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z <= b.Z;
        return aFirst ? (a, b) : (b, a);
    }

    // Counter-clockwise seen from outside: a, b, c, d.
    private static void Quad(List<Triangle> target, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        target.Add(new Triangle(a, b, c));
        target.Add(new Triangle(a, c, d));
    }

    // Profile is counter-clockwise in the XZ plane seen from -Y (x right, z up). The -Y cap uses the
    // cap triangles as given, the +Y cap reversed, and every profile edge becomes an outward wall.
    private static Mesh ExtrudeAlongY(Vector2[] profile, (int, int, int)[] caps, float yFrom, float yTo)
    {
        var near = profile.Select(v => new Vector3(v.X, yFrom, v.Y)).ToArray();
        var far = profile.Select(v => new Vector3(v.X, yTo, v.Y)).ToArray();
        var t = new List<Triangle>();
        foreach (var (a, b, c) in caps)
        {
            t.Add(new Triangle(near[a], near[b], near[c]));
            t.Add(new Triangle(far[a], far[c], far[b]));
        }

        for (var i = 0; i < profile.Length; i++)
        {
            var j = (i + 1) % profile.Length;
            Quad(t, near[j], near[i], far[i], far[j]);
        }

        return new Mesh(t);
    }

    private static string Format(Vector3 v)
        => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.######} {v.Y:0.######} {v.Z:0.######}");
}
