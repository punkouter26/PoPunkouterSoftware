using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class OutageTrackerTests
{
    /// <summary>
    /// One service's life in one sequence, because the debounce is a property of the sequence:
    /// a single failure never pages, a timeout neither counts nor resets, the page fires once,
    /// and "recovered" is only ever sent for an outage that was announced.
    /// </summary>
    [Fact]
    public void AlertsOnceAfterConsecutiveFailures_AndRecoversOnlyWhatItAnnounced()
    {
        var tracker = new OutageTracker(failuresBeforeAlert: 2);

        tracker.Observe("PoX", false).Should().BeNull(because: "one failed probe is a blip");
        tracker.Observe("PoX", null).Should().BeNull(because: "a timeout is a cold start, not evidence");
        tracker.Observe("PoX", false).Should().Be(OutageTransition.WentDown);
        tracker.Observe("PoX", false).Should().BeNull(because: "still down is not news");
        tracker.Observe("pox", true).Should().Be(OutageTransition.Recovered, because: "names are case-insensitive");
        tracker.Observe("PoX", true).Should().BeNull();

        tracker.Observe("PoY", false).Should().BeNull();
        tracker.Observe("PoY", true).Should().BeNull(because: "nothing was announced, so nothing recovered");
    }
}
