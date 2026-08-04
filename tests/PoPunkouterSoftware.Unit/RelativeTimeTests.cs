using PoPunkouterSoftware.Client;

namespace PoPunkouterSoftware.Unit;

public class RelativeTimeFormatTests
{
    [Fact]
    public void NullTimestamp_ReturnsUnknownAge()
    {
        RelativeTime.Format((DateTime?)null).Should().Be("Unknown age");
    }

    [Fact]
    public void MaxValueTimeSpan_ReturnsUnknownAge()
    {
        RelativeTime.Format(TimeSpan.MaxValue).Should().Be("Unknown age");
    }

    [Theory]
    [InlineData(0, "0m ago")]        // sub-minute floors to 0m
    [InlineData(1, "1m ago")]
    [InlineData(59, "59m ago")]      // last value before the hour boundary
    [InlineData(60, "1h ago")]       // exactly one hour flips to hours
    [InlineData(90, "1h ago")]       // partial hours floor
    [InlineData(23 * 60, "23h ago")] // last value before the day boundary
    [InlineData(24 * 60, "1d ago")]  // exactly one day flips to days
    [InlineData(36 * 60, "1d ago")]  // partial days floor
    [InlineData(72 * 60, "3d ago")]
    [InlineData(45 * 24 * 60, "45d ago")]
    public void TimeSpan_FormatsWithFlooredUnits(int totalMinutes, string expected)
    {
        RelativeTime.Format(TimeSpan.FromMinutes(totalMinutes)).Should().Be(expected);
    }

    [Fact]
    public void TimeSpan_JustUnderBoundary_StaysInSmallerUnit()
    {
        RelativeTime.Format(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59)).Should().Be("59m ago");
        RelativeTime.Format(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)).Should().Be("23h ago");
    }

    [Theory]
    [InlineData(5, "5m ago")]
    [InlineData(120, "2h ago")]
    [InlineData(3 * 24 * 60, "3d ago")]
    public void UtcTimestamp_FormatsAgeAgainstNow(int minutesAgo, string expected)
    {
        // A second of slack keeps the elapsed age strictly inside the expected bucket
        // even if the wall clock ticks between AddMinutes and Format.
        var generated = DateTime.UtcNow.AddMinutes(-minutesAgo).AddSeconds(-1);

        RelativeTime.Format(generated).Should().Be(expected);
    }
}

public class RelativeTimeFormatDetailedTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, "just now")]
    [InlineData(0, 0, 0, 59, "just now")]           // under a minute
    [InlineData(0, 0, 1, 0, "1m ago")]              // exactly one minute leaves "just now"
    [InlineData(0, 0, 59, 0, "59m ago")]
    [InlineData(0, 1, 0, 0, "1h 0m ago")]           // exactly one hour flips to h+m form
    [InlineData(0, 2, 30, 0, "2h 30m ago")]
    [InlineData(0, 23, 59, 0, "23h 59m ago")]
    [InlineData(1, 0, 0, 0, "1d 0h ago")]           // exactly one day flips to d+h form
    [InlineData(3, 2, 15, 0, "3d 2h ago")]          // minutes are dropped in day form
    [InlineData(10, 23, 0, 0, "10d 23h ago")]
    public void FormatDetailed_ProducesExactStrings(int days, int hours, int minutes, int seconds, string expected)
    {
        var age = new TimeSpan(days, hours, minutes, seconds);

        RelativeTime.FormatDetailed(age).Should().Be(expected);
    }

    [Fact]
    public void FormatDetailed_HourForm_UsesRemainderMinutesNotTotal()
    {
        // 1h 5m must render the 5-minute remainder, not the 65 total minutes.
        RelativeTime.FormatDetailed(TimeSpan.FromMinutes(65)).Should().Be("1h 5m ago");
    }
}

