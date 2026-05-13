using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>Shared Azure OpenAI chat-completion helper used by feature endpoints.</summary>
internal static class AzureOpenAiClient
{
    private const string ApiVersion = "2024-02-01";

    /// <summary>
    /// Sends a single chat-completion request to Azure OpenAI and returns the response text.
    /// Returns null on HTTP error (warning is logged); throws on network or serialization errors.
    /// </summary>
    internal static async Task<string?> GetCompletionAsync(
        IHttpClientFactory factory,
        string endpoint,
        string apiKey,
        string deployment,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        ILogger logger,
        CancellationToken ct)
    {
        var client = factory.CreateClient("azure-openai");
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}";

        var requestBody = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   },
            },
            temperature,
            max_tokens = maxTokens,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("api-key", apiKey);

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            var truncated = err.Length > 200 ? err[..200] + "…" : err;
            logger.LogWarning("Azure OpenAI call failed: {Status} — {Body}", response.StatusCode, truncated);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }
}
