using Heartbeat.Collector.Desktop.Mac.Native;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class MacAccessibilityReadResultTests
{
    [Theory]
    [InlineData(-25205)]
    [InlineData(-25212)]
    public void UnsupportedOrMissingAttributesAreNormalAbsence(int error)
    {
        Assert.False(MacAccessibilityReadResult.HasValue(error, 0));
    }

    [Theory]
    [InlineData(-25204)] // CannotComplete: application does not respond.
    [InlineData(-25211)] // APIDisabled.
    [InlineData(-25202)] // InvalidUIElement.
    public void ReadFailuresRemainFailuresInsteadOfBecomingEmptyTitles(int error)
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            MacAccessibilityReadResult.HasValue(error, 0));
        Assert.Contains(error.ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message);
    }

    [Fact]
    public void SuccessfulReadsDistinguishAValueFromAnAbsentWindow()
    {
        Assert.True(MacAccessibilityReadResult.HasValue(0, 1));
        Assert.False(MacAccessibilityReadResult.HasValue(0, 0));
    }
}
