using System.Globalization;
using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.Io;

public static class StlAsciiParser
{
    private enum State
    {
        ExpectSolid,
        ExpectFacetOrEndSolid,
        ExpectOuterLoop,
        ExpectVertex,
        ExpectEndLoop,
        ExpectEndFacet,
    }

    private static readonly char[] Separators = { ' ', '\t' };

    public static Mesh Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var triangles = new List<Triangle>();
        var corners = new Vector3[3];
        var cornerCount = 0;
        var state = State.ExpectSolid;
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var tokens = rawLine.Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var keyword = tokens[0].ToLowerInvariant();
            switch (state)
            {
                case State.ExpectSolid:
                    Expect(keyword == "solid", lineNumber, "expected 'solid'", rawLine);
                    state = State.ExpectFacetOrEndSolid;
                    break;

                case State.ExpectFacetOrEndSolid:
                    if (keyword == "endsolid")
                    {
                        state = State.ExpectSolid;
                        break;
                    }

                    Expect(keyword == "facet" && tokens.Length == 5 && tokens[1].ToLowerInvariant() == "normal",
                        lineNumber, "expected 'facet normal nx ny nz' or 'endsolid'", rawLine);
                    state = State.ExpectOuterLoop;
                    break;

                case State.ExpectOuterLoop:
                    Expect(keyword == "outer" && tokens.Length == 2 && tokens[1].ToLowerInvariant() == "loop",
                        lineNumber, "expected 'outer loop'", rawLine);
                    cornerCount = 0;
                    state = State.ExpectVertex;
                    break;

                case State.ExpectVertex:
                    Expect(keyword == "vertex" && tokens.Length == 4, lineNumber, "expected 'vertex x y z'", rawLine);
                    corners[cornerCount++] = new Vector3(
                        ParseNumber(tokens[1], lineNumber, rawLine),
                        ParseNumber(tokens[2], lineNumber, rawLine),
                        ParseNumber(tokens[3], lineNumber, rawLine));
                    if (cornerCount == 3)
                    {
                        state = State.ExpectEndLoop;
                    }

                    break;

                case State.ExpectEndLoop:
                    Expect(keyword == "endloop", lineNumber, "expected 'endloop'", rawLine);
                    state = State.ExpectEndFacet;
                    break;

                case State.ExpectEndFacet:
                    Expect(keyword == "endfacet", lineNumber, "expected 'endfacet'", rawLine);
                    triangles.Add(new Triangle(corners[0], corners[1], corners[2]));
                    state = State.ExpectFacetOrEndSolid;
                    break;
            }
        }

        if (state != State.ExpectSolid)
        {
            throw new InvalidDataException($"line {lineNumber}: unexpected end of text, missing 'endsolid'.");
        }

        return new Mesh(triangles);
    }

    private static void Expect(bool condition, int lineNumber, string expectation, string line)
    {
        if (!condition)
        {
            throw new InvalidDataException($"line {lineNumber}: {expectation}, got '{line.Trim()}'.");
        }
    }

    private static float ParseNumber(string token, int lineNumber, string line)
    {
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidDataException($"line {lineNumber}: '{token}' is not a number, in '{line.Trim()}'.");
        }

        return value;
    }
}
