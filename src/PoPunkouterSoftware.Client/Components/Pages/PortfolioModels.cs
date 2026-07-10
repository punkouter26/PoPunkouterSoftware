using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Client.Components.Pages;

public record GitHubActivity(
    [property: JsonPropertyName("lastCommitDate")] DateTime? LastCommitDate,
    [property: JsonPropertyName("weeklyCommits")] int[]? WeeklyCommits,
    [property: JsonPropertyName("healthScore")] int HealthScore);
