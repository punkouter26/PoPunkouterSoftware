using System.Text.Json.Serialization;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Environment/config carrier returned by <c>/api/config</c>. Lifted out of MainLayout
/// so the source-generated <see cref="AppJsonContext"/> can reference it.
/// </summary>
internal sealed record ConfigResponse(
    [property: JsonPropertyName("apiBase")] string ApiBase,
    [property: JsonPropertyName("isMockMode")] bool IsMockMode,
    [property: JsonPropertyName("managementActionsEnabled")] bool ManagementActionsEnabled);

/// <summary>
/// Minimal RFC 7807 ProblemDetails shape — enough to surface the server's `detail`
/// message when a management action is rejected (403/409/5xx).
/// </summary>
internal sealed record ProblemResponse(
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>
/// Request body for the ad-hoc <c>POST /api/diag/ai</c> call made by
/// <c>AzureStatusNarrative</c>'s "Rewrite this" button. Mirrors Infrastructure's
/// <c>AiTriageRequest</c> shape; declared here (rather than referencing Infrastructure
/// directly, which the Client project deliberately does not) so it can register with the
/// source-generated <see cref="AppJsonContext"/> — an anonymous object would force
/// reflection-based serialization, which fails the trim-safe build.
/// </summary>
internal sealed record AiTriageAdHocRequest(
    [property: JsonPropertyName("attentionItems")] IReadOnlyList<string> AttentionItems);

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every type the WASM client
/// (de)serialises. Replacing reflection-based System.Text.Json with this context makes the
/// client trim-safe (clears the IL2026 warnings) and removes reflection metadata from the
/// published bundle. The server serialises camelCase, so the context matches that policy
/// and stays case-insensitive on the way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AzureReport))]
[JsonSerializable(typeof(OpsSummary))]
[JsonSerializable(typeof(AiSummaryResult))]
[JsonSerializable(typeof(List<HistorySummary>))]
[JsonSerializable(typeof(PortfolioResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(AiTriageAdHocRequest))]
[JsonSerializable(typeof(SnoozeRequest))]
[JsonSerializable(typeof(SnoozeRemoveRequest))]
[JsonSerializable(typeof(SnoozeEntry))]
[JsonSerializable(typeof(List<SnoozeEntry>))]
internal partial class AppJsonContext : JsonSerializerContext;
