using Callsign.Core.World;
using Xunit;

namespace Callsign.Core.Tests;

public class GameCalendarTests
{
    private static DateTimeOffset On(int month) => new(2026, month, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, "Winter")]
    [InlineData(4, "Spring")]
    [InlineData(7, "Summer")]
    [InlineData(10, "Autumn")]
    public void Season_NorthernHemisphere(int month, string expected)
        => Assert.Equal(expected, GameCalendar.Season(On(month), 52)); // Amsterdam-ish

    [Theory]
    [InlineData(1, "Summer")]
    [InlineData(4, "Autumn")]
    [InlineData(7, "Winter")]
    [InlineData(10, "Spring")]
    public void Season_SouthernHemisphere_IsFlipped(int month, string expected)
        => Assert.Equal(expected, GameCalendar.Season(On(month), -33)); // Sydney-ish

    [Fact]
    public void CareerDays_CountsWholeDays_NeverNegative()
    {
        var founded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, GameCalendar.CareerDays(founded, founded.AddHours(5)));   // same day
        Assert.Equal(47, GameCalendar.CareerDays(founded, founded.AddDays(47).AddHours(3)));
        Assert.Equal(0, GameCalendar.CareerDays(founded, founded.AddDays(-2)));   // clock skew never goes negative
    }
}
