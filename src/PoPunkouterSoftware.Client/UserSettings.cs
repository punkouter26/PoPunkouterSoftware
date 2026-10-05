namespace PoPunkouterSoftware.Client;

public class AppUserSettings
{
    public string Theme { get; set; } = "system"; // "system", "dark", "light"
    public bool SoundEnabled { get; set; } = false;
    public double SoundVolume { get; set; } = 0.5;
    public int DefaultSignInDays { get; set; } = 30; // 7, 30, 90
    public bool CompactDensity { get; set; } = false;
    public bool AutoRefreshEnabled { get; set; } = true;
    public bool DesktopZeroScroll { get; set; } = false;
    public bool ReduceAnimations { get; set; } = false;
}

public interface IUserSettingsService
{
    ValueTask<AppUserSettings> GetSettingsAsync();
    ValueTask SaveSettingsAsync(AppUserSettings settings);
}
