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

public class RepoIdTests
{
    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("a/b", "a", "b")]
    [InlineData("my-org/My.Repo-123", "my-org", "My.Repo-123")]
    [InlineData("A_a.9-x/B_b.8-y", "A_a.9-x", "B_b.8-y")]
    [InlineData("1234/5678", "1234", "5678")]
    public void TryParse_ValidForms_SplitsOwnerAndName(string input, string owner, string name)
    {
        RepoId.TryParse(input, out var id).Should().BeTrue();

        id.Owner.Should().Be(owner);
        id.Name.Should().Be(name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-slash")]
    [InlineData("a/b/c")]
    [InlineData("has space/repo")]
    [InlineData("owner/has space")]
    [InlineData("<script>/xss")]
    [InlineData("../../traversal")]
    [InlineData("/repo")]
    [InlineData("owner/")]
    [InlineData("/")]
    public void TryParse_InvalidForms_ReturnsFalseWithDefaultResult(string? input)
    {
        RepoId.TryParse(input, out var id).Should().BeFalse();

        id.Should().Be(default(RepoId), because: "a failed parse must not leak partial state");
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("my-org/My.Repo-123")]
    public void ToString_RoundTripsThroughTryParse(string input)
    {
        RepoId.TryParse(input, out var first).Should().BeTrue();

        first.ToString().Should().Be(input);
        RepoId.TryParse(first.ToString(), out var second).Should().BeTrue();
        second.Should().Be(first);
    }

    [Fact]
    public void ToString_ComposesOwnerSlashName()
    {
        new RepoId("punkouter26", "PoThing").ToString().Should().Be("punkouter26/PoThing");
    }
}
