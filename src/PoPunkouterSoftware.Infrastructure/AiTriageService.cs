using System.Net.Http.Json;
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
