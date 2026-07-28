# AGENT.MD — Architecture & Boundaries

Context map for AI agents and humans. Update this file whenever an architectural
boundary or project configuration changes.

## What this app is
A personal **Azure portfolio / ops dashboard**. A Blazor WASM front-end shows App
Service health, cost, SSL expiry, zombie-app detection, and downtime diagnosis. The
backend is a minimal-API **BFF** that reads an Azure inventory report from Azure Table
Storage (with a local JSON fallback).

There are exactly two pages: `/` (the app catalog) and `/azure` (the ops dashboard).

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
| `src/PoPunkouterSoftware` | API host + BFF. Feature slices under `Features/<Area>/*Endpoints.cs` (**Config, Diag, Portfolio**), each a `Map<Area>Endpoints` extension called from `Program.cs`. Host-level plumbing that belongs to no slice lives in `Host/` (`GlobalExceptionHandler`, `ManagementActionFilter`, `RefreshHub`). Hosts the Blazor WASM app. |
| `src/PoPunkouterSoftware.Client` | Blazor WASM (Radzen UI, mobile-first). `wwwroot` lives **only** here. Shared UI in `Components/Shared` (`ErrorRetryBanner`, `LoadingSkeleton`, `DevErrorBoundary`); page-specific blocks in `Components/Pages/Sections`; relative-time formatting in `RelativeTime.cs`. |
| `src/PoPunkouterSoftware.Shared` | Shared DTOs and the domain vocabulary (`DomainVocabulary.cs`) only — no behavior beyond those helpers, and **no PackageReferences at all**. No server/browser-only deps. Ships in the WASM bundle (`IsTrimmable`). |
| `src/PoPunkouterSoftware.Infrastructure` | Azure services (Table Storage, ARM, pinger, incident, telemetry) plus cross-slice helpers: `ReportFileCache` (report file fallback + data dir), `HistorySummaryMapper`, `AppServicePlanInventory`, `SecretMasking`. Slices must not reference each other — shared logic goes here. |

### Naming schema
- **One type per file, named for the type.** Two deliberate exceptions: a cohesive *vocabulary* of constants is named for the group (`DomainVocabulary.cs`, `TableStorageVocabulary.cs`), and a group of related DTOs is named `<Concern>Models.cs` (`CostModels.cs`, `SecurityModels.cs`).
- **Partial-class aspect files** are `<Type>.<Aspect>.cs` and the aspect names the concern, not the shape: `AzureReportService.Cost.cs`, `AzureDashboard.DerivedViews.cs`. A file called `.Charts.cs` that holds no charts is the failure mode to avoid.
- **A file name must be greppable.** If the name does not appear as a type inside the file and is not one of the two exceptions above, the name is wrong.
- **Test files mirror the source file under test** (`DomainVocabularyTests.cs` holds `ServiceHealthTests`, `SeverityLevelTests`, …), not one file per test class.
- **Records crossing a component boundary** go in `Components/Pages/Models/` — a private nested record cannot be a Blazor component parameter.

