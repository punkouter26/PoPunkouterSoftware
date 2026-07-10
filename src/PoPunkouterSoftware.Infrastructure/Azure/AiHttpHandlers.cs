using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Records the <see cref="Telemetry.AzureOpenAiCalls"/> / <see cref="Telemetry.AzureOpenAiDuration"/>
/// instruments at the HTTP boundary of the "azure-openai" typed client, so every current and
/// future AI call site gets metrics without remembering to instrument itself.
/// </summary>
public sealed class AiTelemetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();
            Telemetry.AzureOpenAiCalls.Add(1,
                new KeyValuePair<string, object?>("status_class", Telemetry.StatusClass((int)response.StatusCode)),
                new KeyValuePair<string, object?>("outcome", response.IsSuccessStatusCode ? "success" : "http_error"));
            Telemetry.AzureOpenAiDuration.Record(sw.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (Exception) when (Track(sw))
        {
            throw; // Track always returns false — the filter only records, never handles.
        }
    }

    private static bool Track(Stopwatch sw)
    {
        sw.Stop();
        Telemetry.AzureOpenAiCalls.Add(1,
            new KeyValuePair<string, object?>("status_class", "other"),
            new KeyValuePair<string, object?>("outcome", "exception"));
        Telemetry.AzureOpenAiDuration.Record(sw.Elapsed.TotalMilliseconds);
        return false;
    }
}

/// <summary>
/// Testing-environment primary handler for the "azure-openai" client: every request is
/// answered in-process with a canned chat-completion payload, so no test run can ever
/// reach Azure AI Foundry (no token spend, no secret required, no network).
/// Wired in Program.cs only when the environment is "Testing".
/// </summary>
public sealed class TestingAiStubHandler(ILogger<TestingAiStubHandler>? logger = null) : HttpMessageHandler
{
    public const string StubModelId = "stub-model";
    public const string StubContent = "This is a stubbed AI response for the Testing environment.";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        logger?.LogDebug("azure-openai request to {Uri} intercepted by Testing stub", request.RequestUri);

        var payload = $$"""
        {
          "id": "chatcmpl-stub",
          "object": "chat.completion",
          "model": "{{StubModelId}}",
          "choices": [
            {
              "index": 0,
              "finish_reason": "stop",
              "message": { "role": "assistant", "content": "{{StubContent}}" }
            }
          ],
          "usage": { "prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0 }
        }
        """;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
    }
}
