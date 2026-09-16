using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class ConPtyImplementationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("app-local", false)]
    [InlineData("oob", false)]
    [InlineData("system", true)]
    [InlineData("inbox", true)]
    public void Parse_maps_supported_names(string? value, bool expectSystem)
    {
        Assert.Equal(
            expectSystem,
            ConPtySelector.Parse(value) == ConPtyImplementation.System);
    }

    [Fact]
    public void Parse_rejects_unknown_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConPtySelector.Parse("unknown"));

        Assert.Contains("MAGPILOT_CONPTY", ex.Message);
    }
}
