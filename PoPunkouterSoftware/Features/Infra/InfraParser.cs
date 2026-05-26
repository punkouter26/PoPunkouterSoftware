using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Features.Infra;

/// <summary>
/// Helper class for parsing GitHub CI/CD workflows and infrastructure-as-code files.
/// Extracted from InfraEndpoints to reduce endpoint complexity.
/// </summary>
internal static class InfraParser
{
    // Workflow trigger keys that indicate CI/CD
    internal static readonly string[] KnownTriggers =
        ["push", "pull_request", "workflow_dispatch", "schedule", "release", "workflow_call"];

    // Azure deploy GitHub Actions — maps action id → friendly deploy target
    internal static readonly Dictionary<string, string> DeployActions = new(StringComparer.OrdinalIgnoreCase)
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
    internal static readonly Regex BicepResourceRx = new(
        @"resource\s+\w+\s+'([^'@]+)@[^']*'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ARM template resource type extraction
    internal static readonly Regex ArmResourceRx = new(
        @"""type""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Workflow `on:` trigger list extraction (handles both inline and block style)
    internal static readonly Regex OnTriggerRx = new(
        @"^on\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Branch filter extraction from trigger blocks (e.g., `push: { branches: [main] }`)
    internal static readonly Regex BranchRx = new(
        @"branches\s*:\s*\[\s*([^\]]+)\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    /// <summary>Fetches all repositories owned by the authenticated user using paginated GitHub API.</summary>
    internal static async Task<List<JsonObject>> FetchAllReposAsync(HttpClient http, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        int page = 1;
        const int perPage = 100;

        while (true)
        {
            var url = $"https://api.github.com/user/repos?per_page={perPage}&page={page}&type=owner";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var items = JsonNode.Parse(json)?
                .AsArray()
                .Select(n => n?.AsObject())
                .OfType<JsonObject>()
                .ToList() ?? [];

            if (items.Count == 0)
                break;

            all.AddRange(items);
            if (items.Count < perPage)
                break;

            page++;
        }

        return all;
    }

    /// <summary>Scans a single repository for CI/CD and infrastructure files.</summary>
    internal static async Task<InfraReview> ScanRepoAsync(
        HttpClient http,
        string owner,
        string repoName,
        string defaultBranch,
        bool isPrivate,
        string? repoUrl,
        ILogger logger,
        CancellationToken ct)
    {
        var review = new InfraReview
        {
            RepoName = repoName,
            DefaultBranch = defaultBranch,
            IsPrivate = isPrivate,
            RepoUrl = repoUrl,
            ScannedAt = DateTime.UtcNow,
        };

        // Common paths to scan for CI/CD and infra files
        var pathsToScan = new[]
        {
            ".github/workflows/*.yml",
            ".github/workflows/*.yaml",
            "*.bicep",
            "*.json",
            "*.arm.json",
            "bicep/**/*.bicep",
            "infra/**/*.bicep",
            "infra/**/*.json",
            "terraform/**/*.tf",
            ".github/*.yml",
            ".github/*.yaml",
        };

        foreach (var pattern in pathsToScan)
        {
            try
            {
                var searchUrl = $"https://api.github.com/search/code?q=filename:{System.Web.HttpUtility.UrlEncode(pattern)}+repo:{owner}/{repoName}&per_page=100";
                using var resp = await http.GetAsync(searchUrl, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                var results = JsonNode.Parse(json)?["items"]?
                    .AsArray()
                    .Select(n => n?["path"]?.GetValue<string>())
                    .OfType<string>()
                    .Distinct()
                    .ToList() ?? [];

                foreach (var path in results)
                {
                    if (!IsInfraFile(path))
                        continue;

                    var content = await FetchFileContentAsync(http, owner, repoName, path, defaultBranch, ct);
                    if (string.IsNullOrEmpty(content))
                        continue;

                    if (path.Contains("workflow", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".yml") || path.EndsWith(".yaml"))
                    {
                        var ciCd = ParseWorkflow(path, content);
                        review.CiCdFiles.Add(ciCd);
                    }
                    else if (IsInfraFile(path))
                    {
                        var infra = ParseInfraFile(path, content);
                        review.InfraFiles.Add(infra);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to scan pattern {Pattern} in {Repo}", pattern, repoName);
            }
        }

        return review;
    }

    /// <summary>Fetches the content of a file from a GitHub repository.</summary>
    internal static async Task<string?> FetchFileContentAsync(
        HttpClient http,
        string owner,
        string repoName,
        string filePath,
        string branch,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repoName}/contents/{System.Web.HttpUtility.UrlEncode(filePath)}?ref={branch}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            var contentB64 = node?["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(contentB64))
                return null;

            return Encoding.UTF8.GetString(Convert.FromBase64String(contentB64));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses a GitHub Actions workflow file for triggers and deploy targets.</summary>
    internal static CiCdFileSummary ParseWorkflow(string path, string content)
    {
        var triggers = new HashSet<string>();
        var deployTargets = new HashSet<string>();
        var branches = new HashSet<string>();

        // Extract workflow triggers
        var triggerMatch = OnTriggerRx.Match(content);
        if (triggerMatch.Success)
        {
            var triggerStr = triggerMatch.Groups[1].Value;
            foreach (var known in KnownTriggers)
            {
                if (triggerStr.Contains(known, StringComparison.OrdinalIgnoreCase))
                    triggers.Add(known);
            }

            // Extract branch filters
            var branchMatch = BranchRx.Match(triggerStr);
            if (branchMatch.Success)
            {
                var branchStr = branchMatch.Groups[1].Value;
                foreach (var b in branchStr.Split(','))
                {
                    var trimmed = b.Trim().Trim('[', ']', '"', '\'');
                    if (!string.IsNullOrEmpty(trimmed))
                        branches.Add(trimmed);
                }
            }
        }

        // Extract deploy actions
        foreach (var actionKey in DeployActions.Keys)
        {
            if (content.Contains(actionKey, StringComparison.OrdinalIgnoreCase))
                deployTargets.Add(DeployActions[actionKey]);
        }

        return new CiCdFileSummary
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            Triggers = triggers.Distinct().ToList(),
            DeployActions = deployTargets.Distinct().ToList(),
            BranchFilters = branches.Distinct().ToList(),
        };
    }

    /// <summary>Parses an infrastructure file (Bicep, ARM, Terraform) for resource types.</summary>
    internal static InfraFileSummary ParseInfraFile(string path, string content)
    {
        var resourceTypes = new HashSet<string>();

        if (path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var match in BicepResourceRx.Matches(content).Cast<Match>())
            {
                var type = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(type))
                    resourceTypes.Add(type);
            }
        }
        else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".arm.json", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var match in ArmResourceRx.Matches(content).Cast<Match>())
            {
                var type = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(type))
                    resourceTypes.Add(type);
            }
        }

        var (deployTarget, deployMethod) = InferDeployment(path, content);

        return new InfraFileSummary
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            FileType = path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) ? "bicep"
                : path.EndsWith(".arm.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith("azuredeploy.json", StringComparison.OrdinalIgnoreCase) ? "arm"
                : path.EndsWith("azure.yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith("azure.yml", StringComparison.OrdinalIgnoreCase) ? "azd"
                : path.Contains("docker-compose", StringComparison.OrdinalIgnoreCase) ? "compose"
                : path.Contains("docker", StringComparison.OrdinalIgnoreCase) ? "docker"
                : "other",
            ResourceTypes = resourceTypes.ToList(),
        };
    }

    /// <summary>Infers the deployment target and method from file path and content.</summary>
    private static (string target, string method) InferDeployment(
        string path,
        string content)
    {
        var target = "Unknown";
        var method = "Unknown";

        // Infer from file path
        if (path.Contains("function", StringComparison.OrdinalIgnoreCase))
            target = "Azure Functions";
        else if (path.Contains("static", StringComparison.OrdinalIgnoreCase) || path.Contains("swa", StringComparison.OrdinalIgnoreCase))
            target = "Static Web Apps";
        else if (path.Contains("container", StringComparison.OrdinalIgnoreCase) || path.Contains("docker", StringComparison.OrdinalIgnoreCase))
            target = "Container Apps";
        else if (path.Contains("app-service", StringComparison.OrdinalIgnoreCase) || path.Contains("appservice", StringComparison.OrdinalIgnoreCase))
            target = "App Service";

        // Infer from content
        if (content.Contains("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase))
            target = "App Service";
        else if (content.Contains("Microsoft.App/containerApps", StringComparison.OrdinalIgnoreCase))
            target = "Container Apps";
        else if (content.Contains("Microsoft.Web/staticSites", StringComparison.OrdinalIgnoreCase))
            target = "Static Web Apps";

        // Infer deployment method
        if (path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase))
            method = "Bicep";
        else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".arm.json", StringComparison.OrdinalIgnoreCase))
            method = "ARM Template";
        else if (path.EndsWith(".tf", StringComparison.OrdinalIgnoreCase))
            method = "Terraform";
        else if (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            method = "GitHub Actions";

        return (Truncate(target, 40), Truncate(method, 30));
    }

    /// <summary>Determines if a file is an infrastructure or CI/CD file worth scanning.</summary>
    private static bool IsInfraFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();

        return ext is ".bicep" or ".json" or ".tf" or ".yml" or ".yaml" or ".arm" ||
               name.EndsWith(".bicep") ||
               path.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("infra/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("terraform/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Truncates a string to a maximum length.</summary>
    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "…" : s;

    /// <summary>Attempts to retrieve a GitHub CLI token from `gh auth token` if available.</summary>
    internal static async Task<string?> TryGetGhTokenAsync(ILogger logger, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;

            var token = (await proc.StandardOutput.ReadLineAsync())?.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to retrieve gh CLI token");
            return null;
        }
    }
}
