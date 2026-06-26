using System.Text.Json.Serialization;
using PoPunkouterSoftware.Client.Components.Pages;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Environment/config carrier returned by <c>/api/config</c>. Lifted out of MainLayout
/// so the source-generated <see cref="AppJsonContext"/> can reference it.
/// </summary>
internal sealed record ConfigResponse(
    [property: JsonPropertyName("apiBase")] string ApiBase,
    [property: JsonPropertyName("isMockMode")] bool IsMockMode);

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
[JsonSerializable(typeof(List<HistorySummary>))]
[JsonSerializable(typeof(AppsWrapper))]
[JsonSerializable(typeof(GitHubActivity))]
[JsonSerializable(typeof(ConfigResponse))]
internal partial class AppJsonContext : JsonSerializerContext;
