# AGENT.MD — Architecture & Boundaries

Context map for AI agents and humans. Update this file whenever an architectural
boundary or project configuration changes.

## What this app is
A personal **Azure portfolio / ops dashboard**. A Blazor WASM front-end shows App
Service health, cost, SSL expiry, zombie-app detection, GitHub activity, and downtime
diagnosis. The backend is a minimal-API **BFF** that reads an Azure inventory report
from Azure Table Storage (with a local JSON fallback) and proxies GitHub.

## Authentication — NONE (by design)
This site has **no authentication**. There is no login, no `/auth/*` routes, no
`AuthenticationStateProvider`, no Microsoft OAuth, and no guest/fake login. Do not
add auth wiring, session storage, or login UI unless the owner explicitly reverses
this decision. `/api/config` deliberately exposes no auth flags.

## Solution layout (Vertical Slice Architecture)
| Project | Role |
|---|---|
| `PoPunkouterSoftware` (`/src` group) | API host + BFF. Feature slices under `Features/<Area>/*Endpoints.cs`, each an `IEndpointRouteBuilder` extension. Hosts the Blazor WASM app. |
| `PoPunkouterSoftware.Client` | Blazor WASM (Radzen UI, mobile-first). `wwwroot` lives **only** here. |
| `PoPunkouterSoftware.Shared` | Shared DTOs **and FluentValidation rules only**. No server/browser-only deps. Ships in the WASM bundle (`IsTrimmable`). |
| `PoPunkouterSoftware.Infrastructure` | Azure services (Table Storage, ARM, pinger, incident, telemetry). |

## Cross-cutting conventions
- **Routing:** slices register via `Map<Area>Endpoints(this WebApplication)`. `/api/diag/*` uses `MapGroup("/api/diag")`. `/health`, `/healthz`, `/diag` are off the `/api` group.
- **Config:** `Directory.Build.props` (net10, `Nullable`, `TreatWarningsAsErrors`, LangVersion 14) and `Directory.Packages.props` (Central Package Management). Add package **versions** only in `Directory.Packages.props`.
- **Resilience:** typed `HttpClient`s `github` and `azure-openai` use `AddStandardResilienceHandler`. `health` and `azure-probe` deliberately have **no** resilience — they must report real reachability, not retry through outages.
- **Telemetry:** Serilog → Console + File; Azure Monitor via OpenTelemetry (the sole App Insights pipeline). `cloud_RoleName` comes from the OTel resource `service.name` (set by reflection in the API only — never in WASM). High-frequency logs use `[LoggerMessage]` source-gen (see `ServicePingerService`).
- **Secrets:** Azure Key Vault (`kv-poshared`, prefix `PoPunkouterSoftware--`) loaded at startup via Managed Identity; skipped under the `Testing` environment.
- **Storage:** Azure Table Storage. Local dev runs **Azurite in Docker** (`docker-compose.yml`, container `popunkoutersoftware-azurite`). `UseDevelopmentStorage=true` in `appsettings.Development.json`.
- **Management actions:** there is no login, but mutating / expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`, `/api/pinger/toggle/*`, `/api/infra/cicd-review/refresh`) are gated server-side by `ManagementActionFilter` (`.RequireManagementActions()`). It enforces the same `FeatureFlags:EnableManagementActions` flag the UI reads from `/api/config` (on in Development/Testing, otherwise opt-in), plus an optional `Security:ManagementApiKey` checked against the `X-Management-Key` header.

## Trim status
`EnableTrimAnalyzer` is ON for `.Client` and `.Shared`. All client JSON now flows through
the source-generated `AppJsonContext` (`AppJsonContext.cs`), so the analyzer runs clean and
the previous `IL2026`/`IL2104` `WarningsNotAsErrors` suppression has been removed — the
client builds trim-safe under the global `TreatWarningsAsErrors`. Add new client (de)serialised
types to `AppJsonContext` with a `[JsonSerializable]` attribute.

## Tests (`/tests`)
Four projects, by test tier (not mirrored 1:1 to feature slices):
- `UnitTests` — fast, isolated.
- `IntegrationTests` — `WebApplicationFactory` + Testcontainers (Azurite) for ephemeral infra.
- `E2EAPI` — pure HTTP API calls against the hosted app (functional emulation).
- `E2EUI` — C# Playwright UI tests.
**CI (`ci.yml`)** runs on pull requests and executes the two fast tiers (Unit + Integration).
The E2E tiers need a running host / Playwright browser and stay manual. The **deploy**
pipeline (`deploy.yml`) is build-and-deploy only and runs no tests.

## Run / deploy
- **Local (Development env):** F5 in VS Code runs `f5-prep` (kill stale dotnet → start Azurite → build) then launches on `http://localhost:8000`. `SCRIPTS/setup.ps1` provisions WinGet/Docker/Azure prerequisites.
- **Azure (Production env):** `.github/workflows/deploy.yml` builds + deploys to App Service `app-popunkoutersoftware` (OIDC login, no secrets in workflow, no tests).
