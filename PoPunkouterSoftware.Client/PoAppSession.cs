using System.Text.Json;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared.Session;

namespace PoPunkouterSoftware.Client;

public sealed class PoAppSession(IJSRuntime jsRuntime)
{
    private const string StorageKey = "po.session";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _jsRuntime = jsRuntime;

    public async Task<PoUserSession?> GetAsync()
    {
        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("poAppStorage.get", StorageKey);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PoUserSession>(json, _jsonOptions);
        }
        catch (InvalidOperationException)
        {
            // During server prerender, JS interop is unavailable; treat as no session.
            return null;
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async Task SaveAsync(PoUserSession session)
    {
        try
        {
            var json = JsonSerializer.Serialize(session, _jsonOptions);
            await _jsRuntime.InvokeVoidAsync("poAppStorage.set", StorageKey, json);
        }
        catch (InvalidOperationException)
        {
            // Ignore prerender phase where JS interop is unavailable.
        }
        catch (JSException)
        {
            // Ignore storage bridge failures to keep UI responsive.
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("poAppStorage.remove", StorageKey);
        }
        catch (InvalidOperationException)
        {
            // Ignore prerender phase where JS interop is unavailable.
        }
        catch (JSException)
        {
            // Ignore storage bridge failures to keep UI responsive.
        }
    }
}
