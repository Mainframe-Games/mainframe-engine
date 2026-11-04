using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FilterGraphBuilderTests
{
    [Fact]
    public void NumbersAreInvariantCultureWithNoScientificNotation()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0", FilterGraphBuilder.F(0.0000001));
            Assert.Equal("1234567.5", FilterGraphBuilder.F(1234567.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TimesKeepMicrosecondPrecision()
    {
        Assert.Equal("1.123457", FilterGraphBuilder.F(1.123456789));
        Assert.Equal(0.008, FilterGraphBuilder.FadeSeconds);
    }
}
