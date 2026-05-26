using System.Text.Json;
using Microsoft.JSInterop;

namespace PoPunkouterSoftware.Client;

internal sealed class PoAppSession(IJSRuntime jsRuntime)
{
    private const string StorageKey = "po.session";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _jsRuntime = jsRuntime;

    public async Task<PoUserSession?> GetAsync()
    {
        var json = await _jsRuntime.InvokeAsync<string?>("poAppStorage.get", StorageKey);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<PoUserSession>(json, _jsonOptions);
    }

    public async Task SaveAsync(PoUserSession session)
    {
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        await _jsRuntime.InvokeVoidAsync("poAppStorage.set", StorageKey, json);
    }

    public Task ClearAsync() => _jsRuntime.InvokeVoidAsync("poAppStorage.remove", StorageKey).AsTask();
}

internal sealed record PoUserSession(
    string LoginMode,
    string DisplayName,
    string Email,
    bool IsGuest,
    bool IsAuthenticated,
    string Status);
