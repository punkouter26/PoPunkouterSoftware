namespace PoPunkouterSoftware.Shared;

// CI/CD and infrastructure-as-code review results, one per owned GitHub repository.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

/// <summary>Infrastructure and CI/CD review result for one GitHub repository.</summary>
public record InfraReview
{
    public string RepoName { get; init; } = "";
    public string? DefaultBranch { get; init; }
    public bool IsPrivate { get; init; }
    public string? RepoUrl { get; init; }
    /// <summary>Inferred primary hosting target: App Service, Static Web Apps, Container Apps, Functions, Unknown.</summary>
    public string DeploymentTarget { get; init; } = "Unknown";
    /// <summary>Inferred deploy method: GitHub Actions, Manual, Unknown.</summary>
    public string DeploymentMethod { get; init; } = "Unknown";
    public List<CiCdFileSummary> CiCdFiles { get; init; } = new();
    public List<InfraFileSummary> InfraFiles { get; init; } = new();
    public DateTime ScannedAt { get; init; }
    /// <summary>Non-null when scanning failed for this repo (permissions, rate limit, etc.).</summary>
    public string? Error { get; init; }
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

/// <summary>Summary of a single GitHub Actions workflow file.</summary>
public record CiCdFileSummary
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    /// <summary>Workflow triggers extracted from the `on:` key, e.g. push, pull_request, workflow_dispatch.</summary>
    public List<string> Triggers { get; init; } = new();
    /// <summary>Azure deploy action ids found in the workflow, e.g. azure/webapps-deploy.</summary>
    public List<string> DeployActions { get; init; } = new();
    /// <summary>Branch filters extracted from push/pull_request triggers.</summary>
    public List<string> BranchFilters { get; init; } = new();
}

/// <summary>Summary of a single infrastructure definition file (Bicep, ARM, Docker, Azure Developer CLI).</summary>
public record InfraFileSummary
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    /// <summary>bicep | arm | docker | azd | compose</summary>
    public string FileType { get; init; } = "";
    /// <summary>Resource type strings extracted from Bicep files, e.g. Microsoft.Web/sites.</summary>
    public List<string> ResourceTypes { get; init; } = new();
}

// ─── Downtime root-cause diagnosis ───────────────────────────────────────────