## Cross-cutting conventions
- **Routing:** slices register via `Map<Area>Endpoints(this WebApplication)`. `/api/diag/*` uses `MapGroup("/api/diag")`. `/health`, `/healthz`, `/diag` are off the `/api` group. `/health` is the deep probe and `/healthz` the static liveness ping; there is deliberately **no** `/api/health` alias.
- **Every endpoint needs a consumer.** An endpoint whose only caller is its own test is dead code with a green check mark next to it. Three whole slices (GitHub, Infra, Pinger) were deleted in 2026-07 for exactly this — all tested, none reachable from the UI. Before adding a route, know what calls it.
- **Config:** `Directory.Build.props` (net10, `Nullable`, `TreatWarningsAsErrors`, LangVersion 14) and `Directory.Packages.props` (Central Package Management). Add package **versions** only in `Directory.Packages.props`.
- **Resilience:** typed `HttpClient`s `github` and `azure-arm` use `AddStandardResilienceHandler`. `health` and `azure-probe` deliberately have **no** resilience — they must report real reachability, not retry through outages.
- **Named HttpClients are shared:** never reassign `DefaultRequestHeaders` on an instance from `CreateClient(name)`. Handler chains are pooled, so a per-call mutation leaks that header — including credentials — to every other consumer of the same named client. The `github` PAT is bound once, in `Program.cs`. Pass anything per-call on the `HttpRequestMessage`.
- **Caching:** none beyond what the framework provides. `HybridCache` and the sized `IMemoryCache` were removed with the slices that used them; add them back deliberately if a read-through cache is needed again.
- **No AI:** there is no AI integration. The `azure-openai` typed client, its telemetry handler, its Testing-environment stub and the `AzureOpenAI` config block were removed in 2026-07 because nothing in the solution ever resolved that client. Do not re-add AI wiring speculatively — add it when a feature actually calls it.
- **Screenshots & disk:** `AppScreenshotService` pins `PLAYWRIGHT_BROWSERS_PATH` to the persistent `%HOME%` share and installs only the Chromium headless shell — the worker's ephemeral disk previously filled with a full Chromium download and broke every deployment ("not enough space on the disk", 2026-07-10). Kill switch: `FeatureFlags:EnableScreenshots=false`.
- **Retention:** History/HistorySummary table rows are pruned after `Retention:HistoryDays` (default 30) by `AzureReportStore` on each save; blobs age out via the 30-day lifecycle policy in `infra/main.bicep`. Incidents are deliberately never pruned.
- **Telemetry budget:** fixed-rate trace sampling, default 10% (`ApplicationInsights:SamplingRatio`). Exceptions are never lost to it — `GlobalExceptionHandler` logs them through `ILogger` and the logs pipeline is not trace-sampled.
- **Telemetry:** Serilog → Console + File; Azure Monitor via OpenTelemetry (the sole App Insights pipeline). `cloud_RoleName` comes from the OTel resource `service.name` (set by reflection in the API only — never in WASM). High-frequency logs use `[LoggerMessage]` source-gen (see `ServicePingerService`).
- **Secrets:** Azure Key Vault (`kv-poshared`, prefix `PoPunkouterSoftware--`) loaded at startup via System-Assigned Managed Identity; skipped under the `Testing` environment. DECISION: the grant is a classic **access policy** (get/list secrets), not the RBAC "Key Vault Secrets User" role — `kv-poshared` has RBAC authorization disabled and is shared across Po* apps, so switching models is an estate-wide change, not per-app. The App Service plan (`asp-PoPunkouterSoftware-f1`) is deliberately **app-local**, not in PoShared: F1 is free, and isolation keeps this app's cold starts and quota pressure away from the shared plan.
- **Storage:** Azure Table Storage. Local dev runs **Azurite in Docker** (`docker-compose.yml`, container `popunkoutersoftware-azurite`). `UseDevelopmentStorage=true` in `appsettings.Development.json`.
- **Management actions:** there is no login, but mutating / expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`) are gated server-side by `ManagementActionFilter` (`.RequireManagementActions()`). It enforces the same `FeatureFlags:EnableManagementActions` flag the UI reads from `/api/config` (on in Development/Testing, otherwise opt-in), plus an optional `Security:ManagementApiKey` checked against the `X-Management-Key` header.

## Trim status
`EnableTrimAnalyzer` and `PublishTrimmed` are ON for `.Client`; the shared assembly is
trimmable. All client JSON now flows through
the source-generated `AppJsonContext` (`AppJsonContext.cs`), so the analyzer runs clean and
the previous `IL2026`/`IL2104` `WarningsNotAsErrors` suppression has been removed — the
client builds trim-safe under the global `TreatWarningsAsErrors`. Add new client (de)serialised
types to `AppJsonContext` with a `[JsonSerializable]` attribute.

## Tests (`/tests`) — two projects, four tiers (target ratio 100/50/25/25)
- `PoPunkouterSoftware.Tests` — the fast tier. `Unit/` (strictly no-I/O: HTTP stubs,
  in-memory caches) and `Integration/` (`WebApplicationFactory` + Testcontainers Azurite,
  including a fixture that runs Azurite INSIDE the factory with explicit teardown).
  RULE: DTO-mapping tests live in `Integration/`, never `Unit/`.
  Collections run sequentially (`AssemblyInfo.cs`) because concurrent entry-point boots race.
- `PoPunkouterSoftware.Tests.E2E` — against a live instance via `BASE_URL`
  (default `http://localhost:8000`); run on demand, not in CI.
  `ApiSmokeTests` = pure-HTTP E2E API tier (also the post-deploy smoke: point `BASE_URL`
  at production). `PortfolioUiTests` = Playwright UI tier; every UI test runs BOTH
  mobile-portrait (390×844) and desktop-landscape (1440×1000) via the shared
  `Viewports` theory data — visual parity is a hard rule.

**CI/CD:** two workflows. `deploy.yml` is deliberately build-and-deploy only — no test
steps live in the YAML. `screenshots.yml` runs nightly (06:17 UTC) to capture the
portfolio screenshots on a Linux runner, because the production Windows F1 sandbox
cannot run Chromium. All test tiers run locally before pushing to master
(fast tier: `dotnet test tests/PoPunkouterSoftware.Tests`; E2E: on demand, headed via
`HEADED=1`, installed Chrome via `BROWSER_CHANNEL=chrome`).

## Agent tooling (Claude Code)
- **User-level plugins** (`claude plugin list`): `dotnet-claude-kit` (codewithmukesh), `dotnet-skills@wshaddix-dotnet-skills` (167 skills + 16 agents; local marketplace at `~/.claude/local-marketplaces/wshaddix-dotnet-skills`), and the official `dotnet/skills` plugins `dotnet`, `dotnet-aspnetcore`, `dotnet-blazor`, `dotnet-test`, `dotnet-data`, `dotnet-diag` (marketplace `dotnet-agent-skills`).
- **Project-level skills** (`.claude/skills/`): auto-managed by the `dotnet-skills` global tool (managedcode). Refresh with `dotnet skills install --auto --prune --agent claude` after changing package references.

## Run / deploy
- **Local (Development env):** F5 in VS Code runs `f5-prep` (kill stale dotnet → start Azurite → build) then launches on `http://localhost:8000`. `SCRIPTS/setup.ps1` provisions WinGet/Docker/Azure prerequisites.
- **Azure (Production env):** `.github/workflows/deploy.yml` builds + deploys to App Service `app-popunkoutersoftware` (OIDC login, no secrets in workflow, no test steps by design).
