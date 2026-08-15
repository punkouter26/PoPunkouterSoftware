namespace PoPunkouterSoftware.Shared;

// CI/CD and infrastructure-as-code review results, one per owned GitHub repository.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

/// <summary>Infrastructure and CI/CD review result for one GitHub repository.</summary>
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

// ─── Downtime root-cause diagnosis ───────────────────────────────────────────
