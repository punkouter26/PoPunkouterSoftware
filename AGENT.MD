# AGENT.MD — Architecture & Boundaries

Context map for AI agents and humans. Update this file whenever an architectural
boundary or project configuration changes.

## What this app is
A personal **Azure portfolio / ops dashboard**. A Blazor WASM front-end shows App
Service health, cost, SSL expiry, zombie-app detection, GitHub activity, and downtime
diagnosis. The backend is a minimal-API **BFF** that reads an Azure inventory report
from Azure Table Storage (with a local JSON fallback) and proxies GitHub.

The home-page catalog in `apps.json` is authoritative: live Azure inventory decorates
catalog entries with health and deployment URLs, but probe failures never remove apps
from the portfolio. `/api/diag/summary` is the compact first-paint contract for `/azure`;
the full report and history load only when advanced diagnostics is opened.

## Authentication — NONE (by design)
This site has **no authentication**. There is no login, no `/auth/*` routes, no
`AuthenticationStateProvider`, no Microsoft OAuth, and no guest/fake login. Do not
add auth wiring, session storage, or login UI unless the owner explicitly reverses
this decision. `/api/config` deliberately exposes no auth flags.

## Solution layout (Vertical Slice Architecture)
Source projects live under `/src`; tests under `/tests`.
| Project | Role |
|---|---|
| `src/PoPunkouterSoftware` | API host + BFF. Feature slices under `Features/<Area>/*Endpoints.cs` (Config, Diag, GitHub, Infra, Pinger, Portfolio), each a `Map<Area>Endpoints` extension called from `Program.cs`. Hosts the Blazor WASM app. |
| `src/PoPunkouterSoftware.Client` | Blazor WASM (Radzen UI, mobile-first). `wwwroot` lives **only** here. Shared UI in `Components/Shared` (`ErrorRetryBanner`, `LoadingSkeleton`, `DevErrorBoundary`); relative-time formatting in `RelativeTime.cs`. |
| `src/PoPunkouterSoftware.Shared` | Shared DTOs **and FluentValidation rules only** — no behavior beyond the domain vocabulary helpers (`DomainVocabulary.cs`). No server/browser-only deps. Ships in the WASM bundle (`IsTrimmable`). |
| `src/PoPunkouterSoftware.Infrastructure` | Azure services (Table Storage, ARM, pinger, incident, telemetry) plus cross-slice helpers: `ReportFileCache` (report file fallback + data dir), `HistorySummaryMapper`, `AppServicePlanInventory`, `SecretMasking`. Slices must not reference each other — shared logic goes here. |

## Cross-cutting conventions
- **Routing:** slices register via `Map<Area>Endpoints(this WebApplication)`. `/api/diag/*` uses `MapGroup("/api/diag")`. `/health`, `/healthz`, `/diag` are off the `/api` group.
- **Config:** `Directory.Build.props` (net10, `Nullable`, `TreatWarningsAsErrors`, LangVersion 14) and `Directory.Packages.props` (Central Package Management). Add package **versions** only in `Directory.Packages.props`.
- **Resilience:** typed `HttpClient`s `github`, `azure-arm`, and `azure-openai` use `AddStandardResilienceHandler`. `health` and `azure-probe` deliberately have **no** resilience — they must report real reachability, not retry through outages.
- **Caching:** `HybridCache` backs the read-through GitHub-activity and CI/CD-review lookups (stampede protection; outcome-dependent TTL for rate-limited GitHub results). `IMemoryCache` remains only for the pinger snapshot.
- **Telemetry:** Serilog → Console + File; Azure Monitor via OpenTelemetry (the sole App Insights pipeline). `cloud_RoleName` comes from the OTel resource `service.name` (set by reflection in the API only — never in WASM). High-frequency logs use `[LoggerMessage]` source-gen (see `ServicePingerService`).
- **Secrets:** Azure Key Vault (`kv-poshared`, prefix `PoPunkouterSoftware--`) loaded at startup via Managed Identity; skipped under the `Testing` environment.
- **Storage:** Azure Table Storage. Local dev runs **Azurite in Docker** (`docker-compose.yml`, container `popunkoutersoftware-azurite`). `UseDevelopmentStorage=true` in `appsettings.Development.json`.
- **Management actions:** there is no login, but mutating / expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`, `/api/pinger/toggle/*`, `/api/infra/cicd-review/refresh`) are gated server-side by `ManagementActionFilter` (`.RequireManagementActions()`). It enforces the same `FeatureFlags:EnableManagementActions` flag the UI reads from `/api/config` (on in Development/Testing, otherwise opt-in), plus an optional `Security:ManagementApiKey` checked against the `X-Management-Key` header.

## Trim status
`EnableTrimAnalyzer` and `PublishTrimmed` are ON for `.Client`; the shared assembly is
trimmable. All client JSON now flows through
the source-generated `AppJsonContext` (`AppJsonContext.cs`), so the analyzer runs clean and
the previous `IL2026`/`IL2104` `WarningsNotAsErrors` suppression has been removed — the
client builds trim-safe under the global `TreatWarningsAsErrors`. Add new client (de)serialised
types to `AppJsonContext` with a `[JsonSerializable]` attribute.

## Tests (`/tests`) — two-tier layout
- `PoPunkouterSoftware.Tests` — the fast tier. `Unit/` (isolated, HTTP stubs) and
  `Integration/` (`WebApplicationFactory` + Testcontainers Azurite). Collections run
  sequentially (`AssemblyInfo.cs`) because concurrent entry-point boots race.
  Covers the `ManagementActionFilter` gate, pinger, config, diag, and portfolio contracts.
- `PoPunkouterSoftware.Tests.E2E` — C# Playwright UI tests against a live instance
  (`BASE_URL`, default `http://localhost:8000`); run on demand, not in CI.

**CI/CD:** `deploy.yml` is the only workflow and is deliberately build-and-deploy only —
no test steps live in the YAML. All test tiers run locally before pushing to master
(fast tier: `dotnet test tests/PoPunkouterSoftware.Tests`; E2E: on demand, headed via
`HEADED=1`, installed Chrome via `BROWSER_CHANNEL=chrome`).

## Agent tooling (Claude Code)
- **User-level plugins** (`claude plugin list`): `dotnet-claude-kit` (codewithmukesh), `dotnet-skills@wshaddix-dotnet-skills` (167 skills + 16 agents; local marketplace at `~/.claude/local-marketplaces/wshaddix-dotnet-skills`), and the official `dotnet/skills` plugins `dotnet`, `dotnet-aspnetcore`, `dotnet-blazor`, `dotnet-test`, `dotnet-data`, `dotnet-diag` (marketplace `dotnet-agent-skills`).
- **Project-level skills** (`.claude/skills/`): auto-managed by the `dotnet-skills` global tool (managedcode). Refresh with `dotnet skills install --auto --prune --agent claude` after changing package references.

## Run / deploy
- **Local (Development env):** F5 in VS Code runs `f5-prep` (kill stale dotnet → start Azurite → build) then launches on `http://localhost:8000`. `SCRIPTS/setup.ps1` provisions WinGet/Docker/Azure prerequisites.
- **Azure (Production env):** `.github/workflows/deploy.yml` builds + deploys to App Service `app-popunkoutersoftware` (OIDC login, no secrets in workflow, no test steps by design).
