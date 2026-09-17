using System.Globalization;
using Miller.Core.GCode;
using Xunit;

namespace Miller.Tests.Core.GCode;

public sealed class GCodeFormatterTests
{
    [Theory]
    [InlineData(12.5f, "12.5")]
    [InlineData(0f, "0")]
    [InlineData(-3.125f, "-3.125")]
    [InlineData(0.0004f, "0")]
    [InlineData(-0.0004f, "0")]
    [InlineData(100f, "100")]
    [InlineData(1.2345f, "1.235")]
    [InlineData(2.0005f, "2.001")]
    [InlineData(-7.10f, "-7.1")]
    public void Format_UsesThreeDecimalsAndTrims(float value, string expected)
    {
        Assert.Equal(expected, GCodeFormatter.Format(value));
    }

    [Fact]
    public void Word_PrefixesTheLetter()
    {
        Assert.Equal("X12.5", GCodeFormatter.Word('X', 12.5f));
        Assert.Equal("F800", GCodeFormatter.Word('F', 800f));
    }

    [Fact]
    public void Format_IgnoresTheThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // InvariantGlobalization is on, so named cultures do not exist; a clone with a comma separator stands in.
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("12,5", 12.5f.ToString(comma));
            Assert.Equal("12.5", GCodeFormatter.Format(12.5f));
            Assert.Equal("-0.25", GCodeFormatter.Format(-0.25f));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_RejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentException>(() => GCodeFormatter.Format(float.NaN));
        Assert.Throws<ArgumentException>(() => GCodeFormatter.Format(float.PositiveInfinity));
    }
}
