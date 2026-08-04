namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Cross-cutting helper for rendering secret-bearing configuration values in
/// diagnostics output without disclosing them.
/// </summary>
public static class SecretMasking
{
    public static string MaskValue(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "(not set)" :
        v.Length <= 8 ? "****" :
        v[..4] + new string('*', Math.Min(v.Length - 8, 20)) + v[^4..];
}
