using Microsoft.Extensions.Caching.Memory;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared.Azure;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Features.Infra;

/// <summary>
/// Scans every owned GitHub repo for CI/CD workflow files and infrastructure definitions,
/// then returns a structured comparison so all apps' deployment setups can be reviewed
/// side-by-side.
/// </summary>
internal static class InfraEndpoints
{
    private const string CacheKey = "infra-cicd-review";

    // Workflow trigger keys that indicate CI/CD
    private static readonly string[] KnownTriggers =
        ["push", "pull_request", "workflow_dispatch", "schedule", "release", "workflow_call"];

    // Azure deploy GitHub Actions — maps action id → friendly deploy target
    private static readonly Dictionary<string, string> DeployActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["azure/webapps-deploy"] = "App Service",
        ["azure/static-web-apps-deploy"] = "Static Web Apps",
        ["azure/container-apps-deploy"] = "Container Apps",
        ["azure/functions-action"] = "Azure Functions",
        ["azure/aci-deploy"] = "Container Instance",
        ["azure/k8s-deploy"] = "AKS",
        ["azure/arm-deploy"] = "ARM/Bicep",
        ["azure/login"] = "(Azure login)",
    };

    // Bicep resource extraction: `resource <id> '<type>@<version>'`
    private static readonly Regex BicepResourceRx = new(
        @"resource\s+\w+\s+'([^'@]+)@[^']*'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ARM template resource type extraction
    private static readonly Regex ArmResourceRx = new(
        @"""type""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Workflow `on:` trigger list extraction (handles both inline and block style)
    private static readonly Regex OnTriggerRx = new(
        @"^on\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Branch filter extraction (branches: [...] or branches:\n  - ...)
    private static readonly Regex BranchRx = new(
        @"branches\s*:\s*\[([^\]]+)\]|branches\s*:\n((?:\s+-[^\n]+\n)+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    internal static WebApplication MapInfraEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/infra").WithTags("Infra");

        group.MapGet("/cicd-review", async (
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // ── Feature guard: PAT required for private repos ─────────────────
            var pat = config["GitHub:PersonalAccessToken"];

            // If no PAT is configured, try the local `gh auth token` CLI fallback
            if (string.IsNullOrWhiteSpace(pat))
                pat = await TryGetGhTokenAsync(logger, ct);

            if (string.IsNullOrWhiteSpace(pat))
            {
                return Results.Ok(new
                {
                    disabled = true,
                    message = "GitHub CI/CD review requires authentication. Either:\n" +
                               "• Run 'gh auth login' locally (uses gh CLI token automatically), or\n" +
                               "• Add a PAT to Key Vault as PoPunkouterSoftware--GitHub--PersonalAccessToken.",
                    reviews = new List<InfraReview>(),
                });
            }

            // ── Cache hit ─────────────────────────────────────────────────────
            if (cache.TryGetValue(CacheKey, out List<InfraReview>? cached))
                return Results.Ok(new { disabled = false, reviews = cached });

            // ── Build authenticated GitHub API client ─────────────────────────
            var http = httpClientFactory.CreateClient("github");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", pat);

            try
            {
                // ── 1. List all owned repos (paginated) ───────────────────────
                var repos = await FetchAllReposAsync(http, ct);
                logger.LogInformation("InfraEndpoints: scanning {Count} repos", repos.Count);

                // ── 2. Scan each repo ─────────────────────────────────────────
                var reviews = new List<InfraReview>();
                foreach (var repo in repos)
                {
                    var repoName = repo["name"]?.GetValue<string>() ?? "";
                    var fullName = repo["full_name"]?.GetValue<string>() ?? "";
                    var owner = fullName.Split('/')[0];
                    var defaultBranch = repo["default_branch"]?.GetValue<string>() ?? "main";
                    var isPrivate = repo["private"]?.GetValue<bool>() ?? false;
                    var repoUrl = repo["html_url"]?.GetValue<string>();

                    try
                    {
                        var review = await ScanRepoAsync(
                            http, owner, repoName, defaultBranch, isPrivate, repoUrl, logger, ct);

                        var (wfStatus, wfConclusion, wfCompleted, wfUrl, wfName) =
                            await FetchLatestWorkflowRunAsync(http, fullName, defaultBranch, ct);

                        review = review with
                        {
                            LatestWorkflowRunStatus = wfStatus,
                            LatestWorkflowRunConclusion = wfConclusion,
                            LatestWorkflowRunCompletedAt = wfCompleted,
                            LatestWorkflowRunUrl = wfUrl,
                            LatestWorkflowRunName = wfName,
                        };

                        reviews.Add(review);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to scan repo {Repo}", repoName);
                        reviews.Add(new InfraReview
                        {
                            RepoName = repoName,
                            DefaultBranch = defaultBranch,
                            IsPrivate = isPrivate,
                            RepoUrl = repoUrl,
                            ScannedAt = DateTime.UtcNow,
                            Error = ex.Message,
                        });
                    }
                }

                // Sort: repos with CI/CD first, then alphabetical
                reviews = reviews
                    .OrderBy(r => r.CiCdFiles.Count == 0 ? 1 : 0)
                    .ThenBy(r => r.RepoName)
                    .ToList();

                var cacheTtlHours = config.GetValue<int>("Infra:CiCdCacheHours", 6);
                cache.Set(CacheKey, reviews, TimeSpan.FromHours(cacheTtlHours));
                return Results.Ok(new { disabled = false, reviews });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CI/CD review scan failed");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        })
        .WithName("GetCiCdReview");

        // ── Force-refresh (clears cache and re-scans) ─────────────────────────
        group.MapPost("/cicd-review/refresh", (IMemoryCache cache) =>
        {
            cache.Remove(CacheKey);
            return Results.Ok(new { cleared = true });
        })
        .RequireManagementActions()
        .WithName("RefreshCiCdReview");

        return app;
    }

    // ── GitHub API helpers ────────────────────────────────────────────────────

    private static async Task<List<JsonObject>> FetchAllReposAsync(HttpClient http, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        var page = 1;
        while (true)
        {
            var url = $"https://api.github.com/user/repos?visibility=all&affiliation=owner&per_page=100&page={page}";
            var resp = await http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 429)
            {
                // GitHub rate-limited — return whatever repos we collected so far
                // rather than throwing and losing the partial result.
                break;
            }
            if (!resp.IsSuccessStatusCode)
                break;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var arr = JsonSerializer.Deserialize<JsonArray>(json, _jsonOpts) ?? new JsonArray();
            if (arr.Count == 0)
                break;
            foreach (var item in arr)
                if (item is JsonObject obj)
                    all.Add(obj);
            if (arr.Count < 100)
                break;
            page++;
        }
        return all;
    }

    private static async Task<InfraReview> ScanRepoAsync(
        HttpClient http,
        string owner,
        string repoName,
        string defaultBranch,
        bool isPrivate,
        string? repoUrl,
        ILogger logger,
        CancellationToken ct)
    {
        // Get the full recursive file tree for this repo
        var treeUrl = $"https://api.github.com/repos/{owner}/{repoName}/git/trees/{defaultBranch}?recursive=1";
        var treeResp = await http.GetAsync(treeUrl, ct);

        if (!treeResp.IsSuccessStatusCode)
        {
            var errBody = await treeResp.Content.ReadAsStringAsync(ct);
            return new InfraReview
            {
                RepoName = repoName,
                DefaultBranch = defaultBranch,
                IsPrivate = isPrivate,
                RepoUrl = repoUrl,
                ScannedAt = DateTime.UtcNow,
                Error = $"Tree fetch failed ({(int)treeResp.StatusCode}): {Truncate(errBody, 120)}",
            };
        }

        var treeJson = await treeResp.Content.ReadAsStringAsync(ct);
        using var treeDoc = JsonDocument.Parse(treeJson);

        var paths = new List<string>();
        if (treeDoc.RootElement.TryGetProperty("tree", out var tree))
        {
            foreach (var node in tree.EnumerateArray())
            {
                var path = node.GetProperty("path").GetString() ?? "";
                var type = node.TryGetProperty("type", out var t) ? t.GetString() : "blob";
                if (type == "blob")
                    paths.Add(path);
            }
        }

        // Classify files
        var workflowPaths = paths
            .Where(p => p.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                     && (p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                      || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var infraPaths = paths
            .Where(p => IsInfraFile(p))
            .ToList();

        // Fetch and parse workflow files
        var cicdFiles = new List<CiCdFileSummary>();
        foreach (var wfPath in workflowPaths)
        {
            var content = await FetchFileContentAsync(http, owner, repoName, wfPath, ct);
            if (content is null)
                continue;
            cicdFiles.Add(ParseWorkflow(wfPath, content));
        }

        // Fetch and parse infra files
        var infraFiles = new List<InfraFileSummary>();
        foreach (var infraPath in infraPaths)
        {
            var content = await FetchFileContentAsync(http, owner, repoName, infraPath, ct);
            if (content is null)
                continue;
            infraFiles.Add(ParseInfraFile(infraPath, content));
        }

        // Infer deployment target and method from what we found
        var (target, method) = InferDeployment(cicdFiles, infraFiles);

        return new InfraReview
        {
            RepoName = repoName,
            DefaultBranch = defaultBranch,
            IsPrivate = isPrivate,
            RepoUrl = repoUrl,
            DeploymentTarget = target,
            DeploymentMethod = method,
            CiCdFiles = cicdFiles,
            InfraFiles = infraFiles,
            ScannedAt = DateTime.UtcNow,
        };
    }

    private static async Task<string?> FetchFileContentAsync(
        HttpClient http, string owner, string repo, string path, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{Uri.EscapeDataString(path)}";
        var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("content", out var contentEl))
            return null;
        var b64 = contentEl.GetString()?.Replace("\n", "") ?? "";
        if (string.IsNullOrWhiteSpace(b64))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(b64);
            // Cap at 40 KB to avoid memory pressure on huge generated files
            if (bytes.Length > 40_960)
                bytes = bytes[..40_960];
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private static CiCdFileSummary ParseWorkflow(string path, string content)
    {
        var triggers = new List<string>();
        var deployActs = new List<string>();
        var branches = new List<string>();

        // Extract `on:` triggers
        var onMatch = OnTriggerRx.Match(content);
        if (onMatch.Success)
        {
            var onValue = onMatch.Groups[1].Value.Trim();
            // Inline list style: `on: [push, pull_request]`
            if (onValue.StartsWith('['))
            {
                triggers.AddRange(
                    onValue.Trim('[', ']')
                           .Split(',')
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0));
            }
            else if (onValue.Length > 0 && !onValue.StartsWith('#'))
            {
                triggers.Add(onValue.Split(' ')[0].Trim(':'));
            }
        }

        // Block-style triggers: lines like `  push:` under `on:`
        foreach (var trigger in KnownTriggers)
        {
            if (Regex.IsMatch(content, $@"^\s+{Regex.Escape(trigger)}\s*:", RegexOptions.Multiline)
             && !triggers.Contains(trigger))
            {
                triggers.Add(trigger);
            }
        }

        // Extract deploy action uses
        foreach (var (actionId, _) in DeployActions)
        {
            if (content.Contains(actionId, StringComparison.OrdinalIgnoreCase)
             && !deployActs.Contains(actionId))
            {
                deployActs.Add(actionId);
            }
        }

        // Extract branch filters
        foreach (Match m in BranchRx.Matches(content))
        {
            var inline = m.Groups[1].Value;
            var block = m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(inline))
                branches.AddRange(inline.Split(',').Select(s => s.Trim(' ', '\'', '"')).Where(s => s.Length > 0));
            if (!string.IsNullOrWhiteSpace(block))
                branches.AddRange(block.Split('\n').Select(l => l.Trim().TrimStart('-').Trim(' ', '\'', '"')).Where(s => s.Length > 0));
        }

        return new CiCdFileSummary
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            Triggers = triggers.Distinct().ToList(),
            DeployActions = deployActs.Distinct().ToList(),
            BranchFilters = branches.Distinct().ToList(),
        };
    }

    private static InfraFileSummary ParseInfraFile(string path, string content)
    {
        var lower = path.ToLowerInvariant();
        var fileType = lower.EndsWith(".bicep") ? "bicep"
                      : lower.EndsWith("azuredeploy.json") ? "arm"
                      : lower.EndsWith("azure.yaml") ||
                        lower.EndsWith("azure.yml") ? "azd"
                      : lower.Contains("dockerfile") ? "docker"
                      : lower.Contains("docker-compose") ? "compose"
                      : "other";

        var resourceTypes = new List<string>();

        if (fileType == "bicep")
        {
            foreach (Match m in BicepResourceRx.Matches(content))
            {
                var rt = m.Groups[1].Value.Trim();
                if (!resourceTypes.Contains(rt))
                    resourceTypes.Add(rt);
            }
        }
        else if (fileType == "arm")
        {
            foreach (Match m in ArmResourceRx.Matches(content))
            {
                var rt = m.Groups[1].Value.Trim();
                if (rt.Contains('/') && !resourceTypes.Contains(rt))
                    resourceTypes.Add(rt);
            }
        }

        return new InfraFileSummary
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            FileType = fileType,
            ResourceTypes = resourceTypes,
        };
    }

    // ── Inference helpers ─────────────────────────────────────────────────────

    private static (string target, string method) InferDeployment(
        List<CiCdFileSummary> cicd, List<InfraFileSummary> infra)
    {
        if (cicd.Count == 0)
            return ("Unknown", "Manual / Unknown");

        // Collect all deploy actions across all workflow files
        var allActions = cicd.SelectMany(c => c.DeployActions).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Determine primary hosting target (most specific wins)
        var target = "Unknown";
        if (allActions.Contains("azure/container-apps-deploy"))
            target = "Container Apps";
        else if (allActions.Contains("azure/k8s-deploy"))
            target = "AKS";
        else if (allActions.Contains("azure/functions-action"))
            target = "Azure Functions";
        else if (allActions.Contains("azure/static-web-apps-deploy"))
            target = "Static Web Apps";
        else if (allActions.Contains("azure/webapps-deploy"))
            target = "App Service";
        else if (allActions.Contains("azure/aci-deploy"))
            target = "Container Instance";
        else if (allActions.Contains("azure/arm-deploy"))
        {
            // Check bicep resource types for more context
            var resTypes = infra.SelectMany(f => f.ResourceTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (resTypes.Any(r => r.Contains("staticSites", StringComparison.OrdinalIgnoreCase)))
                target = "Static Web Apps";
            else if (resTypes.Any(r => r.Contains("containerApps", StringComparison.OrdinalIgnoreCase)))
                target = "Container Apps";
            else if (resTypes.Any(r => r.Contains("sites", StringComparison.OrdinalIgnoreCase)))
                target = "App Service";
            else
                target = "ARM/Bicep Deploy";
        }

        // Also check infra files if still unknown
        if (target == "Unknown" && infra.Count > 0)
        {
            var resTypes = infra.SelectMany(f => f.ResourceTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (resTypes.Any(r => r.Contains("staticSites", StringComparison.OrdinalIgnoreCase)))
                target = "Static Web Apps";
            else if (resTypes.Any(r => r.Contains("containerApps", StringComparison.OrdinalIgnoreCase)))
                target = "Container Apps";
            else if (resTypes.Any(r => r.Contains("sites", StringComparison.OrdinalIgnoreCase)))
                target = "App Service";
        }

        var method = cicd.Count > 0 ? "GitHub Actions" : "Manual";
        return (target, method);
    }

    private static bool IsInfraFile(string path)
    {
        var lower = path.ToLowerInvariant();
        var file = Path.GetFileName(lower);

        return lower.EndsWith(".bicep")
            || file == "azuredeploy.json"
            || file == "maintemplate.json"
            || file == "azure.yaml"
            || file == "azure.yml"
            || file == "dockerfile"
            || file.StartsWith("dockerfile.")
            || file == "docker-compose.yml"
            || file == "docker-compose.yaml";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Tries to obtain a GitHub token from the local `gh` CLI (`gh auth token`).
    /// Returns null if gh is not installed, not authenticated, or the call fails.
    /// This lets developers skip manual PAT setup — just `gh auth login` once.
    /// </summary>
    private static async Task<string?> TryGetGhTokenAsync(ILogger logger, CancellationToken ct)
    {
        try
        {
            // `gh` is a plain binary on both Windows and Linux/macOS
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var token = (await stdoutTask).Trim();
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(token))
            {
                logger.LogInformation("InfraEndpoints: using token from 'gh auth token'");
                return token;
            }

            var err = (await stderrTask).Trim();
            logger.LogDebug("InfraEndpoints: gh auth token unavailable — {Reason}", err);
            return null;
        }
        catch (Exception ex)
        {
            // gh not installed or PATH issue — silently ignore
            logger.LogDebug(ex, "InfraEndpoints: gh CLI not available");
            return null;
        }
    }

    /// <summary>
    /// Fetches the most recent workflow run status/conclusion for a given GitHub repo and branch.
    /// </summary>
    private static async Task<(string? status, string? conclusion, DateTime? completedAt, string? runUrl, string? runName)>
        FetchLatestWorkflowRunAsync(HttpClient http, string fullName, string defaultBranch, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{fullName}/actions/runs?branch={Uri.EscapeDataString(defaultBranch)}&per_page=1";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null, null, null);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
                return (null, null, null, null, null);

            var run = runs[0];
            var st = run.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            var conc = run.TryGetProperty("conclusion", out var concEl) ? concEl.GetString() : null;
            var hu = run.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            var rn = run.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            DateTime? comp = run.TryGetProperty("updated_at", out var ua)
                && ua.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ua.GetString(), out var dt) ? dt : null;

            return (st, conc, comp, hu, rn);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }
}
