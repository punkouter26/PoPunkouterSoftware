using Microsoft.JSInterop;

namespace PoPunkouterSoftware.Client;

public class LocalStorageUserSettingsService(IJSRuntime js) : IUserSettingsService
{
    private const string StorageKey = "pops:settings";
    private AppUserSettings? _cached;

    public async ValueTask<AppUserSettings> GetSettingsAsync()
    {
        if (_cached is not null) return _cached;
        try
        {
            var themeVal = await js.InvokeAsync<string?>("localStorage.getItem", "pops:theme");
            var soundVal = await js.InvokeAsync<string?>("localStorage.getItem", "pops:sound");
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);

            var settings = !string.IsNullOrWhiteSpace(json)
                ? System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.AppUserSettings) ?? new AppUserSettings()
                : new AppUserSettings();

            // Align with audio-kit and theme-kit native localStorage keys
            if (!string.IsNullOrEmpty(themeVal))
            {
                settings.Theme = themeVal;
            }
            if (soundVal == "1")
            {
                settings.SoundEnabled = true;
            }

            _cached = settings;
            return _cached;
        }
        catch
        {
            // Fall back to default
        }

        _cached = new AppUserSettings();
        return _cached;
    }

    public async ValueTask SaveSettingsAsync(AppUserSettings settings)
    {
        _cached = settings;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonContext.Default.AppUserSettings);
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);

            // Synchronize theme with theme-kit
            if (settings.Theme == "system")
            {
                await js.InvokeVoidAsync("themeKit.set", (string?)null);
            }
            else
            {
                await js.InvokeVoidAsync("themeKit.set", settings.Theme);
            }

            // Synchronize audio kit
            await js.InvokeVoidAsync("audioKit.setEnabled", settings.SoundEnabled);

            // Synchronize compact density attribute on html element
            await js.InvokeVoidAsync("eval", $"document.documentElement.setAttribute('data-compact', '{settings.CompactDensity.ToString().ToLowerInvariant()}')");
        }
        catch
        {
            // Local storage access error / private mode
        }
    }
}
