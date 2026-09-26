using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.Unit;

public class AppScreenshotServiceTests
{
    private static AppScreenshotService Service(bool enabled) => new(
        NullLogger<AppScreenshotService>.Instance,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureFlags:EnableScreenshots"] = enabled.ToString() })
            .Build());

    [Fact]
    public void DueForCapture_PicksMissingAndWeekOldHostsOnly_AndNothingWhenDisabled()
    {
        var now = DateTimeOffset.UtcNow;
        var targets = new List<(string Host, string Url)>
        {
            ("missing.example", "https://missing.example"),
            ("old.example", "https://old.example"),
            ("fresh.example", "https://fresh.example"),
        };
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["old.example"] = now.AddDays(-8).ToUnixTimeSeconds(),
            ["fresh.example"] = now.AddDays(-6).ToUnixTimeSeconds(),
        };

        Service(enabled: true).DueForCapture(targets, versions)
            .Select(t => t.Host).Should().BeEquivalentTo("missing.example", "old.example");
        Service(enabled: false).DueForCapture(targets, versions).Should().BeEmpty();
    }
}
