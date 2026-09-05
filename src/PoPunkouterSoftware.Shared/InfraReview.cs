namespace PoPunkouterSoftware.Shared;

/// <summary>
/// The latest GitHub Actions workflow run for one owned repository. Immutable value object.
///
/// <para>Read by exactly one caller: AzureReportService.GitHubCorrelation fetches these for
/// broken App Services only, and DowntimeDiagnosisService turns a match into the "last
/// deploy failed" line on the downtime evidence panel. It is not a general CI/CD review —
/// the file was named InfraReviewModels.cs and carried a section header for a "Downtime
/// root-cause diagnosis" group of records that had been moved out, leaving a heading over
/// nothing and a plural filename over a single type.</para>
/// </summary>
public record InfraReview
{
    public string RepoName { get; init; } = "";
    public string? DefaultBranch { get; init; }
    public string? RepoUrl { get; init; }
    /// <summary>Status of the most recent GitHub Actions workflow run (completed, in_progress, etc.).</summary>
    public string? LatestWorkflowRunStatus { get; init; }
    /// <summary>Conclusion of the most recent GitHub Actions workflow run (success, failure, cancelled).</summary>
    public string? LatestWorkflowRunConclusion { get; init; }
    /// <summary>When the most recent GitHub Actions workflow run completed.</summary>
    public DateTime? LatestWorkflowRunCompletedAt { get; init; }
    /// <summary>URL to the most recent GitHub Actions workflow run.</summary>
    public string? LatestWorkflowRunUrl { get; init; }
    /// <summary>Display title of the most recent workflow run.</summary>
    public string? LatestWorkflowRunName { get; init; }
}
