using System.Text.Json;
using FluentAssertions;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

public class DowntimeDiagnosisServiceTests
{
    private static (string? State, string? Summary, bool? Caused) Read(string json) =>
        DowntimeDiagnosisService.ReadPlatformHealth(JsonDocument.Parse(json).RootElement);

    /// <summary>
    /// "Is it me or Azure?" — the three answers Resource Health can give, and the attention
    /// sentence that carries the answer to the first paint.
    /// </summary>
    [Fact]
    public void PlatformHealth_SeparatesAzureFaultsFromAppFaults_AndTheSentenceSaysWhich()
    {
        Read("""{"properties":{"availabilityState":"Unavailable","reasonType":"Unplanned","summary":"Platform issue"}}""")
            .Should().Be(("Unavailable", "Platform issue", true));
        Read("""{"properties":{"availabilityState":"Available"}}""").Caused.Should().BeFalse();
        Read("""{"properties":{"availabilityState":"Unavailable","reasonType":"UserInitiated"}}""").Caused.Should().BeFalse();
        Read("""{"properties":{"availabilityState":"Unknown"}}""").Caused.Should().BeNull();
        Read("""{}""").Should().Be(((string?)null, (string?)null, (bool?)null));

        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services =
                [
                    new WebService { Name = "app-a", FriendlyName = "A", HttpStatus = ServiceHealth.Broken },
                    new WebService { Name = "app-b", FriendlyName = "B", HttpStatus = ServiceHealth.Broken },
                    new WebService { Name = "app-c", FriendlyName = "C", HttpStatus = ServiceHealth.Broken },
                ],
            },
            DowntimeDiagnoses =
            [
                new ServiceDowntimeDiagnosis { Name = "app-a", PlatformCaused = true },
                new ServiceDowntimeDiagnosis { Name = "app-b", PlatformCaused = false },
            ],
        };

        AttentionItemsBuilder.Build(report, _ => false).AttentionItems.Should().StartWith(
        [
            "A is unavailable — Azure platform event",
            "B is unavailable — app-side, Azure reports the platform healthy",
            "C is unavailable",
        ]);
    }
}
