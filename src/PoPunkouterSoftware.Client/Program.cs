using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoPunkouterSoftware.Client;
using Radzen;

// SOLID: Single Responsibility — this file only wires WASM DI and starts the host.
// GoF:   Builder — WebAssemblyHostBuilder assembles the WASM app before Run().

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// ─── HttpClient — base URL is the server that hosts this WASM app ─────────────
// Timeout raised to 5 min: the Azure report JSON can be large, and the server may
// be briefly busy after a full subscription scan (thread-pool catch-up).
// A TransientRetryHandler wraps the browser handler to retry idempotent GETs through
// brief transient failures (see TransientRetryHandler).
builder.Services.AddScoped(sp =>
    new HttpClient(new TransientRetryHandler { InnerHandler = new HttpClientHandler() })
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
        Timeout = TimeSpan.FromMinutes(5),
    });

// ─── Radzen UI services ───────────────────────────────────────────────────────
builder.Services.AddRadzenComponents();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();

await builder.Build().RunAsync();
