namespace PoPunkouterSoftware.Shared.Azure;

// Root-cause diagnosis for an unreachable service, with the deployment and activity-log evidence behind it.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

/// <summary>
/// Per-service root-cause analysis performed for every broken or unreachable
/// App Service after connectivity testing.  Aggregates ARM state, App Service
/// Plan health, recent deployment results, and 48-hour activity-log events.
/// </summary>
public record ServiceDowntimeDiagnosis
{
    public string Name { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? ResourceGroup { get; init; }
    public string HttpStatus { get; init; } = "";
    /// <summary>Normal | Limited | DisasterRecoveryMode — from ARM site resource.</summary>
    public string? AvailabilityState { get; init; }
    /// <summary>Normal | Exceeded — indicates free-tier quota breach.</summary>
    public string? UsageState { get; init; }
    /// <summary>True when the app is currently quota-suspended.</summary>
    public bool IsSuspended { get; init; }
    public DateTime? SuspendedTill { get; init; }
    public string? PlanName { get; init; }
    /// <summary>Ready | Pending | Creating — from ARM server farm resource.</summary>
    public string? PlanStatus { get; init; }
    public string? PlanSku { get; init; }
    public bool PlanStopped { get; init; }
    public List<DeploymentEntry> RecentDeployments { get; init; } = new();
    public List<ActivityLogEntry> RecentActivity { get; init; } = new();
    public string LikelyCause { get; init; } = "Unknown";
    public string? SuggestedFix { get; init; }
    /// <summary>App Insights 7-day exception count for this service.</summary>
    public int? AppInsightsExceptions7Days { get; init; }
    /// <summary>App Insights 7-day failed request count for this service.</summary>
    public int? AppInsightsFailedRequests7Days { get; init; }
    /// <summary>URL to the latest GitHub Actions workflow run for this service's repo.</summary>
    public string? GitHubWorkflowRunUrl { get; init; }
    /// <summary>Latest GitHub Actions workflow run status (completed, in_progress, etc.).</summary>
    public string? GitHubWorkflowStatus { get; init; }
    /// <summary>Latest GitHub Actions workflow run conclusion (success, failure, cancelled, etc.).</summary>
    public string? GitHubWorkflowConclusion { get; init; }
    /// <summary>When the latest GitHub workflow run completed.</summary>
    public DateTime? GitHubWorkflowCompletedAt { get; init; }
    /// <summary>Kudu process diagnostics — list of running processes or error.</summary>
    public string? KuduProcesses { get; init; }
    /// <summary>True when the Kudu SCM site was reachable.</summary>
    public bool KuduReachable { get; init; }
}

/// <summary>One entry from the Kudu/ARM deployment history of an App Service.</summary>
public record DeploymentEntry
{
    public string? DeploymentId { get; init; }
    public bool? Active { get; init; }
    public int? StatusCode { get; init; }
    /// <summary>Success | Failed | Deploying | Building | Pending</summary>
    public string? StatusText { get; init; }
    public string? Message { get; init; }
    public DateTime? DeployedAt { get; init; }
    public string? Author { get; init; }
}

/// <summary>One event from the Azure Activity Log (last 48 hours) for a broken service.</summary>
public record ActivityLogEntry
{
    public string? OperationName { get; init; }
    public string? Status { get; init; }
    public DateTime? EventTimestamp { get; init; }
    public string? Caller { get; init; }
    public string? Level { get; init; }
}

// ─── History summary (for /timebased page time-series charts) ───────────────
