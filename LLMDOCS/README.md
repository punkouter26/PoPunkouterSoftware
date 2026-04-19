# LLMDOCS — PoPunkouterSoftware

> This folder is the **single source of truth** for any LLM (or new developer) onboarding to this codebase.
> Update it whenever the public API surface or project structure changes significantly.

## Quick Facts

| Item | Value |
|------|-------|
| Solution | `PoPunkouterSoftware.sln` |
| Namespace prefix | `PoPunkouterSoftware.*` / `PoShared.*` |
| Framework | .NET 10 (pinned via `global.json`) |
| UI | Blazor **Server** (InteractiveServer render mode) + Radzen UI |
| Architecture | Onion — Domain → Application → Infrastructure → Presentation |
| Ports (local) | HTTP: 5000 · HTTPS: 5001 |
| Auth | None yet; **ANON** bypass available in Dev via floating button |
| Secrets | Azure Key Vault (`kv-poshared`) — never in `appsettings.json` |
| Storage | Azure Table Storage (`AzureReport` table) |
| Logging | Serilog → Console, File (`logs/`), App Insights |
| Telemetry | OpenTelemetry → Azure Monitor (PoShared App Insights) |

---

## Project Map

```
PoPunkouterSoftware/          ← ASP.NET Core + Blazor Server host
  Domain/Azure/               ← Onion: domain interfaces (IAzureReportRepository)
  Application/Azure/          ← Onion: application interfaces (IAzureReportService)
  Infrastructure/             ← Onion: external integrations
    Auth/AnonAuthMiddleware    ← Dev-only ANON cookie middleware
    AppKeyVaultSecretManager   ← KV secret prefix filtering
  Features/Azure/             ← Concrete implementations of domain/application interfaces
    AzureModels.cs            ← Domain records (AzureReport, WebService, etc.)
    AzureReportService.cs     ← IAzureReportService: Azure SDK orchestration
    AzureReportStore.cs       ← IAzureReportRepository: Table Storage persistence
    AppsJsonSyncer.cs         ← Sync discovered apps back to wwwroot/data/apps.json
  Features/Diag/
    DiagEndpoints.cs          ← /api/diag/* route registration
  Components/
    Layout/MainLayout.razor   ← App shell (Radzen Layout + AnonLoginButton)
    Pages/
      Index.razor             ← Home — app grid + Azure live-status cards
      AzureDashboard.razor    ← Full Azure cost/health dashboard
      Diag.razor              ← /diag — external connection status
    Shared/
      AnonLoginButton.razor   ← Dev-only ANON bypass floating button
      DevErrorBoundary.razor  ← Shows full stack trace in Dev; generic msg in Prod
  Program.cs                  ← DI wiring, middleware pipeline, minimal API endpoints
  api.http                    ← REST Client file for all primary API functions

PoShared/                     ← Shared library (server + future WASM client)
  Auth/UserInfo.cs            ← Shared user identity value object

tests/
  PoPunkouterSoftware.Tests.Unit/        ← xUnit; targets Domain + Service layers
  PoPunkouterSoftware.Tests.Integration/ ← xUnit + Testcontainers (Azurite); tests API endpoints
  PoPunkouterSoftware.Tests.E2E/         ← Playwright (TS); headed in Dev, headless in CI
```

---

## Key API Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/health` | JSON health check of all external connections |
| GET | `/api/config` | Returns `{ apiBase }` for client configuration |
| GET | `/api/diag/report` | Latest Azure report (Table Storage → file fallback) |
| POST | `/api/diag/refresh` | Trigger live Azure SDK analysis (30–90 s) |
| GET | `/api/auth/anon/login` | Set ANON cookie (Dev only) |
| GET | `/api/auth/anon/logout` | Clear ANON cookie |
| GET | `/api/auth/anon/status` | `{ isAnon: bool }` |
| GET | `/openapi/v1.json` | OpenAPI spec |
| GET | `/scalar` | Scalar API explorer UI |

---

## Dev Workflow

```bash
# 1. Start the server (F5 in VS Code kills existing dotnet first)
dotnet run --project PoPunkouterSoftware/PoPunkouterSoftware.csproj

# 2. Run unit tests
dotnet test tests/PoPunkouterSoftware.Tests.Unit/

# 3. Run integration tests (requires Docker for Azurite)
dotnet test tests/PoPunkouterSoftware.Tests.Integration/

# 4. Run E2E tests (server must be running; headed in Dev)
cd tests/PoPunkouterSoftware.Tests.E2E
npx playwright test
```

## Onion Architecture

```
┌──────────────────────────────────────────┐
│  Presentation  (Blazor pages, API routes) │
├──────────────────────────────────────────┤
│  Infrastructure  (Azure SDK, Table Storage│
│                   Auth middleware, KV)    │
├──────────────────────────────────────────┤
│  Application  (IAzureReportService)      │
├──────────────────────────────────────────┤
│  Domain  (IAzureReportRepository,        │
│           AzureReport records)           │
└──────────────────────────────────────────┘
```

Dependency flow: outer rings depend on inner rings. Inner rings never reference outer rings.

## Feature Flags

Controlled via `appsettings.json → FeatureFlags`:

| Flag | Default | Purpose |
|------|---------|---------|
| `EnableAzureRefresh` | `true` | Allow live Azure SDK refresh from the UI |
| `EnableAiIntegration` | `false` | Enable real AI calls (Dev env only when true) |

## SOLID / GoF patterns used

| Location | Pattern |
|----------|---------|
| `IAzureReportRepository` | Interface Segregation + Repository (GoF) |
| `IAzureReportService` | Dependency Inversion + Facade (GoF) |
| `AzureReportStore` | Repository, Open/Closed |
| `AzureReportService` | Single Responsibility, Facade |
| `AppKeyVaultSecretManager` | Adapter (GoF), SRP, Open/Closed |
| `DiagEndpoints.MapDiagEndpoints` | Extension Method as Decorator (GoF) |
| `AnonAuthMiddleware` | Chain of Responsibility (GoF) |
| `UserInfo` record | Value Object (GoF) |
| `DevErrorBoundary` | Template Method (GoF) |

## ⚠ Known Architecture Gap

The requirement calls for **Blazor WASM** (browser-hosted client) with the server acting as a pure API host. The current project uses **Blazor Server** (SignalR-based, server renders everything). Migration path:

1. Create `PoPunkouterSoftware.Client` project (`Microsoft.NET.Sdk.BlazorWebAssembly`)
2. Move all `.razor` pages/components to the Client project
3. Add `services.AddHttpClient()` in Client; call existing `/api/*` endpoints
4. Server calls `builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents()`
5. `wwwroot/` moves to Client project; Server `wwwroot/` is deleted
