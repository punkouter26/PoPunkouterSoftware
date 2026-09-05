using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Cross-cutting helper for rendering secret-bearing configuration values in
/// diagnostics output without disclosing them.
/// </summary>
public static partial class SecretMasking
{
    public static string MaskValue(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "(not set)" :
        v.Length <= 8 ? "****" :
        v[..4] + new string('*', Math.Min(v.Length - 8, 20)) + v[^4..];

    /// <summary>
    /// Replaces the subscription GUID inside every ARM resource id with a fixed placeholder,
    /// leaving the rest of the id (resource group, provider, type, name) intact.
    /// </summary>
    /// <remarks>
    /// <para><c>GET /api/diag/report</c> is anonymous — it has to be, because the Advanced
    /// diagnostics panel on /azure is a WASM client with no credential — and it returns the
    /// whole inventory: every resource id, and with it the subscription id repeated a few
    /// hundred times. CLAUDE.md records that exact payload leaking once already, from
    /// <c>wwwroot/data/azure-full-report.json</c> under a served directory. Moving it behind
    /// a route did not change who can read it.</para>
    /// <para>The subscription id is the one field in that payload that is a credential-shaped
    /// identifier rather than a description of the estate: it is what an attacker needs to
    /// address the subscription in an ARM call or a support-channel pretext. Resource names
    /// and groups are already public — they are in the hostnames the portfolio links to.
    /// So this masks the id and keeps the rest, which is the difference between a report the
    /// UI can render and one it cannot.</para>
    /// <para>Applied to the serialized JSON rather than the object graph on purpose: the id
    /// appears inside free-text strings across a dozen nested record types, and a per-record
    /// <c>with</c>-expression rewrite would silently miss whichever one is added next.</para>
    /// </remarks>
    public static string MaskSubscriptionIds(string json) =>
        string.IsNullOrEmpty(json) ? json : SubscriptionIdPattern().Replace(json, "/subscriptions/****");

    // Case-insensitive: ARM ids are returned with inconsistent casing on the segment names.
    [GeneratedRegex(
        @"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubscriptionIdPattern();
}
