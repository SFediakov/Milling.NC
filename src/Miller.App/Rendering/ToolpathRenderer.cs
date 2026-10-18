using System.Numerics;
using Avalonia.OpenGL;
using Miller.Core.Toolpaths;

namespace Miller.App.Rendering;

// Toolpath as lines colored by move kind. The done part (segments before the progress index) is
// drawn from a bright buffer, the remaining part from a buffer whose colors are mixed towards the
// background, so no blending state is needed. A third buffer holds every segment in the highlight
// color, of which only the next moves after the progress index are drawn. Geometry building is
// static so tests cover it without a GL context.
public sealed class ToolpathRenderer : IDisposable
{
    public const float RemainingMix = 0.5f;
    public const int FloatsPerVertex = 7;

    private readonly VertexBuffer _bright;
    private readonly VertexBuffer _dim;
    private readonly VertexBuffer _next;
    private readonly Vector4 _nextColor;
    private readonly IReadOnlyDictionary<MoveKind, Vector4> _colors;
    private readonly Vector4 _background;

    public ToolpathRenderer(GlInterface gl, IReadOnlyDictionary<MoveKind, Vector4> colors, Vector4 background, Vector4 nextColor)
    {
        _bright = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.ColorLocation, 4));
        _dim = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.ColorLocation, 4));
        _next = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.ColorLocation, 4));
        _nextColor = nextColor;
        _colors = colors ?? throw new ArgumentNullException(nameof(colors));
        _background = background;
    }

    public int SegmentCount { get; private set; }

    public void Upload(IReadOnlyList<ToolpathSegment> segments)
    {
        var (bright, dim) = BuildSegments(segments, _colors, _background);
        SegmentCount = bright.Length / (2 * FloatsPerVertex);
        _bright.Upload(bright, bright.Length, GlConsts.GL_STATIC_DRAW);
        _dim.Upload(dim, dim.Length, GlConsts.GL_STATIC_DRAW);
        var next = BuildHighlight(segments, _nextColor);
        _next.Upload(next, next.Length, GlConsts.GL_STATIC_DRAW);
    }

    // The full path and the next moves are separate options; the highlight is drawn last so it lies
    // over the dimmed remaining part (equal depth passes GL_LEQUAL).
    public void Draw(int progressIndex, bool full, bool nextMoves, int nextCount)
    {
        if (full)
        {
            var (doneVertices, remainingVertices) = Split(progressIndex, SegmentCount);
            _bright.Draw(GlConstants.GL_LINES, 0, doneVertices);
            _dim.Draw(GlConstants.GL_LINES, doneVertices, remainingVertices);
        }

        if (nextMoves)
        {
            var (first, count) = NextRange(progressIndex, SegmentCount, nextCount);
            _next.Draw(GlConstants.GL_LINES, first, count);
        }
    }

    public void Dispose()
    {
        _bright.Dispose();
        _dim.Dispose();
        _next.Dispose();
    }

    public static (float[] Bright, float[] Dim) Build(Toolpath? toolpath, IReadOnlyDictionary<MoveKind, Vector4> colors, Vector4 background)
        => BuildSegments(toolpath?.Segments ?? Array.Empty<ToolpathSegment>(), colors, background);

    // Two vertices per segment: position and color, bright and dimmed variants.
    public static (float[] Bright, float[] Dim) BuildSegments(IReadOnlyList<ToolpathSegment> segments, IReadOnlyDictionary<MoveKind, Vector4> colors, Vector4 background)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return (Array.Empty<float>(), Array.Empty<float>());
        }

        var bright = new float[segments.Count * 2 * FloatsPerVertex];
        var dim = new float[bright.Length];
        var k = 0;
        foreach (var s in segments)
        {
            var color = colors[s.Kind];
            var dimmed = Dim(color, background);
            k = Put(bright, dim, k, s.Start, color, dimmed);
            k = Put(bright, dim, k, s.End, color, dimmed);
        }

        return (bright, dim);
    }

    // Two vertices per segment, every one in the highlight color.
    public static float[] BuildHighlight(IReadOnlyList<ToolpathSegment> segments, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var data = new float[segments.Count * 2 * FloatsPerVertex];
        var k = 0;
        foreach (var s in segments)
        {
            k = Put(data, data, k, s.Start, color, color);
            k = Put(data, data, k, s.End, color, color);
        }

        return data;
    }

    // First vertex and vertex count of the next `nextCount` segments after the progress index.
    public static (int FirstVertex, int VertexCount) NextRange(int progressIndex, int segmentCount, int nextCount)
    {
        var first = Math.Clamp(progressIndex, 0, segmentCount);
        var count = Math.Clamp(nextCount, 0, segmentCount - first);
        return (first * 2, count * 2);
    }

    public static Vector4 Dim(Vector4 color, Vector4 background) => Vector4.Lerp(background, color, RemainingMix) with { W = 1f };

    // Vertex counts of the done and remaining parts for a progress index (segments completed).
    public static (int DoneVertices, int RemainingVertices) Split(int progressIndex, int segmentCount)
    {
        var done = Math.Clamp(progressIndex, 0, segmentCount);
        return (done * 2, (segmentCount - done) * 2);
    }

    private static int Put(float[] bright, float[] dim, int k, Vector3 p, Vector4 color, Vector4 dimmed)
    {
        bright[k] = p.X;
        bright[k + 1] = p.Y;
        bright[k + 2] = p.Z;
        bright[k + 3] = color.X;
        bright[k + 4] = color.Y;
        bright[k + 5] = color.Z;
        bright[k + 6] = color.W;
        dim[k] = p.X;
        dim[k + 1] = p.Y;
        dim[k + 2] = p.Z;
        dim[k + 3] = dimmed.X;
        dim[k + 4] = dimmed.Y;
        dim[k + 5] = dimmed.Z;
        dim[k + 6] = dimmed.W;
        return k + FloatsPerVertex;
    }
}
