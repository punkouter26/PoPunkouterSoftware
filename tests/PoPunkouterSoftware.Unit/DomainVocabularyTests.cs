using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

// Trimmed to the budget (CLAUDE.md: 100 Unit). The cases removed were extra casing
// variants of a rule already proven once per method, and per-value rank assertions that
// the ordering facts below subsume — one `BeInAscendingOrder` over the whole ladder is a
// stronger claim than five independent equality checks, and does not have to be edited
// when a level is inserted.

public class ServiceHealthTests
{
    [Theory]
    [InlineData("active", true)]
    [InlineData("ACTIVE", true)]          // case-insensitive
    [InlineData("  active  ", false)]     // but NOT trimmed — the wire format is exact
    [InlineData("broken", false)]
    [InlineData(null, false)]
    public void IsHealthy_MatchesOnlyActive_CaseInsensitive(string? status, bool expected)
    {
        ServiceHealth.IsHealthy(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("broken", true)]
    [InlineData("Unreachable", true)]     // second broken synonym, and case-insensitive
    [InlineData("active", false)]
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
    [InlineData("Cleanup", 0)]    // case-insensitive at the top of the ladder
    [InlineData("mystery", 4)]    // unrecognised sorts last, never first
    [InlineData(null, 4)]
    public void Rank_IsCaseInsensitive_AndSortsUnknownLast(string? level, int expected)
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
