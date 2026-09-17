using System.Buffers.Binary;
using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.Io;

public static class StlBinaryParser
{
    public const int HeaderLength = 80;
    public const int CountLength = 4;
    public const int PrefixLength = HeaderLength + CountLength;
    public const int RecordLength = 50;
    private const int VectorLength = 12;

    // The header text is not a format indicator (binary files may start with "solid"); only the
    // size rule decides.
    public static bool MatchesSizeRule(ReadOnlySpan<byte> data)
        => data.Length >= PrefixLength && data.Length == ExpectedLength(ReadCount(data));

    public static Mesh Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < PrefixLength)
        {
            throw new InvalidDataException(
                $"Binary STL needs at least {PrefixLength} bytes, got {data.Length}.");
        }

        var count = ReadCount(data);
        var expected = ExpectedLength(count);
        if (data.Length != expected)
        {
            throw new InvalidDataException(
                $"Binary STL size rule failed: {count} triangles need {expected} bytes, file has {data.Length}.");
        }

        var triangles = new Triangle[count];
        var offset = PrefixLength;
        for (var i = 0; i < triangles.Length; i++)
        {
            var record = data.Slice(offset, RecordLength);
            triangles[i] = new Triangle(
                ReadVector(record.Slice(VectorLength, VectorLength)),
                ReadVector(record.Slice(2 * VectorLength, VectorLength)),
                ReadVector(record.Slice(3 * VectorLength, VectorLength)));
            offset += RecordLength;
        }

        return new Mesh(triangles);
    }

    private static uint ReadCount(ReadOnlySpan<byte> data)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(HeaderLength, CountLength));

    private static long ExpectedLength(uint count)
        => PrefixLength + (long)RecordLength * count;

    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes)
        => new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(4)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(8)));
}
