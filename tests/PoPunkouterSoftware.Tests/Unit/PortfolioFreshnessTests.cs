using PoPunkouterSoftware.Shared.Portfolio;

namespace PoPunkouterSoftware.Tests.Unit;

/// <summary>
/// The staleness threshold decides whether a "Live" badge may be shown at all, so the
/// boundary behaviour is contract, not implementation detail.
/// </summary>
public class PortfolioFreshnessTests
{
    private static readonly DateTime Now = new(2026, 07, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MissingGeneratedAt_IsStale()
    {
        // No report can never be trusted as current data.
        PortfolioFreshness.IsStale(null, Now).Should().BeTrue();
    }

    [Fact]
    public void FreshReport_IsNotStale()
    {
        PortfolioFreshness.IsStale(Now.AddHours(-1), Now).Should().BeFalse();
    }

    [Fact]
    public void ExactlyAtThreshold_IsNotStale()
    {
        PortfolioFreshness.IsStale(Now - PortfolioFreshness.StaleAfter, Now).Should().BeFalse();
    }

    [Fact]
    public void JustPastThreshold_IsStale()
    {
        PortfolioFreshness.IsStale(Now - PortfolioFreshness.StaleAfter - TimeSpan.FromSeconds(1), Now).Should().BeTrue();
    }

    [Fact]
    public void TwoWeekOldReport_IsStale()
    {
        // The exact scenario that shipped: a 13-day-old scan rendering green "Live" badges.
        PortfolioFreshness.IsStale(Now.AddDays(-13), Now).Should().BeTrue();
    }
}
