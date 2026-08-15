using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// NET_RULES UPDATE: "Consider using AI services from Azure, Google, and
/// Huggingface with a preference for whatever model is cheapest and can do
/// the job."
///
/// This implementation uses the Hugging Face Inference API (free tier, no
/// account required for low-volume public models). The cheapest viable
/// choice for a one-paragraph triage summary is a small text2text
/// model — <c>google/flan-t5-base</c> — which is a free model on the HF
/// public Inference API and behaves well on "summarize the following
/// bullet list" instructions. No API key is required for anonymous usage
/// below the rate limit; if a key is configured (Key Vault secret
/// <c>HuggingFace--ApiToken</c>), the Authorization header is added.
///
/// AI outages must NOT cascade. The typed HttpClient <c>ai-hf</c> has NO
/// resilience pipeline (mirroring <c>health</c> / <c>azure-probe</c>) — an
/// AI outage is reported as "AI summary unavailable" and degrades the
/// dashboard gracefully. The caller (the dashboard) renders the failed
/// state as a disabled expander, never as an error toast.
/// </summary>
public sealed class AiTriageService
{
    public const string HttpClientName = "ai-hf";
    public const string DefaultModel = "google/flan-t5-base";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AiTriageService> _log;

    public AiTriageService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<AiTriageService> log)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _log = log;
    }

    public bool IsEnabled =>
        _config.GetValue("FeatureFlags:EnableAiSummary", false);

    public async Task<AiTriageResult> SummarizeAsync(AiTriageRequest req, CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return new AiTriageResult(
                Available: false,
                Summary: null,
                Model: null,
                Reason: "disabled");
        }

        var apiToken = _config["HuggingFace:ApiToken"];
        var model = _config["HuggingFace:Model"] ?? DefaultModel;

        var bullets = req.AttentionItems is { Count: > 0 }
            ? string.Join("; ", req.AttentionItems)
            : "No specific items. Provide a one-sentence overall health summary.";

        var input = $"Summarize in one short paragraph: {bullets}";

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"models/{model}")
            {
                Content = JsonContent.Create(new { inputs = input }),
            };
            msg.Headers.Accept.ParseAdd("application/json");
            // HF Inference API expects Authorization if the model is gated,
            // and *allows* an unauthenticated call for public models. We
            // attach the key only when one is configured — never echo a
            // missing token as an empty bearer.
            if (!string.IsNullOrWhiteSpace(apiToken))
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

            using var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Hugging Face Inference API returned {Status}: {Body}", (int)resp.StatusCode, body);
                return new AiTriageResult(
                    Available: false,
                    Summary: null,
                    Model: model,
                    Reason: $"http-{(int)resp.StatusCode}");
            }

            // HF Inference API returns: [{"generated_text": "..."}] for text2text.
            var stream = await resp.Content.ReadFromJsonAsync<List<HfResponse>>(cancellationToken: ct);
            var text = stream?.FirstOrDefault()?.GeneratedText?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new AiTriageResult(
                    Available: false,
                    Summary: null,
                    Model: model,
                    Reason: "empty-response");
            }
            return new AiTriageResult(
                Available: true,
                Summary: text,
                Model: model,
                Reason: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI triage failed; returning unavailable");
            return new AiTriageResult(
                Available: false,
                Summary: null,
                Model: model,
                Reason: "exception");
        }
    }

    /// <summary>
    /// The scan-time entry point (called by <c>ReportRefreshRunner</c>): decides whether the
    /// Hugging Face model actually needs to run this scan, and always returns a populated
    /// <see cref="AiSummaryResult"/> — the dashboard never sees a dead "AI summary
    /// unavailable" state.
    ///
    /// <para>Skips the model call (Source <c>"cached"</c>) when <paramref name="previous"/>
    /// was itself a real generation (<c>Source == "ai"</c>) and the attention items hash is
    /// unchanged since that run — an unattended fleet with nothing new to report should not
    /// burn the free-tier HF quota every scan. Any other case (first scan, attention items
    /// changed, or the previous summary was itself a fallback) calls the model for real.</para>
    /// </summary>
    public async Task<AiSummaryResult> GenerateSummaryAsync(
        IReadOnlyList<string>? attentionItems, AiSummaryResult? previous, CancellationToken ct)
    {
        var hash = ComputeAttentionHash(attentionItems);
        var now = DateTimeOffset.UtcNow;

        if (previous is { Source: "ai" } && previous.AttentionHash == hash)
        {
            return previous with { Source = "cached", GeneratedAtUtc = now };
        }

        if (!IsEnabled)
        {
            return new AiSummaryResult
            {
                Text = BuildTemplateFallback(attentionItems),
                Source = "disabled",
                Model = null,
                GeneratedAtUtc = now,
                AttentionHash = hash,
            };
        }

        var result = await SummarizeAsync(new AiTriageRequest(attentionItems), ct);
        if (result.Available && !string.IsNullOrWhiteSpace(result.Summary))
        {
            return new AiSummaryResult
            {
                Text = result.Summary!,
                Source = "ai",
                Model = result.Model,
                GeneratedAtUtc = now,
                AttentionHash = hash,
            };
        }

        return new AiSummaryResult
        {
            Text = BuildTemplateFallback(attentionItems),
            Source = "template-fallback",
            Model = result.Model,
            GeneratedAtUtc = now,
            AttentionHash = hash,
        };
    }

    /// <summary>
    /// Pure, no-I/O rule-based one-sentence summary built directly from the attention items
    /// list — the fallback text whenever the real AI path is disabled or fails, so the
    /// dashboard always has something useful to show instead of a dead state. Parses the
    /// fixed formats <see cref="AttentionItemsBuilder"/> produces ("{name} is unavailable",
    /// "{n} security configuration finding(s)", "{n} cleanup candidate(s)", the staleness
    /// note) rather than requiring structured counts, so it stays usable from anywhere that
    /// only has the flat string list (e.g. an ad-hoc <c>POST /api/diag/ai</c> caller).
    /// </summary>
    public static string BuildTemplateFallback(IReadOnlyList<string>? attentionItems)
    {
        var items = attentionItems ?? Array.Empty<string>();
        if (items.Count == 0)
            return "No issues detected. No AI summary available.";

        var unavailableCount = items.Count(i => i.EndsWith(" is unavailable", StringComparison.Ordinal));
        var securityCount = ExtractLeadingCount(items, "security configuration finding");
        var cleanupCount = ExtractLeadingCount(items, "cleanup candidate");
        var isStale = items.Any(i => i.Contains("stale", StringComparison.OrdinalIgnoreCase));

        var parts = new List<string>();
        if (securityCount > 0)
            parts.Add($"{securityCount} security finding(s)");
        if (cleanupCount > 0)
            parts.Add($"{cleanupCount} cleanup candidate(s)");
        if (unavailableCount > 0)
            parts.Add($"{unavailableCount} service(s) unavailable");

        var body = parts.Count > 0
            ? string.Join(", ", parts) + "."
            : "Attention items present but not individually categorized.";
        var staleNote = isStale ? " Azure data may be stale." : "";

        return $"{body}{staleNote} No AI summary available.";
    }

    private static int ExtractLeadingCount(IReadOnlyList<string> items, string marker)
    {
        var match = items.FirstOrDefault(i => i.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return 0;

        var firstToken = match.Split(' ', 2)[0];
        return int.TryParse(firstToken, out var n) ? n : 0;
    }

    /// <summary>
    /// Pure, no-I/O SHA-256 hex digest of the attention items, joined with a control
    /// character (U+001F) unlikely to appear in generated text so items containing commas,
    /// pipes, etc. cannot collide two different lists onto the same hash.
    /// </summary>
    public static string ComputeAttentionHash(IReadOnlyList<string>? attentionItems)
    {
        const char separator = (char)0x1F; // ASCII "unit separator" -- unlikely to appear in generated text
        var joined = string.Join(separator, attentionItems ?? Array.Empty<string>());
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record HfResponse(
        [property: JsonPropertyName("generated_text")] string? GeneratedText);
}

public sealed record AiTriageRequest(
    [property: JsonPropertyName("attentionItems")] IReadOnlyList<string>? AttentionItems);

public sealed record AiTriageResult(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("reason")] string? Reason);
