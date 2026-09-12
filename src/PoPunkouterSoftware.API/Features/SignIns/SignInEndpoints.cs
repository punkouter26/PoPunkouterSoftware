using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware.API;

/// <summary>
/// The read contract behind <c>/users</c>: who has signed in to any Po app, from the shared
/// Application Insights component. One endpoint, one consumer — the <c>SignIns</c> page.
/// </summary>
/// <remarks>
/// <para>Anonymous, like <c>/api/diag/report</c> and for the same reason: the page is a WASM
/// island with no credential, and this app deliberately has no identity provider. That is
/// precisely why the payload is masked before it leaves the server — see
/// <c>SecretMasking.MaskEmail</c>. The roster contains the addresses of real people,
/// including people who are not the owner, and an unmasked one would publish third-party PII
/// to any visitor.</para>
/// <para>Unprivileged rather than behind <c>.RequireManagementActions()</c>: it mutates
/// nothing and costs one Log Analytics query, which puts it alongside <c>/api/diag/ai</c> and
/// the snooze endpoints. A management gate would also make it unreachable from the page —
/// the WASM client cannot present the <c>X-Management-Key</c> header — leaving an endpoint
/// whose only caller is its own test, which is the exact shape CLAUDE.md forbids.</para>
/// </remarks>
internal static class SignInEndpoints
{
    internal static WebApplication MapSignInEndpoints(this WebApplication app)
    {
        app.MapGet("/api/signins", async (SignInQueryService signIns, int? days, CancellationToken ct) =>
        {
            // A caller-supplied window is clamped, never rejected: ?days=9999 means "as much
            // as you have", and answering 400 to it would be pedantry about a number the page
            // only offers three values for anyway.
            var window = days is > 0 ? SignInQueryService.ClampWindow(days.Value) : signIns.DefaultWindowDays;

            // No try/catch: the service degrades internally to an "unavailable" report rather
            // than throwing, so there is no failure here that a handler could improve on.
            return Results.Ok(await signIns.GetAsync(window, ct));
        })
        .WithName("GetSignIns").WithTags("SignIns");

        return app;
    }
}
