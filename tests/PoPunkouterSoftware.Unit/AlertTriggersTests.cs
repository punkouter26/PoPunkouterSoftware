using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

public class AlertTriggersTests
{
    /// <summary>
    /// Both rules fire on the crossing and stay quiet the night after: a channel that repeats
    /// yesterday's news is a channel that gets muted.
    /// </summary>
    [Fact]
    public void SslAndBudget_AlertOnceWhenCrossed_NotEveryScanAfter()
    {
        var yesterday = new AzureReport
        {
            SslExpiry = [new SslEntry { Name = "a", Url = "https://a", DaysLeft = 15 }, new SslEntry { Name = "b", Url = "https://b", DaysLeft = 9 }],
            Cost = new CostInfo { TotalCost30Days = 30 },
        };
        var today = yesterday with
        {
            SslExpiry =
            [
                new SslEntry { Name = "a", Url = "https://a", DaysLeft = 14 },  // crossed tonight
                new SslEntry { Name = "b", Url = "https://b", DaysLeft = 8 },   // already announced
                new SslEntry { Name = "c", Url = "https://c", DaysLeft = 90 },  // nowhere near
            ],
            Cost = new CostInfo { TotalCost30Days = 45 },
        };

        AlertTriggers.NewSslExpiries(today, yesterday).Select(s => s.Name).Should().Equal("a");
        AlertTriggers.NewSslExpiries(today, today).Should().BeEmpty();

        AlertTriggers.BudgetCrossed(today, yesterday, budgetUsd: 40).Should().NotBeNull()
            .And.Match<CostForecast>(f => f.Verdict == CostVerdict.Over && f.ProjectedMonthTotal == 45);
        AlertTriggers.BudgetCrossed(today, today, budgetUsd: 40).Should().BeNull("it was already over");
        AlertTriggers.BudgetCrossed(today, yesterday, budgetUsd: null).Should().BeNull("no budget, no verdict");
    }
}
