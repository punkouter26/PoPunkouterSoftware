using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Tests.Unit;

public class ServiceHealthTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("Active", true)]
    [InlineData("ACTIVE", true)]
    [InlineData("broken", false)]
    [InlineData("unreachable", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData("  active  ", false)] // no trimming — the wire format is exact
    [InlineData(null, false)]
    public void IsHealthy_MatchesOnlyActive_CaseInsensitive(string? status, bool expected)
    {
        ServiceHealth.IsHealthy(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("broken", true)]
    [InlineData("BROKEN", true)]
    [InlineData("unreachable", true)]
    [InlineData("Unreachable", true)]
    [InlineData("active", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBroken_MatchesBrokenAndUnreachable_CaseInsensitive(string? status, bool expected)
    {
        ServiceHealth.IsBroken(status).Should().Be(expected);
    }

    [Fact]
    public void UnknownStatus_IsNeitherHealthyNorBroken()
    {
        ServiceHealth.IsHealthy(ServiceHealth.Unknown).Should().BeFalse();
        ServiceHealth.IsBroken(ServiceHealth.Unknown).Should().BeFalse();
    }
}

public class SeverityLevelTests
{
    [Theory]
    [InlineData("critical", 0)]
    [InlineData("CRITICAL", 0)]
    [InlineData("high", 1)]
    [InlineData("High", 1)]
    [InlineData("medium", 2)]
    [InlineData("low", 3)]
    [InlineData("bogus", 4)]
    [InlineData("", 4)]
    [InlineData(null, 4)]
    public void Rank_MapsSeverities_MostSevereFirst(string? severity, int expected)
    {
        SeverityLevel.Rank(severity).Should().Be(expected);
    }

    [Fact]
    public void Rank_OrdersSeveritiesStrictly_UnknownLast()
    {
        var ordered = new[]
        {
            SeverityLevel.Critical, SeverityLevel.High, SeverityLevel.Medium, SeverityLevel.Low, "unrecognized",
        };

        ordered.Select(SeverityLevel.Rank).Should().BeInAscendingOrder()
            .And.OnlyHaveUniqueItems(because: "each severity must occupy its own rank");
    }
}

public class ResourceRiskLevelTests
{
    [Theory]
    [InlineData("cleanup", 0)]
    [InlineData("Cleanup", 0)]
    [InlineData("cost", 1)]
    [InlineData("watch", 2)]
    [InlineData("ok", 3)]
    [InlineData("OK", 3)]
    [InlineData("mystery", 4)]
    [InlineData(null, 4)]
    public void Rank_MapsRiskLevels_MostActionableFirst(string? level, int expected)
    {
        ResourceRiskLevel.Rank(level).Should().Be(expected);
    }

    [Fact]
    public void SortingByRank_PutsCleanupBeforeCostBeforeWatchBeforeOk()
    {
        var shuffled = new[] { "ok", "watch", "cleanup", "cost" };

        var sorted = shuffled.OrderBy(ResourceRiskLevel.Rank).ToArray();

        sorted.Should().Equal("cleanup", "cost", "watch", "ok");
    }
}
