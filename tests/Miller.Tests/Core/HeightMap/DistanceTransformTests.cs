using Miller.Core.HeightMaps;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class DistanceTransformTests
{
    private static float[,] BruteForce(bool[,] mask, float cellSize)
    {
        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var result = new float[width, height];
        for (var i = 0; i < width; i++)
        {
            for (var j = 0; j < height; j++)
            {
                var best = float.PositiveInfinity;
                for (var a = 0; a < width; a++)
                {
                    for (var b = 0; b < height; b++)
                    {
                        if (mask[a, b])
                        {
                            best = MathF.Min(best, MathF.Sqrt((i - a) * (i - a) + (j - b) * (j - b)) * cellSize);
                        }
                    }
                }

                result[i, j] = best;
            }
        }

        return result;
    }

    [Fact]
    public void MatchesBruteForce_OnRandomMasks()
    {
        var random = new Random(7);
        for (var round = 0; round < 5; round++)
        {
            var mask = new bool[37, 23];
            for (var i = 0; i < 37; i++)
            {
                for (var j = 0; j < 23; j++)
                {
                    mask[i, j] = random.NextDouble() < 0.04;
                }
            }

            var expected = BruteForce(mask, 0.5f);
            var actual = DistanceTransform.Distances(mask, 0.5f);
            for (var i = 0; i < 37; i++)
            {
                for (var j = 0; j < 23; j++)
                {
                    Assert.Equal(expected[i, j], actual[i, j], 4);
                }
            }
        }
    }

    [Fact]
    public void SingleCell_GivesEuclideanDistances_AndEmptyMaskGivesInfinity()
    {
        var mask = new bool[9, 9];
        mask[4, 4] = true;
        var distances = DistanceTransform.Distances(mask, 2f);
        Assert.Equal(0f, distances[4, 4]);
        Assert.Equal(2f, distances[5, 4]);
        Assert.Equal(2f * MathF.Sqrt(2), distances[3, 3], 5);
        Assert.Equal(2f * 5f, distances[0, 1], 5);

        var empty = DistanceTransform.Distances(new bool[5, 4], 1f);
        Assert.All(empty.Cast<float>(), d => Assert.Equal(float.PositiveInfinity, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => DistanceTransform.Distances(mask, 0f));
    }

    [Fact]
    public void FullFirstColumn_AndLastRow_AreHandled()
    {
        var mask = new bool[6, 5];
        for (var j = 0; j < 5; j++)
        {
            mask[0, j] = true;
        }

        for (var i = 0; i < 6; i++)
        {
            mask[i, 4] = true;
        }

        var distances = DistanceTransform.Distances(mask, 1f);
        Assert.Equal(0f, distances[0, 2]);
        Assert.Equal(0f, distances[5, 4]);
        Assert.Equal(1f, distances[5, 3]);
        Assert.Equal(3f, distances[3, 0]);
        Assert.Equal(4f, distances[5, 0]);
    }
}
