using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Client.Components.Pages;

public class AppsWrapper
{
    public List<AppModel> Apps { get; set; } = new();
}

public class AppModel
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string>? Technologies { get; set; }
    public string? GithubRepo { get; set; }
}

public record GitHubActivity(
    [property: JsonPropertyName("lastCommitDate")] DateTime? LastCommitDate,
    [property: JsonPropertyName("weeklyCommits")] int[]? WeeklyCommits,
    [property: JsonPropertyName("healthScore")] int HealthScore);
